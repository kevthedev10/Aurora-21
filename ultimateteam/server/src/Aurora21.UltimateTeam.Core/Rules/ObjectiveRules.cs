using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

public sealed class ObjectiveCampaignRules
{
    public required long Id { get; set; }
    public required string Name { get; set; }
    public required long StartedDaysAgo { get; set; }
    public required long EndsInDays { get; set; }
}

public sealed class ObjectiveCategoryRules
{
    public required long Id { get; set; }
    public required string Name { get; set; }
}

public sealed class ObjectiveDefinition
{
    public required long Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
}

public sealed class WelcomeGroupRules
{
    public required long Id { get; set; }
    public required string Title { get; set; }
    public required string SubTitle { get; set; }
    public required ObjectiveDefinition Objective { get; set; }
    public required long RewardPackId { get; set; }
    public required int RewardPackCount { get; set; }

    /// <summary>Whether the reward packs are delivered untradeable.</summary>
    public bool RewardUntradeable { get; set; }
    public required long XpAward { get; set; }

    /// <summary>The objective stat the club must reach before the welcome reward pays (e.g. matches); null serves it completed.</summary>
    public string? Stat { get; set; }
    public long Target { get; set; }
}

/// <summary>FIFA 21's numeric objective group states (objectives use state names instead).</summary>
public sealed class GroupStateRules
{
    public required long InProgress { get; set; }
    public required long Completed { get; set; }
    public required long Rewarded { get; set; }
}

/// <summary>
/// A pack of My Stadium items (retail's Stadium Development "Tifos Pack", "Club Anthem Pack", ...): <see cref="Count"/>
/// items the club does not own yet, drawn from the catalogue's <see cref="Subtypes"/>.
/// </summary>
public sealed class VanityPackReward
{
    public required string Name { get; set; }
    public required List<long> Subtypes { get; set; }
    public long Count { get; set; } = 1;

    /// <summary>
    /// Fixed items (asset ids of <see cref="Subtypes"/>) the reward gives instead of a random draw, as retail's milestone
    /// trophies were named ones; the tile shows the first.
    /// </summary>
    public List<long> Assets { get; set; } = new();
}

/// <summary>
/// One reward: coins, a pack delivered to My Packs (untradeable when it says so), a pack of My Stadium items, or a FUT
/// Stadium tier (<see cref="StadiumTier"/>, a rules/club.json venues.futStadiums venue). <see cref="UnlockSlots"/>
/// (My Stadium slot ids) may come with any of them, or alone: retail's Stadium Development objectives unlock a slot.
/// </summary>
public sealed class ObjectiveReward : IJsonOnDeserialized
{
    /// <summary>An explicitly rewardless objective; missing source rewards must not be invented.</summary>
    public bool None { get; set; }
    public List<ObjectivePlayerReward> Players { get; set; } = new();
    public long? Coins { get; set; }

    /// <summary>FIFA Points (owner, 2026-10-04). FIFA 21 has no known award tile for them: they are paid, not shown.</summary>
    public long FifaPoints { get; set; }

    /// <summary>One pack, the older single-pack form (<see cref="Untradeable"/> is its flag); the panel writes <see cref="Packs"/>.</summary>
    public long? Pack { get; set; }
    public bool Untradeable { get; set; }

    /// <summary>Packs and player picks, each with a count and its own tradeability (owner, 2026-10-04: a full reward).</summary>
    public List<ObjectivePackLine> Packs { get; set; } = new();
    public VanityPackReward? Vanity { get; set; }
    public long? StadiumTier { get; set; }
    public List<long> UnlockSlots { get; set; } = new();

    /// <summary>
    /// A Season Pass level's choices (owner, 2026-10-04): the club picks one of these rewards (FIFA's rewardOptionList, the
    /// claim names the option). A reward with options pays nothing itself; only levels may have them.
    /// </summary>
    public List<ObjectiveReward> Options { get; set; } = new();

    public const int MaxOptions = 5;

    /// <summary>The rewards a club can pick from: its options, or the reward itself.</summary>
    [JsonIgnore]
    public IReadOnlyList<ObjectiveReward> Choices => Options.Count > 0 ? Options : [this];

    /// <summary>Every pack line, the single-pack form included.</summary>
    [JsonIgnore]
    public IEnumerable<ObjectivePackLine> PackLines => (Pack is { } pack ? [new ObjectivePackLine { PackId = pack, Untradeable = Untradeable }] : Array.Empty<ObjectivePackLine>())
        .Concat(Packs);

    public void OnDeserialized()
    {
        var pays = (Coins ?? 0) != 0 || FifaPoints != 0 || Pack is not null || Packs.Count > 0 || Players.Count > 0
            || Vanity is not null || StadiumTier is not null || UnlockSlots.Count > 0;
        if (None)
        {
            if (pays || Options.Count > 0) throw new InvalidDataException("a reward marked none cannot also pay anything");
            return;
        }
        if (Players.Any(player => player.ResourceId < 1)) throw new InvalidDataException("a player reward names a catalogue resource id");
        if (Options.Count > 0)
        {
            if (Options.Count is < 2 or > MaxOptions || Options.Any(option => option.Options.Count > 0))
            {
                throw new InvalidDataException($"a reward with choices offers 2 to {MaxOptions} rewards, none with choices of its own");
            }
            if (pays)
            {
                throw new InvalidDataException("a reward with choices pays only the chosen one: put everything in its options");
            }
            return;
        }
        if (Coins is < 0 || FifaPoints < 0 || Packs.Any(line => line.PackId < 1 || line.Count is < 1 or > ObjectivePackLine.MaxCount))
        {
            throw new InvalidDataException($"a reward's coins and FIFA Points are 0 or more, and each pack line names a pack and 1 to {ObjectivePackLine.MaxCount} copies");
        }
        if (!pays)
        {
            throw new InvalidDataException("a reward pays something: coins, FIFA Points, packs, a My Stadium item, a stadium tier or slot unlocks");
        }
        if (Untradeable && Pack is null)
        {
            throw new InvalidDataException("only a pack reward can be untradeable");
        }
    }
}

public sealed class ObjectivePlayerReward
{
    public required long ResourceId { get; set; }
    public bool Untradeable { get; set; } = true;
}

/// <summary>Server-side match requirements; identity and league/nation/club come from owned catalogue items.</summary>
public class ObjectivePlayerRestriction
{
    public long? LeagueId { get; set; }
    public long? Nation { get; set; }

    /// <summary>The players' club (the catalogue's teamid).</summary>
    public long? ClubId { get; set; }

    [JsonIgnore]
    public bool IsEmpty => LeagueId is null && Nation is null && ClubId is null;
}

public sealed class ObjectiveStartingSquad : ObjectivePlayerRestriction
{
    public int MinimumCount { get; set; }
    public int? ExactCount { get; set; }
}

public sealed class ObjectiveMatchRequirements
{
    /// <summary>The stats a match-conditioned objective can track.</summary>
    public static readonly IReadOnlySet<string> Stats = new HashSet<string>(StringComparer.Ordinal)
        { "matches", "wins", "goals", "assists", "scoringMatches", "cleanSheets" };

    /// <summary>The stats a player restriction applies to: they are counted from what each player did.</summary>
    public static readonly IReadOnlySet<string> PlayerStats = new HashSet<string>(StringComparer.Ordinal) { "goals", "assists", "scoringMatches" };

    public required List<string> Modes { get; set; }
    public ObjectiveStartingSquad? StartingSquad { get; set; }
    public ObjectivePlayerRestriction? Player { get; set; }
    public string? GoalType { get; set; }
    public string? AssistType { get; set; }
}

/// <summary>
/// The native event adapter's reviewed mapping. Null means unverified, never an ordinary-goal fallback.
/// Numeric flags and player_id semantics must be established for the supported client build before publishing.
/// </summary>
public sealed class ObjectiveScoringEvents
{
    public string? PlayerIdKind { get; set; }
    public long? Finesse { get; set; }
    public long? Cross { get; set; }
    public long? ThroughBall { get; set; }
    public long? Flag(string? type) => type switch { "finesse" => Finesse, "cross" => Cross, "through_ball" => ThroughBall, _ => null };
}

/// <summary>A pack or player pick an objective reward pays: how many, and whether the club receives them untradeable.</summary>
public sealed class ObjectivePackLine
{
    public const long MaxCount = 100;

    public required long PackId { get; set; }
    public long Count { get; set; } = 1;
    public bool Untradeable { get; set; }
}

/// <summary>A group on the live objectives list: the game shows it while it is switched on (a gift also needs its window open).</summary>
public sealed class ObjectiveLiveEntry
{
    public required long Group { get; set; }
    public required bool Enabled { get; set; }
}

/// <summary>
/// A one-off gift: a group served while its window is open, whose one objective pays the award once per club. With a
/// <see cref="Stat"/> the club earns it (its stat reaches <see cref="Target"/>) before it can claim it; without one it is
/// served already completed. The window bounds are inclusive; a missing bound leaves that side open.
/// </summary>
public sealed class ObjectiveGift
{
    public required long Id { get; set; }
    public required string Title { get; set; }
    public required string SubTitle { get; set; }
    public required ObjectiveDefinition Objective { get; set; }
    public required ObjectiveReward Award { get; set; }
    public DateTimeOffset? AvailableFrom { get; set; }
    public DateTimeOffset? AvailableUntil { get; set; }

    /// <summary>The objective stat the club must reach first (e.g. matches); null serves the gift completed.</summary>
    public string? Stat { get; set; }
    public long Target { get; set; }

    public bool IsOpen(long nowSeconds) =>
        (AvailableFrom is not { } from || nowSeconds >= from.ToUnixTimeSeconds())
        && (AvailableUntil is not { } until || nowSeconds <= until.ToUnixTimeSeconds());
}

/// <summary>A tracked objective: its stat must reach target; claiming pays the award and the XP.</summary>
public sealed class GrindObjective
{
    public required long Id { get; set; }
    public required string Name { get; set; }
    public required string Stat { get; set; }
    public required long Target { get; set; }
    public required ObjectiveReward Award { get; set; }
    public required long Xp { get; set; }
    public string? Description { get; set; }
    public ObjectiveMatchRequirements? Requirements { get; set; }
    [JsonIgnore]
    public string Counter => Requirements is null ? Stat : $"objective:{Id}";
}

public sealed class GrindGroup
{
    public string? SourceKey { get; set; }
    public required long Id { get; set; }
    public required string Title { get; set; }
    public required string SubTitle { get; set; }
    public required ObjectiveReward Award { get; set; }
    public required List<GrindObjective> Objectives { get; set; }
    public long Xp { get; set; }
}

public class SeasonProgressRules : IJsonOnDeserialized
{
    public required int LevelCount { get; set; }
    public required long XpPerLevel { get; set; }
    public required List<ObjectiveReward> LevelRewards { get; set; }

    /// <summary>The last level served and claimable; null serves every level.</summary>
    public int? RewardLevelCap { get; set; }

    /// <summary>The levels the campaign serves and pays: 1 up to this.</summary>
    public int ServedLevels => Math.Min(LevelCount, RewardLevelCap ?? LevelCount);

    public ObjectiveReward RewardFor(long level) => LevelRewards[(int)level - 1];

    public virtual void OnDeserialized()
    {
        if (LevelCount < 1 || XpPerLevel < 1)
        {
            throw new InvalidDataException("a Season Pass needs at least 1 level and at least 1 XP per level");
        }
        if (LevelRewards.Count != LevelCount)
        {
            throw new InvalidDataException($"seasonProgress lists {LevelRewards.Count} level rewards for {LevelCount} levels");
        }
        if (RewardLevelCap is < 1)
        {
            throw new InvalidDataException("seasonProgress rewardLevelCap must be at least 1, or null for no cap");
        }
    }
}

/// <summary>
/// A custom season (owner, 2026-10-07): a named Season Pass with its own dates, levels, XP curve and rewards. While it runs
/// clubs earn and claim its XP and levels; a club's progress in every other season is kept (migration 166), so a season that
/// returns never pays a level twice. Outside every season the default one (campaign and seasonProgress) runs.
/// </summary>
public sealed class ObjectiveSeasonRules : SeasonProgressRules
{
    public required long Id { get; set; }
    public required string Name { get; set; }

    /// <summary>When the season starts (inclusive), ISO 8601 with a UTC offset.</summary>
    public required DateTimeOffset StartsAt { get; set; }

    /// <summary>When it ends (exclusive); FIFA counts down to it.</summary>
    public required DateTimeOffset EndsAt { get; set; }

    public bool Enabled { get; set; } = true;

    public bool IsRunning(long nowSeconds) => Enabled && nowSeconds >= StartsAt.ToUnixTimeSeconds() && nowSeconds < EndsAt.ToUnixTimeSeconds();

    public override void OnDeserialized()
    {
        base.OnDeserialized();
        if (Id < 1 || string.IsNullOrWhiteSpace(Name) || EndsAt <= StartsAt)
        {
            throw new InvalidDataException($"season {Id} needs a positive id, a name and an end after its start");
        }
    }
}

/// <summary>The season in force: its id (the key clubs' Season XP and level claims are kept under), name, window and levels.</summary>
public sealed record ActiveSeason(long Id, string Name, long Start, long End, SeasonProgressRules Progress, bool IsDefault);

/// <summary>rules/objectives.json: the FUT Objectives campaign.</summary>
public sealed class ObjectiveRules : IJsonOnDeserialized
{
    public ObjectiveScoringEvents ScoringEvents { get; set; } = new();
    public required ObjectiveCampaignRules Campaign { get; set; }
    public required ObjectiveCategoryRules Category { get; set; }
    public required long GroupType { get; set; }
    public required GroupStateRules GroupStates { get; set; }
    public required WelcomeGroupRules WelcomeGroup { get; set; }
    public required HashSet<string> NoProgressEndReasons { get; set; }

    /// <summary>The most goals one match end counts for objectives: FIFA reports them, so a crafted end could claim any number.</summary>
    public long MaxGoalsPerMatch { get => _maxGoalsPerMatch; set => _maxGoalsPerMatch = Math.Max(0, value); }

    private long _maxGoalsPerMatch = 20;

    /// <summary>
    /// Whether match end, pack open and SBC submit replies carry the objectives that moved (dynamicObjectivesUpdates), so
    /// FIFA updates its cached campaign without the player leaving FUT. Off, progress shows on the next FUT entry only.
    /// </summary>
    public bool LiveProgressUpdates { get; set; } = true;
    public required Dictionary<string, string> StatLabels { get; set; }
    public required List<GrindGroup> GrindGroups { get; set; }
    public List<ObjectiveGift> Gifts { get; set; } = new();
    public required SeasonProgressRules SeasonProgress { get; set; }

    /// <summary>Custom seasons by date (owner, 2026-10-07); the default season (campaign, seasonProgress) runs outside them.</summary>
    public List<ObjectiveSeasonRules> Seasons { get; set; } = new();

    private const long DaySeconds = 86400;

    /// <summary>
    /// The season running at <paramref name="nowSeconds"/>: the custom season whose window holds it, else the default season,
    /// whose window is rolling (it started campaign.startedDaysAgo days ago and ends in campaign.endsInDays days).
    /// </summary>
    public ActiveSeason SeasonAt(long nowSeconds) => Seasons.FirstOrDefault(season => season.IsRunning(nowSeconds)) is { } custom
        ? new ActiveSeason(custom.Id, custom.Name, custom.StartsAt.ToUnixTimeSeconds(), custom.EndsAt.ToUnixTimeSeconds(), custom, false)
        : new ActiveSeason(Campaign.Id, Campaign.Name, nowSeconds - Campaign.StartedDaysAgo * DaySeconds,
            nowSeconds + Campaign.EndsInDays * DaySeconds, SeasonProgress, true);

    /// <summary>Every Season Pass reward: the default season's and each custom season's levels.</summary>
    [JsonIgnore]
    private IEnumerable<ObjectiveReward> SeasonRewards => SeasonProgress.LevelRewards.Concat(Seasons.SelectMany(season => season.LevelRewards));

    /// <summary>
    /// The live objectives list (owner, 2026-10-04), in the order FIFA lists them: every group the game shows, each switched on
    /// or off. The welcome group, the grind groups and the gifts are the catalogue; a group not listed stays in it unseen. Null
    /// (rules from before the list) lists every group, switched on, welcome first.
    /// </summary>
    public List<ObjectiveLiveEntry>? Live { get; set; }

    /// <summary>Every group id in the catalogue: the welcome group, the grind groups, then the gifts.</summary>
    [JsonIgnore]
    public IEnumerable<long> GroupIds => GrindGroups.Select(group => group.Id).Prepend(WelcomeGroup.Id).Concat(Gifts.Select(gift => gift.Id));

    /// <summary>The ids of the groups the game shows, in list order.</summary>
    [JsonIgnore]
    public IReadOnlyList<long> LiveGroupIds => Live?.Where(entry => entry.Enabled).Select(entry => entry.Group).ToList() ?? GroupIds.ToList();

    public bool IsLive(long groupId) => LiveGroupIds.Contains(groupId);

    /// <summary>Why the live list is wrong (it names a group the catalogue does not have), or null.</summary>
    public string? LiveProblem()
    {
        if (Live?.FirstOrDefault(entry => !GroupIds.Contains(entry.Group)) is { } missing)
            return $"The live objectives list names group {missing.Group}, which is not in the catalogue.";
        var unsupported = GrindGroups.Where(group => IsLive(group.Id)).SelectMany(group => group.Objectives)
            .FirstOrDefault(objective => objective.Requirements is { } req && (req.GoalType ?? req.AssistType) is { } type
                && (ScoringEvents.Flag(type) is null || ScoringEvents.PlayerIdKind is null));
        return unsupported is null ? null : $"Objective {unsupported.Id} requires a verified native scoring-event mapping before it can go live.";
    }

    /// <summary>Every pack an objective, group, gift or Season Pass level pays (the content check refuses a missing one).</summary>
    [JsonIgnore]
    public IEnumerable<long> PackIds => GrindGroups.SelectMany(group => group.Objectives.Select(objective => objective.Award).Append(group.Award))
        .Concat(Gifts.Select(gift => gift.Award)).Concat(SeasonRewards.SelectMany(reward => reward.Choices))
        .SelectMany(reward => reward.PackLines.Select(line => line.PackId)).Append(WelcomeGroup.RewardPackId).Distinct();

    [JsonIgnore]
    public IEnumerable<long> PlayerIds => GrindGroups.SelectMany(group => group.Objectives.Select(objective => objective.Award).Append(group.Award))
        .Concat(Gifts.Select(gift => gift.Award)).Concat(SeasonRewards.SelectMany(reward => reward.Choices))
        .SelectMany(reward => reward.Players.Select(player => player.ResourceId)).Distinct();

    [JsonIgnore]
    public IReadOnlyDictionary<long, (GrindGroup Group, GrindObjective Objective)> GrindObjectivesById { get; private set; } =
        new Dictionary<long, (GrindGroup, GrindObjective)>();

    public void OnDeserialized()
    {
        if (ScoringEvents.PlayerIdKind is not (null or "assetId" or "itemId")
            || new[] { ScoringEvents.Finesse, ScoringEvents.Cross, ScoringEvents.ThroughBall }.Any(flag => flag < 0))
            throw new InvalidDataException("scoringEvents needs nonnegative flags and playerIdKind assetId or itemId");
        var byId = new Dictionary<long, (GrindGroup, GrindObjective)>();
        foreach (var group in GrindGroups)
        {
            foreach (var objective in group.Objectives)
            {
                if (objective.Target < 1 || objective.Xp < 0 || group.Xp < 0) throw new InvalidDataException("objective targets are positive and XP is nonnegative");
                if (objective.Requirements is { } req)
                {
                    if (req.Modes.Count == 0 || req.Modes.Any(mode => !RewardModes.All.Contains(mode))
                        || !ObjectiveMatchRequirements.Stats.Contains(objective.Stat)
                        || req.Player is not null && !ObjectiveMatchRequirements.PlayerStats.Contains(objective.Stat)
                        || objective.Stat == "scoringMatches" && (req.GoalType ?? req.AssistType) is not null
                        || req.GoalType is not (null or "finesse") || req.AssistType is not (null or "cross" or "through_ball")
                        || req.GoalType is not null && objective.Stat != "goals" || req.AssistType is not null && objective.Stat != "assists"
                        || req.StartingSquad is { } squad && (squad.MinimumCount is < 0 or > 11 || squad.ExactCount is < 1 or > 11))
                        throw new InvalidDataException($"objective {objective.Id} has invalid match requirements");
                    foreach (var restriction in new[] { req.Player, req.StartingSquad })
                        if (restriction is not null && (restriction.LeagueId is <= 0 || restriction.Nation is <= 0 || restriction.ClubId is <= 0
                            || restriction.IsEmpty))
                            throw new InvalidDataException($"objective {objective.Id} needs a league, nation or club restriction");
                }
                if (!StatLabels.ContainsKey(objective.Stat))
                {
                    throw new InvalidDataException($"objective {objective.Id} tracks '{objective.Stat}', which has no stat label");
                }
                byId.Add(objective.Id, (group, objective));
            }
        }
        GrindObjectivesById = byId;
        if (WelcomeGroup.Stat is { } welcomeStat && (!StatLabels.ContainsKey(welcomeStat) || WelcomeGroup.Target < 1))
        {
            throw new InvalidDataException($"the welcome group tracks '{welcomeStat}' to {WelcomeGroup.Target}; it needs a labelled stat and a target of 1 or more");
        }
        // A listed group that does not exist is refused when the content loads (LiveProblem): a group and its listing may be
        // staged in one changeset, and staging checks each change against the published file.
        if (Live is not null && Live.Select(entry => entry.Group).Distinct().Count() != Live.Count)
        {
            throw new InvalidDataException("the live objectives list names a group twice");
        }
        // Choices are a Season Pass level's (FIFA's level rewardOptionList); an objective, group or gift pays one reward.
        if (GrindGroups.SelectMany(group => group.Objectives.Select(objective => objective.Award).Append(group.Award)).Concat(Gifts.Select(gift => gift.Award))
            .Any(reward => reward.Options.Count > 0))
        {
            throw new InvalidDataException("only a Season Pass level can offer a choice of rewards");
        }
        // Season ids key clubs' Season XP: one per season, never the default season's. Seasons never overlap.
        var seasonIds = new HashSet<long> { Campaign.Id };
        foreach (var season in Seasons)
        {
            if (!seasonIds.Add(season.Id))
                throw new InvalidDataException($"season {season.Id} reuses a season id (the default season is {Campaign.Id})");
        }
        var byStart = Seasons.Where(season => season.Enabled).OrderBy(season => season.StartsAt).ToList();
        for (var index = 1; index < byStart.Count; index++)
        {
            if (byStart[index].StartsAt < byStart[index - 1].EndsAt)
                throw new InvalidDataException($"seasons {byStart[index - 1].Id} and {byStart[index].Id} overlap");
        }
        // Gift claims share the group claim scope, so a gift id must not be a group's.
        var groupIds = GrindGroups.Select(group => group.Id).Append(WelcomeGroup.Id).ToHashSet();
        foreach (var gift in Gifts)
        {
            if (gift.Stat is { } stat && (!StatLabels.ContainsKey(stat) || gift.Target < 1))
            {
                throw new InvalidDataException($"gift {gift.Id} tracks '{stat}' to {gift.Target}; it needs a labelled stat and a target of 1 or more");
            }
            if (!groupIds.Add(gift.Id) || byId.ContainsKey(gift.Objective.Id))
            {
                throw new InvalidDataException($"gift {gift.Id} reuses a group or objective id");
            }
        }
    }
}
