using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>Shared checks of the Division Rivals reward areas (migration 131).</summary>
internal static class RivalsRewardEdits
{
    public static int Number(JsonObject key, string name, int lowest, int highest)
    {
        if (key[name] is not JsonValue value || !value.TryGetValue(out int number) || number < lowest || number > highest)
            throw new ContentChangeException(400, $"{name} is a whole number from {lowest} to {highest}.");
        return number;
    }

    /// <summary>A staged Rivals reward, read strictly and checked as the payouts need it.</summary>
    public static Reward Reward(JsonNode? node, string label)
    {
        Reward reward;
        try
        {
            reward = Rules.Reward.FromJson(node, label);
        }
        catch (InvalidDataException error)
        {
            throw new ContentChangeException(400, error.Message);
        }
        if (RivalsRewardRules.RewardProblem(reward) is { } problem)
            throw new ContentChangeException(400, $"{label}: {problem}.");
        return reward;
    }

    /// <summary>Packs created in the same changeset exist by now; the reward_lines foreign key would refuse any other.</summary>
    public static async ValueTask RequirePacksAsync(IUnitOfWork work, IEnumerable<Reward> rewards, string label)
    {
        var packs = rewards.SelectMany(reward => reward.Lines).Where(line => line.PackId is not null).Select(line => line.PackId!.Value).Distinct().ToArray();
        var known = await work.QueryAsync("SELECT pack_id FROM ultimateteam.store_packs WHERE pack_id = ANY(@ids)", row => row.GetInt64(0), ("ids", packs));
        if (packs.Except(known).FirstOrDefault() is var missing and not 0)
            throw new ContentChangeException(400, $"{label}: pack {missing} does not exist.");
    }
}

/// <summary>
/// One Division Rivals rank's weekly reward options as a content row, keyed by <c>{"division": 1-10, "rank": 1-6}</c> (1 is
/// Rank I). Its value is <c>{"options": [reward, …]}</c>: 1-4 rewards in the shared format, offered in that order (with several,
/// the first is granted from rivals_option_a, the second _b, later ones _c; a single one from rivals_fixed). Every rank always
/// has options, so a change is an update; hide is refused. Settled weeks keep the options they stored.
/// </summary>
public sealed class RivalsRankArea : IContentArea
{
    public const string AreaName = "rivals_rank";

    public string Name => AreaName;

    // After packs (10, 20) and the Champions ranks (30); before rules (100).
    public int ApplyOrder => 31;

    public JsonObject NormalizeKey(JsonObject key) => new()
    {
        ["division"] = RivalsRewardEdits.Number(key, "division", 1, RivalsRewardRules.DivisionCount),
        ["rank"] = RivalsRewardEdits.Number(key, "rank", 1, RivalsRewardRules.Ranks),
    };

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key) =>
        await ModeRewardRepository.RivalsRankAsync(work, Division(key), Rank(key)) is { } options ? ToJson(options) : null;

    public ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        if (operation != ContentOperation.Update)
            throw new ContentChangeException(400, "Every Division Rivals rank has reward options: edit them instead.");
        _ = Options(key, after);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var options = Options(key, after);
        await RivalsRewardEdits.RequirePacksAsync(work, options, Label(key));
        await ModeRewardRepository.WriteRivalsRankAsync(work, Division(key), Rank(key), options);
    }

    public static JsonObject ToJson(IEnumerable<Reward> options) =>
        new() { ["options"] = new JsonArray(options.Select(option => (JsonNode?)option.ToJson()).ToArray()) };

    private static List<Reward> Options(JsonObject key, JsonObject? after)
    {
        if (after is null || after.Count != 1 || after["options"] is not JsonArray options
            || options.Count is < 1 or > RivalsRewardRules.MaxRankOptions)
            throw new ContentChangeException(400, $"A Division Rivals rank change is {{\"options\": […]}} with 1 to {RivalsRewardRules.MaxRankOptions} rewards.");
        return options.Select((option, index) => RivalsRewardEdits.Reward(option, $"{Label(key)} option {index + 1}")).ToList();
    }

    private static string Label(JsonObject key) => $"Division {Division(key)} Rank {ModeRewardRepository.RivalsRankName(Rank(key))}";
    private static int Division(JsonObject key) => key["division"]!.GetValue<int>();
    private static int Rank(JsonObject key) => key["rank"]!.GetValue<int>();
}

/// <summary>
/// One Division Rivals promotion reward (paid once, the first time a club reaches the division) as a content row, keyed by
/// <c>{"division": 1-10}</c>. Its value is <c>{"reward": …}</c> in the shared format. Every division has one: update only.
/// </summary>
public sealed class RivalsPromotionArea : IContentArea
{
    public const string AreaName = "rivals_promotion";

    public string Name => AreaName;

    public int ApplyOrder => 32;

    public JsonObject NormalizeKey(JsonObject key) => new() { ["division"] = RivalsRewardEdits.Number(key, "division", 1, RivalsRewardRules.DivisionCount) };

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key) =>
        await ModeRewardRepository.RivalsPromotionAsync(work, Division(key)) is { } reward ? new JsonObject { ["reward"] = reward.ToJson() } : null;

    public ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        if (operation != ContentOperation.Update)
            throw new ContentChangeException(400, "Every Division Rivals division has a promotion reward: edit it instead.");
        _ = Reward(key, after);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var reward = Reward(key, after);
        await RivalsRewardEdits.RequirePacksAsync(work, [reward], $"Promotion to Division {Division(key)}");
        await ModeRewardRepository.WriteRivalsPromotionAsync(work, Division(key), reward);
    }

    private static Reward Reward(JsonObject key, JsonObject? after)
    {
        if (after is null || after.Count != 1 || !after.ContainsKey("reward"))
            throw new ContentChangeException(400, "A Division Rivals promotion change is {\"reward\": …}.");
        return RivalsRewardEdits.Reward(after["reward"], $"Promotion to Division {Division(key)}");
    }

    private static int Division(JsonObject key) => key["division"]!.GetValue<int>();
}
