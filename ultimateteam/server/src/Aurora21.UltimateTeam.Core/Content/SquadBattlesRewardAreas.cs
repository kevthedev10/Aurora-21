using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>
/// One Squad Battles reward tier as a content row (migration 133), keyed by its stable <c>{"tierId": "elite1"}</c>. Its value is
/// the whole tier: <c>{"kind", "rankStart", "rankEnd", "tierLevel", "percentStart", "percentEnd", "minPoints", "reward"}</c>. A Top
/// slot's places follow the Top size and a points tier's FIFA level is fixed, so only a points tier's percent band and minimum
/// points, and every tier's reward, change. Update only; the publish checks the whole table as start-up does.
/// </summary>
public sealed class SquadBattlesTierArea : IContentArea
{
    public const string AreaName = "squad_battles_tier";

    public string Name => AreaName;

    // After packs (10, 20), Champions (30) and Rivals (31, 32); before rules (100).
    public int ApplyOrder => 33;

    public JsonObject NormalizeKey(JsonObject key)
    {
        if (key["tierId"] is not JsonValue value || !value.TryGetValue(out string? id) || id is not { Length: > 0 and <= 32 })
            throw new ContentChangeException(400, "tierId is a Squad Battles tier id.");
        return new JsonObject { ["tierId"] = id };
    }

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key) =>
        await ModeRewardRepository.SquadBattlesTierAsync(work, Id(key)) is { } tier ? ToJson(tier) : null;

    public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        if (operation != ContentOperation.Update)
            throw new ContentChangeException(400, "Squad Battles always has its six Top slots and twelve tiers: edit a tier instead.");
        var existing = await ModeRewardRepository.SquadBattlesTierAsync(work, Id(key))
            ?? throw new ContentChangeException(404, "There is no such Squad Battles tier.");
        _ = Tier(key, after, existing);
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var existing = await ModeRewardRepository.SquadBattlesTierAsync(work, Id(key))
            ?? throw new ContentChangeException(404, "There is no such Squad Battles tier.");
        var tier = Tier(key, after, existing);
        await RivalsRewardEdits.RequirePacksAsync(work, [tier.Reward], $"Squad Battles {tier.Id}");
        await ModeRewardRepository.WriteSquadBattlesTierAsync(work, tier);
    }

    public static JsonObject ToJson(SquadBattlesRewardTier tier) => new()
    {
        ["kind"] = tier.Kind, ["rankStart"] = tier.RankStart, ["rankEnd"] = tier.RankEnd, ["tierLevel"] = tier.TierLevel,
        ["percentStart"] = tier.PercentStart, ["percentEnd"] = tier.PercentEnd, ["minPoints"] = tier.MinPoints, ["reward"] = tier.Reward.ToJson(),
    };

    /// <summary>A staged tier, read strictly and checked against the stored one.</summary>
    private static SquadBattlesRewardTier Tier(JsonObject key, JsonObject? after, SquadBattlesRewardTier existing)
    {
        if (after is null) throw new ContentChangeException(400, "A Squad Battles tier change needs the tier.");
        var row = (JsonObject)after.DeepClone();
        if (row.ContainsKey("id")) throw new ContentChangeException(400, "The tierId is the key; it is not part of the tier.");
        row["id"] = Id(key);
        SquadBattlesRewardTier tier;
        try
        {
            tier = RulesJson.Deserialize<SquadBattlesRewardTier>(row.ToJsonString(), $"Squad Battles tier {Id(key)}");
        }
        catch (InvalidDataException error)
        {
            throw new ContentChangeException(400, error.Message);
        }
        if (tier.Kind != existing.Kind)
            throw new ContentChangeException(400, "A tier's kind cannot change.");
        if (tier.Ranked && (tier.RankStart != existing.RankStart || tier.RankEnd != existing.RankEnd))
            throw new ContentChangeException(400, "Top slot places are set by the Top players number.");
        if (!tier.Ranked && tier.TierLevel != existing.TierLevel)
            throw new ContentChangeException(400, "A tier's FIFA level cannot change.");
        if (!tier.Ranked && (tier.PercentStart is < 1 or > 100 || tier.PercentEnd is < 1 or > 100 || tier.PercentStart > tier.PercentEnd || tier.MinPoints < 0))
            throw new ContentChangeException(400, "Percent bands run from 1 to 100, the first not above the last; minimum points are not negative.");
        if (SquadBattlesRewardRules.RewardProblem(tier.Reward) is { } problem)
            throw new ContentChangeException(400, $"{tier.Id}: {problem}.");
        return tier;
    }

    private static string Id(JsonObject key) => key["tierId"]!.GetValue<string>();
}
