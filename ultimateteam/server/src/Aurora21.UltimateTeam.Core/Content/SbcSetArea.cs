using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>
/// Squad Building Challenge sets as a content area (the panel's SBC catalogue, migration 136). A change is keyed by
/// <c>{"setKey": "UPG_BRONZE"}</c> and its value is <c>{"definition": {…}}</c>: the set as sbc.json writes it (name,
/// description, tabId, repeatable, badge, challenges with their requirement rules and rewards, and set
/// rewards required for two or more challenges). Editing a bundled set stores a full replacement. Hide removes a set added in the panel, or reverts a bundled
/// set to its sbc.json definition; whether the game shows a set is the live list's business (<see cref="SbcLiveArea"/>).
/// </summary>
public sealed partial class SbcSetArea(GameContext game) : IContentArea
{
    public const string AreaName = "sbc_set";
    public const int MaxChallenges = 20;
    public const int MaxRewards = 10;

    private static readonly HashSet<string> SetFields = ["id", "name", "description", "tabId", "repeatable", "badge", "image", "state",
        "challenges", "challengeCount", "challengeIds", "rewards", "rewardsTradeable", "repeatableDaily", "maxCompletions"];
    private static readonly HashSet<string> ChallengeFields = ["id", "order", "name", "description", "difficulty", "formation", "requirements",
        "rewards", "bricks", "number", "retail"];

    public string Name => AreaName;

    // After the rules and packs a set's rewards name.
    public int ApplyOrder => 120;

    public JsonObject NormalizeKey(JsonObject key)
    {
        var setKey = key["setKey"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        if (setKey is null || !KeyPattern().IsMatch(setKey))
            throw new ContentChangeException(400, "setKey is 1 to 64 letters, digits or underscores.");
        return new JsonObject { ["setKey"] = setKey };
    }

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key)
    {
        var setKey = Key(key);
        var definition = (await SbcSetRows.ReadAsync(work, setKey))?.Definition ?? game.Sbc.BundledSet(setKey);
        return definition is null ? null : new JsonObject { ["definition"] = definition.DeepClone() };
    }

    public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var setKey = Key(key);
        var live = await ReadAsync(work, key);
        if (operation == ContentOperation.Hide)
        {
            await CheckRemovableAsync(work, setKey);
            return;
        }
        if (operation == ContentOperation.Insert && live is not null)
            throw new ContentChangeException(409, "That SBC already exists; edit it instead.");
        if (operation == ContentOperation.Update && live is null)
            throw new ContentChangeException(409, "That SBC does not exist; add it instead.");
        if (after?["definition"] is not JsonObject definition)
            throw new ContentChangeException(400, "An SBC change is {\"definition\": {…}}.");
        Check(setKey, definition);
        // Clubs' completions are stored by challenge id: a challenge keeps its id for good (the panel assigns ids itself).
        if (operation == ContentOperation.Update && game.Sbc.FindSet(setKey) is { } current)
        {
            var keys = current.Challenges.ToDictionary(challenge => (long)(challenge.Id - current.Id), challenge => challenge.Key);
            foreach (var challenge in (definition["challenges"] as JsonArray ?? []).OfType<JsonObject>())
                if (challenge["number"] is { } number && JsonValues.TryInt(number, out var value) && keys.TryGetValue(value, out var was)
                    && was != JsonValues.Text(challenge["id"]))
                    throw new ContentChangeException(400, $"Challenge {was} cannot be renamed: clubs' completions are stored by its id.");
        }
    }

    public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
    {
        var setKey = Key(key);
        if (operation == ContentOperation.Hide)
        {
            await CheckRemovableAsync(work, setKey);
            await SbcSetRows.DeleteAsync(work, setKey);
            return;
        }
        var definition = Normalized(setKey, (JsonObject)after!["definition"]!);
        await NumberAsync(work, setKey, definition);
        await SbcSetRows.WriteAsync(work, setKey, definition);
    }

    /// <summary>
    /// Hide reverts a bundled set's edit, or removes a set added in the panel once it is off the live list and no club has
    /// progress in it (a completion, or a saved squad), so a later set reusing its key or ids inherits nothing.
    /// </summary>
    private async ValueTask CheckRemovableAsync(IUnitOfWork work, string setKey)
    {
        var row = await SbcSetRows.ReadAsync(work, setKey);
        if (game.Sbc.BundledSet(setKey) is not null)
        {
            if (row is null) throw new ContentChangeException(409, "A bundled SBC cannot be removed; take it off Live SBCs instead.");
            return;
        }
        if (row is null) throw new ContentChangeException(409, "That SBC does not exist.");
        if (await work.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM ultimateteam.sbc_live WHERE set_key = @key)", ("key", setKey)))
            throw new ContentChangeException(409, "Take this SBC off Live SBCs before removing it.");
        if (game.Sbc.FindSet(setKey) is { } set && await HasProgressAsync(work, set))
            throw new ContentChangeException(409, "Clubs have progress in this SBC, so it cannot be removed. Take it off Live SBCs instead.");
    }

    private static ValueTask<bool> HasProgressAsync(IUnitOfWork work, SbcSet set) =>
        work.ScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM ultimateteam.sbc_completed WHERE challenge_key = ANY(@keys))
                OR EXISTS (SELECT 1 FROM ultimateteam.sbc_squad_items WHERE challenge_id BETWEEN @first AND @last)
                OR EXISTS (SELECT 1 FROM ultimateteam.sbc_squad_counts WHERE challenge_id BETWEEN @first AND @last)
            """, ("keys", set.Challenges.Select(challenge => challenge.Key).ToArray()),
            ("first", (long)set.Id + 1), ("last", (long)set.Id + SbcCatalog.MaxChallengeNumber));

    /// <summary>The definition as stored: its key, and the order, challenge count and ids sbc.json carries, derived from the challenges.</summary>
    public static JsonObject Normalized(string setKey, JsonObject definition)
    {
        var set = (JsonObject)definition.DeepClone();
        set["id"] = setKey;
        var challenges = set.Arr("challenges") ?? new JsonArray();
        var order = 1;
        foreach (var challenge in challenges.OfType<JsonObject>()) challenge["order"] = order++;
        set["challengeCount"] = challenges.Count;
        set["challengeIds"] = new JsonArray(challenges.OfType<JsonObject>().Select(challenge => (JsonNode)JsonValues.Text(challenge.Get("id"))).ToArray());
        return set;
    }

    /// <summary>
    /// Gives every challenge a fixed number (its client id is the set id plus it; saved squads are stored by that id). A
    /// challenge keeps the number it has, or the one its key has in the live catalogue; a new one gets the next number no
    /// challenge or saved squad of this set has used, so reordering, removing or adding challenges moves no saved squad.
    /// </summary>
    private async ValueTask NumberAsync(IUnitOfWork work, string setKey, JsonObject definition)
    {
        var challenges = (definition.Arr("challenges") ?? new JsonArray()).OfType<JsonObject>().ToList();
        var current = game.Sbc.FindSet(setKey);
        var known = current?.Challenges.ToDictionary(challenge => challenge.Key, challenge => (long)(challenge.Id - current.Id), StringComparer.Ordinal)
            ?? new Dictionary<string, long>(StringComparer.Ordinal);
        var used = new HashSet<long>(known.Values);
        if (current is not null)
        {
            used.UnionWith(await work.QueryAsync("""
                SELECT challenge_id - @set FROM ultimateteam.sbc_squad_items WHERE challenge_id BETWEEN @first AND @last
                UNION SELECT challenge_id - @set FROM ultimateteam.sbc_squad_counts WHERE challenge_id BETWEEN @first AND @last
                """, row => row.GetInt64(0), ("set", (long)current.Id), ("first", (long)current.Id + 1), ("last", (long)current.Id + SbcCatalog.MaxChallengeNumber)));
        }
        foreach (var challenge in challenges)
        {
            if (challenge["number"] is not null) continue;
            if (known.TryGetValue(JsonValues.Text(challenge.Get("id")), out var number) && challenges.All(other => !Same(other["number"], number)))
                challenge["number"] = number;
        }
        used.UnionWith(challenges.Select(challenge => challenge["number"]).Where(node => node is not null).Select(node => (long)JsonValues.Int(node)));
        var next = used.Count == 0 ? 1 : used.Max() + 1;
        foreach (var challenge in challenges.Where(challenge => challenge["number"] is null))
        {
            if (next > SbcCatalog.MaxChallengeNumber)
                throw new ContentChangeException(400, $"This SBC has used all {SbcCatalog.MaxChallengeNumber} challenge numbers; add the challenge to a new SBC.");
            challenge["number"] = next++;
        }
    }

    private static bool Same(JsonNode? node, long number) => node is not null && JsonValues.TryInt(node, out var value) && value == number;

    /// <summary>Why staging this set would be refused, or null: the editor's live check (OperatorPanel SBC preview).</summary>
    public string? Problem(string setKey, JsonObject definition)
    {
        try
        {
            Check(setKey, definition);
            return null;
        }
        catch (ContentChangeException error)
        {
            return error.Message;
        }
    }

    /// <summary>Refuses a set the game could not show, check or pay, with a message naming the field.</summary>
    private void Check(string setKey, JsonObject set)
    {
        Fields(set, SetFields, "the set");
        if (set["id"] is { } id && JsonValues.Text(id) != setKey) throw Bad("id must match the setKey.");
        Text(set, "name", "the set", 1, 80);
        Text(set, "description", "the set", 0, 400);
        if (!JsonValues.TryInt(set["tabId"], out var tabId) || !Tabs().Contains(tabId))
            throw Bad($"tabId is one of the game's SBC tabs: {string.Join(", ", Tabs())}.");
        if (set["repeatable"] is { } repeatable && repeatable.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            throw Bad("repeatable is true or false.");
        if (set["repeatableDaily"] is { } daily && daily.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            throw Bad("repeatableDaily is true or false.");
        if (set.Bool("repeatableDaily") && !set.Bool("repeatable")) throw Bad("Daily SBCs must be repeatable.");
        if (set["maxCompletions"] is not null)
        {
            if (set["maxCompletions"]!.GetValueKind() != JsonValueKind.Number
                || !JsonValues.TryInt(set["maxCompletions"], out var limit) || limit < 1 || limit > 10000
                || JsonValues.Float(set["maxCompletions"]) != limit)
                throw Bad("maxCompletions must be a whole number from 1 to 10000.");
            if (!set.Bool("repeatable")) throw Bad("Limited SBCs must be repeatable.");
        }
        if (set["badge"] is { } badge)
        {
            var name = JsonValues.Text(badge);
            if (name.Length > 0 && !Badges().Contains(name, StringComparer.Ordinal)) throw Bad($"badge {name} is not one of the SBC badge images.");
        }
        if (set["challenges"] is not JsonArray challenges || challenges.Count is 0 or > MaxChallenges)
            throw Bad($"An SBC has 1 to {MaxChallenges} challenges.");
        var ownKeys = new HashSet<string>(StringComparer.Ordinal);
        var otherKeys = game.Sbc.Challenges.Where(challenge => challenge.SetKey != setKey).Select(challenge => challenge.Key).ToHashSet(StringComparer.Ordinal);
        var numbers = new HashSet<long>();
        var position = 0;
        foreach (var node in challenges)
        {
            position++;
            var where = $"challenge {position}";
            if (node is not JsonObject challenge) throw Bad($"{where} is an object.");
            Fields(challenge, ChallengeFields, where);
            var challengeKey = challenge["id"] is JsonValue value && value.TryGetValue(out string? text) ? text : "";
            if (!KeyPattern().IsMatch(challengeKey)) throw Bad($"{where} needs an id of 1 to 64 letters, digits or underscores.");
            if (!ownKeys.Add(challengeKey) || otherKeys.Contains(challengeKey))
                throw Bad($"Challenge id {challengeKey} is already used; every challenge id must be unique.");
            if (challenge["number"] is { } numberNode && (!JsonValues.TryInt(numberNode, out var number)
                    || number is < 1 or > SbcCatalog.MaxChallengeNumber || !numbers.Add(number)))
                throw Bad($"{where}: number is a whole number from 1 to {SbcCatalog.MaxChallengeNumber}, different for every challenge.");
            Text(challenge, "name", where, 1, 80);
            Text(challenge, "description", where, 0, 400);
            if (challenge["formation"] is { } formation && (formation.GetValueKind() != JsonValueKind.String
                    || !game.Chemistry.Formations.ContainsKey(JsonValues.Text(formation))))
                throw Bad($"{where}: formation is one of the game's formations (chemistry.json).");
            Requirements(challenge, where);
            Bricks(challenge, where);
            Rewards(challenge["rewards"], where, required: false);
        }
        if (set["rewardsTradeable"] is { } tradeable && tradeable.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            throw Bad("rewardsTradeable is true or false.");
        Rewards(set["rewards"], "the group reward", required: challenges.Count >= 2);
    }

    /// <summary>
    /// Locked slots (SbcBricks): the slots the player fills (freeIndices, 1 to squad size - 1 of them), and on locked slots
    /// either an authored card (players: slot to resource id) or a retail placeholder (dummies: slot to league and nation).
    /// The PLAYERS rule must ask for exactly the free slots, or FIFA's count and the submit check disagree.
    /// </summary>
    private void Bricks(JsonObject challenge, string where)
    {
        if (challenge["bricks"] is null) return;
        var squad = (int)game.Rules.Sbc.SquadSize;
        if (challenge["bricks"] is not JsonObject bricks || bricks.Any(entry => entry.Key is not ("freeIndices" or "players" or "dummies")))
            throw Bad($"{where}: bricks is {{freeIndices, players, dummies}}.");
        var free = new HashSet<long>();
        foreach (var node in bricks["freeIndices"] as JsonArray ?? throw Bad($"{where}: bricks needs freeIndices."))
            if (!JsonValues.TryInt(node, out var index) || index < 0 || index >= squad || !free.Add(index))
                throw Bad($"{where}: freeIndices are different slots from 0 to {squad - 1}.");
        if (free.Count is 0 || free.Count >= squad)
            throw Bad($"{where}: a challenge with locked slots leaves 1 to {squad - 1} slots free.");
        var locked = new HashSet<long>();
        long LockedSlot(string key, string what)
        {
            if (!long.TryParse(key, out var index) || index < 0 || index >= squad || free.Contains(index) || !locked.Add(index))
                throw Bad($"{where}: a {what} goes on a locked slot (0 to {squad - 1}, not free), one per slot.");
            return index;
        }
        foreach (var (key, node) in bricks["players"] as JsonObject ?? [])
        {
            LockedSlot(key, "locked player");
            if (!JsonValues.TryInt(node, out var resourceId) || !game.Players.Contains(resourceId))
                throw Bad($"{where}: locked player {JsonValues.Text(node)} is not a card.");
        }
        foreach (var (key, node) in bricks["dummies"] as JsonObject ?? [])
        {
            LockedSlot(key, "placeholder");
            if (node is not JsonObject dummy || dummy.Int("league") <= 0 || dummy.Int("nation") <= 0)
                throw Bad($"{where}: a placeholder names a league and a nation.");
        }
        var players = ((challenge["requirements"] as JsonObject)?["rules"] as JsonArray ?? [])
            .OfType<JsonObject>().FirstOrDefault(rule => JsonValues.Text(rule["type"]).Equals("PLAYERS", StringComparison.OrdinalIgnoreCase));
        if (players is null || (players["attributes"] as JsonObject)?.Int("max") != free.Count)
            throw Bad($"{where}: with {free.Count} free slots, the Players in squad requirement is {free.Count}.");
    }

    private void Requirements(JsonObject challenge, string where)
    {
        if (challenge["requirements"] is null) return;
        if (challenge["requirements"] is not JsonObject requirements || requirements["rules"] is not JsonArray rules || rules.Count > 20)
            throw Bad($"{where}: requirements is {{\"rules\": [up to 20 rules]}}.");
        var known = game.Rules.Sbc.Requirements;
        foreach (var node in rules)
        {
            if (node is not JsonObject rule) throw Bad($"{where}: each requirement is an object.");
            var type = JsonValues.Text(rule["type"]).ToUpperInvariant();
            var attributes = rule["attributes"] as JsonObject ?? new JsonObject();
            if (type == "PLAYERS")
            {
                foreach (var bound in new[] { "min", "max" })
                    if (attributes[bound] is { } count && (!JsonValues.TryInt(count, out var players) || players is < 1 or > 11))
                        throw Bad($"{where}: the player count is 1 to 11.");
                continue;
            }
            if (!known.TryGetValue(type, out var definition)) throw Bad($"{where}: {type} is not a requirement type ({string.Join(", ", known.Keys.Order())}).");
            if (definition.Selector is { } selector)
            {
                var valid = selector switch
                {
                    "leagueId" => game.Live.Snapshot.Catalog.Leagues.Any(x => x.NativeLeagueId == attributes.Int(selector)),
                    "nationId" => game.Live.Snapshot.Catalog.Nations.Any(x => x.NativeNationId == attributes.Int(selector)),
                    "clubId" => game.Players.Entries.Any(x => x.Value.Int("teamid") == attributes.Int(selector)),
                    "quality" => definition.Qualities.ContainsKey(attributes.Text(selector)),
                    _ => false,
                };
                if (!valid) throw Bad($"{where}: {type} needs a valid {selector} selection.");
            }
            if (definition.Bound == Rules.SbcBound.Value)
            {
                var quality = JsonValues.Text(attributes["value"]).ToLowerInvariant();
                if (!definition.Qualities.ContainsKey(quality))
                    throw Bad($"{where}: {type} is one of {string.Join(", ", definition.Qualities.Keys)}.");
                continue;
            }
            var numbers = attributes.Where(entry => entry.Key is "min" or "max" or "exact").ToList();
            if (numbers.Count == 0) throw Bad($"{where}: {type} needs a value.");
            var limit = type is "RATING" or "CHEMISTRY" or "AGE" ? 100 : game.Rules.Sbc.SquadSize;
            foreach (var (name, number) in numbers)
                if (!JsonValues.TryInt(number, out var amount) || amount < 0 || amount > limit)
                    throw Bad($"{where}: {type} {name} is a whole number from 0 to {limit}.");
        }
    }

    private void Rewards(JsonNode? node, string where, bool required)
    {
        if (required && (node is null || node is JsonObject obj && (obj["entries"] is null || obj["entries"] is JsonArray { Count: 0 })))
            throw Bad("A group reward is required for an SBC with two or more challenges.");
        if (node is null && !required) return;
        if (node is not JsonObject rewards || rewards["entries"] is not JsonArray entries || entries.Count > MaxRewards)
            throw Bad($"{where}: rewards is {{\"entries\": [up to {MaxRewards} rewards]}}.");
        foreach (var entryNode in entries)
        {
            if (entryNode is not JsonObject entry) throw Bad($"{where}: each reward is an object.");
            var type = JsonValues.Text(entry["type"]).ToUpperInvariant();
            var attributes = entry["attributes"] as JsonObject ?? new JsonObject();
            switch (type)
            {
                case "PACK":
                    var count = attributes["count"] is { } countNode && JsonValues.TryInt(countNode, out var copies) ? copies : 1;
                    if (count is < 1 or > 10) throw Bad($"{where}: a pack reward gives 1 to 10 packs.");
                    if (attributes.Int("packId") is not (var packId and > 0)) throw Bad($"{where}: a pack reward names its pack by packId.");
                    if (game.Packs.GetPackConfig(packId).Count == 0) throw Bad($"{where}: pack {packId} does not exist.");
                    break;
                case "PLAYER":
                    // A card picked in the panel (resourceId), or an authored player matched by name, rating and position.
                    if (!game.Players.Contains(attributes.Int("resourceId")) && JsonValues.Text(attributes["name"]).Trim().Length == 0)
                        throw Bad($"{where}: a player reward names a card.");
                    break;
                case "MANAGER_LEAGUE":
                    if (game.Cards.ManagerLeagueCardForLeague(attributes.Int("league")) is null)
                        throw Bad($"{where}: a manager league reward names a league with a manager league card.");
                    break;
                case "COINS":
                case "XP":
                    if (!JsonValues.TryInt(entry["value"], out var amount) || amount is < 1 or > 10_000_000)
                        throw Bad($"{where}: a {type.ToLowerInvariant()} reward is 1 to 10,000,000.");
                    break;
                default:
                    throw Bad($"{where}: {type} rewards are not supported here (PACK, PLAYER, MANAGER_LEAGUE, COINS or XP).");
            }
        }
    }

    /// <summary>The badge images a set may name: the files in the SBC art folder, without .png.</summary>
    public IReadOnlyList<string> Badges()
    {
        var folder = Path.Combine(game.Paths.DataRoot, game.Rules.Sbc.Images.ArtFolder);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.png").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
    }

    /// <summary>The game's SBC tab ids (sbc.json tabs).</summary>
    public IReadOnlyList<long> Tabs() => (game.Sbc.Root.Arr("tabs") ?? new JsonArray()).OfType<JsonObject>().Select(tab => tab.Int("id")).ToList();

    private static void Fields(JsonObject node, HashSet<string> allowed, string where)
    {
        foreach (var (name, _) in node)
            if (!allowed.Contains(name)) throw Bad($"{where}: {name} is not an SBC field.");
    }

    private static void Text(JsonObject node, string name, string where, int min, int max)
    {
        if (node[name] is null && min == 0) return;
        var text = node[name] is JsonValue value && value.TryGetValue(out string? s) ? s : null;
        if (text is null || text.Trim().Length < min || text.Length > max)
            throw Bad($"{where}: {name} is text of {min} to {max} characters.");
    }

    private static string Key(JsonObject key) => key["setKey"]!.GetValue<string>();

    /// <summary>Whether a text is a valid set key: 1 to 64 letters, digits or underscores.</summary>
    public static bool IsSetKey(string key) => KeyPattern().IsMatch(key);

    private static ContentChangeException Bad(string message) => new(400, message);

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex KeyPattern();
}
