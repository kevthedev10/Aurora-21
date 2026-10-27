using System.Globalization;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// Environment switches for behaviour that is still being verified against the live game, read
/// once at start-up. Each defaults to the setting that is known to work.
/// </summary>
public sealed record FeatureSwitches
{
    public const string RivalsClientDataVariable = "ULTIMATETEAM21_RIVALS_CLIENT_DATA";
    public const string MatchmakingFinishedVariable = "ULTIMATETEAM21_MM_FINISHED";
    public const string JoinerCommand22Variable = "ULTIMATETEAM21_JOINER_CMD22";
    public const string InitiateConnectionsAfterSetupVariable = "ULTIMATETEAM21_CMD22_AFTER";
    public const string InGameProbeVariable = "ULTIMATETEAM21_INGAME_PROBE_S";
    public const string PlatformHostNotificationVariable = "ULTIMATETEAM21_ENABLE_UNVERIFIED_PLATFORM_HOST_NOTIFICATION";
    public const string PlayerRemovedNotificationVariable = "ULTIMATETEAM21_PLAYER_REMOVED";
    public const string GameRemovedNotificationVariable = "ULTIMATETEAM21_GAME_REMOVED";
    public const string PregameDepartureVariable = "ULTIMATETEAM21_PREGAME_DEPARTURE";
    public const string SetupStallVariable = "ULTIMATETEAM21_SETUP_STALL_S";
    public const string ResumeGraceVariable = "ULTIMATETEAM21_RESUME_GRACE_S";
    public const string RequestHoldVariable = "ULTIMATETEAM21_REQ_HOLD_S";
    public const string CaptureAllVariable = "ULTIMATETEAM_FIRE2_CAPTURE_ALL";
    public const string MarketTargetVariable = "ULTIMATETEAM21_MARKET_TARGET";

    /// <summary>Return the stored Rivals/Champions intro markers at login (on).</summary>
    public bool RivalsClientData { get; init; } = true;

    /// <summary>Send GameManager notification 10 after a successful pairing (off: it crashes a client whose session is gone).</summary>
    public bool MatchmakingFinished { get; init; }

    /// <summary>
    /// Send the joiner JoiningPlayerInitiateConnections (4/22) instead of GameSetup (off). Live on 2026-09-15 15:50 the joiner
    /// then never ingested the game (Python server, LOCALFUT21_JOINER_CMD22 default 0).
    /// </summary>
    public bool JoinerCommand22 { get; init; }

    /// <summary>
    /// Follow every GameSetup (4/20) with InitiateConnections (4/22) for the same recipient (on). 20 alone builds the lobby
    /// but the joiner never dials its peer (no c4/k67 edge report, live 2026-09-15 18:2x); production has sent 20 then 22
    /// to each player since (Python server, LOCALFUT21_CMD22_AFTER default 1).
    /// </summary>
    public bool InitiateConnectionsAfterSetup { get; init; } = true;

    /// <summary>Seconds after PRE_GAME to push IN_GAME as a diagnostic probe; 0 disables it.</summary>
    public double InGameProbeSeconds { get; init; }

    /// <summary>
    /// Send PlatformHostInitialized (4/71, with PHID) once the host's session update and both endpoints are in (on). The
    /// production Python server has sent it since 2026-09-11 (LOCALFUT21_ENABLE_UNVERIFIED_PLATFORM_HOST_NOTIFICATION=1),
    /// including in every verified full online match; set the variable to 0 to stop it.
    /// </summary>
    public bool PlatformHostNotification { get; init; } = true;

    /// <summary>
    /// Tell the players still in a game through PlayerRemoved (4/40) when one leaves (on): a DNF first end-of-match report,
    /// a leave request, a new or cancelled search, or a lost connection. Without it they learn only through their own 15-35 s
    /// mesh-loss timeout — the loading-screen hang of 2026-10-03 game 272737575 and the PRE_GAME waits of 2026-10-04.
    /// </summary>
    public bool PlayerRemovedNotification { get; init; } = true;

    /// <summary>
    /// After that PlayerRemoved, also send GameRemoved (4/16) when the departure ended the game before IN_GAME (on), unless
    /// the pregame-departure mode is silent. Live from 2026-10-05 20:50 UTC it did not stop the next-setup bail below.
    /// </summary>
    public bool GameRemovedNotification { get; init; } = true;

    /// <summary>
    /// The start-up pregame-departure mode (announce, silent or self; default silent), the panel changes it live. A client told
    /// in the kit screen that its opponent left bails out of its own next Rivals setup 1-5 s in, which leaves its new opponent
    /// behind in turn (live 2026-10-05: 239 of 281 such players in the same session, 0 of 15 after a relogin).
    /// </summary>
    public string? PregameDepartureMode { get; init; }

    /// <summary>
    /// Seconds after GameSetup before a game whose mesh never completed, although one player is waiting on it, is ended and
    /// each player told it was removed (20; 0 disables it). One client that never reports after GameSetup leaves the other on
    /// "Initializing..." until someone quits: 14 of 1,342 setups 2026-10-04 to 10-06 hung 30-135 s, while every game that went on
    /// to play completed its mesh within 7.6 s (p99 1.3 s).
    /// </summary>
    public double SetupStallSeconds { get; init; } = DefaultSetupStallSeconds;

    public const double DefaultSetupStallSeconds = 20;

    /// <summary>
    /// Seconds a player's IN_GAME match and Blaze session key outlive their closed Blaze connection, so FIFA's resume on a new
    /// connection can still take the match over (5; 0 disables it, at most 10). Without it the close ended the game and the
    /// relay within 2 ms, and a resume 0.6 s later was refused (live 2026-10-07 01:27, game 286857202). Kept short: a player
    /// who really quit holds their opponent only this long.
    /// </summary>
    public double ResumeGraceSeconds { get; init; } = DefaultResumeGraceSeconds;

    public const double DefaultResumeGraceSeconds = 5;
    public const double MaxResumeGraceSeconds = 10;

    /// <summary>
    /// Seconds after the host's GameSetup before a player's SetPlayerAttributes change (OSDK REQ=1/REQ=2) is relayed to the
    /// other players (1; 0 relays at once, at most 3). The sender still hears its own change at once. The host answers
    /// RequestSessionData only once REQ=1 reaches it, and a REQ=1 that arrives before its kit screen listens is missed for
    /// good: of 465 setups on 2026-10-07, 36% of those whose REQ=1 came 0.6 s after GameSetup stuck on "Initializing...",
    /// against 2% from 0.9 s on.
    /// </summary>
    public double RequestHoldSeconds { get; init; } = DefaultRequestHoldSeconds;

    public const double DefaultRequestHoldSeconds = 1;
    public const double MaxRequestHoldSeconds = 3;

    /// <summary>Capture every Blaze frame instead of only matchmaking evidence (off).</summary>
    public bool CaptureAllFrames { get; init; }

    /// <summary>The number of active AI listings the shared market keeps, when set.</summary>
    public int? MarketTarget { get; init; }

    public static FeatureSwitches FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        bool Flag(string name, bool fallback) => read(name) is { } value ? value is not ("0" or "" or "false") : fallback;
        return new FeatureSwitches
        {
            RivalsClientData = Flag(RivalsClientDataVariable, true),
            MatchmakingFinished = Flag(MatchmakingFinishedVariable, false),
            JoinerCommand22 = Flag(JoinerCommand22Variable, false),
            InitiateConnectionsAfterSetup = Flag(InitiateConnectionsAfterSetupVariable, true),
            InGameProbeSeconds = double.TryParse(read(InGameProbeVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? Math.Max(0, seconds) : 0,
            PlatformHostNotification = Flag(PlatformHostNotificationVariable, true),
            PlayerRemovedNotification = Flag(PlayerRemovedNotificationVariable, true),
            GameRemovedNotification = Flag(GameRemovedNotificationVariable, true),
            PregameDepartureMode = read(PregameDepartureVariable),
            SetupStallSeconds = double.TryParse(read(SetupStallVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out var stall) ? Math.Max(0, stall) : DefaultSetupStallSeconds,
            ResumeGraceSeconds = double.TryParse(read(ResumeGraceVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out var grace) ? Math.Clamp(grace, 0, MaxResumeGraceSeconds) : DefaultResumeGraceSeconds,
            RequestHoldSeconds = double.TryParse(read(RequestHoldVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out var hold) ? Math.Clamp(hold, 0, MaxRequestHoldSeconds) : DefaultRequestHoldSeconds,
            CaptureAllFrames = read(CaptureAllVariable) == "1",
            MarketTarget = int.TryParse(read(MarketTargetVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var target) ? target : null,
        };
    }
}
