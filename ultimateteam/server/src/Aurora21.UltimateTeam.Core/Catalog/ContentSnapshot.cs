using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>
/// The game content as read from PostgreSQL (the catalogue, the store packs, and the rules as rows since migration 073)
/// and the bundled files (sbc.json, chemistry and stadiums), before it is projected into catalogs. Start-up and a live
/// reload read it the same way, so a reload validates exactly what start-up validates.
/// </summary>
public sealed record ContentSources(RelationalCatalogSnapshot Catalog, PackRules Packs, GameRules Rules,
    StadiumCatalog Stadiums, ChemistryModel Chemistry, SbcCatalog Sbc)
{
    public static async Task<ContentSources> LoadAsync(RuntimePaths paths, NpgsqlDataSource source, ILogger log,
        bool allowLegacy, CancellationToken cancellationToken = default)
    {
        // The catalogue, the store packs (migration 041) and the rules (065) come from one read transaction, so loading
        // needs no extra connection.
        var (catalog, packs, documents, tables, sbcSets) = await RelationalCatalogLoader.LoadContentAsync(source, cancellationToken, allowLegacy);
        return WithFiles(paths, log, RulesFrom(documents, packs, tables), catalog, packs, sbcSets);
    }

    /// <summary>
    /// The content as an open unit of work sees it, uncommitted changes included: a content publish validates the
    /// revision its own transaction would commit. The schema was checked when the server started.
    /// </summary>
    public static async Task<ContentSources> LoadAsync(IUnitOfWork work, RuntimePaths paths, ILogger log,
        CancellationToken cancellationToken = default)
    {
        var catalog = await work.LoadCatalogSnapshotAsync();
        var packs = (await StorePackCatalog.LoadAsync(work, cancellationToken)).Rules;
        return WithFiles(paths, log, RulesFrom(await RuleRows.DocumentsAsync(work), packs, await ModeRewardRepository.TablesAsync(work)),
            catalog, packs, await SbcContentRows.ReadAsync(work));
    }

    private static GameRules RulesFrom(IReadOnlyDictionary<string, string> documents, PackRules packs, ModeRewardTables tables)
    {
        var rules = GameRules.FromDocuments(documents, packs, tables, file => $"rules/{file}.json in the database");
        Services.Market.MarketMaintenanceJob.ValidateInterval(TimeSpan.FromSeconds(rules.Market.MaintenanceIntervalSeconds));
        foreach (var (mode, reward) in rules.Seasons.MatchCompletionRewards)
            if (reward.PackId is { } packId && (packId == Services.Store.LegacyMatchPackService.PackId || packs.Pack(packId) is null || packs.PlayerPickPackIds.Contains(packId)))
                throw new InvalidDataException($"Completion reward {mode} must name an existing pack, not a player pick: {packId}.");
        if (rules.Objectives.LiveProblem() is { } live)
            throw new InvalidDataException(live);
        if (rules.Objectives.PackIds.FirstOrDefault(packId => packs.Pack(packId) is null) is var missing and not 0)
            throw new InvalidDataException($"An objective or Season Pass reward names pack {missing}, which does not exist.");
        return rules;
    }

    private static ContentSources WithFiles(RuntimePaths paths, ILogger log, GameRules rules, RelationalCatalogSnapshot catalog, PackRules packs,
        SbcContentRows sbcSets)
    {
        var chemistry = ChemistryModel.Load(paths.DataRoot);
        var sbc = SbcCatalog.Load(paths.DataRoot, log, sbcSets);
        CheckSbcRules(rules.Sbc, chemistry, sbc);
        return new ContentSources(catalog, packs, rules, StadiumCatalog.Load(paths.DataRoot, log), chemistry, sbc);
    }

    /// <summary>The SBC settings that name other content: the default formation (chemistry.json) and the fallback tab art (sbc.json tabs).</summary>
    private static void CheckSbcRules(SbcRules rules, ChemistryModel chemistry, SbcCatalog sbc)
    {
        if (!chemistry.Formations.ContainsKey(rules.Formation))
            throw new InvalidDataException($"The default SBC formation {rules.Formation} is not a formation in chemistry.json.");
        var tabs = (sbc.Root.Arr("tabs") ?? new JsonArray()).OfType<JsonObject>().Select(tab => tab.Int("id")).ToList();
        if (tabs.Count > 0 && !tabs.Contains(rules.Images.FallbackTabId))
            throw new InvalidDataException($"The fallback SBC tab {rules.Images.FallbackTabId} is not one of the game's SBC tabs.");
    }
}

/// <summary>
/// One immutable revision of the game content: the catalogs, the rules and what is derived from them. Requests
/// read it through <see cref="LiveContent"/>; a reload builds a new one and swaps it in whole.
/// </summary>
public sealed class ContentSnapshot
{
    private ContentSnapshot(long revision, DateTimeOffset loadedAt, ContentSources sources, PlayerCatalog players, Cards cards)
    {
        Revision = revision;
        LoadedAt = loadedAt;
        Catalog = sources.Catalog;
        PromoAvailability = new PromoAvailability(
            sources.Catalog.Players.ToDictionary(card => card.DefinitionId, card => card.CardTypeId),
            sources.Catalog.Promos, sources.Catalog.PromoCards,
            sources.Catalog.PromoEvents, sources.Catalog.PromoEventPromos, sources.Catalog.IconRotations);
        PromoCardSelector = new PromoCardSelector(sources.Catalog.Promos, sources.Catalog.PromoBuckets,
            sources.Catalog.PromoCards, sources.Catalog.Definitions
                .Where(definition => definition.NativeResourceId is not null)
                .ToDictionary(definition => definition.DefinitionId, definition => definition.NativeResourceId!.Value.Value));
        Rules = sources.Rules;
        Players = players;
        Cards = cards;
        Managers = ManagerCatalog.FromRelationalSnapshot(sources.Catalog);
        Kits = KitCatalog.FromRelationalSnapshot(sources.Catalog);
        Stadiums = sources.Stadiums;
        Chemistry = sources.Chemistry;
        Sbc = sources.Sbc;
        _drops = new Lazy<NonPlayerDropTable>(() => NonPlayerDropTable.Build(this));
    }

    private readonly Lazy<NonPlayerDropTable> _drops;

    /// <summary>Counts up from 1 (start-up) within one server process.</summary>
    public long Revision { get; }
    public DateTimeOffset LoadedAt { get; }
    public RelationalCatalogSnapshot Catalog { get; }
    public PromoAvailability PromoAvailability { get; }
    public PromoCardSelector PromoCardSelector { get; }
    public GameRules Rules { get; }
    public PlayerCatalog Players { get; }
    public Cards Cards { get; }
    public ManagerCatalog Managers { get; }
    public KitCatalog Kits { get; }
    public StadiumCatalog Stadiums { get; }
    public ChemistryModel Chemistry { get; }
    public SbcCatalog Sbc { get; }
    /// <summary>Every non-player item packs hand out, and how they are drawn.</summary>
    public NonPlayerDropTable Drops => _drops.Value;

    /// <param name="packRules">The live store pack rules (<see cref="PackSettings"/>), which change without a reload.</param>
    public static ContentSnapshot Create(ContentSources sources, long revision, Func<PackRules> packRules, DateTimeOffset loadedAt)
    {
        ValidatePromoReferences(sources);
        var players = PlayerCatalog.FromRelationalSnapshot(sources.Catalog);
        if (sources.Rules.Objectives.PlayerIds.FirstOrDefault(id => !players.Contains(id)) is var missingPlayer and not 0)
            throw new InvalidDataException($"An objective or Season Pass reward names player {missingPlayer}, which does not exist.");
        var cards = new Cards(players, sources.Rules.Cards, packRules, sources.Rules.Market,
            sources.Catalog.ManagerLeagueCards());
        var snapshot = new ContentSnapshot(revision, loadedAt, sources, players, cards);
        foreach (var (_, node) in sources.Packs.StorePacks.Concat(sources.Packs.TokenOffers))
        {
            if (node is not JsonObject config) continue;
            _ = Services.Store.MixedPackContents.Read(config);
            if (config.Bool("enabled") && Services.Store.PackOpeningService.MixedAvailability(config, snapshot, loadedAt) is { } issue)
                throw new InvalidDataException($"Pack {config.Text("name")} cannot be enabled: {issue}");
        }
        return snapshot;
    }

    private static void ValidatePromoReferences(ContentSources sources)
    {
        var byId = sources.Catalog.Promos.ToDictionary(p => p.PromoId);
        var byCode = sources.Catalog.Promos.ToDictionary(p => p.Code);
        var categories = byCode.Values.Select(p => p.OddsCategory).ToHashSet();
        foreach (var weight in sources.Rules.Draft.Picks.CategoryWeights.Concat(sources.Rules.Draft.Picks.WeakRoundCategoryWeights))
            if (!categories.Contains(weight.Key)) throw new InvalidDataException($"Draft names unknown category {weight.Key}.");
        var enabledEvents = sources.Catalog.PromoEvents.Where(e => e.Enabled).Select(e => e.EventId).ToHashSet();
        foreach (var link in sources.Catalog.PromoEventPromos.Where(l => l.Enabled && enabledEvents.Contains(l.EventId)))
        {
            var promo = byId[link.PromoId];
            if (promo.Status != "active" || PromoAvailability.IsIconTier(promo.Code)
                || Content.PromoEdits.StandardTypes.ContainsKey(promo.Code) || promo.Code is "fut_champions" or "developer_cards")
                throw new InvalidDataException($"Event {link.EventId} references inactive or restricted promo {promo.Code}.");
        }
        foreach (var entry in sources.Packs.StorePacks)
        {
            if (entry.Value is not JsonObject pack) continue;
            void Validate(JsonObject config)
            {
                if (config["promo_codes"] is not JsonArray codes) return;
                foreach (var node in codes)
                {
                    var code = node?.GetValue<string>() ?? "";
                    if (!byCode.TryGetValue(code, out var promo) || promo.Status != "active" || code is "fut_champions" or "developer_cards")
                        throw new InvalidDataException($"Pack {entry.Key} references inactive or restricted promo {code}.");
                }
            }
            Validate(pack);
            if (pack["player_picks"] is JsonArray picks)
                foreach (var group in picks.OfType<JsonObject>()) Validate(group);
        }
    }
}

/// <summary>
/// The server's current <see cref="ContentSnapshot"/>, shared by every club context of the process and replaced whole
/// by <see cref="ReloadAsync"/>. A unit of work (a FUT request, a Blaze frame, a panel request, a job pass) calls
/// <see cref="Pin"/> first, so everything it reads comes from one revision even if a reload lands meanwhile. Pin per
/// unit of work only: Task.Run and timers copy the pin into what they start, so a pin at connection or start-up scope
/// would freeze everything started under it.
/// </summary>
public sealed class LiveContent
{
    private readonly AsyncLocal<ContentSnapshot?> _pinned = new();
    private readonly SemaphoreSlim _reloads = new(1, 1);
    private ContentSnapshot _current;

    public LiveContent(ContentSnapshot initial) => _current = initial;

    /// <summary>The snapshot this unit of work pinned, or the current one when nothing is pinned.</summary>
    public ContentSnapshot Snapshot
    {
        get
        {
            if (_pinned.Value is { } pinned)
            {
                return pinned;
            }
            UnpinnedReadObserver?.Invoke();
            return Volatile.Read(ref _current);
        }
    }

    /// <summary>The newest snapshot, whatever this unit of work pinned.</summary>
    public ContentSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Test-only: called on every read outside a pin, to find a unit of work that does not pin.</summary>
    internal Action? UnpinnedReadObserver { get; set; }

    /// <summary>
    /// Pins the current snapshot for this asynchronous flow until the returned value is disposed. Inside an existing
    /// pin it keeps that one, so one unit of work never sees two revisions.
    /// </summary>
    public ContentPin Pin()
    {
        if (_pinned.Value is not null)
        {
            return default;
        }
        _pinned.Value = Volatile.Read(ref _current);
        return new ContentPin(this);
    }

    /// <summary>
    /// Builds the next revision and makes it current. A build that throws changes nothing. Reloads run one at a time;
    /// units of work that pinned the old snapshot finish on it.
    /// </summary>
    public async Task<ContentSnapshot> ReloadAsync(Func<long, CancellationToken, Task<ContentSnapshot>> build,
        CancellationToken cancellationToken = default)
    {
        await _reloads.WaitAsync(cancellationToken);
        try
        {
            var revision = Current.Revision + 1;
            var next = await build(revision, cancellationToken);
            if (next.Revision != revision)
            {
                throw new InvalidOperationException($"A content reload built revision {next.Revision}, not {revision}.");
            }
            Volatile.Write(ref _current, next);
            return next;
        }
        finally
        {
            _reloads.Release();
        }
    }

    /// <summary>Ends a <see cref="Pin"/>; the default value (a nested pin) does nothing.</summary>
    public readonly struct ContentPin : IDisposable
    {
        private readonly LiveContent? _owner;

        internal ContentPin(LiveContent owner) => _owner = owner;

        public void Dispose()
        {
            if (_owner is not null)
            {
                _owner._pinned.Value = null;
            }
        }
    }
}
