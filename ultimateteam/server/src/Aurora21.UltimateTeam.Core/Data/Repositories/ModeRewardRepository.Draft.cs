using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>The bundled seed of the Draft reward levels (runtime-source/ultimateteam21/rewards/draft.json).</summary>
public sealed class DraftRewardSeed
{
    public required List<DraftRewardLevel> Levels { get; set; }
}

/// <summary>The FUT Draft reward levels and their choices (migration 135).</summary>
public static partial class ModeRewardRepository
{
    public const string DraftSeedFile = "draft.json";

    /// <exception cref="InvalidDataException">The seed is missing or does not read.</exception>
    public static List<DraftRewardLevel> DraftSeed(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "rewards", DraftSeedFile);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Invalid reward seed {path}: {error.Message}", error);
        }
        return RulesJson.Deserialize<DraftRewardSeed>(text, path).Levels;
    }

    /// <summary>The Draft reward levels, online first, each by wins with its choices in order.</summary>
    public static async ValueTask<List<DraftRewardLevel>> DraftLevelsAsync(IUnitOfWork work)
    {
        var levels = await work.QueryAsync("""
            SELECT mode, wins, reward_id FROM ultimateteam.draft_reward_levels ORDER BY mode = 'single', wins
            """, row => (Mode: row.GetString(0), Wins: row.GetInt32(1), Reward: row.GetInt64(2)));
        var choices = await work.QueryAsync("""
            SELECT mode, wins, weight, reward_id FROM ultimateteam.draft_reward_choices ORDER BY mode, wins, choice
            """, row => (Mode: row.GetString(0), Wins: row.GetInt32(1), Weight: row.GetInt32(2), Reward: row.GetInt64(3)));
        var rewards = await RewardsAsync(work, levels.Select(row => row.Reward).Concat(choices.Select(row => row.Reward)).ToArray());
        return levels.Select(level => new DraftRewardLevel
        {
            Mode = level.Mode, Wins = level.Wins, Reward = rewards[level.Reward],
            Choices = choices.Where(choice => choice.Mode == level.Mode && choice.Wins == level.Wins)
                .Select(choice => new DraftRewardChoice { Weight = choice.Weight, Reward = rewards[choice.Reward] }).ToList(),
        }).ToList();
    }

    public static async ValueTask<DraftRewardLevel?> DraftLevelAsync(IUnitOfWork work, string mode, int wins) =>
        (await DraftLevelsAsync(work)).FirstOrDefault(level => level.Mode == mode && level.Wins == wins);

    /// <summary>Adds a level, or replaces its reward and all its choices.</summary>
    public static async ValueTask WriteDraftLevelAsync(IUnitOfWork work, DraftRewardLevel level)
    {
        var existing = await work.QueryAsync("SELECT reward_id FROM ultimateteam.draft_reward_levels WHERE mode=@mode AND wins=@wins",
            row => row.GetInt64(0), ("mode", level.Mode), ("wins", level.Wins));
        if (existing.Count > 0)
        {
            await ReplaceRewardAsync(work, existing[0], level.Reward);
            var old = await work.QueryAsync("""
                DELETE FROM ultimateteam.draft_reward_choices WHERE mode=@mode AND wins=@wins RETURNING reward_id
                """, row => row.GetInt64(0), ("mode", level.Mode), ("wins", level.Wins));
            await work.ExecuteAsync("DELETE FROM ultimateteam.rewards WHERE reward_id = ANY(@ids)", ("ids", old.ToArray()));
        }
        else
        {
            await work.ExecuteAsync("INSERT INTO ultimateteam.draft_reward_levels(mode, wins, reward_id) VALUES(@mode, @wins, @reward)",
                ("mode", level.Mode), ("wins", level.Wins), ("reward", await InsertRewardAsync(work, level.Reward)));
        }
        foreach (var (choice, number) in level.Choices.Select((choice, index) => (choice, index + 1)))
        {
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.draft_reward_choices(mode, wins, choice, weight, reward_id) VALUES(@mode, @wins, @choice, @weight, @reward)
                """, ("mode", level.Mode), ("wins", level.Wins), ("choice", number), ("weight", choice.Weight),
                ("reward", await InsertRewardAsync(work, choice.Reward)));
        }
    }

    /// <summary>
    /// Start-up: fills empty Draft reward levels once from the bundled seed. Nothing is converted: the owner replaced the old
    /// draft.json runRewards (removed by migration 135) with FIFA 21's published bundles.
    /// </summary>
    public static async ValueTask<string?> FillDraftAsync(IUnitOfWork work, string dataRoot, ILogger log)
    {
        await work.ExecuteAsync("LOCK TABLE ultimateteam.draft_reward_levels IN EXCLUSIVE MODE");
        if (await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.draft_reward_levels") > 0)
        {
            return null;
        }
        var levels = DraftSeed(dataRoot);
        foreach (var level in levels)
        {
            await WriteDraftLevelAsync(work, level);
        }
        var done = $"filled {levels.Count} Draft reward levels ({levels.Sum(level => level.Choices.Count)} choices) from the bundled seed";
        log.LogInformation("REWARDS {Done}", done);
        return done;
    }

    /// <summary>A level's name: "Online, 3 wins".</summary>
    public static string DraftLevelName(string mode, int wins) =>
        $"{(mode == DraftRewardLevel.Online ? "Online" : "Single-player")}, {wins} {(wins == 1 ? "win" : "wins")}";
}
