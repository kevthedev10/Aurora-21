using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>
/// The game rules as a content area (live-content-editing-plan.md, step 6). A change is keyed by
/// <c>{"file": "market", "path": ["priceBounds", "maximumByRating", 2, "maximum"]}</c> (strings are object keys, numbers
/// list positions) and its value is <c>{"value": …}</c>: one value, or a whole list or object at that location. Update
/// replaces the value, insert adds a key (or appends at the list's length), and hide removes it. Each change is checked
/// against the file's rules model when staged, and the whole content again when published.
/// </summary>
public sealed class RulesArea : IContentArea
{
    public const string AreaName = "rules";

    public string Name => AreaName;

    // After catalogue areas: rules may refer to catalogue rows, never the other way round.
    public int ApplyOrder => 100;

    public JsonObject NormalizeKey(JsonObject key)
    {
        var file = key["file"] is JsonValue fileValue && fileValue.TryGetValue(out string? name) ? name : null;
        if (file is null || !GameRules.DatabaseFiles.Contains(file))
            throw new ContentChangeException(400, $"file is one of {string.Join(", ", GameRules.DatabaseFiles)}.");
        if (key["path"] is not JsonArray path || path.Count == 0 || path.Count > 16)
            throw new ContentChangeException(400, "path is a list of 1 to 16 keys and list positions.");
        var canonical = new JsonArray();
        foreach (var segment in path)
        {
            if (segment is JsonValue text && text.GetValueKind() == JsonValueKind.String && text.GetValue<string>() is { Length: > 0 and <= 128 } keyName)
                canonical.Add(keyName);
            else if (RuleRows.IsPosition(segment) && segment!.GetValue<int>() <= 100_000)
                canonical.Add(segment.GetValue<int>());
            else
                throw new ContentChangeException(400, "Each path step is a key (text) or a list position (a whole number from 0).");
        }
        return new JsonObject { ["file"] = file, ["path"] = canonical };
    }

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key)
    {
        var document = await DocumentAsync(work, File(key));
        var value = RuleRows.Find(document, Path(key), out var found);
        return found ? new JsonObject { ["value"] = value?.DeepClone() } : null;
    }

    public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        if (operation != ContentOperation.Hide && (after is null || !after.ContainsKey("value")))
            throw new ContentChangeException(400, "A rules change is {\"value\": …}.");
        if (MovedToTables(File(key), Path(key)) is { } moved)
            throw new ContentChangeException(409, moved);
        // The file with this change alone must still read as its rules model: a typo or wrong type is refused here.
        var document = await DocumentAsync(work, File(key));
        Change(document, operation, key, after);
        try
        {
            GameRules.ValidateDocument(File(key), document.ToJsonString());
        }
        catch (InvalidDataException error)
        {
            throw new ContentChangeException(400, error.Message);
        }
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var file = File(key);
        var document = await DocumentAsync(work, file);
        var previousTop = file == "seasons" ? document["weekendLeague"]?["topX"]?.GetValue<int>() : null;
        var previousBoard = file == "squad_battles" ? document["competition"]?["boardSize"]?.GetValue<int>() : null;
        Change(document, operation, key, after);
        await RuleRows.WriteAsync(work, file, document);
        // A group going live for the first time counts from now (migration 143).
        if (file == "objectives" && !await work.ScalarAsync<bool>("SELECT coalesce(current_setting('ultimateteam.defer_objective_start',true),'')='on'"))
            await ObjectiveStartRepository.StartLiveAsync(work, RulesJson.Deserialize<ObjectiveRules>(document.ToJsonString(), "objectives rules"));
        // The Squad Battles Top size places its six Top slots (migration 133), as the Champions one does below.
        if (file == "squad_battles" && previousBoard != document["competition"]?["boardSize"]?.GetValue<int>())
        {
            try
            {
                await ModeRewardRepository.PlaceSquadBattlesSlotsAsync(work, document["competition"]!["boardSize"]!.GetValue<int>());
            }
            catch (InvalidDataException error)
            {
                throw new ContentChangeException(422, error.Message);
            }
        }
        if (file == "seasons" && previousTop != document["weekendLeague"]?["topX"]?.GetValue<int>())
        {
            var size = document["weekendLeague"]!["topX"]!.GetValue<int>();
            var ranks = (await ModeRewardRepository.ChampionsRanksAsync(work)).Where(rank => rank.Positions is not null)
                .OrderBy(rank => rank.Positions![0]).ToArray();
            if (ranks.Length != ChampionsRewardRules.ClientTopBands)
                throw new ContentChangeException(422, "Champions requires six leaderboard reward slots before changing the Top size.");
            var bands = LeaderboardSizes.ChampionsBands(size);
            for (var index = 0; index < ranks.Length; index++)
                await work.ExecuteAsync("UPDATE ultimateteam.champions_reward_ranks SET name=@name, positions_low=@low, positions_high=@high WHERE tier_level=@tier",
                    ("name", LeaderboardSizes.Name(bands[index].Low, bands[index].High)), ("low", bands[index].Low), ("high", bands[index].High),
                    ("tier", ranks[index].TierLevel));
        }
    }

    private static void Change(JsonObject document, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var path = Path(key);
        RuleRows.Find(document, path, out var exists);
        try
        {
            switch (operation)
            {
                case ContentOperation.Update when exists:
                    RuleRows.Set(document, path, after!["value"]);
                    break;
                case ContentOperation.Insert when !exists:
                    RuleRows.Set(document, path, after!["value"]);
                    break;
                case ContentOperation.Hide when exists:
                    RuleRows.Remove(document, path);
                    break;
                default:
                    throw new ContentChangeException(409, exists ? "That rule already exists; edit it instead." : "That rule does not exist.");
            }
        }
        catch (InvalidDataException error)
        {
            throw new ContentChangeException(400, error.Message);
        }
    }

    /// <summary>
    /// Why a rule path can no longer be edited here, or null: the Champions reward ranks are tables since migration 126 (Modes,
    /// Champions), the Rivals reward options and promotion rewards since 131 (Modes, Rivals), the Squad Battles tiers since 133
    /// (Modes, Squad Battles), and the per-Weekend-League overrides (125) and band sizing (130, 133) were removed. A change
    /// staged before then names them.
    /// </summary>
    private static string? MovedToTables(string file, JsonArray path) =>
        path[0] is JsonValue first && first.TryGetValue(out string? name) ? (file, name) switch
        {
            ("champions_rewards", "positionsNeedWins") => "Champions leaderboard rewards no longer require minimum wins. Remove this rule change.",
            ("champions_rewards", "ranks") => "The Champions reward ranks moved to Modes → Champions; edit them there. Remove this rule change.",
            ("champions_rewards", "eventOverrides") => "Champions event overrides were removed; rewards now change by publishing. Remove this rule change.",
            ("champions_rewards", "leaderboardBands") => "Champions band sizing was removed; the Top players number sets the band places. Remove this rule change.",
            ("rivals_rewards", "divisions" or "promotion") => "The Division Rivals rewards moved to Modes → Rivals; edit them there. Remove this rule change.",
            ("sqbt_rewards", "tiers") => "The Squad Battles reward tiers moved to Modes → Squad Battles; edit them there. Remove this rule change.",
            ("draft", "runRewards") => "The Draft run rewards moved to Modes → Draft; edit them there. Remove this rule change.",
            ("draft", "winsToComplete") => "FIFA's Draft runs to four wins, and each wins count has its reward level: the run length cannot change.",
            ("sqbt_rewards", "leaderboardBands") => "Squad Battles band sizing was removed; the Top players number sets the slot places. Remove this rule change.",
            _ => null,
        } : null;

    private static async ValueTask<JsonObject> DocumentAsync(IUnitOfWork work, string file) =>
        (await RuleRows.ReadAsync(work, file)).TryGetValue(file, out var rows) ? RuleRows.Rebuild(rows) : new JsonObject();

    private static string File(JsonObject key) => key["file"]!.GetValue<string>();

    private static JsonArray Path(JsonObject key) => (JsonArray)key["path"]!;
}
