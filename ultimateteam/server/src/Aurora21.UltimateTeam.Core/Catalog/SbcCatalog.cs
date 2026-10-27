using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>An authored SBC challenge. The client keys everything by integer id; sbc.json uses strings.</summary>
public sealed record SbcChallenge(int Id, int SetId, string SetKey, JsonObject Source)
{
    public string Key => JsonValues.Text(Source.Get("id"));
}

/// <summary>A retail placeholder brick: a locked slot holding a card of this league and nation (FUTBIN's "dummy" slot).</summary>
public readonly record struct SbcBrickDummy(long League, long Nation);

/// <summary>
/// A challenge's locked slots (the client's bricks). <paramref name="FreeIndices"/> lists the slots the player fills; every
/// other slot inside the squad size is locked. A locked slot is empty (the client's BRICK) unless it carries an authored
/// card (<paramref name="CustomPlayers"/>, a resource id) or a retail placeholder (<paramref name="Dummies"/>, a league
/// and nation the server resolves to a card): those are CUSTOM_BRICK slots whose card counts toward chemistry.
/// Captured EA responses encode this as playerRequirements playerType "DEFAULT" / "BRICK" / "CUSTOM_BRICK".
/// </summary>
public sealed record SbcBricks(IReadOnlyList<int> FreeIndices, IReadOnlyDictionary<int, long> CustomPlayers,
    IReadOnlyDictionary<int, SbcBrickDummy> Dummies, long SquadSize)
{
    public bool IsFree(int index) => FreeIndices.Contains(index);

    public bool IsBrick(int index) => !IsFree(index) && index >= 0 && index < SquadSize;

    /// <summary>Whether a locked slot carries a card (an authored player or a placeholder).</summary>
    public bool IsCustom(int index) => IsBrick(index) && (CustomPlayers.ContainsKey(index) || Dummies.ContainsKey(index));

    public bool HasCustom => CustomPlayers.Count > 0 || Dummies.Count > 0;

    public static SbcBricks? Parse(JsonObject source, long squadSize)
    {
        if (source.Obj("bricks") is not { } bricks)
        {
            return null;
        }
        var free = (bricks.Arr("freeIndices") ?? new JsonArray())
            .Select(node => (int)JsonValues.Int(node))
            .Where(index => index >= 0 && index < squadSize)
            .Distinct()
            .ToList();
        var players = new Dictionary<int, long>();
        foreach ((var key, var node) in bricks.Obj("players") ?? new JsonObject())
        {
            if (int.TryParse(key, out var index) && index >= 0 && index < squadSize
                && JsonValues.TryInt(node, out var resourceId) && resourceId > 0)
            {
                players[index] = resourceId;
            }
        }
        var dummies = new Dictionary<int, SbcBrickDummy>();
        foreach ((var key, var node) in bricks.Obj("dummies") ?? new JsonObject())
        {
            if (int.TryParse(key, out var index) && index >= 0 && index < squadSize && node is JsonObject dummy
                && dummy.Int("league") > 0 && dummy.Int("nation") > 0)
            {
                dummies[index] = new SbcBrickDummy(dummy.Int("league"), dummy.Int("nation"));
            }
        }
        if (free.Count == 0 || free.Count >= squadSize)
        {
            return null;
        }
        return new SbcBricks(free, players, dummies, squadSize);
    }
}

public sealed record SbcSet(int Id, string Key, JsonObject Source, IReadOnlyList<SbcChallenge> Challenges);

/// <summary>
/// The Squad Building Challenge catalogue: the human-authored sbc.json with the operator's panel edits (migration 136,
/// <see cref="SbcSetRow"/>) over it, where a row replaces the bundled set with its key or adds a new set. Every set is in
/// the catalogue; the game shows one only while it is on the live list and switched on (migration 142, <see cref="IsLive"/>).
/// </summary>
public sealed class SbcCatalog
{
    /// <summary>
    /// Set ids are FirstSetId + index * SetIdStep for the bundled sets (their place in sbc.json, kept when one is edited),
    /// and FirstSetId + (PanelSetIndexBase + ordinal) * SetIdStep for sets added in the panel, so neither moves when the
    /// other grows. A challenge's id is its set id plus its number: the challenge's <c>number</c> (1 to MaxChallengeNumber,
    /// written when the panel saves it, so reordering or removing challenges moves no saved squad), else its 1-based place.
    /// </summary>
    public const int FirstSetId = 900000;
    public const int SetIdStep = 100;
    public const int PanelSetIndexBase = 1000;
    public const int MaxChallengeNumber = SetIdStep - 1;

    private readonly Dictionary<int, SbcSet> _setsById = new();
    private readonly Dictionary<int, SbcChallenge> _challengesById = new();

    private SbcCatalog(JsonObject root, SbcContentRows content)
    {
        Root = root;
        Bundled = root;
        Live = content.Live.ToDictionary(entry => entry.Key, entry => entry.Enabled, StringComparer.Ordinal);
        LiveKeys = content.Live.Where(entry => entry.Enabled).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var edits = content.Sets.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var bundledKeys = new HashSet<string>(StringComparer.Ordinal);
        var placed = new List<(int Index, JsonObject Source)>();
        var index = 0;
        foreach (var node in root.Arr("sets") ?? new JsonArray())
        {
            if (node is JsonObject bundled)
            {
                var key = JsonValues.Text(bundled.Get("id"));
                bundledKeys.Add(key);
                placed.Add((index, edits.TryGetValue(key, out var edit) ? WithKey(edit.Definition, key) : bundled));
            }
            index++;
        }
        foreach (var row in content.Sets.Where(row => !bundledKeys.Contains(row.Key)).OrderBy(row => row.Ordinal))
            placed.Add(((int)(PanelSetIndexBase + row.Ordinal), WithKey(row.Definition, row.Key)));

        var sets = new List<SbcSet>();
        var liveChallengeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (position, setSource) in placed)
        {
            var setId = FirstSetId + position * SetIdStep;
            var setKey = JsonValues.Text(setSource.Get("id"));
            CheckPackRewards(setKey, setSource);
            var challenges = new List<SbcChallenge>();
            var numbers = new HashSet<long>();
            var challengeIndex = 1;
            foreach (var challengeNode in setSource.Arr("challenges") ?? new JsonArray())
            {
                if (challengeNode is JsonObject challengeSource)
                {
                    var number = Number(challengeSource, challengeIndex);
                    if (number is < 1 or > MaxChallengeNumber || !numbers.Add(number))
                        throw new InvalidDataException($"SBC {setKey}: challenge number {number} is outside 1 to {MaxChallengeNumber} or used twice.");
                    var challenge = new SbcChallenge(setId + (int)number, setId, setKey, challengeSource);
                    // Completions are stored by challenge key, so two live challenges sharing one would share progress.
                    if (LiveKeys.Contains(setKey) && !liveChallengeKeys.Add(challenge.Key))
                        throw new InvalidDataException($"SBC challenge id {challenge.Key} is used more than once.");
                    challenges.Add(challenge);
                    _challengesById[challenge.Id] = challenge;
                }
                challengeIndex++;
            }
            var set = new SbcSet(setId, setKey, setSource, challenges);
            sets.Add(set);
            _setsById[setId] = set;
        }
        Sets = sets;
    }

    /// <summary>Every PACK reward names its pack by id: the old pack names were converted at start-up (SbcRewardPackConversion).</summary>
    private static void CheckPackRewards(string setKey, JsonObject set)
    {
        var sources = (set.Arr("challenges") ?? new JsonArray()).OfType<JsonObject>().Prepend(set);
        foreach (var entry in sources.SelectMany(source => (source.Obj("rewards")?.Arr("entries") ?? new JsonArray()).OfType<JsonObject>()))
            if (JsonValues.Text(entry.Get("type")).Equals("PACK", StringComparison.OrdinalIgnoreCase) && (entry.Obj("attributes")?.Int("packId") ?? 0) <= 0)
                throw new InvalidDataException($"SBC {setKey}: a pack reward needs a packId.");
    }

    /// <summary>A challenge's number: its own <c>number</c> when it has one, else its 1-based place in the set.</summary>
    public static long Number(JsonObject challenge, int place) =>
        challenge["number"] is { } number && JsonValues.TryInt(number, out var value) ? value : place;

    private static JsonObject WithKey(JsonObject definition, string key)
    {
        var source = (JsonObject)definition.DeepClone();
        source["id"] = key;
        return source;
    }

    /// <summary>sbc.json as bundled: its tabs, and the sets the panel edits start from.</summary>
    public JsonObject Root { get; }

    public JsonObject Bundled { get; }

    /// <summary>The bundled definition of a set, or null when the set exists only in the panel.</summary>
    public JsonObject? BundledSet(string key) => (Bundled.Arr("sets") ?? new JsonArray()).OfType<JsonObject>()
        .FirstOrDefault(set => JsonValues.Text(set.Get("id")) == key);

    /// <summary>Every set in the catalogue, live or not.</summary>
    public IReadOnlyList<SbcSet> Sets { get; }

    /// <summary>The live list: each listed set key and whether it is switched on.</summary>
    public IReadOnlyDictionary<string, bool> Live { get; }

    /// <summary>The keys of the sets the game shows: listed and switched on.</summary>
    public HashSet<string> LiveKeys { get; }

    public bool IsLive(string setKey) => LiveKeys.Contains(setKey);

    public IEnumerable<SbcChallenge> Challenges => _challengesById.Values;

    /// <summary>
    /// Where SBC rewards pay each pack, for the pack catalogue's "used by" and its delete check: every catalogue set, live or
    /// not, since listing one again would pay it. "SBC Bronze Upgrade (UPG_BRONZE)".
    /// </summary>
    public Dictionary<long, List<string>> PackUses()
    {
        var uses = new Dictionary<long, List<string>>();
        foreach (var set in Sets)
        {
            var label = $"SBC {JsonValues.Text(set.Source.Get("name"), set.Key)} ({set.Key})";
            var sources = set.Challenges.Select(challenge => challenge.Source).Prepend(set.Source);
            foreach (var entry in sources.SelectMany(source => (source.Obj("rewards")?.Arr("entries") ?? new JsonArray()).OfType<JsonObject>()))
            {
                if (!JsonValues.Text(entry.Get("type")).Equals("PACK", StringComparison.OrdinalIgnoreCase) || (entry.Obj("attributes")?.Int("packId") ?? 0) is not (var packId and > 0))
                    continue;
                var list = uses.TryGetValue(packId, out var known) ? known : uses[packId] = [];
                if (!list.Contains(label)) list.Add(label);
            }
        }
        return uses;
    }

    public SbcSet? FindSet(int id) => _setsById.GetValueOrDefault(id);

    public SbcSet? FindSet(string key) => Sets.FirstOrDefault(set => set.Key == key);

    public SbcChallenge? FindChallenge(int id) => _challengesById.GetValueOrDefault(id);

    /// <summary>The bundled catalogue with the panel's edits and live list. A broken edit throws: start-up and a publish refuse it.</summary>
    public static SbcCatalog Load(string dataRoot, ILogger logger, SbcContentRows content)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(Path.Combine(dataRoot, "sbc.json"))) as JsonObject
                ?? throw new InvalidDataException("sbc.json must contain an object");
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            logger.LogWarning("SBC catalogue unavailable ({Error}); SBC screen will be empty", error.Message);
            root = new JsonObject { ["tabs"] = new JsonArray(), ["sets"] = new JsonArray() };
        }
        // Outside the guard: a broken panel edit must refuse start-up and a publish, not empty the SBC screen.
        return new SbcCatalog(root, content);
    }
}
