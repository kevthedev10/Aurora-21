using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>Fixed FIFA 21 network topology. These are protocol compatibility data, not user preferences.</summary>
public static class Ports
{
    public const int Redirect = 42231;
    public const int TlsRedirector = 42230;
    public const int Blaze = 44321;
    public const int Qos = 17502;
    public const int Easw = 42232;
    public const int Ssf = 10853;
    public const int Fut = 8090;
    public const int LegacyFut = 8081;
    public const int Lsx = 3216;

    public static IReadOnlyCollection<int> Production { get; } =
        new[] { Fut, LegacyFut, TlsRedirector, Redirect, Easw, Blaze, Ssf, Qos, Lsx };
}

/// <summary>
/// The local identity and club preferences: the user's config.json over the bundled
/// config.example.json, which supplies every default. A value of the wrong type falls back
/// to the example's value. Ports are not configurable; see <see cref="Ports"/>.
/// </summary>
public sealed class ServerConfig
{
    private const string FileName = "config.json";

    private readonly JsonObject _defaults;
    private readonly JsonObject _values;

    private ServerConfig(JsonObject defaults, JsonObject values)
    {
        _defaults = defaults;
        _values = values;
    }

    public string DisplayName => Text("display_name");
    public string DateOfBirth => Text("date_of_birth");
    public int MinimumAge => (int)IntKeep("minimum_age");
    public long NucleusId => IntKeep("nucleus_id");
    public long PersonaId => IntKeep("persona_id");
    public long ClubId => IntKeep("club_id");
    public string ClubName => Text("club_name");
    public string ClubAbbr => Text("club_abbr");
    public long InitialCredits => IntKeep("credits");
    public long RareFlag => IntKeep("rare_flag");
    public int InitialClubPlayerCount => (int)_values.Int("initial_club_player_count", _defaults.Int("initial_club_player_count"));
    public bool DebugUnknownHttp => _values.Bool("debug_unknown_http", _defaults.Bool("debug_unknown_http"));

    /// <summary>Club database session timeouts and the deadlock retry budget.</summary>
    public DatabaseSettings Database => DatabaseSettings.From(LimitSettings.Section(_values, "database"), _defaults.Obj("database")
        ?? throw new InvalidDataException("The bundled config.example.json has no database section"));

    public ShutdownSettings Shutdown => ShutdownSettings.From(LimitSettings.Section(_values, "shutdown"), _defaults.Obj("shutdown")
        ?? throw new InvalidDataException("The bundled config.example.json has no shutdown section"));

    public ConnectionLimitSettings ConnectionLimits => ConnectionLimitSettings.From(LimitSettings.Section(_values, "connection_limits"), _defaults.Obj("connection_limits")
        ?? throw new InvalidDataException("The bundled config.example.json has no connection_limits section"));

    public TcpKeepAliveSettings TcpKeepAlive => TcpKeepAliveSettings.From(LimitSettings.Section(_values, "tcp_keepalive"), _defaults.Obj("tcp_keepalive")
        ?? throw new InvalidDataException("The bundled config.example.json has no tcp_keepalive section"),
        Environment.GetEnvironmentVariable("ULTIMATETEAM_TCP_KEEPALIVE_IDLE_SECONDS"));

    public NativeAuthSettings NativeAuth => NativeAuthSettings.From(LimitSettings.Section(_values, "native_auth"), _defaults.Obj("native_auth")
        ?? throw new InvalidDataException("The bundled config.example.json has no native_auth section"));

    public LauncherApiSettings LauncherApi => LauncherApiSettings.From(LimitSettings.Section(_values, "launcher_api"), _defaults.Obj("launcher_api")
        ?? throw new InvalidDataException("The bundled config.example.json has no launcher_api section"));

    /// <summary>Opt-in diagnostics: packet capture files and SCOUT request logging.</summary>
    public bool CaptureDiagnostics => _values.Obj("diagnostics")?.Bool("capture") ?? false;

    public IReadOnlyList<long> StarterPlayers => JsonValues.Longs(_values.Get("starter_players"));

    /// <summary>Environment switches for behaviour still being verified in the live game.</summary>
    public FeatureSwitches Switches { get; init; } = FeatureSwitches.FromEnvironment();

    public bool RivalsClientData => Switches.RivalsClientData;

    public int AccountAge(DateOnly today)
    {
        var dob = DateOnly.ParseExact(DateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var age = today.Year - dob.Year;
        if ((today.Month, today.Day).CompareTo((dob.Month, dob.Day)) < 0)
        {
            age--;
        }
        return age;
    }

    public bool IsUnderAge(DateOnly today) => AccountAge(today) < MinimumAge;

    /// <summary>
    /// This configuration for another player: the same preferences with that player's own ids. With
    /// <paramref name="clubNames"/>, a shared-server player whose configured club is still the bundled placeholder gets its
    /// own derived club name and abbreviation (<see cref="ClubNames"/>); a club name configured for the server is kept.
    /// </summary>
    public ServerConfig ForPlayer(PlayerIdentity player, string? displayName = null, Rules.ClubNameRules? clubNames = null)
    {
        var values = (JsonObject)_values.DeepClone();
        values["persona_id"] = player.PersonaId;
        values["nucleus_id"] = player.NucleusId;
        values["club_id"] = player.ClubId;
        if (displayName is not null)
        {
            values["display_name"] = displayName;
        }
        if (clubNames is not null && !player.IsSinglePlayer && ClubName == _defaults.Text("club_name"))
        {
            var (name, abbreviation) = ClubNames.For(player.Id, displayName ?? DisplayName, clubNames);
            values["club_name"] = name;
            values["club_abbr"] = abbreviation;
        }
        return new ServerConfig(_defaults, values) { Switches = Switches };
    }

    /// <summary>This configuration with the club name and abbreviation its player chose when FIFA created the club.</summary>
    public ServerConfig WithClub(string name, string abbreviation)
    {
        var values = (JsonObject)_values.DeepClone();
        values["club_name"] = name;
        values["club_abbr"] = abbreviation;
        return new ServerConfig(_defaults, values) { Switches = Switches };
    }

    public static ServerConfig Load(UserConfigStore store)
    {
        var defaults = store.ReadExample(FileName)
                       ?? throw new InvalidDataException($"The bundled {UserConfigStore.ExampleName(FileName)} is missing from {store.DataRoot}");
        var values = store.Read(FileName, defaults);
        if (values.TryGetPropertyValue("storage_backend", out var selected) && selected?.ToString() is { } backend
            && backend != "relational")
        {
            throw new RuntimeSetupException("config.json selects a retired storage backend. Existing legacy club data has not been converted; remove storage_backend only when ready to use the relational club.");
        }
        return new ServerConfig(defaults, values);
    }

    private string Text(string key) => _values.Text(key, _defaults.Text(key));

    private long IntKeep(string key) => _values.IntKeep(key, _defaults.IntKeep(key));
}
