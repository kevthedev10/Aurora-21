using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>The bundled seed of the Squad Battles reward tiers (runtime-source/ultimateteam21/rewards/squad_battles.json).</summary>
public sealed class SquadBattlesRewardSeed
{
    public required List<SquadBattlesRewardTier> Tiers { get; set; }
}

/// <summary>The Squad Battles reward tiers (migration 133).</summary>
public static partial class ModeRewardRepository
{
    public const string SquadBattlesSeedFile = "squad_battles.json";

    /// <exception cref="InvalidDataException">The seed is missing or does not read.</exception>
    public static List<SquadBattlesRewardTier> SquadBattlesSeed(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "rewards", SquadBattlesSeedFile);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Invalid reward seed {path}: {error.Message}", error);
        }
        return RulesJson.Deserialize<SquadBattlesRewardSeed>(text, path).Tiers;
    }

    /// <summary>The Squad Battles tiers: Top slots by position, then the points tiers by tierLevel.</summary>
    public static async ValueTask<List<SquadBattlesRewardTier>> SquadBattlesTiersAsync(IUnitOfWork work)
    {
        var rows = await work.QueryAsync("""
            SELECT tier_id, kind, rank_start, rank_end, tier_level, percent_start, percent_end, min_points, reward_id
            FROM ultimateteam.squad_battles_reward_tiers ORDER BY kind = 'points', rank_start, tier_level, tier_id
            """, row => (Id: row.GetString(0), Kind: row.GetString(1), RankStart: row.GetInt32(2), RankEnd: row.GetInt32(3), TierLevel: row.GetInt32(4),
                PercentStart: row.GetInt32(5), PercentEnd: row.GetInt32(6), MinPoints: row.GetInt64(7), Reward: row.GetInt64(8)));
        var rewards = await RewardsAsync(work, rows.Select(row => row.Reward).ToArray());
        return rows.Select(row => new SquadBattlesRewardTier
        {
            Id = row.Id, Kind = row.Kind, RankStart = row.RankStart, RankEnd = row.RankEnd, TierLevel = row.TierLevel,
            PercentStart = row.PercentStart, PercentEnd = row.PercentEnd, MinPoints = row.MinPoints, Reward = rewards[row.Reward],
        }).ToList();
    }

    public static async ValueTask<SquadBattlesRewardTier?> SquadBattlesTierAsync(IUnitOfWork work, string id) =>
        (await SquadBattlesTiersAsync(work)).FirstOrDefault(tier => tier.Id == id);

    /// <summary>Adds a tier, or replaces the one with its id (its ranges and reward).</summary>
    public static async ValueTask WriteSquadBattlesTierAsync(IUnitOfWork work, SquadBattlesRewardTier tier)
    {
        var existing = await work.QueryAsync("SELECT reward_id FROM ultimateteam.squad_battles_reward_tiers WHERE tier_id=@id",
            row => row.GetInt64(0), ("id", tier.Id));
        var rewardId = existing.Count > 0 ? existing[0] : await InsertRewardAsync(work, tier.Reward);
        if (existing.Count > 0)
        {
            await ReplaceRewardAsync(work, rewardId, tier.Reward);
        }
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.squad_battles_reward_tiers(tier_id, kind, rank_start, rank_end, tier_level, percent_start, percent_end, min_points, reward_id)
            VALUES(@id, @kind, @rankStart, @rankEnd, @level, @percentStart, @percentEnd, @minPoints, @reward)
            ON CONFLICT(tier_id) DO UPDATE SET kind=excluded.kind, rank_start=excluded.rank_start, rank_end=excluded.rank_end,
                tier_level=excluded.tier_level, percent_start=excluded.percent_start, percent_end=excluded.percent_end, min_points=excluded.min_points
            """, ("id", tier.Id), ("kind", tier.Kind), ("rankStart", tier.RankStart), ("rankEnd", tier.RankEnd), ("level", tier.TierLevel),
            ("percentStart", tier.PercentStart), ("percentEnd", tier.PercentEnd), ("minPoints", tier.MinPoints), ("reward", rewardId));
    }

    /// <summary>
    /// Places the six Top slots for a Top size (squad_battles.json competition.boardSize): Rank 1, then the other places split
    /// over five slots, in the slots' current order. Called when the Top size is published and when the tables are filled.
    /// </summary>
    public static async ValueTask PlaceSquadBattlesSlotsAsync(IUnitOfWork work, int size)
    {
        var slots = await work.QueryAsync("""
            SELECT tier_id FROM ultimateteam.squad_battles_reward_tiers WHERE kind = 'ranked' ORDER BY rank_start, tier_id
            """, row => row.GetString(0));
        if (slots.Count != SquadBattlesRewardRules.RankedSlots)
            throw new InvalidDataException($"Squad Battles needs exactly {SquadBattlesRewardRules.RankedSlots} Top slots before the Top size changes.");
        var bands = LeaderboardSizes.ChampionsBands(size);
        foreach (var (id, (low, high)) in slots.Zip(bands))
        {
            await work.ExecuteAsync("UPDATE ultimateteam.squad_battles_reward_tiers SET rank_start=@low, rank_end=@high WHERE tier_id=@id",
                ("low", (int)low), ("high", (int)high), ("id", id));
        }
    }

    /// <summary>
    /// Start-up: fills empty Squad Battles tiers once. A database whose rules still hold sqbt_rewards.json tiers (before migration
    /// 133) has them converted: each pack line with the tradeability it was paid with (rules/tradeability.json for the
    /// squad_battles source), the coin awards as the reward's coins, and the Top tiers made six slots (today's five get "ranked5",
    /// a copy of top100's prize, before top200) placed by the Top size; those rule rows are removed. A fresh database gets the seed.
    /// </summary>
    public static async ValueTask<string?> FillSquadBattlesAsync(IUnitOfWork work, string dataRoot, ILogger log)
    {
        await work.ExecuteAsync("LOCK TABLE ultimateteam.squad_battles_reward_tiers IN EXCLUSIVE MODE");
        if (await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.squad_battles_reward_tiers") > 0)
        {
            return null;
        }
        var documents = await RuleRows.DocumentsAsync(work);
        List<SquadBattlesRewardTier> tiers;
        string done;
        var convert = documents.TryGetValue("sqbt_rewards", out var stored) && JsonNode.Parse(stored)?["tiers"] is JsonArray;
        if (convert)
        {
            var tradeability = RulesJson.Deserialize<TradeabilityRules>(documents["tradeability"], "rules/tradeability.json in the database");
            tiers = ((JsonArray)JsonNode.Parse(stored!)!["tiers"]!).OfType<JsonObject>().Select(tier => ConvertSquadBattlesTier(tier, tradeability)).ToList();
            var ranked = tiers.Where(tier => tier.Ranked).ToList();
            var number = 5;
            while (ranked.Count < SquadBattlesRewardRules.RankedSlots)
            {
                var copyOf = ranked[Math.Max(0, ranked.Count - 2)];
                while (tiers.Any(tier => tier.Id == $"ranked{number}")) number++;
                ranked.Insert(Math.Max(1, ranked.Count - 1), new SquadBattlesRewardTier
                {
                    Id = $"ranked{number}", Kind = "ranked", Reward = Reward.FromJson(copyOf.Reward.ToJson(), copyOf.Id),
                });
            }
            while (ranked.Count > SquadBattlesRewardRules.RankedSlots) ranked.RemoveAt(ranked.Count - 2);
            // Order the slots for placing: each gets a provisional start by its position in the list.
            foreach (var (tier, index) in ranked.Select((tier, index) => (tier, index))) (tier.RankStart, tier.RankEnd) = (index + 1, index + 1);
            tiers = ranked.Concat(tiers.Where(tier => !tier.Ranked)).ToList();
            await work.ExecuteAsync("DELETE FROM ultimateteam.content_rules WHERE file='sqbt_rewards' AND path->>0 IN ('tiers', 'leaderboardBands')");
            done = $"converted {tiers.Count} Squad Battles reward tiers from the stored rules (six Top slots)";
        }
        else
        {
            tiers = SquadBattlesSeed(dataRoot);
            done = $"filled {tiers.Count} Squad Battles reward tiers from the bundled seed";
        }
        foreach (var tier in tiers)
        {
            await WriteSquadBattlesTierAsync(work, tier);
        }
        if (convert)
        {
            var size = documents.TryGetValue("squad_battles", out var battles) && JsonNode.Parse(battles)?["competition"]?["boardSize"] is JsonValue value
                && value.TryGetValue(out int board) ? Math.Clamp(board, LeaderboardSizes.ChampionsMinimum, LeaderboardSizes.Maximum) : 25;
            await PlaceSquadBattlesSlotsAsync(work, size);
        }
        log.LogInformation("REWARDS {Done}", done);
        return done;
    }

    private static SquadBattlesRewardTier ConvertSquadBattlesTier(JsonObject tier, TradeabilityRules tradeability)
    {
        var awards = (tier.Arr("awards") ?? new JsonArray()).OfType<JsonObject>().ToList();
        return new SquadBattlesRewardTier
        {
            Id = tier.Text("id"), Kind = tier.Text("kind"),
            RankStart = (int)tier.Int("rankStart"), RankEnd = (int)tier.Int("rankEnd"), TierLevel = (int)tier.Int("tierLevel"),
            PercentStart = (int)tier.Int("percentStart"), PercentEnd = (int)tier.Int("percentEnd"), MinPoints = tier.Int("minPoints"),
            Reward = new Reward
            {
                Coins = awards.Where(award => award.Text("type") == "coin").Sum(award => award.Int("amount")),
                Lines = awards.Where(award => award.Text("type") == "pack").Select(award => new RewardLine
                {
                    PackId = award.Int("packId"),
                    Count = award.Int("count", 1),
                    Untradeable = tradeability.Untradeable(RewardSources.SquadBattles, award.Int("packId")),
                }).ToList(),
            },
        };
    }

    /// <summary>A Top slot's name from its places: "Rank 1", "Rank 2-5".</summary>
    public static string SquadBattlesSlotName(int low, int high) => LeaderboardSizes.Name(low, high);
}
