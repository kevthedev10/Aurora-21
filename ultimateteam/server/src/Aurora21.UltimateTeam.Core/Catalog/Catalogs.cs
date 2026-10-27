using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>FUT manager card definitions exported from cards_ng_db (resource ids from 1,000,000).</summary>
public sealed class ManagerCatalog
{
    private readonly List<KeyValuePair<long, JsonObject>> _entries = new();
    private readonly Dictionary<long, JsonObject> _byId = new();
    private bool _copyOnRead;

    public IReadOnlyList<KeyValuePair<long, JsonObject>> Entries => _copyOnRead
        ? Array.AsReadOnly(_entries.Select(e => new KeyValuePair<long, JsonObject>(e.Key, (JsonObject)e.Value.DeepClone())).ToArray())
        : _entries;

    public int Count => _entries.Count;

    public bool Contains(long resourceId) => _byId.ContainsKey(resourceId);

    public JsonObject? Find(long resourceId) => _copyOnRead
        ? _byId.GetValueOrDefault(resourceId)?.DeepClone() as JsonObject : _byId.GetValueOrDefault(resourceId);

    /// <summary>The highest rated manager (ties go to the lowest id).</summary>
    public long? BestManagerId() =>
        _entries.Count == 0 ? null : _entries.OrderByDescending(e => e.Value.Int("rating")).ThenBy(e => e.Key).First().Key;

    public static ManagerCatalog Load(string dataRoot, ILogger logger)
    {
        var catalog = new ManagerCatalog();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(dataRoot, "managers.json"))) is not JsonObject root)
            {
                throw new InvalidDataException("manager catalog must be a JSON object");
            }
            foreach (var (key, value) in root)
            {
                if (value is JsonObject meta && long.TryParse(key, out var id))
                {
                    if (catalog._byId.ContainsKey(id))
                    {
                        catalog._entries.RemoveAll(entry => entry.Key == id);
                    }
                    catalog._byId[id] = meta;
                    catalog._entries.Add(new(id, meta));
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            logger.LogWarning("Could not load FIFA 21 manager catalog: {Error}", error.Message);
        }
        return catalog;
    }

    public static ManagerCatalog FromRelationalSnapshot(RelationalCatalogSnapshot snapshot, int gameYear = 2021)
    {
        var catalog = new ManagerCatalog { _copyOnRead = true };
        var managers = snapshot.Managers.ToDictionary(m => m.DefinitionId);
        var extensions = snapshot.Extensions.ToDictionary(e => e.DefinitionId);
        foreach (var definition in snapshot.Definitions)
        {
            if (definition.GameYear != gameYear) continue;
            if (!managers.TryGetValue(definition.DefinitionId, out var manager)) continue;
            var id = definition.NativeResourceId?.Value ?? throw new InvalidDataException("Manager has no native resource ID.");
            var projected = RelationalStaffAndKitProjection.Manager(definition, manager, extensions.GetValueOrDefault(manager.DefinitionId));
            catalog._byId.Add(id, projected);
            catalog._entries.Add(new(id, projected));
        }
        return catalog;
    }
}

/// <summary>Installed FIFA 21 kit definitions (kits.json).</summary>
public sealed class KitCatalog
{
    private readonly IReadOnlyList<JsonObject> _kits;
    private readonly bool _copyOnRead;

    public KitCatalog(IReadOnlyList<JsonObject> kits) : this(kits, copyOnRead: false) { }

    private KitCatalog(IReadOnlyList<JsonObject> kits, bool copyOnRead)
    {
        _kits = kits;
        _copyOnRead = copyOnRead;
    }

    public IReadOnlyList<JsonObject> Kits => _copyOnRead
        ? Array.AsReadOnly(_kits.Select(k => (JsonObject)k.DeepClone()).ToArray()) : _kits;

    public int Count => _kits.Count;

    public static KitCatalog FromRelationalSnapshot(RelationalCatalogSnapshot snapshot, int gameYear = 2021)
    {
        // This compatibility view reproduces the current kits.json candidate list.
        // Club-rule virtual defaults remain available in the typed snapshot for
        // slot rendering, but must not become additional pack candidates.
        var kits = snapshot.Customisations.Where(k => k.CustomisationKind == "kit" && k.PackEligible).ToDictionary(k => k.DefinitionId);
        var extensions = snapshot.Extensions.ToDictionary(e => e.DefinitionId);
        var projected = snapshot.Definitions.Where(d => d.GameYear == gameYear
                && kits.ContainsKey(d.DefinitionId) && d.NativeResourceId is { Value: > 0 })
            .Select(d => RelationalStaffAndKitProjection.Kit(d, kits[d.DefinitionId], extensions.GetValueOrDefault(d.DefinitionId))).ToArray();
        return new KitCatalog(Array.AsReadOnly(projected), copyOnRead: true);
    }

    public static KitCatalog Load(string dataRoot, ILogger logger)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dataRoot, "kits.json")));
            var kits = (root is JsonObject obj ? obj.Arr("kits") : null) ?? new JsonArray();
            return new KitCatalog(kits.OfType<JsonObject>().Where(kit => kit.Int("resourceId") > 0).ToList());
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            logger.LogWarning("Could not load FIFA 21 kit catalog: {Error}", error.Message);
            return new KitCatalog(Array.Empty<JsonObject>());
        }
    }
}

public sealed record Stadium(long Id, string Name, long Capacity);

/// <summary>Physical FIFA 21 venue ids recovered from the game's stadium preview assets.</summary>
public sealed class StadiumCatalog
{
    /// <summary>Used when stadiums.json is missing or names no default: the Sanderson Park venue.</summary>
    private const long FallbackDefaultId = 261;
    private const long FallbackCapacity = 45000;

    private readonly Dictionary<long, Stadium> _byId;

    public StadiumCatalog(IReadOnlyList<Stadium> stadiums, long defaultId)
    {
        Stadiums = stadiums;
        DefaultId = defaultId;
        _byId = stadiums.GroupBy(stadium => stadium.Id).ToDictionary(group => group.Key, group => group.Last());
    }

    public IReadOnlyList<Stadium> Stadiums { get; }

    public long DefaultId { get; }

    public int Count => Stadiums.Count;

    public Stadium? Find(long id) => _byId.GetValueOrDefault(id);

    public static StadiumCatalog Load(string dataRoot, ILogger logger)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dataRoot, "stadiums.json"))) as JsonObject ?? new JsonObject();
            var defaultId = root.Int("default", FallbackDefaultId);
            var stadiums = (root.Arr("stadiums") ?? new JsonArray())
                .OfType<JsonObject>()
                .Where(s => s.Int("id") > 0)
                .Select(s =>
                {
                    var id = s.IntKeep("id");
                    return new Stadium(id, s.Text("name", $"Stadium {id}"), s.Int("capacity", FallbackCapacity));
                })
                .ToList();
            return new StadiumCatalog(stadiums, defaultId);
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            logger.LogWarning("Could not load FIFA 21 stadium catalog: {Error}", error.Message);
            return new StadiumCatalog(Array.Empty<Stadium>(), FallbackDefaultId);
        }
    }
}
