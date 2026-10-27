using System.Text.Json.Serialization;
using Aurora21.UltimateTeam.Core.Helpers;

namespace Aurora21.UltimateTeam.Core.Rules;

public sealed class DraftEntryRules
{
    public required long Coins { get; set; }
    public required long Points { get; set; }
    public required long DraftTokens { get; set; }
    public required List<long> StoreEntryIds { get; set; }
    public required string StoreName { get; set; }
    public required string StoreDescription { get; set; }
}

public sealed class DraftPickRules
{
    public required int ChoicesPerScreen { get; set; }
    public required long MinRating { get; set; }
    public required long StrongFloor { get; set; }
    public required long CaptainStrongFloor { get; set; }
    public required long RatingBiasBase { get; set; }
    public required double Exponent { get; set; }
    public required double CaptainExponent { get; set; }
    public required double SpecialBias { get; set; }
    public required double WeakRoundExponent { get; set; }
    public required double WeakRoundSpecialBias { get; set; }
    /// <summary>Relative category weights; unspecified standard categories use 1 and specials use SpecialBias.</summary>
    public Dictionary<string, double> CategoryWeights { get; set; } = new();
    public Dictionary<string, double> WeakRoundCategoryWeights { get; set; } = new();
    /// <summary>
    /// Draft's own weights for the Gold Rare groups (by group code), replacing the pack weights on Draft screens: packs keep
    /// a thin top for the market, a Draft squad is single-use and should feel strong. Empty uses the pack weights. A group
    /// left out is never drawn, so name every group.
    /// </summary>
    public Dictionary<string, double> GoldRareBucketWeights { get; set; } = new();
    public Dictionary<string, double> WeakRoundGoldRareBucketWeights { get; set; } = new();
    public required double Elite99Chance { get; set; }
    public required List<int> Elite99RoundCounts { get; set; }
    public required List<int> WeakRoundCounts { get; set; }
    public required int ManagerPoolSize { get; set; }

    /// <summary>Whether offers use standard base cards and scheduled promos; false offers every generally available card.</summary>
    public required bool ScheduledPool { get; set; }
}

/// <summary>One bundle of a Draft reward level: drawn by its weight against the level's other choices.</summary>
public sealed class DraftRewardChoice
{
    public int Weight { get; set; } = 1;
    public required Reward Reward { get; set; }
}

/// <summary>
/// A Draft reward level (migration 135, todo.md section 2): what a run of one mode ending on a number of wins gets. Every run
/// there gets <see cref="Reward"/>; one of <see cref="Choices"/> is drawn by weight from the run's seed. Also the shape of the
/// bundled seed rewards/draft.json.
/// </summary>
public sealed class DraftRewardLevel
{
    public const string Online = "online";
    public const string Single = "single";

    public required string Mode { get; set; }
    public required int Wins { get; set; }
    public required Reward Reward { get; set; }
    public required List<DraftRewardChoice> Choices { get; set; }

    /// <summary>The grant source a mode's packs carry (rules/tradeability.json).</summary>
    public static string Source(string mode) => mode == Online ? RewardSources.DraftOnline : RewardSources.DraftSingle;
}

/// <summary>
/// What a finished run pays: its level's reward and the drawn choice (1-based <see cref="Choice"/>), packs granted under
/// <see cref="Source"/> with each line's own tradeability.
/// </summary>
public sealed record DraftRunPayout(Reward Level, int Choice, Reward Drawn, string Source)
{
    public long Coins => Level.Coins + Drawn.Coins;
    public IEnumerable<RewardLine> Lines => Level.Lines.Concat(Drawn.Lines);

    /// <summary>Every pack paid, one entry per copy.</summary>
    public IReadOnlyList<long> Packs => Lines.Where(line => line.PackId is not null)
        .SelectMany(line => Enumerable.Repeat(line.PackId!.Value, (int)line.Count)).ToList();
}

public sealed class DraftFormation
{
    public required string Name { get; set; }
    public required List<string> Positions { get; set; }
    /// <summary>Whether new runs are offered this formation; a run already on it keeps working.</summary>
    public bool Offered { get; set; } = true;
}

/// <summary>rules/draft.json: entry cost, formations, slot positions, choice weighting and run rewards.</summary>
public sealed class DraftRules : IJsonOnDeserialized
{
    /// <summary>The formations a new run may be offered.</summary>
    public IReadOnlyList<string> FormationNames => Formations.Where(formation => formation.Offered).Select(formation => formation.Name).ToList();

    public IReadOnlyList<string>? FormationPositions(string name) =>
        Formations.FirstOrDefault(formation => formation.Name == name)?.Positions;

    public required DraftEntryRules Entry { get; set; }
    public required long SquadId { get; set; }
    public required string SquadName { get; set; }
    public required long FirstItemId { get; set; }
    public required long WinsToComplete { get; set; }

    /// <summary>The most choices a level holds, and the largest weight.</summary>
    public const int MaxChoices = 50;
    public const int MaxWeight = 1000;

    /// <summary>The run reward levels (migration 135), set by <see cref="WithRewards"/>: both modes, wins 0 to winsToComplete.</summary>
    [JsonIgnore]
    public IReadOnlyList<DraftRewardLevel> RewardLevels { get; private set; } = [];

    public DraftRewardLevel? RewardLevel(string mode, int wins) =>
        RewardLevels.FirstOrDefault(level => level.Mode == mode && level.Wins == wins);

    /// <summary>Why a level is invalid, or null: 1 to <see cref="MaxChoices"/> choices of weight 1 to <see cref="MaxWeight"/>, and coins and packs only.</summary>
    public static string? LevelProblem(DraftRewardLevel level)
    {
        if (level.Mode is not (DraftRewardLevel.Online or DraftRewardLevel.Single))
            return $"unknown Draft mode {level.Mode}";
        if (level.Choices.Count is < 1 or > MaxChoices)
            return $"a level needs 1 to {MaxChoices} choices";
        if (level.Choices.FirstOrDefault(choice => choice.Weight is < 1 or > MaxWeight) is not null)
            return $"a choice's weight runs from 1 to {MaxWeight}";
        foreach (var (reward, name) in level.Choices.Select((choice, index) => (choice.Reward, $"choice {index + 1}")).Prepend((level.Reward, "the level reward")))
        {
            if ((reward.Problem(cards: false) ?? RewardProblem(reward)) is { } problem)
                return $"{name}: {problem}";
        }
        return null;
    }

    private static string? RewardProblem(Reward reward) =>
        reward.FcPoints != 0 || reward.Xp != 0 ? "Draft pays coins and packs only"
        : reward.Lines.Any(line => line.Source is not null) ? "a Draft line is granted under its mode's source"
        : null;

    /// <summary>These rules with their reward levels, validated: each mode has one level for every wins count from 0 to winsToComplete.</summary>
    /// <exception cref="InvalidDataException">The levels are incomplete or invalid.</exception>
    public DraftRules WithRewards(IEnumerable<DraftRewardLevel> levels)
    {
        var list = levels.OrderBy(level => level.Mode == DraftRewardLevel.Single).ThenBy(level => level.Wins).ToList();
        foreach (var level in list)
        {
            if (LevelProblem(level) is { } problem)
                throw new InvalidDataException($"{level.Mode} {level.Wins} wins: {problem}");
        }
        foreach (var mode in new[] { DraftRewardLevel.Online, DraftRewardLevel.Single })
        {
            var wins = list.Where(level => level.Mode == mode).Select(level => (long)level.Wins).ToList();
            if (!wins.SequenceEqual(Enumerable.Range(0, (int)WinsToComplete + 1).Select(win => (long)win)))
                throw new InvalidDataException($"{mode} needs one level for each of 0 to {WinsToComplete} wins");
        }
        RewardLevels = list;
        return this;
    }

    /// <summary>
    /// A finished run's reward: its level (wins past the table use the last) and one choice drawn by weight from the run's
    /// seed, so the same run always draws the same choice. Difficulty does not change it.
    /// </summary>
    public DraftRunPayout RunReward(long wins, bool online, long seed)
    {
        var mode = online ? DraftRewardLevel.Online : DraftRewardLevel.Single;
        var count = (int)Math.Clamp(wins, 0, WinsToComplete);
        var level = RewardLevel(mode, count) ?? throw new InvalidOperationException($"No Draft reward level for {mode} {count} wins.");
        var index = GameRandom.Seeded("draft-reward", seed, mode, count).WeightedChoice(
            Enumerable.Range(0, level.Choices.Count).ToList(), level.Choices.Select(choice => (double)choice.Weight).ToList());
        return new DraftRunPayout(level.Reward, index + 1, level.Choices[index].Reward, DraftRewardLevel.Source(mode));
    }

    public void OnDeserialized()
    {
        if (!double.IsFinite(Picks.SpecialBias) || Picks.SpecialBias <= 0 || !double.IsFinite(Picks.WeakRoundSpecialBias) || Picks.WeakRoundSpecialBias <= 0
            || Picks.CategoryWeights.Concat(Picks.WeakRoundCategoryWeights).Any(p => !double.IsFinite(p.Value) || p.Value < 0)
            || Picks.CategoryWeights.Count > 200 || Picks.WeakRoundCategoryWeights.Count > 200)
            throw new InvalidDataException("Draft category weights must be finite and nonnegative, with positive fallback special weights.");
        foreach (var weights in new[] { Picks.GoldRareBucketWeights, Picks.WeakRoundGoldRareBucketWeights })
            if (weights.Count > 200 || weights.Values.Any(w => !double.IsFinite(w) || w < 0) || (weights.Count > 0 && weights.Values.Sum() <= 0))
                throw new InvalidDataException("Draft Gold Rare group weights must be finite and nonnegative, with at least one positive.");
    }

    public required List<string> Difficulties { get; set; }
    public required Dictionary<string, string> DifficultyAliases { get; set; }
    public required int FormationChoiceCount { get; set; }
    public required List<DraftFormation> Formations { get; set; }
    public required string DefaultFormation { get; set; }
    public required Dictionary<string, List<string>> PositionAliases { get; set; }
    public required Dictionary<long, List<string>> BenchSlots { get; set; }
    public required DraftPickRules Picks { get; set; }
}
