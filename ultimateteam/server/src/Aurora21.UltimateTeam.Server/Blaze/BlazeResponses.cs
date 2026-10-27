using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Auth;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Protocol.Tdf;
using static Aurora21.UltimateTeam.Server.Blaze.BlazeComponent;

namespace Aurora21.UltimateTeam.Server.Blaze;

/// <summary>What the session must send after a reply.</summary>
public enum BlazeFollowUp
{
    None,

    /// <summary>The UserSessions login sequence; without it the client logs out after 20 s.</summary>
    LoginNotifications,

    /// <summary>The request was not recognised and is logged for analysis.</summary>
    Unknown,
}

/// <summary>One Blaze reply: the TDF payload, a label used for logging, and any follow-up.</summary>
public sealed record BlazeReply(byte[] Payload, string Label, BlazeFollowUp FollowUp = BlazeFollowUp.None, BlazeNotification? Notification = null);

/// <summary>A server-initiated Blaze notification.</summary>
public sealed record BlazeNotification(int Component, int Command, byte[] Payload, string Label);

/// <summary>
/// Local Blaze responses for the FIFA 21 PC client. Payload shapes were reconstructed from
/// the successful retail trace; field order matters to the client's sequential TDF reader.
/// Identity and configuration values come from Resources/blaze.json.
/// </summary>
public sealed class BlazeResponses
{
    /// <summary>The Blaze entity type of a user session in object ids.</summary>
    private const int UserSessionEntityType = 2;

    private delegate BlazeReply Handler(Fire2Frame frame);

    /// <summary>A command whose reply needs club data, so it runs on the pooled data source.</summary>
    private delegate ValueTask<BlazeReply> AsyncHandler(Fire2Frame frame, CancellationToken cancellationToken);

    private readonly GameContext _game;
    private readonly FutServices _fut;
    private readonly BlazeSettings _settings;
    private readonly TlsProbeOptions _tlsProbe;
    private readonly Dictionary<(int Component, int Command), Handler> _handlers;
    private readonly Dictionary<(int Component, int Command), AsyncHandler> _asyncHandlers;

    /// <summary>
    /// The session KEY of the login replies and notifications (FIFA 35/10 SESS.KEY, UserSessions 30722/8 KEY). FIFA echoes
    /// it as the Fire2 metadata SKEY when it resumes on a new connection, so the connection handler gives every login its
    /// own random key (<see cref="BlazeSessionKeys"/>). Defaults to the configured title key.
    /// </summary>
    public string SessionKey { get; init; }

    /// <summary>
    /// PreAuth's MAID, one per connection's setup responder: FIFA keeps it as its machine id and echoes it as MACI in every
    /// network address it reports, so two players must never share it. Random unless set (the byte-parity test pins it).
    /// </summary>
    public long MachineId { get; init; } = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);

    public BlazeResponses(FutServices fut, TlsProbeOptions? tlsProbe = null)
    {
        _fut = fut;
        _game = fut.Game;
        _settings = BlazeSettings.Load();
        _tlsProbe = tlsProbe ?? TlsProbeOptions.Off;
        SessionKey = _settings.Session.TitleSessionKey;
        _handlers = new Dictionary<(int, int), Handler>
        {
            [(Util, UtilCommand.PreAuth)] = _ => new(PreAuth(), "Util.PreAuth"),
            [(Util, UtilCommand.Ping)] = _ => new(new TdfWriter().Int("STIM", _game.NowSeconds).ToArray(), "Util.Ping"),
            [(Util, UtilCommand.FetchClientConfig)] = FetchClientConfig,
            [(Util, UtilCommand.PostAuth)] = _ => new(PostAuth(), "Util.PostAuth"),
            [(Util, UtilCommand.GetTelemetryServer)] = _ => new(TdfWriter.Build(Telemetry), "Util.GetTelemetryServer"),
            [(Util, UtilCommand.GetUserOptions)] = _ => new(new TdfWriter().MapStringString("SMAP", BlazeSettings.Pairs(_settings.UserOptions)).ToArray(), "Util.GetUserOptions"),
            [(Util, UtilCommand.GetUserOption)] = GetUserOption,
            [(Util, UtilCommand.ClientState)] = _ => Empty("Util.ClientState/Metrics"),
            // IncrementMatchPlayedResponse {ECOD, EMSG, REST}: the client reads only the error code, so this answers the
            // request the way retail would instead of as an unhandled one (about one per match).
            [(UserActivityTracker, UserActivityTrackerCommand.IncrementMatchPlayed)] = _ =>
                new(new TdfWriter().Int("REST", 1).ToArray(), "UserActivityTracker.IncrementMatchPlayed"),
            // Runs on every name the player types (new squad, rename); an empty reply crashed the game.
            [(Util, UtilCommand.FilterForProfanity)] = FilterForProfanity,
            // Top Players and Leaderboards: empty replies made Top Players crash.
            [(Stats, StatsCommand.GetPeriodIds)] = _ => new(PeriodIds(), "Stats.GetPeriodIds"),
            [(Stats, StatsCommand.GetLeaderboardGroup)] = frame => new(LeaderboardGroup(TdfReader.FindString(frame.Payload, "NAME") ?? string.Empty), "Stats.GetLeaderboardGroup"),
            // Friendlies (MyFriendlies) and Pro Clubs (VPro*) records: FIFA waits on both, so empty replies left the
            // Friendlies menu stuck (2026-10-08). No stats are kept: an empty group, then its empty values.
            [(Stats, StatsCommand.GetStatGroup)] = frame => new(StatGroup(TdfReader.FindString(frame.Payload, "NAME") ?? string.Empty), "Stats.GetStatGroup"),
            [(Stats, StatsCommand.GetStatsByGroupAsync)] = frame => new(Array.Empty<byte>(), "Stats.GetStatsByGroupAsync",
                Notification: StatsByGroup(frame.Payload)),
            [(Util, UtilCommand.ClientMetrics)] = _ => Empty("Util.ClientState/Metrics"),
            // The FIFA 21 title login bridge answers with a FullLogin-style session object.
            [(Fifa, FifaCommand.LoginSession)] = _ => new(TitleLoginSession(), "FIFA35.LoginSession", BlazeFollowUp.LoginNotifications),
            // The client schedules its next subscribe CNP + NTMT after this reply unless census data arrives first (we send
            // none), and retries after RTMT on an error. Zero timeouts (an empty reply, or the old RSUB echo, which is not a
            // response member) made it re-subscribe back to back, several times a second, for as long as it sat in menus.
            [(CensusData, CensusDataCommand.SubscribeToCensusDataUpdates)] = _ => new(CensusSubscription, "CensusData.SubscribeToCensusDataUpdates"),
            // An empty ack; the terminal ResultNotification follows separately.
            [(GameReporting, GameReportingCommand.SubmitOfflineGameReport)] = _ => Empty("GameReporting.SubmitOfflineGameReport"),
            [(OsdkSettings, OsdkSettingsCommand.FetchSettings)] = _ => new(TickerSettings(), "Ticker.GetSettings"),
            [(OsdkSettings, OsdkSettingsCommand.FetchSettingsGroups)] = _ => new(TickerGroups(), "Ticker.GetGroups"),
            // Void RPCs: the empty reply is the whole answer.
            [(UserSessions, UserSessionsCommand.UpdateHardwareFlags)] = _ => Empty("UserSessions.UpdateHardwareFlags"),
            [(Util, UtilCommand.SetConnectionState)] = _ => Empty("Util.SetConnectionState"),
            [(CensusData, CensusDataCommand.UnsubscribeFromCensusData)] = _ => Empty("CensusData.UnsubscribeFromCensusData"),
            // An empty URLResponse: no sponsored events.
            [(SponsoredEvents, SponsoredEventsCommand.GetEventsUrl)] = _ => Empty("SponsoredEvents.GetEventsUrl"),
            [(Authentication, AuthenticationCommand.ListEntitlements)] = _ => new(Entitlements(), "Authentication.ListEntitlements"),
            [(Authentication, AuthenticationCommand.GetAuthToken)] = _ => new(new TdfWriter().Str("AUTH", _game.Token).ToArray(), "Authentication.GetAuthToken"),
            [(Authentication, AuthenticationCommand.Logout)] = _ => Empty("Authentication.Logout"),
        };
        foreach (var command in AuthenticationCommand.LocalLogins)
        {
            // Only the plain Login is followed by the UserSessions sequence.
            var followUp = command == AuthenticationCommand.Login ? BlazeFollowUp.LoginNotifications : BlazeFollowUp.None;
            _handlers.Add((Authentication, command), _ => new(LocalLogin(), $"Authentication.LocalLogin(0x{command:X})", followUp));
        }
        foreach (var command in AuthenticationCommand.Entitlements)
        {
            _handlers.Add((Authentication, command), _ => new(Entitlements(), $"Authentication.Entitlements(0x{command:X})"));
        }
        foreach (var command in AuthenticationCommand.AccountPersonas)
        {
            _handlers.Add((Authentication, command), _ => new(LocalLogin(), $"Authentication.AccountPersona(0x{command:X})"));
        }
        foreach (var reply in _settings.EmptyReplies)
        {
            _handlers.Add((reply[0], reply[1]), _ => Empty("captured-empty"));
        }
        // Commands whose reply needs club data run on the pooled data source instead of this thread's reply path.
        _asyncHandlers = new Dictionary<(int, int), AsyncHandler>
        {
            [(Stats, StatsCommand.GetLeaderboard)] = LeaderboardValuesAsync,
            [(Stats, StatsCommand.GetCenteredLeaderboard)] = LeaderboardValuesAsync,
            [(Stats, StatsCommand.GetFilteredLeaderboard)] = LeaderboardValuesAsync,
        };
    }

    /// <summary>
    /// SubscribeToCensusDataUpdatesResponse: censusNotificationPeriod, notificationTimeout and resubscribeTimeout, TimeValues
    /// in microseconds, in tag order. The client re-subscribes every CNP + NTMT (90 s) while it sits in the menus.
    /// </summary>
    private static readonly byte[] CensusSubscription = new TdfWriter()
        .Int("CNP", 60_000_000)
        .Int("NTMT", 30_000_000)
        .Int("RTMT", 60_000_000)
        .ToArray();

    private long Persona => _game.Config.PersonaId;

    public async ValueTask<BlazeReply> RespondAsync(Fire2Frame frame, CancellationToken cancellationToken)
    {
        if (_asyncHandlers.TryGetValue((frame.Component, frame.Command), out var asyncHandler))
        {
            return await asyncHandler(frame, cancellationToken);
        }
        return _handlers.TryGetValue((frame.Component, frame.Command), out var handler)
            ? handler(frame)
            : Empty("unknown-empty", BlazeFollowUp.Unknown);
    }

    /// <summary>The UserSessions sequence the client waits for after a successful login reply.</summary>
    public IReadOnlyList<BlazeNotification> LoginNotifications() => new[]
    {
        new BlazeNotification(UserSessions, UserSessionsNotification.UserAdded, UserAdded(), "UserSessions.UserAdded"),
        new BlazeNotification(UserSessions, UserSessionsNotification.UserAuthenticated, LoginSessionNotification(), "UserSessions.LoginSession"),
        new BlazeNotification(UserSessions, UserSessionsNotification.ExtendedDataUpdate, ExtendedDataUpdate(), "UserSessions.ExtendedDataUpdate"),
        UserUpdated(),
    };

    /// <summary>Tells the client to refresh the user's session data (after a match settles over HTTP).</summary>
    public BlazeNotification UserUpdated() => new(UserSessions, UserSessionsNotification.UserUpdated,
        new TdfWriter().Int("FLGS", 3).Int("ID", Persona).ToArray(), "UserSessions.UserUpdated");

    /// <summary>GameReporting.ResultNotification: FIFA waits for it before leaving the match.</summary>
    public static BlazeNotification GameReportingResult(long reportId)
    {
        reportId = Math.Max(0, reportId);
        var payload = new TdfWriter().NullVariable("DATA").Int("EROR", 0).Int("FNL", 1).Int("GHID", reportId).Int("GRID", reportId).ToArray();
        return new(GameReporting, GameReportingCommand.ResultNotification, payload, "GameReporting.ResultNotification");
    }

    private static BlazeReply Empty(string label, BlazeFollowUp followUp = BlazeFollowUp.None) => new(Array.Empty<byte>(), label, followUp);

    private BlazeReply FetchClientConfig(Fire2Frame frame)
    {
        var configId = TdfReader.FindString(frame.Payload, "CFID") ?? string.Empty;
        // OSDK_CORE routes the client's HTTP services (FUT, EASW, SSF) to the local listeners.
        var entries = _settings.ClientConfigs.TryGetValue(configId, out var config)
            ? BlazeSettings.Pairs(config, ("fut", Ports.Fut), ("easw", Ports.Easw), ("ssf", Ports.Ssf), ("ssfClientId", LocalAuthService.SsfClientId))
            : new List<KeyValuePair<string, string>>();
        // The TLS probe moves each selected service's URLs to its HTTPS listener, keeping the path.
        var rewrites = new List<(string Plain, string Tls)>();
        if (_tlsProbe.Fut)
        {
            rewrites.Add(($"http://127.0.0.1:{Ports.Fut}", _tlsProbe.Origin));
        }
        if (_tlsProbe.Easw)
        {
            rewrites.Add(($"http://127.0.0.1:{Ports.Easw}", _tlsProbe.EaswOrigin));
        }
        if (_tlsProbe.Ssf)
        {
            rewrites.Add(($"http://127.0.0.1:{Ports.Ssf}", _tlsProbe.SsfOrigin));
        }
        for (var index = 0; index < entries.Count; index++)
        {
            var (key, value) = entries[index];
            foreach (var (plain, tls) in rewrites)
            {
                if (value.StartsWith(plain, StringComparison.Ordinal))
                {
                    entries[index] = KeyValuePair.Create(key, tls + value[plain.Length..]);
                    break;
                }
            }
        }
        var payload = new TdfWriter().MapStringString("CONF", entries).Str("ID", configId).Str("ITYP", string.Empty).Str("TOKN", string.Empty).ToArray();
        return new(payload, $"Util.FetchClientConfig({configId})");
    }

    /// <summary>Request and response are TLST&lt;{DIRT FilterResult, UTXT}&gt;: every text passes unchanged.</summary>
    private static BlazeReply FilterForProfanity(Fire2Frame frame)
    {
        var texts = TdfReader.FindAllStrings(frame.Payload, "UTXT");
        var payload = new TdfWriter()
            .ListStruct("TLST", texts.Select(text => (Action<TdfWriter>)(w => w.Int("DIRT", 0).Str("UTXT", text))).ToList())
            .ToArray();
        return new(payload, "Util.FilterForProfanity");
    }

    /// <summary>Stats.getPeriodIds: day, week and month counters since the stats epoch.</summary>
    private byte[] PeriodIds()
    {
        var stats = _settings.Stats;
        var now = DateTimeOffset.FromUnixTimeSeconds(_game.NowSeconds).UtcDateTime;
        var epoch = new DateTime(stats.EpochYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var daily = (long)(now - epoch).TotalDays;
        var monthly = (now.Year - epoch.Year) * 12L + (now.Month - epoch.Month);
        return new TdfWriter()
            .Int("DBUF", 0).Int("DHOU", 0).Int("DLY", daily).Int("DRET", stats.DailyRetention)
            .Int("MBUF", 0).Int("MDAY", 1).Int("MHOU", 0).Int("MLY", monthly).Int("MRET", stats.MonthlyRetention)
            .Int("WBUF", 0).Int("WDAY", 1).Int("WHOU", 0).Int("WLY", daily / 7).Int("WRET", stats.WeeklyRetention)
            .ToArray();
    }

    /// <summary>Stats.getLeaderboardGroup: one ranked skill stat for the requested board.</summary>
    private byte[] LeaderboardGroup(string name)
    {
        var stats = _settings.Stats;
        return new TdfWriter()
            .Int("ASCD", 0).Str("BNAM", string.Empty).Str("DESC", name).Int("LBSZ", stats.LeaderboardSize)
            .ListStruct("LIST", new Action<TdfWriter>[]
            {
                w => w.Str("CATG", string.Empty).Str("DFLT", "0").Int("DRVD", 0).Str("FRMT", "%d").Str("KIND", string.Empty)
                    .Str("LDSC", stats.StatLongName).Str("META", string.Empty).Str("NAME", stats.StatName)
                    .Str("SDSC", stats.StatShortName).Int("TYPE", 0),
            })
            .Str("META", string.Empty).Str("NAME", name).Str("SNAM", name)
            .ToArray();
    }

    /// <summary>Stats.getStatGroup: StatGroupResponse naming the group, with no stat columns.</summary>
    private static byte[] StatGroup(string name) => new TdfWriter()
        .Str("CNAM", string.Empty).Str("DESC", name).Str("META", string.Empty).Str("NAME", name)
        .ListStruct("STAT", Array.Empty<Action<TdfWriter>>())
        .ToArray();

    /// <summary>
    /// Stats.GetStatsAsyncNotification for a getStatsByGroupAsync: KeyScopedStatValues echoing its group and view id, one
    /// value-less row per requested entity, and LAST so the client stops waiting.
    /// </summary>
    internal static BlazeNotification StatsByGroup(ReadOnlySpan<byte> request)
    {
        var name = TdfReader.FindString(request, "NAME") ?? string.Empty;
        var periodOffset = TdfReader.FindInteger(request, "POFF") ?? 0;
        IReadOnlyList<long> entities = TdfReader.TryParse(request, out var fields)
            ? (TdfReader.Find(fields, "EID", TdfType.List)?.Children ?? Array.Empty<TdfField>())
                .Where(child => child.Type == TdfType.Integer).Select(child => child.Integer).ToList()
            : Array.Empty<long>();
        var rows = entities.Select(entity => (Action<TdfWriter>)(w =>
            w.Int("EID", entity).Int("POFF", periodOffset).ListString("STAT", Array.Empty<string>()))).ToList();
        var payload = new TdfWriter()
            .Str("GRNM", name).Str("KEY", string.Empty).Int("LAST", 1)
            .Struct("STS", sts => sts.ListStruct("STAT", rows))
            .Int("VID", TdfReader.FindInteger(request, "VID") ?? 0)
            .ToArray();
        return new(Stats, StatsNotification.GetStatsAsync, payload, $"Stats.GetStatsAsyncNotification({name})");
    }

    /// <summary>Stats leaderboard rows (LDLS): the server's clubs ranked by Rivals skill.</summary>
    private async ValueTask<BlazeReply> LeaderboardValuesAsync(Fire2Frame frame, CancellationToken cancellationToken)
    {
        var count = TdfReader.FindInteger(frame.Payload, "COUN") is { } requested and > 0 ? (int)Math.Min(requested, _settings.Stats.LeaderboardSize) : _settings.Stats.LeaderboardSize;
        var ranked = await _game.ReadAsync(uow => _fut.Leaderboards.RankingsAsync(uow, count), cancellationToken);
        var rows = ranked.Select(entry => (Action<TdfWriter>)(w =>
        {
            var score = entry.Skill.ToString(System.Globalization.CultureInfo.InvariantCulture);
            w.Int("RANK", entry.Rank).Str("RSTA", score).Int("RWFG", 0).ListString("STAT", new[] { score })
                .Struct("USER", u => u
                    .Int("AID", entry.PersonaId).Int("ALOC", _settings.UserLocation).Blob("EXBB", ReadOnlySpan<byte>.Empty)
                    .Int("EXID", 0).Int("ID", entry.PersonaId).Str("NAME", entry.Name).Str("NASP", _settings.PersonaNamespace)
                    .Int("ORIG", entry.PersonaId).Int("PIDI", 0));
        })).ToList();
        return new(new TdfWriter().ListStruct("LDLS", rows).ToArray(), "Stats.GetLeaderboard");
    }

    private BlazeReply GetUserOption(Fire2Frame frame)
    {
        var key = TdfReader.FindString(frame.Payload, "KEY") ?? string.Empty;
        var value = _settings.UserOptions[key]?.GetValue<string>() ?? string.Empty;
        return new(new TdfWriter().Str("DATA", value).Str("KEY", key).ToArray(), $"Util.GetUserOption({key})");
    }

    /// <summary>PreAuth advertises the loopback OAuth settings and the QoS service (port 17502, not Blaze).</summary>
    private byte[] PreAuth()
    {
        var preAuth = _settings.PreAuth;
        var qos = preAuth.Qos;
        var conf = BlazeSettings.Pairs(preAuth.ClientConfig);
        conf.AddRange(LocalAuthService.Config(Ports.Fut, _tlsProbe.Nucleus ? _tlsProbe.BaseUrl : null));
        void QosServer(TdfWriter w, string name) => w.Str("PSA", _tlsProbe.AdvertisedHost).Int("PSP", Ports.Qos).Str("SNA", name);
        return new TdfWriter()
            .Str("ASRC", _settings.BuildSource)
            .ListInt("CIDS", preAuth.ComponentIds)
            .Struct("CONF", w => w.MapStringString("CONF", conf))
            .Int("EEFA", 1)
            .Str("ESRC", _settings.BuildSource)
            .Str("INST", _settings.InstanceName)
            // FIFA 21 echoes the assigned MAID as MACI in its matchmaking NetworkAddress, so every player needs their own.
            // PreAuth comes before login, so a hosted server cannot name the player yet (the local club's persona went to
            // everyone): each connection gets its own random id instead.
            .Int("MAID", MachineId)
            .Int("MINR", 0)
            .Str("NASP", _settings.PersonaNamespace)
            .Str("PILD", string.Empty)
            .Str("PLAT", _settings.Platform)
            .Struct("QOSS", w => w
                .Struct("BWPS", server => QosServer(server, "qos"))
                .Int("LNP", qos.ProbeCount)
                .MapStringStruct("LTPS", new[]
                {
                    KeyValuePair.Create<string, Action<TdfWriter>>(qos.PingSite, server => QosServer(server, qos.PingSite)),
                })
                .Int("SVID", qos.ServiceId)
                .Int("TIME", qos.TimeoutMs))
            .Str("RSRC", _settings.BuildSource)
            .Str("SVER", _settings.ServerVersion)
            .ToArray();
    }

    private byte[] LocalLogin()
    {
        var nucleus = _game.Config.NucleusId;
        var token = _game.Token;
        return new TdfWriter()
            .Int("CNTX", 0)
            .Int("ERRC", 0)
            .Str("SKEY", token)
            .Int("ANON", 0)
            .Int("NTOS", 0)
            .Struct("SESS", s => s
                .Int("1CON", 0)
                .Int("BUID", Persona)
                .Int("FRST", 0)
                .Str("KEY", token)
                .Int("LLOG", _game.NowSeconds)
                .Str("MAIL", _settings.Session.LocalEmail)
                .Struct("PDTL", p => p
                    .Str("DSNM", _game.Config.DisplayName)
                    .Int("LAST", 0)
                    .Int("PID", Persona)
                    .Int("PLAT", 4)
                    .Int("STAS", 0)
                    .Int("XREF", nucleus))
                .Int("UID", nucleus))
            .Int("SPAM", 0)
            .Int("UNDR", 0)
            .ToArray();
    }

    /// <summary>The exact c35/k10 login-response shape from the retail trace.</summary>
    private byte[] TitleLoginSession() => new TdfWriter()
        .Int("ANON", 0)
        .Int("NTOS", 0)
        .Struct("SESS", s => s
            .Int("1CON", 0)
            .Int("BUID", Persona)
            .Int("FRST", 0)
            .Str("KEY", SessionKey)
            .Int("LLOG", _game.NowSeconds)
            .Str("MAIL", _settings.Session.TitleEmail)
            .Struct("PDTL", p => p.Str("DSNM", _game.Config.DisplayName).Int("PID", Persona).Int("PLAT", 4))
            .Int("UID", Persona))
        .Int("SPAM", 1)
        .Int("UNDR", 0)
        .ToArray();

    private static void ExtendedData(TdfWriter w) => w
        .Int("HWFG", 0)
        .Struct("QDAT", q => q.Int("DBPS", 0).Int("NATT", 0).Int("UBPS", 0))
        .Int("UATT", 0);

    /// <summary>
    /// Blaze::PlatformInfo (AIDS, only PLAT). FIFA's user name index is keyed by platform and looks the local user up as
    /// pc (4): an identity without it is filed under INVALID (0), the client's own id resolves to 0 and the co-op lobby
    /// game handle never binds, so Ready does nothing (Python server 7ae589e, USER_PLATFORM_AIDS; observed 2026-09-17/18).
    /// AIDS sorts after AID and before ALOC, BLOB, BUID and ID.
    /// </summary>
    public static void PlatformInfo(TdfWriter w, long platform) => w.Struct("AIDS", aids => aids.Int("PLAT", platform));

    private byte[] UserAdded() => new TdfWriter()
        .Struct("DATA", ExtendedData)
        .Struct("USER", u => u
            .Int("AID", Persona)
            .Also(w => PlatformInfo(w, _settings.Online.UserPlatform))
            .Int("ALOC", _settings.UserLocation)
            .Blob("EXBB", ReadOnlySpan<byte>.Empty)
            .Int("EXID", 0)
            .Int("ID", Persona)
            .Str("NAME", _game.Config.DisplayName)
            .Str("NASP", _settings.PersonaNamespace)
            .Int("ORIG", Persona)
            .Int("PIDI", 0))
        .ToArray();

    /// <summary>UserSessionLoginInfo re-keys the entry UserAdded made, so it carries AIDS too (its sibling PLAT is not read).</summary>
    private byte[] LoginSessionNotification() => new TdfWriter()
        .Also(w => PlatformInfo(w, _settings.Online.UserPlatform))
        .Int("BUID", Persona)
        .ObjectId("CGID", UserSessions, UserSessionEntityType, Persona)
        .Str("DSNM", _game.Config.DisplayName)
        .Str("KEY", SessionKey)
        .Int("LAST", _game.NowSeconds)
        .Str("MAIL", _settings.Session.TitleEmail)
        .Str("NASP", _settings.PersonaNamespace)
        .Int("PID", Persona)
        .Int("PLAT", 0)
        .Int("UID", Persona)
        .Int("USTP", 0)
        .Int("XREF", 0)
        .ToArray();

    private byte[] ExtendedDataUpdate() => new TdfWriter().Struct("DATA", ExtendedData).Int("USID", Persona).ToArray();

    private void Telemetry(TdfWriter w) => w
        .Str("ADRS", _tlsProbe.AdvertisedHost)
        .Int("ANON", 0)
        .Str("DISA", _settings.Telemetry.Disabled)
        .Str("FILT", string.Empty)
        .Int("LOC", _settings.UserLocation)
        .Str("NOOK", string.Empty)
        .Int("PORT", _settings.Telemetry.Port)
        .Int("SDLY", 0)
        .Str("SESS", string.Empty)
        .Str("SKEY", string.Empty)
        .Int("SPCT", 0)
        .Str("STIM", string.Empty);

    private byte[] PostAuth() => new TdfWriter()
        .Struct("PSS", w => w.Int("PORT", 0).Int("RPRT", 0).Int("TIID", 0))
        .Struct("TELE", Telemetry)
        .Struct("TICK", w => w.Str("ADRS", _tlsProbe.AdvertisedHost).Int("PORT", _settings.Ticker.Port).Str("SKEY", string.Empty))
        .Struct("UROP", w => w.Int("TMOP", 0).Int("UID", Persona))
        .ToArray();

    private byte[] TickerSettings() => new TdfWriter().ListStruct("LSST", new Action<TdfWriter>[]
    {
        w => w.Str("ID", _settings.Ticker.SettingId).Int("LOCF", 0).Int("TOGG", 0),
    }).ToArray();

    private byte[] TickerGroups() => new TdfWriter().ListStruct("LGRP", new Action<TdfWriter>[]
    {
        w => w.Str("ID", _settings.Ticker.GroupId).ListString("LSET", new[] { _settings.Ticker.SettingId }),
    }).ToArray();

    private byte[] Entitlements() => new TdfWriter().ListStruct("NLST", _settings.Entitlements.Names
        .Select((name, index) => (Action<TdfWriter>)(w => w
            .Str("DEVI", string.Empty)
            .Str("GDAY", string.Empty)
            .Str("GNAM", name)
            .Int("ID", index + 1)
            .Int("ISCO", 0)
            .Int("PID", Persona)
            .Str("PJID", string.Empty)
            .Int("PRCA", 0)
            .Str("PRID", string.Empty)
            .Int("STAT", 1)
            .Int("STRC", 0)
            .Str("TAG", _settings.Entitlements.Tag)
            .Str("TDAY", string.Empty)
            .Int("TYPE", 1)
            .Int("UCNT", 0)
            .Int("VER", 0)))
        .ToList()).ToArray();
}
