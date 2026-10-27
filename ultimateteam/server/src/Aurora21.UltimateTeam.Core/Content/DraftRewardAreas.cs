using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>
/// One Draft reward level as a content row (migration 135), keyed by <c>{"mode": "online"|"single", "wins": 0-4}</c>. Its value is
/// <c>{"reward", "choices": [{"weight", "reward"}]}</c>: the reward every run ending there gets, and the 1-50 choices one is drawn
/// from. Every mode has a level for each wins count, so this is update only.
/// </summary>
public sealed class DraftLevelArea : IContentArea
{
    public const string AreaName = "draft_level";

    public string Name => AreaName;

    // After packs (10, 20), Champions (30), Rivals (31, 32) and Squad Battles (33); before rules (100).
    public int ApplyOrder => 34;

    public JsonObject NormalizeKey(JsonObject key)
    {
        if (key["mode"] is not JsonValue mode || !mode.TryGetValue(out string? name) || name is not (DraftRewardLevel.Online or DraftRewardLevel.Single))
            throw new ContentChangeException(400, "mode is online or single.");
        if (key["wins"] is not JsonValue wins || !wins.TryGetValue(out int count) || count is < 0 or > 10)
            throw new ContentChangeException(400, "wins is a Draft win count.");
        return new JsonObject { ["mode"] = name, ["wins"] = count };
    }

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key) =>
        await ModeRewardRepository.DraftLevelAsync(work, Mode(key), Wins(key)) is { } level ? ToJson(level) : null;

    public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        if (operation != ContentOperation.Update)
            throw new ContentChangeException(400, "Draft has a reward level for every win count of both modes: edit a level instead.");
        _ = await ModeRewardRepository.DraftLevelAsync(work, Mode(key), Wins(key))
            ?? throw new ContentChangeException(404, "There is no such Draft reward level.");
        _ = Level(key, after);
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        _ = await ModeRewardRepository.DraftLevelAsync(work, Mode(key), Wins(key))
            ?? throw new ContentChangeException(404, "There is no such Draft reward level.");
        var level = Level(key, after);
        await RivalsRewardEdits.RequirePacksAsync(work, level.Choices.Select(choice => choice.Reward).Prepend(level.Reward),
            $"Draft {ModeRewardRepository.DraftLevelName(level.Mode, level.Wins)}");
        await ModeRewardRepository.WriteDraftLevelAsync(work, level);
    }

    public static JsonObject ToJson(DraftRewardLevel level) => new()
    {
        ["reward"] = level.Reward.ToJson(),
        ["choices"] = new JsonArray(level.Choices.Select(choice => (JsonNode?)new JsonObject
        {
            ["weight"] = choice.Weight, ["reward"] = choice.Reward.ToJson(),
        }).ToArray()),
    };

    /// <summary>A staged level, read strictly and checked as start-up checks it.</summary>
    private static DraftRewardLevel Level(JsonObject key, JsonObject? after)
    {
        if (after is null) throw new ContentChangeException(400, "A Draft level change needs the level.");
        var row = (JsonObject)after.DeepClone();
        if (row.ContainsKey("mode") || row.ContainsKey("wins")) throw new ContentChangeException(400, "The mode and wins are the key; they are not part of the level.");
        row["mode"] = Mode(key);
        row["wins"] = Wins(key);
        DraftRewardLevel level;
        try
        {
            level = RulesJson.Deserialize<DraftRewardLevel>(row.ToJsonString(), $"Draft {Mode(key)} {Wins(key)} wins");
        }
        catch (InvalidDataException error)
        {
            throw new ContentChangeException(400, error.Message);
        }
        if (DraftRules.LevelProblem(level) is { } problem)
            throw new ContentChangeException(400, $"{ModeRewardRepository.DraftLevelName(level.Mode, level.Wins)}: {problem}.");
        return level;
    }

    private static string Mode(JsonObject key) => key["mode"]!.GetValue<string>();

    private static int Wins(JsonObject key) => key["wins"]!.GetValue<int>();
}
