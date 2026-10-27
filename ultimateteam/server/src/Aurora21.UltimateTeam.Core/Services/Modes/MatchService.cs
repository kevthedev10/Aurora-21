using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Services.Store;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>Match creation handoffs and the CPU squads single-player modes play against.</summary>
public sealed class MatchService
{
    private const string RivalsMatchSubType = "RIVALS_MATCH";

    /// <summary>The error code of a CreateMatch refused for concept players.</summary>
    private const long ConceptSquadErrorCode = 471;

    /// <summary>The reason of a CreateMatch refused for Pro, custom or grant-only cards (PLAN 7.1).</summary>
    private const string RestrictedSquadReason = "Squad contains Pro, custom or grant-only players";

    /// <summary>The reason of a single-player CreateMatch refused while the operator has paused new matches.</summary>
    private const string SinglePlayerPausedReason = "Matches are paused by the server";

    /// <summary>Keys naming a mode other than Draft in a CreateMatch body.</summary>
    private static readonly string[] OtherModeMatchKeys = { "sqbtEventId", "rivalsEventId", "championId", "matchSubType" };

    /// <summary>CreateMatch context echoed back when the response does not set it itself.</summary>
    private static readonly string[] EchoedCreateKeys = { "matchSubType", "rivalsEventId", "squadId", "type", "customData1" };

    /// <summary>Match-end context echoed back as submitted.</summary>
    private static readonly string[] EchoedEndKeys = { "gid", "matchId", "matchReportId", "isHomeTeam", "myMatchStats", "opponentMatchStats" };

    private readonly FutServices _fut;
    private readonly ILogger _log;
    private long _lastMatchId;

    public MatchService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Match");
        _lastMatchId = fut.Game.NowSeconds & MatchIdSeedMask;
    }

    /// <summary>Capture the allowlisted event fields before any mode's settlement or progress gate.</summary>
    internal async ValueTask LogScoringEvidenceAsync(JsonObject body, CancellationToken cancellationToken)
    {
        if (!_log.IsEnabled(LogLevel.Information)) return;
        var ids = MatchScoringEvidence.ItemIds(body);
        var owned = await _fut.Game.ReadAsync(async work =>
        {
            var metadata = new Dictionary<long, JsonObject>();
            foreach (var item in await work.GetItemsAsync(ids))
                if (item.Text("itemType") == "player" && _fut.Game.Players.Find(item.Int("resourceId")) is { } meta)
                    metadata[item.Int("id")] = new JsonObject
                    {
                        ["resourceId"] = item.Int("resourceId"), ["assetId"] = meta.Int("assetId"),
                        ["leagueId"] = meta.Int("leagueId"), ["nation"] = meta.Int("nation"),
                    };
            return metadata;
        }, cancellationToken);
        _log.LogInformation("SCORING EVIDENCE {Evidence}", MatchScoringEvidence.Summarize(body, owned).ToJsonString());
    }

    internal enum MatchOutcome
    {
        Win,
        Draw,
        Loss,

        /// <summary>A no-contest: not a result at all.</summary>
        None,
    }

    /// <summary>Match ids start from the current time, masked to stay a positive 30-bit number.</summary>
    private const long MatchIdSeedMask = 0x3FFFFFFF;

    /// <summary>The last match id handed to this club (FIFA composes match/{id}/ready from it).</summary>
    public long? CurrentMatchId { get; private set; }

    private long AllocateMatchId()
    {
        var id = Interlocked.Increment(ref _lastMatchId);
        CurrentMatchId = id;
        return id;
    }

    /// <summary>
    /// POST/PUT /match. A Draft CreateMatch plays the Draft run (once ready). Every other match plays
    /// the club squad FIFA names (making it the active squad), against the Squad Battles opponent
    /// named by <c>sqbtOpponentSquadId</c>; a squad holding concept players is refused. The response
    /// carries the match id FIFA builds its readiness URL from and echoes the validation handshake.
    /// </summary>
    public async ValueTask<FutResult> CreateAsync(string method, JsonNode? payload, DraftSlot slot,
        CancellationToken cancellationToken = default)
    {
        var body = payload as JsonObject ?? new JsonObject();
        var prepared = await _fut.Game.RunAsync("match-create", async uow =>
        {
            var draftRequest = await IsDraftMatchRequestAsync(uow, method, body);
            // Paused matches start nothing: no Draft arming, match clock or Squad Battles opponent record.
            if (method == "POST" && !_fut.Game.MatchGate.Open && IsSinglePlayerCreate(body, draftRequest, slot))
            {
                return (Paused: true, Run: (JsonObject?)null, ConceptPlayers: false, ExpiredLoans: false, Restricted: new List<long>(), RequestedSquad: 0L, ActiveChanged: false);
            }
            var run = draftRequest ? await _fut.Draft.CurrentRunAsync(uow, slot) : null;
            if (run is not null)
            {
                // This create names the run's next match: its first match end settles it, a replay of that end does not.
                await _fut.Draft.ArmMatchAsync(uow, slot, run);
            }
            var conceptPlayers = !draftRequest && method == "POST" && await SquadHasConceptPlayersAsync(uow, body.Get("squadId"));
            var expiredLoans = !draftRequest && method == "POST" && await SquadHasExpiredLoansAsync(uow, body);
            // PLAN 7.1: Rivals, the Weekend League and Squad Battles never take a Pro, custom or grant-only card (friendlies
            // may); a Draft squad is drafted from offers that never hold one.
            var restricted = !draftRequest && !conceptPlayers && method == "POST" && IsCompetitiveCreate(body)
                ? await RestrictedSquadCardsAsync(uow, body.Get("squadId"))
                : [];
            if (conceptPlayers || expiredLoans || restricted.Count > 0)
            {
                return (Paused: false, Run: run, ConceptPlayers: conceptPlayers, ExpiredLoans: expiredLoans, Restricted: restricted, RequestedSquad: 0L, ActiveChanged: false);
            }
            // The reward gate's match clock: a POST starts a match, the first PUT after it is the kickoff.
            await uow.NoteMatchCreateAsync(post: method == "POST");
            if (method == "POST")
            {
                // A Squad Battles CreateMatch names its opponent and difficulty, a Weekend League one its championId; the match
                // end scores them.
                await _fut.SquadBattles.RecordCreateAsync(uow, body);
                await _fut.WeekendLeague.RecordCreateAsync(uow, body);
            }
            var requestedSquad = body.Int("squadId");
            var activeChanged = false;
            if (!draftRequest && method == "POST" && requestedSquad != 0 && requestedSquad != await uow.ActiveSquadIdAsync())
            {
                activeChanged = await uow.SetActiveSquadAsync(requestedSquad);
            }
            return (Paused: false, Run: run, ConceptPlayers: false, ExpiredLoans: false, Restricted: restricted, RequestedSquad: requestedSquad, ActiveChanged: activeChanged);
        }, cancellationToken);
        if (prepared.Paused)
        {
            // The concept refusal's shape and code, the match-create refusal FIFA is already answered with.
            _log.LogWarning("MATCH REFUSED new matches are paused body={Keys}", string.Join(",", body.Select(entry => entry.Key)));
            return new FutResult(400, new JsonObject { ["reason"] = SinglePlayerPausedReason, ["code"] = ConceptSquadErrorCode });
        }
        if (prepared.ExpiredLoans)
        {
            _log.LogWarning("MATCH REFUSED expired loan in starting eleven or substitutes");
            return new FutResult(400, new JsonObject { ["reason"] = "Squad contains an expired loan", ["code"] = ConceptSquadErrorCode });
        }
        if (prepared.ConceptPlayers)
        {
            _log.LogWarning("MATCH REFUSED concept players in squad");
            return new FutResult(400, TemplateLibrary.Core.Object("match/concept-refused", new TemplateArgs { ["code"] = ConceptSquadErrorCode }));
        }
        if (prepared.Restricted.Count > 0)
        {
            // The concept refusal's shape and code, the match-create refusal FIFA is already answered with.
            _log.LogWarning("MATCH REFUSED restricted cards in squad resources={Resources} body={Keys}",
                string.Join(",", prepared.Restricted), string.Join(",", body.Select(entry => entry.Key)));
            return new FutResult(400, new JsonObject { ["reason"] = RestrictedSquadReason, ["code"] = ConceptSquadErrorCode });
        }
        if (prepared.Run is not null)
        {
            if (prepared.Run.Text("state") != "READY_FOR_MATCH")
            {
                return FutResult.Error(409, "DRAFT_NOT_READY_FOR_MATCH");
            }
            if (slot != DraftSlot.Online)
            {
                return FutResult.Ok(await DraftMatchResponseAsync(prepared.Run, cancellationToken));
            }
            var online = await OnlineDraftMatchResponseAsync(prepared.Run, cancellationToken);
            EchoCreate(online, body);
            return FutResult.Ok(online);
        }
        if (prepared.ActiveChanged)
        {
            _log.LogInformation("Active squad set to {Squad} for match", prepared.RequestedSquad);
        }
        long? opponentSlot = body.Get("sqbtOpponentSquadId") is { } node && JsonValues.TryInt(node, out var parsed) ? parsed : null;
        var response = await NativeMatchResponseAsync(opponentSlot, IsOnlineClubCreate(body), cancellationToken);
        // FIFA plays a Weekend League (FUT Champions, game type 79) match at championsWeekendLeagueStadiumId, read from the
        // create before the pairing as well (CardsDLL 0x1802b64fa, applied by 0x180205b10 to the STADIUM setting): 0 put
        // every match in a stock venue, Emirates (game reports STID 156), labelled *StadiumName_0 (live 10-04). Both POST
        // and kickoff PUT of a Weekend League create name the FUT Champions Stadium, as in retail.
        if (await _fut.Game.ReadAsync(_fut.WeekendLeague.MatchPendingAsync, cancellationToken))
        {
            response["championsWeekendLeagueStadiumId"] = _fut.Game.Rules.Seasons.WeekendLeague.VenueId;
        }
        EchoCreate(response, body);
        _log.LogInformation("FUT match create method={Method} matchId={MatchId} matchSubType={SubType}", method, response.Get("matchId"), response.Get("matchSubType"));
        return FutResult.Ok(response);
    }

    /// <summary>
    /// Whether a CreateMatch belongs to Draft: Draft POSTs its squad id (or squad 0 without any other
    /// mode's event), and its following PUT carries no squad, so it follows the last POST.
    /// </summary>
    private async ValueTask<bool> IsDraftMatchRequestAsync(IUnitOfWork uow, string method, JsonObject body)
    {
        if (method != "POST" && !body.ContainsKey("squadId"))
        {
            return await uow.LastMatchCreateIsDraftAsync();
        }
        var squadId = body.Int("squadId");
        var isDraft = squadId == _fut.Draft.SquadId || (squadId == 0 && !OtherModeMatchKeys.Any(body.ContainsKey));
        await uow.SetLastMatchCreateIsDraftAsync(isDraft);
        return isDraft;
    }

    /// <summary>
    /// Whether a CreateMatch starts a single-player match the match pause refuses: Squad Battles (sqbtEventId) or a
    /// single-player Draft match. Offline friendlies carry no known marker and are not paused.
    /// </summary>
    private static bool IsSinglePlayerCreate(JsonObject body, bool draftRequest, DraftSlot slot) =>
        body.ContainsKey("sqbtEventId") || (draftRequest && slot != DraftSlot.Online);

    /// <summary>
    /// Whether a CreateMatch enters a competitive mode, whose squads may not hold restricted cards: Division Rivals (its
    /// subtype or event), the Weekend League (championId) or Squad Battles (sqbtEventId).
    /// </summary>
    private static bool IsCompetitiveCreate(JsonObject body) =>
        IsOnlineClubCreate(body) || body.ContainsKey("rivalsEventId") || body.ContainsKey("sqbtEventId");

    /// <summary>
    /// A card no competitive squad may hold (PLAN 7.1) while seasons.json rewardEligibility.restrictGrantOnlyCards is on: every
    /// Pro card (the developers' exclusive and the Aqua Pro cards, the custom cards of custom-players.json) and any other
    /// grant-only card. Off, nothing is restricted.
    /// </summary>
    internal bool IsRestrictedCard(long resourceId) =>
        _fut.Game.Rules.Seasons.RewardEligibility.RestrictGrantOnlyCards && _fut.Game.Cards.IsGrantOnly(resourceId);

    /// <summary>Every restricted card's resource id: the Pro cards and rules/cards.json grantOnlyResourceIds.</summary>
    internal long[] RestrictedResourceIds() => _fut.Game.Cards.ProCardIds.Concat(_fut.Game.Rules.Cards.GrantOnlyResourceIds).Distinct().ToArray();

    /// <summary>The restricted cards among these club items (a squad's or a match end's), by resource id.</summary>
    internal async ValueTask<List<long>> RestrictedCardsAsync(IUnitOfWork uow, IEnumerable<long> itemIds)
    {
        var ids = itemIds.Where(id => id > 0 && !_fut.Game.Cards.IsConceptItemId(id)).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }
        return (await uow.GetItemsAsync(ids)).Where(ClubInventoryService.IsPlayer).Select(ClubInventoryService.ResourceOrAsset)
            .Where(IsRestrictedCard).Distinct().ToList();
    }

    /// <summary>The restricted cards in the named squad (or the active one), starters, substitutes and reserves alike.</summary>
    private async ValueTask<List<long>> RestrictedSquadCardsAsync(IUnitOfWork uow, JsonNode? squadIdNode)
    {
        var squadId = squadIdNode is not null && JsonValues.TryInt(squadIdNode, out var parsed) ? parsed : await uow.ActiveSquadIdAsync();
        var squad = await uow.GetSquadAsync(squadId);
        return await RestrictedCardsAsync(uow, (squad?.Arr("players") ?? new JsonArray()).OfType<JsonObject>()
            .Select(slot => JsonValues.Int((slot.Obj("itemData") ?? slot).GetOr("id", slot.Get("itemId")))));
    }

    /// <summary>The club items a match end lists (the matchday squad).</summary>
    internal static IEnumerable<long> EndItemIds(JsonObject body) =>
        (body.Arr("items") ?? new JsonArray()).OfType<JsonObject>().Select(entry => entry.Int("id"));

    /// <summary>Starting eleven and the seven substitutes; squad slots from here on are reserves, who never take part.</summary>
    internal const int MatchdaySlots = 18;

    /// <summary>
    /// Whether a starter or substitute is an expired loan. A reserve does not count: an expired loan Mbappé among the
    /// reserves refused every match its club created (live 10-05).
    /// </summary>
    private async ValueTask<bool> SquadHasExpiredLoansAsync(IUnitOfWork uow, JsonObject body)
    {
        var id = body.ContainsKey("squadId") ? body.Int("squadId") : await uow.ActiveSquadIdAsync();
        var squad = await uow.GetSquadAsync(id);
        var ids = (squad?.Arr("players") ?? new JsonArray()).OfType<JsonObject>()
            .Where((slot, ordinal) => JsonValues.TryInt(slot.GetOr("index", ordinal), out var index) && index < MatchdaySlots)
            .Select(slot => JsonValues.Int((slot.Obj("itemData") ?? slot).GetOr("id", slot.Get("itemId"))))
            .Where(id => id > 0).Distinct().ToArray();
        return (await uow.GetItemsAsync(ids)).Any(ItemFactory.IsExpiredLoan);
    }

    private static IEnumerable<long> ParticipatingItemIds(JsonObject body)
    {
        var entries = (body.Arr("items") ?? new JsonArray()).OfType<JsonObject>().ToArray();
        var fitness = entries.Take(11).Where(e => e.Get("fitness") is not null)
            .Select(e => e["fitness"]!.ToJsonString()).ToHashSet();
        return entries.Where((entry, index) => index < 11
                || (entry.Get("fitness") is { } value && fitness.Contains(value.ToJsonString()))
                || new[] { "goals", "assists", "yellowCards", "redCards" }.Any(key => entry.Int(key) > 0))
            .Select(entry => entry.Int("id")).Where(id => id > 0).Distinct();
    }

    private static async ValueTask ConsumeLoanMatchesAsync(IUnitOfWork uow, JsonObject body)
    {
        foreach (var item in await uow.GetItemsAsync(ParticipatingItemIds(body).ToArray()))
        {
            if (!ClubInventoryService.IsPlayer(item) || item.Int("loans") <= 0 || ItemFactory.IsExpiredLoan(item)) continue;
            var remaining = item.Int("loans") - 1;
            item["loans"] = remaining;
            if (remaining == 0) item["expiredLoan"] = true;
            await uow.SaveItemAsync(item.Int("id"), item);
        }
    }

    /// <summary>
    /// Marks an early leave by a serial leaver (rewardEligibility.opponentLeft.serialLeaver; owner, 2026-10-08): a player
    /// who already left <c>earlyLeaves</c> matches before the win minute within the window denies no more results, so this
    /// leave counts for the stayer as if past the win minute. Read before the mode settles its result.
    /// </summary>
    internal async ValueTask<MatchEndFacts> NoteSerialLeaverAsync(IUnitOfWork uow, MatchEndFacts facts, string? leaver)
    {
        var rules = _fut.Game.Rules.Seasons.RewardEligibility.OpponentLeft;
        if (leaver is not { Length: > 0 } || rules.SerialLeaver.EarlyLeaves <= 0 || !facts.EarlyLeave(rules))
        {
            return facts;
        }
        var since = uow.NowSeconds - (long)(rules.SerialLeaver.WindowHours * 3600);
        var earlier = await uow.EarlyLeavesSinceAsync(leaver, since);
        return earlier >= rules.SerialLeaver.EarlyLeaves ? facts with { SerialLeaver = true } : facts;
    }

    /// <summary>
    /// The queue penalty for an early leave just logged (rewardEligibility.opponentLeft.queueCooldown; owner, 2026-10-09):
    /// with earlyLeaves early leaves inside the window since the leaver's last penalty, this leave is an offence. The n-th
    /// unlifted offence within offenceMemoryDays bars online searches for ladderSeconds[n-1]; the one after the ladder is a
    /// ban until an operator lifts it.
    /// </summary>
    private async ValueTask PenaliseLeaverAsync(IUnitOfWork uow, string leaver, string key, string mode, long minute, QueueCooldownRules rules)
    {
        if (rules.EarlyLeaves <= 0)
        {
            return;
        }
        var now = uow.NowSeconds;
        var since = Math.Max(now - rules.WindowSeconds, await uow.LastPenaltyAsync(leaver));
        var leaves = await uow.EarlyLeavesSinceAsync(leaver, since);
        if (leaves < rules.EarlyLeaves)
        {
            return;
        }
        var offence = (int)await uow.OffencesSinceAsync(leaver, now - rules.OffenceMemoryDays * 86400) + 1;
        var until = rules.PenaltySeconds(offence) is { } seconds ? now + seconds : (long?)null;
        if (await uow.AddPenaltyAsync(leaver, offence, key, until) != 1)
        {
            return;
        }
        _fut.Game.LeaverCooldown.Set(leaver, new LeaverCooldown.Penalty(offence, until));
        _log.LogWarning("LEAVER PENALTY leaver={Leaver} offence={Offence} match={Match} mode={Mode} minute={Minute} until={Until}: {Leaves} early leaves in {Window}s, {Penalty}",
            leaver, offence, key, mode, minute, until, leaves, rules.WindowSeconds, until is null ? "banned from online until lifted" : "no online search until then");
    }

    /// <summary>
    /// Logs the leave of an online match's opponent (match_leaves, the serial-leaver count and the panel's Leavers page):
    /// its minute, the score and what the stayer got. A leave the relay heard the leaver play through is not logged: its
    /// client was still in the match, and its own end says what happened.
    /// </summary>
    internal async ValueTask RecordLeaveAsync(IUnitOfWork uow, MatchEndFacts facts, MatchClock? clock, string mode, string? matchKey,
        long mine, long theirs, MatchOutcome outcome)
    {
        if (!facts.OpponentLeft || facts.OpponentHeardToEnd || clock?.OpponentPlayer is not { Length: > 0 } leaver)
        {
            return;
        }
        var rules = _fut.Game.Rules.Seasons.RewardEligibility.OpponentLeft;
        var early = facts.EarlyLeave(rules);
        var key = matchKey?.Split('@')[0] ?? $"unidentified@{uow.NowSeconds}";
        var logged = await uow.AddMatchLeaveAsync(new MatchLeaveRow(leaver, clock.OpponentPersona, _fut.Game.Player.Id, mode, key, facts.Minute,
            mine, theirs, early, facts.SerialLeaver, outcome.ToString()));
        if (early && logged == 1)
        {
            await PenaliseLeaverAsync(uow, leaver, key, mode, facts.Minute, rules.QueueCooldown);
        }
        if (facts.SerialLeaver)
        {
            _log.LogWarning("SERIAL LEAVER leaver={Leaver} stayer={Stayer} match={Match} mode={Mode} minute={Minute} score={Mine}-{Theirs} outcome={Outcome}: {Count}+ early leaves in {Hours}h, the leave counts",
                leaver, _fut.Game.Player.Id, key, mode, facts.Minute, mine, theirs, outcome, rules.SerialLeaver.EarlyLeaves, rules.SerialLeaver.WindowHours);
        }
    }

    /// <summary>
    /// Whether a rewarded game against <paramref name="opponent"/> in <paramref name="mode"/> would pass its pair cap: the
    /// cap's rewarded games in its rolling window, or in <paramref name="weekendLeague"/>, are all used. Never without an
    /// opponent or a cap.
    /// </summary>
    internal async ValueTask<bool> PairCappedAsync(IUnitOfWork uow, string mode, long? opponent, long? weekendLeague)
    {
        if (_fut.Game.Rules.Seasons.RewardEligibility.PairCapFor(mode) is not { } cap || opponent is not { } persona
            || (cap.PerWeekendLeague && weekendLeague is null))
        {
            return false;
        }
        var games = await uow.RewardedPairGamesAsync(persona, cap.Modes, cap.PerWeekendLeague ? null : uow.NowSeconds - cap.WindowSeconds,
            cap.PerWeekendLeague ? weekendLeague : null);
        return games >= cap.Games;
    }

    /// <summary>
    /// Grants the mode's optional completion pack after coins are paid, and records paid or flagged matches
    /// for reward history and pair caps. Returns the granted catalogue pack id.
    /// </summary>
    internal async ValueTask<long?> SettleRewardAsync(IUnitOfWork uow, RewardDecision decision, long? opponent, long? weekendLeague, long coins)
    {
        var pack = decision.GrantsPack ? await _fut.CompletionPacks.GrantAsync(uow, decision.Mode!) : null;
        if (decision.PaysResult && decision.Mode is { } paid) await _fut.CompletionPacks.GrantPointsAsync(uow, paid);
        var flagged = decision.Verdict is RewardVerdict.PairCapped or RewardVerdict.RestrictedSquad;
        if (decision.Mode is { } mode && (coins > 0 || pack is not null || flagged))
        {
            await uow.AddMatchRewardAsync(new MatchRewardRow(mode, decision.Verdict.ToString(), opponent, mode == RewardModes.WeekendLeague ? weekendLeague : null,
                coins, pack is null ? null : "completion", false));
        }
        if (flagged)
        {
            _log.LogWarning("MATCH REWARD FLAGGED club={Club} verdict={Verdict} mode={Mode} opponent={Opponent}: paid nothing",
                uow.ClubId, decision.Verdict, decision.Mode, opponent);
        }
        return pack;
    }

    /// <summary>Whether the named squad (or the active one) has a concept card in a player slot.</summary>
    private async ValueTask<bool> SquadHasConceptPlayersAsync(IUnitOfWork uow, JsonNode? squadIdNode)
    {
        var squadId = squadIdNode is not null && JsonValues.TryInt(squadIdNode, out var parsed) ? parsed : await uow.ActiveSquadIdAsync();
        var squad = await uow.GetSquadAsync(squadId);
        return (squad?.Arr("players") ?? new JsonArray()).OfType<JsonObject>().Any(slot =>
        {
            var data = slot.Obj("itemData") ?? slot;
            return _fut.Game.Cards.IsConceptItemId(JsonValues.Int(data.GetOr("id", slot.Get("itemId")))) || data.Bool("dream");
        });
    }

    /// <summary>
    /// Settles a non-Draft match from what FIFA submits: a DNF is a loss, otherwise the goals decide. The reward gate
    /// (<see cref="RewardEligibility"/>, PLAN 4.3) decides what it pays: coins, objective progress and Season XP, and the
    /// Match Pack. Asks Blaze to refresh the user.
    /// </summary>
    public async ValueTask<JsonObject> EndMatchAsync(JsonNode? payload, CancellationToken cancellationToken = default)
    {
        var game = _fut.Game;
        var body = payload as JsonObject ?? new JsonObject();
        var endReason = body.Str("endReason").ToUpperInvariant();
        if (endReason == "DNF" && DraftService.NoGame(body.Get("gid")) && DraftService.NoGame(body.Get("matchId")))
        {
            return await SearchEndedAsync(body, cancellationToken);
        }
        var (mine, theirs) = ObjectivesService.Goals(body);
        // A no-contest was called off before it counted: nothing is paid, rated or recorded. A quit is a loss: FIFA sends
        // the quitter's end with a 0-0 score, which made every quit a draw (Python server 5b8b918).
        var scored = endReason == "NO_CONTEST" ? MatchOutcome.None
            : MatchEndFacts.Abandoned(endReason) ? MatchOutcome.Loss
            : mine > theirs ? MatchOutcome.Win : mine < theirs ? MatchOutcome.Loss : MatchOutcome.Draw;
        var seasons = game.Rules.Seasons;
        string? leaver = null;
        (string Opponent, string Match, long? RealSeconds)? desynced = null;
        var quietFirstBy = OpponentQuietFirstBy(body);
        var (response, settled) = await game.RunAsync("match-end", async uow =>
        {
            leaver = null;
            desynced = null;
            await uow.LockClubAsync();
            var matchKey = SettlementKey(body);
            if (matchKey is not null && !await uow.TryBeginMatchSettlementAsync(matchKey))
            {
                // FIFA sends the last online game's matchId again with the end of the next match (a Squad Battles game after
                // a Rivals one, live 10-05), so a settled id alone is no replay. Every settlement takes the match clock: a
                // clock means a CreateMatch since, and this end is that new match, keyed by when it was created.
                var replayKey = matchKey;
                if (await uow.MatchClockAsync() is { } created)
                {
                    matchKey = $"{matchKey}@{created.CreatedAt.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    replayKey = matchKey;
                }
                if (replayKey != matchKey || !await uow.TryBeginMatchSettlementAsync(matchKey))
                {
                    var replay = await uow.LatestMatchSettlementAsync(replayKey)
                                 ?? throw new InvalidOperationException($"Match settlement {replayKey} was claimed without a response");
                    _log.LogInformation("Match end replay ignored match={MatchKey}", replayKey);
                    return (replay, false);
                }
                _log.LogWarning("MATCH END REUSED ID match={MatchKey}: a new match since that id settled, settled as its own", matchKey);
            }

            // The reward gate reads how long the match ran (the match clock since kickoff, and FIFA's game clock) and whom it
            // was against (the online pairing).
            var eligibility = seasons.RewardEligibility;
            var clock = await uow.TakeMatchClockAsync();
            var facts = MatchEndFacts.From(body, clock?.RealSeconds(uow.NowSeconds), eligibility, quietFirstBy);
            facts = await NoteSerialLeaverAsync(uow, facts, clock?.OpponentPlayer);
            var liveWeekendLeague = _fut.WeekendLeague.LiveId();
            var weekendLeagueCapped = await PairCappedAsync(uow, RewardModes.WeekendLeague, clock?.OpponentPersona, liveWeekendLeague);
            // Weekend League and Squad Battles matches are won or lost by their own rules (a level score goes to penalties, a
            // quit is a loss), and the result they settle on is the match's result. Any match end takes a pending Squad
            // Battles CreateMatch; a no-contest scores nothing.
            var weekendLeague = scored == MatchOutcome.None ? null : await _fut.WeekendLeague.SettleAsync(uow, body, endReason, facts, weekendLeagueCapped);
            // Squad Battles points need a match of the minimum length, as its rewards do: a CPU opponent never leaves, so a
            // shorter end is a crafted one. A quit still counts (a loss worth no points that marks the squad played).
            var squadBattles = await _fut.SquadBattles.SettleAsync(uow, body,
                counted: scored != MatchOutcome.None && (facts.LongEnough || facts.Quitter));
            // Any match end takes the pending Rivals pairing; with one (and no other mode's CreateMatch) the match is Rivals.
            var pairing = await _fut.Rivals.TakePairingAsync(uow);
            var mode = weekendLeague is not null ? RewardModes.WeekendLeague
                : squadBattles is not null ? RewardModes.SquadBattles
                : pairing is not null ? RewardModes.Rivals
                : null;
            var opponent = clock?.OpponentPersona ?? pairing?.OpponentPersona;
            var pairCapped = mode == RewardModes.WeekendLeague ? weekendLeagueCapped
                : mode == RewardModes.Rivals && await PairCappedAsync(uow, RewardModes.Rivals, opponent, null);
            var outcome = weekendLeague?.Outcome
                ?? (squadBattles is { Counted: true } battle ? battle.Outcome
                    : mode == RewardModes.Rivals ? RewardEligibility.PairedOutcome(facts, scored, pairCapped, mine, theirs)
                    : scored);
            var restricted = mode is not null && (await RestrictedCardsAsync(uow, EndItemIds(body))).Count > 0;
            var decision = RewardEligibility.Decide(seasons, mode, facts, outcome, pairCapped, restricted);
            if (facts.Desync && mode is RewardModes.Rivals or RewardModes.WeekendLeague && clock?.OpponentPlayer is { Length: > 0 } desyncedWith)
            {
                // The second report of one match (the opponent's, or a reused id's) counts once: key on the id FIFA sent.
                desynced = (desyncedWith, (matchKey ?? "unidentified").Split('@')[0], facts.RealSeconds);
            }
            // The opponent left an online match: its own client may never report (Alt+F4, crash), so its loss is settled now.
            if (facts.OpponentLeft && mode is RewardModes.Rivals or RewardModes.WeekendLeague)
            {
                leaver = clock?.OpponentPlayer;
                await RecordLeaveAsync(uow, facts, clock, mode, matchKey, mine, theirs, outcome);
            }
            // The mode's flat completion reward, or the desync consolation coins.
            var (award, earned, partials) = RewardEligibility.Coins(seasons, decision, outcome, body);
            var balance = await uow.AddCreditsToAsync(uow.ClubId, earned);
            // Only a paid match counts objective progress and Season XP (a void Weekend League match counts for nothing). A win
            // is the settled result: a shootout win in Squad Battles or the Weekend League, or an opponent who left, is a win.
            if (decision.CountsProgress && weekendLeague is not { Void: true })
            {
                await _fut.Objectives.RecordMatchEndAsync(uow, endReason, body, won: outcome == MatchOutcome.Win, draft: false, mode: decision.Mode);
            }
            // Only identified matches that settled to a result (a win, draw or loss; a quit is a loss) consume loans: an end
            // the server gives no result (no-contest, too short, desync, the stayer's no-result rule, pair cap) uses no
            // loan game. Settlement replays returned above.
            if (matchKey is not null && outcome != MatchOutcome.None)
                await ConsumeLoanMatchesAsync(uow, body);
            await RecordItemStatsAsync(uow, body, endReason, outcome);
            // A no-contest is not rated and not in the club record, and a Weekend League match is not rated in Rivals (it adds
            // to the Weekly Score instead).
            var rivals = await _fut.Rivals.SettleAsync(uow, pairing, outcome, mine, rate: weekendLeague is null);
            if (outcome != MatchOutcome.None)
            {
                await uow.AddClubRecordAsync(outcome == MatchOutcome.Win ? 1 : 0, outcome == MatchOutcome.Draw ? 1 : 0, outcome == MatchOutcome.Loss ? 1 : 0);
            }
            var pack = await SettleRewardAsync(uow, decision, opponent, liveWeekendLeague, earned);
            if (rivals is not null)
            {
                _log.LogInformation("Rivals match rated rating={Rating}({Delta:+#;-#;0}) division={Division} placed={Placed} weeklyScore=+{Gain}",
                    rivals.Record.SkillRating, rivals.Record.LastDelta, rivals.Record.Division, rivals.Record.Placed, rivals.ScoreGain);
                // Promotion coins are credited by the rating; the response's balance includes them.
                balance = await uow.CreditsAsync();
            }
            _log.LogInformation("Match end match={MatchKey} endReason={Reason} flags={Flags} score={Mine}-{Theirs} outcome={Outcome} mode={Mode} verdict={Verdict} gameSeconds={GameSeconds} realSeconds={RealSeconds} award={Award} earned={Earned} completionPack={Pack} balance={Balance}",
                matchKey ?? "unidentified", endReason.Length > 0 ? endReason : "UNSET", facts.Flags, mine, theirs, outcome, mode ?? "none", decision.Verdict,
                facts.GameSeconds, facts.RealSeconds, award, earned, pack?.ToString() ?? "none", balance);
            var ended = TemplateLibrary.Core.Object("match/ended", new TemplateArgs
            {
                ["endReason"] = body.Str("endReason") is { Length: > 0 } reason ? reason : outcome == MatchOutcome.Win ? "WIN" : "LOSS",
                ["items"] = body.Arr("items") ?? new JsonArray(),
                ["matchData"] = body.Str("matchData"),
                ["award"] = award,
                ["earned"] = earned,
                ["partials"] = partials,
                ["balance"] = balance,
                ["won"] = outcome == MatchOutcome.Win ? 1 : 0,
                ["draw"] = outcome == MatchOutcome.Draw ? 1 : 0,
                ["loss"] = outcome == MatchOutcome.Loss ? 1 : 0,
                ["played"] = outcome == MatchOutcome.None ? 0 : 1,
                ["unopenedPacks"] = (await uow.UnopenedPackIdsAsync()).Count,
            });
            foreach (var key in EchoedEndKeys.Where(body.ContainsKey))
            {
                ended[key] = body[key]?.DeepClone();
            }
            if (squadBattles is not null)
            {
                ended["squadBattlesScore"] = squadBattles.Score;
                // The week's matches counted so far, as the hub shows them.
                ended["gamesPlayed"] = squadBattles.GamesCounted;
            }
            if (rivals is not null)
            {
                RivalsService.AddMatchEndFields(ended, rivals);
            }
            if (matchKey is not null)
            {
                await uow.CompleteMatchSettlementAsync(matchKey, ended);
            }
            return (ended, true);
        }, cancellationToken);
        if (settled)
        {
            // Only after the settlement committed.
            game.UserSessionRefresh.Set();
            if (desynced is { } desync)
            {
                game.DesyncQuarantine.Note(game.Player.Id, desync.Opponent, desync.Match, desync.RealSeconds);
            }
            if (leaver is not null)
            {
                await SettleLeaverAsync(leaver, body, cancellationToken);
            }
        }
        return response;
    }

    /// <summary>
    /// For a DNF end of an online game: how long before this player's last gameplay datagram the opponent's last one reached
    /// the relay. <see cref="MatchEndFacts.From"/> reads it on a lost-connection end as the opponent leaving (owner,
    /// 2026-10-04: a crash, pulled cable or dead connection carries no opponent-left bit, so the stayer was given the loss),
    /// and on any DNF end as whether the opponent's game ran to the end (2026-10-07). Logged for every such end so the
    /// relayQuietSeconds threshold can be checked against live games; null for any other end, or without a relay or its
    /// record of the game. On a lost-connection end, an opponent who left the live Blaze game first went quiet first
    /// whatever the relay heard (at least relayQuietSeconds): an alt-tab disconnect stops both streams in the same instant,
    /// so the alt-tabber, who never sends its own end, took no loss and its opponent no win (2026-10-08).
    /// </summary>
    internal TimeSpan? OpponentQuietFirstBy(JsonObject body) => OpponentQuietFirstBy(body, log: true);

    private TimeSpan? OpponentQuietFirstBy(JsonObject body, bool log)
    {
        var reason = body.Str("endReason");
        if (!reason.StartsWith("DNF_", StringComparison.OrdinalIgnoreCase)
            || _fut.Players?.MatchRoster is not { } roster || !JsonValues.TryInt(body.Get("gid"), out var gameId) || gameId <= 0)
        {
            return null;
        }
        // Every DNF result reads the relay: a lost connection needs it to say who left, and an opponent-left end needs it to
        // say whether the opponent's game was still running (RewardEligibility.StayerOutcome).
        var gap = roster.OpponentQuietFirstBy(_fut.Game.Player.Id, gameId);
        var rules = _fut.Game.Rules.Seasons.RewardEligibility;
        var lost = MatchEndFacts.LostConnection(reason, body.Int("matchStatusFlags"), rules);
        var leftFirst = lost && roster.OpponentLeftGameFirst(_fut.Game.Player.Id, gameId);
        if (log)
        {
            _log.LogWarning("MATCH END {Kind} gid={Game} player={Player} endReason={Reason} flags={Flags} opponentQuietFirstBy={Gap} opponentLeftGameFirst={LeftFirst}",
                lost ? "CONNECTION LOST" : "DNF", gameId, _fut.Game.Player.Id, reason, body.Int("matchStatusFlags"),
                gap is { } seconds ? $"{seconds.TotalSeconds:0.0}s" : "unknown", leftFirst);
        }
        var quiet = TimeSpan.FromSeconds(rules.OpponentLeft.RelayQuietSeconds);
        return leftFirst && !(gap >= quiet) ? quiet : gap;
    }

    /// <summary>
    /// Settles the loss of the opponent who left this club's online match (owner, 2026-10-03): its own client may never
    /// report (Alt+F4, a crash or a dropped connection), so the match end of the player who stayed opens the leaver's club
    /// (online or not) and settles its match there as a QUIT, exactly as its own quit would: a loss in Rivals (rated), the
    /// Weekend League (a game used) or its online Draft run (the run ends), in the club record, paying nothing. The
    /// leaver's FUT shows it on its next login. Only the leaver's match against this club is settled (its match clock
    /// still names this club), and only once: a later end of its own is a replay (the same match id), or finds nothing.
    /// When the gameplay relay heard the leaver until the end (it was not quiet relayQuietSeconds before this player), its
    /// client was still in the match and sends its own end: nothing is settled on this player's word, unless it left the
    /// live Blaze game first on a lost-connection end (an alt-tab disconnect, <see cref="OpponentQuietFirstBy(JsonObject)"/>). Both ends of a
    /// broken link can carry the opponent-left bits; the first to report took the win and gave the other a QUIT it never
    /// made (mourad, Rivals 1-1, 2026-10-06), and its own end was then ignored as a replay.
    /// </summary>
    internal async ValueTask SettleLeaverAsync(string leaverId, JsonObject stayerBody, CancellationToken cancellationToken)
    {
        if (_fut.Players is not { IsShared: true } players || leaverId == _fut.Game.Player.Id)
        {
            return;
        }
        if (OpponentQuietFirstBy(stayerBody, log: false) is { } quiet
            && quiet.TotalSeconds < _fut.Game.Rules.Seasons.RewardEligibility.OpponentLeft.RelayQuietSeconds)
        {
            _log.LogWarning("MATCH LEAVER NOT SETTLED leaver={Leaver} stayer={Stayer} match={Match} endReason={Reason} flags={Flags} leaverQuietFirstBy={Gap:0.0}s: the relay heard the leaver until the end, its own end decides",
                leaverId, _fut.Game.Player.Id, stayerBody.Get("matchId")?.ToJsonString(), stayerBody.Str("endReason"), stayerBody.Int("matchStatusFlags"), quiet.TotalSeconds);
            return;
        }
        try
        {
            var identity = leaverId == PlayerIdentity.SinglePlayerId ? players.Local.Player : PlayerIdentity.Of(leaverId);
            using var lease = await players.AcquireAsync(identity, cancellationToken);
            var peer = lease.Session.Fut;
            var clock = await peer.Game.ReadAsync(uow => uow.MatchClockAsync(), cancellationToken);
            if (clock?.OpponentPersona != _fut.Game.Config.PersonaId)
            {
                _log.LogInformation("MATCH LEAVER already settled leaver={Leaver} match={Match}: its match against this club has ended",
                    leaverId, stayerBody.Get("matchId")?.ToJsonString());
                return;
            }
            // The leaver's view of the same match: the same ids and clock, the goals the other way round.
            var quit = new JsonObject { ["endReason"] = "QUIT", ["matchStatusFlags"] = 0, ["secondsPlayed"] = stayerBody.Int("secondsPlayed") };
            foreach (var key in new[] { "gid", "matchId" })
            {
                if (stayerBody.ContainsKey(key)) quit[key] = stayerBody[key]?.DeepClone();
            }
            quit["myMatchStats"] = stayerBody["opponentMatchStats"]?.DeepClone() ?? new JsonObject();
            quit["opponentMatchStats"] = stayerBody["myMatchStats"]?.DeepClone() ?? new JsonObject();
            var (slot, lastMatchWasDraft) = await peer.Draft.MatchEndStateAsync(cancellationToken);
            var ended = lastMatchWasDraft ? await peer.Draft.EndMatchAsync(slot, quit, cancellationToken) : null;
            if (ended is null)
            {
                // The leaver's club match: its quit names the players its own quit would, so their loan games are used
                // exactly as there (a quit settles as a loss; like any quit, it adds no game to the cards' stats).
                quit["items"] = await peer.Game.ReadAsync(uow => LeaverQuitItemsAsync(uow, peer.Game.Rules.Club.Squad.Starters), cancellationToken);
                ended = await peer.Matches.EndMatchAsync(quit, cancellationToken);
            }
            _log.LogWarning("MATCH LEAVER SETTLED leaver={Leaver} stayer={Stayer} match={Match} draft={Draft} loss={Loss}",
                leaverId, _fut.Game.Player.Id, quit.Get("matchId")?.ToJsonString(), lastMatchWasDraft, ended.Int("loss", ended.Int("lost")));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogError(error, "MATCH LEAVER settlement failed leaver={Leaver}", leaverId);
        }
    }

    /// <summary>
    /// The match-end <c>items</c> a leaver's own quit would have sent, as far as the server can know them: its active
    /// squad's starting XI, in slot order, each with the on-the-pitch marker (1) a real end gives them. The XI is exactly who
    /// <see cref="ParticipatingItemIds"/> counts from a real quit's first eleven entries; a substitute counts there only when
    /// his marker shows he came on, which the leaver's client never reported, so no bench player is named.
    /// </summary>
    private static async ValueTask<JsonArray> LeaverQuitItemsAsync(IUnitOfWork uow, int starters)
    {
        var squad = await uow.GetSquadAsync(await uow.ActiveSquadIdAsync());
        var ids = (squad?.Arr("players") ?? new JsonArray()).OfType<JsonObject>()
            .Select(slot => (Index: slot.Int("index"), Id: JsonValues.Int((slot.Obj("itemData") ?? slot).GetOr("id", slot.Get("itemId")))))
            .Where(slot => slot.Index >= 0 && slot.Index < starters && slot.Id > 0)
            .OrderBy(slot => slot.Index)
            .Select(slot => slot.Id)
            .Distinct();
        return new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id, ["fitness"] = 1 }).ToArray());
    }

    /// <summary>
    /// A search that ended without a game: FIFA follows CancelMatchmaking (or an expired or refused search) with a match
    /// end of DNF and no gid or match id. No match was played, so nothing is paid, counted or settled (Python server
    /// 50243f5: it had paid the loss coins and counted a 0-0 match, XP and a clean sheet, on every cancelled search).
    /// </summary>
    private async ValueTask<JsonObject> SearchEndedAsync(JsonObject body, CancellationToken cancellationToken)
    {
        // A pending Weekend League CreateMatch is dropped: the search ended without a match (Python server 24d7015).
        var (balance, unopened) = await _fut.Game.RunAsync("match-search-ended", async uow =>
        {
            await _fut.WeekendLeague.SearchEndedAsync(uow);
            await uow.TakeMatchClockAsync();
            return (await uow.CreditsAsync(), (await uow.UnopenedPackIdsAsync()).Count);
        }, cancellationToken);
        _log.LogInformation("Match end without a game (search ended) endReason=DNF balance={Balance}", balance);
        var ended = TemplateLibrary.Core.Object("match/ended", new TemplateArgs
        {
            ["endReason"] = body.Str("endReason"),
            ["items"] = body.Arr("items") ?? new JsonArray(),
            ["matchData"] = body.Str("matchData"),
            ["award"] = 0,
            ["earned"] = 0,
            ["partials"] = new JsonArray(),
            ["balance"] = balance,
            ["won"] = 0,
            ["draw"] = 0,
            ["loss"] = 0,
            ["played"] = 0,
            ["unopenedPacks"] = unopened,
        });
        foreach (var key in EchoedEndKeys.Where(body.ContainsKey))
        {
            ended[key] = body[key]?.DeepClone();
        }
        return ended;
    }

    /// <summary>The client report id when present, otherwise the id most recently issued by CreateMatch.</summary>
    /// <summary>Results whose numbers count toward player stats: a quit or the caller's own disconnect never counts.</summary>
    private static readonly HashSet<string> ItemStatsResults = new(StringComparer.Ordinal) { "WIN", "DRAW", "LOSS", "DNF_WIN", "DNF_DRAW", "DNF_LOSS" };

    /// <summary>
    /// Adds the match to each owned player that took part: season and lifetime games, goals, yellow and red cards
    /// (statsList rows 0-3, which the bio view reads) and assists (Python server ca228bc); the tenth club game makes the
    /// card loyal (<see cref="LoyaltyGames"/>). The match end lists the
    /// matchday squad in <c>items</c>, the XI first; its <c>fitness</c> is not a fitness level but one value shared by
    /// everyone who was on the pitch, so a player took part if he started, shares the starters' value, or contributed.
    /// Runs once per settled match (the settlement key guards a replay), and only for a match that settled to a result:
    /// an end the server gives no result (<paramref name="outcome"/> None) adds no game, as it uses no loan game.
    /// </summary>
    private static async ValueTask RecordItemStatsAsync(IUnitOfWork uow, JsonObject body, string endReason, MatchOutcome outcome)
    {
        var entries = (body.Arr("items") ?? new JsonArray()).OfType<JsonObject>().ToList();
        if (outcome == MatchOutcome.None || !ItemStatsResults.Contains(endReason) || entries.Count == 0)
        {
            return;
        }
        static long Count(JsonObject entry, params string[] names) =>
            names.Select(name => JsonValues.TryInt(entry.Get(name), out var value) ? value : 0).FirstOrDefault(value => value != 0) is var found && found > 0 ? found : 0;
        var starterFitness = entries.Take(11).Select(entry => entry.Get("fitness")?.ToJsonString()).ToHashSet();
        var rows = entries.Select((entry, position) => (Id: entry.Int("id"), Goals: Count(entry, "goals"), Assists: Count(entry, "assists"),
                Yellows: Count(entry, "yellowCards", "yellowcards", "yellows"), Reds: Count(entry, "redCards", "redcards", "reds"),
                Played: position < 11 || starterFitness.Contains(entry.Get("fitness")?.ToJsonString())))
            .Where(row => row.Id > 0 && (row.Played || row.Goals + row.Assists + row.Yellows + row.Reds > 0))
            .Select(row => new { native_item_id = row.Id, goals = row.Goals, assists = row.Assists, yellows = row.Yellows, reds = row.Reds })
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }
        await uow.ExecuteAsync("""
            -- query: match.item-stats
            UPDATE ultimateteam.player_items p SET
                match_stats = jsonb_build_object(
                    'current', (SELECT jsonb_agg(e.value::bigint + CASE e.ord WHEN 1 THEN 1 WHEN 2 THEN x.goals WHEN 3 THEN x.yellows WHEN 4 THEN x.reds ELSE 0 END ORDER BY e.ord)
                        FROM jsonb_array_elements_text(p.match_stats -> 'current') WITH ORDINALITY e(value, ord)),
                    'lifetime', (SELECT jsonb_agg(e.value::bigint + CASE e.ord WHEN 1 THEN 1 WHEN 2 THEN x.goals WHEN 3 THEN x.yellows WHEN 4 THEN x.reds ELSE 0 END ORDER BY e.ord)
                        FROM jsonb_array_elements_text(p.match_stats -> 'lifetime') WITH ORDINALITY e(value, ord))),
                assists = p.assists + x.assists, lifetime_assists = p.lifetime_assists + x.assists,
                loyalty_bonus = CASE WHEN (p.match_stats -> 'current' ->> 0)::bigint + 1 >= @loyal_games THEN 1 ELSE p.loyalty_bonus END
            FROM json_to_recordset(@rows) AS x(native_item_id bigint, goals bigint, assists bigint, yellows bigint, reds bigint)
            JOIN ultimateteam.items i ON i.club_id = @club AND i.native_item_id = x.native_item_id AND i.family_id = 1
            WHERE p.item_id = i.id
            """, ("rows", RelationalItemRepository.Rows(rows)), ("loyal_games", LoyaltyGames));
    }

    /// <summary>
    /// Games for the club after which a card is loyal (retail: ten). The tenth game also sets the stored loyalty bonus,
    /// so a bought card stays loyal after that whatever its stat rows say, as a packed card is from the start.
    /// </summary>
    private const long LoyaltyGames = 10;

    private string? SettlementKey(JsonObject body)
    {
        foreach (var key in new[] { "matchId", "gid", "matchReportId" })
        {
            if (body.Get(key) is { } value && JsonValues.HasValue(value))
            {
                return JsonValues.TryInt(value, out var numeric)
                    ? "id:" + numeric.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "id:" + value.ToJsonString();
            }
        }
        return CurrentMatchId is { } current ? "id:" + current.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Whether a CreateMatch body names Division Rivals (its match subtype) or the Weekend League (championId): both play the
    /// clubs' own squads against another club (Python server _RIVALS_MATCH_SUBTYPES and the championId create).
    /// </summary>
    private static bool IsOnlineClubCreate(JsonObject body) =>
        body.ContainsKey("championId") || RivalsCreateSubTypes.Contains(body.Str("matchSubType"));

    /// <summary>The CreateMatch subtypes of a Division Rivals match: a rated match and a placement match.</summary>
    private static readonly HashSet<string> RivalsCreateSubTypes = new(StringComparer.OrdinalIgnoreCase) { RivalsMatchSubType, "PLACEMENT_MATCH" };

    /// <summary>
    /// The pre-match handoff: the complete active squad with active club items in both
    /// <c>actives</c> and <c>club</c> (the kit selector derives ownership from this). A Rivals or Weekend League match (a
    /// paired online game, or a create naming either) plays the club as its online documents show it
    /// (<see cref="ClubMatchSquadAsync"/>); any other match (Squad Battles) keeps the kits and plays home fixtures at
    /// the club's own stadium.
    /// </summary>
    public async ValueTask<JsonObject> NativeMatchResponseAsync(long? opponentSlot, bool onlineClubCreate = false,
        CancellationToken cancellationToken = default)
    {
        var online = OnlineRivalsOpponent();
        var stableClub = online is not null || onlineClubCreate;
        var response = await _fut.Game.ReadAsync(async uow =>
        {
            JsonObject squad;
            if (stableClub)
            {
                squad = await ClubMatchSquadAsync(_fut, uow, includeStadium: false);
            }
            else
            {
                // A home fixture plays at the club's own stadium, like the online path; the flat aliases carried the
                // forced venue, so every home game sat in the Bernabéu (local 10-03). Away fixtures take their venue
                // from the opponent squad below.
                squad = await _fut.Club.NativeSquadAsync(uow);
                var actives = await _fut.SquadBattles.MatchActivesAsync(uow, opponent: false);
                squad["actives"] = ClubPresentationService.ToArray(actives);
                squad["club"] = ClubPresentationService.ToArray(actives);
                var venue = await _fut.Club.HomeVenueAsync(uow, actives);
                squad["stadiumAssetId"] = venue;
                squad["stadiumId"] = venue;
            }
            var local = TemplateLibrary.Core.Object("match/club-match", new TemplateArgs
            {
                ["squad"] = squad,
                ["now"] = _fut.Game.NowSeconds,
                ["stadiumId"] = _fut.SquadBattles.StadiumId,
                ["matchId"] = AllocateMatchId(),
            });
            if (online is null && opponentSlot is not null)
            {
                // Home fixtures read squad.actives; away fixtures resolve the venue from the opponent: the hub tile's own
                // frozen squad, so a real club hosts in its own stadium and a stand-in CPU in the Bernabéu.
                var opponent = await _fut.SquadBattles.SetDocumentAsync(uow, opponentSlot.Value);
                local["opponentSquad"] = opponent.DeepClone();
                local["awaySquad"] = opponent.DeepClone();
                local["opponent"] = opponent;
            }
            return local;
        }, cancellationToken);
        if (online is not null)
        {
            await ApplyOnlineRivalsAsync(response, response.Obj("squad")!, online, cancellationToken);
            LogSquadCards("SQUAD OWN CARDS", _fut.Game.Config.PersonaId, response.Obj("squad")!);
        }
        return response;
    }

    /// <summary>CreateMatch is a validation handshake: confirm the submitted players and echo the match context.</summary>
    private void EchoCreate(JsonObject response, JsonObject body)
    {
        if (body.Arr("playersAttributesValidation") is { } validation)
        {
            var entries = validation.OfType<JsonObject>().ToList();
            response["playersAttributesValidation"] = ClubPresentationService.ToArray(entries);
            // The cards whose attribute record the client does not hold at the current version. CardsDLL (0x180278120)
            // downloads exactly these (GET /attributes/metadata) before the match, so both clients play every card with the
            // same stats; answering [] left stale records on different stats on the two screens, the desync behind the
            // "match draw" screen (Python server 4736f1a).
            var mismatches = entries
                .Where(entry => entry.Int("defId") > 0 && _fut.Game.Cards.AttributeRecordVersion(entry.Int("defId")) != entry.Int("version"))
                .Select(entry => (JsonNode?)entry.Int("defId"))
                .ToArray();
            response["mismatches"] = new JsonArray(mismatches);
            if (mismatches.Length > 0)
            {
                _log.LogWarning("ATTR SYNC player={Player} refresh={Count} of {Total} defIds={DefIds} sent={Sent}", _fut.Game.Player.Id,
                    mismatches.Length, entries.Count, string.Join(",", mismatches.Take(30).Select(id => id!.ToJsonString())),
                    string.Join(",", entries.Take(30).Select(entry => $"{entry.Int("defId")}v{entry.Int("version")}")));
            }
        }
        foreach (var key in EchoedCreateKeys.Where(key => body.ContainsKey(key) && !response.ContainsKey(key)))
        {
            response[key] = body[key]?.DeepClone();
        }
    }

    /// <summary>
    /// The Online Draft CreateMatch handoff (Python server 51a1670): the Rivals contract with Draft squads. "squad" is the
    /// caller's own Draft XI; once Blaze has paired the search, the opponent's Draft XI (read from its own club) rides
    /// under the opponent aliases, and the host is the home side for both. There is no CPU opponent.
    /// </summary>
    private async ValueTask<JsonObject> OnlineDraftMatchResponseAsync(JsonObject run, CancellationToken cancellationToken)
    {
        var online = OnlineRivalsOpponent();
        var response = await _fut.Game.ReadAsync(async uow =>
        {
            var squad = await _fut.Draft.NativeMatchSquadAsync(uow, run);
            return TemplateLibrary.Core.Object("match/club-match", new TemplateArgs
            {
                ["squad"] = squad,
                ["now"] = _fut.Game.NowSeconds,
                ["stadiumId"] = _fut.SquadBattles.StadiumId,
                ["matchId"] = AllocateMatchId(),
            });
        }, cancellationToken);
        if (online is null)
        {
            _log.LogWarning("DRAFT ONLINE CREATE not paired yet: own squad only");
            return response;
        }
        // The reward gate's pair cap needs the opponent.
        await _fut.Game.RunAsync("draft-pairing", uow => uow.NoteMatchOpponentAsync(online.OpponentPersonaId, online.OpponentId), cancellationToken);
        var squad = response.Obj("squad")!;
        var isHome = online.CallerIsHome;
        squad["home"] = isHome;
        squad["isHomeTeam"] = isHome;
        response["home"] = isHome;
        response["isHomeTeam"] = isHome;
        if (_fut.Players?.TryAcquireExisting(online.OpponentId) is { } opponentLease)
        {
            using var lease = opponentLease;
            var peer = lease.Session.Fut;
            if (await peer.Game.RunAsync("draft-peer-squad", peer.Draft.OnlineMatchSquadAsync, cancellationToken) is { } opponent)
            {
                opponent["home"] = !isHome;
                opponent["isHomeTeam"] = !isHome;
                response["opponentSquad"] = opponent.DeepClone();
                // By side, as the Rivals handoff (Python server _rivals_attach_peer, which Online Draft shares).
                response["awaySquad"] = (isHome ? opponent : squad).DeepClone();
                response["opponent"] = opponent.DeepClone();
                response["homeSquad"] = (isHome ? squad : opponent).DeepClone();
            }
        }
        response["opponentPersonaId"] = online.OpponentPersonaId;
        response["opponentPersonaIds"] = new JsonArray(online.OpponentPersonaId);
        _log.LogWarning("DRAFT ONLINE CREATE gameId={GameId} isHomeTeam={Home} opponent={Opponent}", online.GameId, isHome, online.OpponentId);
        return response;
    }

    private OnlineOpponent? OnlineRivalsOpponent() =>
        _fut.Players?.MatchRoster?.OpponentOf(_fut.Game.Player.Id);

    /// <summary>
    /// GET user/list?personaIdList=..., the club summary FIFA's online kit-select screen (and House Rules) loads for the
    /// players of a match, 1-2 s after the pairing (live 10-04: unrouted, it answered {} in every online match). CardsDLL's
    /// parser (0x1802c4460) reads a "user" array of {personaId, clubName, clubAbbr, established, teamId, badge{resourceId,
    /// teamId}, homekit/awaykit{resourceId, teamId, categoryId, year}, squadList}; squadList and the Weekend League record are
    /// left to the client's defaults. Only the caller and its paired opponent are known; any other persona is left out.
    /// </summary>
    public async ValueTask<JsonObject> UserListAsync(IEnumerable<long> personas, CancellationToken cancellationToken)
    {
        var users = new JsonArray();
        foreach (var persona in personas.Distinct())
        {
            if (persona == _fut.Game.Config.PersonaId)
            {
                users.Add(await _fut.Game.ReadAsync(uow => UserSummaryAsync(_fut, uow), cancellationToken));
            }
            else if (OnlineRivalsOpponent() is { } online && online.OpponentPersonaId == persona
                && _fut.Players?.TryAcquireExisting(online.OpponentId) is { } opponentLease)
            {
                using var lease = opponentLease;
                var peer = lease.Session.Fut;
                users.Add(await peer.Game.ReadAsync(uow => UserSummaryAsync(peer, uow), cancellationToken));
            }
        }
        return new JsonObject { ["user"] = users };
    }

    private static async ValueTask<JsonNode?> UserSummaryAsync(FutServices club, IUnitOfWork uow)
    {
        var actives = await club.Club.ActiveClubItemsAsync(uow);
        JsonObject? Active(string state) => actives.FirstOrDefault(item => item.Text("itemState") == state);
        // Kit category follows kitType as the Squad Battles summaries send it: 0/1/3 is home/away/goalkeeper = 2/3/5.
        JsonObject? Kit(JsonObject? item) => item is null ? null : new JsonObject
        {
            ["resourceId"] = item.Int("resourceId"),
            ["teamId"] = item.Int("teamid"),
            ["categoryId"] = item.Int("kitType") switch { 1 => 3, 3 => 5, _ => 2 },
            ["year"] = item.Int("year"),
        };
        var badge = Active("activeBadge");
        var badgeResource = badge?.Int("resourceId") ?? 0;
        // A badge definition is 6000000 + crest team id (CardsDLL subtracts it to get the CrestID).
        var badgeBase = club.Game.Rules.SquadBattles.Sets.BadgeResourceBase;
        var crest = badgeResource > badgeBase && badgeResource - badgeBase < 1_000_000 ? badgeResource - badgeBase : 0;
        var user = new JsonObject
        {
            ["personaId"] = club.Game.Config.PersonaId,
            ["clubName"] = club.Game.Config.ClubName,
            ["clubAbbr"] = club.Game.Config.ClubAbbr,
            ["established"] = club.Game.Established.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["teamId"] = crest,
        };
        // Only real items: a missing badge or kit stays absent, as the client's defaults expect, never a zero resource id.
        if (badgeResource > 0) user["badge"] = new JsonObject { ["resourceId"] = badgeResource, ["teamId"] = crest };
        if (Kit(Active("activeHomeKit")) is { } home) user["homekit"] = home;
        if (Kit(Active("activeAwayKit")) is { } away) user["awaykit"] = away;
        return user;
    }

    /// <summary>
    /// Division Rivals pairs two real clubs: the host (the first roster player) is the home side for both clients. "squad"
    /// is always the caller's own club; the opponent's own club (squad, kits and stadium) rides under the opponent aliases,
    /// and homeSquad/awaySquad name the two sides, so for the away caller awaySquad is its own squad (Python server
    /// RIVALS_OWN_SQUAD: before it, awaySquad named the opponent for both callers).
    /// </summary>
    private async ValueTask ApplyOnlineRivalsAsync(JsonObject response, JsonObject squad, OnlineOpponent online,
        CancellationToken cancellationToken)
    {
        var isHome = online.CallerIsHome;
        squad["home"] = isHome;
        squad["isHomeTeam"] = isHome;
        response["home"] = isHome;
        response["isHomeTeam"] = isHome;
        // The reward gate's pair cap needs the opponent even when its club is not open to snapshot.
        await _fut.Game.RunAsync("match-opponent", uow => uow.NoteMatchOpponentAsync(online.OpponentPersonaId, online.OpponentId), cancellationToken);
        var (opponent, opponentRating) = await OpponentSquadAsync(online, includeStadium: false, cancellationToken);
        if (opponent is not null)
        {
            // The match end rates the result against the opponent's rating at kickoff (Python server 17172fc). An online
            // Friendly notes no pairing: the match end would settle it as Division Rivals (skill rating and rewards).
            if (!online.Friendly)
            {
                await _fut.Game.RunAsync("rivals-pairing", uow => _fut.Rivals.NotePairingAsync(uow, online, opponentRating), cancellationToken);
            }
            response["opponentSquad"] = opponent.DeepClone();
            response["awaySquad"] = (isHome ? opponent : squad).DeepClone();
            response["opponent"] = opponent.DeepClone();
            response["homeSquad"] = (isHome ? squad : opponent).DeepClone();
        }
        // championsWeekendLeagueStadiumId names the home club's venue, which both sides know (the away caller from the
        // opponent's squad, the tier 1 FUT Stadium when that club is not open); a Weekend League create replaces it with
        // the FUT Champions Stadium (CreateAsync).
        var home = isHome ? squad : opponent;
        response["championsWeekendLeagueStadiumId"] = _fut.Game.Rules.Club.Venues.MatchVenue(home is null ? null : home.Int("stadiumAssetId"));
        response["opponentPersonaId"] = online.OpponentPersonaId;
        response["opponentPersonaIds"] = new JsonArray(online.OpponentPersonaId);
        if (!online.Friendly)
        {
            response["matchSubType"] = RivalsMatchSubType;
        }
        _log.LogWarning("{Mode} MATCH HANDOFF gameId={GameId} isHomeTeam={Home} opponent={Opponent}", online.Friendly ? "FRIENDLY" : "RIVALS",
            online.GameId, isHome, online.OpponentId);
    }

    /// <summary>
    /// A club's squad as its Rivals and Weekend League match documents carry it (Python server SQUAD_IDENTITY_STABLE_CLUB):
    /// the club's whole active set exactly as /userMassInfo serves it (the real stadium item and its contents, both kits, the
    /// badge and the ball) and the club's own venue in the flat aliases. Rewriting the stadium item with the Bernabeu under
    /// the club's own item id made one id mean two things at kickoff and dropped the crest and ball (09-19 captures).
    /// <paramref name="includeStadium"/> adds the club's My Stadium document built from the same items, so its stadiumId
    /// agrees with the flat aliases (includeMyStadium; Python server PARITY_PEER_DOC).
    /// </summary>
    private static async ValueTask<JsonObject> ClubMatchSquadAsync(FutServices club, IUnitOfWork uow, bool includeStadium)
    {
        var squad = await club.Club.NativeSquadAsync(uow);
        var actives = (squad.Arr("actives") ?? new JsonArray()).OfType<JsonObject>().ToList();
        var venue = await club.Club.HomeVenueAsync(uow, actives);
        squad["stadiumAssetId"] = venue;
        squad["stadiumId"] = venue;
        if (includeStadium)
        {
            squad["myStadiumInfo"] = await club.Club.ClubStadiumAsync(uow, actives);
        }
        return squad;
    }

    /// <summary>
    /// The paired opponent's own club squad as the match handoff shows it (its own club and venue, the away side for the
    /// home caller), with its Skill Rating; null when its club is not open. The lease keeps the opponent's club open (not
    /// reaped or evicted) while its squad is copied.
    /// </summary>
    private async ValueTask<(JsonObject? Squad, long Rating)> OpponentSquadAsync(OnlineOpponent online, bool includeStadium,
        CancellationToken cancellationToken)
    {
        if (_fut.Players?.TryAcquireExisting(online.OpponentId) is not { } opponentLease)
        {
            return (null, 0);
        }
        using var lease = opponentLease;
        var peer = lease.Session.Fut;
        var (opponent, rating) = await peer.Game.ReadAsync(async uow =>
            (await ClubMatchSquadAsync(peer, uow, includeStadium), await peer.Rivals.SkillRatingAsync(uow)),
            cancellationToken);
        opponent["home"] = !online.CallerIsHome;
        opponent["isHomeTeam"] = !online.CallerIsHome;
        return (opponent, rating);
    }

    /// <summary>
    /// The live opponent's squad as the caller's pre-match screen asks for it: in an Online Draft pairing (the caller's last
    /// CreateMatch was one) the opponent's Draft XI, otherwise the club squad the match handoff carries (Python server
    /// _peer_active_squad_document). Null when the opponent's club is not open. It always carries the opponent's My Stadium
    /// document, whatever the request asked: CardsDLL's squad parser (0x3248e0) stores an online opponent's stadium from the
    /// squad whose personaId is that opponent's and clears it when that squad has none (0x3263ff), so the pre-match
    /// /squad/active?active=true fetch without includeMyStadium wiped the opponent's goal song and tifos (10-07).
    /// </summary>
    private async ValueTask<JsonObject?> PrematchOpponentAsync(OnlineOpponent online, CancellationToken cancellationToken)
    {
        var (slot, lastMatchWasDraft) = await _fut.Draft.MatchEndStateAsync(cancellationToken);
        if (lastMatchWasDraft && slot == DraftSlot.Online)
        {
            return await _fut.Draft.SquadForPersonaAsync(DraftSlot.Online, online.OpponentPersonaId, cancellationToken);
        }
        return (await OpponentSquadAsync(online, includeStadium: true, cancellationToken)).Squad;
    }

    /// <summary>
    /// GET squad/{id}/user/{persona} for the caller's paired opponent (Python server 59237cd): the Weekend League pre-match
    /// asks for its opponent this way, and answering with the caller's own squad made FIFA cancel every match ("data
    /// mismatch with your opponent's squad"). The same document the match handoff carries, with its My Stadium document;
    /// null for any other persona.
    /// </summary>
    public async ValueTask<JsonObject?> OpponentSquadForPersonaAsync(long persona, CancellationToken cancellationToken)
    {
        if (OnlineRivalsOpponent() is not { } online || online.OpponentPersonaId != persona)
        {
            return null;
        }
        var squad = await PrematchOpponentAsync(online, cancellationToken);
        _log.LogInformation("SQUAD BY USER persona={Persona} opponent={Opponent} served={Served}",
            persona, online.OpponentId, squad is not null);
        return squad;
    }

    /// <summary>
    /// GET squad/active/user/{persona}[?includeMyStadium=1], the fetch FIFA's Rivals pre-match (X-PERFORMANCE-GROUP
    /// ..._FUT_ONLINE_COOPPREMATCH) loads its one opponent squad per match with; CardsDLL's squad verifier compares the
    /// opponent's exchanged record with it (Python server SQUAD_ACTIVE_BY_PERSONA; unrouted, it answered {} and the match
    /// was cancelled). The caller's own persona gets its own active squad (with its My Stadium document and venue when
    /// asked); the caller's live match opponent gets the document the match handoff carries. Null for any other persona,
    /// which keeps the unknown answer.
    /// </summary>
    public async ValueTask<JsonObject?> ActiveSquadForPersonaAsync(long persona, bool includeStadium, CancellationToken cancellationToken)
    {
        JsonObject? squad = null;
        if (persona == _fut.Game.Config.PersonaId)
        {
            squad = await _fut.Game.ReadAsync(async uow => includeStadium
                ? await ClubMatchSquadAsync(_fut, uow, includeStadium: true)
                : await _fut.Club.NativeSquadAsync(uow), cancellationToken);
        }
        else if (OnlineRivalsOpponent() is { } online && online.OpponentPersonaId == persona)
        {
            squad = await PrematchOpponentAsync(online, cancellationToken);
        }
        _log.LogWarning("SQUAD ACTIVE BY PERSONA caller={Caller} persona={Persona} stadium={Stadium} served={Served} squadPersona={SquadPersona} squadName={SquadName} starters={Starters}",
            _fut.Game.Player.Id, persona, includeStadium, squad is not null, squad?.Get("personaId")?.ToJsonString(), squad?.Str("squadName"), Starters(squad));
        if (squad is not null) LogSquadCards("SQUAD SERVED CARDS", persona, squad);
        return squad;
    }

    /// <summary>
    /// The live opponent's squad for an exact GET squad/active stamped by FIFA's online pre-match screen (the zero-persona
    /// form of the pre-match fetch, which CardsDLL builds as /active?active=true while it knows no opponent persona; Python
    /// server RIVALS_OPPONENT_ACTIVE_SWAP). A caller whose search is queued but not paired yet waits up to the rules' budget
    /// for the pairing (the fetch can overtake it by about two seconds); it is answered the moment the pairing appears.
    /// Null when there is no opponent (after the wait): the caller keeps its own squad.
    /// </summary>
    public async ValueTask<JsonObject?> PrematchActiveSquadAsync(CancellationToken cancellationToken)
    {
        if (_fut.Players?.MatchRoster is not { } roster)
        {
            return null;
        }
        var rules = _fut.Game.Rules.Seasons.OnlineMatch;
        var caller = _fut.Game.Player.Id;
        var budget = roster.IsSearching(caller) ? rules.OpponentWait : TimeSpan.Zero;
        var started = Stopwatch.GetTimestamp();
        var online = roster.OpponentOf(caller);
        while (online is null && budget - Stopwatch.GetElapsedTime(started) is var remaining && remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining < rules.OpponentWaitPoll ? remaining : rules.OpponentWaitPoll, cancellationToken);
            online = roster.OpponentOf(caller);
        }
        var waited = Stopwatch.GetElapsedTime(started).TotalSeconds;
        if (online is null)
        {
            if (budget > TimeSpan.Zero)
            {
                _log.LogWarning("RIVALS OPPONENT HANDOFF caller={Caller} reason=squad/active no pairing after waited={Waited:0.00}s budget={Budget:0.0}s, serving the caller's own squad",
                    caller, waited, budget.TotalSeconds);
            }
            return null;
        }
        var squad = await PrematchOpponentAsync(online, cancellationToken);
        _log.LogWarning("RIVALS OPPONENT HANDOFF caller={Caller} reason=squad/active opponent={Opponent} served={Served} waited={Waited:0.00}s squadPersona={SquadPersona} squadName={SquadName} starters={Starters}",
            caller, online.OpponentId, squad is not null, waited, squad?.Get("personaId")?.ToJsonString(), squad?.Str("squadName"), Starters(squad));
        if (squad is not null) LogSquadCards("SQUAD SERVED CARDS", online.OpponentPersonaId, squad);
        return squad;
    }

    /// <summary>
    /// One line per served squad listing every card as slot:itemId:defId:rating:attributeVersion:chemStyle, so a cancelled
    /// match ("data mismatch with your opponent's squad", FUT_ENDMATCHOVR) can be compared with what each side was sent.
    /// </summary>
    private void LogSquadCards(string label, long persona, JsonObject squad)
    {
        var cards = (squad.Arr("players") ?? new JsonArray()).OfType<JsonObject>()
            .Select(slot => (Index: slot.Int("index"), Item: slot.Obj("itemData")))
            .Where(slot => slot.Item is not null && slot.Item.Int("id") > 0)
            .Select(slot => (slot.Index, Item: slot.Item!))
            .Select(slot => $"{slot.Index}:{slot.Item.Int("id")}:{slot.Item.Int("resourceId")}:{slot.Item.Int("rating")}:" +
                            $"v{_fut.Game.Cards.AttributeRecordVersion(slot.Item.Int("resourceId"))}:{slot.Item.Int("playStyle")}");
        _log.LogInformation("{Label} caller={Caller} persona={Persona} cards={Cards}", label, _fut.Game.Player.Id, persona, string.Join(" ", cards));
    }

    /// <summary>The filled starting slots of a squad document, for the pre-match log lines.</summary>
    private int Starters(JsonObject? squad) =>
        (squad?.Arr("players") ?? new JsonArray()).Take(_fut.Game.Rules.Club.Squad.Starters).Count(slot => slot?["itemData"] is JsonObject);

    public async ValueTask<JsonObject> DraftMatchResponseAsync(JsonObject run, CancellationToken cancellationToken = default) =>
        await _fut.Game.ReadAsync(async uow =>
    {
        var squad = await _fut.Draft.NativeMatchSquadAsync(uow, run);
        // Different stages of the offline Draft handoff read different aliases; the Draft XI is away.
        squad["home"] = false;
        squad["isHomeTeam"] = false;
        // Solo Draft can assign either side as venue owner; keep the CPU squad in the handoff.
        return TemplateLibrary.Core.Object("draft/match", new TemplateArgs
        {
            ["squad"] = squad,
            ["opponent"] = await DraftOpponentAsync(uow, run, squadId: 1),
            ["now"] = _fut.Game.NowSeconds,
            ["stadiumId"] = _fut.SquadBattles.StadiumId,
        });
    }, cancellationToken);

    /// <summary>The CPU squad single-player Draft requests before POST /match; it owns the Draft venue.</summary>
    public async ValueTask<JsonObject> OfflineSquadAsync(long squadId, CancellationToken cancellationToken = default)
    {
        var squad = await _fut.Game.ReadAsync(async uow =>
            await DraftOpponentAsync(uow, await _fut.Draft.CurrentRunAsync(uow, DraftSlot.SinglePlayer), squadId), cancellationToken);
        squad["home"] = true;
        squad["isHomeTeam"] = true;
        return squad;
    }

    /// <summary>The Squad Battles opponent for the current Draft round (one per win so far).</summary>
    private async ValueTask<JsonObject> DraftOpponentAsync(IUnitOfWork uow, JsonObject? run, long squadId)
    {
        var rounds = _fut.Game.Rules.Draft.WinsToComplete;
        var opponent = await _fut.SquadBattles.OpponentAsync(uow, Math.Clamp(run?.Int("wins") ?? 0, 0, rounds - 1));
        opponent["id"] = squadId;
        opponent["squadId"] = squadId;
        opponent["squadType"] = "DRAFT_SQUAD";
        opponent["active"] = true;
        opponent["valid"] = true;
        return opponent;
    }
}
