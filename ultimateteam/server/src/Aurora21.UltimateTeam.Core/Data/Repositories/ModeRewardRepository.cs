using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>The bundled seed of the FUT Champions reward ranks (runtime-source/ultimateteam21/rewards/champions.json).</summary>
public sealed class ChampionsRewardSeed
{
    public required List<ChampionsRank> Ranks { get; set; }
}

/// <summary>
/// The mode reward tables (migration 126, todo.md section 2): the shared rewards and reward_lines, and each mode's tier table
/// naming them (champions_reward_ranks first). The content snapshot reads them in the rules' transaction; the operator panel
/// changes them through content changesets; start-up fills them once, from the stored rules or the bundled seed.
/// </summary>
public static partial class ModeRewardRepository
{
    public const string ChampionsSeedFile = "champions.json";

    /// <summary>The bundled Champions ranks: what a fresh database is filled with, and what file-only rule loads use.</summary>
    /// <exception cref="InvalidDataException">The seed is missing or does not read.</exception>
    public static List<ChampionsRank> ChampionsSeed(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "rewards", ChampionsSeedFile);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Invalid reward seed {path}: {error.Message}", error);
        }
        return RulesJson.Deserialize<ChampionsRewardSeed>(text, path).Ranks;
    }

    /// <summary>The Champions reward ranks, by tierLevel.</summary>
    public static async ValueTask<List<ChampionsRank>> ChampionsRanksAsync(IUnitOfWork work)
    {
        var rows = await work.QueryAsync("""
            SELECT tier_level, name, wins_low, wins_high, positions_low, positions_high, reward_id
            FROM ultimateteam.champions_reward_ranks ORDER BY tier_level
            """, row => (Tier: row.GetInt32(0), Name: row.GetString(1), WinsLow: Nullable(row, 2), WinsHigh: Nullable(row, 3),
                PositionsLow: Nullable(row, 4), PositionsHigh: Nullable(row, 5), RewardId: row.GetInt64(6)));
        var rewards = await RewardsAsync(work, rows.Select(row => row.RewardId).ToArray());
        return rows.Select(row => new ChampionsRank
        {
            TierLevel = row.Tier,
            Name = row.Name,
            Wins = row.WinsLow is { } low ? [low, row.WinsHigh!.Value] : null,
            Positions = row.PositionsLow is { } first ? [first, row.PositionsHigh!.Value] : null,
            Reward = rewards[row.RewardId],
        }).ToList();
    }

    /// <summary>One Champions rank, or null when no rank has that tierLevel.</summary>
    public static async ValueTask<ChampionsRank?> ChampionsRankAsync(IUnitOfWork work, int tierLevel) =>
        (await ChampionsRanksAsync(work)).FirstOrDefault(rank => rank.TierLevel == tierLevel);

    /// <summary>Adds a Champions rank, or replaces the one with its tierLevel (name, range and reward).</summary>
    public static async ValueTask WriteChampionsRankAsync(IUnitOfWork work, ChampionsRank rank)
    {
        var existing = await work.QueryAsync("SELECT reward_id FROM ultimateteam.champions_reward_ranks WHERE tier_level=@tier",
            row => row.GetInt64(0), ("tier", rank.TierLevel));
        var rewardId = existing.Count > 0 ? existing[0] : await InsertRewardAsync(work, rank.Reward);
        if (existing.Count > 0)
        {
            await ReplaceRewardAsync(work, rewardId, rank.Reward);
        }
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.champions_reward_ranks(tier_level, name, wins_low, wins_high, positions_low, positions_high, reward_id)
            VALUES(@tier, @name, @winsLow, @winsHigh, @positionsLow, @positionsHigh, @reward)
            ON CONFLICT(tier_level) DO UPDATE SET name=excluded.name, wins_low=excluded.wins_low, wins_high=excluded.wins_high,
                positions_low=excluded.positions_low, positions_high=excluded.positions_high
            """, ("tier", rank.TierLevel), ("name", rank.Name), ("winsLow", rank.Wins?[0]), ("winsHigh", rank.Wins?[1]),
            ("positionsLow", rank.Positions?[0]), ("positionsHigh", rank.Positions?[1]), ("reward", rewardId));
    }

    /// <summary>Removes a Champions rank and its reward. Settled results keep the copy of the reward they stored.</summary>
    public static async ValueTask RemoveChampionsRankAsync(IUnitOfWork work, int tierLevel)
    {
        var removed = await work.QueryAsync("DELETE FROM ultimateteam.champions_reward_ranks WHERE tier_level=@tier RETURNING reward_id",
            row => row.GetInt64(0), ("tier", tierLevel));
        foreach (var rewardId in removed)
        {
            await work.ExecuteAsync("DELETE FROM ultimateteam.rewards WHERE reward_id=@id", ("id", rewardId));
        }
    }

    /// <summary>
    /// Where the reward tables name each pack, as readable labels ("Champions: Rank 1"): the operator panel's pack usage and
    /// its delete check (the reward_lines foreign key refuses the delete anyway).
    /// </summary>
    public static async ValueTask<Dictionary<long, List<string>>> PackUsesAsync(IUnitOfWork work)
    {
        var rows = await work.QueryAsync("""
            SELECT DISTINCT l.pack_id, 0 AS mode, c.tier_level AS a, 0 AS b, c.name FROM ultimateteam.reward_lines l
            JOIN ultimateteam.champions_reward_ranks c ON c.reward_id = l.reward_id WHERE l.pack_id IS NOT NULL
            UNION
            SELECT DISTINCT l.pack_id, 1, r.division, r.rank, '' FROM ultimateteam.reward_lines l
            JOIN ultimateteam.rivals_reward_options r ON r.reward_id = l.reward_id WHERE l.pack_id IS NOT NULL
            UNION
            SELECT DISTINCT l.pack_id, 2, p.division, 0, '' FROM ultimateteam.reward_lines l
            JOIN ultimateteam.rivals_promotion_rewards p ON p.reward_id = l.reward_id WHERE l.pack_id IS NOT NULL
            UNION
            SELECT DISTINCT l.pack_id, 3, CASE WHEN t.kind = 'ranked' THEN t.rank_start ELSE 1000 + t.tier_level END, t.rank_end,
                CASE WHEN t.kind = 'ranked' THEN '' ELSE t.tier_id END
            FROM ultimateteam.reward_lines l JOIN ultimateteam.squad_battles_reward_tiers t ON t.reward_id = l.reward_id WHERE l.pack_id IS NOT NULL
            UNION
            SELECT DISTINCT l.pack_id, 4, CASE WHEN d.mode = 'single' THEN 100 ELSE 0 END + d.wins, 0, d.mode FROM ultimateteam.reward_lines l
            JOIN (SELECT mode, wins, reward_id FROM ultimateteam.draft_reward_levels
                  UNION ALL SELECT mode, wins, reward_id FROM ultimateteam.draft_reward_choices) d ON d.reward_id = l.reward_id
            WHERE l.pack_id IS NOT NULL
            ORDER BY 1, 2, 3, 4
            """, row => (Pack: row.GetInt64(0), Label: row.GetInt32(1) switch
            {
                0 => $"Champions: {row.GetString(4)}",
                1 => $"Rivals: Division {row.GetInt32(2)} Rank {RivalsRankName(row.GetInt32(3))}",
                2 => $"Rivals promotion: Division {row.GetInt32(2)}",
                4 => $"Draft: {DraftLevelName(row.GetString(4), row.GetInt32(2) % 100)}",
                _ => row.GetInt32(2) < 1000 ? $"Squad Battles: {SquadBattlesSlotName(row.GetInt32(2), row.GetInt32(3))}" : $"Squad Battles: {row.GetString(4)}",
            }));
        return rows.GroupBy(row => row.Pack).ToDictionary(group => group.Key, group => group.Select(row => row.Label).Distinct().ToList());
    }

    /// <summary>
    /// Start-up: fills empty reward tables once. A database whose rules still hold champions_rewards.json ranks (before
    /// migration 126) has them converted, each pack line with the tradeability it was paid with (rules/tradeability.json for
    /// the weekend_league source; the old per-pack flag was never read), and those rule rows removed. A fresh database gets
    /// the bundled seed. Returns what it did, or null when the tables were already filled.
    /// </summary>
    public static async ValueTask<string?> FillAsync(IUnitOfWork work, string dataRoot, ILogger log)
    {
        await work.ExecuteAsync("LOCK TABLE ultimateteam.champions_reward_ranks IN EXCLUSIVE MODE");
        if (await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.champions_reward_ranks") > 0)
        {
            return null;
        }
        var documents = await RuleRows.DocumentsAsync(work);
        List<ChampionsRank> ranks;
        string done;
        if (documents.TryGetValue("champions_rewards", out var stored) && JsonNode.Parse(stored)?["ranks"] is JsonArray old)
        {
            var tradeability = RulesJson.Deserialize<TradeabilityRules>(documents["tradeability"], "rules/tradeability.json in the database");
            ranks = old.OfType<JsonObject>().Select(rank => ConvertChampionsRank(rank, tradeability)).ToList();
            await work.ExecuteAsync("DELETE FROM ultimateteam.content_rules WHERE file='champions_rewards' AND path->>0='ranks'");
            done = $"converted {ranks.Count} Champions reward ranks from the stored rules";
            done += SplitTopBands(ranks)
                ? "; converted the four default Top 200 bands to the six fixed Top 25 bands"
                : "; the Top 200 bands were not the original four, so they were kept as stored";
        }
        else
        {
            ranks = ChampionsSeed(dataRoot);
            done = $"filled {ranks.Count} Champions reward ranks from the bundled seed";
        }
        foreach (var rank in ranks)
        {
            await WriteChampionsRankAsync(work, rank);
        }
        log.LogInformation("REWARDS {Done}", done);
        return done;
    }

    /// <summary>
    /// Converts the original four default bands to the owner's fixed Top 25 (2026-10-03). Stable tier ids and rewards
    /// stay with their corresponding band; the two additional bands copy the old third and fourth rewards. Operator-edited
    /// position ranges are left alone. Databases with populated six-band tables are upgraded by migration 127.
    /// </summary>
    private static bool SplitTopBands(List<ChampionsRank> ranks)
    {
        var bands = ranks.Where(rank => rank.Positions is not null).OrderBy(rank => rank.TierLevel)
            .Select(rank => (rank.TierLevel, Low: rank.Positions![0], High: rank.Positions[1])).ToList();
        if (!bands.SequenceEqual([(1, 1L, 1L), (2, 2L, 20L), (3, 21L, 100L), (4, 101L, 200L)]) || ranks.Any(rank => rank.TierLevel is 17 or 18))
        {
            return false;
        }
        ChampionsRank Split(int from, int tier, string name, long low, long high)
        {
            var source = ranks.Single(rank => rank.TierLevel == from);
            return new ChampionsRank
            {
                TierLevel = tier, Name = name, Positions = [low, high],
                Reward = Reward.FromJson(source.Reward.ToJson(), $"Champions rank {from}"),
            };
        }
        var second = ranks.Single(rank => rank.TierLevel == 2);
        second.Name = "Rank 2-5";
        second.Positions = [2, 5];
        ranks.Add(Split(3, 17, "Rank 11-15", 11, 15));
        ranks.Add(Split(4, 18, "Rank 21-25", 21, 25));
        var third = ranks.Single(rank => rank.TierLevel == 3);
        third.Name = "Rank 6-10";
        third.Positions = [6, 10];
        var fourth = ranks.Single(rank => rank.TierLevel == 4);
        fourth.Name = "Rank 16-20";
        fourth.Positions = [16, 20];
        return true;
    }

    /// <summary>A champions_rewards.json rank (before migration 126) as a rank of the reward tables.</summary>
    private static ChampionsRank ConvertChampionsRank(JsonObject rank, TradeabilityRules tradeability) => new()
    {
        TierLevel = (int)rank.Int("tierLevel"),
        Name = rank.Text("name"),
        Wins = Range(rank["wins"]),
        Positions = Range(rank["positions"]),
        Reward = new Reward
        {
            Coins = rank.Int("coins"),
            FcPoints = rank.Int("fcPoints"),
            Lines = (rank.Arr("packs") ?? new JsonArray()).OfType<JsonObject>().Select(pack => new RewardLine
            {
                PackId = pack.Int("packId"),
                Count = pack.Int("count", 1),
                Untradeable = tradeability.Untradeable(RewardSources.WeekendLeague, pack.Int("packId")),
            }).ToList(),
        },
    };

    private static List<long>? Range(JsonNode? node) =>
        node is JsonArray range ? range.Select(value => JsonValues.Int(value)).ToList() : null;

    /// <summary>Rewards by id, with their lines in order.</summary>
    public static async ValueTask<Dictionary<long, Reward>> RewardsAsync(IUnitOfWork work, long[] ids)
    {
        var rewards = (await work.QueryAsync("SELECT reward_id, coins, fc_points, xp FROM ultimateteam.rewards WHERE reward_id = ANY(@ids)",
            row => (Id: row.GetInt64(0), Reward: new Reward { Coins = row.GetInt64(1), FcPoints = row.GetInt64(2), Xp = row.GetInt64(3) }),
            ("ids", ids))).ToDictionary(row => row.Id, row => row.Reward);
        var lines = await work.QueryAsync("""
            SELECT reward_id, pack_id, card_resource_id, count, untradeable, source FROM ultimateteam.reward_lines
            WHERE reward_id = ANY(@ids) ORDER BY reward_id, position
            """, row => (Id: row.GetInt64(0), Line: new RewardLine
            {
                PackId = Nullable(row, 1),
                CardResourceId = Nullable(row, 2),
                Count = row.GetInt32(3),
                Untradeable = row.GetBoolean(4),
                Source = row.IsDBNull(5) ? null : row.GetString(5),
            }), ("ids", ids));
        foreach (var (id, line) in lines)
        {
            rewards[id].Lines.Add(line);
        }
        return rewards;
    }

    private static async ValueTask<long> InsertRewardAsync(IUnitOfWork work, Reward reward)
    {
        var id = await work.ScalarAsync<long>("""
            INSERT INTO ultimateteam.rewards(coins, fc_points, xp) VALUES(@coins, @fc, @xp) RETURNING reward_id
            """, ("coins", reward.Coins), ("fc", reward.FcPoints), ("xp", reward.Xp));
        await InsertLinesAsync(work, id, reward);
        return id;
    }

    private static async ValueTask ReplaceRewardAsync(IUnitOfWork work, long id, Reward reward)
    {
        await work.ExecuteAsync("UPDATE ultimateteam.rewards SET coins=@coins, fc_points=@fc, xp=@xp WHERE reward_id=@id",
            ("coins", reward.Coins), ("fc", reward.FcPoints), ("xp", reward.Xp), ("id", id));
        await work.ExecuteAsync("DELETE FROM ultimateteam.reward_lines WHERE reward_id=@id", ("id", id));
        await InsertLinesAsync(work, id, reward);
    }

    private static async ValueTask InsertLinesAsync(IUnitOfWork work, long id, Reward reward)
    {
        foreach (var (line, position) in reward.Lines.Select((line, position) => (line, position)))
        {
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.reward_lines(reward_id, position, pack_id, card_resource_id, count, untradeable, source)
                VALUES(@id, @position, @pack, @card, @count, @untradeable, @source)
                """, ("id", id), ("position", position), ("pack", line.PackId), ("card", line.CardResourceId),
                ("count", (int)line.Count), ("untradeable", line.Untradeable), ("source", line.Source));
        }
    }

    private static long? Nullable(Npgsql.NpgsqlDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : Convert.ToInt64(row.GetValue(ordinal), CultureInfo.InvariantCulture);
}
