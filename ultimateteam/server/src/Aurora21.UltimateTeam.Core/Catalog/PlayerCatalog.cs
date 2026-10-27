using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>
/// The FIFA 21 card catalog, keyed by resource id. Iteration order is stable (players.json,
/// custom-players.json, then migrated ids) because seeded Draft generation consumes candidates
/// in that order.
/// </summary>
public sealed class PlayerCatalog
{
    /// <summary>High-byte revision namespace used to keep custom avatars off base player ids.</summary>
    public const long ResourceRevisionSize = 0x01000000;

    /// <summary>The rareflag of imported WeFUT Icon Moments avatars.</summary>
    private const long WeFutIconMomentsRareflag = 84;

    private readonly List<long> _order = new();
    private readonly Dictionary<long, PlayerMeta> _entries = new();
    private readonly Dictionary<long, PlayerMeta> _byAsset = new();
    private readonly Dictionary<long, long> _migrations = new();
    private readonly List<JsonDocument> _documents = new();
    private IReadOnlyList<KeyValuePair<long, PlayerMeta>>? _snapshot;

    internal IReadOnlyDictionary<long, IReadOnlyList<long>>? NativeRarityGroups { get; private set; }

    public int Count => _entries.Count;

    /// <summary>Old zero-revision custom ids mapped to their revision-namespace replacements.</summary>
    public IReadOnlyDictionary<long, long> ResourceIdMigrations => new ReadOnlyDictionary<long, long>(_migrations);

    /// <summary>Entries in load order.</summary>
    public IReadOnlyList<KeyValuePair<long, PlayerMeta>> Entries =>
        _snapshot ??= _order.Select(id => new KeyValuePair<long, PlayerMeta>(id, _entries[id])).ToList();

    public IEnumerable<long> Ids => Entries.Select(entry => entry.Key);

    /// <summary>Player positions that have at least one 99-rated card.</summary>
    public IReadOnlySet<string> NinetyNinePositions { get; private set; } = new HashSet<string>();

    public bool Contains(long resourceId) => _entries.ContainsKey(resourceId);

    public PlayerMeta? Find(long resourceId) => _entries.GetValueOrDefault(resourceId);

    public PlayerMeta? FindByAsset(long assetId) => _byAsset.GetValueOrDefault(assetId);

    /// <summary>
    /// The rating of a player's base card (rare flag 0 or 1, not special) by asset id, or null when the catalogue has none.
    /// Pack odds v3 lets a special whose base card sits in a lower tier also drop there, as an upgraded special.
    /// </summary>
    public long? BaseRating(long assetId) => _baseRatings.TryGetValue(assetId, out var rating) ? rating : null;

    private Dictionary<long, long> _baseRatings = new();

    public static PlayerCatalog Load(string dataRoot, ILogger logger)
    {
        var catalog = new PlayerCatalog();
        foreach (var file in new[] { "players.json", "custom-players.json" })
        {
            var path = Path.Combine(dataRoot, file);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                using var stream = File.OpenRead(path);
                var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("player catalog must be a JSON object");
                }
                catalog.AddDocument(document);
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
            {
                logger.LogWarning("Could not load player catalog {File}: {Error}", file, error.Message);
            }
        }
        catalog.MigrateCustomAvatarIds();
        catalog.BuildIndexes();
        return catalog;
    }

    /// <summary>Adapter for existing synchronous consumers; does not reread player JSON or rerun ID migration.</summary>
    public static PlayerCatalog FromRelationalSnapshot(RelationalCatalogSnapshot snapshot, int gameYear = 2021)
    {
        var catalog = new PlayerCatalog();
        var types = snapshot.CardTypes.ToDictionary(t => t.CardTypeId);
        var typeGroups = snapshot.RarityGroups.GroupBy(g => g.CardTypeId).ToDictionary(g => g.Key,
            g => g.Select(row => row.NativeGroupId).Distinct().Order().ToArray());
        var nativeGroups = new Dictionary<long, IReadOnlyList<long>>();
        foreach (var type in snapshot.CardTypes.Where(t => t.NativeRareflag.HasValue))
        {
            var membership = typeGroups.GetValueOrDefault(type.CardTypeId) ?? Array.Empty<long>();
            var code = type.NativeRareflag!.Value;
            if (nativeGroups.TryGetValue(code, out var previous) && !previous.SequenceEqual(membership))
                throw new InvalidDataException("Rarity groups differ across tiers for the same native rarity code.");
            nativeGroups[code] = Array.AsReadOnly(membership);
        }
        catalog.NativeRarityGroups = nativeGroups.ToFrozenDictionary();
        var players = snapshot.Players.ToDictionary(p => p.DefinitionId);
        var extensions = snapshot.Extensions.ToDictionary(e => e.DefinitionId);
        foreach (var definition in snapshot.Definitions)
        {
            if (definition.GameYear != gameYear) continue;
            if (!players.TryGetValue(definition.DefinitionId, out var player)) continue;
            if (definition.NativeResourceId is not { } resource)
                throw new InvalidDataException("Player definition has no native resource ID.");
            var projected = RelationalPlayerProjection.Project(definition, player, types[player.CardTypeId],
                snapshot.FootballersById[player.FootballerId], extensions.GetValueOrDefault(player.DefinitionId));
            var meta = new PlayerMeta(JsonSerializer.SerializeToElement(projected), readOnly: true);
            catalog.Put(resource.Value, meta);
            var oldImage = meta.Int("dynamicImageResourceId");
            if (meta.Rareflag == WeFutIconMomentsRareflag && oldImage > 0 && oldImage < ResourceRevisionSize
                && resource.Value >= ResourceRevisionSize)
                catalog._migrations.Add(oldImage, resource.Value);
        }
        catalog.BuildIndexes();
        catalog._snapshot = Array.AsReadOnly(catalog._order.Select(id =>
            new KeyValuePair<long, PlayerMeta>(id, catalog._entries[id])).ToArray());
        catalog.NinetyNinePositions = catalog.NinetyNinePositions.ToFrozenSet();
        return catalog;
    }

    public IReadOnlyDictionary<long, int> SpecialPoolCounts() =>
        Entries.Where(entry => entry.Value.Rareflag > 1)
            .GroupBy(entry => entry.Value.Rareflag)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Count());

    private void AddDocument(JsonDocument document)
    {
        _documents.Add(document);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object || !long.TryParse(property.Name, out var id))
            {
                continue;
            }
            Put(id, new PlayerMeta(property.Value));
        }
    }

    private void Put(long id, PlayerMeta meta)
    {
        if (!_entries.ContainsKey(id))
        {
            _order.Add(id);
        }
        _entries[id] = meta;
        _snapshot = null;
    }

    private void Remove(long id)
    {
        if (_entries.Remove(id))
        {
            _order.Remove(id);
            _snapshot = null;
        }
    }

    /// <summary>
    /// WeFUT Icon Moments avatars were imported in the zero-revision namespace
    /// where FIFA 21 resolves them as unrelated base players. Publish them in a revision
    /// namespace while keeping the original id for the dynamic head image.
    /// </summary>
    private void MigrateCustomAvatarIds()
    {
        foreach (var (oldId, oldMeta) in Entries.ToList())
        {
            if (oldMeta.Rareflag != WeFutIconMomentsRareflag || oldId >= ResourceRevisionSize)
            {
                continue;
            }
            var newId = oldId + ResourceRevisionSize;
            while (_entries.ContainsKey(newId))
            {
                newId += ResourceRevisionSize;
            }
            var meta = oldMeta.CloneWithOverrides();
            meta.Override("resourceId", newId);
            meta.Override("dynamicImageResourceId", oldId);
            var replacements = meta.Get("replacesResourceIds") is JsonArray existing
                ? existing.Select(node => node?.DeepClone()).ToList()
                : new List<JsonNode?>();
            if (!replacements.Any(node => JsonValues.IsStrictInt(node, out var value) && value == oldId))
            {
                replacements.Add(oldId);
            }
            meta.Override("replacesResourceIds", new JsonArray(replacements.ToArray()));
            Remove(oldId);
            Put(newId, meta);
            _migrations[oldId] = newId;
        }
    }

    private void BuildIndexes()
    {
        _byAsset.Clear();
        var baseRatings = new Dictionary<long, long>();
        var ninetyNine = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, meta) in Entries)
        {
            var asset = meta.Int("assetId");
            if (asset != 0)
            {
                _byAsset.TryAdd(asset, meta);
                // A player with several base versions counts as his highest one.
                if (!meta.Special && meta.Rareflag is 0 or 1 && meta.Rating > baseRatings.GetValueOrDefault(asset))
                    baseRatings[asset] = meta.Rating;
            }
            if (meta.Rating == 99)
            {
                ninetyNine.Add(meta.Position.ToUpperInvariant());
            }
        }
        NinetyNinePositions = ninetyNine;
        _baseRatings = baseRatings;
    }
}
