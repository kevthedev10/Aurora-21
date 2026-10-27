using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core;

/// <summary>
/// Everything a request handler needs: configuration, game rules, the read-only catalogs, the
/// club account, pooled unit-of-work runner and logging. Built once at start-up; the catalogs and rules come from
/// <see cref="Live"/>, which <see cref="ReloadContentAsync"/> replaces without a restart.
/// </summary>
public sealed class GameContext : IDisposable
{
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private GameContext()
    {
    }

    // Isolated fixture entrypoint: requires an installed package and no legacy tables.
    internal static async Task<GameContext> CreateRelationalSliceAsync(RuntimePaths paths, ILoggerFactory loggers,
        TimeProvider? time, string connectionString, Action<string>? observer = null)
    {
        paths.EnsureDirectories();
        var user = new UserConfigStore(paths);
        var config = ServerConfig.Load(user);
        // The bundled rules files must read: they fill the rules rows of a fresh database and add new keys.
        _ = GameRules.Load(paths.DataRoot);
        var log = loggers.CreateLogger("UltimateTeam.RelationalSlice");
        var database = ClubDatabase.Connect(paths, true, connectionString, config.Database, log);
        try
        {
            await using (var check = database.DataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_tables WHERE schemaname='public')"))
                if ((bool)(await check.ExecuteScalarAsync())!)
                    throw new RuntimeSetupException("The incomplete relational slice cannot attach to a database containing public tables.");
            var runner = new ClubUnitOfWorkRunner(database.DataSource, time ?? TimeProvider.System,
                config.Database.DeadlockRetries, log, observer);
            await runner.RunAsync(0, "rules-fill", w => FillRulesAsync(w, paths, log));
            var sources = await ContentSources.LoadAsync(paths, database.DataSource, log, allowLegacy: false);
            var opened = await runner.RunAsync(0, "relational-slice-open", w => w.EnsureRelationalClubAsync(
                new ClubIdentity(config.PersonaId, config.NucleusId), config.InitialCredits, config.DisplayName));
            var a = opened.Account;
            var packSettings = new PackSettings(user, sources.Packs);
            var live = new LiveContent(ContentSnapshot.Create(sources, 1, () => packSettings.Rules, (time ?? TimeProvider.System).GetUtcNow()));
            var game = new GameContext {
                Paths = paths, Config = config, Live = live, AllowLegacyCatalog = false,
                Packs = packSettings,
                Content = NewContentCache(paths, live, log, runner), Database = database,
                UnknownRequests = config.DebugUnknownHttp ? new Web.UnknownRequestLog(paths.LogsDirectory) : null,
                Account = new ClubAccount(a.ClubId, a.Established, a.Sid, a.Token, opened.Created), UnitOfWork = runner,
                Loggers = loggers, TestMode = true, Player = PlayerIdentity.SinglePlayer(config), Time = time ?? TimeProvider.System,
                MathGroups = NewMathGroups(paths, live, loggers),
                DesyncQuarantine = NewDesyncQuarantine(live, loggers, time),
                DatabaseCommandObserver = observer,
            };
            runner.BindRelationalItems(new RelationalItemCompatibilityStore(new Items.RelationalItemWireMapper(game)));
            return game;
        }
        catch { database.Dispose(); throw; }
    }

    public required RuntimePaths Paths { get; init; }
    private ServerConfig _config = null!;

    /// <summary>This club's configuration; its club name and abbreviation change once, when FIFA creates the club.</summary>
    public required ServerConfig Config
    {
        get => Volatile.Read(ref _config);
        init => _config = value;
    }

    /// <summary>
    /// Whether FIFA creates this club itself (its first-run country and club screens, then CreateUser) instead of the
    /// server seeding it at open. The hosted server's player clubs; see ultimateteam/onboarding.md.
    /// </summary>
    public bool NativeClubCreation { get; init; }

    /// <summary>From now on this club carries the name and abbreviation the player chose when FIFA created it.</summary>
    internal void UseChosenClubIdentity(string name, string abbreviation) =>
        Volatile.Write(ref _config, Config.WithClub(name, abbreviation));

    /// <summary>
    /// The live game content, shared by every club context of this process. The catalog and rule properties below read
    /// the snapshot the current unit of work pinned (<see cref="LiveContent.Pin"/>), so a reload reaches open clubs.
    /// </summary>
    public required LiveContent Live { get; init; }

    /// <summary>Whether a content reload may read a database that still has legacy tables (false only for the isolated slice).</summary>
    private bool AllowLegacyCatalog { get; init; } = true;

    public GameRules Rules => Live.Snapshot.Rules;
    public required PackSettings Packs { get; init; }

    /// <summary>The shared whole-market active count for hub loads; every player context of this process uses the same one.</summary>
    public Services.Market.ActiveListingCountCache ActiveListingCount { get; init; } = new();

    /// <summary>Settled competition weeks and cached live standings, shared by every club this process serves.</summary>
    public Services.Modes.CompetitionCache Competitions { get; init; } = new();

    /// <summary>Each player's Windows maths group for online pairing, shared by every club this process serves.</summary>
    public required Sessions.MathGroups MathGroups { get; init; }

    /// <summary>Clients that desync with everyone, kept apart in online pairing (shared by every club, in memory).</summary>
    public required Sessions.DesyncQuarantine DesyncQuarantine { get; init; }

    /// <summary>Players kept out of online searches for leaving matches early (shared by every club, in memory).</summary>
    public Sessions.LeaverCooldown LeaverCooldown { get; init; } = new();

    /// <summary>Whether new online and single-player matches may start, shared by every club this process serves.</summary>
    public Sessions.MatchGate MatchGate { get; init; } = new();

    /// <summary>The shared record of requests no route handled (null when <c>debug_unknown_http</c> is off).</summary>
    public Web.UnknownRequestLog? UnknownRequests { get; init; }
    public PlayerCatalog Players => Live.Snapshot.Players;
    public Cards Cards => Live.Snapshot.Cards;
    /// <summary>Captures one content revision and draw time for every card check in a draw.</summary>
    public (Func<long, bool> General, Func<long, bool> Icon, PromoCardSelector Selector,
        IReadOnlySet<long> ActivePromos) CaptureCardDrawAt(DateTimeOffset now, JsonObject? config = null)
        => CaptureCardDraw(Live.Snapshot, now, config);

    public static (Func<long, bool> General, Func<long, bool> Icon, PromoCardSelector Selector,
        IReadOnlySet<long> ActivePromos) CaptureCardDraw(ContentSnapshot snapshot, DateTimeOffset now, JsonObject? config = null)
    {
        var includeGeneral = config?.Get("include_general_pool") is null || config.Bool("include_general_pool");
        var activePromos = includeGeneral ? snapshot.PromoAvailability.ActivePromoIds(now).ToHashSet() : new HashSet<long>();
        var requested = config?.Arr("promo_codes")?.Select(node => node?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal) ?? [];
        var specificPromos = new HashSet<long>();
        foreach (var code in requested)
        {
            var promo = snapshot.Catalog.Promos.SingleOrDefault(promo => promo.Code == code && promo.Status == "active")
                ?? throw new InvalidOperationException($"Unknown or inactive promo source: {code}");
            if (promo.Code is "fut_champions" or "developer_cards")
                throw new InvalidOperationException($"{code} cannot be a general pack or pick source");
            activePromos.Add(promo.PromoId);
            specificPromos.Add(promo.PromoId);
        }
        if (!includeGeneral && activePromos.Count == 0)
            throw new InvalidOperationException("A pack without the general pool needs at least one specific promo");
        var namedCards = specificPromos.Count == 0 ? new HashSet<DefinitionId>()
            : snapshot.Catalog.PromoCards.Where(member => specificPromos.Contains(member.PromoId))
                .Select(member => member.PlayerCardDefinitionId).ToHashSet();
        bool General(long resourceId)
        {
            var player = snapshot.Players.Find(resourceId);
            if (player is null || player.Rareflag == 18) return false; // Champions reds are reward-only.
            var definition = snapshot.Catalog.ResolveDefinition(2021, 1, new NativeResourceId(resourceId));
            return definition is not null && ((includeGeneral && snapshot.PromoAvailability.IsAvailable(definition.DefinitionId, now))
                || namedCards.Contains(definition.DefinitionId));
        }
        bool Icon(long resourceId) =>
            snapshot.Catalog.ResolveDefinition(2021, 1, new NativeResourceId(resourceId)) is { } definition
            && (snapshot.PromoAvailability.IsIcon(definition.DefinitionId, now)
                || snapshot.PromoCardSelector.Contains("icon", resourceId, activePromos));
        return (General, Icon, snapshot.PromoCardSelector, activePromos);
    }

    public Func<long, bool> GeneralCardAvailabilityAt(DateTimeOffset now) => CaptureCardDrawAt(now).General;

    public Func<long, bool> IconCardAt() => CaptureCardDrawAt(Time.GetUtcNow()).Icon;
    public PromoAvailability PromoAvailability => Live.Snapshot.PromoAvailability;
    public ManagerCatalog Managers => Live.Snapshot.Managers;
    public KitCatalog Kits => Live.Snapshot.Kits;
    public StadiumCatalog Stadiums => Live.Snapshot.Stadiums;

    /// <summary>FIFA's squad chemistry model (runtime-source/ultimateteam21/chemistry.json).</summary>
    public ChemistryModel Chemistry => Live.Snapshot.Chemistry;
    public SbcCatalog Sbc => Live.Snapshot.Sbc;

    /// <summary>The relational catalogue revision the item wire mapper renders from.</summary>
    internal RelationalCatalogSnapshot Catalog => Live.Snapshot.Catalog;
    public required ContentCache Content { get; init; }
    public required ClubDatabase Database { get; init; }
    private ClubAccount _account = null!;
    /// <summary>The club row this context opened; refreshed after an exclusive operator reset.</summary>
    public required ClubAccount Account { get => Volatile.Read(ref _account); init => _account = value; }

    /// <summary>Refreshes rotated account values while the registry holds exclusive reset access to this club.</summary>
    public void RefreshResetAccount(ClubAccount account)
    {
        if (account.ClubId != ClubId) throw new InvalidOperationException("A reset cannot switch a context to another club.");
        Volatile.Write(ref _account, account);
        _utSid = null;
        UtAuthSid = null;
    }

    /// <summary>Root transactions on the pooled data source (Phase 4); shared by every player context.</summary>
    public required ClubUnitOfWorkRunner UnitOfWork { get; init; }

    public required ILoggerFactory Loggers { get; init; }
    public required bool TestMode { get; init; }
    public required PlayerIdentity Player { get; init; }
    public required TimeProvider Time { get; init; }

    /// <summary>Test-only SQL observation inherited by every player context.</summary>
    private Action<string>? DatabaseCommandObserver { get; init; }

    /// <summary>False for a player's club context, which shares the server's database.</summary>
    private bool OwnsDatabase { get; init; } = true;

    /// <summary>Set by HTTP match settlement; the Blaze session pushes a UserUpdated notification.</summary>
    public PendingSignal UserSessionRefresh { get; } = new();

    /// <summary>This context's club.</summary>
    public long ClubId => Account.ClubId;

    /// <summary>
    /// The club's established time, read once when the club opened (it never changes while it is open), so
    /// rendering items costs no query.
    /// </summary>
    public long Established => Account.Established;

    /// <summary>The club's session id, read when the club opened.</summary>
    public string Sid => Account.Sid;

    /// <summary>The club's auth token, read when the club opened.</summary>
    public string Token => Account.Token;

    private string? _utSid;

    /// <summary>
    /// The club's <c>X-UT-SID</c>, derived once from the SID loaded with the account. A running server changes a
    /// club's SID only when it creates the club; clearing one is a test fixture operation and the offline
    /// <c>reset-club</c> command is followed by a fresh open.
    /// </summary>
    public string UtSid => _utSid ??= Services.Club.ClubPresentationService.UtSid(Account.Sid);

    private string? _utAuthSid;

    /// <summary>
    /// In the session identity mode, the random <c>X-UT-SID</c> the <c>/ut/auth</c> being answered issues (set by the
    /// HTTP endpoint for that request); null in the other modes, where the reply carries the club-derived one.
    /// </summary>
    public string? UtAuthSid
    {
        get => Volatile.Read(ref _utAuthSid);
        set => Volatile.Write(ref _utAuthSid, value);
    }

    /// <summary>A durable command on this club: one pooled transaction, committed when <paramref name="work"/> returns.</summary>
    public Task<T> RunAsync<T>(string operation, Func<UnitOfWork, ValueTask<T>> work, CancellationToken cancellationToken) =>
        UnitOfWork.RunAsync(ClubId, operation, work, cancellationToken);

    public Task RunAsync(string operation, Func<UnitOfWork, ValueTask> work, CancellationToken cancellationToken) =>
        UnitOfWork.RunAsync(ClubId, operation, work, cancellationToken);

    /// <summary>A durable command repeated after a deadlock or serialization failure.</summary>
    public Task<T> RunRetryableAsync<T>(string operation, Func<UnitOfWork, ValueTask<T>> work, CancellationToken cancellationToken) =>
        UnitOfWork.RunRetryableAsync(ClubId, operation, work, cancellationToken);

    /// <summary>A read of this club in a short pooled transaction.</summary>
    public Task<T> ReadAsync<T>(Func<UnitOfWork, ValueTask<T>> work, CancellationToken cancellationToken) =>
        UnitOfWork.RunReadAsync(ClubId, work, cancellationToken);

    public long NowSeconds => Time.GetUtcNow().ToUnixTimeSeconds();

    public long NowMilliseconds => Time.GetUtcNow().ToUnixTimeMilliseconds();

    public ILogger Logger(string category) => Loggers.CreateLogger(category);

    /// <param name="connectionString">An explicit PostgreSQL server; otherwise the environment or the managed local cluster.</param>
    public static Task<GameContext> CreateAsync(RuntimePaths paths, ILoggerFactory loggers, bool testMode, TimeProvider? time = null,
        string? connectionString = null, CancellationToken cancellationToken = default)
        => CreateAsync(paths, loggers, testMode, time, connectionString, databaseCommandObserver: null, cancellationToken);

    /// <summary>Creates a context with test-only application SQL observation.</summary>
    internal static async Task<GameContext> CreateAsync(RuntimePaths paths, ILoggerFactory loggers, bool testMode, TimeProvider? time,
        string? connectionString, Action<string>? databaseCommandObserver, CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        var log = loggers.CreateLogger("UltimateTeam.Startup");
        var userConfig = new UserConfigStore(paths);
        var config = ServerConfig.Load(userConfig);
        // The bundled rules files must read: they fill the rules rows of a fresh database and add new keys.
        var rules = GameRules.Load(paths.DataRoot);
        _ = config.Shutdown;
        Services.Market.MarketMaintenanceJob.ValidateInterval(TimeSpan.FromSeconds(rules.Market.MaintenanceIntervalSeconds));

        var database = ClubDatabase.Connect(paths, testMode, connectionString, config.Database, loggers.CreateLogger("UltimateTeam.Database"));
        try
        {
            await DatabasePreflight.CheckAsync(database.DataSource, database.Settings, loggers.CreateLogger("UltimateTeam.Database"), cancellationToken);
            var runner = new ClubUnitOfWorkRunner(database.DataSource, time ?? TimeProvider.System, config.Database.DeadlockRetries,
                loggers.CreateLogger("UltimateTeam.UnitOfWork"), databaseCommandObserver);
            await RelationalSchemaBootstrap.InstallForRuntimeAsync(database.DataSource, cancellationToken);
            // From here on the server reads its rules from the database rows (migration 073).
            await runner.RunAsync(0, "rules-fill", w => FillRulesAsync(w, paths, log), cancellationToken);
            var sources = await ContentSources.LoadAsync(paths, database.DataSource, log, allowLegacy: true, cancellationToken);
            var packSettings = new PackSettings(userConfig, sources.Packs);
            var live = new LiveContent(ContentSnapshot.Create(sources, 1, () => packSettings.Rules, (time ?? TimeProvider.System).GetUtcNow()));
            log.LogInformation("Loaded {Count} relational player definitions", live.Current.Players.Count);
            var opened = await runner.RunAsync(0, "open-relational-club", w => w.EnsureRelationalClubAsync(
                new ClubIdentity(config.PersonaId, config.NucleusId), config.InitialCredits, config.DisplayName), cancellationToken);
            var a = opened.Account;
            var account = new ClubAccount(a.ClubId, a.Established, a.Sid, a.Token, opened.Created);
            log.LogInformation("Club {ClubId} for persona {PersonaId} in {Database}", account.ClubId, config.PersonaId, database.Describe());
            var game = new GameContext
            {
                Paths = paths,
                Config = config,
                Live = live,
                Packs = packSettings,
                Content = NewContentCache(paths, live, loggers.CreateLogger("UltimateTeam.Content"), runner),
                Database = database,
                UnknownRequests = config.DebugUnknownHttp ? new Web.UnknownRequestLog(paths.LogsDirectory) : null,
                Account = account,
                UnitOfWork = runner,
                Loggers = loggers,
                TestMode = testMode,
                Player = PlayerIdentity.SinglePlayer(config),
                Time = time ?? TimeProvider.System,
                DatabaseCommandObserver = databaseCommandObserver,
                MathGroups = NewMathGroups(paths, live, loggers),
                DesyncQuarantine = NewDesyncQuarantine(live, loggers, time),
            };
            runner.BindRelationalItems(new RelationalItemCompatibilityStore(new Items.RelationalItemWireMapper(game)));
            return game;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens (creating on first use) another player's club: the same rules, catalogs, database and
    /// connection pool, with that player's own ids and session signal.
    /// </summary>
    /// <param name="nativeClubCreation">A new club waits for FIFA to create it (<see cref="NativeClubCreation"/>).</param>
    public async Task<GameContext> ForPlayerAsync(PlayerIdentity player, CancellationToken cancellationToken = default,
        bool nativeClubCreation = false)
    {
        // Each player of a shared server is named after its own recorded name (Python server 62ac9ad), not the placeholder.
        var displayName = player.IsSinglePlayer ? null : PlayerNames.Persona(player.Id, PlayerNames.Recorded(Paths.PlayerNamesPath, player.Id));
        // Its club is its own too, unless the server configures one (Python server CLUB_IDENTITY).
        var config = Config.ForPlayer(player, displayName, Rules.Club.ClubNames);
        // The schema was checked when this process opened its own club.
        var account = await ClubOpen.EnsureClubAsync(UnitOfWork, new ClubIdentity(player.PersonaId, player.NucleusId), config.InitialCredits,
            cancellationToken);
        var chosen = await UnitOfWork.RunAsync(account.ClubId, "open-club-setup", async uow =>
        {
            _ = displayName is null ? await uow.SetDisplayNameIfMissingAsync(config.DisplayName) : await uow.SetDisplayNameAsync(displayName);
            return await uow.ChosenClubIdentityAsync();
        }, cancellationToken);
        // A club FIFA created keeps the name and abbreviation its player typed; the persona stays the recorded name.
        if (chosen is { } club)
        {
            config = config.WithClub(club.Name, club.Abbreviation);
        }
        return new GameContext
        {
            NativeClubCreation = nativeClubCreation,
            Paths = Paths,
            Config = config,
            Live = Live,
            AllowLegacyCatalog = AllowLegacyCatalog,
            Packs = Packs,
            Content = Content,
            Database = Database,
            Account = account,
            UnitOfWork = UnitOfWork,
            Loggers = Loggers,
            TestMode = TestMode,
            Player = player,
            Time = Time,
            ActiveListingCount = ActiveListingCount,
            Competitions = Competitions,
            MathGroups = MathGroups,
            DesyncQuarantine = DesyncQuarantine,
            LeaverCooldown = LeaverCooldown,
            MatchGate = MatchGate,
            UnknownRequests = UnknownRequests,
            DatabaseCommandObserver = DatabaseCommandObserver,
            OwnsDatabase = false,
        };
    }

    /// <summary>
    /// Reads the game content again (the PostgreSQL catalogue, store packs and rules rows, and the bundled SBC, chemistry
    /// and stadium files) through the start-up loader and its validation, and makes it current for every club this process
    /// serves. A load that fails changes nothing.
    /// </summary>
    public Task<ContentSnapshot> ReloadContentAsync(CancellationToken cancellationToken = default) =>
        Live.ReloadAsync(async (revision, token) =>
        {
            var sources = await ContentSources.LoadAsync(Paths, Database.DataSource, Logger("UltimateTeam.Content"), AllowLegacyCatalog, token);
            var next = ContentSnapshot.Create(sources, revision, () => Packs.Rules, Time.GetUtcNow());
            Packs.Publish(sources.Packs);
            return next;
        }, cancellationToken);

    /// <summary>
    /// Start-up filling, in one transaction: the rules rows from the bundled files (a fresh database, or keys a code update
    /// added), then the mode reward tables once (migrations 126, 131, 133 and 135: converted from the stored rules, or the bundled seeds),
    /// then SBC reward pack names to ids once (SbcRewardPackConversion).
    /// </summary>
    private static async ValueTask<IReadOnlyList<string>> FillRulesAsync(IUnitOfWork work, RuntimePaths paths, ILogger log)
    {
        var added = await RuleRows.FillAsync(work, GameRules.ReadFiles(paths.DataRoot), log);
        await ModeRewardRepository.FillAsync(work, paths.DataRoot, log);
        await ModeRewardRepository.FillRivalsAsync(work, paths.DataRoot, log);
        await ModeRewardRepository.FillSquadBattlesAsync(work, paths.DataRoot, log);
        await ModeRewardRepository.FillDraftAsync(work, paths.DataRoot, log);
        await SbcRewardPackConversion.FillAsync(work, paths.DataRoot, log);
        // Live objective groups that never started (a fresh database) start now (migration 143).
        if ((await RuleRows.DocumentsAsync(work)).TryGetValue("objectives", out var objectives))
            await ObjectiveStartRepository.StartLiveAsync(work, RulesJson.Deserialize<ObjectiveRules>(objectives, "rules/objectives.json in the database"));
        return added;
    }

    private static ContentCache NewContentCache(RuntimePaths paths, LiveContent live, ILogger log, ClubUnitOfWorkRunner runner) =>
        new(paths, () => { var content = live.Snapshot; return (content.Players, content.Cards); }, log)
        {
            // Uploaded heads (migration 074), read from PostgreSQL the first time the game asks for one.
            CustomHeads = async (headId, token) => (await runner.RunReadAsync(0, work => work.QueryAsync(
                "SELECT dds FROM ultimateteam.custom_player_heads WHERE head_id=@id", row => (byte[])row.GetValue(0), ("id", headId)), token))
                .FirstOrDefault(),
        };

    private static Sessions.DesyncQuarantine NewDesyncQuarantine(LiveContent live, ILoggerFactory loggers, TimeProvider? time) =>
        new(() => live.Snapshot.Rules.Seasons.DesyncQuarantine, () => (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds(),
            loggers.CreateLogger("UltimateTeam.DesyncQuarantine"));

    private static Sessions.MathGroups NewMathGroups(RuntimePaths paths, LiveContent live, ILoggerFactory loggers) =>
        new(paths.WindowsBuildsPath, () => live.Snapshot.Rules.Seasons.MathGroups, loggers.CreateLogger("UltimateTeam.MathGroups"));

    public void Dispose()
    {
        if (OwnsDatabase)
        {
            // Player contexts share the server context's content cache, database and pool.
            var disposal = StopAsync();
            _ = disposal.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    public Task StopAsync()
    {
        if (!OwnsDatabase) return Task.CompletedTask;
        lock (_disposeGate) return _disposeTask ??= DisposeOwnedAsync();
    }

    private async Task DisposeOwnedAsync()
    {
        // Even a forced host deadline cannot dispose a pool underneath a transaction still rolling back.
        try
        {
            await Task.WhenAll(Content.StopAsync(), UnitOfWork.StopAsync()).ConfigureAwait(false);
        }
        finally
        {
            Database.Dispose();
            UnknownRequests?.Dispose();
        }
    }
}

/// <summary>A thread-safe one-shot flag: set by one component, consumed once by another.</summary>
public sealed class PendingSignal
{
    private int _set;

    public bool IsSet => Volatile.Read(ref _set) != 0;

    public void Set() => Interlocked.Exchange(ref _set, 1);

    public bool TryConsume() => Interlocked.Exchange(ref _set, 0) != 0;

    public void Clear() => Interlocked.Exchange(ref _set, 0);
}
