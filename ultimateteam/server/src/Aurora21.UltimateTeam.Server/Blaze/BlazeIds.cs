namespace Aurora21.UltimateTeam.Server.Blaze;

/// <summary>Blaze component ids the FIFA 21 client talks to.</summary>
public static class BlazeComponent
{
    public const int Authentication = 1;
    public const int GameManager = 4;
    public const int Messaging = 15;
    public const int Stats = 7;
    public const int Util = 9;
    public const int CensusData = 10;
    public const int GameReporting = 28;

    /// <summary>Not a FIFA 21 component (the client registers no component 35); kept for the parity vectors.</summary>
    public const int Fifa = 35;
    public const int OsdkSettings = 2249;
    public const int SponsoredEvents = 2076;
    public const int UserSessions = 30722;
    public const int UserActivityTracker = 2234;
}

public static class UtilCommand
{
    public const int FetchClientConfig = 1;
    public const int Ping = 2;
    public const int GetTelemetryServer = 5;
    public const int PreAuth = 7;
    public const int PostAuth = 8;
    public const int GetUserOption = 10;
    public const int GetUserOptions = 12;
    public const int FilterForProfanity = 20;
    public const int ClientMetrics = 22;

    /// <summary>The client's connection state (ACTV); void.</summary>
    public const int SetConnectionState = 23;
    public const int ClientState = 28;
}

/// <summary>Authentication commands, grouped by the reply FIFA 21 accepts for them.</summary>
public static class AuthenticationCommand
{
    public const int Login = 0x0A;
    public const int ListEntitlements = 0x20;
    public const int GetAuthToken = 0x24;
    public const int Logout = 0x46;

    public static readonly int[] LocalLogins = { 0x28, 0x32, 0x3C, 0x98, Login };
    public static readonly int[] Entitlements = { 0x1D, 0x30 };
    public static readonly int[] AccountPersonas = { 0x1E, 0x5A, 0x64, 0x6E };
}

public static class CensusDataCommand
{
    public const int UnsubscribeFromCensusData = 2;
    public const int SubscribeToCensusDataUpdates = 5;
}

public static class SponsoredEventsCommand
{
    public const int GetEventsUrl = 3;
}

public static class GameReportingCommand
{
    /// <summary>The ONLINE end-of-match report (Rivals, Weekend League, Online Draft).</summary>
    public const int SubmitGameReport = 1;
    public const int SubmitOfflineGameReport = 2;
    public const int ResultNotification = 114;
}

/// <summary>UserActivityTracker (component 2234).</summary>
public static class UserActivityTrackerCommand
{
    /// <summary>Sent once per match, empty; CardsDLL's completion handler (0x140d7d710) only checks the RPC error code.</summary>
    public const int IncrementMatchPlayed = 5;
}

public static class FifaCommand
{
    public const int LoginSession = 10;
}

public static class OsdkSettingsCommand
{
    public const int FetchSettings = 1;
    public const int FetchSettingsGroups = 2;
}

/// <summary>GameManager commands FIFA 21 sends while forming a matchmade game.</summary>
public static class GameManagerCommand
{
    public const int AdvanceGameState = 3;

    /// <summary>OSDK pregame: the arbitrator's LockGame, completed by GameSettingsChange.</summary>
    public const int SetGameSettings = 4;

    /// <summary>OSDK pregame game attributes (the Ready vote's ARB_3 step), relayed as GameAttribChange.</summary>
    public const int SetGameAttributes = 7;

    /// <summary>OSDK pregame player attributes (REQ=1 / REQ=2), relayed as PlayerAttribChange.</summary>
    public const int SetPlayerAttributes = 8;

    /// <summary>RemovePlayerRequest: a player leaves a game (REAS 26 when it moves on from a game it still held).</summary>
    public const int RemovePlayer = 11;
    public const int UpdateGameSession = 15;
    public const int StartMatchmakingScenario = 16;
    public const int CancelMatchmaking = 17;

    /// <summary>RemovePlayerRequest for the player's group: how FIFA 21 leaves a game (REAS 8 GROUP_LEFT).</summary>
    public const int LeaveGameByGroup = 22;
    public const int MeshEndpointsConnected = 65;
    public const int MeshEndpointsDisconnected = 66;

    /// <summary>The client lost its peer link to the target player (TCG).</summary>
    public const int MeshEndpointsConnectionLost = 67;
    public const int ConnectionTelemetry = 171;
}

/// <summary>GameManager notifications the server pushes to matchmade players.</summary>
public static class GameManagerNotification
{
    public const int MatchmakingFinished = 10;
    public const int MatchmakingSessionConnectionValidated = 11;

    /// <summary>
    /// NotifyGameRemoved {GID, REAS}, 0x10 in the client's GameManager notification table: what destroys the client's own Game
    /// object. Without it a player left behind in the pregame keeps the dead game, and its next Rivals setup dies 1-5 s into
    /// the kit screen (live 2026-10-05: 236 of 259 instant setup failures were the previous setup's remaining player).
    /// </summary>
    public const int GameRemoved = 16;
    public const int GameSetup = 20;
    public const int JoiningPlayerInitiateConnections = 22;
    public const int PlayerJoinCompleted = 30;

    /// <summary>
    /// NotifyPlayerRemoved {GID, PID, REAS}, the roster notification for a player that left: 0x28 in the Blaze GameManager
    /// table every other id here follows (10, 11, 20, 22, 30, 71, 80, 90, 100, 110, 115, 116); 32 (0x20) is no GameManager
    /// notification there.
    /// </summary>
    public const int PlayerRemoved = 40;
    public const int PlatformHostInitialized = 71;
    public const int GameAttribChange = 80;
    public const int PlayerAttribChange = 90;
    public const int GameStateChange = 100;
    public const int GameSettingsChange = 110;
    public const int GameSessionUpdated = 115;
    public const int GamePlayerStateChange = 116;
}

public static class StatsCommand
{
    public const int GetStatGroup = 4;
    public const int GetLeaderboardGroup = 10;
    public const int GetLeaderboard = 12;
    public const int GetCenteredLeaderboard = 13;
    public const int GetFilteredLeaderboard = 14;
    public const int GetPeriodIds = 20;

    /// <summary>Answered with an empty ack; the values follow in <see cref="StatsNotification.GetStatsAsync"/>.</summary>
    public const int GetStatsByGroupAsync = 16;
}

public static class StatsNotification
{
    /// <summary>KeyScopedStatValues: the values a getStatsByGroupAsync asked for (LAST closes the request).</summary>
    public const int GetStatsAsync = 50;
}

public static class UserSessionsCommand
{
    /// <summary>Carries only HWFG.</summary>
    public const int UpdateHardwareFlags = 8;
    public const int LookupUser = 12;

    /// <summary>The client's network info (INFO: ADDR with MACI, NQOS), sent at login and periodically.</summary>
    public const int UpdateNetworkInfo = 20;
    public const int LookupUsersByPersonaNames = 50;
}

/// <summary>Blaze Messaging: the FUT co-op kit screen runs its Ready vote over ordinary <c>gctl</c> messages.</summary>
public static class MessagingCommand
{
    public const int SendMessage = 1;
}

public static class MessagingNotification
{
    public const int NotifyMessage = 1;
}

public static class UserSessionsNotification
{
    public const int ExtendedDataUpdate = 1;
    public const int UserAdded = 2;
    public const int UserUpdated = 5;
    public const int UserAuthenticated = 8;
}
