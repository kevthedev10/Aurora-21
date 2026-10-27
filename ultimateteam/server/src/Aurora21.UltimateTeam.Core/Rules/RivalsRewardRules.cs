using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>
/// A pack in a reward: how many, whether it is granted untradeable (its items open untradeable), and optionally the grant source it
/// is recorded under (default: the mode's), such as <c>rivals_option_c</c> or <c>rivals_promotion</c>.
/// </summary>
public sealed class RewardPack
{
    public required long PackId { get; set; }
    public long Count { get; set; } = 1;
    public bool Untradeable { get; set; }
    public string? Source { get; set; }
}

/// <summary>One reward: coins, FUT Champions Points and packs.</summary>
public sealed class RewardOption
{
    public long Coins { get; set; }
    public long FcPoints { get; set; }
    public List<RewardPack> Packs { get; set; } = new();

    /// <summary>The stored form, kept with a settled result so a later rules edit never changes it.</summary>
    public JsonObject ToJson() => new()
    {
        ["coins"] = Coins,
        ["fcPoints"] = FcPoints,
        ["packs"] = new JsonArray(Packs.Select(pack =>
        {
            var stored = new JsonObject { ["packId"] = pack.PackId, ["count"] = pack.Count, ["untradeable"] = pack.Untradeable };
            if (pack.Source is { } source)
            {
                stored["source"] = source;
            }
            return (JsonNode?)stored;
        }).ToArray()),
    };

    /// <summary>A reward of the mode tables as the payouts carry it: its coins, points and pack lines.</summary>
    public static RewardOption From(Reward reward) => new()
    {
        Coins = reward.Coins,
        FcPoints = reward.FcPoints,
        Packs = reward.Packs.ToList(),
    };

    public static RewardOption FromJson(JsonObject? stored) => new()
    {
        Coins = stored?.Int("coins") ?? 0,
        FcPoints = stored?.Int("fcPoints") ?? 0,
        Packs = (stored?.Arr("packs") ?? new JsonArray()).OfType<JsonObject>()
            .Select(pack => new RewardPack
            {
                PackId = pack.Int("packId"),
                Count = pack.Int("count", 1),
                Untradeable = pack.Bool("untradeable"),
                Source = pack.Str("source") is { Length: > 0 } source ? source : null,
            })
            .ToList(),
    };

    /// <summary>Several rewards as one: their coins and points added, their packs listed in order.</summary>
    public static RewardOption Sum(IEnumerable<RewardOption> options)
    {
        var total = new RewardOption();
        foreach (var option in options)
        {
            total.Coins += option.Coins;
            total.FcPoints += option.FcPoints;
            total.Packs.AddRange(option.Packs);
        }
        return total;
    }
}

/// <summary>One club's result in a weekly competition, for its division's table.</summary>
public sealed record RivalsWeekRow(long ClubId, int Division, long Score, long Games);

/// <summary>
/// A division's week: how many of its clubs scored, each rank's threshold (Rank I first) and each club's rank. A pooled division
/// (fewer clubs than poolBelowClubs) takes the server-wide table's thresholds.
/// </summary>
public sealed record RivalsDivisionTable(int Participants, List<long> Thresholds, Dictionary<long, int> Ranks, bool Pooled = false);

/// <summary>One club's final place in a Weekend League, and the reward its rank pays in that Weekend League.</summary>
public sealed record ChampionsStanding(long ClubId, long Position, long Wins, long Games, ChampionsRank Rank, RewardOption Reward);

/// <summary>The Division Rivals Weekly Score (EA help, Nov 2020).</summary>
public sealed class RivalsWeeklyScoreRules
{
    public required long Win { get; set; }
    public required long Draw { get; set; }
    public required long Loss { get; set; }
    public required long PerGoal { get; set; }
    public required long GoalCap { get; set; }
    public required long MatchCap { get; set; }

    /// <summary>
    /// Whether a completed Weekend League match adds <see cref="WeekendLeagueMatch"/> and uses one of the week's counting
    /// matches (retail FIFA 21). Off, Weekend League matches leave the Rivals Weekly Score and its counting matches alone.
    /// </summary>
    public required bool WeekendLeagueCounts { get; set; }
    public required long WeekendLeagueMatch { get; set; }
}

/// <summary>
/// rules/rivals_rewards.json (Python server 21bf125, 56145ed; phase-1 table, pack-odds-2026-09 PLAN.md 5.1): the Weekly Score,
/// the six ranks' shares of each division and the pooling threshold for small divisions; with the promotion rewards and every
/// division's reward options by rank from the Rivals reward tables (migration 131, <see cref="WithRewards"/>). A rank offers 1-4
/// options (FIFA shows at most four).
/// </summary>
public sealed class RivalsRewardRules : IJsonOnDeserialized
{
    public const int Ranks = 6;
    public const int DivisionCount = 10;
    private const int MaxOptions = 4;

    public required long Version { get; set; }
    public required long SettleDelaySeconds { get; set; }
    public required RivalsWeeklyScoreRules WeeklyScore { get; set; }
    public required List<int> RankShares { get; set; }

    /// <summary>A division with fewer clubs than this in a week ranks on the pooled server-wide table; 0 never pools.</summary>
    public required int PoolBelowClubs { get; set; }

    /// <summary>The reward for reaching each division the first time; set by <see cref="WithRewards"/>.</summary>
    [JsonIgnore]
    public Dictionary<int, RewardOption> Promotion { get; private set; } = new();

    /// <summary>Each division's six ranks of reward options; set by <see cref="WithRewards"/>.</summary>
    [JsonIgnore]
    public Dictionary<int, List<List<RewardOption>>> Divisions { get; private set; } = new();

    /// <summary>The Weekly Score one finished match earns; nothing once the week's counting matches are used.</summary>
    public long ScoreGain(string outcome, long goalsFor, long counted)
    {
        var rules = WeeklyScore;
        if (counted >= rules.MatchCap)
        {
            return 0;
        }
        var result = outcome switch { "win" => rules.Win, "draw" => rules.Draw, "loss" => rules.Loss, _ => -1 };
        return result < 0 ? 0 : result + rules.PerGoal * Math.Min(rules.GoalCap, Math.Max(0, goalsFor));
    }

    /// <summary>The rewards for every division entered for the first time (those numerically below the best so far), as one.</summary>
    public (RewardOption Reward, List<int> Entered) PromotionFor(int bestDivision, int newDivision)
    {
        var entered = Enumerable.Range(newDivision, Math.Max(0, bestDivision - newDivision)).ToList();
        return (RewardOption.Sum(entered.Select(division => Promotion[division])), entered);
    }

    /// <summary>(tierStart, tierEnd) percent bands per rank, best first: 0-20, 20-45, ...</summary>
    public List<(int Start, int End)> RankBands()
    {
        var bands = new List<(int, int)>();
        var start = 0;
        foreach (var share in RankShares)
        {
            bands.Add((start, start + share));
            start += share;
        }
        return bands;
    }

    public RewardOption Option(int division, int rank, int categoryId)
    {
        var options = Divisions[Math.Clamp(division, 1, DivisionCount)][Math.Clamp(rank, 1, Ranks) - 1];
        var index = categoryId - 1;
        return index >= 0 && index < options.Count ? options[index] : options[0];
    }

    /// <summary>
    /// One division's week: its ranks and score thresholds. Clubs with a Weekly Score are ordered by score, then fewer
    /// matches, then id; each rank's threshold is the lowest score inside its percentile band, an empty band takes the
    /// band above's value, and the list never rises. Every club then takes the best rank whose threshold it reaches (the
    /// test FIFA uses to pick the claim tier), so ties go to the better rank.
    /// </summary>
    public RivalsDivisionTable DivisionTable(IEnumerable<RivalsWeekRow> rows)
    {
        var qualified = rows.Where(row => row.Score > 0).OrderByDescending(row => row.Score).ThenBy(row => row.Games).ThenBy(row => row.ClubId).ToList();
        var bands = RankBands();
        var lowest = new Dictionary<int, long>();
        for (var position = 1; position <= qualified.Count; position++)
        {
            var percentile = (int)Math.Ceiling(100.0 * position / qualified.Count);
            var index = bands.FindIndex(band => band.Start < percentile && percentile <= band.End);
            var rank = index < 0 ? Ranks : index + 1;
            var score = qualified[position - 1].Score;
            lowest[rank] = Math.Min(lowest.GetValueOrDefault(rank, score), score);
        }
        var thresholds = new List<long>();
        var previous = (qualified.Count > 0 ? qualified[0].Score : 0) + 1;
        for (var rank = 1; rank <= Ranks; rank++)
        {
            var value = Math.Max(1, Math.Min(previous, lowest.GetValueOrDefault(rank, previous)));
            thresholds.Add(value);
            previous = value;
        }
        return new RivalsDivisionTable(qualified.Count, thresholds, qualified.ToDictionary(row => row.ClubId, row => RankForScore(thresholds, row.Score)));
    }

    /// <summary>
    /// Every division's table for one week (PLAN.md 2 item 19): a division with poolBelowClubs clubs or more ranks within itself; a
    /// smaller one takes the thresholds of one pooled table of every club with a Weekly Score that week, server-wide, and its clubs
    /// rank against them. Retail ranked within the division, which gives a lone club Rank VI and two clubs III and VI.
    /// </summary>
    public Dictionary<int, RivalsDivisionTable> WeekTables(IReadOnlyCollection<RivalsWeekRow> rows)
    {
        var pooled = new Lazy<RivalsDivisionTable>(() => DivisionTable(rows));
        return Enumerable.Range(1, DivisionCount).ToDictionary(division => division, division => WeekTable(rows, division, pooled));
    }

    /// <summary>One division's table for a week, as <see cref="WeekTables"/> builds it.</summary>
    public RivalsDivisionTable WeekTable(IReadOnlyCollection<RivalsWeekRow> rows, int division) =>
        WeekTable(rows, division, new Lazy<RivalsDivisionTable>(() => DivisionTable(rows)));

    private RivalsDivisionTable WeekTable(IReadOnlyCollection<RivalsWeekRow> rows, int division, Lazy<RivalsDivisionTable> pooled)
    {
        var own = DivisionTable(rows.Where(row => row.Division == division));
        if (own.Participants >= PoolBelowClubs)
        {
            return own;
        }
        return new RivalsDivisionTable(own.Participants, pooled.Value.Thresholds,
            own.Ranks.Keys.ToDictionary(club => club, club => pooled.Value.Ranks[club]), Pooled: true);
    }

    /// <summary>The rank a Weekly Score reaches against a table's thresholds; 0 without a score.</summary>
    public static int RankForScore(IReadOnlyList<long> thresholds, long score)
    {
        if (score <= 0)
        {
            return 0;
        }
        for (var index = 0; index < thresholds.Count; index++)
        {
            if (score >= thresholds[index])
            {
                return index + 1;
            }
        }
        return Ranks;
    }

    /// <summary>
    /// Each rank's Weekly Score range for the hub's prizeTiers, best first: from its threshold up to the rank above's less
    /// one (Rank I open-ended). A rank with the same threshold as the rank above is empty this week, so its range ends
    /// below its start rather than swallowing the rank above's scores.
    /// </summary>
    public static List<(int TierLevel, long StartValue, long EndValue)> TierRanges(IReadOnlyList<long> thresholds)
    {
        var ranges = new List<(int TierLevel, long StartValue, long EndValue)>();
        for (var index = 0; index < thresholds.Count; index++)
        {
            var start = thresholds[index];
            var end = index == 0 ? int.MaxValue : Math.Max(start, thresholds[index - 1] - 1);
            if (index > 0 && start >= thresholds[index - 1])
            {
                end = start - 1;
            }
            ranges.Add((index + 1, start, end));
        }
        return ranges;
    }

    public void OnDeserialized()
    {
        if (RankShares.Count != Ranks || RankShares.Any(share => share <= 0) || RankShares.Sum() != 100)
        {
            throw new InvalidDataException("rankShares must be six positive percentages adding up to 100");
        }
        if (PoolBelowClubs < 0)
        {
            throw new InvalidDataException("poolBelowClubs must not be negative");
        }
        if (new[] { WeeklyScore.Win, WeeklyScore.Draw, WeeklyScore.Loss, WeeklyScore.PerGoal, WeeklyScore.GoalCap, WeeklyScore.MatchCap,
                WeeklyScore.WeekendLeagueMatch }.Any(value => value < 0) || SettleDelaySeconds < 0)
        {
            throw new InvalidDataException("weeklyScore and settleDelaySeconds must not be negative");
        }
    }

    /// <summary>The most reward options one rank offers (FIFA shows at most four).</summary>
    public const int MaxRankOptions = MaxOptions;

    /// <summary>These settings with their reward tables, checked as a whole.</summary>
    /// <exception cref="InvalidDataException">The tables are incomplete or a reward is invalid; the message says why.</exception>
    public RivalsRewardRules WithRewards(RivalsRewardTables tables)
    {
        if (!tables.Promotion.Keys.Order().SequenceEqual(Enumerable.Range(1, DivisionCount)))
        {
            throw new InvalidDataException("promotion needs divisions 1-10");
        }
        if (!tables.Divisions.Keys.Order().SequenceEqual(Enumerable.Range(1, DivisionCount)))
        {
            throw new InvalidDataException("divisions needs 1-10");
        }
        foreach (var (division, reward) in tables.Promotion)
        {
            if (RewardProblem(reward) is { } problem)
            {
                throw new InvalidDataException($"promotion to division {division}: {problem}");
            }
        }
        foreach (var (division, ranks) in tables.Divisions)
        {
            if (ranks.Count != Ranks || ranks.Any(options => options.Count is < 1 or > MaxOptions))
            {
                throw new InvalidDataException($"division {division} needs six ranks of 1-4 options");
            }
            foreach (var (options, rank) in ranks.Select((options, index) => (options, index + 1)))
            {
                if (options.Select(RewardProblem).FirstOrDefault(problem => problem is not null) is { } problem)
                {
                    throw new InvalidDataException($"division {division} rank {rank}: {problem}");
                }
            }
        }
        Promotion = tables.Promotion.ToDictionary(pair => pair.Key, pair => RewardOption.From(pair.Value));
        Divisions = tables.Divisions.ToDictionary(pair => pair.Key,
            pair => pair.Value.Select(options => options.Select(RewardOption.From).ToList()).ToList());
        return this;
    }

    /// <summary>Why a Rivals reward is invalid, or null: the shared checks, no player cards yet and no XP (Rivals pays none).</summary>
    public static string? RewardProblem(Reward reward) =>
        reward.Problem(cards: false) ?? (reward.Xp != 0 ? "Division Rivals rewards pay no XP" : null);

    /// <summary>Every pack a reward names, for the start-up check that each exists.</summary>
    [JsonIgnore]
    public IEnumerable<long> PackIds => Divisions.Values.SelectMany(ranks => ranks).SelectMany(options => options).Concat(Promotion.Values)
        .SelectMany(option => option.Packs).Select(pack => pack.PackId);
}

/// <summary>
/// One Weekend League reward rank (the champions_reward_ranks table since migration 126; seeded from rewards/champions.json): by wins
/// out of maxMatches, or a Top 200 band by leaderboard position. <see cref="TierLevel"/> is its stable id: settled results name it.
/// </summary>
public sealed class ChampionsRank
{
    public required int TierLevel { get; set; }
    public required string Name { get; set; }
    public List<long>? Wins { get; set; }
    public List<long>? Positions { get; set; }
    public required Reward Reward { get; set; }

    [JsonIgnore]
    public long Coins => Reward.Coins;

    [JsonIgnore]
    public long FcPoints => Reward.FcPoints;

    [JsonIgnore]
    public List<RewardPack> Packs => Reward.Packs.ToList();

    [JsonIgnore]
    public bool ByWins => Wins is not null;

    /// <summary>
    /// FIFA's own tier number (service 0x18047f0f0; Python server 764ef6e): win ranks 1-12 (Elite 1 to Bronze 3), the Top 200
    /// bands 1-6 in their own list. Set from the rules' order when they load.
    /// </summary>
    [JsonIgnore]
    public int ClientTier { get; internal set; }
}

/// <summary>A Top 200 band as one Weekend League pays it: the rank and the leaderboard positions it covers there.</summary>
public sealed record ChampionsBand(ChampionsRank Rank, long Low, long High, int ClientTier);

/// <summary>
/// The Weekend League reward table (Python server 56145ed; phase-1 20-game table, pack-odds-2026-09 PLAN.md 5.2): the settings of
/// rules/champions_rewards.json and the ranks of the champions_reward_ranks table (migration 126, <see cref="WithRanks"/>), win
/// ranks plus leaderboard bands, paid to clubs with minMatchesToRank matches. A band needs its position and, from Weekend League
/// minWinsForTopFromEvent, minWinsForTop wins (owner, 2026-10-05: 6 from 2962); its places are fixed by the Top size. FUT Champions Points are capped at fcPointsCap in
/// total.
/// </summary>
public sealed class ChampionsRewardRules
{
    public required long Version { get; set; }
    public required int MinMatchesToRank { get; set; }

    /// <summary>The wins a Top band place needs; fewer take the rank their wins earn, whatever the position.</summary>
    public required int MinWinsForTop { get; set; }

    /// <summary>The first Weekend League that needs <see cref="MinWinsForTop"/>; earlier ones keep paying a Top place on position alone.</summary>
    public required long MinWinsForTopFromEvent { get; set; }

    /// <summary>The wins a Top band place needs in one Weekend League.</summary>
    public long MinWinsForTopIn(long eventId) => eventId >= MinWinsForTopFromEvent ? MinWinsForTop : 0;

    public required long FcPointsCap { get; set; }

    /// <summary>The reward ranks, by tierLevel; set once by <see cref="WithRanks"/> when the rules load.</summary>
    [JsonIgnore]
    public IReadOnlyList<ChampionsRank> Ranks { get; private set; } = [];

    /// <summary>The most wins the win ranks cover; it must be seasons.json weekendLeague.maxMatches.</summary>
    [JsonIgnore]
    public long MaxWins => Ranks.Where(rank => rank.ByWins).Max(rank => rank.Wins![1]);

    /// <summary>The rank a number of wins earns.</summary>
    public ChampionsRank RankForWins(long wins, long maxMatches) =>
        Ranks.First(rank => rank.ByWins && rank.Wins![0] <= Math.Clamp(wins, 0, maxMatches) && Math.Clamp(wins, 0, maxMatches) <= rank.Wins[1]);

    /// <summary>
    /// The Top bands every Weekend League pays: the leaderboard slots' places as the Top size set them (seasons.json
    /// weekendLeague.topX, <see cref="LeaderboardSizes.ChampionsBands"/>), in position order and numbered 1-n for FIFA. They do
    /// not change with the number of players (owner, 2026-10-03: band sizing removed).
    /// </summary>
    public List<ChampionsBand> Bands() =>
        Ranks.Where(rank => !rank.ByWins).OrderBy(rank => rank.Positions![0])
            .Select((rank, index) => new ChampionsBand(rank, rank.Positions![0], rank.Positions[1], index + 1)).ToList();

    /// <summary>
    /// A Weekend League's final ranks. Clubs with fewer than minMatchesToRank matches are unranked, and so are
    /// <paramref name="excluded"/> clubs (staff in a test week), whose places pass to the clubs below; the rest are ordered by wins,
    /// then Skill Rating. A leaderboard position inside one of the Weekend League's <paramref name="bands"/> with the wins a Top
    /// place needs in <paramref name="eventId"/> replaces Elite 1; everyone else takes the rank their wins earn (the clubs below have no more wins, so no place passes on).
    /// </summary>
    public List<ChampionsStanding> Standings(long eventId, IEnumerable<(long ClubId, long Wins, long Games, long Rating)> rows, long maxMatches,
        IReadOnlyList<ChampionsBand> bands, IReadOnlySet<long>? excluded = null)
    {
        var minWins = MinWinsForTopIn(eventId);
        var ranked = rows.Where(row => row.Games >= MinMatchesToRank && excluded?.Contains(row.ClubId) != true)
            .OrderByDescending(row => row.Wins).ThenByDescending(row => row.Rating).ThenBy(row => row.ClubId).ToList();
        return ranked.Select((row, index) =>
        {
            var position = index + 1L;
            var band = row.Wins >= minWins ? bands.FirstOrDefault(entry => entry.Low <= position && position <= entry.High)?.Rank : null;
            var rank = band ?? RankForWins(row.Wins, maxMatches);
            return new ChampionsStanding(row.ClubId, position, row.Wins, row.Games, rank, Reward(rank));
        }).ToList();
    }

    /// <summary>A rank's reward as a <see cref="RewardOption"/>.</summary>
    public static RewardOption Reward(ChampionsRank rank) => new()
    {
        Coins = rank.Coins,
        FcPoints = rank.FcPoints,
        Packs = rank.Packs,
    };

    /// <summary>These settings with their reward ranks, checked as a whole table.</summary>
    /// <exception cref="InvalidDataException">The ranks do not make a valid table; the message says why.</exception>
    public ChampionsRewardRules WithRanks(IEnumerable<ChampionsRank> ranks)
    {
        Ranks = ranks.OrderBy(rank => rank.TierLevel).ToList();
        if (Ranks.Count == 0 || Ranks.Select(rank => rank.TierLevel).Distinct().Count() != Ranks.Count || Ranks.Any(rank => rank.TierLevel <= 0))
        {
            throw new InvalidDataException("ranks need distinct positive tierLevels");
        }
        foreach (var rank in Ranks)
        {
            if ((rank.Wins is null) == (rank.Positions is null) || (rank.Wins ?? rank.Positions)!.Count != 2
                || rank.Wins is { } wins && (wins[0] < 0 || wins[0] > wins[1]) || rank.Positions is { } positions && (positions[0] < 1 || positions[0] > positions[1]))
            {
                throw new InvalidDataException($"{rank.Name}: give wins or positions as [low, high]");
            }
            if (rank.Reward.Problem(cards: false) is { } problem)
            {
                throw new InvalidDataException($"{rank.Name}: {problem}");
            }
        }
        if (!Ranks.Any(rank => rank.ByWins))
        {
            throw new InvalidDataException("ranks need win ranks");
        }
        var covered = Ranks.Where(rank => rank.ByWins).SelectMany(rank => Enumerable.Range((int)rank.Wins![0], (int)(rank.Wins[1] - rank.Wins[0] + 1))).Order();
        if (!covered.SequenceEqual(Enumerable.Range(0, (int)MaxWins + 1)))
        {
            throw new InvalidDataException($"the win ranks must cover 0-{MaxWins} wins exactly once");
        }
        var bandPositions = Ranks.Where(rank => !rank.ByWins).OrderBy(rank => rank.Positions![0]).Select(rank => rank.Positions!).ToList();
        if (bandPositions.Where((positions, index) => positions[0] != (index == 0 ? 1 : bandPositions[index - 1][1] + 1)).Any())
        {
            throw new InvalidDataException("the Top 200 bands must run on from position 1");
        }
        if (Ranks.Count(rank => rank.ByWins) > ClientWinTiers)
        {
            throw new InvalidDataException($"FIFA shows at most {ClientWinTiers} win ranks");
        }
        if (bandPositions.Count > ClientTopBands)
        {
            throw new InvalidDataException($"FIFA shows at most {ClientTopBands} Top 200 bands");
        }
        // FIFA numbers each list best first, whatever the tierLevels (stable ids, so a band added later has a higher one):
        // the bands by position, the win ranks by wins.
        var bandTier = 1;
        foreach (var rank in Ranks.Where(rank => !rank.ByWins).OrderBy(rank => rank.Positions![0]))
        {
            rank.ClientTier = bandTier++;
        }
        var winTier = 1;
        foreach (var rank in Ranks.Where(rank => rank.ByWins).OrderByDescending(rank => rank.Wins![0]))
        {
            rank.ClientTier = winTier++;
        }
        return this;
    }

    /// <summary>FIFA's Weekend League tiers: 1-3 Elite, 4-6 Gold, 7-9 Silver, 10-12 Bronze.</summary>
    public const int ClientWinTiers = 12;

    /// <summary>
    /// FIFA numbers the Top 200 bands 1-6 in their own list (<see cref="ChampionsRank.ClientTier"/>: client service
    /// 0x18047f0f0, Python server 764ef6e). Not yet checked in game with six.
    /// </summary>
    public const int ClientTopBands = 6;

    /// <summary>FIFA's own name for a win tier (1-12): Elite 1-3, Gold 1-3, Silver 1-3, Bronze 1-3.</summary>
    public static string ClientWinTierName(int clientTier) =>
        $"{(((clientTier - 1) / 3) switch { 0 => "Elite", 1 => "Gold", 2 => "Silver", _ => "Bronze" })} {(clientTier - 1) % 3 + 1}";

    /// <summary>FIFA's tier number for a wins total (1-12).</summary>
    public int ClientTierForWins(long wins, long maxMatches) => RankForWins(wins, maxMatches).ClientTier;

    [JsonIgnore]
    public IEnumerable<long> PackIds => Ranks.SelectMany(rank => rank.Packs).Select(pack => pack.PackId);
}
