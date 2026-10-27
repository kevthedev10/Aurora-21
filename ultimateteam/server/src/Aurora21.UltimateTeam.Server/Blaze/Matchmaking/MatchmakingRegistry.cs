using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Server.Relay;

namespace Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

/// <summary>One queued StartMatchmakingScenario search.</summary>
public sealed record MatchmakingEntry(long SessionId, string PlayerId, string Mode, byte[] Payload, long CreatedTicks, BlazeClient? Client);

/// <summary>
/// A player who left a game (their own leave request, a new search, a cancelled search or a lost Blaze connection), why
/// (a <see cref="PlayerRemovedReason"/>), the players still in the game who must be told, and whether the game ended with it.
/// </summary>
public sealed record GameDeparture(RivalsMatch Match, MatchmakingEntry Leaver, long Reason, IReadOnlyList<MatchmakingEntry> Recipients, bool GameRemoved);

/// <summary>A game ended because its setup stalled: its players, and who had initialized and linked, for the log.</summary>
public sealed record StalledSetup(IReadOnlyList<MatchmakingEntry> Players, string Initialized, string Edges);

/// <summary>Read-only operator view captured under the registry lock.</summary>
public sealed record MatchmakingLobbyPlayer(string PlayerId, string State, string Mode, long? SessionId, long? GameId, long? GameState, long? QueuedForMs);
public sealed record MatchmakingLobbySnapshot(bool Accepting, IReadOnlyList<MatchmakingLobbyPlayer> Players);

/// <summary>One paired game for the public active-matches list: its players (host first), how long ago it was paired and, once kicked off, how long it has been IN_GAME.</summary>
public sealed record ActiveMatch(long GameId, string Mode, int? HouseRuleType, long State, IReadOnlyList<ActiveMatchPlayer> Players, long PairedForMs, long? InGameForMs);
public sealed record ActiveMatchPlayer(string PlayerId, bool Left);

/// <summary>FIFA 21 Blaze game states.</summary>
public static class GameState
{
    public const long Initializing = 1;
    public const long ConnectionVerification = 3;
    public const long PostGame = 4;
    public const long PreGame = 130;
    public const long InGame = 131;
}

/// <summary>
/// A paired two-player game and the lifecycle facts gathered while both clients set it up. The
/// first player is the Blaze host. Every set and flag only ever grows or latches.
/// </summary>
public sealed class RivalsMatch
{
    internal RivalsMatch(long gameId, long reportId, string mode, IReadOnlyList<MatchmakingEntry> players, BlazeClient trigger, RelayAllocation? relay)
    {
        GameId = gameId;
        ReportId = reportId;
        Mode = mode;
        Players = players;
        TriggerClient = trigger;
        Relay = relay;
    }

    public long GameId { get; }
    public long ReportId { get; }
    public string Mode { get; }

    /// <summary>An online Friendlies game's house-rule game type (<see cref="HouseRules"/>); null for every other game.</summary>
    public int? HouseRuleType { get; internal init; }
    public IReadOnlyList<MatchmakingEntry> Players { get; internal set; }
    public MatchmakingEntry Host => Players[0];
    internal BlazeClient TriggerClient { get; set; }
    public RelayAllocation? Relay { get; }

    internal HashSet<string> ConnectedPlayers { get; } = new(StringComparer.Ordinal);

    /// <summary>Players whose own mesh endpoint is initialized (a self-report, not a peer link).</summary>
    internal HashSet<string> MeshInitializedPlayers { get; } = new(StringComparer.Ordinal);

    internal HashSet<(string Source, string Target)> ConnectedPeerEdges { get; } = new();

    /// <summary>Latched once every directed cross-player CONNECTED edge has been observed.</summary>
    public bool MeshComplete { get; internal set; }

    internal HashSet<string> JoinNotifiedPlayers { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> PlayerStateNotifiedPlayers { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> SessionUpdatedPlayers { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> ConnectionValidationNotifiedPlayers { get; } = new(StringComparer.Ordinal);
    internal byte[]? HostSessionUpdatePayload { get; set; }
    internal bool SessionUpdateNotified { get; set; }
    internal bool PendingPregame { get; set; }
    internal bool PregameNotified { get; set; }
    internal bool PlatformHostInitializedNotified { get; set; }
    internal bool JoinNotificationsStarted { get; set; }
    private long _state = GameState.ConnectionVerification;

    public long State
    {
        get => _state;
        internal set
        {
            if (value == GameState.InGame) InGameTicks ??= Environment.TickCount64;
            _state = value;
        }
    }

    /// <summary><see cref="Environment.TickCount64"/> when the game was paired.</summary>
    internal long PairedTicks { get; } = Environment.TickCount64;

    /// <summary><see cref="Environment.TickCount64"/> when the game first reached IN_GAME (kick-off); null before.</summary>
    internal long? InGameTicks { get; private set; }

    /// <summary>GAME.GSET after the arbitrator's SetGameSettings (LockGame); null until one arrives.</summary>
    internal long? GameSettings { get; set; }

    /// <summary>Game attributes the clients stored through SetGameAttributes (the Ready vote's ARB_3 step).</summary>
    internal Dictionary<string, string> GameAttributes { get; } = new(StringComparer.Ordinal);

    private readonly object _gameSpeedGate = new();
    private bool _gameSpeedChosen;
    private long? _gameSpeed;

    /// <summary>
    /// The fifaGameSpeed every GameSetup of this game carries: the setting's value the first time one is built. Both players
    /// must play the same speed (the lockstep simulation desyncs otherwise), so a panel change only reaches the next game.
    /// </summary>
    internal long? GameSpeed(OnlineGameSpeed setting)
    {
        lock (_gameSpeedGate)
        {
            if (!_gameSpeedChosen)
            {
                _gameSpeed = setting.Value;
                _gameSpeedChosen = true;
            }
            return _gameSpeed;
        }
    }

    /// <summary>Per player id: player attributes stored through SetPlayerAttributes (OSDK REQ=1 / REQ=2).</summary>
    internal Dictionary<string, Dictionary<string, string>> PlayerAttributes { get; } = new(StringComparer.Ordinal);

    /// <summary><see cref="Environment.TickCount64"/> when the host's GameSetup went out; null until it has.</summary>
    internal long? SetupDeliveredTicks { get; set; }

    private readonly object _attributeRelayGate = new();
    private Task _attributeRelays = Task.CompletedTask;

    /// <summary>
    /// Runs <paramref name="relay"/> after every PlayerAttribChange relay queued before it, so the other players see a
    /// player's REQ=1 before its REQ=2 however long the first is held.
    /// </summary>
    internal Task ChainAttributeRelay(Func<Task> relay)
    {
        lock (_attributeRelayGate)
        {
            _attributeRelays = _attributeRelays.ContinueWith(_ => relay(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _attributeRelays;
        }
    }

    /// <summary>Players whose ONLINE end-of-match report (GameReporting 28/1) has arrived.</summary>
    internal HashSet<string> ReportedPlayers { get; } = new(StringComparer.Ordinal);

    /// <summary>Players who left; each departure is announced to the others once.</summary>
    internal HashSet<string> DepartedPlayers { get; } = new(StringComparer.Ordinal);

    internal bool IsMember(string playerId) => Players.Any(player => player.PlayerId == playerId);

    internal IReadOnlySet<string> RosterIds => Players.Select(player => player.PlayerId).ToHashSet(StringComparer.Ordinal);

    public MatchmakingEntry? Entry(string playerId) => Players.FirstOrDefault(player => player.PlayerId == playerId);
}

/// <summary>
/// The process-wide two-player queue and the games it pairs. FUT_Rivals scenarios pair (the mode with live
/// evidence), and FUT_Draft ones for Online Draft (Python server 51a1670: the same StartMatchmakingScenario with its own
/// criteria; deployed, not yet played live), and FUT_Champions ones for the Weekend League (Python server 56145ed: GAME_MODE
/// 79 with FUT_CHAMPIONS_FORM; not yet played live), and FUT_HouseRules ones for online Friendlies (<see cref="HouseRules"/>: only
/// two searches that share a house-rule game type, launcher friends first). A search pairs only with one of its own mode. Each
/// claim method latches its notification so repeated
/// evaluation after every client report is safe.
/// </summary>
public sealed class MatchmakingRegistry
{
    internal const string RivalsMode = "fut_rivals";
    private static readonly HashSet<string> PairedModes = new(StringComparer.Ordinal) { RivalsMode, "fut_draft", "fut_champions", "fut_houserules", "fut_houserules_ltm" };
    private static readonly TimeSpan StaleSearch = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an online Friendlies search whose player has a launcher friend online (and not in a game) is kept from
    /// strangers, so two friends who press Play a few seconds apart meet each other. A stranger arriving later pairs as usual.
    /// </summary>
    internal static readonly TimeSpan FriendHold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(10);

    private static readonly IReadOnlyDictionary<long, long[]> AllowedTransitions = new Dictionary<long, long[]>
    {
        [GameState.Initializing] = new[] { GameState.ConnectionVerification },
        [GameState.ConnectionVerification] = new[] { GameState.PreGame },
        [GameState.PreGame] = new[] { GameState.InGame },
        [GameState.InGame] = new[] { GameState.PostGame },
        [GameState.PostGame] = Array.Empty<long>(),
    };

    private readonly object _gate = new();
    private readonly GameplayRelay? _relay;
    private readonly Func<string, string, bool> _compatible;
    private readonly Func<bool> _pairing;
    private readonly Func<string, string, bool> _friends;
    private readonly Func<string, IReadOnlyCollection<string>> _friendsOf;
    private readonly Func<string, bool> _online;
    private readonly Func<string, long?> _barredUntil;
    private readonly TimeSpan _friendHold;
    private readonly ILogger _log;
    private readonly Dictionary<long, MatchmakingEntry> _sessions = new();
    private readonly Dictionary<long, RivalsMatch> _games = new();
    private readonly Dictionary<string, long> _gamesByPlayer = new(StringComparer.Ordinal);
    private readonly List<RivalsMatch> _ready = new();

    /// <summary>Departures recorded under the lock, announced after it is released.</summary>
    private readonly List<GameDeparture> _departures = new();

    /// <summary>
    /// Who left each game first while it was being played, with its members, kept <see cref="FirstLeaverMemory"/> after the
    /// leave (the game itself is gone once everyone has left, before the match ends arrive): <see cref="OpponentLeftGameFirst"/>.
    /// </summary>
    private readonly Dictionary<long, (string Leaver, string[] Players, long Ticks)> _firstInGameLeavers = new();
    private static readonly TimeSpan FirstLeaverMemory = TimeSpan.FromMinutes(10);
    private long _nextSessionId;
    private long _nextGameId;
    private bool _accepting = true;

    /// <param name="compatible">
    /// Whether two players may be paired: their Windows maths must agree (<c>MathGroups</c>; Python server 43d3bac). Null
    /// pairs anyone.
    /// </param>
    /// <param name="pairing">
    /// Whether the operator allows new matches (<c>MatchGate.Open</c>). While it is false a search is answered
    /// as usual but neither queued nor paired, so the player keeps searching; a match already paired continues. Null allows.
    /// </param>
    /// <param name="friends">
    /// Whether two players are mutual launcher friends (<c>LauncherFriends</c>); used by online Friendlies only, never by a
    /// ranked mode. Null: nobody is.
    /// </param>
    /// <param name="friendsOf">A player's launcher friends, for the friend hold. Null: none.</param>
    /// <param name="online">Whether a player is connected (PlayerPresence), for the friend hold. Null: nobody.</param>
    /// <param name="friendHold">The friend hold; null is <see cref="FriendHold"/>.</param>
    /// <param name="barredUntil">
    /// When a player's online-search cooldown ends (unix seconds; <c>LeaverCooldown</c>), null when they may search. A barred
    /// search is answered like a paused one: never queued nor paired. Null bars nobody.
    /// </param>
    public MatchmakingRegistry(GameplayRelay? relay, ILogger log, TimeProvider? time = null, Func<string, string, bool>? compatible = null,
        Func<bool>? pairing = null, Func<string, string, bool>? friends = null, Func<string, IReadOnlyCollection<string>>? friendsOf = null,
        Func<string, bool>? online = null, TimeSpan? friendHold = null, Func<string, long?>? barredUntil = null)
    {
        _relay = relay;
        _compatible = compatible ?? ((_, _) => true);
        _pairing = pairing ?? (() => true);
        _friends = friends ?? ((_, _) => false);
        _friendsOf = friendsOf ?? (_ => Array.Empty<string>());
        _online = online ?? (_ => false);
        _barredUntil = barredUntil ?? (_ => null);
        _friendHold = friendHold ?? FriendHold;
        _log = log;
        var now = (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        _nextSessionId = Math.Max(1, now & 0x7FFFFFFF);
        _nextGameId = Math.Max(1, (now >> 4) & 0x7FFFFFFF);
    }

    internal static string ModeKey(string mode) => mode.Trim().ToLowerInvariant();

    /// <summary>
    /// Called outside the registry lock for every player who leaves a game that others are still in. Without the announcement
    /// (NotifyPlayerRemoved) the remaining client keeps waiting in PRE_GAME until its own peer link times out.
    /// </summary>
    public Action<GameDeparture>? PlayerDeparted { get; set; }

    /// <summary>Whether new searches are still queued and paired (false once the server is shutting down).</summary>
    public bool Accepting
    {
        get
        {
            lock (_gate)
            {
                return _accepting;
            }
        }
    }

    /// <summary>Shutdown: stop queueing and pairing new searches. Games already paired continue until their clients leave.</summary>
    public void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
            _sessions.Clear();
        }
    }

    /// <summary>
    /// Queues a search (replacing the player's previous search and game) and pairs it when possible.
    /// Returns 0 without queueing once the server has stopped accepting searches.
    /// </summary>
    public long Start(string playerId, string mode, byte[] payload, BlazeClient? client)
    {
        try
        {
            return StartLocked(playerId, mode, payload, client);
        }
        finally
        {
            FlushDepartures();
        }
    }

    private long StartLocked(string playerId, string mode, byte[] payload, BlazeClient? client)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (!_accepting)
            {
                _log.LogWarning("MATCHMAKING REFUSED player={Player}: the server is shutting down", playerId);
                return 0;
            }
            if (!_pairing())
            {
                _log.LogWarning("MATCHMAKING PAUSED player={Player} mode={Mode}: new matches are paused in the panel", playerId, mode);
                return 0;
            }
            if (_barredUntil(playerId) is { } until)
            {
                _log.LogWarning("MATCHMAKING BARRED player={Player} mode={Mode} until={Until}: leaver cooldown", playerId, mode, until);
                return 0;
            }
            foreach (var stale in _sessions.Values.Where(entry => now - entry.CreatedTicks >= StaleSearch.TotalMilliseconds).ToList())
            {
                _sessions.Remove(stale.SessionId);
            }
            foreach (var own in _sessions.Values.Where(entry => entry.PlayerId == playerId).ToList())
            {
                _sessions.Remove(own.SessionId);
            }
            if (_gamesByPlayer.TryGetValue(playerId, out var previousGame))
            {
                DepartLocked(previousGame, playerId, PlayerRemovedReason.PlayerLeftSwitchedGameSession, removed: true);
                RemoveGameLocked(previousGame);
            }
            var sessionId = _nextSessionId;
            _nextSessionId = sessionId >= 0x7FFFFFFF ? 1 : sessionId + 1;
            _sessions[sessionId] = new MatchmakingEntry(sessionId, playerId, mode, payload, now, client);
            PairLocked(sessionId);
            return sessionId;
        }
    }

    private void PairLocked(long newestId)
    {
        if (!_sessions.TryGetValue(newestId, out var newest) || newest.Client is null || !PairedModes.Contains(ModeKey(newest.Mode)))
        {
            return;
        }
        // Longest-waiting first: a Dictionary's enumeration order is not insertion order once entries have been removed.
        var waiting = _sessions.Values.Where(entry => entry.SessionId != newestId && entry.PlayerId != newest.PlayerId
                                                      && ModeKey(entry.Mode) == ModeKey(newest.Mode) && entry.Client is not null)
            .OrderBy(entry => entry.CreatedTicks).ThenBy(entry => entry.SessionId).ToList();
        int? houseRuleType = null;
        MatchmakingEntry? candidate;
        if (HouseRules.IsHouseRulesMode(newest.Mode))
        {
            (candidate, houseRuleType) = FriendlyCandidateLocked(newest, waiting);
            if (candidate is null)
            {
                return;
            }
        }
        else
        {
            candidate = waiting.FirstOrDefault(entry => _compatible(entry.PlayerId, newest.PlayerId));
        }
        if (candidate is null)
        {
            if (waiting.Count > 0)
            {
                _log.LogInformation("MATCHMAKING MATH GROUP player={Player} kept apart from {Waiting}", newest.PlayerId, string.Join(",", waiting.Select(entry => entry.PlayerId)));
            }
            return;
        }
        var gameId = _nextGameId;
        RelayAllocation? relay = null;
        if (_relay is not null)
        {
            try
            {
                relay = _relay.Allocate(gameId, new[]
                {
                    new RelayPlayer(candidate.PlayerId, candidate.Client!.PeerAddress, candidate.Client.ServerAddress),
                    new RelayPlayer(newest.PlayerId, newest.Client.PeerAddress, newest.Client.ServerAddress),
                });
            }
            catch (GameplayRelayCapacityException error)
            {
                _log.LogError("MATCHMAKING RELAY CAPACITY gameId={GameId} players={First}/{Second}: {Error}", gameId, candidate.PlayerId, newest.PlayerId, error.Message);
                return;
            }
            catch (GameplayRelayException error)
            {
                _log.LogError(error, "MATCHMAKING RELAY ALLOCATION FAILED gameId={GameId}", gameId);
                return;
            }
        }
        _sessions.Remove(candidate.SessionId);
        _sessions.Remove(newestId);
        _nextGameId = gameId >= 0x7FFFFFFF ? 1 : gameId + 1;
        var match = new RivalsMatch(gameId, ReportIdOf(gameId, newestId), newest.Mode, new[] { candidate, newest }, newest.Client, relay)
        {
            HouseRuleType = houseRuleType,
        };
        if (HouseRules.IsHouseRulesMode(newest.Mode))
        {
            _log.LogInformation("MATCHMAKING FRIENDLIES PAIRED gameId={GameId} players={First}/{Second} houseRuleType={Type} friends={Friends}",
                gameId, candidate.PlayerId, newest.PlayerId, houseRuleType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
                _friends(candidate.PlayerId, newest.PlayerId));
        }
        _games[gameId] = match;
        foreach (var player in match.Players)
        {
            _gamesByPlayer[player.PlayerId] = gameId;
        }
        _ready.Add(match);
    }

    /// <summary>
    /// The waiting online Friendlies search the newest one pairs with: only searches sharing a house-rule game type (and the
    /// maths group); a mutual launcher friend first (longest waiting), otherwise the longest-waiting stranger, unless either
    /// side is still within its friend hold (<see cref="FriendHold"/>).
    /// </summary>
    private (MatchmakingEntry? Candidate, int? GameType) FriendlyCandidateLocked(MatchmakingEntry newest, IReadOnlyList<MatchmakingEntry> waiting)
    {
        var mine = HouseRules.Preferences(newest.Payload);
        var now = Environment.TickCount64;
        var agreeing = new List<(MatchmakingEntry Entry, int? Type)>();
        foreach (var entry in waiting.Where(entry => _compatible(entry.PlayerId, newest.PlayerId)))
        {
            var (compatible, type) = HouseRules.Agree(HouseRules.Preferences(entry.Payload), mine);
            if (compatible)
            {
                agreeing.Add((entry, type));
            }
        }
        foreach (var (entry, type) in agreeing)
        {
            if (_friends(entry.PlayerId, newest.PlayerId))
            {
                return (entry, type);
            }
        }
        if (agreeing.Count == 0)
        {
            if (waiting.Count > 0)
            {
                _log.LogInformation("MATCHMAKING FRIENDLIES no shared house rule player={Player} types={Types} waiting={Waiting}",
                    newest.PlayerId, string.Join(",", mine), waiting.Count);
            }
            return (null, null);
        }
        if (HeldForFriendLocked(newest, now))
        {
            _log.LogInformation("MATCHMAKING FRIENDLIES HOLD player={Player}: a launcher friend is online", newest.PlayerId);
            return (null, null);
        }
        foreach (var (entry, type) in agreeing)
        {
            if (!HeldForFriendLocked(entry, now))
            {
                return (entry, type);
            }
        }
        return (null, null);
    }

    /// <summary>Whether a search is still kept for a launcher friend: young enough, with a friend online and not in a game.</summary>
    private bool HeldForFriendLocked(MatchmakingEntry entry, long now) =>
        now - entry.CreatedTicks < _friendHold.TotalMilliseconds
        && _friendsOf(entry.PlayerId).Any(friend => _friends(entry.PlayerId, friend) && _online(friend) && !_gamesByPlayer.ContainsKey(friend));

    /// <summary>Records a post-GameSetup acknowledgement: (game, first for this player, every player acknowledged).</summary>
    public (RivalsMatch? Match, bool First, bool Complete) AcknowledgeGameStep(string playerId, long gameId, bool connected)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match) || !match.IsMember(playerId))
            {
                return (null, false, false);
            }
            var acknowledged = connected ? match.ConnectedPlayers : match.SessionUpdatedPlayers;
            var first = acknowledged.Add(playerId);
            return (match, first, acknowledged.Count == match.Players.Count);
        }
    }

    /// <summary>
    /// Classifies one UpdateMeshConnection report. A self-targeted report initializes the reporter's
    /// endpoint; a cross-player report is a peer link only with the CONNECTED status (4). A missing or
    /// foreign target is never reinterpreted as a self report. The last value is true only for the
    /// report that first completes every directed link.
    /// </summary>
    public (RivalsMatch? Match, MatchmakingEntry? Target, bool First, bool CompletedNow) AcknowledgeMeshTarget(string playerId, long gameId, long? targetPersonaId, long? status)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match) || !match.IsMember(playerId))
            {
                return (null, null, false, false);
            }
            var target = match.Players.FirstOrDefault(player => player.Client!.PersonaId == (targetPersonaId ?? 0));
            if (target is null)
            {
                return (match, null, false, false);
            }
            bool first;
            if (target.PlayerId == playerId)
            {
                first = match.MeshInitializedPlayers.Add(playerId);
            }
            else if ((status ?? 0) == MeshConnectedStatus)
            {
                first = match.ConnectedPeerEdges.Add((playerId, target.PlayerId));
            }
            else
            {
                first = false;
            }
            var roster = match.RosterIds;
            var complete = roster.IsSubsetOf(match.MeshInitializedPlayers)
                           && roster.SelectMany(source => roster.Where(destination => destination != source).Select(destination => (source, destination)))
                               .All(match.ConnectedPeerEdges.Contains);
            var wasComplete = match.MeshComplete;
            if (complete)
            {
                match.MeshComplete = true;
                match.ConnectedPlayers.UnionWith(roster);
            }
            return (match, target, first, complete && !wasComplete);
        }
    }

    /// <summary>The CONNECTED status of a cross-player mesh report.</summary>
    public const long MeshConnectedStatus = 4;

    /// <summary>Records the pre-identity (TCG=0, FLGS=0) endpoint callback: setup readiness, never a peer link.</summary>
    public (RivalsMatch? Match, bool First) AcknowledgeEndpointInitialization(string playerId, long gameId)
    {
        lock (_gate)
        {
            return _games.TryGetValue(gameId, out var match) && match.IsMember(playerId)
                ? (match, match.MeshInitializedPlayers.Add(playerId))
                : (null, false);
        }
    }

    /// <summary>Claims roster activation once the directed mesh is complete.</summary>
    public (RivalsMatch? Match, bool Claimed) ClaimJoinNotifications(long gameId) => Claim(gameId, match =>
    {
        if (!match.MeshComplete || match.JoinNotificationsStarted)
        {
            return false;
        }
        match.JoinNotificationsStarted = true;
        return true;
    });

    /// <summary>Records that one player's active state reached every peer: (game, all players covered).</summary>
    public (RivalsMatch? Match, bool Complete) MarkPlayerStateNotificationsDelivered(long gameId, string targetPlayerId) =>
        MarkDelivered(gameId, targetPlayerId, match => match.PlayerStateNotifiedPlayers);

    public (RivalsMatch? Match, bool Complete) MarkJoinNotificationsDelivered(long gameId, string targetPlayerId) =>
        MarkDelivered(gameId, targetPlayerId, match => match.JoinNotifiedPlayers);

    private (RivalsMatch? Match, bool Complete) MarkDelivered(long gameId, string targetPlayerId, Func<RivalsMatch, HashSet<string>> set)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match))
            {
                return (null, false);
            }
            if (!match.IsMember(targetPlayerId))
            {
                return (match, false);
            }
            var notified = set(match);
            notified.Add(targetPlayerId);
            return (match, notified.Count == match.Players.Count);
        }
    }

    public Task<bool> WaitForMeshInitializationAsync(long gameId, TimeSpan timeout) =>
        WaitAsync(gameId, timeout, match => match.RosterIds.IsSubsetOf(match.MeshInitializedPlayers));

    public Task<bool> WaitForMeshCompletionAsync(long gameId, TimeSpan timeout) =>
        WaitAsync(gameId, timeout, match => match.MeshComplete);

    public Task<bool> WaitForRosterActivationAsync(long gameId, TimeSpan timeout) =>
        WaitAsync(gameId, timeout, match => match.PlayerStateNotifiedPlayers.Count == match.Players.Count);

    private async Task<bool> WaitAsync(long gameId, TimeSpan timeout, Func<RivalsMatch, bool> ready)
    {
        var deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);
        while (true)
        {
            lock (_gate)
            {
                if (!_games.TryGetValue(gameId, out var match))
                {
                    return false;
                }
                if (ready(match))
                {
                    return true;
                }
            }
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }
            await Task.Delay(WaitPollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The host's AdvanceGameState. PRE_GAME is only recorded as pending (published once the mesh and
    /// roster are ready); other states follow the allowed transitions. Returns (game, state changed).
    /// </summary>
    public (RivalsMatch? Match, bool Changed) AdvanceGameState(string playerId, long gameId, long state)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match) || !match.IsMember(playerId))
            {
                return (null, false);
            }
            if (match.Host.PlayerId != playerId)
            {
                _log.LogWarning("MATCHMAKING ADVANCE REJECTED gameId={GameId} requester={Player} host={Host} state={State}", gameId, playerId, match.Host.PlayerId, state);
                return (match, false);
            }
            if (state == GameState.PreGame)
            {
                var changed = !match.PendingPregame && !match.PregameNotified;
                match.PendingPregame = true;
                return (match, changed);
            }
            if (state != match.State && !(AllowedTransitions.TryGetValue(match.State, out var next) && next.Contains(state)))
            {
                _log.LogWarning("MATCHMAKING ADVANCE INVALID gameId={GameId} host={Host} current={Current} requested={State}", gameId, match.Host.PlayerId, match.State, state);
                return (match, false);
            }
            var stateChanged = match.State != state;
            match.State = state;
            return (match, stateChanged);
        }
    }

    /// <summary>
    /// Claims PRE_GAME once the host's session update, every connection validation, player state and
    /// join notification and the complete mesh are in, and the host asked for it.
    /// </summary>
    public (RivalsMatch? Match, bool Claimed) ClaimPregameIfReady(long gameId) => Claim(gameId, match =>
    {
        if (match.PregameNotified
            || !match.MeshComplete
            || !match.SessionUpdatedPlayers.Contains(match.Host.PlayerId)
            || !match.RosterIds.IsSubsetOf(match.ConnectionValidationNotifiedPlayers)
            || match.PlayerStateNotifiedPlayers.Count != match.Players.Count
            || match.JoinNotifiedPlayers.Count != match.Players.Count
            || !match.PendingPregame)
        {
            return false;
        }
        match.PregameNotified = true;
        match.State = GameState.PreGame;
        return true;
    });

    /// <summary>Claims one recipient's setup completion, once, after its endpoint is initialized.</summary>
    public (RivalsMatch? Match, bool Claimed) ClaimConnectionValidation(long gameId, string playerId) => Claim(gameId, match =>
        match.IsMember(playerId)
        && !match.ConnectionValidationNotifiedPlayers.Contains(playerId)
        && match.MeshInitializedPlayers.Contains(playerId)
        && match.ConnectionValidationNotifiedPlayers.Add(playerId));

    /// <summary>Latches PlatformHostInitialized after the host's session update and both endpoints.</summary>
    public (RivalsMatch? Match, bool Claimed) ClaimPlatformHostInitialized(long gameId) => Claim(gameId, match =>
    {
        if (match.PlatformHostInitializedNotified || !match.SessionUpdatedPlayers.Contains(match.Host.PlayerId)
            || !match.RosterIds.IsSubsetOf(match.MeshInitializedPlayers))
        {
            return false;
        }
        match.PlatformHostInitializedNotified = true;
        return true;
    });

    /// <summary>Keeps the host's first UpdateGameSession payload for GameSessionUpdated.</summary>
    public RivalsMatch? StoreHostSessionUpdatePayload(string playerId, long gameId, byte[] payload)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match))
            {
                return null;
            }
            if (match.Host.PlayerId == playerId)
            {
                match.HostSessionUpdatePayload ??= payload;
            }
            return match;
        }
    }

    /// <summary>
    /// Releases GameSessionUpdated once both endpoints exist and the host's payload is known. Gating it
    /// on the complete mesh would deadlock: the guest may need it before promoting its endpoint.
    /// </summary>
    public (RivalsMatch? Match, byte[]? Payload, bool Claimed) ClaimSessionUpdateNotification(long gameId)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match) || match.SessionUpdateNotified)
            {
                return (match, null, false);
            }
            if (!match.RosterIds.IsSubsetOf(match.MeshInitializedPlayers) || match.HostSessionUpdatePayload is null)
            {
                return (match, null, false);
            }
            match.SessionUpdateNotified = true;
            return (match, match.HostSessionUpdatePayload, true);
        }
    }

    /// <summary>The newly paired game whose second StartMatchmaking came from this connection, taken once.</summary>
    public RivalsMatch? TakeReady(string playerId, BlazeClient client)
    {
        lock (_gate)
        {
            var index = _ready.FindIndex(match => match.TriggerClient == client && match.IsMember(playerId));
            if (index < 0)
            {
                return null;
            }
            var match = _ready[index];
            _ready.RemoveAt(index);
            return match;
        }
    }

    /// <summary>Removes the player's game and their search (the given session, or all of theirs: the client never learns its id).</summary>
    public bool Cancel(string playerId, long sessionId)
    {
        try
        {
            return CancelLocked(playerId, sessionId);
        }
        finally
        {
            FlushDepartures();
        }
    }

    private bool CancelLocked(string playerId, long sessionId)
    {
        lock (_gate)
        {
            var inGame = _gamesByPlayer.TryGetValue(playerId, out var gameId);
            if (inGame)
            {
                DepartLocked(gameId, playerId, PlayerRemovedReason.PlayerLeftCancelledMatchmaking, removed: true);
            }
            var removedGame = inGame && RemoveGameLocked(gameId);
            if (_sessions.TryGetValue(sessionId, out var entry) && entry.PlayerId == playerId)
            {
                _sessions.Remove(sessionId);
                return true;
            }
            var owned = _sessions.Values.Where(search => search.PlayerId == playerId).Select(search => search.SessionId).ToList();
            owned.ForEach(id => _sessions.Remove(id));
            return owned.Count > 0 || removedGame;
        }
    }

    /// <summary>
    /// Moves everything <paramref name="from"/> holds (its queued search, its seat in a game, a game it triggered) to
    /// <paramref name="to"/>, the same player's resumed Blaze connection, so later notifications reach the new connection and
    /// the old one's eventual close no longer ends the game. Returns how many searches and games moved.
    /// </summary>
    public (int Searches, int Games) Rebind(BlazeClient from, BlazeClient to)
    {
        if (from.PlayerId != to.PlayerId)
        {
            throw new ArgumentException("A Blaze connection can only be rebound to the same player's connection.", nameof(to));
        }
        lock (_gate)
        {
            var searches = 0;
            foreach (var entry in _sessions.Values.Where(entry => entry.Client == from).ToList())
            {
                _sessions[entry.SessionId] = entry with { Client = to };
                searches++;
            }
            var games = 0;
            foreach (var match in _games.Values.Concat(_ready).Distinct())
            {
                var moved = false;
                if (match.Players.Any(player => player.Client == from))
                {
                    match.Players = match.Players.Select(player => player.Client == from ? player with { Client = to } : player).ToList();
                    moved = true;
                }
                if (match.TriggerClient == from)
                {
                    match.TriggerClient = to;
                    moved = true;
                }
                if (moved && from.PeerAddress != to.PeerAddress)
                {
                    // The resumed connection is verified, so its address is the player's: let their gameplay datagrams through from it.
                    _relay?.Repoint(match.GameId, to.PlayerId, to.PeerAddress);
                }
                games += moved ? 1 : 0;
            }
            return (searches, games);
        }
    }

    /// <summary>Whether <paramref name="client"/> holds a seat it has not left in a game that is IN_GAME.</summary>
    public bool InLiveGame(BlazeClient client)
    {
        lock (_gate)
        {
            return _gamesByPlayer.TryGetValue(client.PlayerId, out var gameId) && _games.TryGetValue(gameId, out var match)
                && match.State == GameState.InGame && match.Players.Any(player => player.Client == client)
                && !match.DepartedPlayers.Contains(client.PlayerId);
        }
    }

    /// <summary>Drops a closed connection's searches and the game it was part of.</summary>
    public void Disconnect(BlazeClient client)
    {
        lock (_gate)
        {
            foreach (var entry in _sessions.Values.Where(entry => entry.PlayerId == client.PlayerId && entry.Client == client).ToList())
            {
                _sessions.Remove(entry.SessionId);
            }
            if (_gamesByPlayer.TryGetValue(client.PlayerId, out var gameId) && _games.TryGetValue(gameId, out var match)
                && match.Players.Any(player => player.Client == client))
            {
                _log.LogWarning("MATCHMAKING CLIENT DISCONNECTED gameId={GameId} player={Player} initialized={Initialized} edges={Edges} meshComplete={Mesh} sessionUpdated={Updated} pendingPregame={Pending} gameState={State}",
                    gameId, client.PlayerId, string.Join(",", match.MeshInitializedPlayers.Order()), string.Join(",", match.ConnectedPeerEdges.Select(edge => $"{edge.Source}->{edge.Target}")),
                    match.MeshComplete, string.Join(",", match.SessionUpdatedPlayers.Order()), match.PendingPregame, match.State);
                DepartLocked(gameId, client.PlayerId, PlayerRemovedReason.BlazeServerConnectionLost, removed: true);
                RemoveGameLocked(gameId);
            }
        }
        FlushDepartures();
    }

    /// <summary>
    /// The player's own leave request (LeaveGameByGroup, or RemovePlayer for itself) for <paramref name="gameId"/>, or a
    /// quit end-of-match report (<paramref name="keepGame"/>). Before IN_GAME a leave request ends the game. From IN_GAME on,
    /// and always for a quit report, the game stays for the remaining player, who still finishes and reports (the report
    /// flow, a new search or a lost connection ends it), and ends once every player has left. Returns a status for the log:
    /// ended, left, all-left, already-left or unknown-game (a game the server already ended, such as the one FIFA leaves with
    /// REAS 26 when it joins its next game).
    /// </summary>
    public string Leave(string playerId, long gameId, long reason, bool keepGame = false)
    {
        string status;
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match) || !match.IsMember(playerId))
            {
                status = "unknown-game";
            }
            else if (match.DepartedPlayers.Contains(playerId))
            {
                status = "already-left";
            }
            else if (!keepGame && match.State is not (GameState.InGame or GameState.PostGame))
            {
                DepartLocked(gameId, playerId, reason, removed: true);
                RemoveGameLocked(gameId, "player-left");
                status = "ended";
            }
            else
            {
                DepartLocked(gameId, playerId, reason, removed: false);
                if (match.Players.All(player => match.DepartedPlayers.Contains(player.PlayerId)))
                {
                    RemoveGameLocked(gameId, "all-left");
                    status = "all-left";
                }
                else
                {
                    // A new search from the leaver must not take the remaining player's game down with it.
                    if (_gamesByPlayer.TryGetValue(playerId, out var mapped) && mapped == gameId)
                    {
                        _gamesByPlayer.Remove(playerId);
                    }
                    status = "left";
                }
            }
        }
        FlushDepartures();
        return status;
    }

    /// <summary>Records a member's departure once; recipients are the members who have not left themselves.</summary>
    private void DepartLocked(long gameId, string playerId, long reason, bool removed)
    {
        if (!_games.TryGetValue(gameId, out var match) || match.Entry(playerId) is not { } leaver || !match.DepartedPlayers.Add(playerId))
        {
            return;
        }
        if (match.State == GameState.InGame && match.DepartedPlayers.Count == 1)
        {
            NoteFirstInGameLeaverLocked(match, playerId);
        }
        var recipients = match.Players
            .Where(player => player.PlayerId != playerId && !match.DepartedPlayers.Contains(player.PlayerId) && player.Client is not null)
            .ToList();
        if (recipients.Count > 0)
        {
            _departures.Add(new GameDeparture(match, leaver, reason, recipients, removed));
        }
    }

    private void FlushDepartures()
    {
        List<GameDeparture> ready;
        lock (_gate)
        {
            if (_departures.Count == 0)
            {
                return;
            }
            ready = _departures.ToList();
            _departures.Clear();
        }
        foreach (var departure in ready)
        {
            try
            {
                PlayerDeparted?.Invoke(departure);
            }
            catch (Exception error)
            {
                _log.LogError(error, "MATCHMAKING DEPARTURE CALLBACK FAILED gameId={GameId} leaver={Player}", departure.Match.GameId, departure.Leaver.PlayerId);
            }
        }
    }

    /// <summary>
    /// Merges one SetPlayerAttributes request into the target roster player (Python server _OSDK_PLAYER_ATTRS). Returns the
    /// game, a status for the log (ok, unknown-game, sender-not-in-game, unknown-player, too-many-keys) and the pairs whose
    /// value changed: only those are relayed.
    /// </summary>
    public (RivalsMatch? Match, string Status, IReadOnlyDictionary<string, string> Changed) SetPlayerAttributes(
        string playerId, long gameId, long personaId, IReadOnlyDictionary<string, string> attributes, int maxStoredKeys)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match))
            {
                return (null, "unknown-game", NoAttributes);
            }
            if (!match.IsMember(playerId))
            {
                return (match, "sender-not-in-game", NoAttributes);
            }
            if (match.Players.FirstOrDefault(player => player.Client!.PersonaId == personaId) is not { } target)
            {
                return (match, "unknown-player", NoAttributes);
            }
            if (!match.PlayerAttributes.TryGetValue(target.PlayerId, out var stored))
            {
                stored = new Dictionary<string, string>(StringComparer.Ordinal);
                match.PlayerAttributes[target.PlayerId] = stored;
            }
            return MergeAttributes(match, stored, attributes, maxStoredKeys);
        }
    }

    /// <summary>
    /// Merges one SetGameAttributes request into the game (Python server _OSDK_GAME_ATTR_RELAY); see <see cref="SetPlayerAttributes"/>.
    /// A game-speed key is dropped: the server sets the speed, and both players must play the same one.
    /// </summary>
    public (RivalsMatch? Match, string Status, IReadOnlyDictionary<string, string> Changed) SetGameAttributes(
        string playerId, long gameId, IReadOnlyDictionary<string, string> attributes, int maxStoredKeys)
    {
        if (attributes.Keys.Any(MatchmakingPayloads.IsGameSpeedKey))
        {
            attributes = attributes.Where(pair => !MatchmakingPayloads.IsGameSpeedKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match))
            {
                return (null, "unknown-game", NoAttributes);
            }
            return match.IsMember(playerId)
                ? MergeAttributes(match, match.GameAttributes, attributes, maxStoredKeys)
                : (match, "sender-not-in-game", NoAttributes);
        }
    }

    /// <summary>
    /// Applies one SetGameSettings request (Python server _OSDK_SETTINGS_RELAY): new = (old &amp; ~GSTM) | (GSET &amp; GSTM), and a
    /// mask of 0 replaces the value. Any player of the game may send it; the arbitrator's LockGame is the expected caller.
    /// </summary>
    public (RivalsMatch? Match, string Status, long Old, long New) SetGameSettings(string playerId, long gameId, long settings, long mask, long initialSettings)
    {
        const long full = 0xFFFFFFFF;
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match))
            {
                return (null, "unknown-game", 0, 0);
            }
            if (!match.IsMember(playerId))
            {
                return (match, "sender-not-in-game", 0, 0);
            }
            var old = (match.GameSettings ?? initialSettings) & full;
            settings &= full;
            mask &= full;
            var updated = mask == 0 ? settings : ((old & ~mask) | (settings & mask)) & full;
            match.GameSettings = updated;
            return (match, "ok", old, updated);
        }
    }

    /// <summary>A snapshot of the game's OSDK state for building its GameSetup: settings, game attributes, and player attributes by player id.</summary>
    public (long? Settings, IReadOnlyDictionary<string, string> GameAttributes, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PlayerAttributes) OsdkState(long gameId)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match))
            {
                return (null, NoAttributes, new Dictionary<string, IReadOnlyDictionary<string, string>>());
            }
            return (match.GameSettings,
                new Dictionary<string, string>(match.GameAttributes, StringComparer.Ordinal),
                match.PlayerAttributes.ToDictionary(entry => entry.Key,
                    entry => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(entry.Value, StringComparer.Ordinal), StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Records one player's ONLINE end-of-match report: (game, first report from this player, every player reported). Only
    /// the report that completes the roster may end the game and its relay, so the player who finishes first cannot take
    /// the peer's transport away (Python server acknowledge_game_report).
    /// </summary>
    public (RivalsMatch? Match, bool First, bool AllReported) AcknowledgeGameReport(string playerId, long gameId)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match) || !match.IsMember(playerId))
            {
                return (null, false, false);
            }
            var first = match.ReportedPlayers.Add(playerId);
            // A player who left will not report: the remaining player's report completes the game.
            return (match, first, match.Players.All(player => match.ReportedPlayers.Contains(player.PlayerId) || match.DepartedPlayers.Contains(player.PlayerId)));
        }
    }

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    private static (RivalsMatch Match, string Status, IReadOnlyDictionary<string, string> Changed) MergeAttributes(
        RivalsMatch match, Dictionary<string, string> stored, IReadOnlyDictionary<string, string> attributes, int maxStoredKeys)
    {
        var changed = attributes.Where(pair => !stored.TryGetValue(pair.Key, out var value) || value != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (stored.Keys.Union(changed.Keys, StringComparer.Ordinal).Count() > maxStoredKeys)
        {
            return (match, "too-many-keys", NoAttributes);
        }
        foreach (var (key, value) in changed)
        {
            stored[key] = value;
        }
        return (match, "ok", changed);
    }

    /// <summary>
    /// Ends <paramref name="match"/> when its setup stalled: it is still the live game, its mesh never completed, and at least
    /// one player initialized its endpoint (so that player is waiting on the other). Nobody's departure is announced; the
    /// caller tells each player it was removed. Returns the players and the mesh facts for the log, or null when the game
    /// completed its mesh, already ended, or neither client ever reported.
    /// </summary>
    public StalledSetup? EndStalledSetup(RivalsMatch match)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(match.GameId, out var current) || !ReferenceEquals(current, match) || match.MeshComplete
                || match.MeshInitializedPlayers.Count == 0 || match.State is GameState.InGame or GameState.PostGame)
            {
                return null;
            }
            var stalled = new StalledSetup(match.Players.ToList(), string.Join(",", match.MeshInitializedPlayers.Order()),
                string.Join(",", match.ConnectedPeerEdges.Select(edge => $"{edge.Source}->{edge.Target}")));
            RemoveGameLocked(match.GameId, "setup-stalled");
            return stalled;
        }
    }

    public bool EndGame(string playerId, long gameId)
    {
        lock (_gate)
        {
            return _games.TryGetValue(gameId, out var match) && match.IsMember(playerId) && RemoveGameLocked(gameId);
        }
    }

    /// <summary>Who is searching (and in which mode) and who is in a game, read together for the operator panel.</summary>
    public MatchmakingLobbySnapshot PanelSnapshot()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            var players = _sessions.Values.Where(entry => !_gamesByPlayer.ContainsKey(entry.PlayerId))
                .Select(entry => new MatchmakingLobbyPlayer(entry.PlayerId, "searching", entry.Mode, entry.SessionId, null, null, Math.Max(0, now - entry.CreatedTicks)))
                .ToList();
            foreach (var (playerId, gameId) in _gamesByPlayer)
                if (_games.TryGetValue(gameId, out var match))
                    players.Add(new MatchmakingLobbyPlayer(playerId, "match", match.Mode, match.Entry(playerId)?.SessionId, gameId, match.State, null));
            return new MatchmakingLobbySnapshot(_accepting, players);
        }
    }

    /// <summary>Every paired game that is still live, oldest first, read together for the public active-matches list.</summary>
    public IReadOnlyList<ActiveMatch> ActiveMatches()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            return _games.Values.OrderBy(match => match.PairedTicks)
                .Select(match => new ActiveMatch(match.GameId, match.Mode, match.HouseRuleType, match.State,
                    match.Players.Select(player => new ActiveMatchPlayer(player.PlayerId, match.DepartedPlayers.Contains(player.PlayerId))).ToList(),
                    Math.Max(0, now - match.PairedTicks), match.InGameTicks is { } kickOff ? Math.Max(0, now - kickOff) : null))
                .ToList();
        }
    }

    public (IReadOnlyDictionary<string, string> Searching, IReadOnlySet<string> InMatch) Lobby()
    {
        lock (_gate)
        {
            var searching = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in _sessions.Values.Where(entry => !_gamesByPlayer.ContainsKey(entry.PlayerId)))
            {
                searching[entry.PlayerId] = entry.Mode;
            }
            return (searching, _gamesByPlayer.Keys.ToHashSet(StringComparer.Ordinal));
        }
    }

    /// <summary>Whether a game has taken this player's search and has not ended for them (the launcher's inMatch).</summary>
    public bool IsInMatch(string playerId)
    {
        lock (_gate)
        {
            return _gamesByPlayer.ContainsKey(playerId);
        }
    }

    /// <summary>Whether the player has a queued search that no game has taken yet.</summary>
    public bool IsSearching(string playerId)
    {
        lock (_gate)
        {
            return !_gamesByPlayer.ContainsKey(playerId) && _sessions.Values.Any(entry => entry.PlayerId == playerId);
        }
    }

    /// <summary>
    /// The relay's answer to who went quiet first in a game, live or recently released (<see cref="GameplayRelay.QuietFirstBy"/>).
    /// FIFA's match end names its game by the report id (its <c>gid</c>, <see cref="ReportIdOf"/>), not the Blaze game id the
    /// relay keys on, so either is accepted: on 2026-10-04 all 21 lost-connection ends looked the game up by gid and missed.
    /// </summary>
    public TimeSpan? RelayQuietFirstBy(long gameOrReportId, string playerId) =>
        _relay is null ? null : _relay.QuietFirstBy(gameOrReportId, playerId)
            ?? (GameIdOfReport(gameOrReportId) is { } gameId ? _relay.QuietFirstBy(gameId, playerId) : null);

    /// <summary>
    /// Whether the caller's opponent left this game (its Blaze game id or report id) first, while it was being played: its
    /// leave request or quit report reached Blaze while the caller was still in the game. An alt-tab disconnect
    /// (FIFA_SETTING_ENABLE_ALT_TAB_DISCONNECTS, live 2026-10-08) ends the alt-tabbed client's game at once: it reports,
    /// leaves the game and never sends its FUT match end, while both gameplay streams stop in the same instant, so the relay
    /// cannot tell who left. The caller's FIFA reports the lost link only after the leave (38 one-sided lost-connection ends
    /// in 1.5 h on 2026-10-08, each with the absent player first out of the live game). A crash or pulled cable never
    /// leaves: Blaze times it out minutes later, after the player who stayed has left.
    /// </summary>
    public bool OpponentLeftGameFirst(long gameOrReportId, string playerId)
    {
        lock (_gate)
        {
            PruneFirstLeaversLocked();
            return (_firstInGameLeavers.TryGetValue(gameOrReportId, out var left)
                    || (GameIdOfReport(gameOrReportId) is { } gameId && _firstInGameLeavers.TryGetValue(gameId, out left)))
                && left.Leaver != playerId && left.Players.Contains(playerId, StringComparer.Ordinal);
        }
    }

    private void NoteFirstInGameLeaverLocked(RivalsMatch match, string playerId)
    {
        PruneFirstLeaversLocked();
        _firstInGameLeavers[match.GameId] = (playerId, match.Players.Select(player => player.PlayerId).ToArray(), Environment.TickCount64);
    }

    private void PruneFirstLeaversLocked()
    {
        var expired = Environment.TickCount64 - (long)FirstLeaverMemory.TotalMilliseconds;
        foreach (var gameId in _firstInGameLeavers.Where(entry => entry.Value.Ticks < expired).Select(entry => entry.Key).ToList())
        {
            _firstInGameLeavers.Remove(gameId);
        }
    }

    /// <summary>
    /// The report id FIFA is given for a game and echoes as the match end's <c>gid</c>: the game id in the high bits and the
    /// completing search's id in the low 16.
    /// </summary>
    internal static long ReportIdOf(long gameId, long sessionId) => (gameId << 16) | (sessionId & 0xFFFF);

    /// <summary>The game id inside a report id (<see cref="ReportIdOf"/>), or null for a value too small to be one.</summary>
    internal static long? GameIdOfReport(long reportId) => reportId >= 1L << 16 ? reportId >> 16 : null;

    public long? GameIdFor(string playerId)
    {
        lock (_gate)
        {
            return _gamesByPlayer.TryGetValue(playerId, out var gameId) ? gameId : null;
        }
    }

    public RivalsMatch? MatchFor(long gameId)
    {
        lock (_gate)
        {
            return _games.GetValueOrDefault(gameId);
        }
    }

    /// <summary>Runs <paramref name="read"/> on the game under the registry lock (the match's sets are not thread-safe).</summary>
    public T Read<T>(long gameId, Func<RivalsMatch?, T> read)
    {
        lock (_gate)
        {
            return read(_games.GetValueOrDefault(gameId));
        }
    }

    /// <summary>The state machine side of an IN_GAME probe: moves a PRE_GAME game to IN_GAME.</summary>
    internal bool ProbeToInGame(long gameId)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var match) || match.State != GameState.PreGame)
            {
                return false;
            }
            match.State = GameState.InGame;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            foreach (var gameId in _games.Keys.ToList())
            {
                RemoveGameLocked(gameId, "registry-reset");
            }
            _sessions.Clear();
            _ready.Clear();
        }
    }

    private (RivalsMatch? Match, bool Claimed) Claim(long gameId, Func<RivalsMatch, bool> claim)
    {
        lock (_gate)
        {
            return _games.TryGetValue(gameId, out var match) ? (match, claim(match)) : (null, false);
        }
    }

    private bool RemoveGameLocked(long gameId, string reason = "match-removed")
    {
        if (!_games.Remove(gameId, out var match))
        {
            return false;
        }
        _ready.RemoveAll(entry => entry.GameId == gameId);
        foreach (var player in match.Players)
        {
            if (_gamesByPlayer.TryGetValue(player.PlayerId, out var mapped) && mapped == gameId)
            {
                _gamesByPlayer.Remove(player.PlayerId);
            }
        }
        _relay?.Release(gameId, reason);
        return true;
    }
}
