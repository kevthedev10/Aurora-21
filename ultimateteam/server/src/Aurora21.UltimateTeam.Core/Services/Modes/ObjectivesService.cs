using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// FUT Objectives (the FIFA 21 Seasonal Campaign, SCMP): a completed welcome group, grind objective
/// groups whose progress the server tracks from match ends, SBC submissions and pack openings, dated
/// one-off gifts, and a Season Progress level track. Every reward is paid once: coins, or packs in My Packs.
/// </summary>
public sealed class ObjectivesService
{
    /// <summary>
    /// The status of a refused objective claim (not earned yet, or not in the campaign). CardsDLL's claim callbacks
    /// (group 0x317ca0, objective 0x316180) treat any 200 as success: the group parser 0x317f20 marks the group claimed
    /// and the prize screen shows the rewards cached from the campaign, so an empty 200 looked like a paid reward. A
    /// 409 maps to a failure result (mapper 0x37e0a0) and shows FUT_SCMP_REDEEM_ERROR without touching the local
    /// campaign; the body is never read. 401/403/458/459/474/489-491 would end the session (0x36a7f0): never use them.
    /// </summary>
    private const HttpStatusCode ClaimRefusedStatus = HttpStatusCode.Conflict;

    private const string ClaimNotEarnedReason = "objective reward not earned yet";
    private const string ClaimUnknownReason = "objective reward not offered";

    private readonly FutServices _fut;
    private readonly ILogger _log;
    private readonly object _pendingLock = new();
    private readonly Dictionary<long, JsonObject> _pendingGroups = new();
    private long _pendingXp;

    public ObjectivesService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Objectives");
    }

    private GameContext Game => _fut.Game;
    private ObjectiveRules Rules => Game.Rules.Objectives;
    private WelcomeGroupRules Welcome => Rules.WelcomeGroup;

    /// <summary>The season in force: a custom season by date, else the default one (owner, 2026-10-07).</summary>
    private ActiveSeason Season => Rules.SeasonAt(Game.NowSeconds);

    private JsonObject WelcomeAward()
    {
        // The tile says the tradeability its payout grants, what the welcome claim in ClaimAsync pays.
        var award = _fut.Awards.Pack(Welcome.RewardPackId, Welcome.RewardPackCount);
        var untradeable = Welcome.RewardUntradeable || Game.Rules.Tradeability.Untradeable(RewardSources.Objective, Welcome.RewardPackId);
        award["untradeable"] = untradeable;
        award["isUntradeable"] = untradeable;
        return award;
    }

    public Task<JsonObject> CampaignAsync(CancellationToken cancellationToken = default) =>
        Game.ReadAsync(CampaignAsync, cancellationToken);

    private async ValueTask<JsonObject> CampaignAsync(IUnitOfWork uow)
    {
        var season = Rules.SeasonAt(uow.NowSeconds);
        var state = await uow.ObjectivesAsync(season.Id);
        // Groups carry their objectives here too: CardsDLL forces a group without objectives to LOCKED.
        var category = TemplateLibrary.Core.Object("objectives/category", new TemplateArgs
        {
            ["id"] = Rules.Category.Id,
            ["name"] = Rules.Category.Name,
            ["groups"] = Groups(state),
        });
        return TemplateLibrary.Core.Object("objectives/campaign", new TemplateArgs
        {
            ["id"] = season.Id,
            ["name"] = season.Name,
            ["userXp"] = state.UserXp,
            ["start"] = season.Start,
            ["end"] = season.End,
            ["category"] = category,
            ["levels"] = new JsonArray(Enumerable.Range(1, season.Progress.ServedLevels)
                .Select(level => (JsonNode)Level(season.Progress, level, state.ClaimedLevels.Contains(level), state.LevelOptions.GetValueOrDefault(level))).ToArray()),
        });
    }

    /// <summary>The details parser reads the group array from key <c>groupsList</c> (not the request's <c>groupList</c>).</summary>
    public Task<JsonObject> CategoryDetailsAsync(long categoryId, CancellationToken cancellationToken = default) =>
        Game.ReadAsync(async uow => TemplateLibrary.Core.Object("objectives/category-details", new TemplateArgs
        {
            ["groups"] = Groups(await uow.ObjectivesAsync(Rules)),
            ["categoryId"] = categoryId,
        }), cancellationToken);

    /// <summary>
    /// Claims one Season Progress level reward (enough XP, once, up to the served levels): the option the club picked when the
    /// level offers a choice (the claim's option id), else its one reward.
    /// </summary>
    public Task<JsonObject> ClaimLevelAsync(long level, long option = 0, CancellationToken cancellationToken = default) =>
        Game.RunAsync("objective-claim-level", async uow =>
        {
            await uow.LockClubAsync();
            var season = Rules.SeasonAt(uow.NowSeconds);
            var state = await uow.ObjectivesAsync(season.Id);
            var alreadyClaimed = state.ClaimedLevels.Contains(level);
            var progress = season.Progress;
            if (level < 1 || level > progress.ServedLevels || alreadyClaimed || state.UserXp < level * progress.XpPerLevel
                || option < 0 || option >= progress.RewardFor(level).Choices.Count)
            {
                return TemplateLibrary.Core.Object("objectives/claim-refused", new TemplateArgs { ["alreadyClaimed"] = alreadyClaimed });
            }
            var reward = progress.RewardFor(level).Choices[(int)option];
            await GrantAsync(uow, reward, RewardSources.Season);
            var current = await uow.ObjectivesAsync(season.Id);
            await uow.SaveObjectivesAsync(current with
            {
                ClaimedLevels = state.ClaimedLevels.Append(level).Distinct().Order().ToList(),
                LevelOptions = new Dictionary<long, long>(current.LevelOptions) { [level] = option },
            });
            _log.LogInformation("SCMP season {Season} level {Level} claimed, option {Option}", season.Id, level, option);
            return TemplateLibrary.Core.Object("objectives/level-claimed", new TemplateArgs
            {
                ["awards"] = Awards(reward, RewardSources.Season),
                ["items"] = new JsonArray(),
            });
        }, cancellationToken);

    /// <summary>
    /// Claims objective rewards: the welcome group (no group id, or its own), an open gift, one grind objective,
    /// one grind group, or — for rewards/all — everything claimable.
    /// </summary>
    /// <remarks>
    /// A reward claimed before it is earned (a group whose objectives are not all claimed, an objective short of its
    /// target) or one the campaign does not have is refused with an error, so the player is told instead of being
    /// handed an empty claim. Claiming a reward twice stays a plain <c>alreadyClaimed</c> answer: a retried request
    /// must not turn into an error. Claim-all takes whatever is claimable and never refuses.
    /// </remarks>
    public Task<FutResult> ClaimAsync(long? groupId, long? objectiveId, CancellationToken cancellationToken = default) =>
        Game.RunAsync("objective-claim", async uow =>
        {
            var awards = new JsonArray();
            await uow.LockClubAsync();
            var before = await uow.ObjectivesAsync(Rules);
            var outcome = ClaimOutcome.Paid;
            if (groupId is null)
            {
                await ClaimWelcomeAsync(uow, awards);
                foreach (var gift in Rules.Gifts)
                {
                    await ClaimGiftAsync(uow, gift, awards);
                }
                foreach (var group in Rules.GrindGroups)
                {
                    foreach (var objective in group.Objectives)
                    {
                        await ClaimObjectiveAsync(uow, group, objective.Id, awards);
                    }
                    await ClaimGroupAsync(uow, group, awards);
                }
            }
            else if (groupId == Welcome.Id)
            {
                outcome = await ClaimWelcomeAsync(uow, awards);
            }
            else if (Rules.Gifts.FirstOrDefault(gift => gift.Id == groupId) is { } gift)
            {
                outcome = await ClaimGiftAsync(uow, gift, awards);
            }
            else if (Rules.GrindGroups.FirstOrDefault(group => group.Id == groupId) is not { } group)
            {
                outcome = ClaimOutcome.Unknown;
            }
            else
            {
                outcome = objectiveId is { } id
                    ? await ClaimObjectiveAsync(uow, group, id, awards)
                    : await ClaimGroupAsync(uow, group, awards);
            }
            if (outcome is ClaimOutcome.NotEarned or ClaimOutcome.Unknown)
            {
                return ClaimRefused(uow.ClubId, groupId, objectiveId, outcome);
            }
            var after = await uow.ObjectivesAsync(Rules);
            return FutResult.Ok(TemplateLibrary.Core.Object("objectives/group-claimed", new TemplateArgs
            {
                ["awards"] = awards,
                ["items"] = new JsonArray(),
                ["alreadyClaimed"] = before.RewardClaimed == after.RewardClaimed
                    && before.ClaimedObjectives.Count == after.ClaimedObjectives.Count && before.ClaimedGroups.Count == after.ClaimedGroups.Count,
            }));
        }, cancellationToken);

    /// <summary>What one claim did: paid, had already been paid, is not earned yet, or names nothing in the campaign.</summary>
    private enum ClaimOutcome { Paid, AlreadyClaimed, NotEarned, Unknown }

    private FutResult ClaimRefused(long clubId, long? groupId, long? objectiveId, ClaimOutcome outcome)
    {
        var reason = outcome == ClaimOutcome.NotEarned ? ClaimNotEarnedReason : ClaimUnknownReason;
        // One line per refusal, so a "my group reward never came" report can be checked against what the client asked for.
        _log.LogWarning("SCMP CLAIM REFUSED club={Club} group={Group} objective={Objective} reason={Reason}",
            clubId, groupId, objectiveId?.ToString(CultureInfo.InvariantCulture) ?? "-", reason);
        return FutResult.Coded(ClaimRefusedStatus, reason);
    }

    /// <summary>Pays the welcome group once the club has earned it (rules/objectives.json welcomeGroup stat and target).</summary>
    private async ValueTask<ClaimOutcome> ClaimWelcomeAsync(IUnitOfWork uow, JsonArray awards)
    {
        var state = await uow.ObjectivesAsync(Rules);
        if (!Rules.IsLive(Welcome.Id)) return ClaimOutcome.Unknown;
        if (state.RewardClaimed)
        {
            return ClaimOutcome.AlreadyClaimed;
        }
        if (Welcome.Stat is { } stat && state.Progress(Welcome.Id, stat) < Welcome.Target)
        {
            return ClaimOutcome.NotEarned;
        }
        for (var copy = 0; copy < Welcome.RewardPackCount; copy++)
        {
            await uow.AddUnopenedPackAsync(Welcome.RewardPackId, RewardSources.Objective,
                untradeable: Welcome.RewardUntradeable || Game.Rules.Tradeability.Untradeable(RewardSources.Objective, Welcome.RewardPackId));
        }
        await uow.SaveObjectivesAsync(state with { RewardClaimed = true, UserXp = state.UserXp + Welcome.XpAward });
        awards.Add(WelcomeAward());
        _log.LogInformation("SCMP welcome reward granted: {Count} x pack {Pack}, +{Xp} XP", Welcome.RewardPackCount, Welcome.RewardPackId, Welcome.XpAward);
        return ClaimOutcome.Paid;
    }

    // ---- progress -----------------------------------------------------------

    /// <summary>
    /// Counts a finished match: matches and the mode's Season XP always (the same whatever the result), wins (and Draft wins),
    /// goals and clean sheets. <paramref name="won"/> is the result the match end settled on, not the score: a Squad Battles,
    /// Weekend League or Draft match level on goals is won or lost on penalties, and a player whose opponent quit or dropped
    /// (DNF_WIN) wins whatever the score. <paramref name="mode"/> is the paying mode whose completion reward sets the XP.
    /// </summary>
    public async ValueTask RecordMatchEndAsync(IUnitOfWork uow, string endReason, JsonObject body, bool won, bool draft, string? mode = null)
    {
        if (Rules.NoProgressEndReasons.Contains(draft ? endReason : endReason.ToUpperInvariant()))
        {
            return;
        }
        var (mine, theirs) = Goals(body);
        var conditional = await new ObjectiveMatchProgress(Game).CountsAsync(uow, body, mode, won, Rules);
        await BumpAsync(uow, mode is not null && Game.Rules.Seasons.MatchCompletionRewards.TryGetValue(mode, out var reward) ? reward.Xp : 0,
            new[] { ("matches", 1L), ("wins", won ? 1L : 0), ("draftWins", won && draft ? 1L : 0),
                ("goals", Math.Clamp(mine, 0, Rules.MaxGoalsPerMatch)), ("scoringMatches", mine > 0 ? 1L : 0), ("cleanSheets", theirs == 0 ? 1L : 0) }.Concat(conditional).ToArray());
    }

    /// <summary>Counts a match the score decides (a win is more goals).</summary>
    public ValueTask RecordMatchAsync(IUnitOfWork uow, long myGoals, long opponentGoals, bool draft) =>
        RecordMatchAsync(uow, myGoals, opponentGoals, myGoals > opponentGoals, draft);

    public ValueTask RecordMatchAsync(IUnitOfWork uow, long myGoals, long opponentGoals, bool won, bool draft, string? mode = null) =>
        BumpAsync(uow, mode is not null && Game.Rules.Seasons.MatchCompletionRewards.TryGetValue(mode, out var reward) ? reward.Xp : 0,
            ("matches", 1),
            ("wins", won ? 1 : 0),
            ("draftWins", won && draft ? 1 : 0),
            ("goals", Math.Clamp(myGoals, 0, Rules.MaxGoalsPerMatch)),
            ("scoringMatches", myGoals > 0 ? 1 : 0),
            ("cleanSheets", opponentGoals == 0 ? 1 : 0));

    /// <summary>Counts a first SBC completion for objectives; it earns no Season XP (migration 146).</summary>
    public ValueTask RecordSbcCompletedAsync(IUnitOfWork uow) => BumpAsync(uow, 0, ("sbcs", 1));

    /// <summary>Counts an opened pack for objectives; it earns no Season XP (migration 146).</summary>
    public ValueTask RecordPackOpenedAsync(IUnitOfWork uow) => BumpAsync(uow, 0, ("packs", 1));

    /// <summary>The goals of both sides from a match-end body's <c>myMatchStats</c> and <c>opponentMatchStats</c>.</summary>
    public static (long Mine, long Theirs) Goals(JsonObject body) =>
        (body.Obj("myMatchStats")?.Int("goals") ?? 0, body.Obj("opponentMatchStats")?.Int("goals") ?? 0);

    /// <summary>Adds to objective stats and Season Progress XP in one update.</summary>
    private async ValueTask BumpAsync(IUnitOfWork uow, long xp, params (string Stat, long Amount)[] stats)
    {
        // SaveObjectives replaces the snapshot, including claims. Lock before reading so a progress
        // update cannot erase a reward claim committed by another connection while it waits to save.
        await uow.LockClubAsync();
        var current = await uow.ObjectivesAsync(Rules);
        var updated = new Dictionary<string, long>(current.Stats, StringComparer.Ordinal);
        foreach (var (stat, amount) in stats.Where(entry => entry.Amount > 0))
        {
            updated[stat] = updated.GetValueOrDefault(stat) + amount;
        }
        var next = current with { Stats = updated, UserXp = current.UserXp + xp };
        await uow.SaveObjectivesAsync(next);
        if (Rules.LiveProgressUpdates && uow.ClubId == Game.ClubId)
        {
            QueueProgress(Groups(current), Groups(next), xp);
        }
        // One line per recorded event, so a "my objective did not move" report can be checked against what was counted.
        _log.LogInformation("SCMP progress club={Club} added={Added} xp=+{Xp} totals={Totals} userXp={UserXp}", uow.ClubId,
            string.Join(",", stats.Where(entry => entry.Amount > 0).Select(entry => $"{entry.Stat}+{entry.Amount}")), xp,
            string.Join(",", updated.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}")),
            current.UserXp + xp);
    }

    // ---- live progress -------------------------------------------------------

    /// <summary>
    /// Keeps the objectives a bump moved (progress or state) until a reply that FIFA reads them from takes them. FIFA loads the
    /// campaign once per FUT entry; retail then sent changes in <c>dynamicObjectivesUpdates</c> on action replies. Only the
    /// moved objectives go: CardsDLL's merge (0x392d00) queues an "objective completed" notice for every completed objective
    /// it is sent, so resending finished ones would repeat the notice.
    /// </summary>
    private void QueueProgress(JsonArray before, JsonArray after, long xp)
    {
        var previous = before.OfType<JsonObject>().ToDictionary(group => group.Int("groupId"));
        lock (_pendingLock)
        {
            foreach (var group in after.OfType<JsonObject>())
            {
                var id = group.Int("groupId");
                var was = (previous.GetValueOrDefault(id)?.Arr("objectives") ?? new JsonArray()).OfType<JsonObject>()
                    .ToDictionary(objective => objective.Int("objectiveId"));
                var moved = (group.Arr("objectives") ?? new JsonArray()).OfType<JsonObject>()
                    .Where(objective => !was.TryGetValue(objective.Int("objectiveId"), out var old)
                        || old.Int("currentProgress") != objective.Int("currentProgress") || old.Text("state") != objective.Text("state"))
                    .ToList();
                if (moved.Count == 0)
                {
                    continue;
                }
                // A later bump in the same reply window replaces an objective queued earlier.
                var objectives = (_pendingGroups.GetValueOrDefault(id)?.Arr("objectives") ?? new JsonArray()).OfType<JsonObject>()
                    .ToDictionary(objective => objective.Int("objectiveId"), objective => (JsonNode)objective.DeepClone());
                foreach (var objective in moved)
                {
                    objectives[objective.Int("objectiveId")] = objective.DeepClone();
                }
                var queued = (JsonObject)group.DeepClone();
                queued["objectives"] = new JsonArray(objectives.Values.ToArray());
                _pendingGroups[id] = queued;
            }
            _pendingXp += xp;
        }
    }

    /// <summary>
    /// The queued progress as FIFA's <c>dynamicObjectivesUpdates</c> (parser 0x32f970), or null when nothing moved; taking it
    /// clears the queue. <c>scmpCategoryProgressList</c> holds group documents read like the campaign's (0x334f90) and merged by
    /// group and objective id into the cached campaign (0x392c90); <c>autoRedeemedXp</c> adds to the cached Season XP and
    /// flags each level it crosses (0x392360).
    /// </summary>
    public JsonObject? TakeProgressUpdates()
    {
        lock (_pendingLock)
        {
            if (_pendingGroups.Count == 0 && _pendingXp == 0)
            {
                return null;
            }
            var updates = new JsonObject();
            if (_pendingGroups.Count > 0)
            {
                updates["scmpCategoryProgressList"] = new JsonArray(new JsonObject
                {
                    ["categoryId"] = Rules.Category.Id,
                    ["scmpGroupProgressList"] = new JsonArray(_pendingGroups.Values.Select(group => (JsonNode?)group).ToArray()),
                });
            }
            if (_pendingXp > 0)
            {
                updates["autoRedeemedXp"] = _pendingXp;
            }
            DiscardProgressUpdates();
            return updates;
        }
    }

    /// <summary>Drops the queued progress, as after a refused action whose transaction rolled back.</summary>
    public void DiscardProgressUpdates()
    {
        lock (_pendingLock)
        {
            _pendingGroups.Clear();
            _pendingXp = 0;
        }
    }

    /// <summary>Adds the queued progress to a reply FIFA reads it from (match end, pack open, SBC submit).</summary>
    public JsonNode? WithProgressUpdates(JsonNode? reply)
    {
        if (reply is JsonObject body && TakeProgressUpdates() is { } updates)
        {
            body["dynamicObjectivesUpdates"] = updates;
        }
        return reply;
    }

    // ---- documents ----------------------------------------------------------

    /// <summary>The live groups in live-list order (rules/objectives.json live): the welcome group, grind groups and open gifts.</summary>
    private JsonArray Groups(ObjectivesState state) =>
        new(Rules.LiveGroupIds.Select(id => (JsonNode?)(id == Welcome.Id ? WelcomeGroupDocument(state)
                : Rules.GrindGroups.FirstOrDefault(group => group.Id == id) is { } group ? GrindGroupDocument(group, state)
                : Rules.Gifts.FirstOrDefault(gift => gift.Id == id) is { } gift && gift.IsOpen(Game.NowSeconds) ? GiftGroupDocument(gift, state)
                : null))
            .Where(group => group is not null).ToArray());

    private (long Start, long End) CampaignWindow()
    {
        var season = Season;
        return (season.Start, season.End);
    }

    /// <summary>
    /// A Season Progress level. CardsDLL (levels parser 0x333640) reads <c>state</c> and <c>chosenOption</c> per level; the
    /// tile (0x195550) shows 0 as locked, 1 as "Claim Reward" and 2 as "Claimed" with the option whose optionId equals
    /// <c>chosenOption</c>. A level without a state (-1) gets 0 or 1 from the club's XP (0x198109), so a claimed level must be
    /// sent as 2 or it reads as claimable again after every reload.
    /// </summary>
    private JsonObject Level(SeasonProgressRules progress, long level, bool claimed, long chosen) => TemplateLibrary.Core.Object("objectives/level", new TemplateArgs
    {
        ["level"] = level,
        ["xp"] = level * progress.XpPerLevel,
        ["state"] = claimed ? LevelClaimedState : LevelStateFromXp,
        ["chosenOption"] = claimed ? chosen : LevelStateFromXp,
        // One option per reward the club can pick (rewardOptionList); a level without a choice has the one, option 0.
        ["options"] = new JsonArray(progress.RewardFor(level).Choices.Select((reward, option) => (JsonNode)TemplateLibrary.Core.Object(
            "objectives/level-option", new TemplateArgs { ["optionId"] = option, ["defaultOption"] = option == 0, ["awards"] = Awards(reward, RewardSources.Season) })).ToArray()),
    });

    /// <summary>CardsDLL's "unset" level state and chosen option: the client works the state out from XP itself.</summary>
    private const long LevelStateFromXp = -1;

    /// <summary>CardsDLL's claimed level state (FUT_SCMP_CLAIMED_REWARD).</summary>
    private const long LevelClaimedState = 2;

    /// <summary>Stadium card definitions are 6200000 + venue (migration 053).</summary>
    private const long StadiumCardBase = 6200000;

    /// <summary>
    /// A reward's tiles, in order: coins, each pack line, then its My Stadium item, stadium or slot. FIFA Points have no known
    /// FIFA 21 award tile, so they are paid but not shown.
    /// </summary>
    private JsonArray Awards(ObjectiveReward reward, string source)
    {
        var awards = new JsonArray();
        if (reward.Coins is { } coins and > 0) awards.Add(_fut.Awards.ObjectiveCoins(coins));
        foreach (var line in reward.PackLines)
        {
            var award = _fut.Awards.Pack(line.PackId, line.Count);
            var untradeable = line.Untradeable || Game.Rules.Tradeability.Untradeable(source, line.PackId);
            award["untradeable"] = untradeable;
            award["isUntradeable"] = untradeable;
            awards.Add(award);
        }
        foreach (var player in reward.Players)
        {
            var item = _fut.Items.MarkReward(_fut.Items.BuildPlayer(player.ResourceId, 0, "unassigned"), source,
                player.Untradeable || Game.Rules.Tradeability.Untradeable(source));
            var award = _fut.Awards.Item(item, Game.Players.Find(player.ResourceId)?.Text("name") ?? "Player");
            award["untradeable"] = item.Bool("untradeable");
            awards.Add(award);
        }
        // My Stadium rewards show as an item tile: a card of the pack's type, of the unlocked slot's type, or the stadium.
        if (reward.StadiumTier is { } tier)
        {
            awards.Add(_fut.Awards.Item(TierStadiumItem(tier), Game.Rules.Club.Venues.FutStadiumName, Store.RewardAwardService.AwardLabelStadiums));
        }
        else if (reward.Vanity is not null || reward.UnlockSlots.Count > 0)
        {
            var subtype = reward.Vanity?.Subtypes.FirstOrDefault()
                ?? reward.UnlockSlots.Select(id => Game.Rules.Club.SlotById(id)).OfType<MyStadiumSlot>().Select(slot => (long?)slot.CardSubType).FirstOrDefault()
                ?? 0;
            var sample = _fut.Vanity.SampleItem(subtype, reward.Vanity?.Assets.Count > 0 ? reward.Vanity.Assets[0] : null) ?? new JsonObject();
            awards.Add(_fut.Awards.Item(sample, reward.Vanity?.Name ?? "My Stadium slot"));
        }
        return awards;
    }

    /// <summary>
    /// The tier's own stadium card (6200000 + venue, StadiumName_&lt;venue&gt;): the shared default card 6200058 showed
    /// every tier as the same stadium.
    /// </summary>
    private JsonObject TierStadiumItem(long tier)
    {
        var card = Game.Rules.Club.Venues.FutStadiumCards.GetValueOrDefault(tier);
        return ItemFactory.ClubCosmeticItem("stadium", StadiumCardBase + tier, Game.Established, StadiumCardBase + tier, tier, "free",
            "unassigned", $"StadiumName_{tier}", card?.Rating ?? 83, card?.Rareflag ?? 1, Game.Rules.Club.DefaultStadiumCapacity)!;
    }

    /// <summary>Pays every part of a reward: coins, FIFA Points, each pack line, and its My Stadium item, stadium tier or slot.</summary>
    private async ValueTask GrantAsync(IUnitOfWork uow, ObjectiveReward reward, string source)
    {
        // unlockSlots need no write: a slot is open once the club has claimed the reward that names it (VanityService).
        if (reward.Coins is { } coins and > 0)
        {
            await uow.AddCreditsToAsync(uow.ClubId, coins);
        }
        if (reward.FifaPoints > 0)
        {
            await uow.AddFifaPointsAsync(reward.FifaPoints);
        }
        foreach (var line in reward.PackLines)
        {
            // Untradeable when the line says so, or when the grant source does (rules/tradeability.json).
            for (var copy = 0; copy < line.Count; copy++)
            {
                await uow.AddUnopenedPackAsync(line.PackId, source, untradeable: line.Untradeable || Game.Rules.Tradeability.Untradeable(source, line.PackId));
            }
        }
        foreach (var player in reward.Players)
        {
            var item = _fut.Items.BuildPlayer(player.ResourceId, await uow.NextItemIdAsync(), "unassigned");
            _fut.Items.MarkReward(item, source, player.Untradeable || Game.Rules.Tradeability.Untradeable(source));
            await uow.SaveItemAsync(item.Int("id"), item);
        }
        if (reward.Vanity is { } vanity)
        {
            var granted = await _fut.Vanity.GrantAsync(uow, vanity.Subtypes, vanity.Count, vanity.Assets);
            _log.LogInformation("SCMP My Stadium pack {Pack}: {Count} item(s)", vanity.Name, granted.Count);
        }
        if (reward.StadiumTier is { } tier)
        {
            // Retail moved the club into its new stadium tier; the club can still choose another venue.
            await uow.SetSelectedStadiumIdAsync(tier);
            // Retail hands the tier's card over as an item: FIFA opens the items screen on the claim and the pile was
            // empty (local 10-03). The card joins the club as an owned stadium card when stored.
            var stadium = TierStadiumItem(tier);
            stadium["id"] = await uow.ReserveItemIdsAsync(1);
            stadium["untradeable"] = true;
            await uow.SaveItemsAsync(new[] { stadium });
            _log.LogInformation("SCMP stadium tier {Tier}: card {Card} granted to unassigned", tier, StadiumCardBase + tier);
        }
    }

    /// <summary>
    /// The welcome group, served completed: an IN_PROGRESS objective without a condition block makes
    /// the live evaluator (run on squad edits) crash the game.
    /// </summary>
    private JsonObject WelcomeGroupDocument(ObjectivesState state)
    {
        var claimed = state.RewardClaimed;
        // Earned by playing (rules/objectives.json welcomeGroup stat and target): tracked like a grind objective until then.
        if (!claimed && Welcome.Stat is { } stat && state.Progress(Welcome.Id, stat) < Welcome.Target)
        {
            var tracked = TemplateLibrary.Core.Object("objectives/grind-objective", new TemplateArgs
            {
                ["id"] = Welcome.Objective.Id,
                ["name"] = Welcome.Objective.Name,
                ["description"] = Welcome.Objective.Description,
                ["target"] = Welcome.Target,
                ["progress"] = state.Progress(Welcome.Id, stat),
                ["state"] = "IN_PROGRESS",
                ["awards"] = new JsonArray(WelcomeAward()),
            });
            return GroupDocument(Welcome.Id, Welcome.Title, Welcome.SubTitle, Rules.GroupStates.InProgress, priority: 1, new JsonArray(WelcomeAward()),
                new List<JsonObject> { tracked }, completed: 0);
        }
        var objective = TemplateLibrary.Core.Object("objectives/objective", new TemplateArgs
        {
            ["id"] = Welcome.Objective.Id,
            ["name"] = Welcome.Objective.Name,
            ["description"] = Welcome.Objective.Description,
            ["state"] = claimed ? "REDEEMED" : "COMPLETED",
            ["awards"] = new JsonArray(WelcomeAward()),
        });
        return GroupDocument(Welcome.Id, Welcome.Title, Welcome.SubTitle,
            claimed ? Rules.GroupStates.Rewarded : Rules.GroupStates.Completed, priority: 1, new JsonArray(WelcomeAward()), new List<JsonObject> { objective }, completed: 1);
    }

    /// <summary>
    /// An open gift. One the club has yet to earn is a tracked objective, like a grind one (never IN_PROGRESS without a
    /// condition block); otherwise it is served completed like the welcome group.
    /// </summary>
    private JsonObject GiftGroupDocument(ObjectiveGift gift, ObjectivesState state)
    {
        var claimed = state.ClaimedGroups.Contains(gift.Id);
        if (!claimed && gift.Stat is { } stat && state.Progress(gift.Id, stat) < gift.Target)
        {
            var tracked = GrindObjectiveDocument(gift.Id, new GrindObjective
            {
                Id = gift.Objective.Id, Name = gift.Objective.Name, Stat = stat, Target = gift.Target, Award = gift.Award, Xp = 0,
            }, state);
            return GroupDocument(gift.Id, gift.Title, gift.SubTitle, Rules.GroupStates.InProgress, gift.Id, Awards(gift.Award, RewardSources.Gift),
                new List<JsonObject> { tracked }, completed: 0);
        }
        var objective = TemplateLibrary.Core.Object("objectives/objective", new TemplateArgs
        {
            ["id"] = gift.Objective.Id,
            ["name"] = gift.Objective.Name,
            ["description"] = gift.Objective.Description,
            ["state"] = claimed ? "REDEEMED" : "COMPLETED",
            ["awards"] = Awards(gift.Award, RewardSources.Gift),
        });
        return GroupDocument(gift.Id, gift.Title, gift.SubTitle, claimed ? Rules.GroupStates.Rewarded : Rules.GroupStates.Completed,
            gift.Id, Awards(gift.Award, RewardSources.Gift), new List<JsonObject> { objective }, completed: 1);
    }

    private JsonObject GrindGroupDocument(GrindGroup group, ObjectivesState state)
    {
        var rows = group.Objectives.Select(objective => GrindObjectiveDocument(group.Id, objective, state)).ToList();
        var redeemed = rows.Count(row => row.Text("state") == "REDEEMED");
        var completed = rows.Count(row => row.Text("state") is "COMPLETED" or "REDEEMED");
        var groupState = state.ClaimedGroups.Contains(group.Id) ? Rules.GroupStates.Rewarded
            : redeemed == rows.Count ? Rules.GroupStates.Completed
            : Rules.GroupStates.InProgress;
        // Listed in live-list order: the list position is the group's priority.
        var priority = Rules.LiveGroupIds.ToList().IndexOf(group.Id) + 1;
        return GroupDocument(group.Id, group.Title, group.SubTitle, groupState, priority > 0 ? priority : group.Id, Awards(group.Award, RewardSources.Objective), rows, completed);
    }

    private JsonObject GroupDocument(long id, string title, string subTitle, long state, long priority, JsonArray awards, IReadOnlyCollection<JsonObject> objectives, long completed)
    {
        var (start, end) = CampaignWindow();
        return TemplateLibrary.Core.Object("objectives/group", new TemplateArgs
        {
            ["id"] = id,
            ["groupType"] = Rules.GroupType,
            ["state"] = state,
            ["priority"] = priority,
            ["title"] = title,
            ["subTitle"] = subTitle,
            ["start"] = start,
            ["end"] = end,
            ["objectiveCount"] = objectives.Count,
            ["completedCount"] = completed,
            ["awards"] = awards,
            ["objectives"] = new JsonArray(objectives.Select(objective => (JsonNode?)objective).ToArray()),
        });
    }

    /// <summary>
    /// A tracked objective. CardsDLL takes the goal from <c>multiplier</c>. Its rule can never be met on
    /// the client (chemistry tops out at 100), so progress stays server-driven while the squad-edit
    /// evaluator, which walks every IN_PROGRESS objective's rules, still finds one.
    /// </summary>
    private JsonObject GrindObjectiveDocument(long groupId, GrindObjective objective, ObjectivesState state)
    {
        var progress = Math.Min(objective.Target, state.Progress(groupId, objective.Counter));
        var text = (objective.Description ?? Rules.StatLabels[objective.Stat]).Replace("{n}", objective.Target.ToString("N0", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return TemplateLibrary.Core.Object("objectives/grind-objective", new TemplateArgs
        {
            ["id"] = objective.Id,
            ["name"] = objective.Name,
            ["description"] = text,
            ["target"] = objective.Target,
            ["progress"] = progress,
            ["state"] = state.ClaimedObjectives.Contains(objective.Id) ? "REDEEMED" : progress >= objective.Target ? "COMPLETED" : "IN_PROGRESS",
            ["awards"] = Awards(objective.Award, RewardSources.Objective),
        });
    }

    // ---- claims ---------------------------------------------------------------

    /// <summary>Pays a completed grind objective of <paramref name="group"/> once, with its XP.</summary>
    private async ValueTask<ClaimOutcome> ClaimObjectiveAsync(IUnitOfWork uow, GrindGroup group, long objectiveId, JsonArray awards)
    {
        if (group.Objectives.FirstOrDefault(candidate => candidate.Id == objectiveId) is not { } objective)
        {
            return ClaimOutcome.Unknown;
        }
        var state = await uow.ObjectivesAsync(Rules);
        if (!Rules.IsLive(group.Id)) return ClaimOutcome.Unknown;
        if (state.ClaimedObjectives.Contains(objectiveId))
        {
            return ClaimOutcome.AlreadyClaimed;
        }
        if (state.Progress(group.Id, objective.Counter) < objective.Target)
        {
            return ClaimOutcome.NotEarned;
        }
        await GrantAsync(uow, objective.Award, RewardSources.Objective);
        await uow.SaveObjectivesAsync(state with
        {
            ClaimedObjectives = state.ClaimedObjectives.Append(objectiveId).Order().ToList(),
            UserXp = state.UserXp + objective.Xp,
        });
        foreach (var award in Awards(objective.Award, RewardSources.Objective)) awards.Add(award?.DeepClone());
        _log.LogInformation("SCMP objective {Objective} claimed, +{Xp} XP", objectiveId, objective.Xp);
        return ClaimOutcome.Paid;
    }

    /// <summary>Pays a gift once, and only while its window is open (a closed gift is no longer offered).</summary>
    private async ValueTask<ClaimOutcome> ClaimGiftAsync(IUnitOfWork uow, ObjectiveGift gift, JsonArray awards)
    {
        var state = await uow.ObjectivesAsync(Rules);
        if (!Rules.IsLive(gift.Id)) return ClaimOutcome.Unknown;
        if (state.ClaimedGroups.Contains(gift.Id))
        {
            return ClaimOutcome.AlreadyClaimed;
        }
        if (!gift.IsOpen(Game.NowSeconds))
        {
            return ClaimOutcome.Unknown;
        }
        if (gift.Stat is { } stat && state.Progress(gift.Id, stat) < gift.Target)
        {
            return ClaimOutcome.NotEarned;
        }
        await GrantAsync(uow, gift.Award, RewardSources.Gift);
        await uow.SaveObjectivesAsync(state with { ClaimedGroups = state.ClaimedGroups.Append(gift.Id).Order().ToList() });
        foreach (var award in Awards(gift.Award, RewardSources.Gift)) awards.Add(award?.DeepClone());
        _log.LogInformation("SCMP gift {Gift} claimed", gift.Id);
        return ClaimOutcome.Paid;
    }

    /// <summary>Pays a grind group's award once every objective in it has been claimed.</summary>
    private async ValueTask<ClaimOutcome> ClaimGroupAsync(IUnitOfWork uow, GrindGroup group, JsonArray awards)
    {
        var state = await uow.ObjectivesAsync(Rules);
        if (!Rules.IsLive(group.Id)) return ClaimOutcome.Unknown;
        if (state.ClaimedGroups.Contains(group.Id))
        {
            return ClaimOutcome.AlreadyClaimed;
        }
        if (group.Objectives.Any(objective => !state.ClaimedObjectives.Contains(objective.Id)))
        {
            return ClaimOutcome.NotEarned;
        }
        await GrantAsync(uow, group.Award, RewardSources.Objective);
        await uow.SaveObjectivesAsync(state with { ClaimedGroups = state.ClaimedGroups.Append(group.Id).Order().ToList(), UserXp = state.UserXp + group.Xp });
        foreach (var award in Awards(group.Award, RewardSources.Objective)) awards.Add(award?.DeepClone());
        _log.LogInformation("SCMP group {Group} claimed", group.Id);
        return ClaimOutcome.Paid;
    }
}
