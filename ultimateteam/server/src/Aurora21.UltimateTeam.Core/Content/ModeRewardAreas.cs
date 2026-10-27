using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>
/// One FUT Champions reward rank as a content row (migration 126, todo.md section 2), keyed by its stable
/// <c>{"tierLevel": 1}</c>. Its value is the whole rank: <c>{"name", "wins" or "positions": [low, high], "reward"}</c>, the reward
/// in the shared format. Update replaces it, insert adds a tierLevel, hide removes it. Each change is checked on its own when
/// staged; the publish loads the whole table as start-up does, so coverage, bands and FIFA's 12 win ranks are checked together.
/// </summary>
public sealed class ChampionsRankArea : IContentArea
{
    public const string AreaName = "champions_rank";
    public const int MaxTierLevel = 999;

    public string Name => AreaName;

    // After packs (10, 20), so a rank can use a pack created in the same changeset; before rules (100).
    public int ApplyOrder => 30;

    public JsonObject NormalizeKey(JsonObject key)
    {
        if (key["tierLevel"] is not JsonValue value || !value.TryGetValue(out int tier) || tier is < 1 or > MaxTierLevel)
            throw new ContentChangeException(400, $"tierLevel is a whole number from 1 to {MaxTierLevel}.");
        return new JsonObject { ["tierLevel"] = tier };
    }

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key) =>
        await ModeRewardRepository.ChampionsRankAsync(work, Tier(key)) is { } rank ? ToJson(rank) : null;

    public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var existing = await ModeRewardRepository.ChampionsRankAsync(work, Tier(key));
        if (operation == ContentOperation.Hide)
        {
            if (existing?.Positions is not null)
                throw new ContentChangeException(400, "The six Champions leaderboard reward slots cannot be removed.");
            return;
        }
        var rank = Rank(Tier(key), after);
        if (existing?.Positions is not null && (rank.Positions is null || !existing.Positions.SequenceEqual(rank.Positions)))
            throw new ContentChangeException(400, "Leaderboard positions are assigned automatically by the Champions Top size.");
        if (rank.Positions is not null && existing?.Positions is null)
            throw new ContentChangeException(400, "Champions always has six leaderboard reward slots; edit an existing slot.");
        // Top slots are named from their positions; win ranks keep FIFA's tier names. Neither is edited.
        if (existing is not null && rank.Name != existing.Name)
            throw new ContentChangeException(400, "Rank names are set automatically and cannot be changed.");
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        if (operation == ContentOperation.Hide)
        {
            await ModeRewardRepository.RemoveChampionsRankAsync(work, Tier(key));
            return;
        }
        var rank = Rank(Tier(key), after);
        // Packs created in this changeset exist by now; the reward_lines foreign key would refuse any other missing pack.
        var packs = rank.Reward.Lines.Where(line => line.PackId is not null).Select(line => line.PackId!.Value).Distinct().ToArray();
        var known = await work.QueryAsync("SELECT pack_id FROM ultimateteam.store_packs WHERE pack_id = ANY(@ids)", row => row.GetInt64(0), ("ids", packs));
        if (packs.Except(known).FirstOrDefault() is var missing and not 0)
            throw new ContentChangeException(400, $"{rank.Name}: pack {missing} does not exist.");
        await ModeRewardRepository.WriteChampionsRankAsync(work, rank);
    }

    /// <summary>A rank as its content row: the shape the panel edits.</summary>
    public static JsonObject ToJson(ChampionsRank rank)
    {
        var row = new JsonObject { ["name"] = rank.Name };
        if (rank.Wins is { } wins) row["wins"] = new JsonArray(wins[0], wins[1]);
        if (rank.Positions is { } positions) row["positions"] = new JsonArray(positions[0], positions[1]);
        row["reward"] = rank.Reward.ToJson();
        return row;
    }

    /// <summary>A staged rank, read strictly and checked on its own.</summary>
    private static ChampionsRank Rank(int tier, JsonObject? after)
    {
        if (after is null) throw new ContentChangeException(400, "A Champions rank change needs the rank.");
        var row = (JsonObject)after.DeepClone();
        if (row.ContainsKey("tierLevel")) throw new ContentChangeException(400, "The tierLevel is the key; it is not part of the rank.");
        row["tierLevel"] = tier;
        ChampionsRank rank;
        try
        {
            rank = RulesJson.Deserialize<ChampionsRank>(row.ToJsonString(), $"Champions rank {tier}");
        }
        catch (InvalidDataException error)
        {
            throw new ContentChangeException(400, error.Message);
        }
        if (rank.Name.Trim().Length is < 1 or > 64 || rank.Name.Trim() != rank.Name)
            throw new ContentChangeException(400, "A rank needs a name of 1 to 64 characters, without leading or trailing spaces.");
        if ((rank.Wins is null) == (rank.Positions is null))
            throw new ContentChangeException(400, $"{rank.Name}: give either wins or positions.");
        if (rank.Wins is { } wins && (wins.Count != 2 || wins[0] < 0 || wins[0] > wins[1] || wins[1] > 1000))
            throw new ContentChangeException(400, $"{rank.Name}: wins are [low, high], from 0, low not above high.");
        if (rank.Positions is { } positions && (positions.Count != 2 || positions[0] < 1 || positions[0] > positions[1] || positions[1] > 100_000))
            throw new ContentChangeException(400, $"{rank.Name}: positions are [low, high], from 1, low not above high.");
        if (rank.Reward.Problem(cards: false) is { } problem)
            throw new ContentChangeException(400, $"{rank.Name}: {problem}.");
        if (rank.Reward.Xp != 0)
            throw new ContentChangeException(400, $"{rank.Name}: FUT Champions rewards pay no XP.");
        if (rank.Reward.Lines.Any(line => line.Source is not null))
            throw new ContentChangeException(400, $"{rank.Name}: Champions reward lines are granted from the weekend_league source.");
        return rank;
    }

    private static int Tier(JsonObject key) => key["tierLevel"]!.GetValue<int>();
}
