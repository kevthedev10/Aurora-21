using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>The Division Rivals reward tables (migration 131) and the tables of every mode together.</summary>
public static partial class ModeRewardRepository
{
    public const string RivalsSeedFile = "rivals.json";

    /// <summary>Rank I to VI as FIFA names them.</summary>
    public static string RivalsRankName(int rank) => rank switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", _ => rank.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>Every mode's bundled seed: what a fresh database is filled with, and what file-only rule loads use.</summary>
    public static ModeRewardTables Seeds(string dataRoot) => new(ChampionsSeed(dataRoot), RivalsSeed(dataRoot), SquadBattlesSeed(dataRoot), DraftSeed(dataRoot));

    /// <summary>Every mode's reward tables as stored.</summary>
    public static async ValueTask<ModeRewardTables> TablesAsync(IUnitOfWork work) =>
        new(await ChampionsRanksAsync(work), await RivalsTablesAsync(work), await SquadBattlesTiersAsync(work), await DraftLevelsAsync(work));

    /// <exception cref="InvalidDataException">The seed is missing or does not read.</exception>
    public static RivalsRewardTables RivalsSeed(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "rewards", RivalsSeedFile);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Invalid reward seed {path}: {error.Message}", error);
        }
        return RulesJson.Deserialize<RivalsRewardTables>(text, path);
    }

    /// <summary>The Rivals reward options by division and rank (Rank I first), and the promotion rewards by division.</summary>
    public static async ValueTask<RivalsRewardTables> RivalsTablesAsync(IUnitOfWork work)
    {
        var options = await work.QueryAsync("""
            SELECT division, rank, option, reward_id FROM ultimateteam.rivals_reward_options ORDER BY division, rank, option
            """, row => (Division: row.GetInt32(0), Rank: row.GetInt32(1), Option: row.GetInt32(2), Reward: row.GetInt64(3)));
        var promotion = await work.QueryAsync("SELECT division, reward_id FROM ultimateteam.rivals_promotion_rewards ORDER BY division",
            row => (Division: row.GetInt32(0), Reward: row.GetInt64(1)));
        var rewards = await RewardsAsync(work, options.Select(row => row.Reward).Concat(promotion.Select(row => row.Reward)).ToArray());
        var divisions = new Dictionary<int, List<List<Reward>>>();
        foreach (var row in options)
        {
            var ranks = divisions.TryGetValue(row.Division, out var existing) ? existing : divisions[row.Division] = new List<List<Reward>>();
            while (ranks.Count < row.Rank) ranks.Add(new List<Reward>());
            ranks[row.Rank - 1].Add(rewards[row.Reward]);
        }
        return new RivalsRewardTables
        {
            Divisions = divisions,
            Promotion = promotion.ToDictionary(row => row.Division, row => rewards[row.Reward]),
        };
    }

    /// <summary>One rank's options in order, or null when the rank has none.</summary>
    public static async ValueTask<List<Reward>?> RivalsRankAsync(IUnitOfWork work, int division, int rank)
    {
        var ids = await work.QueryAsync("""
            SELECT reward_id FROM ultimateteam.rivals_reward_options WHERE division=@division AND rank=@rank ORDER BY option
            """, row => row.GetInt64(0), ("division", division), ("rank", rank));
        if (ids.Count == 0) return null;
        var rewards = await RewardsAsync(work, ids.ToArray());
        return ids.Select(id => rewards[id]).ToList();
    }

    /// <summary>Replaces one rank's options (1-4); their rewards are replaced with them.</summary>
    public static async ValueTask WriteRivalsRankAsync(IUnitOfWork work, int division, int rank, IReadOnlyList<Reward> options)
    {
        var old = await work.QueryAsync("""
            DELETE FROM ultimateteam.rivals_reward_options WHERE division=@division AND rank=@rank RETURNING reward_id
            """, row => row.GetInt64(0), ("division", division), ("rank", rank));
        foreach (var id in old) await work.ExecuteAsync("DELETE FROM ultimateteam.rewards WHERE reward_id=@id", ("id", id));
        foreach (var (reward, option) in options.Select((reward, index) => (reward, index + 1)))
        {
            var id = await InsertRewardAsync(work, reward);
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.rivals_reward_options(division, rank, option, reward_id) VALUES(@division, @rank, @option, @reward)
                """, ("division", division), ("rank", rank), ("option", option), ("reward", id));
        }
    }

    /// <summary>A division's promotion reward, or null.</summary>
    public static async ValueTask<Reward?> RivalsPromotionAsync(IUnitOfWork work, int division)
    {
        var ids = await work.QueryAsync("SELECT reward_id FROM ultimateteam.rivals_promotion_rewards WHERE division=@division",
            row => row.GetInt64(0), ("division", division));
        return ids.Count == 0 ? null : (await RewardsAsync(work, ids.ToArray()))[ids[0]];
    }

    /// <summary>Adds or replaces a division's promotion reward.</summary>
    public static async ValueTask WriteRivalsPromotionAsync(IUnitOfWork work, int division, Reward reward)
    {
        var existing = await work.QueryAsync("SELECT reward_id FROM ultimateteam.rivals_promotion_rewards WHERE division=@division",
            row => row.GetInt64(0), ("division", division));
        if (existing.Count > 0)
        {
            await ReplaceRewardAsync(work, existing[0], reward);
            return;
        }
        var id = await InsertRewardAsync(work, reward);
        await work.ExecuteAsync("INSERT INTO ultimateteam.rivals_promotion_rewards(division, reward_id) VALUES(@division, @reward)",
            ("division", division), ("reward", id));
    }

    /// <summary>
    /// Start-up: fills empty Rivals tables once. A database whose rules still hold rivals_rewards.json divisions and promotion
    /// (before migration 131) has them converted, each pack line with the tradeability it was paid with, and those rule rows
    /// removed: weekly options paid rules/tradeability.json for their option's source (fixed, A, B, C by position), promotion
    /// the pack's flag or the rivals_promotion switch. A line keeps its stored grant source. A fresh database gets the seed.
    /// </summary>
    public static async ValueTask<string?> FillRivalsAsync(IUnitOfWork work, string dataRoot, ILogger log)
    {
        await work.ExecuteAsync("LOCK TABLE ultimateteam.rivals_reward_options IN EXCLUSIVE MODE");
        if (await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.rivals_reward_options") > 0)
        {
            return null;
        }
        var documents = await RuleRows.DocumentsAsync(work);
        RivalsRewardTables tables;
        string done;
        if (documents.TryGetValue("rivals_rewards", out var stored) && JsonNode.Parse(stored) is JsonObject old
            && old["divisions"] is JsonObject oldDivisions && old["promotion"] is JsonObject oldPromotion)
        {
            var tradeability = RulesJson.Deserialize<TradeabilityRules>(documents["tradeability"], "rules/tradeability.json in the database");
            tables = new RivalsRewardTables
            {
                Divisions = oldDivisions.ToDictionary(pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture),
                    pair => ((JsonArray)pair.Value!).OfType<JsonArray>().Select(options =>
                    {
                        var list = options.OfType<JsonObject>().ToList();
                        return list.Select((option, index) => ConvertRivalsReward(option,
                            pack => tradeability.Untradeable(RewardSources.RivalsOption(index, list.Count), pack.Int("packId")))).ToList();
                    }).ToList()),
                Promotion = oldPromotion.ToDictionary(pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture),
                    pair => ConvertRivalsReward((JsonObject)pair.Value!,
                        pack => pack.Bool("untradeable") || tradeability.Untradeable(RewardSources.RivalsPromotion, pack.Int("packId")))),
            };
            await work.ExecuteAsync("DELETE FROM ultimateteam.content_rules WHERE file='rivals_rewards' AND path->>0 IN ('divisions', 'promotion')");
            done = "converted the Division Rivals rewards from the stored rules";
        }
        else
        {
            tables = RivalsSeed(dataRoot);
            done = "filled the Division Rivals rewards from the bundled seed";
        }
        foreach (var (division, ranks) in tables.Divisions)
        {
            foreach (var (options, rank) in ranks.Select((options, index) => (options, index + 1)))
            {
                await WriteRivalsRankAsync(work, division, rank, options);
            }
        }
        foreach (var (division, reward) in tables.Promotion)
        {
            await WriteRivalsPromotionAsync(work, division, reward);
        }
        log.LogInformation("REWARDS {Done}", done);
        return done;
    }

    /// <summary>A rivals_rewards.json reward (before migration 131) in the shared format.</summary>
    private static Reward ConvertRivalsReward(JsonObject option, Func<JsonObject, bool> untradeable) => new()
    {
        Coins = option.Int("coins"),
        FcPoints = option.Int("fcPoints"),
        Lines = (option.Arr("packs") ?? new JsonArray()).OfType<JsonObject>().Select(pack => new RewardLine
        {
            PackId = pack.Int("packId"),
            Count = pack.Int("count", 1),
            Untradeable = untradeable(pack),
            Source = pack.Str("source") is { Length: > 0 } source ? source : null,
        }).ToList(),
    };
}
