using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>A scored Weekend League match: its outcome, whether it counted, and the record then; a void match counts for nothing.</summary>
internal sealed record WeekendLeagueMatch(MatchService.MatchOutcome Outcome, bool Counted, WeekendLeagueRecord? Record, long RivalsWeeklyScore, bool Void = false);

/// <summary>
/// The FUT Champions Weekend League as in FIFA 21 retail (Python server 56145ed, champions_wl.py): Friday 07:00 to Monday
/// 07:00 UTC, maxMatches matches (20 in phase 1), entry for FUT Champions Points redeemed before it starts or within the grace
/// hours after, its own scoring, and rewards settled once for every club at 17:00 UK time on the Monday it ends
/// (rules/champions_rewards.json, with Top 200 bands frozen when it opens), claimed per club.
/// </summary>
public sealed class WeekendLeagueService
{
    private readonly FutServices _fut;
    private readonly ILogger _log;

    public WeekendLeagueService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.WeekendLeague");
    }

    private GameContext Game => _fut.Game;
    private WeekendLeagueRules Rules => Game.Rules.Seasons.WeekendLeague;
    private ChampionsRewardRules Rewards => Game.Rules.ChampionsRewards;

    /// <summary>The Weekend Leagues this club may play: its redeemed entries and the open events every club enters.</summary>
    public async ValueTask<HashSet<long>> QualifiedIdsAsync(IUnitOfWork uow) => Qualified(await uow.WeekendLeagueEntriesAsync());

    private HashSet<long> Qualified(IEnumerable<long> entries) => entries.Concat(Rules.OpenEvents).ToHashSet();

    /// <summary>The Weekend League the screens are about: the live one, else the next.</summary>
    internal long SlotId() => Rules.Slot(Game.NowSeconds).Id;

    /// <summary>The Weekend League live now, if any (the reward gate's per-weekend pair cap).</summary>
    internal long? LiveId() => Rules.Live(Game.NowSeconds)?.Id;

    /// <summary>An entry for a Weekend League that has not finished (the Rivals hub's qualifiedInWeekendLeague).</summary>
    public async ValueTask<bool> HoldsEntryAsync(IUnitOfWork uow) =>
        (await QualifiedIdsAsync(uow)).Any(id => Rules.Event(id).End > Game.NowSeconds);

    /// <summary>
    /// POST rivals/user/weekendleague/access: spends FUT Champions Points on an entry, as FIFA does locally on a success
    /// (0x1801a9b89); nothing re-fetches it, so the server must store the same. The entry is for the live Weekend League
    /// inside its grace hours, otherwise for the next one, and is final.
    /// </summary>
    public async ValueTask<FutResult> RedeemAsync(IUnitOfWork uow)
    {
        await uow.LockClubAsync();
        var now = Game.NowSeconds;
        var record = await _fut.Rivals.ProgressAsync(uow);
        var target = Rules.RedeemTarget(now);
        var qualified = await QualifiedIdsAsync(uow);
        if (!record.Placed)
        {
            return FutResult.Error(400, "PLACEMENT_NOT_COMPLETE");
        }
        if (qualified.Contains(target.Id))
        {
            return FutResult.Error(400, "ALREADY_QUALIFIED");
        }
        if (record.FcPoints < Rules.QualificationPoints)
        {
            return FutResult.Error(400, "NOT_ENOUGH_POINTS");
        }
        record.FcPoints -= Rules.QualificationPoints;
        await uow.SaveRivalsRecordAsync(record);
        // Entries for long-finished Weekend Leagues are history.
        var expired = (await uow.WeekendLeagueEntriesAsync()).Where(id => Rules.Event(id).End <= now - 8 * SeasonRules.WeekSeconds);
        await uow.AddWeekendLeagueEntryAsync(target.Id, Rules.QualificationPoints, expired);
        _log.LogWarning("WEEKEND LEAGUE ENTRY club={Club} event={Event} start={Start} pointsLeft={Points}", uow.ClubId, target.Id, target.Start, record.FcPoints);
        return FutResult.Ok(new JsonObject());
    }

    /// <summary>
    /// POST champion/user/registration {"competitionCountryCode": "&lt;ISO&gt;"}. FIFA reads only competitionRegion from the
    /// reply (0x18030c100); its Play check wants competitionCountryCode in the next hub, so the country is stored.
    /// </summary>
    public async ValueTask<FutResult> RegisterAsync(IUnitOfWork uow, JsonNode? payload)
    {
        var country = ((payload as JsonObject)?.Str("competitionCountryCode") ?? string.Empty).Trim().ToUpperInvariant();
        country = country[..Math.Min(3, country.Length)];
        if (country.Length == 0 || !country.All(char.IsAsciiLetter))
        {
            return FutResult.Error(400, "INVALID_COUNTRY");
        }
        var region = Rules.RegionFor(country);
        await uow.SaveChampionRegistrationAsync(country, region);
        _log.LogInformation("CHAMPIONS REGISTRATION club={Club} country={Country} region={Region}", uow.ClubId, country, region);
        return FutResult.Ok(TemplateLibrary.Core.Object("seasons/champion-registered", new TemplateArgs { ["region"] = region }));
    }

    /// <summary>The registered country and region; a club that never registered counts as the default country and its region.</summary>
    internal async ValueTask<(string Country, string Region)> RegistrationAsync(IUnitOfWork uow) =>
        await uow.ChampionRegistrationAsync() is { } registered ? registered : (Rules.DefaultCountry, Rules.RegionFor(Rules.DefaultCountry));

    /// <summary>
    /// GET user/pidinfo (Python server c756982): the passport country the FUT Champions registration screen confirms (parser
    /// 0x180371bf0 reads competitionCountryCode only). Without it the screen showed "International" and YES did nothing.
    /// </summary>
    public async ValueTask<JsonObject> PidInfoAsync(IUnitOfWork uow) =>
        TemplateLibrary.Core.Object("seasons/pidinfo", new TemplateArgs { ["country"] = (await RegistrationAsync(uow)).Country });

    /// <summary>
    /// Remembers a Weekend League CreateMatch (it names championId; the match end carries no marker); any other clears it.
    /// A due lost-connection claim settles first.
    /// </summary>
    public async ValueTask RecordCreateAsync(IUnitOfWork uow, JsonObject body)
    {
        await ApplyConnectionClaimAsync(uow);
        await uow.SetChampionPendingAsync(body.ContainsKey("championId") ? (JsonValues.TryInt(body.Get("championId"), out var league) ? league : 0) : null);
    }

    /// <summary>Whether the club's current match is a Weekend League one: a Weekend League CreateMatch still pending.</summary>
    internal async ValueTask<bool> MatchPendingAsync(IUnitOfWork uow) =>
        await uow.ChampionPendingSinceAsync() is { } since && Game.NowSeconds - since <= Rules.PendingMatchSeconds;

    /// <summary>
    /// A search that ended with no game (a DNF without gid or match id) after a Weekend League CreateMatch (Python server
    /// 24d7015): not a match, and the pending CreateMatch is dropped so no later end takes it.
    /// </summary>
    internal ValueTask<int> SearchEndedAsync(IUnitOfWork uow) => uow.SetChampionPendingAsync(null);

    /// <summary>
    /// Scores a finished Weekend League match; null for any other match. It is won or lost (Python server 24d7015, c3992c5):
    /// a quit or disconnect is a loss; a desync (matchStatusFlags) voids it for both players; a level score goes to penalties;
    /// a level end the opponent quit on is a win; a level end after a lost connection is held as a claim that becomes a win
    /// once the opponent has had its time to report (<see cref="ApplyConnectionClaimAsync"/>); any other undecided level end
    /// is void. A void match counts for nothing: the match end pays and records it like a no-contest. The reward gate adds
    /// (PLAN 4.3): a game beyond the pair cap against this opponent is void; any other end before the minimum length is void
    /// (the quitter's is still a loss); the player whose opponent left gets <see cref="RewardEligibility.StayerOutcome"/>.
    /// </summary>
    internal async ValueTask<WeekendLeagueMatch?> SettleAsync(IUnitOfWork uow, JsonObject body, string endReason, MatchEndFacts facts, bool pairCapped)
    {
        await ApplyConnectionClaimAsync(uow);
        if (await uow.TakeChampionPendingAsync() is not { } pending || Game.NowSeconds - pending.CreatedAt > Rules.PendingMatchSeconds)
        {
            return null;
        }
        var (mine, theirs) = ObjectivesService.Goals(body);
        var myPens = body.Obj("myMatchStats")?.Int("penaltyGoals") ?? 0;
        var theirPens = body.Obj("opponentMatchStats")?.Int("penaltyGoals") ?? 0;
        var flags = body.Int("matchStatusFlags");
        var abandoned = MatchEndFacts.Abandoned(endReason);
        var gameId = JsonValues.HasValue(body.Get("gid")) ? body.Get("gid")!.ToJsonString().Trim('"') : null;
        if (gameId is not null)
        {
            Game.Competitions.NoteReport(gameId, uow.ClubId, abandoned, Game.NowSeconds, Rules.ReportMemorySeconds);
        }
        var bits = Rules.MatchStatusFlags;
        var outcome = pairCapped ? MatchService.MatchOutcome.None
            : (flags & bits.Desync) != 0 && !abandoned ? MatchService.MatchOutcome.None
            : abandoned ? MatchService.MatchOutcome.Loss
            : facts.OpponentLeft ? RewardEligibility.StayerOutcome(facts, mine, theirs)
            : !facts.LongEnough ? MatchService.MatchOutcome.None
            : endReason is "LOSS" or "DNF_LOSS" ? MatchService.MatchOutcome.Loss
            : endReason is "WIN" or "DNF_WIN" ? MatchService.MatchOutcome.Win
            : mine != theirs ? (mine > theirs ? MatchService.MatchOutcome.Win : MatchService.MatchOutcome.Loss)
            : myPens != theirPens ? (myPens > theirPens ? MatchService.MatchOutcome.Win : MatchService.MatchOutcome.Loss)
            : (flags & bits.OpponentQuit) != 0 ? MatchService.MatchOutcome.Win
            : MatchService.MatchOutcome.None;
        if (outcome == MatchService.MatchOutcome.None && !pairCapped && !facts.OpponentLeft && facts.LongEnough && (flags & bits.ConnectionLost) != 0 && (flags & bits.Desync) == 0 && gameId is not null)
        {
            var report = Game.Competitions.OpponentReport(gameId, uow.ClubId);
            if (report is { Conceded: true })
            {
                outcome = MatchService.MatchOutcome.Win;
            }
            else if (report is null)
            {
                await uow.SaveChampionClaimAsync(gameId, pending.CreatedAt);
                _log.LogWarning("CHAMPIONS MATCH_END CONNECTION LOST club={Club} gid={Game} score={Mine}-{Theirs}: a win if the opponent sends no result within {Seconds}s",
                    uow.ClubId, gameId, mine, theirs, Rules.ConnectionWinAfterSeconds);
            }
        }
        if (outcome == MatchService.MatchOutcome.None)
        {
            _log.LogWarning("CHAMPIONS MATCH_END VOID club={Club} endReason={Reason} flags={Flags} score={Mine}-{Theirs} gid={Game}",
                uow.ClubId, endReason.Length > 0 ? endReason : "UNSET", flags, mine, theirs, gameId);
            return new WeekendLeagueMatch(MatchService.MatchOutcome.None, false, null, 0, Void: true);
        }
        return await CountAsync(uow, pending.CreatedAt, outcome, abandoned, pending.LeagueId);
    }

    /// <summary>
    /// Counts one decided Weekend League match in the Weekend League live when it was created: only with an entry, up to the
    /// maximum matches; form moves by one between its bounds; a completed match also adds to the Rivals Weekly Score.
    /// </summary>
    private async ValueTask<WeekendLeagueMatch> CountAsync(IUnitOfWork uow, long matchAt, MatchService.MatchOutcome outcome, bool abandoned, long leagueId)
    {
        var live = Rules.Live(matchAt);
        if (live is not { } current || !(await QualifiedIdsAsync(uow)).Contains(current.Id))
        {
            _log.LogWarning("CHAMPIONS MATCH_END not counted club={Club} league={League} live={Live}", uow.ClubId, leagueId, live?.Id);
            return new WeekendLeagueMatch(outcome, false, null, 0);
        }
        var record = await uow.WeekendLeagueRecordAsync(current.Id);
        if (record.Games >= Rules.MaxMatches)
        {
            return new WeekendLeagueMatch(outcome, false, null, 0);
        }
        var key = RivalsService.OutcomeKey(outcome);
        record = record with
        {
            Games = record.Games + 1,
            Wins = record.Wins + (key == "win" ? 1 : 0),
            Draws = record.Draws + (key == "draw" ? 1 : 0),
            Losses = record.Losses + (key == "loss" ? 1 : 0),
            Form = Rules.FormAfter(record.Form, key),
        };
        await uow.SaveWeekendLeagueRecordAsync(record);
        var weekly = abandoned ? 0 : await _fut.Rivals.CountWeekendLeagueMatchAsync(uow);
        _log.LogWarning("CHAMPIONS MATCH_END club={Club} event={Event} outcome={Outcome} record={Wins}-{Draws}-{Losses} played={Played} form={Form} rivalsWeekly=+{Weekly}",
            uow.ClubId, current.Id, key, record.Wins, record.Draws, record.Losses, record.Games, record.Form, weekly);
        return new WeekendLeagueMatch(outcome, true, record, weekly);
    }

    /// <summary>
    /// Settles a held lost-connection claim once the opponent has had connectionWinAfterSeconds to report (Python server
    /// c3992c5): a win (Weekend League record, the win award, the club record) unless the opponent sent a result of its own
    /// that does not concede, and only while that same Weekend League is live. Called on the hub, CreateMatch and match end.
    /// </summary>
    internal async ValueTask ApplyConnectionClaimAsync(IUnitOfWork uow)
    {
        var now = Game.NowSeconds;
        if (await uow.TakeDueChampionClaimAsync(now - Rules.ConnectionWinAfterSeconds) is not { } claim)
        {
            return;
        }
        if (Game.Competitions.OpponentReport(claim.GameId, uow.ClubId) is { Conceded: false })
        {
            _log.LogWarning("CHAMPIONS CONNECTION LOST club={Club} gid={Game} stays void: the opponent sent a result too", uow.ClubId, claim.GameId);
            return;
        }
        if (Rules.Live(claim.MatchAt) is not { } match || Rules.Live(now) is not { } live || match.Id != live.Id)
        {
            _log.LogWarning("CHAMPIONS CONNECTION LOST club={Club} gid={Game} not counted: that Weekend League is over", uow.ClubId, claim.GameId);
            return;
        }
        var result = await CountAsync(uow, claim.MatchAt, MatchService.MatchOutcome.Win, abandoned: false, 0);
        if (!result.Counted)
        {
            return;
        }
        await uow.AddCreditsToAsync(uow.ClubId, Game.Rules.Seasons.MatchCompletionRewards[RewardModes.WeekendLeague].Coins);
        await uow.AddClubRecordAsync(1, 0, 0);
        // The claim was held only past the reward gate's minimum length: a win as the gate pays it, with its Match Pack.
        var pack = await _fut.CompletionPacks.GrantAsync(uow, RewardModes.WeekendLeague);
        await _fut.CompletionPacks.GrantPointsAsync(uow, RewardModes.WeekendLeague);
        await uow.AddMatchRewardAsync(new MatchRewardRow(RewardModes.WeekendLeague, nameof(RewardVerdict.Paid), null, match.Id,
            Game.Rules.Seasons.MatchCompletionRewards[RewardModes.WeekendLeague].Coins, pack is null ? null : "completion", false));
        Game.UserSessionRefresh.Set();
        _log.LogWarning("CHAMPIONS CONNECTION WIN club={Club} gid={Game} record={Wins}-{Draws}-{Losses} played={Played} award={Award}",
            uow.ClubId, claim.GameId, result.Record!.Wins, result.Record.Draws, result.Record.Losses, result.Record.Games, Game.Rules.Seasons.MatchCompletionRewards[RewardModes.WeekendLeague].Coins);
    }

    // ---- rewards ----------------------------------------------------------------

    /// <summary>FIFA's tier number for a wins total: 1-3 Elite, 4-6 Gold, 7-9 Silver, 10-12 Bronze (Python server 764ef6e).</summary>
    private long TierForWins(long wins) => Rewards.ClientTierForWins(wins, Rules.MaxMatches);

    /// <summary>The Top bands every Weekend League pays: the places the Top size sets, as currently published.</summary>
    internal List<ChampionsBand> Bands() => Rewards.Bands();

    /// <summary>
    /// A league's prizeTiers (parser 0x18032c301): the Top bands it pays are LB_RANKING_TYPE with their leaderboard positions and go
    /// to their own list, numbered from 1; ranks by wins are NUMBER_OF_WINS_TYPE with tierLevel 1-12. Each shows the reward it pays.
    /// </summary>
    private JsonArray PrizeTiers()
    {
        JsonNode Tier(ChampionsRank rank, string type, int tierLevel, long low, long high) => TemplateLibrary.Core.Object("seasons/prize-tier", new TemplateArgs
        {
            ["tierType"] = type,
            ["tierLevel"] = tierLevel,
            ["low"] = low,
            ["high"] = high,
            ["awards"] = _fut.Rivals.RenderOption(ChampionsRewardRules.Reward(rank), RewardSources.WeekendLeague, ownTradeability: true),
        });
        var tiers = new JsonArray();
        foreach (var band in Bands())
        {
            tiers.Add(Tier(band.Rank, "LB_RANKING_TYPE", band.ClientTier, band.Low, band.High));
        }
        foreach (var rank in Rewards.Ranks.Where(rank => rank.ByWins))
        {
            tiers.Add(Tier(rank, "NUMBER_OF_WINS_TYPE", rank.ClientTier, rank.Wins![0], rank.Wins[1]));
        }
        return tiers;
    }

    /// <summary>
    /// Settles a Weekend League once for every club at its rewards time (17:00 UK time on the Monday it ends; retail paid Thursday):
    /// each ranked club's result with its reward fixed then, so a later rules edit never changes it. The Top 200 bands are the ones
    /// frozen at its opening; staff clubs are left out while rules/staff.json says so, their places passing to the clubs below. A
    /// concurrent settlement waits on the first.
    /// </summary>
    private async ValueTask SettleAsync(IUnitOfWork uow, long eventId)
    {
        var bands = Bands();
        var staff = Game.Rules.Staff.Excludes(StaffRules.WeekendLeague, Rules.Event(eventId).End) ? await uow.StaffClubIdsAsync(Game.Rules.Staff) : null;
        var standings = Rewards.Standings(eventId, await uow.WeekendLeagueRowsAsync(eventId, Game.Rules.Seasons.Rivals.RatingVersion), Rules.MaxMatches,
            bands, staff);
        if (!await uow.TryBeginChampionSettlementAsync(eventId, standings.Count))
        {
            return;
        }
        foreach (var standing in standings)
        {
            await uow.AddChampionResultAsync(eventId, standing);
        }
        _log.LogWarning("CHAMPIONS SETTLED event={Event} ranked={Ranked} bands={Bands} staffClubs={Staff}", eventId, standings.Count,
            string.Join(",", bands.Select(band => $"{band.Low}-{band.High}")), staff?.Count ?? 0);
    }

    /// <summary>The claim window: the Weekend Leagues whose rewards can still be claimed.</summary>
    internal (long From, long To) PrizeWindow() => Rules.LatestStarted(Game.NowSeconds).Id is var latest ? (latest - Rules.PrizeEventsBack, latest) : default;

    /// <summary>
    /// Settles every Weekend League of the claim window whose rewards time has passed. No query once they are known settled
    /// (<see cref="CompetitionCache"/>).
    /// </summary>
    internal ValueTask EnsureSettledAsync(IUnitOfWork uow)
    {
        var (from, to) = PrizeWindow();
        var due = Enumerable.Range(0, (int)(to - from + 1)).Select(offset => from + offset)
            .Where(id => id >= Rules.FirstRewardEvent && Game.NowSeconds >= Rules.RewardsTime(id));
        return Game.Competitions.EnsureSettledAsync("champions", due, uow.ChampionSettledEventsAsync, id => SettleAsync(uow, id));
    }

    /// <summary>This club's settled, unclaimed Weekend League results within the claim window, oldest first, the due ones settled first.</summary>
    private async ValueTask<List<ChampionResult>> UnclaimedAsync(IUnitOfWork uow)
    {
        await EnsureSettledAsync(uow);
        var (from, to) = PrizeWindow();
        return await uow.UnclaimedChampionResultsAsync(from, to);
    }

    private static JsonArray UnclaimedList(IEnumerable<long> events) =>
        new(events.Select(id => (JsonNode?)TemplateLibrary.Core.Object("seasons/champion-unclaimed", new TemplateArgs { ["eventId"] = id })).ToArray());

    /// <summary>
    /// champion/user/prize: collects every settled Weekend League reward (reply parser 0x180301ef0). FIFA adds the listed
    /// coins and packs itself, so exactly what is listed is credited; the events are marked claimed first, so a failure can
    /// lose a reward but never pay it twice.
    /// </summary>
    public async ValueTask<JsonObject> ClaimPrizesAsync(IUnitOfWork uow)
    {
        await uow.LockClubAsync();
        var unclaimed = await UnclaimedAsync(uow);
        var claimed = unclaimed.Count == 0 ? new HashSet<long>() : await uow.ClaimChampionResultsAsync(unclaimed.Select(result => result.EventId));
        var awarded = new JsonArray();
        var prizes = unclaimed.Where(result => claimed.Contains(result.EventId)).ToList();
        if (prizes.Count > 0)
        {
            var rivals = await _fut.Rivals.ProgressAsync(uow);
            foreach (var result in prizes)
            {
                var reward = result.Reward;
                if (reward.Coins > 0)
                {
                    await uow.AddCreditsToAsync(uow.ClubId, reward.Coins);
                }
                // Each pack line's own untradeable choice pays (migration 126). A result settled before it stored the
                // champions_rewards.json flag, which matched rules/tradeability.json for every Champions pack.
                foreach (var pack in reward.Packs)
                {
                    for (var copy = 0; copy < pack.Count; copy++)
                    {
                        await uow.AddUnopenedPackAsync(pack.PackId, RewardSources.WeekendLeague, untradeable: pack.Untradeable);
                    }
                }
                var gained = _fut.Rivals.AddFcPoints(rivals, reward.FcPoints);
                var settledRank = Rewards.Ranks.FirstOrDefault(rank => rank.TierLevel == result.TierLevel);
                var top = settledRank is { ByWins: false };
                awarded.Add(TemplateLibrary.Core.Object("seasons/champion-prize", new TemplateArgs
                {
                    ["eventId"] = result.EventId,
                    // A Top place shows its position; every result shows the rank its wins earned (tier 1 read as Elite 1).
                    ["position"] = top ? result.Position : 0,
                    ["tierLevel"] = top ? TierForWins(result.Wins) : settledRank?.ClientTier ?? TierForWins(result.Wins),
                    ["awards"] = _fut.Rivals.RenderOption(reward, RewardSources.WeekendLeague, ownTradeability: true),
                }));
                _log.LogWarning("CHAMPIONS PRIZE club={Club} event={Event} position={Position} rank={Rank} wins={Wins} coins={Coins} fc={Fc}(+{Gained})",
                    uow.ClubId, result.EventId, result.Position, result.RankName, result.Wins, reward.Coins, reward.FcPoints, gained);
            }
            await uow.SaveRivalsRecordAsync(rivals);
        }
        return TemplateLibrary.Core.Object("seasons/prize-claimed", new TemplateArgs { ["awarded"] = awarded });
    }

    // ---- documents ----------------------------------------------------------------

    /// <summary>championEvent.upcomingChampionEvents: the live Weekend League (active) first, then the next one.</summary>
    private JsonArray Calendar(long now, HashSet<long> qualified)
    {
        var entries = new JsonArray();
        foreach (var (item, active) in new[] { (Rules.Live(now), true), (Rules.Upcoming(now), false) })
        {
            if (item is { } weekendLeague)
            {
                entries.Add(TemplateLibrary.Core.Object("seasons/champion-calendar-entry", new TemplateArgs
                {
                    ["eventId"] = weekendLeague.Id,
                    ["start"] = weekendLeague.Start,
                    ["end"] = weekendLeague.End,
                    ["active"] = active,
                    ["qualified"] = qualified.Contains(weekendLeague.Id),
                }));
            }
        }
        return entries;
    }

    /// <summary>One board FIFA reads: a weekend or a month of Weekend Leagues, the current one or the previous.</summary>
    private sealed record View(long Id, long[] Events, bool Monthly, bool Current, long Start, long End);

    /// <summary>
    /// The four events FIFA indexes by position in previousChampionEvents (0x1802fd240, 0x18012e380): weekly previous, weekly
    /// current, monthly previous, monthly current (Python server 764ef6e).
    /// </summary>
    private List<View> Views(long now)
    {
        var current = Rules.Slot(now);
        var previous = Rules.Event(current.Id - 1);
        var views = new List<View>();
        foreach (var (item, isCurrent) in new[] { (previous, false), (current, true) })
        {
            views.Add(new View(item.Id, [item.Id], false, isCurrent, item.Start, item.End));
        }
        foreach (var (back, isCurrent) in new[] { (1, false), (0, true) })
        {
            var month = Rules.MonthOf(now, back);
            views.Add(new View(month.Id, month.Events.ToArray(), true, isCurrent, month.Start, month.End));
        }
        return views;
    }

    /// <summary>
    /// One Weekend League, or a month of them, as an event object (parser 0x18032b420). maxMatches is what FIFA shows as the
    /// Weekend League's match count (seasons.json weekendLeague.maxMatches; a month's is that times its Weekend Leagues).
    /// </summary>
    private async ValueTask<JsonObject> LeagueDocAsync(IUnitOfWork uow, View view, long now) =>
        TemplateLibrary.Core.Object("seasons/champion-league", new TemplateArgs
        {
            ["eventId"] = view.Id,
            ["localizedName"] = Game.Rules.Seasons.Champions.LocalizedName,
            ["start"] = view.Start,
            ["end"] = view.End,
            ["current"] = now,
            ["maxMatches"] = view.Monthly ? Rules.MaxMatches * Math.Max(1, view.Events.Length) : Rules.MaxMatches,
            ["minMatchesToRank"] = Rewards.MinMatchesToRank,
            ["prizeTiers"] = view.Monthly ? new JsonArray() : PrizeTiers(),
        });

    /// <summary>The board a query names: an event id among the four, else period (curr or prev) and type (base or aggregate: month).</summary>
    private View ViewFor(List<View> views, string? period, string? type, long? eventId = null)
    {
        if (eventId is { } id && views.FirstOrDefault(view => view.Id == id) is { } match)
        {
            return match;
        }
        var monthly = (type ?? "base").ToLowerInvariant() is "aggregate" or "month";
        var current = (period ?? "curr").ToLowerInvariant() != "prev";
        return views.First(view => view.Monthly == monthly && view.Current == current);
    }

    /// <summary>
    /// A board's standings, most wins first, then Skill Rating, name and club: other clubs' rows from the shared cache (reused
    /// for standingsCacheSeconds; settlement reads fresh), this club's own from its current records when given (else its
    /// cached row stays).
    /// </summary>
    private async ValueTask<List<ChampionBoardRow>> RankedAsync(IUnitOfWork uow, View view, ChampionBoardRow? own)
    {
        var rows = await Game.Competitions.RowsAsync(view.Monthly ? "champions-month" : "champions-week", view.Id, Rules.StandingsCacheSeconds,
            () => uow.ChampionBoardRowsAsync(view.Events, Game.Rules.Seasons.Rivals.RatingVersion, Rules.RegionFor(Rules.DefaultCountry)));
        if (own is null)
        {
            return Ordered(rows);
        }
        var cached = rows.FirstOrDefault(row => row.ClubId == uow.ClubId);
        var table = rows.Where(row => row.ClubId != uow.ClubId).ToList();
        if (own is { Games: > 0 } && (cached is not null || own.Name.Length > 0))
        {
            table.Add(own with { Name = cached?.Name ?? own.Name, Rating = cached?.Rating ?? own.Rating, Region = cached?.Region ?? own.Region });
        }
        return Ordered(table);
    }

    private static List<ChampionBoardRow> Ordered(IEnumerable<ChampionBoardRow> rows) =>
        rows.OrderByDescending(row => row.Wins).ThenByDescending(row => row.Rating)
            .ThenBy(row => row.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(row => row.ClubId).ToList();

    /// <summary>
    /// A board place as FIFA shows it: rank is the leaderboard position, 0 outside it (a weekend place needs the minimum
    /// matches, the wins a Top place needs in that Weekend League and a position inside the bands that weekend pays); tierLevel is 1-12 for a weekend's wins out of maxMatches,
    /// a leaderboard place included, 0 for a month. FIFA names tierLevel 1 Elite 1, so a Top place with few wins (any ranked
    /// club while few play) showed Elite.
    /// </summary>
    private (long Rank, long TierLevel) Placing(View view, ChampionBoardRow row, int position, long topPositions)
    {
        var top = row.Games >= Rewards.MinMatchesToRank && (view.Monthly || row.Wins >= Rewards.MinWinsForTopIn(view.Id)) && position <= topPositions;
        var rank = top ? position : 0;
        return view.Monthly ? (rank, 0) : (rank, TierForWins(row.Wins));
    }

    /// <summary>The last position a board's Top 200 place covers: topX for a month, the end of the weekend's last paid band.</summary>
    private long TopPositions(View view) =>
        view.Monthly ? Rules.TopX : Math.Min(Rules.TopX, Bands().Select(band => band.High).DefaultIfEmpty(0).Max());

    /// <summary>This club's row on a board, from its own records in the board's Weekend Leagues.</summary>
    private ChampionBoardRow OwnRow(IUnitOfWork uow, View view, IReadOnlyDictionary<long, WeekendLeagueRecord> records)
    {
        var mine = view.Events.Select(id => records.GetValueOrDefault(id)).OfType<WeekendLeagueRecord>().ToList();
        return new ChampionBoardRow(uow.ClubId, Game.Config.PersonaId, 0, Game.Config.DisplayName, mine.Sum(record => record.Wins), mine.Sum(record => record.Draws),
            mine.Sum(record => record.Losses), mine.Sum(record => record.Games), view.Events.Length == 1 ? mine.FirstOrDefault()?.Form ?? 0 : 0, 0, string.Empty);
    }

    /// <summary>This club's record and place on one board.</summary>
    private async ValueTask<(ChampionBoardRow Row, long Rank, long TierLevel, long Remaining)> OwnAsync(IUnitOfWork uow, View view, ChampionBoardRow own)
    {
        var table = await RankedAsync(uow, view, own);
        var position = table.FindIndex(row => row.ClubId == uow.ClubId) + 1;
        var (rank, tier) = position > 0 ? Placing(view, table[position - 1], position, TopPositions(view)) : (0, view.Monthly ? 0 : TierForWins(0));
        return (own, rank, tier, view.Monthly ? 0 : Math.Max(0, Rules.MaxMatches - own.Games));
    }

    private long RemainingFor(View view, ChampionBoardRow row) =>
        view.Current && !view.Monthly ? Math.Max(0, Rules.MaxMatches - row.Games) : Rules.FinishedRemainingMatches;

    /// <summary>
    /// The FUT home hub's championEvent block (parser 0x1803379f0): the calendar that drives the Rivals FUT Champions tile,
    /// the redeem popup and its countdown, and the home Champions tile's place. Built from the hub's one read of this club's
    /// competition rows (the due Weekend Leagues settled before it); the standings come from the shared cache.
    /// </summary>
    internal async ValueTask<JsonObject> HomeEventAsync(IUnitOfWork uow, HubCompetitionState state)
    {
        var now = Game.NowSeconds;
        var view = Views(now).First(candidate => !candidate.Monthly && candidate.Current);
        var own = OwnRow(uow, view, new Dictionary<long, WeekendLeagueRecord> { [state.WeekendLeague.EventId] = state.WeekendLeague });
        var (row, rank, tier, remaining) = await OwnAsync(uow, view, own);
        return TemplateLibrary.Core.Object("seasons/champion-home-event", new TemplateArgs
        {
            ["current"] = now,
            ["graceHours"] = Rules.GraceHours,
            ["calendar"] = Calendar(now, Qualified(state.WeekendLeagueEntries)),
            ["unclaimed"] = UnclaimedList(state.WeekendLeagueUnclaimed),
            ["eventId"] = view.Id,
            ["played"] = row.Games,
            ["remaining"] = remaining,
            ["minMatchesToRank"] = Rewards.MinMatchesToRank,
            ["wins"] = row.Wins,
            ["rank"] = rank,
            ["tierLevel"] = tier,
            ["prizeTiers"] = PrizeTiers(),
        });
    }

    /// <summary>
    /// GET champion/user/hub (parser 0x180378310). activeChampionLeagues holds the Weekend League the screens are about (the
    /// live one, else the next); FIFA shows its match tiles only once it has started and is in qualifiedChampionLeagueIds,
    /// otherwise the "not qualified" tiles lead to Rivals. previousChampionEvents holds the four boards FIFA reads by position,
    /// and userStats this club's place on each. A due lost-connection claim settles first.
    /// </summary>
    public async ValueTask<JsonObject> HubAsync(IUnitOfWork uow)
    {
        await ApplyConnectionClaimAsync(uow);
        var now = Game.NowSeconds;
        var views = Views(now);
        var current = views.First(view => !view.Monthly && view.Current);
        var records = await uow.WeekendLeagueRecordsAsync(views.SelectMany(view => view.Events).Distinct());
        var stats = new JsonArray();
        foreach (var view in views)
        {
            var (row, rank, tier, _) = await OwnAsync(uow, view, OwnRow(uow, view, records));
            stats.Add(TemplateLibrary.Core.Object("seasons/champion-user-stat", new TemplateArgs
            {
                ["eventId"] = view.Id,
                ["played"] = row.Games,
                ["wins"] = row.Wins,
                ["rank"] = rank,
                ["tierLevel"] = tier,
                ["form"] = row.Form,
            }));
        }
        var (country, region) = await RegistrationAsync(uow);
        var docs = new List<JsonObject>();
        foreach (var view in views)
        {
            docs.Add(await LeagueDocAsync(uow, view, now));
        }
        return TemplateLibrary.Core.Object("seasons/champion-hub", new TemplateArgs
        {
            ["leagues"] = new JsonArray(docs[views.IndexOf(current)].DeepClone()),
            ["previous"] = new JsonArray(docs.Select(doc => (JsonNode?)doc).ToArray()),
            ["unclaimed"] = UnclaimedList((await UnclaimedAsync(uow)).Select(result => result.EventId)),
            ["qualifiedIds"] = (await QualifiedIdsAsync(uow)).Contains(current.Id) ? new JsonArray(current.Id) : new JsonArray(),
            ["userStats"] = stats,
            ["country"] = country,
            ["region"] = region,
            ["formMin"] = Rules.FormMin,
        });
    }

    /// <summary>GET champion/user/stats?period=curr|prev&amp;type=base|aggregate (parser 0x18033a1b0): one flat record; rank is the Top 200 position.</summary>
    public async ValueTask<JsonObject> StatsAsync(IUnitOfWork uow, string? period, string? type)
    {
        var view = ViewFor(Views(Game.NowSeconds), period, type);
        var (row, rank, tier, remaining) = await OwnAsync(uow, view, OwnRow(uow, view, await uow.WeekendLeagueRecordsAsync(view.Events)));
        return TemplateLibrary.Core.Object("seasons/champion-stats", new TemplateArgs
        {
            ["eventId"] = view.Id,
            ["played"] = row.Games,
            ["wins"] = row.Wins,
            ["remaining"] = remaining,
            ["rank"] = rank,
            ["tierLevel"] = tier,
        });
    }

    /// <summary>
    /// GET champion/leaderboard?period&amp;type[&amp;region] or ?championEventId (TopX, parser 0x180303100): up to the Top 200, most
    /// wins first. score and tiebreak are objects; FIFA shows tiebreak / 1000 as Skill Rating.
    /// </summary>
    public async ValueTask<JsonObject> LeaderboardAsync(IUnitOfWork uow, string? period, string? type, string? region, long? eventId)
    {
        var view = ViewFor(Views(Game.NowSeconds), period, type, eventId);
        var table = await RankedAsync(uow, view, null);
        if (!string.IsNullOrEmpty(region))
        {
            table = table.Where(row => Rules.RegionMatches(region.ToUpperInvariant(), row.Region)).ToList();
        }
        var board = Game.Rules.Seasons.Leaderboards;
        return new JsonObject
        {
            ["entries"] = new JsonArray(table.Take(Rules.TopX).Select((row, index) => (JsonNode?)TemplateLibrary.Core.Object("seasons/champion-leaderboard-row", new TemplateArgs
            {
                ["name"] = PlayerNames.Readable(row.Name) ?? board.DefaultName,
                ["rank"] = index + 1,
                ["wins"] = row.Wins,
                ["tiebreak"] = row.Rating * 1000,
                ["remaining"] = RemainingFor(view, row),
                ["sku"] = Rules.LeaderboardSku,
                ["badge"] = board.Row.Badge,
                ["est"] = board.Row.Est,
                ["nucleusId"] = row.NucleusId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["personaId"] = row.PersonaId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })).ToArray()),
        };
    }

    /// <summary>
    /// GET champion/type/{week|month}/period/{curr|prev}/friends/stats (parser 0x1803043e0): every club on this server counts
    /// as a friend; score is a plain number, and sku comes before rank (FIFA's parser falls through from one to the other).
    /// </summary>
    public async ValueTask<JsonObject> FriendsAsync(IUnitOfWork uow, string kind, string period)
    {
        var view = ViewFor(Views(Game.NowSeconds), period, kind == "month" ? "aggregate" : "base");
        var table = await RankedAsync(uow, view, null);
        var topPositions = TopPositions(view);
        return TemplateLibrary.Core.Object("seasons/champion-friends", new TemplateArgs
        {
            ["stats"] = new JsonArray(table.Select((row, index) =>
            {
                var (rank, tier) = Placing(view, row, index + 1, topPositions);
                return (JsonNode?)TemplateLibrary.Core.Object("seasons/champion-friend-stat", new TemplateArgs
                {
                    ["sku"] = Rules.LeaderboardSku,
                    ["name"] = PlayerNames.Readable(row.Name) ?? Game.Rules.Seasons.Leaderboards.DefaultName,
                    ["rank"] = rank,
                    ["tierLevel"] = tier,
                    ["wins"] = row.Wins,
                    ["played"] = row.Games,
                    ["remaining"] = RemainingFor(view, row),
                });
            }).ToArray()),
        });
    }
}
