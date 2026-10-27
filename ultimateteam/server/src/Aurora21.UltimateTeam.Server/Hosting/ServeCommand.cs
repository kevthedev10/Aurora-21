using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Market;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Server.Relay;
using Aurora21.UltimateTeam.Server.Diagnostics;
using Aurora21.UltimateTeam.Server.Http;
using Aurora21.UltimateTeam.Server.Lsx;
using Aurora21.UltimateTeam.Server.Panel;
using Aurora21.UltimateTeam.Server.Qos;
using Microsoft.AspNetCore.Connections.Features;

namespace Aurora21.UltimateTeam.Server.Hosting;

public enum LsxMode
{
    Auto,
    Off,
    Required,
}

/// <summary>How requests are assigned to players.</summary>
public enum PlayerIdentityMode
{
    /// <summary>Every request is the configured local player's.</summary>
    Single,

    /// <summary>Each trusted peer IPv4 address (LAN or VPN) is its own player with its own club.</summary>
    PeerIp,

    /// <summary>
    /// Each player is identified by a verified credential, never an address (native-hosting-investigation.md): FUT by an
    /// issued auth code and its <c>X-UT-SID</c> session; Blaze by the same code before club selection.
    /// Replacement LSX needs the explicit loopback local test option; hosted LSX remains unbound.
    /// </summary>
    Session,
}

/// <summary>The bounded UDP relay that carries gameplay between two matched players.</summary>
public sealed record GameplayRelayOptions
{
    public bool Enabled { get; init; }

    /// <summary>The address relay sockets bind to; null uses the server host.</summary>
    public string? BindAddress { get; init; }

    /// <summary>The address players are told to send to; null uses the address their Blaze connection reached.</summary>
    public string? AdvertisedAddress { get; init; }

    public int PortStart { get; init; } = 31000;
    public int PortEnd { get; init; } = 31199;
}

/// <summary>
/// The launcher API on a loopback port behind the operator's HTTPS reverse proxy: <c>/client/*</c> for the Aurora
/// launcher and <c>/api/v1/launch/code</c>. Without an issuer and a key it still answers health, presence, trust anchors
/// and install downloads, and refuses launches as not configured, so a server can run before the account service's key
/// arrives.
/// </summary>
public sealed record LaunchApiOptions(int Port)
{
    /// <summary>The account service's exact <c>iss</c> claim.</summary>
    public string? Issuer { get; init; }

    /// <summary>ES256: the account service's P-256 public key (SPKI PEM). Exclusive with <see cref="SecretPath"/>.</summary>
    public string? PublicKeyPath { get; init; }

    /// <summary>HS256: a file holding the secret shared only with the account service.</summary>
    public string? SecretPath { get; init; }

    /// <summary>ES256 key given as text (the environment on the VPS): SPKI PEM, or its base64 body on one line.</summary>
    public string? PublicKeyText { get; init; }

    /// <summary>HS256 secret given as text (the environment on the VPS).</summary>
    public string? SecretText { get; init; }

    /// <summary>How many account-service keys were given (exactly one is needed with an issuer).</summary>
    public int KeySources => new[] { PublicKeyPath, SecretPath, PublicKeyText, SecretText }.Count(source => source is not null);

    /// <summary>Optional: the folder holding the client pack's <c>manifest.json</c> and files.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>The TLS redirector the health check connects to; null reports it up.</summary>
    public DnsEndPoint? RedirectorHealth { get; init; } = new("127.0.0.1", Ports.TlsRedirector);

    public bool IdentityConfigured => Issuer is not null;
}

/// <summary>A separate loopback listener for the operator panel; never publish it through the launcher proxy.</summary>
/// <summary>
/// The shared Aurora operator panel (Aurora 16's panel page and its /panel/api contract) on a loopback port, reached
/// through an SSH tunnel; the tunnel is the credential, as it is for Aurora 16. Never publish this port.
/// </summary>
/// <param name="BackupDirectory">The database backup folder (read-only here) the panel lists and asks the backup service to add to.</param>
/// <param name="AssetsDirectory">The panel images folder; null serves the runtime's panel-assets folder.</param>
public sealed record PanelApiOptions(int Port, string? BackupDirectory = null, string? AssetsDirectory = null);

public sealed record ServeOptions(string Host, LsxMode LsxMode, int? HttpOnlyTestPort, bool Capture)
{
    public PlayerIdentityMode IdentityMode { get; init; } = PlayerIdentityMode.Single;
    public PlayerRegistryOptions Players { get; init; } = new();
    public GameplayRelayOptions Relay { get; init; } = new();
    public bool LocalSessionTest { get; init; }
    public TlsProbeOptions TlsProbe { get; init; } = TlsProbeOptions.Off;

    /// <summary>
    /// A loopback Blaze listener that speaks TLS with the redirector certificate (native-hosting-investigation.md gate 5),
    /// beside the plaintext one; the redirector's <c>--blaze-tls-port</c> points FIFA at it. Null when off.
    /// </summary>
    public int? BlazeTlsProbePort { get; init; }

    /// <summary>
    /// The hosted profile (native-hosting-investigation.md): players reach this server over the network. It needs session
    /// identity, TLS for every advertised service, the Blaze TLS listener, the advertised host name and LSX off. The TLS
    /// listeners and QoS bind to <see cref="Host"/>; the plaintext game listeners bind to loopback only. A player's new club
    /// waits for FIFA to create it (<see cref="PlayerRegistryOptions.NativeClubCreation"/>, ultimateteam/onboarding.md).
    /// </summary>
    public bool Hosted { get; init; }
    public LaunchApiOptions? LaunchApi { get; init; }
    public PanelApiOptions? PanelApi { get; init; }

    /// <summary>Rotates logs/ultimateteam.log to <c>.1</c> at this size (two files at most); 0 never rotates.</summary>
    public int LogMaxMegabytes { get; init; }
}

/// <summary>
/// Runs every FIFA 21 local service in one Kestrel host. Blaze, FUT, QoS (TCP+UDP) and SSF are
/// required; the legacy FUT port, EASW and the debug redirector are optional so EA App-owned ports
/// never abort start-up. LSX follows <see cref="LsxMode"/>.
/// </summary>
public static class ServeCommand
{
    /// <summary>The Discord bot token the role checks use; set from the VPS .env, never logged or passed on.</summary>
    public const string DiscordBotTokenVariable = "ULTIMATETEAM_DISCORD_BOT_TOKEN";

    private enum Service
    {
        Fut,
        FutTlsProbe,
        LegacyFut,
        Blaze,
        BlazeTlsProbe,
        EaswTlsProbe,
        SsfTlsProbe,
        QosHttp,
        Ssf,
        Easw,
        Redirect,
        Lsx,
        LaunchApi,
        PanelApi,
    }

    /// <param name="Bind">The address to bind; null uses the server's <c>--host</c>.</param>
    private sealed record Listener(Service Service, string Label, int Port, bool Required, IPAddress? Bind = null);

    public static async Task<int> RunAsync(ServeOptions options, string? dataRoot)
    {
        try
        {
            options.Players.Validate();
        }
        catch (ArgumentException)
        {
            throw new RuntimeSetupException("Invalid player limits: MaxOpenClubs (1..10000), ConcurrentOpens (1..32), IdleTimeout/ReapInterval (>0..1 day).");
        }
        if (!IPAddress.TryParse(options.Host, out var host))
        {
            throw new UsageException("--host must be an IP address");
        }
        // First, so a contradiction names --hosted rather than a later loopback-only check.
        if (options.Hosted &&
            (options.IdentityMode != PlayerIdentityMode.Session || options.TlsProbe.Mode != TlsProbeMode.All || options.BlazeTlsProbePort is null ||
             !options.TlsProbe.AdvertiseHostname || options.LsxMode != LsxMode.Off || options.LocalSessionTest || options.HttpOnlyTestPort is not null))
        {
            throw new UsageException("--hosted needs session identity, --tls-probe-mode all, a Blaze TLS port, the advertised host name and --lsx-mode off, " +
                                     "and cannot run the local session test or an HTTP-only test");
        }
        if (options.Hosted && host.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new UsageException("--hosted requires an IPv4 --host because the gameplay relay is IPv4-only");
        }
        if (options.LaunchApi is { } launchOptions &&
            (!options.Hosted || launchOptions.Port is < 1024 or > 65535 || Ports.Production.Contains(launchOptions.Port) ||
             launchOptions.Port == options.TlsProbe.Port || launchOptions.Port == options.BlazeTlsProbePort ||
             launchOptions.Port == options.TlsProbe.EaswPort || launchOptions.Port == options.TlsProbe.SsfPort ||
             !AbsoluteIfSet(launchOptions.InstallDirectory) ||
             (launchOptions.IdentityConfigured
                 ? string.IsNullOrWhiteSpace(launchOptions.Issuer) || launchOptions.KeySources != 1 ||
                   !AbsoluteIfSet(launchOptions.PublicKeyPath) || !AbsoluteIfSet(launchOptions.SecretPath)
                 : launchOptions.KeySources != 0)))
        {
            throw new UsageException("--launch-api-port needs --hosted and a distinct unprivileged port; an issuer needs exactly one of " +
                                     "--launch-assertion-public-key or --launch-assertion-secret-file; every path must be absolute");
        }
        // The panel listens on loopback only, so a local (non-hosted) server may serve it too: there it is how rules and
        // content are edited (live-content-editing-plan.md, step 6).
        if (options.PanelApi is { } panelOptions &&
            (panelOptions.Port is < 1024 or > 65535 ||
             Ports.Production.Contains(panelOptions.Port) || panelOptions.Port == options.LaunchApi?.Port ||
             panelOptions.Port == options.TlsProbe.Port || panelOptions.Port == options.BlazeTlsProbePort ||
             panelOptions.Port == options.TlsProbe.EaswPort || panelOptions.Port == options.TlsProbe.SsfPort ||
             !AbsoluteIfSet(panelOptions.BackupDirectory) || !AbsoluteIfSet(panelOptions.AssetsDirectory)))
        {
            throw new UsageException("--panel-api-port needs a distinct unprivileged port, and an absolute --backup-dir and --panel-assets-dir");
        }
        if (options.IdentityMode == PlayerIdentityMode.PeerIp && host.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new UsageException("--player-identity-mode peer-ip requires an IPv4 --host because the gameplay relay is IPv4-only");
        }
        if (options.HttpOnlyTestPort is { } testPort && (!IPAddress.IsLoopback(host) || testPort is < 1024 or > 65535 || Ports.Production.Contains(testPort)))
        {
            throw new UsageException("HTTP-only tests require loopback and an unprivileged, non-production test port");
        }
        if (options.LocalSessionTest && (options.IdentityMode != PlayerIdentityMode.Session || !IPAddress.IsLoopback(host)))
        {
            throw new UsageException("--local-session-test requires session identity mode on loopback");
        }
        if (options.TlsProbe.Mode != TlsProbeMode.Off &&
            ((!options.Hosted && !IPAddress.IsLoopback(host)) || options.TlsProbe.Port is < 1024 or > 65535 ||
             Ports.Production.Contains(options.TlsProbe.Port) || options.TlsProbe.Port == options.HttpOnlyTestPort))
        {
            throw new UsageException("TLS probe requires loopback and a free, unprivileged, non-production port");
        }
        foreach (var (selected, extraPort) in new[] { (options.TlsProbe.Easw, options.TlsProbe.EaswPort), (options.TlsProbe.Ssf, options.TlsProbe.SsfPort) })
        {
            if (selected && (extraPort > 65535 || Ports.Production.Contains(extraPort) || extraPort == options.HttpOnlyTestPort || extraPort == options.BlazeTlsProbePort))
            {
                throw new UsageException("The EASW and SSF TLS probes (the TLS probe port + 2 and + 3) need free, non-production ports");
            }
        }
        if (options.BlazeTlsProbePort is { } blazeTlsPort &&
            ((!options.Hosted && !IPAddress.IsLoopback(host)) || blazeTlsPort is < 1024 or > 65535 || Ports.Production.Contains(blazeTlsPort) ||
             blazeTlsPort == options.HttpOnlyTestPort || (options.TlsProbe.Mode != TlsProbeMode.Off && blazeTlsPort == options.TlsProbe.Port)))
        {
            throw new UsageException("The Blaze TLS probe requires loopback and a free, unprivileged, non-production port of its own");
        }
        var paths = RuntimePaths.Resolve(dataRoot);
        using var tlsProbeCertificate = options.TlsProbe.Mode == TlsProbeMode.Off && options.BlazeTlsProbePort is null ? null : LoadTlsProbeCertificate(paths);
        using var launchVerifier = options.LaunchApi is { IdentityConfigured: true } launchApi
            ? LoadLaunchVerifier(launchApi)
            : null;
        var testMode = options.HttpOnlyTestPort is not null;
        if (testMode)
        {
            paths.EnsureIsolatedTestRoots();
        }
        paths.EnsureDirectories();

        using var fileLog = new FileLoggerProvider(Path.Combine(paths.LogsDirectory, "ultimateteam.log"), options.LogMaxMegabytes * 1024L * 1024L);
        using var loggers = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddFilter("Microsoft", LogLevel.Warning);
            logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            });
            logging.AddProvider(fileLog);
        });
        var log = loggers.CreateLogger("UltimateTeam.Server");
        // Before the game context, so startup database work is measured too.
        using var metrics = MetricsExport.Start(Environment.GetEnvironmentVariable(MetricsExport.EndpointVariable), log);

        using var game = await GameContext.CreateAsync(paths, loggers, testMode);
        var latencyAdmission = options.Hosted
            ? new ConnectionLatencyAdmission(game.Config.ConnectionLimits.MaximumPingMilliseconds)
            : null;
        if (latencyAdmission is { MaximumPingMilliseconds: > 0 } limit)
        {
            log.LogInformation("BLAZE LATENCY LIMIT maxPingMs={MaxPingMs} source=linux-tcp-srtt admissionOnly=true unknown=allow",
                limit.MaximumPingMilliseconds);
            if (!OperatingSystem.IsLinux())
                log.LogWarning("Blaze latency admission cannot measure TCP RTT on this platform; connections will be allowed without a measurement.");
        }
        log.LogInformation("RUNTIME_LIMITS maxClubs={Clubs} concurrentOpens={Opens} idleMs={Idle} reapMs={Reap} maintenanceMs={Maintenance} drainMs={Drain}",
            options.Players.MaxOpenClubs, options.Players.ConcurrentOpens, options.Players.IdleTimeout.TotalMilliseconds,
            options.Players.ReapInterval.TotalMilliseconds, game.Rules.Market.MaintenanceIntervalSeconds * 1000L,
            game.Config.Shutdown.DrainTimeoutMilliseconds);
        var fut = await FutServices.CreateAsync(game);
        // Hosted players create their own clubs through FIFA's first-run screens; the server seeds no club for them.
        var playerOptions = options.Hosted ? options.Players with { NativeClubCreation = true } : options.Players;
        if (playerOptions.NativeClubCreation)
        {
            log.LogInformation("CLUB CREATION native: a new player's club is created by FIFA's first-run country and club screens");
        }
        using var players = new PlayerRegistry(fut, playerOptions, shared: options.IdentityMode is PlayerIdentityMode.PeerIp or PlayerIdentityMode.Session);
        if (options.IdentityMode == PlayerIdentityMode.Session)
        {
            // Issued codes survive a restart (by hash), so players' re-login and FUT re-auth after a deploy still work.
            players.NativeSessions = new NativeSessions(game.Config.NativeAuth,
                storePath: options.LocalSessionTest ? null : Path.Combine(game.Paths.DataDirectory, "native-auth-codes.json"));
            if (options.LocalSessionTest)
            {
                var code = Environment.GetEnvironmentVariable("ULTIMATETEAM21_LOCAL_TEST_CODE");
                Environment.SetEnvironmentVariable("ULTIMATETEAM21_LOCAL_TEST_CODE", null);
                if (code is null)
                {
                    throw new RuntimeSetupException("Local session test code was not supplied by the launcher.");
                }
                players.NativeSessions.RegisterLocalTestCode(code, players.Local.Player);
                players.LocalSessionTest = true;
                log.LogInformation("PLAYER IDENTITY local session test: one synthetic code registered for configured player");
            }
            else
            {
                log.LogWarning("PLAYER IDENTITY session: FUT and Blaze require issued auth codes; replacement LSX is refused");
            }
        }
        var marketMaintenance = new MarketMaintenanceJob(fut);
        using var capture = new PacketCapture(game, options.Capture || game.Config.CaptureDiagnostics);
        var http = new HttpEndpoints(players);
        // Hosted access (who may launch, maintenance, the player limit) lives in PostgreSQL and is edited from the panel;
        // the optional approval file only approves ids that have no row yet.
        // Verified Blaze sessions, recorded for the panel; rows a stopped run left open are closed first.
        var sessionLog = options.Hosted ? new PlayerSessionLog(game.UnitOfWork, loggers.CreateLogger("UltimateTeam.Access")) : null;
        if (sessionLog is not null) await sessionLog.CloseStaleAsync(CancellationToken.None);
        var presence = options.Hosted ? new PlayerPresence(null, sessionLog) : null;
        // Who may play is decided by Discord roles, asked through the bot whose token is only ever in the environment.
        var discordRoles = DiscordRoles.FromToken(Environment.GetEnvironmentVariable(DiscordBotTokenVariable));
        // The panel reads access and players through the issuer, so a local server with the panel builds one too; its launch,
        // Discord and maintenance features then simply show as not configured.
        var issuer = options.Hosted || options.PanelApi is not null
            ? new LaunchIssuer(launchVerifier, players.NativeSessions ?? new NativeSessions(game.Config.NativeAuth), await PlayerAccess.LoadAsync(game.UnitOfWork, CancellationToken.None),
                discordRoles, presence, game.MathGroups, () => game.NowSeconds, paths.PlayerNamesPath)
            : null;
        // The panel's saved match pause applies from start-up; a server without the panel keeps matches open.
        issuer?.Access.Settings.ApplyTo(game.MatchGate);
        var launchEndpoint = options.LaunchApi is null ? null : new LaunchCodeEndpoint(issuer!);
        if (options.Hosted && issuer is not null)
        {
            var access = issuer.Access.Settings;
            log.LogInformation("PLAYER ACCESS {Players} accounts ({Blocked} blocked); play roles {Roles}; Discord bot {Bot}; maintenance {Maintenance}, " +
                "limit {Limit}; launch identity {Identity}", issuer.Access.Players.Count, issuer.Access.Players.Count(player => player.Blocked),
                access.PlayRoles.Count == 0 ? "none (every verified account may play)" : access.PlayRoles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                discordRoles.TokenSet ? "token set" : "NO TOKEN", access.Maintenance ? "ON" : "off", access.MaxPlayers,
                launchVerifier is null ? "NOT CONFIGURED (launches are refused)" : launchVerifier.Algorithm);
            if (access.PlayRoles.Count > 0 && (!discordRoles.TokenSet || access.GuildId.Length == 0))
            {
                log.LogWarning("PLAYER ACCESS play roles are set but the Discord bot token or server id is missing: every launch is refused until both are set");
            }
        }
        using var relay = new GameplayRelay(options.Relay, loggers.CreateLogger("UltimateTeam.Relay"));
        try
        {
            relay.Start(options.Host);
        }
        catch (GameplayRelayException error)
        {
            log.LogError("FAILED REQUIRED gameplay relay configuration: {Error}", error.Message);
            return 2;
        }
        var matchmakingLog = loggers.CreateLogger("UltimateTeam.Matchmaking");
        var gameSpeed = new OnlineGameSpeed(BlazeSettings.Load().Online.GameSpeed);
        var pregameDeparture = new PregameDeparture(game.Config.Switches.PregameDepartureMode);
        var launcherFriends = new LauncherFriends();
        await SeedLeaverCooldownAsync(game, matchmakingLog);
        var matchmaking = new RivalsMatchmaking(new MatchmakingRegistry(relay, matchmakingLog, compatible: (first, second) => game.MathGroups.Compatible(first, second) && game.DesyncQuarantine.Compatible(first, second),
            pairing: () => game.MatchGate.Open, friends: launcherFriends.Mutual, friendsOf: launcherFriends.Of,
            online: player => presence?.IsOnline(player) ?? false, barredUntil: player => game.LeaverCooldown.Until(player, game.NowSeconds)),
            new MatchmakingPayloads(BlazeSettings.Load(), () => game.NowSeconds, gameSpeed: gameSpeed, pregameDeparture: pregameDeparture),
            relay, game.Config.Switches, capture, matchmakingLog);
        players.MatchRoster = new MatchmakingRoster(matchmaking.Registry);
        var clientEndpoint = options.LaunchApi is null ? null : new ClientApiEndpoint(issuer!, matchmaking.Registry, new ClientApiOptions(
            game.Config.LauncherApi, PublicGameAddress(options.Relay), paths.CertificateRoot, options.LaunchApi.InstallDirectory,
            options.LaunchApi.RedirectorHealth), leaderboardUi: () => LeaderboardUiManifest.Build(game.Rules), mathGroups: game.MathGroups,
            friends: launcherFriends);
        var contentSchedule = OperatorPanel.CreateContentSchedule(game);
        await contentSchedule.RunOnceAsync();
        // The backup service watches backup-requests in the runtime (state) volume; the panel only lists the dumps.
        var panelEndpoint = options.PanelApi is null ? null : new OperatorPanel(game, players, issuer!, matchmaking.Registry, new OperatorPanelOptions
        {
            Schedule = contentSchedule,
            LogsDirectory = paths.LogsDirectory,
            BackupDirectory = options.PanelApi.BackupDirectory,
            AssetsDirectory = options.PanelApi.AssetsDirectory ?? paths.PanelAssetsDirectory,
            BackupRequestDirectory = Path.Combine(paths.RuntimeRoot, "backup-requests"),
            RestartRequestDirectory = options.Hosted ? Environment.GetEnvironmentVariable("ULTIMATETEAM_RESTART_REQUESTS") : null,
            AdvertisedHost = PublicGameAddress(options.Relay) ?? options.Host,
            Services = PanelServices(options),
            GameSpeed = gameSpeed,
            PregameDeparture = pregameDeparture,
        });
        if (testMode)
        {
            var port = options.HttpOnlyTestPort!.Value;
            log.LogInformation("HTTP-ONLY TEST MODE port={Port} state={Root}", port, paths.RuntimeRoot);
            try
            {
                await marketMaintenance.RunOnceAsync();
            }
            catch (Exception error)
            {
                log.LogError("Initial market maintenance failed: {Error}", error.Message);
                return 2;
            }
            var testListeners = new List<Listener> { new(Service.Fut, "FUT-test", port, true) };
            if (panelEndpoint is not null)
                testListeners.Add(new Listener(Service.PanelApi, "Panel-API-test", options.PanelApi!.Port, true, IPAddress.Loopback));
            if (tlsProbeCertificate is not null && options.TlsProbe.Mode != TlsProbeMode.Off)
            {
                testListeners.Add(new Listener(Service.FutTlsProbe, "FUT-TLS-probe", options.TlsProbe.Port, true));
            }
            var testApp = BuildApp(testListeners, host, loggers, players, matchmaking, capture, http, options.TlsProbe, tlsProbeCertificate, launchEndpoint, panelEndpoint, clientEndpoint, presence, latencyAdmission, options.Hosted);
            using var testStopping = new CancellationTokenSource();
            using var testHostStopping = testApp.Lifetime.ApplicationStopping.Register(testStopping.Cancel);
            var testScheduleTask = contentSchedule.RunAsync(testStopping.Token);
            var testMaintenanceTask = marketMaintenance.RunAsync(testStopping.Token);
            var testShutdown = CreateShutdown(log, matchmaking.Registry, testApp, testStopping, new[] { testMaintenanceTask, testScheduleTask }, players, game, relay);
            var testClean = false;
            try
            {
                await testApp.StartAsync();
                try { await Task.Delay(Timeout.Infinite, testStopping.Token); }
                catch (OperationCanceledException) when (testStopping.IsCancellationRequested) { }
            }
            finally
            {
                testClean = await testShutdown.StopAsync();
                DisposeStoppedApp(testApp);
            }
            return testClean ? 0 : 2;
        }

        // Hosted: players reach only the TLS listeners and QoS; the plaintext game listeners stay on loopback.
        var plainBind = options.Hosted ? IPAddress.Loopback : null;
        var requested = new List<Listener>
        {
            new(Service.Fut, "FUT", Ports.Fut, true, plainBind),
            new(Service.Blaze, "Blaze", Ports.Blaze, true, plainBind),
            new(Service.QosHttp, "QoS-HTTP", Ports.Qos, true),
            new(Service.Ssf, "SSF", Ports.Ssf, true, plainBind),
            new(Service.LegacyFut, "FUT-compat", Ports.LegacyFut, false, plainBind),
            new(Service.Redirect, "Cleartext-redirector-debug", Ports.Redirect, false, plainBind),
            new(Service.Easw, "EASW", Ports.Easw, false, plainBind),
        };
        if (tlsProbeCertificate is not null && options.TlsProbe.Mode != TlsProbeMode.Off)
        {
            requested.Add(new Listener(Service.FutTlsProbe, "FUT-TLS-probe", options.TlsProbe.Port, true));
        }
        if (options.BlazeTlsProbePort is { } blazeTlsProbePort)
        {
            requested.Add(new Listener(Service.BlazeTlsProbe, "Blaze-TLS-probe", blazeTlsProbePort, true));
        }
        if (options.TlsProbe.Easw)
        {
            requested.Add(new Listener(Service.EaswTlsProbe, "EASW-TLS-probe", options.TlsProbe.EaswPort, true));
        }
        if (options.TlsProbe.Ssf)
        {
            requested.Add(new Listener(Service.SsfTlsProbe, "SSF-TLS-probe", options.TlsProbe.SsfPort, true));
        }
        if (launchEndpoint is not null)
        {
            requested.Add(new Listener(Service.LaunchApi, "Launch-API", options.LaunchApi!.Port, true, IPAddress.Loopback));
        }
        if (panelEndpoint is not null)
        {
            requested.Add(new Listener(Service.PanelApi, "Panel-API", options.PanelApi!.Port, true, IPAddress.Loopback));
        }
        switch (options.LsxMode)
        {
            case LsxMode.Off:
                log.LogInformation("Origin-LSX disabled; EA App/Origin is expected to own 127.0.0.1:{Port}", Ports.Lsx);
                break;
            case LsxMode.Required:
                requested.Add(new(Service.Lsx, "Origin-LSX", Ports.Lsx, true));
                break;
            default:
                requested.Add(new(Service.Lsx, "Origin-LSX", Ports.Lsx, false));
                break;
        }

        var active = new List<Listener>();
        var requiredFailed = false;
        foreach (var listener in requested)
        {
            var bind = listener.Bind ?? host;
            if (CanBindTcp(bind, listener.Port, out var error))
            {
                log.LogInformation("Bound {Kind} {Label} on {Host}:{Port}", listener.Required ? "REQUIRED" : "optional", listener.Label, bind, listener.Port);
                active.Add(listener);
            }
            else if (listener.Required)
            {
                log.LogError("FAILED REQUIRED {Label} on {Host}:{Port}: {Error}", listener.Label, bind, listener.Port, error);
                requiredFailed = true;
            }
            else
            {
                log.LogWarning("Skipping optional {Label} on {Host}:{Port}: {Error}", listener.Label, bind, listener.Port, error);
            }
        }

        QosUdpResponder? qosUdp = null;
        if (!requiredFailed)
        {
            try
            {
                qosUdp = new QosUdpResponder(host, Ports.Qos, loggers.CreateLogger("UltimateTeam.QosUdp"));
                log.LogInformation("Bound REQUIRED QoS-UDP on {Host}:{Port}/udp", host, Ports.Qos);
            }
            catch (SocketException error)
            {
                log.LogError("FAILED REQUIRED QoS-UDP on {Host}:{Port}/udp: {Error}", host, Ports.Qos, error.Message);
                requiredFailed = true;
            }
        }
        if (requiredFailed)
        {
            log.LogError("FIFA 21 cannot continue until the REQUIRED port above is free.");
            qosUdp?.Dispose();
            return 2;
        }

        using var qos = qosUdp!;
        try
        {
            await marketMaintenance.RunOnceAsync();
        }
        catch (Exception error)
        {
            log.LogError("Initial market maintenance failed: {Error}", error.Message);
            return 2;
        }
        var app = BuildApp(active, host, loggers, players, matchmaking, capture, http, options.TlsProbe, tlsProbeCertificate, launchEndpoint, panelEndpoint, clientEndpoint, presence, latencyAdmission, options.Hosted);
        try
        {
            await app.StartAsync();
        }
        catch (IOException error)
        {
            log.LogError("Failed to start listeners: {Error}", error.Message);
            DisposeStoppedApp(app);
            return 2;
        }

        using var stopping = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        // The host's console lifetime turns SIGTERM/SIGQUIT (and console close) into ApplicationStopping.
        using var hostStopping = app.Lifetime.ApplicationStopping.Register(stopping.Cancel);
        var qosTask = qos.RunAsync(stopping.Token);
        var contentScheduleTask = contentSchedule.RunAsync(stopping.Token);
        var marketMaintenanceTask = marketMaintenance.RunAsync(stopping.Token);
        var shutdown = CreateShutdown(log, matchmaking.Registry, app, stopping, new[] { qosTask, marketMaintenanceTask, contentScheduleTask }, players, game, relay);
        var clean = false;
        try
        {

            bool IsActive(Service service) => active.Any(listener => listener.Service == service);
            // Each listener's actual address: in the hosted profile the plaintext ones are loopback-only.
            string At(Service service, int port) => $"{active.FirstOrDefault(listener => listener.Service == service)?.Bind ?? host}:{port}";
            Console.WriteLine(new string('=', 70));
            Console.WriteLine(options.Hosted ? "FIFA21 Ultimate Team (C#) READY - HOSTED PROFILE" : "FIFA21 Ultimate Team (C#) READY");
            Console.WriteLine($" REQUIRED Blaze : {At(Service.Blaze, Ports.Blaze)}  OK");
            Console.WriteLine($" REQUIRED FUT   : http://{At(Service.Fut, Ports.Fut)}/  OK");
            var lsxLabel = options.LsxMode == LsxMode.Off ? "DISABLED (EA App owns it)" : IsActive(Service.Lsx) ? "OK" : "SKIPPED";
            Console.WriteLine($" LSX mode       : {options.LsxMode.ToString().ToLowerInvariant()} / {At(Service.Lsx, Ports.Lsx)}  {lsxLabel}");
            Console.WriteLine($" REQUIRED QoS   : {host}:{Ports.Qos} tcp+udp  OK");
            Console.WriteLine($" REQUIRED SSF   : http://{At(Service.Ssf, Ports.Ssf)}/sf/auth  OK");
            foreach (var (service, label, port) in new[]
                     {
                         (Service.FutTlsProbe, "FUT/Nucleus", options.TlsProbe.Port), (Service.BlazeTlsProbe, "Blaze", options.BlazeTlsProbePort ?? 0),
                         (Service.EaswTlsProbe, "EASW", options.TlsProbe.EaswPort), (Service.SsfTlsProbe, "SSF", options.TlsProbe.SsfPort),
                     }.Where(entry => IsActive(entry.Item1)))
            {
                Console.WriteLine($" TLS {label,-11}: {At(service, port)}  OK");
            }
            Console.WriteLine($" Gameplay relay : {(relay.Active ? $"{relay.BindAddress}:{options.Relay.PortStart}-{options.Relay.PortEnd}/udp" : "DISABLED")}");
            Console.WriteLine($" Optional EASW  : http://{At(Service.Easw, Ports.Easw)}/  {(IsActive(Service.Easw) ? "OK" : "SKIPPED")}");
            Console.WriteLine($" Optional FUTc  : http://{At(Service.LegacyFut, Ports.LegacyFut)}/  {(IsActive(Service.LegacyFut) ? "OK" : "SKIPPED")}");
            Console.WriteLine($" Optional RDBG  : {At(Service.Redirect, Ports.Redirect)}  {(IsActive(Service.Redirect) ? "OK" : "SKIPPED")}");
            var localCredits = players.IsShared ? 0 : await game.ReadAsync(uow => uow.CreditsAsync(), CancellationToken.None);
            Console.WriteLine(!players.IsShared
                ? $" Local club     : {game.Config.ClubName} / {game.Config.DisplayName} / {localCredits} coins"
                : options.IdentityMode == PlayerIdentityMode.Session
                    ? $" Player state   : one club per verified session code; max {options.Players.MaxOpenClubs}, idle {options.Players.IdleTimeout.TotalMinutes:g} min"
                    : $" Player state   : one club per trusted peer IPv4; max {options.Players.MaxOpenClubs}, idle {options.Players.IdleTimeout.TotalMinutes:g} min");
            Console.WriteLine($" State/logs     : {paths.RuntimeRoot}");
            Console.WriteLine($" Diagnostics    : {(capture.Enabled ? "packet capture ON" : "off")}");
            Console.WriteLine(new string('=', 70));
            log.LogInformation("Required FIFA 21 localhost services ready");

            try
            {
                await Task.Delay(Timeout.Infinite, stopping.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            clean = await shutdown.StopAsync();
            DisposeStoppedApp(app);
        }
        return clean ? 0 : 2;
        // After this method returns, the using declarations dispose players (club state), then game (store and database).
    }

    /// <summary>Loads the leaver penalties still running (leaver_penalties) for matchmaking; an old database without the table has none.</summary>
    private static async Task SeedLeaverCooldownAsync(GameContext game, ILogger log)
    {
        try
        {
            var penalties = await game.ReadAsync(uow => uow.ActivePenaltiesAsync(game.NowSeconds), CancellationToken.None);
            foreach (var (player, offence, until) in penalties)
            {
                game.LeaverCooldown.Set(player, new LeaverCooldown.Penalty(offence, until));
            }
            log.LogInformation("LEAVER PENALTIES loaded {Count} running ({Bans} bans)", penalties.Count, penalties.Count(row => row.Until is null));
        }
        catch (Npgsql.PostgresException error)
        {
            log.LogWarning("LEAVER PENALTIES not loaded: {Error}", error.MessageText);
        }
    }

    private static void DisposeStoppedApp(WebApplication app)
    {
        // StopAsync already used the overall deadline. Do not introduce a second unbounded host stop
        // on the forced path; disposal continues as owned cleanup, with any fault explicitly observed.
        var disposal = app.DisposeAsync().AsTask();
        _ = disposal.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>
    /// Stops in dependency order: no new pairing, no new requests (in-flight ones finish and connections close),
    /// every club lease released, relay allocations freed. Club state and the database are disposed last.
    /// Readiness reporting is added with the management endpoints (deployment Phase 6).
    /// </summary>
    private static HostShutdown CreateShutdown(ILogger log, MatchmakingRegistry matchmaking, WebApplication app,
        CancellationTokenSource stopping, Task[] jobs, PlayerRegistry players, GameContext game, GameplayRelay relay)
    {
        Task jobsCancellation = Task.CompletedTask;
        return new HostShutdown(game.Config.Shutdown.DrainTimeout, log,
            () =>
            {
                matchmaking.StopAccepting();
                players.RequestStop();
                game.Content.RequestStop();
                game.UnitOfWork.RequestStop();
                jobsCancellation = stopping.CancelAsync();
            },
            token => app.StopAsync(token),
            () => Task.WhenAll(jobs.Concat(new[] { players.StopAsync(), game.Content.StopAsync(), game.UnitOfWork.StopAsync(), jobsCancellation })),
            token => players.WaitForDrainAsync(game.Config.Shutdown.DrainTimeout, cancellationToken: token),
            relay.Dispose);
    }

    private static WebApplication BuildApp(IReadOnlyList<Listener> listeners, IPAddress host, ILoggerFactory loggers,
        PlayerRegistry players, RivalsMatchmaking matchmaking, PacketCapture capture, HttpEndpoints http,
        TlsProbeOptions tlsProbe, X509Certificate2? tlsProbeCertificate, LaunchCodeEndpoint? launchEndpoint,
        OperatorPanel? panelEndpoint, ClientApiEndpoint? clientEndpoint, PlayerPresence? presence, ConnectionLatencyAdmission? latencyAdmission = null,
        bool hosted = false)
    {
        var game = players.Local.Game;
        var keepAlive = hosted ? game.Config.TcpKeepAlive : null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggers);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = game.Config.Shutdown.DrainTimeout);
        builder.Services.AddSingleton(new BlazeConnectionHandler(players, matchmaking, capture, tlsProbe, presence, latencyAdmission));
        builder.Services.AddSingleton(new LsxConnectionHandler(players, capture));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = HttpExchange.MaxBodyBytes;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
            foreach (var listener in listeners)
            {
                kestrel.Listen(listener.Bind ?? host, listener.Port, endpoint =>
                {
                    // Socket middleware precedes TLS. Local/plaintext and private web listeners keep their defaults.
                    if (keepAlive is { Enabled: true } && listener.Service is
                        (Service.FutTlsProbe or Service.BlazeTlsProbe or Service.EaswTlsProbe or Service.SsfTlsProbe))
                        HostedTcpKeepAlive.Configure(endpoint, keepAlive, listener.Label, loggers.CreateLogger("UltimateTeam.Transport"));
                    switch (listener.Service)
                    {
                        case Service.Blaze:
                            endpoint.UseConnectionHandler<BlazeConnectionHandler>();
                            break;
                        case Service.BlazeTlsProbe:
                            // The same Blaze handler behind TLS 1.2 with the redirector's leaf certificate, as the TLS
                            // redirector serves it (the trust patch supplies the anchor in-process).
                            endpoint.UseHttps(new HttpsConnectionAdapterOptions
                            {
                                ServerCertificate = tlsProbeCertificate ?? throw new InvalidOperationException("TLS probe certificate is missing."),
                                SslProtocols = SslProtocols.Tls12,
                                ClientCertificateMode = ClientCertificateMode.NoCertificate,
                                OnAuthenticate = (connection, _) =>
                                    game.Logger("UltimateTeam.TlsProbe").LogInformation("BLAZE TLS PROBE TCP peer={Peer}", connection.RemoteEndPoint),
                            });
                            endpoint.Use(next => async connection =>
                            {
                                var tls = connection.Features.Get<ITlsHandshakeFeature>();
                                game.Logger("UltimateTeam.TlsProbe").LogInformation("BLAZE TLS PROBE handshake peer={Peer} protocol={Protocol} cipher={Cipher} sni={Sni}",
                                    connection.RemoteEndPoint, tls?.Protocol, tls?.NegotiatedCipherSuite, tls?.HostName);
                                await next(connection);
                            });
                            endpoint.UseConnectionHandler<BlazeConnectionHandler>();
                            break;
                        case Service.Lsx:
                            endpoint.UseConnectionHandler<LsxConnectionHandler>();
                            break;
                        case Service.FutTlsProbe:
                        case Service.EaswTlsProbe:
                        case Service.SsfTlsProbe:
                            endpoint.Protocols = HttpProtocols.Http1;
                            endpoint.UseHttps(new HttpsConnectionAdapterOptions
                            {
                                ServerCertificate = tlsProbeCertificate ?? throw new InvalidOperationException("TLS probe certificate is missing."),
                                SslProtocols = SslProtocols.Tls12,
                                ClientCertificateMode = ClientCertificateMode.NoCertificate,
                                OnAuthenticate = (connection, _) =>
                                    game.Logger("UltimateTeam.TlsProbe").LogInformation("{Service} TLS PROBE TCP peer={Peer}",
                                        listener.Service switch { Service.EaswTlsProbe => "EASW", Service.SsfTlsProbe => "SSF", _ => "FUT" }, connection.RemoteEndPoint),
                            });
                            break;
                        default:
                            endpoint.Protocols = HttpProtocols.Http1;
                            break;
                    }
                });
            }
        });

        var app = builder.Build();
        var byPort = listeners.ToDictionary(listener => listener.Port, listener => listener.Service);
        var tlsLog = game.Logger("UltimateTeam.TlsProbe");
        app.Run(async context =>
        {
            var service = byPort.GetValueOrDefault(context.Connection.LocalPort);
            if (service is Service.FutTlsProbe or Service.EaswTlsProbe or Service.SsfTlsProbe)
            {
                var tls = context.Features.Get<ITlsHandshakeFeature>();
                tlsLog.LogInformation("{Service} TLS PROBE request protocol={Protocol} cipher={Cipher} sni={Sni} method={Method} path={Path}",
                    service switch { Service.EaswTlsProbe => "EASW", Service.SsfTlsProbe => "SSF", _ => "FUT" },
                    tls?.Protocol, tls?.NegotiatedCipherSuite, tls?.HostName, context.Request.Method, context.Request.Path);
            }
            await (service switch
            {
                Service.QosHttp => http.Qos.HandleAsync(context),
                Service.Ssf or Service.SsfTlsProbe => http.Ssf.HandleAsync(context),
                Service.Easw or Service.EaswTlsProbe => http.Easw.HandleAsync(context),
                Service.Redirect => http.Redirector.HandleCleartextAsync(context),
                Service.LaunchApi => context.Request.Path.StartsWithSegments("/client") && clientEndpoint is not null
                    ? clientEndpoint.HandleAsync(context)
                    : launchEndpoint!.HandleAsync(context),
                Service.PanelApi => panelEndpoint!.HandleAsync(context),
                _ => http.Fut.HandleAsync(context),
            });
        });
        return app;
    }

    private static X509Certificate2 LoadTlsProbeCertificate(RuntimePaths paths)
    {
        var cert = Path.Combine(paths.CertificateRoot, "certs", "spring18-cert.pem");
        var key = Path.Combine(paths.CertificateRoot, "certs", "spring18-key.pem");
        if (!File.Exists(cert) || !File.Exists(key))
        {
            throw new RuntimeSetupException("The existing redirector certificate is required for the TLS probe. Generate it through normal setup first.");
        }
        using var pem = X509Certificate2.CreateFromPemFile(cert, key);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static LaunchAssertionVerifier LoadLaunchVerifier(LaunchApiOptions options)
    {
        try
        {
            if (options.PublicKeyPath is { } publicKey)
            {
                return new LaunchAssertionVerifier(File.ReadAllText(publicKey), options.Issuer!);
            }
            if (options.PublicKeyText is { } publicText)
            {
                // A .env holds one line: accept the PEM with literal \n, or only its base64 body.
                var pem = publicText.Replace("\\n", "\n").Trim();
                if (!pem.StartsWith("-----BEGIN", StringComparison.Ordinal))
                    pem = "-----BEGIN PUBLIC KEY-----\n" + string.Join('\n', pem.Chunk(64).Select(line => new string(line))) + "\n-----END PUBLIC KEY-----\n";
                return new LaunchAssertionVerifier(pem, options.Issuer!);
            }
            // The shared secret as text (at least 32 bytes after trimming), from the environment or a file; never logged.
            string text;
            if (options.SecretText is { } secretText) text = secretText;
            else
            {
                var file = new FileInfo(options.SecretPath!);
                if (file.Length > 4096) throw new InvalidDataException("secret file too large");
                text = File.ReadAllText(file.FullName);
            }
            var secret = System.Text.Encoding.UTF8.GetBytes(text.Trim());
            try { return new LaunchAssertionVerifier(secret, options.Issuer!); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                                          System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw new RuntimeSetupException("The hosted launch assertion key or secret, or its issuer, is missing or invalid.");
        }
    }

    private static bool AbsoluteIfSet(string? path) => path is null || Path.IsPathFullyQualified(path);

    /// <summary>The server's public IPv4 for the launcher's hosts entry: the relay's advertised address, when it is one.</summary>
    private static string? PublicGameAddress(GameplayRelayOptions relay) =>
        IPAddress.TryParse(relay.AdvertisedAddress, out var address) && address.AddressFamily == AddressFamily.InterNetwork
            ? address.ToString()
            : null;

    /// <summary>What the panel's Dashboard lists: the listeners this process holds, and the TLS redirector it probes.</summary>
    private static IReadOnlyList<PanelService> PanelServices(ServeOptions options)
    {
        static Task<bool> Held(CancellationToken _) => Task.FromResult(true);
        var list = new List<PanelService>
        {
            new("blaze", "Blaze (TLS)", options.BlazeTlsProbePort ?? Ports.Blaze, Held),
            new("fut", "FUT and Nucleus (TLS)", options.TlsProbe.Port, Held),
            new("easw", "EASW (TLS)", options.TlsProbe.EaswPort, Held),
            new("ssf", "SSF (TLS)", options.TlsProbe.SsfPort, Held),
            new("qos", "QoS", Ports.Qos, Held),
        };
        if (options.LaunchApi?.RedirectorHealth is { } redirector)
        {
            list.Insert(0, new PanelService("redirector", "TLS redirector", redirector.Port, async cancellationToken =>
            {
                try
                {
                    using var client = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(1));
                    await client.ConnectAsync(redirector.Host, redirector.Port, timeout.Token);
                    return true;
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
            }));
        }
        return list;
    }

    private static bool CanBindTcp(IPAddress host, int port, out string error)
    {
        error = string.Empty;
        try
        {
            using var probe = new TcpListener(host, port) { ExclusiveAddressUse = OperatingSystem.IsWindows() };
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
