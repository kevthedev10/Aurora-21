using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Aurora21.UltimateTeam.Protocol.Tdf;

namespace Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

/// <summary>A network endpoint one recipient uses for a roster member.</summary>
public readonly record struct PeerEndpoint(string Address, long Port);

/// <summary>The FIFA 21 GameManager PlayerRemovedReason values the server sends or receives.</summary>
public static class PlayerRemovedReason
{
    public const long PlayerConnectionLost = 2;
    public const long BlazeServerConnectionLost = 3;
    public const long GameDestroyed = 5;
    public const long PlayerLeft = 7;
    public const long GroupLeft = 8;
    public const long PlayerLeftCancelledMatchmaking = 25;
    public const long PlayerLeftSwitchedGameSession = 26;

    /// <summary>The highest value FIFA 21 knows; anything above is replaced by <see cref="PlayerLeft"/>.</summary>
    public const long Last = 26;
}

/// <summary>The FIFA 21 GameManager GameDestructionReason values the server sends.</summary>
public static class GameDestructionReason
{
    public const long SysGameEnding = 0;
}

/// <summary>
/// The TDF payloads of the Rivals matchmaking notifications. Field order is exactly what FIFA 21's
/// sequential decoder expects; values follow the Blaze 3 schema and the captured FIFA 21 clients. The
/// GameSetup family has the shape the production Python server sent (GAMESETUP_WIRE_FIX conservative, the
/// OSDK pregame items and USER_PLATFORM_AIDS; flag bank prod-flags-20260927), under which every verified
/// full online match was played.
/// </summary>
public sealed class MatchmakingPayloads
{
    /// <summary>The union member of a NetworkAddress that holds an IpPairAddress.</summary>
    private const byte IpPairMember = 2;

    /// <summary>The GameSetupReason union member for a matchmaking result, and that member's own tag.</summary>
    private const byte MatchmakingSetupReason = 3;
    private const string MatchmakingSetupReasonTag = "MMSC";

    /// <summary>FIFA 21's gameplay UDP port, used when StartMatchmaking names none.</summary>
    public const long DefaultGameplayPort = 3659;

    /// <summary>ReplicatedGamePlayer.STAT: ACTIVE_CONNECTING in GameSetup, ACTIVE_CONNECTED once the mesh is complete.</summary>
    private const long PlayerActiveConnecting = 2;
    private const long PlayerActiveConnected = 4;

    /// <summary>
    /// The scenario id GameSetup (MMSC.MSCD/MSID) and ConnectionValidated (MSCD) publish. onNotifyGameSetup creates the
    /// GameSetupJob only when MSCD equals the StartMatchmaking reply's MSID, and that reply is empty (0).
    /// </summary>
    private const long PublishedScenarioId = 0;

    private const long MatchmakingCancelled = 4;
    private const int UserSessionEntityType = 2;

    /// <summary>ServerMessage.SRCE: ENTITY_TYPE_USER {30722, 1} (the peer's bridge compares only the type half).</summary>
    private const int UserEntityType = 1;

    private static readonly KeyValuePair<string, string>[] RivalsGameAttributes =
    {
        new("CUSTOM_CONTROLLER", "0"), new("FUT_COOP_MATCHMAKING_TYPE", "1"), new("FUT_NEW_USER", "1"),
        new("FUT_RIVALS_OVERRIDE_VALUE", "1000"), new("FUT_RIVALS_SEARCH_VALUE", "1000"), new("FUT_RIVALS_THR", "OSDK_matchRelax"),
        new("GAMEGROUP_ONLY", "0"), new("GAME_MODE", "76"), new("HALF_LENGTH", "4"), new("HALF_LENGTH_THR", string.Empty),
        new("MATCHUP_HASH", "1684366964"), new("MATCH_CLUB_TYPE", "0"), new("MATCH_GUESTS", "0"), new("NET_TOP", "1"), new("WOMEN_RULE", "0"),
    };

    /// <summary>The game attribute FIFA reads the match's game speed from (see <see cref="OnlineGameSpeed"/>).</summary>
    internal const string GameSpeedAttribute = "fifaGameSpeed";

    private readonly BlazeSettings _settings;
    private readonly Func<long> _nowSeconds;
    private readonly Func<long> _nowMicroseconds;

    /// <param name="nowMicroseconds">The TimeValue clock (joinedGameTimestamp); defaults to <paramref name="nowSeconds"/> in microseconds.</param>
    /// <param name="gameSpeed">The shared speed setting the panel edits; defaults to blaze.json <c>online.gameSpeed</c>.</param>
    /// <param name="pregameDeparture">The shared pregame-departure mode the panel edits; defaults to <see cref="PregameDeparture.Silent"/>.</param>
    public MatchmakingPayloads(BlazeSettings settings, Func<long> nowSeconds, Func<long>? nowMicroseconds = null, OnlineGameSpeed? gameSpeed = null,
        PregameDeparture? pregameDeparture = null)
    {
        _settings = settings;
        _nowSeconds = nowSeconds;
        _nowMicroseconds = nowMicroseconds ?? (() => nowSeconds() * 1_000_000);
        GameSpeed = gameSpeed ?? new OnlineGameSpeed(settings.Online.GameSpeed);
        PregameDeparture = pregameDeparture ?? new PregameDeparture();
    }

    /// <summary>How a departure that ends a game before IN_GAME is announced.</summary>
    public PregameDeparture PregameDeparture { get; }

    /// <summary>The online-match wire values and relay limits (blaze.json <c>online</c>).</summary>
    public BlazeOnlineSettings Online => _settings.Online;

    /// <summary>The game speed new online games are played at.</summary>
    public OnlineGameSpeed GameSpeed { get; }

    /// <summary>The direct endpoint of a player: its peer address and the last gameplay port its StartMatchmaking named.</summary>
    public static PeerEndpoint DirectEndpoint(MatchmakingEntry entry)
    {
        var ports = TdfReader.FindAllIntegers(entry.Payload, "PORT").Where(port => port > 0).ToList();
        return new PeerEndpoint(entry.Client!.PeerAddress, ports.Count > 0 ? ports[^1] : DefaultGameplayPort);
    }

    /// <summary>
    /// The endpoint <paramref name="recipient"/> uses for <paramref name="subject"/>: its own direct endpoint,
    /// or, with a relay, the recipient's own relay port, which stands for the peer in its roster view.
    /// </summary>
    public static PeerEndpoint ProjectedEndpoint(RivalsMatch match, MatchmakingEntry recipient, MatchmakingEntry subject)
    {
        if (recipient.PlayerId == subject.PlayerId || match.Relay is null)
        {
            return DirectEndpoint(subject);
        }
        var (address, port) = match.Relay.EndpointFor(recipient.PlayerId);
        return new PeerEndpoint(address, port);
    }

    /// <summary>An IpPairAddress union value; the enclosing pair carries the machine id.</summary>
    private static Action<TdfWriter> IpPair(PeerEndpoint endpoint, long machineId) => w =>
    {
        var ip = IPAddress.TryParse(endpoint.Address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
            ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(parsed.GetAddressBytes())
            : 0;
        void Address(TdfWriter a) => a.Int("IP", ip).Int("MACI", 0).Int("PORT", Math.Max(0, endpoint.Port));
        w.Struct("EXIP", Address).Struct("INIP", Address).Int("MACI", Math.Max(0, machineId));
    };

    private void ExtendedData(TdfWriter w, PeerEndpoint endpoint, long machineId) => w
        .Union("ADDR", IpPairMember, IpPair(endpoint, machineId))
        .Int("HWFG", 0)
        .Struct("QDAT", q => q.Int("DBPS", 0).Int("NATT", 0).Int("UBPS", 0))
        .Int("UATT", 0);

    /// <summary>A UserIdentification; its AIDS keys the client's name index (see <see cref="BlazeResponses.PlatformInfo"/>).</summary>
    private void User(TdfWriter w, BlazeClient peer, string name) => w
        .Int("AID", peer.PersonaId)
        .Also(u => BlazeResponses.PlatformInfo(u, Online.UserPlatform))
        .Int("ALOC", _settings.UserLocation)
        .Blob("EXBB", ReadOnlySpan<byte>.Empty)
        .Int("EXID", 0)
        .Int("ID", peer.PersonaId)
        .Str("NAME", name)
        .Str("NASP", _settings.PersonaNamespace)
        .Int("ORIG", peer.PersonaId)
        .Int("PIDI", 0);

    /// <summary>UserSessions.UserAdded for another peer (DirtySDK resolves peers through UserSessions too).</summary>
    public byte[] PeerUserAdded(BlazeClient peer, PeerEndpoint endpoint) => PeerUserData(peer, endpoint, peer.DisplayName);

    private byte[] PeerUserData(BlazeClient peer, PeerEndpoint endpoint, string name) => new TdfWriter()
        .Struct("DATA", w => ExtendedData(w, endpoint, peer.AdvertisedMachineId))
        .Struct("USER", w => User(w, peer, name))
        .ToArray();

    public static byte[] PeerUserUpdated(BlazeClient peer) => new TdfWriter().Int("FLGS", 3).Int("ID", peer.PersonaId).ToArray();

    /// <summary>
    /// UserSessions.ExtendedDataUpdate. FIFA 21's own local-user record needs the NetworkAddress: without
    /// ADDR its union stays invalid and the match report builder dereferences null.
    /// </summary>
    public byte[] PeerExtendedDataUpdate(BlazeClient peer, PeerEndpoint endpoint) => new TdfWriter()
        .Struct("DATA", w => ExtendedData(w, endpoint, peer.AdvertisedMachineId))
        .Int("USID", peer.PersonaId)
        .ToArray();

    /// <summary>
    /// LookupUsersByPersonaNames (30722/50): the users that exist under the requested names, each under its own identity, like
    /// retail and the production Python server (roster players whose name matches, case-insensitively; the whole roster when
    /// no names are asked; nothing outside a game). FIFA asks this for its EA friends list about once a minute: answering every
    /// name with a roster player filed the opponent and the caller under friends' names mid-match.
    /// </summary>
    public byte[] LookupUsers(RivalsMatch? match, BlazeClient caller, IReadOnlyList<string> requestedNames)
    {
        if (match is null)
        {
            return new TdfWriter().ListStruct("ULST", Array.Empty<Action<TdfWriter>>()).ToArray();
        }
        var me = match.Entry(caller.PlayerId);
        var requested = requestedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var users = new List<Action<TdfWriter>>();
        foreach (var entry in match.Players.Where(player => player.Client is not null)
            .Where(player => requested.Count == 0 || requested.Contains(player.Client!.DisplayName))
            .OrderBy(player => player.PlayerId == caller.PlayerId))
        {
            var endpoint = ProjectedEndpoint(match, me ?? entry, entry);
            users.Add(w => w.Struct("DATA", d => ExtendedData(d, endpoint, entry.Client!.AdvertisedMachineId)).Struct("USER", u => User(u, entry.Client!, entry.Client!.DisplayName)));
        }
        return new TdfWriter().ListStruct("ULST", users).ToArray();
    }

    /// <summary>MatchmakingFinished for a cancelled search; FIFA's CancelMatchmaking MSID is echoed back.</summary>
    public static byte[] MatchmakingCancelledNotification(long sessionId, long personaId) => new TdfWriter()
        .Int("MAXF", 0).Int("MSID", sessionId).Int("RSLT", MatchmakingCancelled).Int("USID", personaId).ToArray();

    /// <summary>
    /// MatchmakingFinished on success. MSID stays 0: the client never learns the server's queue id and
    /// resolving an unknown session crashed it.
    /// </summary>
    public static byte[] MatchmakingFinishedNotification(BlazeClient recipient, long result) => new TdfWriter()
        .Int("MAXF", 0).Int("MSID", 0).Int("RSLT", result).Int("USID", recipient.PersonaId).ToArray();

    public static byte[] GamePlayerStateChange(long gameId, long personaId) =>
        new TdfWriter().Int("GID", gameId).Int("PID", personaId).Int("STAT", PlayerActiveConnected).ToArray();

    /// <summary>PlayerJoinCompleted with joinedGameTimestamp, a TimeValue in microseconds (after PID in tag order).</summary>
    public byte[] PlayerJoinCompleted(long gameId, long personaId) =>
        new TdfWriter().Int("GID", gameId).Int("PID", personaId).Int("TIME", _nowMicroseconds()).ToArray();

    /// <summary>
    /// PlayerRemoved (4/40) {GID, PID, REAS}, the roster member order of PlayerJoinCompleted plus the removal reason (a
    /// <see cref="PlayerRemovedReason"/>). FIFA 21's PlayerRemovedReason enum runs PLAYER_JOIN_TIMEOUT 0,
    /// PLAYER_CONN_POOR_QUALITY 1, PLAYER_CONN_LOST 2, BLAZESERVER_CONN_LOST 3, MIGRATION_FAILED 4, GAME_DESTROYED 5,
    /// GAME_ENDED 6, PLAYER_LEFT 7, GROUP_LEFT 8, PLAYER_KICKED 9 ... PLAYER_LEFT_SWITCHED_GAME_SESSION 26: a quit is
    /// <see cref="PlayerRemovedReason.PlayerLeft"/>, never 0.
    /// </summary>
    public static byte[] PlayerRemoved(long gameId, long personaId, long reason = PlayerRemovedReason.PlayerLeft) =>
        new TdfWriter().Int("GID", gameId).Int("PID", personaId).Int("REAS", reason).ToArray();

    /// <summary>
    /// GameRemoved (4/16) {GID, REAS}, REAS a FIFA 21 GameDestructionReason: SYS_GAME_ENDING 0, SYS_CREATION_FAILED 1,
    /// SYS_GAME_RECREATE 2, SYS_GAME_FAILED_CONNECTION_VALIDATION 3, HOST_LEAVING 4 ... LOCAL_PLAYER_LEAVING 7.
    /// </summary>
    public static byte[] GameRemoved(long gameId, long reason = GameDestructionReason.SysGameEnding) =>
        new TdfWriter().Int("GID", gameId).Int("REAS", reason).ToArray();

    public static byte[] GameStateChange(long gameId, long state) => new TdfWriter().Int("GID", gameId).Int("GSTA", state).ToArray();

    /// <summary>
    /// PlatformHostInitialized with platformHostId: the client's handler overwrites Game+0x798 with PHID and Game+0x790 with
    /// findPlayer(PHID) unconditionally, so without it both are null and no per-peer osdk channel is created (Python server
    /// 8cfb6ee; "one client stuck Initializing", observed 2026-09-18).
    /// </summary>
    public static byte[] PlatformHostInitialized(long gameId, long hostPersonaId, long hostSlot) =>
        new TdfWriter().Int("GID", gameId).Int("PHID", hostPersonaId).Int("PHST", Math.Clamp(hostSlot, 0, 255)).ToArray();

    /// <summary>
    /// GameSessionUpdated from the host's UpdateGameSession: only XNNC and XSES are copied. The request's
    /// PlayStation-only NPSI is not part of the notification and replaying it made the guest abandon setup.
    /// </summary>
    public static byte[] GameSessionUpdated(long gameId, byte[] hostRequest)
    {
        var writer = new TdfWriter().Int("GID", gameId);
        foreach (var tag in new[] { "XNNC", "XSES" })
        {
            if (TdfReader.FindBlob(hostRequest, tag) is { Length: > 0 } value)
            {
                writer.Blob(tag, value);
            }
        }
        return writer.ToArray();
    }

    /// <summary>
    /// MatchmakingSessionConnectionValidated: completes one recipient's own GameSetup job. onNotify finds the scenario
    /// by MSCD; members are in tag order (CONV: ALST FCNT GIDL NTOP TIER).
    /// </summary>
    public byte[] ConnectionValidated(RivalsMatch match, MatchmakingEntry recipient) => new TdfWriter()
        .Struct("CONV", w => w.ListInt("ALST", Array.Empty<long>()).Int("FCNT", 0).ListInt("GIDL", Array.Empty<long>()).Int("NTOP", Online.NetworkTopology).Int("TIER", 0))
        .Int("DISP", 1)
        .Int("GID", match.GameId)
        .ObjectId("GRID", BlazeComponent.UserSessions, UserSessionEntityType, recipient.Client!.PersonaId)
        .Int("MSCD", PublishedScenarioId)
        .Int("MSID", recipient.SessionId)
        .Int("QSVR", 0)
        .Int("USID", recipient.Client.PersonaId)
        .ToArray();

    /// <summary>
    /// One ReplicatedGamePlayer. AIDS comes first: GameManager's roster handler passes it to acquireUser, and 20/22 are the
    /// last acquireUser calls before the Game constructor reads the local id back, so an AIDS-less roster reverts every
    /// index entry the login notifications fixed. PATT carries the "Guest" title attribute and any attributes the player
    /// set through SetPlayerAttributes.
    /// </summary>
    private void MatchPlayer(TdfWriter w, MatchmakingEntry entry, long gameId, int slot, PeerEndpoint endpoint,
        IReadOnlyDictionary<string, string>? storedAttributes)
    {
        var client = entry.Client!;
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["Guest"] = "0" };
        foreach (var (key, value) in storedAttributes ?? new Dictionary<string, string>())
        {
            attributes[key] = value;
        }
        BlazeResponses.PlatformInfo(w, Online.UserPlatform);
        w.Blob("BLOB", ReadOnlySpan<byte>.Empty)
            // CONG is a 64-bit scalar here (an ObjectId is rejected); CSID indexes the connection record table,
            // so each roster endpoint needs its own slot or the guest overwrites the host.
            .Int("CONG", client.PersonaId)
            .Int("CSID", slot)
            .Int("EXID", 0)
            .Int("GID", gameId)
            .Int("JFPS", 0)
            .Int("LOC", _settings.UserLocation)
            .Str("NAME", client.DisplayName)
            .Str("NASP", _settings.PersonaNamespace)
            // The FUT wrapper reconciles the roster by the "Guest" title attribute.
            .MapStringString("PATT", attributes.ToArray())
            .Int("PID", client.PersonaId)
            .Union("PNET", IpPairMember, IpPair(endpoint, client.AdvertisedMachineId))
            .Int("PSET", 0)
            .Int("RCRE", 0)
            .Str("ROLE", string.Empty)
            .Int("SID", slot)
            .Int("SLOT", 0)
            .Int("STAT", PlayerActiveConnecting)
            .Int("TIDX", slot)
            // joinedGameTimestamp is a TimeValue (microseconds).
            .Int("TIME", _nowMicroseconds())
            .ObjectId("UGID", BlazeComponent.UserSessions, UserSessionEntityType, client.PersonaId)
            .Int("UID", client.PersonaId)
            .Str("UUID", string.Empty);
    }

    /// <summary>
    /// A non-Rivals game's title attributes: its host's own search criteria (SCNA; Python server 51a1670). The fixed list is
    /// FUT_Rivals' criteria (GAME_MODE 76); a FUT_Draft search asks for GAME_MODE 80 with FUT_TOURNY_RND and a FUT_Champions
    /// one for GAME_MODE 79 with FUT_CHAMPIONS_FORM, so its game
    /// publishes what its host searched with, NET_TOP kept as this server's. Null keeps the fixed list.
    /// </summary>
    private static KeyValuePair<string, string>[]? CriteriaAttributes(RivalsMatch match)
    {
        if (MatchmakingRegistry.ModeKey(match.Mode) == MatchmakingRegistry.RivalsMode)
        {
            return null;
        }
        var attributes = TdfReader.FindGenericMap(match.Players[0].Payload, "SCNA").ToDictionary(StringComparer.Ordinal);
        // An online Friendly never takes the Rivals list (GAME_MODE 76): it publishes what its host searched with.
        if (!attributes.ContainsKey("GAME_MODE") && !HouseRules.IsHouseRulesMode(match.Mode))
        {
            return null;
        }
        attributes["NET_TOP"] = RivalsGameAttributes.First(attribute => attribute.Key == "NET_TOP").Value;
        return attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Per recipient (Python server e55699b, on by default): the FUT frontend classifies roster players through these game
    /// attributes (IsFUTUserTeam / IsFUTOpponentTeam), the recipient's own team index first, the opponent's second.
    /// </summary>
    private static KeyValuePair<string, string>[] TeamAttributes(int recipientIndex, int players) =>
    [
        new("FUT_USER_TEAM_ID", recipientIndex.ToString(CultureInfo.InvariantCulture)),
        new("FUT_OPPONENT_TEAM_ID", (players > 1 ? 1 - recipientIndex : 0).ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>
    /// GAME.ATTR, sorted by key: the mode's title attributes and team indices, the OSDK roster keys (PreGame.GetGameInfo() is
    /// exactly this map and Session._Initialize aborts with OSDK_OPP_LEFT while OSDK_rosterVersion is undefined), then any
    /// attributes the clients stored through SetGameAttributes, which win. The game speed is the server's alone:
    /// fifaGameSpeed, the same for both players and fixed per game, or no key (clients cannot store one).
    /// </summary>
    private KeyValuePair<string, string>[] GameAttributes(RivalsMatch match, int recipientIndex, IReadOnlyDictionary<string, string> stored)
    {
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in (CriteriaAttributes(match) ?? RivalsGameAttributes).Concat(TeamAttributes(recipientIndex, match.Players.Count)))
        {
            attributes[key] = value;
        }
        attributes["OSDK_rosterURL"] = string.Empty;
        attributes["OSDK_rosterVersion"] = Online.OsdkRosterVersion;
        foreach (var (key, value) in stored)
        {
            attributes[key] = value;
        }
        if (match.GameSpeed(GameSpeed) is { } speed)
        {
            attributes[GameSpeedAttribute] = speed.ToString(CultureInfo.InvariantCulture);
        }
        if (match.HouseRuleType is { } houseRule)
        {
            // Both players play the one game type they share, whatever either searched with: FIFA reads the key
            // case-insensitively, so no other spelling of it may stay.
            var type = houseRule.ToString(CultureInfo.InvariantCulture);
            foreach (var key in attributes.Keys.Where(key => string.Equals(key, HouseRules.GameTypeAttribute, StringComparison.OrdinalIgnoreCase)
                                                             || string.Equals(key, HouseRuleTypeCriterion, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                attributes.Remove(key);
            }
            attributes[HouseRules.GameTypeAttribute] = type;
            attributes[HouseRuleTypeCriterion] = type;
        }
        return attributes.ToArray();
    }

    /// <summary>The house-rule game type under the name FIFA registers its matchmaking attribute with (rule table 0x140c04c11).</summary>
    private const string HouseRuleTypeCriterion = "FUT_FRIENDY_HOUSE_RULES_TYPE";

    /// <summary>A game-speed key as FIFA matches attribute keys (case-insensitively); clients may not store or relay one.</summary>
    internal static bool IsGameSpeedKey(string key) => string.Equals(key, GameSpeedAttribute, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// GameSetup (4/20) or JoiningPlayerInitiateConnections (4/22) for one recipient: the two-player game, its roster with
    /// endpoints projected for that recipient, and the matchmaking reason. NotifyGameSetup members go out in ascending
    /// packed-tag order (GAME LFPJ PROS QOSS QOSV QUEU REAS TELM); REAS is GameSetupReason member 3 under its own tag MMSC.
    /// GAME leaves out HSES, IGNO, TCAP, XNNC and XSES, which are not ReplicatedGameData members (HSES moves into the
    /// HostInfos). Before this shape the client dropped REAS, QOSS, QOSV and LFPJ, read a host session id of 0 and flooded
    /// ConnectionTelemetry (Python server GAMESETUP_WIRE_FIX conservative, live since 2026-09-19).
    /// </summary>
    /// <param name="gameSettings">GAME.GSET: the value after SetGameSettings, else the open settings a new game starts with.</param>
    public byte[] GameSetup(RivalsMatch match, int recipientIndex, long? gameSettings = null,
        IReadOnlyDictionary<string, string>? gameAttributes = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? playerAttributes = null)
    {
        var players = match.Players;
        var host = players[0].Client!;
        var recipient = players[recipientIndex];
        var hostEndpoint = ProjectedEndpoint(match, recipient, players[0]);
        var protocolVersion = TdfReader.FindString(players[0].Payload, "GVER") is { Length: > 0 } version ? version : _settings.Matchmaking.DefaultProtocolVersion;
        var criteria = CriteriaAttributes(match);
        var storedGame = gameAttributes ?? new Dictionary<string, string>();
        void HostInfo(TdfWriter w) => w.Int("CONG", host.PersonaId).Int("CSID", 0).Int("HPID", host.PersonaId).Int("HSES", host.PersonaId).Int("HSLT", 0);

        return new TdfWriter()
            .Struct("GAME", game => game
                .ListInt("ADMN", new[] { host.PersonaId })
                .MapStringString("ATTR", GameAttributes(match, recipientIndex, storedGame))
                .ListInt("CAP", new long[] { 2, 0, 0, 0 })
                .MapStringString("CRIT", Array.Empty<KeyValuePair<string, string>>())
                .Int("GID", match.GameId)
                .Str("GNAM", criteria is null ? $"{_settings.Matchmaking.GameNamePrefix} {match.GameId}" : $"{_settings.Matchmaking.ModeGameNamePrefix} {match.Mode} {match.GameId}")
                .Int("GPVH", 0)
                // PreGame.IsGameLocked() is GSET & 0x80f == 0: with 0 the local player's join leaves the session with
                // OSDK_GAME_UNAVAILABLE instead of requesting the session data.
                .Int("GSET", gameSettings ?? Online.InitialGameSettings)
                .Int("GSID", match.ReportId)
                .Int("GSTA", GameState.ConnectionVerification)
                .Str("GTYP", "gameType0")
                .ListUnion("HNET", new (byte, Action<TdfWriter>)[] { (IpPairMember, IpPair(hostEndpoint, host.AdvertisedMachineId)) })
                .MapStringString("MATR", Array.Empty<KeyValuePair<string, string>>())
                .Int("MCAP", 2)
                .Struct("NQOS", q => q.Int("DBPS", 0).Int("NATT", 0).Int("UBPS", 0))
                .Int("NRES", 0)
                // 130 = PEER_TO_PEER_FULL_MESH: with 0 neither player sees the pre-match ping readout (owner, 2026-09-19).
                .Int("NTOP", Online.NetworkTopology)
                .Str("PGID", string.Empty)
                .Blob("PGSR", ReadOnlySpan<byte>.Empty)
                .Struct("PHST", HostInfo)
                .Int("PRES", 1)
                .Str("PSAS", string.Empty)
                .Int("QCAP", 0)
                .Int("SEED", match.GameId)
                .Struct("THST", HostInfo)
                .ListInt("TIDS", new long[] { 0, 1 })
                .Str("UUID", $"{_settings.Matchmaking.GameUuidPrefix}{match.GameId}")
                .Int("VOIP", 0)
                .Str("VSTR", protocolVersion))
            .Int("LFPJ", 0)
            .ListStruct("PROS", players.Select((player, slot) => (Action<TdfWriter>)(w =>
                MatchPlayer(w, player, match.GameId, slot, ProjectedEndpoint(match, recipient, player), playerAttributes?.GetValueOrDefault(player.PlayerId)))).ToList())
            // QOSS equals the client's constructor defaults.
            .Struct("QOSS", w => w.Int("DURA", 5000).Int("INTV", 250).Int("SIZE", 50))
            .Int("QOSV", 0)
            .ListStruct("QUEU", Array.Empty<Action<TdfWriter>>())
            .Union("REAS", MatchmakingSetupReason, MatchmakingSetupReasonTag, w => w
                .Int("FIT", 1000)
                .Int("MAXF", 1000)
                .Int("MSCD", PublishedScenarioId)
                .Int("MSID", PublishedScenarioId)
                .Int("RSLT", recipientIndex == 0 ? 0 : Online.GameSetupJoinerResult)
                .Int("USID", recipient.Client!.PersonaId))
            .Int("TELM", Math.Max(0, Online.GameSetupTelemetryMicroseconds))
            .ToArray();
    }

    /// <summary>Messaging.SendMessage reply: SendMessageResponse {MGID, MIDS}, the id the relayed NotifyMessage carries too.</summary>
    public static byte[] SendMessageResponse(long messageId) => new TdfWriter().Int("MGID", messageId).ListInt("MIDS", new[] { messageId }).ToArray();

    /// <summary>
    /// Messaging NotifyMessage: a ServerMessage {FLAG, MGID, PYLD, SRCE, TIME, USER} whose PYLD is the sender's ClientMessage
    /// relayed verbatim, which keeps TYPE, TAG, FLAG, STAT, TTYP, TIDS and ATTR byte-exact.
    /// </summary>
    public byte[] ServerMessage(ReadOnlySpan<byte> clientMessage, long messageId, BlazeClient sender) => new TdfWriter()
        // ServerFlags: bit 0 is Fetched; this is a push, not a fetch.
        .Int("FLAG", 0)
        .Int("MGID", messageId)
        .StructRaw("PYLD", clientMessage)
        .ObjectId("SRCE", BlazeComponent.UserSessions, UserEntityType, sender.PersonaId)
        .Int("TIME", _nowSeconds())
        .Struct("USER", u => u
            .Also(w => BlazeResponses.PlatformInfo(w, Online.UserPlatform))
            .Int("ID", sender.PersonaId)
            .Str("NAME", sender.DisplayName)
            .Str("NASP", _settings.PersonaNamespace))
        .ToArray();

    /// <summary>GameManager PlayerAttribChange (4/90) {ATTR, GID, PID}.</summary>
    public static byte[] PlayerAttribChange(long gameId, long personaId, IReadOnlyDictionary<string, string> attributes) => new TdfWriter()
        .MapStringString("ATTR", attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())
        .Int("GID", gameId)
        .Int("PID", personaId)
        .ToArray();

    /// <summary>GameManager GameAttribChange (4/80) {ATTR, GID}.</summary>
    public static byte[] GameAttribChange(long gameId, IReadOnlyDictionary<string, string> attributes) => new TdfWriter()
        .MapStringString("ATTR", attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())
        .Int("GID", gameId)
        .ToArray();

    /// <summary>GameManager GameSettingsChange (4/110) {ATTR = the GameSettings bitfield, GID}.</summary>
    public static byte[] GameSettingsChange(long gameId, long settings) => new TdfWriter()
        .Int("ATTR", settings & 0xFFFFFFFF)
        .Int("GID", gameId)
        .ToArray();
}
