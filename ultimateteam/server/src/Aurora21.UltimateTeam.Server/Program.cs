using System.Net;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Server.Tools;

namespace Aurora21.UltimateTeam.Server;

internal static class Program
{
    private const string Usage = """
        Aurora 21 UltimateTeam server (FIFA 21 local Ultimate Team runtime)

        Usage:
          Aurora21-UltimateTeam-Server [serve] [--host 127.0.0.1] [--lsx-mode auto|off|required] [--capture]
                                            [--http-only-test-port PORT]
                                            [--player-identity-mode single|peer-ip|session]
                                            [--local-session-test]
                                            [--tls-probe-mode off|fut|nucleus|both|all] [--tls-probe-port 18443]
                                            [--blaze-tls-probe-port PORT] [--advertise-hostname]
                                            [--hosted]   (session identity, TLS for every service, LSX off; see README)
                                            [--launch-api-port PORT]   (hosted: /client/* and /api/v1/launch/code on loopback)
                                              [--launch-assertion-issuer ISSUER
                                               (--launch-assertion-public-key PEM | --launch-assertion-secret-file PATH)]
                                              [--client-install-dir FOLDER]
                                              [--redirector-health HOST:PORT]   (default 127.0.0.1:42230)
                                            [--panel-api-port PORT [--backup-dir FOLDER] [--panel-assets-dir FOLDER]]   (the shared Aurora panel, loopback only; hosted: through an SSH tunnel)
                                            [--player-max-open-clubs 20] [--player-idle-minutes 30]
                                            [--player-concurrent-opens 4]
                                            [--log-max-mb 0]   (rotate logs/ultimateteam.log to .1 at this size; 0 never)
                                            [--gameplay-relay on|off] [--gameplay-relay-bind IPV4]
                                            [--gameplay-relay-advertised IPV4]
                                            [--gameplay-relay-port-start 31000] [--gameplay-relay-port-end 31199]
          Aurora21-UltimateTeam-Server redirector [--blaze-tls-port PORT]
          Aurora21-UltimateTeam-Server generate-certs [--force]
          Aurora21-UltimateTeam-Server export-trust-anchors --out FOLDER
          Aurora21-UltimateTeam-Server trust-patch [--pid PID] [--only LABEL] [--skip-origin] [--trust-anchors FOLDER]
          Aurora21-UltimateTeam-Server trust-restore [--pid PID] [--trust-anchors FOLDER]
          Aurora21-UltimateTeam-Server grant-card RESOURCE_ID [--pile club|unassigned] [--allow-duplicate] [--replace-resource-id ID]
          Aurora21-UltimateTeam-Server grant-all-cards
          Aurora21-UltimateTeam-Server reset-club
          Aurora21-UltimateTeam-Server repair-db [--apply]
          Aurora21-UltimateTeam-Server postgres install|status|start|stop

        Common options:
          --data-root PATH   Bundled catalog folder (default: runtime-source/ultimateteam21 next to the executable)

        Environment:
          ULTIMATETEAM_RUNTIME_ROOT          PostgreSQL cluster/logs root (default %LOCALAPPDATA%\FIFA21UltimateTeam)
          ULTIMATETEAM21_USER_CONFIG_ROOT    Preferences root (default %LOCALAPPDATA%\Aurora21\UltimateTeam)
          ULTIMATETEAM_POSTGRES_CONNECTION   Use this PostgreSQL server (Npgsql connection string) instead of the managed cluster
          ULTIMATETEAM_DISCORD_BOT_TOKEN     Hosted: the Discord bot that checks who holds the play and maintenance-bypass roles
          ULTIMATETEAM_LAUNCH_ISSUER         Hosted: the account service's issuer, when --launch-assertion-issuer is not given
          ULTIMATETEAM_LAUNCH_PUBLIC_KEY     ... its ES256 public key (PEM, or the base64 body on one line), or
          ULTIMATETEAM_LAUNCH_SECRET         ... its HS256 secret shared only with this server
          ULTIMATETEAM_POSTGRES_ROOT         Portable PostgreSQL binaries root (default: postgres/ next to runtime-source)
          ULTIMATETEAM21_RIVALS_CLIENT_DATA  0 stops returning stored Rivals intro markers at login (default 1)
          ULTIMATETEAM21_JOINER_CMD22        1 sends the joiner JoiningPlayerInitiateConnections instead of GameSetup (default 0)
          ULTIMATETEAM21_CMD22_AFTER         0 stops following each GameSetup with InitiateConnections (default 1)
          ULTIMATETEAM21_MM_FINISHED         1 sends MatchmakingFinished after pairing (crashes clients; default 0)
          ULTIMATETEAM21_INGAME_PROBE_S      Seconds after PRE_GAME to push IN_GAME as a diagnostic (default 0, off)
          ULTIMATETEAM21_ENABLE_UNVERIFIED_PLATFORM_HOST_NOTIFICATION  0 stops PlatformHostInitialized (default 1)
          ULTIMATETEAM_FIRE2_CAPTURE_ALL     1 captures every Blaze frame, not only matchmaking evidence
          ULTIMATETEAM21_MARKET_TARGET       Active AI listings the shared transfer market keeps (default 0; disabled)
          ULTIMATETEAM21_ADMIN_TOKEN         Shared-server maintenance API secret (never sent to game clients)
        """;

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "--capture", "--allow-duplicate", "--skip-origin", "--help", "--local-session-test", "--force", "--advertise-hostname", "--hosted",
    };

    private static string? AutoOrValue(string value) => value.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : value;

    private const string LaunchIssuerVariable = "ULTIMATETEAM_LAUNCH_ISSUER";
    private const string LaunchPublicKeyVariable = "ULTIMATETEAM_LAUNCH_PUBLIC_KEY";
    private const string LaunchSecretVariable = "ULTIMATETEAM_LAUNCH_SECRET";

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>HOST:PORT, where HOST is an address or a name (a Compose service name, for example).</summary>
    private static DnsEndPoint ParseEndpoint(string value)
    {
        var colon = value.LastIndexOf(':');
        return colon > 0 && int.TryParse(value.AsSpan(colon + 1), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? new DnsEndPoint(value[..colon], port)
            : throw new UsageException("--redirector-health must be HOST:PORT");
    }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "serve";
            var rest = command == "serve" && (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) ? args : args.Skip(1).ToArray();
            var options = new CommandLine(rest, Flags);
            if (options.Flag("--help"))
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var dataRoot = options.Value("--data-root");

            switch (command)
            {
                case "serve":
                    {
                        // Legacy offline grant flags accepted on the server command line.
                        if (options.Long("--grant-card") is { } legacyGrant)
                        {
                            var pile = options.Choice("--grant-pile", "club", "club", "unassigned");
                            var allow = options.Flag("--allow-duplicate");
                            var replace = options.Long("--replace-resource-id");
                            options.EnsureAllConsumed();
                            return await ClubCommands.GrantCardAsync(dataRoot, legacyGrant, pile, allow, replace);
                        }
                        // --hosted changes the defaults it needs; explicit values that contradict it are refused by ServeCommand.
                        var hosted = options.Flag("--hosted");
                        var tlsProbePort = BoundedInt(options, "--tls-probe-port", TlsProbeOptions.DefaultPort, 1024, 65535);
                        var launchPort = options.Long("--launch-api-port");
                        var launchKey = options.Value("--launch-assertion-public-key");
                        var launchSecret = options.Value("--launch-assertion-secret-file");
                        var launchIssuer = options.Value("--launch-assertion-issuer");
                        var installDirectory = options.Value("--client-install-dir");
                        var redirectorHealth = options.Value("--redirector-health");
                        var panelPort = options.Long("--panel-api-port");
                        var backupDirectory = options.Value("--backup-dir");
                        var assetsDirectory = options.Value("--panel-assets-dir");
                        // On the VPS the account service's issuer and key come from the .env (compose passes them as
                        // environment variables); command-line options win. The secret is removed from the environment once read.
                        string? envIssuer = null, envPublicKey = null, envSecret = null;
                        if (launchPort is not null && launchIssuer is null && launchKey is null && launchSecret is null)
                        {
                            envIssuer = NonEmpty(Environment.GetEnvironmentVariable(LaunchIssuerVariable));
                            envPublicKey = NonEmpty(Environment.GetEnvironmentVariable(LaunchPublicKeyVariable));
                            envSecret = NonEmpty(Environment.GetEnvironmentVariable(LaunchSecretVariable));
                            if (envIssuer is null && (envPublicKey ?? envSecret) is not null)
                            {
                                throw new UsageException($"{LaunchIssuerVariable} is needed with {LaunchPublicKeyVariable} or {LaunchSecretVariable}");
                            }
                        }
                        Environment.SetEnvironmentVariable(LaunchSecretVariable, null);
                        if (launchPort is < 1024 or > 65535)
                        {
                            throw new UsageException("--launch-api-port must be an unprivileged TCP port");
                        }
                        if (launchPort is null && (launchKey ?? launchSecret ?? launchIssuer ?? installDirectory ?? redirectorHealth) is not null)
                        {
                            throw new UsageException("The launch identity, client install and redirector health options need --launch-api-port");
                        }
                        if ((launchIssuer is null) != (launchKey is null && launchSecret is null) || (launchKey is not null && launchSecret is not null))
                        {
                            throw new UsageException("--launch-assertion-issuer needs exactly one of --launch-assertion-public-key (ES256) or --launch-assertion-secret-file (HS256)");
                        }
                        if (panelPort is < 1024 or > 65535 || ((backupDirectory is not null || assetsDirectory is not null) && panelPort is null))
                        {
                            throw new UsageException("--panel-api-port must be an unprivileged TCP port; --backup-dir and --panel-assets-dir need --panel-api-port");
                        }
                        var serve = new ServeOptions(
                            options.Value("--host", "127.0.0.1"),
                            options.Choice("--lsx-mode", hosted ? "off" : "auto", "auto", "off", "required") switch
                            {
                                "off" => LsxMode.Off,
                                "required" => LsxMode.Required,
                                _ => LsxMode.Auto,
                            },
                            (int?)options.Long("--http-only-test-port"),
                            options.Flag("--capture"))
                        {
                            Hosted = hosted,
                            LaunchApi = launchPort is null ? null : new LaunchApiOptions((int)launchPort.Value)
                            {
                                Issuer = launchIssuer ?? envIssuer,
                                PublicKeyPath = launchKey,
                                SecretPath = launchSecret,
                                PublicKeyText = envIssuer is null ? null : envPublicKey,
                                SecretText = envIssuer is null ? null : envSecret,
                                InstallDirectory = installDirectory,
                                RedirectorHealth = redirectorHealth is null ? new DnsEndPoint("127.0.0.1", Ports.TlsRedirector) : ParseEndpoint(redirectorHealth),
                            },
                            PanelApi = panelPort is null ? null : new PanelApiOptions((int)panelPort.Value, backupDirectory, assetsDirectory),
                            IdentityMode = options.Choice("--player-identity-mode", hosted ? "session" : "single", "single", "peer-ip", "session") switch
                            {
                                "peer-ip" => PlayerIdentityMode.PeerIp,
                                "session" => PlayerIdentityMode.Session,
                                _ => PlayerIdentityMode.Single,
                            },
                            LocalSessionTest = options.Flag("--local-session-test"),
                            BlazeTlsProbePort = (int?)options.Long("--blaze-tls-probe-port") ?? (hosted ? tlsProbePort + 1 : null),
                            TlsProbe = new TlsProbeOptions(options.Choice("--tls-probe-mode", hosted ? "all" : "off", "off", "fut", "nucleus", "both", "all") switch
                            {
                                "fut" => TlsProbeMode.Fut,
                                "nucleus" => TlsProbeMode.Nucleus,
                                "both" => TlsProbeMode.Both,
                                "all" => TlsProbeMode.All,
                                _ => TlsProbeMode.Off,
                            }, tlsProbePort)
                            {
                                AdvertiseHostname = hosted || options.Flag("--advertise-hostname"),
                            },
                            Players = new PlayerRegistryOptions
                            {
                                MaxOpenClubs = BoundedInt(options, "--player-max-open-clubs", 20, 1, 10000),
                                ConcurrentOpens = BoundedInt(options, "--player-concurrent-opens", 4, 1, 32),
                                IdleTimeout = TimeSpan.FromMinutes(BoundedInt(options, "--player-idle-minutes", 30, 1, 1440)),
                            },
                            LogMaxMegabytes = BoundedInt(options, "--log-max-mb", 0, 0, 10240),
                            Relay = new GameplayRelayOptions
                            {
                                Enabled = options.Choice("--gameplay-relay", "off", "on", "off") == "on",
                                BindAddress = AutoOrValue(options.Value("--gameplay-relay-bind", "auto")),
                                AdvertisedAddress = AutoOrValue(options.Value("--gameplay-relay-advertised", "auto")),
                                PortStart = (int)(options.Long("--gameplay-relay-port-start") ?? 31000),
                                PortEnd = (int)(options.Long("--gameplay-relay-port-end") ?? 31199),
                            },
                        };
                        options.EnsureAllConsumed();
                        return await ServeCommand.RunAsync(serve, dataRoot);
                    }
                case "redirector":
                    {
                        var blazeTlsPort = (int?)options.Long("--blaze-tls-port");
                        if (blazeTlsPort is { } tlsPort && (tlsPort is < 1024 or > 65535 || Ports.Production.Contains(tlsPort)))
                        {
                            throw new UsageException("--blaze-tls-port must be an unprivileged, non-production port");
                        }
                        options.EnsureAllConsumed();
                        return await RedirectorCommand.RunAsync(dataRoot, blazeTlsPort);
                    }
                case "generate-certs":
                    {
                        var force = options.Flag("--force");
                        options.EnsureAllConsumed();
                        return CertificateGenerator.Run(RuntimePaths.Resolve(dataRoot).CertificateRoot, force);
                    }
                case "export-trust-anchors":
                    {
                        var output = options.Value("--out") ?? throw new UsageException("export-trust-anchors needs --out <folder>");
                        options.EnsureAllConsumed();
                        return CertificateGenerator.ExportTrustAnchors(RuntimePaths.Resolve(dataRoot).CertificateRoot, output);
                    }
                case "trust-patch":
                    {
                        var pid = options.Long("--pid");
                        if (pid is <= 0)
                        {
                            throw new UsageException("--pid must be a positive FIFA 21 process ID.");
                        }
                        var only = options.Value("--only");
                        var skipOrigin = options.Flag("--skip-origin");
                        var trustAnchors = options.Value("--trust-anchors");
                        options.EnsureAllConsumed();
                        return OperatingSystem.IsWindows()
                            ? TrustBridge.Patch(RuntimePaths.Resolve(dataRoot).CertificateRoot, (int?)pid, only, skipOrigin, trustAnchors)
                            : WindowsOnly();
                    }
                case "trust-restore":
                    {
                        var pid = options.Long("--pid");
                        var restoreAnchors = options.Value("--trust-anchors");
                        options.EnsureAllConsumed();
                        return OperatingSystem.IsWindows()
                            ? TrustBridge.Restore(RuntimePaths.Resolve(dataRoot).CertificateRoot, (int?)pid, restoreAnchors)
                            : WindowsOnly();
                    }
                case "grant-card":
                    {
                        if (options.Positionals.Count != 1 || !long.TryParse(options.Positionals[0], out var resourceId))
                        {
                            throw new UsageException("grant-card requires one numeric RESOURCE_ID");
                        }
                        var pile = options.Choice("--pile", "club", "club", "unassigned");
                        var allow = options.Flag("--allow-duplicate");
                        var replace = options.Long("--replace-resource-id");
                        options.EnsureAllConsumed();
                        return await ClubCommands.GrantCardAsync(dataRoot, resourceId, pile, allow, replace);
                    }
                case "grant-all-cards":
                    options.EnsureAllConsumed();
                    return await ClubCommands.GrantAllCardsAsync(dataRoot);
                case "reset-club":
                    options.EnsureAllConsumed();
                    return await ClubCommands.ResetClubAsync(dataRoot);
                case "repair-db":
                    {
                        var apply = options.Flag("--apply");
                        options.EnsureAllConsumed();
                        return await ClubCommands.RepairDatabaseAsync(dataRoot, apply);
                    }
                case "postgres":
                    {
                        if (options.Positionals.Count != 1)
                        {
                            throw new UsageException("postgres requires one action: install, status, start or stop");
                        }
                        var action = options.Positionals[0];
                        options.EnsureAllConsumed();
                        return await PostgresCommands.RunAsync(action, dataRoot);
                    }
                default:
                    throw new UsageException($"unknown command '{command}'");
            }
        }
        catch (UsageException error)
        {
            Console.Error.WriteLine(Usage);
            Console.Error.WriteLine($"error: {error.Message}");
            return 2;
        }
        catch (RuntimeSetupException error)
        {
            Console.Error.WriteLine($"error: {error.Message}");
            return 2;
        }
        catch (Npgsql.NpgsqlException error)
        {
            Console.Error.WriteLine($"error: club database: {error.Message}");
            return 2;
        }
    }

    private static int WindowsOnly()
    {
        Console.WriteLine("[ERROR] The trust bridge only runs on Windows next to FIFA21.exe.");
        return 1;
    }

    private static int BoundedInt(CommandLine options, string name, int fallback, int minimum, int maximum)
    {
        var value = options.Long(name) ?? fallback;
        return value >= minimum && value <= maximum
            ? (int)value
            : throw new UsageException($"argument {name}: must be between {minimum} and {maximum}");
    }
}
