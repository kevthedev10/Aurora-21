using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>
/// A Squad Battles prize: coins (<c>type</c> coin) or a number of one pack (<c>type</c> pack), the pack with its own untradeable
/// choice (migration 133; a result settled before stored none, and paid those packs tradeable).
/// </summary>
public sealed class SquadBattlesAward
{
    public required string Type { get; set; }
    public long Amount { get; set; }
    public long PackId { get; set; }
    public long Count { get; set; } = 1;
    public bool Untradeable { get; set; }

    /// <summary>The stored form, kept with a settled result so a later rules edit never changes it.</summary>
    public JsonObject ToJson() => new() { ["type"] = Type, ["amount"] = Amount, ["packId"] = PackId, ["count"] = Count, ["untradeable"] = Untradeable };

    public static SquadBattlesAward FromJson(JsonObject stored) => new()
    {
        Type = stored.Str("type"),
        Amount = stored.Int("amount"),
        PackId = stored.Int("packId"),
        Count = stored.Int("count", 1),
        Untradeable = stored.Bool("untradeable"),
    };

    /// <summary>A reward of the mode tables as Squad Battles pays it: its coins, then each pack line.</summary>
    public static List<SquadBattlesAward> From(Reward reward) =>
        (reward.Coins > 0 ? [new SquadBattlesAward { Type = "coin", Amount = reward.Coins }] : new List<SquadBattlesAward>())
            .Concat(reward.Packs.Select(pack => new SquadBattlesAward { Type = "pack", PackId = pack.PackId, Count = pack.Count, Untradeable = pack.Untradeable }))
            .ToList();
}

/// <summary>
/// One Squad Battles reward tier as the reward tables (migration 133) and the seed rewards/squad_battles.json hold it: the tier with
/// its reward in the shared format. <see cref="Id"/> is stable: settled results name it.
/// </summary>
public sealed class SquadBattlesRewardTier
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public int RankStart { get; set; }
    public int RankEnd { get; set; }
    public int TierLevel { get; set; }
    public int PercentStart { get; set; }
    public int PercentEnd { get; set; }
    public long MinPoints { get; set; }
    public required Reward Reward { get; set; }

    [JsonIgnore]
    public bool Ranked => Kind == "ranked";

    public SquadBattlesTier ToTier() => new()
    {
        Id = Id, Kind = Kind, RankStart = RankStart, RankEnd = RankEnd, TierLevel = TierLevel, PercentStart = PercentStart,
        PercentEnd = PercentEnd, MinPoints = MinPoints, Awards = SquadBattlesAward.From(Reward),
    };
}

/// <summary>
/// One weekly tier, best first. A <c>ranked</c> tier is a Top-N band of leaderboard positions (rankStart to rankEnd); a
/// <c>points</c> tier (tierLevel 1-12, which FIFA names Elite 1-3, Gold, Silver and Bronze 1-3) is a percentile band
/// (percentStart to percentEnd) or, in points mode, a minimum score.
/// </summary>
public sealed class SquadBattlesTier
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public int RankStart { get; set; }
    public int RankEnd { get; set; }
    public int TierLevel { get; set; }
    public int PercentStart { get; set; }
    public int PercentEnd { get; set; }
    public long MinPoints { get; set; }
    public List<SquadBattlesAward> Awards { get; set; } = new();

    [JsonIgnore]
    public bool Ranked => Kind == "ranked";
}

public sealed class SquadBattlesRankedTiers
{
    public required bool Enabled { get; set; }
    public required int MinParticipants { get; set; }
}

/// <summary>One club's week, for the standings.</summary>
public sealed record SquadBattlesWeekRow(long ClubId, string Name, long Points, long Wins, long Games);

/// <summary>A club's place in a week: its position, the Top-N rank (0 outside one), and its tier (null when none).</summary>
public sealed record SquadBattlesStanding(SquadBattlesWeekRow Row, int Position, int Rank, int Percentile, SquadBattlesTier? Tier)
{
    public bool Ranked => Rank > 0;

    /// <summary>The hub's userTierLevel: 0 inside a Top-N tier or without a tier.</summary>
    public int TierLevel => Ranked || Tier is null ? 0 : Tier.TierLevel;
}

/// <summary>A Top-N tier as every week pays it: the positions it covers (the Top size sets them).</summary>
public sealed record SquadBattlesRankedBand(SquadBattlesTier Tier, int Start, int End);

/// <summary>
/// The Squad Battles weekly rank tiers and their prizes (Python server f354894; phase-1 prizes, pack-odds-2026-09 PLAN.md 5.3): the
/// settings of rules/sqbt_rewards.json and the tiers of the squad_battles_reward_tiers table (migration 133, <see cref="WithTiers"/>).
/// Six Top slots are paid by leaderboard position once rankedTiers.minParticipants clubs have points, their places set by the Top
/// size (squad_battles.json competition.boardSize, owner 2026-10-03: no band sizing); everyone else by retail's percentile bands
/// (mode percentile) or fixed thresholds (mode points).
/// </summary>
public sealed class SquadBattlesRewardRules : IJsonOnDeserialized
{
    public required long Version { get; set; }
    public required string Mode { get; set; }
    public required long SettleDelaySeconds { get; set; }
    public required long MinPointsToQualify { get; set; }
    public required SquadBattlesRankedTiers RankedTiers { get; set; }

    /// <summary>The tiers, Top slots first then the points tiers by tierLevel; set by <see cref="WithTiers"/> when the rules load.</summary>
    [JsonIgnore]
    public List<SquadBattlesTier> Tiers { get; private set; } = new();

    /// <summary>The Top slots: Rank 1 and five more, their places set by the Top size.</summary>
    public const int RankedSlots = 6;

    [JsonIgnore]
    public IEnumerable<SquadBattlesTier> PointsTiers => Tiers.Where(tier => !tier.Ranked);

    public bool RankedActive(int participants) => RankedTiers.Enabled && participants >= RankedTiers.MinParticipants;

    /// <summary>The Top slots every week pays, in position order: their places as the Top size set them.</summary>
    public List<SquadBattlesRankedBand> RankedBands() =>
        Tiers.Where(tier => tier.Ranked).OrderBy(tier => tier.RankStart).Select(tier => new SquadBattlesRankedBand(tier, tier.RankStart, tier.RankEnd)).ToList();

    /// <summary>The Top slots as they stand.</summary>
    [JsonIgnore]
    public List<SquadBattlesRankedBand> ConfiguredRankedBands => RankedBands();

    /// <summary>Best first: points, then wins, then fewer games, then name.</summary>
    public static IEnumerable<SquadBattlesWeekRow> Order(IEnumerable<SquadBattlesWeekRow> rows) =>
        rows.OrderByDescending(row => row.Points).ThenByDescending(row => row.Wins).ThenBy(row => row.Games)
            .ThenBy(row => row.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(row => row.ClubId);

    /// <summary>
    /// Every qualified club's position and tier, best first, with the week's Top-N <paramref name="bands"/> (unscaled when not given).
    /// <paramref name="excluded"/> clubs (staff in a test week) are left out, so their places pass to the clubs below.
    /// Outside the Top-N a club holds the best points tier whose threshold (<see cref="Thresholds(IReadOnlyList{SquadBattlesWeekRow})"/>)
    /// its points reach, the same thresholds the hub sends as tierStartValue, so FIFA's own placement after a match agrees.
    /// </summary>
    public List<SquadBattlesStanding> Standings(IEnumerable<SquadBattlesWeekRow> rows, IReadOnlyList<SquadBattlesRankedBand>? bands = null,
        IReadOnlySet<long>? excluded = null)
    {
        var qualified = Order(rows.Where(row => row.Points >= MinPointsToQualify && excluded?.Contains(row.ClubId) != true)).ToList();
        IReadOnlyList<SquadBattlesRankedBand> ranked = RankedActive(qualified.Count) ? bands ?? ConfiguredRankedBands : [];
        var thresholds = Thresholds(qualified);
        var points = PointsTiers.ToList();
        return qualified.Select((row, index) =>
        {
            var position = index + 1;
            var percentile = (int)Math.Ceiling(100.0 * position / qualified.Count);
            var tier = ranked.FirstOrDefault(candidate => candidate.Start <= position && position <= candidate.End)?.Tier;
            var isRanked = tier is not null;
            tier ??= points.FirstOrDefault(candidate => row.Points >= thresholds[candidate.Id]);
            return new SquadBattlesStanding(row, position, isRanked ? position : 0, percentile, tier);
        }).ToList();
    }

    /// <summary>Each points tier's threshold for a standings table (see <see cref="Thresholds(IReadOnlyList{SquadBattlesWeekRow})"/>).</summary>
    public Dictionary<string, long> Thresholds(IReadOnlyList<SquadBattlesStanding> table) => Thresholds(table.Select(standing => standing.Row).ToList());

    /// <summary>
    /// Each points tier's threshold (tierStartValue), by tier id, for the qualified clubs best first. Points mode: the tier's
    /// minPoints. Percentile mode (retail): a tier needs both its percentile band and its retail minimum points, so its threshold
    /// is the higher of minPoints and the score of the last club inside its top percentEnd% of the week (one point above the
    /// leader when no club is); an empty week falls back to the minimums. Thresholds never rise down the list and are at least 1.
    /// </summary>
    public Dictionary<string, long> Thresholds(IReadOnlyList<SquadBattlesWeekRow> ordered)
    {
        var thresholds = new Dictionary<string, long>();
        var unreachable = (ordered.Count > 0 ? ordered.Max(row => row.Points) : 0) + 1;
        foreach (var tier in PointsTiers)
        {
            var value = Math.Max(1, tier.MinPoints);
            if (Mode == "percentile" && ordered.Count > 0)
            {
                // Position p is inside the top e% when ceil(100 p / n) <= e, i.e. p <= floor(e n / 100).
                var last = (int)((long)tier.PercentEnd * ordered.Count / 100);
                value = Math.Max(value, last > 0 ? ordered[last - 1].Points : unreachable);
            }
            thresholds[tier.Id] = value;
        }
        return thresholds;
    }

    [JsonIgnore]
    public IEnumerable<long> PackIds => Tiers.SelectMany(tier => tier.Awards).Where(award => award.Type == "pack").Select(award => award.PackId);

    public void OnDeserialized()
    {
        if (Mode is not ("percentile" or "points"))
        {
            throw new InvalidDataException($"mode must be percentile or points, not {Mode}");
        }
        if (SettleDelaySeconds < 0 || MinPointsToQualify < 1 || RankedTiers.MinParticipants < 1)
        {
            throw new InvalidDataException("settleDelaySeconds, minPointsToQualify and rankedTiers.minParticipants are required");
        }
    }

    /// <summary>Why a Squad Battles reward is invalid, or null: the shared checks, no player cards yet, coins and packs only.</summary>
    public static string? RewardProblem(Reward reward) =>
        reward.Problem(cards: false) ?? (reward.Xp != 0 || reward.FcPoints != 0 ? "Squad Battles rewards pay coins and packs only" : null)
        ?? (reward.Lines.Any(line => line.Source is not null) ? "Squad Battles reward lines are granted from the squad_battles source" : null);

    /// <summary>These settings with their tiers (Top slots first, then the points tiers), checked as a whole table.</summary>
    /// <exception cref="InvalidDataException">The tiers do not make a valid table; the message says why.</exception>
    public SquadBattlesRewardRules WithTiers(IEnumerable<SquadBattlesRewardTier> rewardTiers)
    {
        var all = rewardTiers.ToList();
        if (all.Count == 0 || all.Select(tier => tier.Id).Distinct().Count() != all.Count)
        {
            throw new InvalidDataException("tiers need unique ids");
        }
        if (all.Count(tier => tier.Ranked) != RankedSlots)
        {
            throw new InvalidDataException($"Squad Battles needs exactly {RankedSlots} Top slots");
        }
        if (all.FirstOrDefault(tier => RewardProblem(tier.Reward) is not null) is { } bad)
        {
            throw new InvalidDataException($"{bad.Id}: {RewardProblem(bad.Reward)}");
        }
        Tiers = all.Where(tier => tier.Ranked).OrderBy(tier => tier.RankStart)
            .Concat(all.Where(tier => !tier.Ranked).OrderBy(tier => tier.TierLevel)).Select(tier => tier.ToTier()).ToList();
        int nextRank = 1, nextPercent = 1, lastLevel = 0;
        long? lastMinPoints = null;
        var pointsStarted = false;
        foreach (var tier in Tiers)
        {
            if (tier.Kind == "ranked")
            {
                if (pointsStarted || tier.RankStart != nextRank || tier.RankEnd < tier.RankStart)
                {
                    throw new InvalidDataException($"{tier.Id}: ranked tiers come first and run on from rank {nextRank}");
                }
                nextRank = tier.RankEnd + 1;
            }
            else if (tier.Kind == "points")
            {
                pointsStarted = true;
                if (tier.TierLevel is < 1 or > 12 || tier.TierLevel <= lastLevel)
                {
                    throw new InvalidDataException($"{tier.Id}: tierLevel must be 1-12 (FIFA names only those), rising down the list");
                }
                lastLevel = tier.TierLevel;
                if (Mode == "percentile" && (tier.PercentStart != nextPercent || tier.PercentEnd < tier.PercentStart || tier.PercentEnd > 100))
                {
                    throw new InvalidDataException($"{tier.Id}: percent bands must run on from {nextPercent} up to 100");
                }
                nextPercent = tier.PercentEnd + 1;
                if (Mode == "points" && (tier.MinPoints < 1 || lastMinPoints is { } last && tier.MinPoints >= last))
                {
                    throw new InvalidDataException($"{tier.Id}: minPoints must be at least 1 and fall down the list");
                }
                lastMinPoints = tier.MinPoints;
            }
            else
            {
                throw new InvalidDataException($"{tier.Id}: kind must be ranked or points");
            }
            if (tier.Awards.Any(award => award.Type switch { "coin" => award.Amount <= 0, "pack" => award.Count <= 0, _ => true }))
            {
                throw new InvalidDataException($"{tier.Id}: an award is a positive coin amount or a positive pack count");
            }
        }
        if (Mode == "percentile" && lastLevel > 0 && nextPercent != 101)
        {
            throw new InvalidDataException("percent bands must end at 100");
        }
        return this;
    }
}

/// <summary>A card in a TOTW: its catalogue resource id, and for a starter the 3-4-3 slot it plays.</summary>
public sealed class TotwCard
{
    public required long ResourceId { get; set; }
    public required string Name { get; set; }
    public required long Rating { get; set; }
    public string? Position { get; set; }
    public string? Slot { get; set; }
}

public sealed class TotwWeek
{
    public required long Week { get; set; }
    public required string Released { get; set; }
    public required string Formation { get; set; }
    public required string XiSource { get; set; }
    public required List<TotwCard> Starters { get; set; }
    public required List<TotwCard> Subs { get; set; }
}

/// <summary>
/// rules/sqbt_totw.json (Python server a4fdc85): every FIFA 21 Team of the Week, EA's XIs (checked against EA's TOTW graphics;
/// each week names its source image), and the one playable now.
/// </summary>
public sealed class TotwArchive : IJsonOnDeserialized
{
    public required long Version { get; set; }
    public required string Note { get; set; }
    public required long Current { get; set; }
    public required List<TotwWeek> Weeks { get; set; }

    public TotwWeek? Week(long week) => Weeks.FirstOrDefault(entry => entry.Week == week);

    public void OnDeserialized()
    {
        if (Weeks.FirstOrDefault(entry => entry.Starters.Count != 11) is { } bad)
        {
            throw new InvalidDataException($"TOTW {bad.Week} needs 11 starters");
        }
        if (Weeks.Select(entry => entry.Week).Distinct().Count() != Weeks.Count || Current != 0 && Week(Current) is null)
        {
            throw new InvalidDataException("TOTW weeks must be unique, and the current one in the archive");
        }
    }
}
