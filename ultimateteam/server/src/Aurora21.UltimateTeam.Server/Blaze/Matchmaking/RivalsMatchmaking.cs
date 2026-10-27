using System.Globalization;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Diagnostics;
using Aurora21.UltimateTeam.Server.Relay;
using static Aurora21.UltimateTeam.Server.Blaze.BlazeComponent;

namespace Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

/// <summary>
/// Division Rivals matchmaking over Blaze GameManager: queues searches, pairs two players, sends
/// each the peer subscriptions and GameSetup, and releases the mesh, session and PRE_GAME
/// notifications as the clients report their lifecycle. Every notification is gated by the
/// registry's latches, so gates are re-evaluated after every report.
/// </summary>
public sealed class RivalsMatchmaking
{
    /// <summary>The scheduling gap before asynchronous notifications follow an RPC reply.</summary>
    private static readonly TimeSpan NotificationGap = TimeSpan.FromMilliseconds(20);

    /// <summary>How long AdvanceGameState(PRE_GAME) waits for the reverse mesh edge before it is answered.</summary>
    private static readonly TimeSpan PregameReplyWait = TimeSpan.FromSeconds(3);

    private const long SuccessCreatedGame = 0;

    private readonly MatchmakingRegistry _registry;
    private readonly MatchmakingPayloads _payloads;
    private readonly GameplayRelay? _relay;
    private readonly FeatureSwitches _switches;
    private readonly PacketCapture _capture;
    private readonly ILogger _log;

    /// <summary>The last Messaging message id (ServerMessage.MGID / SendMessageResponse.MGID) this process allocated.</summary>
    private long _nextMessageId;

    public RivalsMatchmaking(MatchmakingRegistry registry, MatchmakingPayloads payloads, GameplayRelay? relay, FeatureSwitches switches, PacketCapture capture, ILogger log)
    {
        _registry = registry;
        _payloads = payloads;
        _relay = relay;
        _switches = switches;
        _capture = capture;
        _log = log;
        if (relay is not null)
        {
            relay.PeerLinkPromoted = (gameId, source, target) => Observe(AcknowledgeRelayPeerLinkAsync(gameId, source, target), "relay-peer-link", gameId);
        }
        registry.PlayerDeparted = departure => Observe(AnnounceDepartureAsync(departure), "player-departed", departure.Match.GameId);
    }

    public MatchmakingRegistry Registry => _registry;

    /// <summary>The reply to a matchmaking request, or null when the request is not one.</summary>
    public BlazeReply? Respond(BlazeClient client, Fire2Frame frame)
    {
        switch ((frame.Component, frame.Command))
        {
            case (GameManager, GameManagerCommand.StartMatchmakingScenario):
                {
                    RecordMachineId(client, frame.Payload);
                    var mode = TdfReader.FindString(frame.Payload, "SCNM") is { Length: > 0 } scenario
                        ? scenario
                        : TdfReader.FindStringMap(frame.Payload, "ATTR").GetValueOrDefault("GAME_MODE", string.Empty);
                    var sessionId = _registry.Start(client.PlayerId, mode, frame.Payload, client);
                    _log.LogInformation("MATCHMAKING START player={Player} mode={Mode} sessionId={Session} machineId={Machine}",
                        client.PlayerId, mode.Length > 0 ? mode : "unknown", sessionId, client.MachineId);
                    // The scenario route keeps wire id zero: projecting the private queue id made both clients ignore GameSetup.
                    return Empty("GameManager.StartMatchmaking");
                }
            case (UserSessions, UserSessionsCommand.UpdateNetworkInfo):
                RecordMachineId(client, frame.Payload);
                _log.LogInformation("USERSESSIONS NETWORK INFO player={Player} machineId={Machine}", client.PlayerId, client.MachineId);
                return Empty("UserSessions.UpdateNetworkInfo");
            case (GameManager, GameManagerCommand.CancelMatchmaking):
                {
                    var sessionId = TdfReader.FindInteger(frame.Payload, "MSID") ?? 0;
                    var removed = _registry.Cancel(client.PlayerId, sessionId);
                    _log.LogInformation("MATCHMAKING CANCEL player={Player} sessionId={Session} removed={Removed}", client.PlayerId, sessionId, removed);
                    return Empty("GameManager.CancelMatchmaking");
                }
            case (GameManager, GameManagerCommand.MeshEndpointsConnected):
                return Empty("GameManager.MeshEndpointsConnected");
            // Leaving is acted on after the reply (AfterReplyAsync), so the leaver sees its reply before the others hear.
            case (GameManager, GameManagerCommand.LeaveGameByGroup):
                return Empty("GameManager.LeaveGameByGroup");
            case (GameManager, GameManagerCommand.RemovePlayer):
                return Empty("GameManager.RemovePlayer");
            case (GameManager, GameManagerCommand.MeshEndpointsDisconnected):
            case (GameManager, GameManagerCommand.MeshEndpointsConnectionLost):
                LogMeshLoss(client, frame);
                return Empty(frame.Command == GameManagerCommand.MeshEndpointsDisconnected
                    ? "GameManager.MeshEndpointsDisconnected" : "GameManager.MeshEndpointsConnectionLost");
            case (GameManager, GameManagerCommand.ConnectionTelemetry):
                _log.LogInformation("MATCHMAKING CONNECTION TELEMETRY player={Player} bytes={Bytes} payload={Payload}", client.PlayerId, frame.Payload.Length, TdfDebug.Summarize(frame.Payload));
                if (_relay is not null && ReadTelemetry(frame.Payload) is { } telemetry)
                {
                    _relay.RecordTelemetry(telemetry.GameId, client.PlayerId, telemetry.LatencyMs, telemetry.LossPercent);
                }
                return Empty("GameManager.ConnectionTelemetry");
            case (GameManager, GameManagerCommand.UpdateGameSession):
                return Empty("GameManager.UpdateGameSession");
            case (GameManager, GameManagerCommand.AdvanceGameState):
                return Empty("GameManager.AdvanceGameState");
            // The OSDK pregame RPCs are void; their relays run after the reply (AfterReplyAsync).
            case (GameManager, GameManagerCommand.SetPlayerAttributes):
                return Empty("GameManager.SetPlayerAttributes");
            case (GameManager, GameManagerCommand.SetGameAttributes):
                return Empty("GameManager.SetGameAttributes");
            case (GameManager, GameManagerCommand.SetGameSettings):
                return Empty("GameManager.SetGameSettings");
            case (Messaging, MessagingCommand.SendMessage):
                // The reply and the relayed NotifyMessage share one id, so it is allocated before the reply is built.
                client.PendingMessageId = Interlocked.Increment(ref _nextMessageId);
                return new BlazeReply(MatchmakingPayloads.SendMessageResponse(client.PendingMessageId), "Messaging.SendMessage");
            // The ONLINE report only acknowledges receipt; its ResultNotification follows (BlazeConnectionHandler).
            case (GameReporting, GameReportingCommand.SubmitGameReport):
                return Empty("GameReporting.SubmitGameReport");
            case (UserSessions, UserSessionsCommand.LookupUsersByPersonaNames):
                {
                    var requested = TdfReader.FindStringList(frame.Payload, "PLST");
                    var gameId = _registry.GameIdFor(client.PlayerId);
                    var payload = gameId is { } id
                        ? _registry.Read(id, match => _payloads.LookupUsers(match, client, requested))
                        : _payloads.LookupUsers(null, client, requested);
                    // The names are the player's EA friends: log how many, not who.
                    _log.LogInformation("USERSESSIONS LOOKUP gameId={GameId} caller={Player} requested={Count}", gameId, client.PlayerId, requested.Count);
                    return new BlazeReply(payload, "UserSessions.LookupUsersByPersonaNames");
                }
            default:
                return null;
        }
    }

    /// <summary>Work done before a request is answered: gate logging, and holding PRE_GAME until the mesh completes.</summary>
    public async Task BeforeReplyAsync(BlazeClient client, Fire2Frame frame)
    {
        if (frame.Component == UserSessions && frame.Command is UserSessionsCommand.LookupUser or UserSessionsCommand.LookupUsersByPersonaNames)
        {
            _log.LogInformation("USERSESSIONS RX cmd={Command} player={Player} bytes={Bytes} payload={Payload} hex={Hex}",
                frame.Command, client.PlayerId, frame.Payload.Length, TdfDebug.Summarize(frame.Payload), Convert.ToHexString(frame.Payload).ToLowerInvariant());
        }
        if (frame.Component != GameManager)
        {
            return;
        }
        var gameId = TdfReader.FindInteger(frame.Payload, "GID") ?? 0;
        if (frame.Command == GameManagerCommand.UpdateGameSession)
        {
            var meshReady = await _registry.WaitForMeshInitializationAsync(gameId, TimeSpan.Zero);
            var rosterReady = await _registry.WaitForRosterActivationAsync(gameId, TimeSpan.Zero);
            _log.LogInformation("MATCHMAKING SESSION UPDATE GATE gameId={GameId} player={Player} meshInitialized={Mesh} rosterActive={Roster}", gameId, client.PlayerId, meshReady, rosterReady);
        }
        else if (frame.Command == GameManagerCommand.AdvanceGameState && TdfReader.FindInteger(frame.Payload, "GSTA") == GameState.PreGame)
        {
            // Acknowledging PRE_GAME before the reverse edge arrives lets FIFA continue as if it committed;
            // the client then disconnects before its peer finishes the mesh.
            var meshReady = await _registry.WaitForMeshCompletionAsync(gameId, PregameReplyWait);
            _log.LogInformation("MATCHMAKING COMMAND3 REPLY GATE gameId={GameId} player={Player} meshComplete={Mesh}", gameId, client.PlayerId, meshReady);
        }
    }

    /// <summary>The asynchronous notifications that follow a request's reply.</summary>
    public async Task AfterReplyAsync(BlazeClient client, Fire2Frame frame)
    {
        switch ((frame.Component, frame.Command))
        {
            case (GameManager, GameManagerCommand.StartMatchmakingScenario):
                await PublishPairedGameAsync(client);
                break;
            case (GameManager, GameManagerCommand.CancelMatchmaking):
                await Task.Delay(NotificationGap);
                var sessionId = TdfReader.FindInteger(frame.Payload, "MSID") ?? 0;
                await client.TrySendAsync(Notify(GameManager, GameManagerNotification.MatchmakingFinished, MatchmakingPayloads.MatchmakingCancelledNotification(sessionId, client.PersonaId)));
                _log.LogInformation("FIRE2 TX NOTIFY GameManager.MatchmakingCancelled sessionId={Session} result=SESSION_CANCELED", sessionId);
                break;
            case (GameManager, GameManagerCommand.MeshEndpointsConnected):
                await HandleMeshReportAsync(client, frame.Payload);
                break;
            case (GameManager, GameManagerCommand.LeaveGameByGroup):
            case (GameManager, GameManagerCommand.RemovePlayer):
                HandleLeave(client, frame);
                break;
            case (GameManager, GameManagerCommand.UpdateGameSession):
                await HandleSessionUpdateAsync(client, frame.Payload);
                break;
            case (GameManager, GameManagerCommand.AdvanceGameState):
                await HandleAdvanceAsync(client, frame.Payload);
                break;
            case (GameReporting, GameReportingCommand.SubmitOfflineGameReport):
                if (_registry.GameIdFor(client.PlayerId) is { } gameId)
                {
                    _registry.EndGame(client.PlayerId, gameId);
                }
                break;
            case (GameReporting, GameReportingCommand.SubmitGameReport):
                await Guarded(() => AcknowledgeOnlineReportAsync(client, frame.Payload), client, frame);
                break;
            // The void reply is already on the sender's socket, so it sees reply-then-notification. A failure here must not end
            // the handler: that would disconnect the sender and remove the game for both players.
            case (Messaging, MessagingCommand.SendMessage):
                await Guarded(() => RelayMessageAsync(client, frame.Payload), client, frame);
                break;
            case (GameManager, GameManagerCommand.SetPlayerAttributes):
            case (GameManager, GameManagerCommand.SetGameAttributes):
                await Guarded(() => RelayAttributesAsync(client, frame.Command, frame.Payload), client, frame);
                break;
            case (GameManager, GameManagerCommand.SetGameSettings):
                await Guarded(() => RelayGameSettingsAsync(client, frame.Payload), client, frame);
                break;
        }
    }

    private async Task Guarded(Func<Task> relay, BlazeClient client, Fire2Frame frame)
    {
        try
        {
            await relay();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogError(error, "MATCHMAKING RELAY FAILED c={Component} cmd={Command} sender={Player}", frame.Component, frame.Command, client.PlayerId);
        }
    }

    /// <summary>
    /// Blaze Messaging relay (Python server 7ae589e, MESSAGING_RELAY): the FUT co-op kit screen runs its Ready arbitration over
    /// ordinary gctl ClientMessages, so each one goes to the other players of the sender's game as NotifyMessage, and back to
    /// the sender too when its FLAG sets Echo. The admin closes the vote (abcr, ARB_3, abir/abrq/abau/abcm) and sends
    /// AdvanceGameState(IN_GAME) itself only once it sees its own messages come back (verified live 2026-09-23, games
    /// 217888921 and 217943331); without the relay both players press Ready and nothing happens.
    /// </summary>
    private async Task RelayMessageAsync(BlazeClient client, byte[] payload)
    {
        var messageId = client.PendingMessageId;
        if (ClientMessage.Parse(payload) is not { } message)
        {
            _log.LogWarning("MESSAGING SENDMESSAGE sender={Sender} status=malformed msgId={MessageId} bytes={Bytes} payload={Payload}",
                client.PlayerId, messageId, payload.Length, TdfDebug.Summarize(payload));
            return;
        }
        var gameId = message.TargetIds.Count > 0 ? message.TargetIds[0] : 0;
        var status = message.Problem(_payloads.Online.Messaging);
        var recipients = new List<MatchmakingEntry>();
        if (status is null)
        {
            (status, recipients) = _registry.Read(gameId, match => match is null
                ? ("unknown-game", new List<MatchmakingEntry>())
                : !match.IsMember(client.PlayerId)
                    ? ("sender-not-in-game", new List<MatchmakingEntry>())
                    : ("ok", match.Players.Where(player => message.Echo || player.PlayerId != client.PlayerId).ToList()));
        }
        var delivered = 0;
        if (status == "ok")
        {
            var notification = Notify(Messaging, MessagingNotification.NotifyMessage, _payloads.ServerMessage(payload, messageId, client));
            foreach (var recipient in recipients)
            {
                delivered += await recipient.Client!.TrySendAsync(notification) ? 1 : 0;
            }
        }
        _log.Log(status == "ok" ? LogLevel.Information : LogLevel.Warning,
            "MESSAGING SENDMESSAGE sender={Sender} status={Status} gameId={GameId} msgId={MessageId} TYPE={Type} TAG={Tag} TTYP={TargetType} TIDS={TargetIds} FLAG=0x{Flags:x}(echo={Echo}) STAT={Stat} ATTR={Attributes} unknown={Unknown} notify1={Delivered}/{Recipients} bytes={Bytes}",
            client.PlayerId, status ?? "ok", gameId, messageId, ClientMessage.FourCc(message.Type), ClientMessage.FourCc(message.Tag),
            message.TargetType is { } targetType ? $"({targetType.Component}, {targetType.EntityType})" : "-", string.Join(",", message.TargetIds),
            message.Flags, message.Echo, message.Status, message.DescribeAttributes(), message.UnknownTags.Count > 0 ? string.Join(",", message.UnknownTags) : "-",
            delivered, recipients.Count, payload.Length);
    }

    /// <summary>
    /// OSDK pregame attribute relays (Python server f021cf1, OSDK_PLAYER_ATTRS and OSDK_GAME_ATTR_RELAY). SetPlayerAttributes
    /// (REQ=1, REQ=2) is stored on the target roster player and relayed as PlayerAttribChange: a non-arbitrator's
    /// RequestSessionData sets REQ=1 and the arbitrator answers with its session sync only after that notification arrives.
    /// SetGameAttributes (the Ready vote's ARB_3) is stored on the game and relayed as GameAttribChange. Only changed pairs are
    /// relayed, to every player including the sender (a player change reaches the others only after the REQ hold, see
    /// <see cref="RelayPlayerAttributesHeldAsync"/>); stored pairs also go into any later GameSetup.
    /// </summary>
    private async Task RelayAttributesAsync(BlazeClient client, int command, byte[] payload)
    {
        var playerRequest = command == GameManagerCommand.SetPlayerAttributes;
        var gameId = TdfReader.FindInteger(payload, "GID") ?? 0;
        var personaId = playerRequest ? TdfReader.FindInteger(payload, "PID") ?? 0 : 0;
        var attributes = TdfReader.FindStringMap(payload, "ATTR");
        var limits = _payloads.Online.OsdkAttributes;
        var problem = AttributeProblem(attributes, limits);
        var (match, status, changed) = problem is not null
            ? (null, problem, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>())
            : playerRequest
                ? _registry.SetPlayerAttributes(client.PlayerId, gameId, personaId, attributes, limits.MaxStoredKeys)
                : _registry.SetGameAttributes(client.PlayerId, gameId, attributes, limits.MaxStoredKeys);
        var delivered = 0;
        if (match is not null && status == "ok" && changed.Count > 0)
        {
            delivered = !playerRequest
                ? await DeliverToAllAsync(match, GameManagerNotification.GameAttribChange, MatchmakingPayloads.GameAttribChange(gameId, changed), "GameAttribChange")
                : _switches.RequestHoldSeconds > 0
                    ? await RelayPlayerAttributesHeldAsync(match, client, MatchmakingPayloads.PlayerAttribChange(gameId, personaId, changed))
                    : await DeliverToAllAsync(match, GameManagerNotification.PlayerAttribChange, MatchmakingPayloads.PlayerAttribChange(gameId, personaId, changed), "PlayerAttribChange");
        }
        else if (status == "ok")
        {
            status = "unchanged";
        }
        static string Describe(IReadOnlyDictionary<string, string> pairs) =>
            "{" + string.Join(", ", pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")) + "}";
        _log.Log(status is "ok" or "unchanged" ? LogLevel.Information : LogLevel.Warning,
            "MATCHMAKING OSDK {Kind} gameId={GameId} sender={Sender} pid={Persona} status={Status} request={Request} changed={Changed} notify{Notification}={Delivered}/{Count}{Payload}",
            playerRequest ? "PLAYER ATTRS" : "GAME ATTRS", gameId, client.PlayerId, personaId, status, Describe(attributes), Describe(changed),
            playerRequest ? GameManagerNotification.PlayerAttribChange : GameManagerNotification.GameAttribChange, delivered, match?.Players.Count ?? 0,
            problem is null ? string.Empty : $" payload={TdfDebug.Summarize(payload)}");
    }

    /// <summary>
    /// PlayerAttribChange under the REQ hold (<see cref="FeatureSwitches.RequestHoldSeconds"/>): the sender hears its own
    /// change at once, every other player no earlier than the hold after the host's GameSetup and in arrival order per game,
    /// so a REQ=2 never overtakes a held REQ=1. A held relay is dropped quietly once the game has ended or the sender or the
    /// recipient has left. Returns the immediate deliveries (the sender's).
    /// </summary>
    private async Task<int> RelayPlayerAttributesHeldAsync(RivalsMatch match, BlazeClient sender, byte[] payload)
    {
        var frame = Notify(GameManager, GameManagerNotification.PlayerAttribChange, payload);
        var arrived = Environment.TickCount64;
        var releaseAt = (match.SetupDeliveredTicks ?? arrived) + (long)(_switches.RequestHoldSeconds * 1000);
        Observe(match.ChainAttributeRelay(() => ReleasePlayerAttributesAsync(match, sender.PlayerId, frame, arrived, releaseAt)), "req-hold", match.GameId);
        return await sender.TrySendAsync(frame) ? 1 : 0;
    }

    private async Task ReleasePlayerAttributesAsync(RivalsMatch match, string senderId, byte[] frame, long arrived, long releaseAt)
    {
        var wait = releaseAt - Environment.TickCount64;
        if (wait > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait));
        }
        var recipients = _registry.Read(match.GameId, current => ReferenceEquals(current, match) && !match.DepartedPlayers.Contains(senderId)
            ? match.Players.Where(player => player.PlayerId != senderId && !match.DepartedPlayers.Contains(player.PlayerId)).ToList()
            : new List<MatchmakingEntry>());
        var heldMs = Environment.TickCount64 - arrived;
        if (recipients.Count == 0)
        {
            _log.LogDebug("MATCHMAKING OSDK REQ HOLD DROPPED gameId={GameId} player={Player} heldMs={HeldMs}", match.GameId, senderId, heldMs);
            return;
        }
        var delivered = 0;
        foreach (var recipient in recipients)
        {
            delivered += await recipient.Client!.TrySendAsync(frame) ? 1 : 0;
        }
        if (releaseAt > arrived)
        {
            _log.LogInformation("MATCHMAKING OSDK REQ HOLD gameId={GameId} player={Player} heldMs={HeldMs} delivered={Delivered}/{Count}",
                match.GameId, senderId, heldMs, delivered, recipients.Count);
        }
        else
        {
            _log.LogInformation("FIRE2 TX NOTIFY GameManager.PlayerAttribChange command={Command} gameId={GameId} delivered={Delivered}/{Count} (others of {Player})",
                GameManagerNotification.PlayerAttribChange, match.GameId, delivered, recipients.Count, senderId);
        }
    }

    /// <summary>Why a SetPlayerAttributes/SetGameAttributes map is refused before it is stored or relayed; null when it is fine.</summary>
    private static string? AttributeProblem(IReadOnlyDictionary<string, string> attributes, BlazeOsdkAttributeLimits limits)
    {
        if (attributes.Count == 0)
        {
            return "no-attributes";
        }
        if (attributes.Count > limits.MaxPairs)
        {
            return "too-many-pairs";
        }
        return attributes.Any(pair => System.Text.Encoding.UTF8.GetByteCount(pair.Key) > limits.MaxBytes || System.Text.Encoding.UTF8.GetByteCount(pair.Value) > limits.MaxBytes)
            ? "oversize"
            : null;
    }

    /// <summary>PreGame.IsGameLocked(): GSET &amp; 0x80f == 0.</summary>
    private const long GameSettingsOpenMask = 0x80F;

    /// <summary>
    /// OSDK SetGameSettings relay (Python server f021cf1, OSDK_SETTINGS_RELAY). With open GSET bits the arbitrator's LockGame
    /// sends {GSET=0, GSTM=0x80f} and completes only on GameSettingsChange, which is therefore always published to every
    /// player, even when nothing changed; without it the pre-match timer keeps running (the kit-screen stall).
    /// </summary>
    private async Task RelayGameSettingsAsync(BlazeClient client, byte[] payload)
    {
        var gameId = TdfReader.FindInteger(payload, "GID") ?? 0;
        var requested = TdfReader.FindInteger(payload, "GSET");
        var mask = TdfReader.FindInteger(payload, "GSTM");
        // A real LockGame carries both members (FIFA writes zero-valued members), so one with neither is not "replace with 0".
        var malformed = (requested is null && mask is null) || requested is < 0 or > 0xFFFFFFFF || mask is < 0 or > 0xFFFFFFFF;
        RivalsMatch? match = null;
        var (status, old, updated, delivered) = ("malformed", 0L, 0L, 0);
        if (!malformed)
        {
            (match, status, old, updated) = _registry.SetGameSettings(client.PlayerId, gameId, requested ?? 0, mask ?? 0, _payloads.Online.InitialGameSettings);
            if (match is not null && status == "ok")
            {
                delivered = await DeliverToAllAsync(match, GameManagerNotification.GameSettingsChange, MatchmakingPayloads.GameSettingsChange(gameId, updated), "GameSettingsChange");
            }
        }
        _log.Log(status == "ok" ? LogLevel.Information : LogLevel.Warning,
            "MATCHMAKING OSDK GAME SETTINGS gameId={GameId} sender={Sender} status={Status} GSET=0x{Requested:x} GSTM=0x{Mask:x} old=0x{Old:x} new=0x{New:x} locked={Locked} notify110={Delivered}/{Count}",
            gameId, client.PlayerId, status, requested ?? 0, mask ?? 0, old, updated, status == "ok" ? (updated & GameSettingsOpenMask) == 0 : null,
            delivered, match?.Players.Count ?? 0);
    }

    /// <summary>
    /// The ONLINE end-of-match report (Python server ONLINE_GAME_REPORT, live since 2026-09-24): the client waits for its
    /// ResultNotification (sent by the connection handler) and otherwise freezes about 45 s at full time (game 218201662).
    /// The game and its relay end only once every player has reported, so the first to finish cannot remove the peer's
    /// transport. A first report that did not finish (FNSH 0) is a quit: the peers are told at once through PlayerRemoved,
    /// because otherwise the only path that un-sticks them is their own 15-35 s mesh-loss timeout — the loading-screen hang
    /// of 2026-10-03 game 272737575, where the survivor sat in loading for 37 s until his timeout fired. A grace timer then
    /// ends the game server-side if no second report follows, so a peer whose client never recovers cannot pin the relay.
    /// </summary>
    private async Task AcknowledgeOnlineReportAsync(BlazeClient client, byte[] payload)
    {
        var gameId = _registry.GameIdFor(client.PlayerId);
        var finished = TdfReader.FindInteger(payload, "FNSH") ?? 0;
        var (match, first, allReported) = gameId is { } id ? _registry.AcknowledgeGameReport(client.PlayerId, id) : (null, false, false);
        _log.LogInformation("GAMEREPORTING ONLINE REPORT gameId={GameId} player={Player} reportId={ReportId} FNSH={Finished} first={First} allReported={AllReported} roster={Roster} bytes={Bytes}",
            gameId, client.PlayerId, TdfReader.FindInteger(payload, "GRID"), finished, first, allReported,
            match?.Players.Count ?? 0, payload.Length);
        if (match is null)
        {
            return;
        }
        if (allReported)
        {
            _registry.EndGame(client.PlayerId, match.GameId);
            return;
        }
        if (first && finished == 0)
        {
            // The same departure as a leave request (announced once, as PLAYER_LEFT); the LeaveGameByGroup FIFA sends right
            // after a quit then finds the player already gone.
            var status = _registry.Leave(client.PlayerId, match.GameId, PlayerRemovedReason.PlayerLeft, keepGame: true);
            _log.LogInformation("MATCHMAKING QUIT REPORT gameId={GameId} leaver={Player} departure={Status} graceEnd={Seconds}s",
                match.GameId, client.PlayerId, status, AbandonedGameEnd.TotalSeconds);
            ScheduleAbandonedGameEnd(match.GameId, client.PlayerId);
        }
    }

    /// <summary>
    /// LeaveGameByGroup / RemovePlayer (RemovePlayerRequest {GID, PID, REAS}). FIFA 21 leaves a game this way when its player
    /// quits the pregame or the match (REAS 8 GROUP_LEFT), and, when it joins its next game, leaves the previous one it still
    /// held (RemovePlayer, REAS 26). Only a player removing itself is honoured; the registry tells the others.
    /// </summary>
    private void HandleLeave(BlazeClient client, Fire2Frame frame)
    {
        var gameId = TdfReader.FindInteger(frame.Payload, "GID") ?? 0;
        var personaId = TdfReader.FindInteger(frame.Payload, "PID") ?? 0;
        var reason = TdfReader.FindInteger(frame.Payload, "REAS") is { } requested and >= 0 and <= PlayerRemovedReason.Last
            ? requested : PlayerRemovedReason.PlayerLeft;
        var status = personaId == client.PersonaId ? _registry.Leave(client.PlayerId, gameId, reason) : "not-self";
        _log.LogInformation("MATCHMAKING LEAVE cmd={Command} gameId={GameId} player={Player} pid={Persona} reason={Reason} status={Status} currentGame={Current}",
            frame.Command, gameId, client.PlayerId, personaId, reason, status, _registry.GameIdFor(client.PlayerId));
    }

    /// <summary>MeshEndpointsDisconnected / MeshEndpointsConnectionLost: the client lost its peer link to TCG. Logged; the client leaves on its own.</summary>
    /// <summary>
    /// The game and FIFA's own view of the peer link in a ConnectionTelemetry report (GID; RPTS[0].LATC latency in ms and
    /// RPTS[0].PKLF packet loss in percent, a big-endian float), or null without a game or a latency yet.
    /// </summary>
    internal static (long GameId, int LatencyMs, double? LossPercent)? ReadTelemetry(byte[] payload)
    {
        if (!TdfReader.TryParse(payload, out var fields))
        {
            return null;
        }
        var gameId = TdfReader.Find(fields, "GID", TdfType.Integer)?.Integer ?? 0;
        var latency = TdfReader.Find(fields, "LATC", TdfType.Integer)?.Integer ?? 0;
        if (gameId <= 0 || latency is <= 0 or > int.MaxValue)
        {
            return null;
        }
        double? loss = TdfReader.Find(fields, "PKLF", TdfType.Float)?.Bytes is { Length: 4 } raw
            ? System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(raw)
            : null;
        return (gameId, (int)latency, loss is >= 0 and <= 100 ? loss : null);
    }

    private void LogMeshLoss(BlazeClient client, Fire2Frame frame)
    {
        var gameId = TdfReader.FindInteger(frame.Payload, "GID") ?? 0;
        var target = TdfReader.FindObjectId(frame.Payload, "TCG");
        var (current, state) = _registry.Read(gameId, match => (match is not null && match.IsMember(client.PlayerId), match?.State));
        _log.LogInformation("MATCHMAKING MESH LOST cmd={Command} gameId={GameId} player={Player} targetPersona={Target} flags={Flags} stat={Stat} liveGame={Live} gameState={State}",
            frame.Command, gameId, client.PlayerId, target?.EntityId, TdfReader.FindInteger(frame.Payload, "FLGS"), TdfReader.FindInteger(frame.Payload, "STAT"), current, state);
    }

    /// <summary>
    /// NotifyPlayerRemoved (4/40) to every player still in the game, after the leaver's own reply; then, when the departure
    /// ended the game before IN_GAME, NotifyGameRemoved (4/16) so their clients drop the game too. From IN_GAME on the game
    /// is left alone: the remaining player still finishes and reports (opponent-left result, hard-drop rule).
    /// A departure that ends the game before IN_GAME follows the live <see cref="PregameDeparture"/> mode: a client told in the
    /// kit screen that its opponent left bailed out of its own next setup 1-5 s in (live 2026-10-05).
    /// </summary>
    private async Task AnnounceDepartureAsync(GameDeparture departure)
    {
        await Task.Delay(NotificationGap);
        var gameId = departure.Match.GameId;
        var personaId = departure.Leaver.Client?.PersonaId ?? 0;
        // A departure that ended the game before IN_GAME; a quit report keeps the game, so it is always announced.
        var pregame = departure.GameRemoved && departure.Match.State is not (GameState.InGame or GameState.PostGame);
        var mode = pregame ? _payloads.PregameDeparture.Mode : PregameDeparture.Announce;
        var (delivered, self, gameRemoved) = (0, 0, 0);
        if (mode != PregameDeparture.Silent)
        {
            if (_switches.PlayerRemovedNotification && personaId != 0)
            {
                var notification = Notify(GameManager, GameManagerNotification.PlayerRemoved, MatchmakingPayloads.PlayerRemoved(gameId, personaId, departure.Reason));
                foreach (var recipient in departure.Recipients)
                {
                    delivered += await recipient.Client!.TrySendAsync(notification) ? 1 : 0;
                }
            }
            if (mode == PregameDeparture.SelfRemoved)
            {
                // Each client's own removal, the only NotifyPlayerRemoved FIFA destroys its local game for.
                foreach (var recipient in departure.Recipients)
                {
                    self += await recipient.Client!.TrySendAsync(Notify(GameManager, GameManagerNotification.PlayerRemoved,
                        MatchmakingPayloads.PlayerRemoved(gameId, recipient.Client!.PersonaId, PlayerRemovedReason.GameDestroyed))) ? 1 : 0;
                }
                if (departure.Leaver.Client is { } leaver && personaId != 0)
                {
                    self += await leaver.TrySendAsync(Notify(GameManager, GameManagerNotification.PlayerRemoved,
                        MatchmakingPayloads.PlayerRemoved(gameId, personaId, departure.Reason))) ? 1 : 0;
                }
            }
            if (_switches.GameRemovedNotification && pregame)
            {
                var notification = Notify(GameManager, GameManagerNotification.GameRemoved, MatchmakingPayloads.GameRemoved(gameId));
                foreach (var recipient in departure.Recipients)
                {
                    gameRemoved += await recipient.Client!.TrySendAsync(notification) ? 1 : 0;
                }
            }
        }
        _log.LogInformation("MATCHMAKING PLAYER REMOVED gameId={GameId} leaver={Player} persona={Persona} reason={Reason} gameState={State} gameEnded={Ended} notify40={Delivered}/{Recipients} self40={Self} notify16={GameRemoved} pregameMode={Mode} enabled={Enabled}",
            gameId, departure.Leaver.PlayerId, personaId, departure.Reason, departure.Match.State, departure.GameRemoved,
            delivered, departure.Recipients.Count, self, gameRemoved, pregame ? mode : "-", _switches.PlayerRemovedNotification);
    }

    /// <summary>How long a game whose only report was a quit waits for the remaining player before it is ended (past the client's own 15-35 s mesh-loss timeout, game 272737575).</summary>
    private static readonly TimeSpan AbandonedGameEnd = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Ends an abandoned game once the grace window has passed without a second report. The remaining player's own late
    /// report still receives its ResultNotification (the connection handler answers every SubmitGameReport), so ending
    /// here cannot freeze the reporting client.
    /// </summary>
    private void ScheduleAbandonedGameEnd(long gameId, string leaverId) =>
        Observe(Task.Run(async () =>
        {
            await Task.Delay(AbandonedGameEnd);
            var overdue = _registry.Read(gameId, match => match is not null && match.ReportedPlayers.Count < match.Players.Count);
            if (!overdue)
            {
                return;
            }
            if (_registry.EndGame(leaverId, gameId))
            {
                _log.LogWarning("MATCHMAKING ABANDONED gameId={GameId} leaver={Player}: no report from the remaining player within {Seconds}s; game and relay ended",
                    gameId, leaverId, AbandonedGameEnd.TotalSeconds);
            }
        }), "abandoned-game-end", gameId);

    /// <summary>A closed connection leaves its searches and game.</summary>
    public void Disconnect(BlazeClient client) => _registry.Disconnect(client);

    /// <summary>How long an IN_GAME player's match waits for a resume after their Blaze connection closes (FeatureSwitches.ResumeGraceSeconds).</summary>
    public TimeSpan ResumeGrace => TimeSpan.FromSeconds(Math.Clamp(_switches.ResumeGraceSeconds, 0, FeatureSwitches.MaxResumeGraceSeconds));

    /// <summary>Whether <paramref name="client"/> sits in a game that is IN_GAME (a match being played).</summary>
    public bool InLiveGame(BlazeClient client) => _registry.InLiveGame(client);

    /// <summary>Hands a resumed player's searches and game from the old Blaze connection to the new one.</summary>
    public (int Searches, int Games) Rebind(BlazeClient from, BlazeClient to) => _registry.Rebind(from, to);

    /// <summary>Whether a received frame is matchmaking evidence worth capturing by default.</summary>
    public bool IsEvidence(Fire2Frame frame) =>
        _switches.CaptureAllFrames
        || (frame.Component == GameManager && frame.Command != GameManagerCommand.ConnectionTelemetry)
        || (frame.Component == UserSessions
            && frame.Command is UserSessionsCommand.LookupUser or UserSessionsCommand.LookupUsersByPersonaNames or UserSessionsCommand.UpdateNetworkInfo);

    private static void RecordMachineId(BlazeClient client, byte[] payload)
    {
        var reported = TdfReader.FindAllIntegers(payload, "MACI").Where(value => value > 0).ToList();
        if (reported.Count > 0)
        {
            client.MachineId = reported[^1];
        }
    }

    private static BlazeReply Empty(string label) => new(Array.Empty<byte>(), label);

    private static byte[] Notify(int component, int command, byte[] payload) => Fire2Frame.Build(component, command, 0, Fire2MessageType.Notification, payload);

    /// <summary>
    /// After the second StartMatchmaking reply is on the wire: peer subscriptions, then GameSetup (the
    /// joiner gets JoiningPlayerInitiateConnections when enabled), then the optional MatchmakingFinished.
    /// </summary>
    private async Task PublishPairedGameAsync(BlazeClient client)
    {
        if (_registry.TakeReady(client.PlayerId, client) is not { } match)
        {
            return;
        }
        await Task.Delay(NotificationGap);
        var subscribed = await DeliverPeerSubscriptionsAsync(match);
        if (subscribed != match.Players.Count)
        {
            _log.LogWarning("MATCHMAKING peer subscription incomplete gameId={GameId} delivered={Delivered}/{Count}", match.GameId, subscribed, match.Players.Count);
        }
        var delivered = await DeliverGameSetupAsync(match);
        await Task.Delay(NotificationGap);
        var finished = await DeliverMatchmakingFinishedAsync(match);
        if (finished != match.Players.Count)
        {
            _log.LogWarning("MATCHMAKING finish notification incomplete gameId={GameId} delivered={Delivered}/{Count}", match.GameId, finished, match.Players.Count);
        }
        _log.LogInformation("MATCHMAKING PAIRED gameId={GameId} players={Players} setup={Delivered}/{Count}",
            match.GameId, string.Join(",", match.Players.Select(player => player.PlayerId)), delivered, match.Players.Count);
        if (delivered != match.Players.Count)
        {
            _registry.Cancel(client.PlayerId, 0);
            return;
        }
        ScheduleSetupStallCheck(match);
        await NoteRivalsPairingAsync(match);
    }

    /// <summary>
    /// Ends the game if its mesh has not completed <see cref="FeatureSwitches.SetupStallSeconds"/> after GameSetup while one
    /// player is waiting on it, and sends each player its own PlayerRemoved (REAS 5 GAME_DESTROYED): the only removal FIFA
    /// destroys its local game for, so the waiting client leaves "Initializing..." instead of hanging until it quits. The
    /// opponent's departure is not announced (a client told in setup that its opponent left bails out of its next setup).
    /// Game 285813784 (2026-10-06): the guest sent nothing after GameSetup and the host waited 30 s until he quit.
    /// </summary>
    private void ScheduleSetupStallCheck(RivalsMatch match)
    {
        if (_switches.SetupStallSeconds <= 0)
        {
            return;
        }
        Observe(Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(_switches.SetupStallSeconds));
            if (_registry.EndStalledSetup(match) is not { } stalled)
            {
                return;
            }
            var removed = 0;
            foreach (var player in stalled.Players)
            {
                if (player.Client is { PersonaId: not 0 } client)
                {
                    removed += await client.TrySendAsync(Notify(GameManager, GameManagerNotification.PlayerRemoved,
                        MatchmakingPayloads.PlayerRemoved(match.GameId, client.PersonaId, PlayerRemovedReason.GameDestroyed))) ? 1 : 0;
                }
            }
            _log.LogWarning("MATCHMAKING SETUP STALLED gameId={GameId} mode={Mode} players={Players} seconds={Seconds} initialized={Initialized} edges={Edges} self40={Removed}/{Count}",
                match.GameId, match.Mode, string.Join(",", stalled.Players.Select(player => player.PlayerId)), _switches.SetupStallSeconds,
                stalled.Initialized, stalled.Edges, removed, stalled.Players.Count);
        }), "setup-stall", match.GameId);
    }

    /// <summary>
    /// Snapshots both players' Skill Ratings into each club's pending Rivals pairing when Blaze pairs a Rivals game (placement
    /// included); the match end rates the result against it. FIFA sends CreateMatch about a second before StartMatchmaking,
    /// so CreateMatch usually finds no pairing to record and the result would go unrated (the Python server records both
    /// sides when the pairing is made, _rivals_note_pairing).
    /// </summary>
    private async Task NoteRivalsPairingAsync(RivalsMatch match)
    {
        if (MatchmakingRegistry.ModeKey(match.Mode) != MatchmakingRegistry.RivalsMode || match.Players.Count != 2)
        {
            return;
        }
        try
        {
            var ratings = new long[match.Players.Count];
            for (var index = 0; index < match.Players.Count; index++)
            {
                var session = match.Players[index].Client!.Session;
                ratings[index] = await session.Game.ReadAsync(uow => session.Fut.Rivals.SkillRatingAsync(uow), CancellationToken.None);
            }
            for (var index = 0; index < match.Players.Count; index++)
            {
                var session = match.Players[index].Client!.Session;
                var opponent = match.Players[1 - index];
                var online = new OnlineOpponent(index == 0, opponent.PlayerId, opponent.Client!.PersonaId, match.GameId);
                var opponentRating = ratings[1 - index];
                await session.Game.RunAsync("rivals-pairing", uow => session.Fut.Rivals.NotePairingAsync(uow, online, opponentRating), CancellationToken.None);
            }
            _log.LogInformation("RIVALS PAIRING NOTED gameId={GameId} players={Players} ratings={Ratings}",
                match.GameId, string.Join(",", match.Players.Select(player => player.PlayerId)), string.Join(",", ratings));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogError(error, "RIVALS PAIRING NOT NOTED gameId={GameId}", match.GameId);
        }
    }

    /// <summary>Each client learns about itself (its NetworkAddress) and the other player before GameSetup.</summary>
    private async Task<int> DeliverPeerSubscriptionsAsync(RivalsMatch match)
    {
        var delivered = 0;
        for (var index = 0; index < match.Players.Count; index++)
        {
            var recipient = match.Players[index];
            var peer = match.Players[1 - index];
            var peerEndpoint = MatchmakingPayloads.ProjectedEndpoint(match, recipient, peer);
            var selfEndpoint = MatchmakingPayloads.ProjectedEndpoint(match, recipient, recipient);
            var notifications = new (int Command, byte[] Payload, string Label)[]
            {
                (UserSessionsNotification.ExtendedDataUpdate, _payloads.PeerExtendedDataUpdate(recipient.Client!, selfEndpoint), "ExtendedDataUpdate(self)"),
                (UserSessionsNotification.UserAdded, _payloads.PeerUserAdded(peer.Client!, peerEndpoint), "UserAdded"),
                (UserSessionsNotification.UserUpdated, MatchmakingPayloads.PeerUserUpdated(peer.Client!), "UserUpdated"),
                (UserSessionsNotification.ExtendedDataUpdate, _payloads.PeerExtendedDataUpdate(peer.Client!, peerEndpoint), "ExtendedDataUpdate"),
            };
            var all = true;
            foreach (var (command, payload, label) in notifications)
            {
                if (!await recipient.Client!.TrySendAsync(Notify(UserSessions, command, payload)))
                {
                    all = false;
                    break;
                }
                _log.LogInformation("FIRE2 TX NOTIFY UserSessions.{Label} gameId={GameId} recipient={Recipient} peer={Peer} endpoint={Address}:{Port}",
                    label, match.GameId, recipient.PlayerId, peer.PlayerId, peerEndpoint.Address, peerEndpoint.Port);
            }
            delivered += all ? 1 : 0;
        }
        return delivered;
    }

    /// <summary>
    /// GameSetup (4/20) to every player, each followed by InitiateConnections (4/22) with the same payload. The two decode
    /// the same TDF but enter different native callbacks: 20 alone builds the lobby but the joiner never dials its peer,
    /// and 22 instead of 20 leaves the joiner nothing to ingest (Python server, both observed live 2026-09-15; production
    /// has sent 20 then 22 to each player since).
    /// </summary>
    private async Task<int> DeliverGameSetupAsync(RivalsMatch match)
    {
        var delivered = 0;
        var (settings, gameAttributes, playerAttributes) = _registry.OsdkState(match.GameId);
        for (var index = 0; index < match.Players.Count; index++)
        {
            var player = match.Players[index];
            var payload = _payloads.GameSetup(match, index, settings, gameAttributes, playerAttributes);
            var (command, name) = index != 0 && _switches.JoinerCommand22
                ? (GameManagerNotification.JoiningPlayerInitiateConnections, "JoiningPlayerInitiateConnections")
                : (GameManagerNotification.GameSetup, "GameSetup");
            var frame = Notify(GameManager, command, payload);
            if (!await player.Client!.TrySendAsync(frame))
            {
                _log.LogWarning("MATCHMAKING GAMESETUP delivery failed gameId={GameId} player={Player}", match.GameId, player.PlayerId);
                continue;
            }
            delivered++;
            if (index == 0)
            {
                match.SetupDeliveredTicks ??= Environment.TickCount64;
            }
            if (command == GameManagerNotification.GameSetup && _switches.InitiateConnectionsAfterSetup)
            {
                await Task.Delay(NotificationGap);
                if (await player.Client.TrySendAsync(Notify(GameManager, GameManagerNotification.JoiningPlayerInitiateConnections, payload)))
                {
                    _log.LogInformation("FIRE2 TX NOTIFY GameManager.JoiningPlayerInitiateConnections command=22 (after GameSetup) gameId={GameId} player={Player} bytes={Bytes}",
                        match.GameId, player.PlayerId, payload.Length);
                }
                else
                {
                    _log.LogWarning("MATCHMAKING INITIATE CONNECTIONS delivery failed gameId={GameId} player={Player}", match.GameId, player.PlayerId);
                }
            }
            _capture.SaveEvidence($"rivals-gamesetup-{match.GameId}-{index}", frame);
            var endpoints = match.Players.Select(roster =>
            {
                var endpoint = MatchmakingPayloads.ProjectedEndpoint(match, player, roster);
                return $"{roster.PlayerId}:{endpoint.Address}:{endpoint.Port}:persona={roster.Client!.PersonaId}:machine={roster.Client.MachineId}";
            });
            _log.LogInformation("FIRE2 TX NOTIFY GameManager.{Name} command={Command} gameId={GameId} player={Player} bytes={Bytes} gameSpeed={GameSpeed} peerEndpoints={Endpoints}",
                name, command, match.GameId, player.PlayerId, payload.Length, match.GameSpeed(_payloads.GameSpeed)?.ToString(CultureInfo.InvariantCulture) ?? "default",
                string.Join(" ", endpoints));
        }
        return delivered;
    }

    /// <summary>
    /// MatchmakingFinished is off by default: the success dispatcher does not null-check the session and
    /// hard-crashes a client whose session is already gone, and FIFA reaches success from GameSetup anyway.
    /// </summary>
    private async Task<int> DeliverMatchmakingFinishedAsync(RivalsMatch match)
    {
        if (!_switches.MatchmakingFinished)
        {
            return match.Players.Count;
        }
        var delivered = 0;
        foreach (var recipient in match.Players)
        {
            if (await recipient.Client!.TrySendAsync(Notify(GameManager, GameManagerNotification.MatchmakingFinished, MatchmakingPayloads.MatchmakingFinishedNotification(recipient.Client, SuccessCreatedGame))))
            {
                delivered++;
                _log.LogInformation("FIRE2 TX NOTIFY GameManager.MatchmakingFinished gameId={GameId} player={Player} result={Result}", match.GameId, recipient.PlayerId, SuccessCreatedGame);
            }
        }
        return delivered;
    }

    private async Task<int> DeliverToAllAsync(RivalsMatch match, int command, byte[] payload, string label)
    {
        var delivered = 0;
        foreach (var player in match.Players)
        {
            delivered += await player.Client!.TrySendAsync(Notify(GameManager, command, payload)) ? 1 : 0;
        }
        _log.LogInformation("FIRE2 TX NOTIFY GameManager.{Label} command={Command} gameId={GameId} delivered={Delivered}/{Count}", label, command, match.GameId, delivered, match.Players.Count);
        return delivered;
    }

    private async Task HandleMeshReportAsync(BlazeClient client, byte[] payload)
    {
        var gameId = TdfReader.FindInteger(payload, "GID") ?? 0;
        var status = TdfReader.FindInteger(payload, "FLGS");
        var targetPersona = TdfReader.FindObjectId(payload, "TCG")?.EntityId;
        var endpointInitialization = (targetPersona ?? 0) == 0 && (status ?? 0) == 0;
        RivalsMatch? match;
        MatchmakingEntry? target = null;
        bool first;
        var completedNow = false;
        if (endpointInitialization)
        {
            (match, first) = _registry.AcknowledgeEndpointInitialization(client.PlayerId, gameId);
        }
        else
        {
            (match, target, first, completedNow) = _registry.AcknowledgeMeshTarget(client.PlayerId, gameId, targetPersona, status);
        }
        var kind = endpointInitialization ? "endpoint-initialization" : target is not null && target.PlayerId == client.PlayerId ? "self-acceptance" : "peer-link";
        _log.LogInformation("MATCHMAKING MESH CONNECTED gameId={GameId} reporter={Player} targetPersona={Target} status={Status} class={Class} first={First} meshCompletedNow={CompletedNow} payload={Payload}",
            gameId, client.PlayerId, targetPersona, status, kind, first, completedNow, TdfDebug.Summarize(payload));
        if (match is null)
        {
            return;
        }
        if (completedNow)
        {
            await Task.Delay(NotificationGap);
            // Only the first full directed-mesh completion releases the roster lifecycle.
            await PublishMeshCompletionAsync(gameId, "blaze-c4-k65");
        }
        var setupEndpointReady = endpointInitialization || (target is not null && target.PlayerId == client.PlayerId && (status ?? 0) == 0);
        if (setupEndpointReady)
        {
            await PublishConnectionValidationAsync(gameId, client.PlayerId);
        }
        await ReevaluateGatesAsync(gameId);
    }

    private async Task HandleSessionUpdateAsync(BlazeClient client, byte[] payload)
    {
        var gameId = TdfReader.FindInteger(payload, "GID") ?? 0;
        var (match, first, _) = _registry.AcknowledgeGameStep(client.PlayerId, gameId, connected: false);
        if (match is null)
        {
            return;
        }
        _registry.StoreHostSessionUpdatePayload(client.PlayerId, gameId, payload);
        if (first)
        {
            _log.LogInformation("MATCHMAKING SESSION UPDATED gameId={GameId} player={Player}", gameId, client.PlayerId);
        }
        await ReevaluateGatesAsync(gameId);
    }

    private async Task HandleAdvanceAsync(BlazeClient client, byte[] payload)
    {
        var gameId = TdfReader.FindInteger(payload, "GID") ?? 0;
        var requested = TdfReader.FindInteger(payload, "GSTA");
        var (current, isHost, meshComplete) = _registry.Read(gameId, match => (match?.State, match?.Host.PlayerId == client.PlayerId, match?.MeshComplete ?? false));
        _log.LogInformation("MATCHMAKING COMMAND3 RX gameId={GameId} player={Player} role={Role} currentState={Current} requestedState={Requested} meshComplete={Mesh}",
            gameId, client.PlayerId, isHost ? "host" : "guest", current, requested, meshComplete);
        if (requested is not { } state)
        {
            return;
        }
        var (advanced, changed) = _registry.AdvanceGameState(client.PlayerId, gameId, state);
        if (advanced is null)
        {
            return;
        }
        if (state == GameState.PreGame)
        {
            await PublishPregameAsync(gameId);
        }
        else if (changed)
        {
            await Task.Delay(NotificationGap);
            await DeliverToAllAsync(advanced, GameManagerNotification.GameStateChange, MatchmakingPayloads.GameStateChange(gameId, state), "GameStateChange");
            if (state == GameState.InGame)
            {
                _relay?.StopHandshakeCapture(gameId);
                _log.LogInformation("GAMEPLAY RELAY TRACE STOP gameId={GameId} reason=in-game", gameId);
            }
        }
        _log.LogInformation("MATCHMAKING COMMAND3 RESULT gameId={GameId} player={Player} requestedState={Requested} resultingState={State} changed={Changed}",
            gameId, client.PlayerId, state, _registry.Read(gameId, match => match?.State), changed);
    }

    private async Task ReevaluateGatesAsync(long gameId)
    {
        await PublishPlatformHostAsync(gameId);
        await PublishSessionUpdateAsync(gameId);
        await PublishPregameAsync(gameId);
    }

    /// <summary>Player states, then join completions, to every player; then the session update and PRE_GAME gates.</summary>
    private async Task PublishMeshCompletionAsync(long gameId, string source)
    {
        if (_registry.MatchFor(gameId) is not { } match)
        {
            return;
        }
        _log.LogInformation("MATCHMAKING MESH LIFECYCLE RELEASE gameId={GameId} source={Source}", gameId, source);
        foreach (var player in match.Players)
        {
            if (await DeliverToAllAsync(match, GameManagerNotification.GamePlayerStateChange, MatchmakingPayloads.GamePlayerStateChange(gameId, player.Client!.PersonaId), "GamePlayerStateChange") == match.Players.Count)
            {
                _registry.MarkPlayerStateNotificationsDelivered(gameId, player.PlayerId);
            }
        }
        var (joinMatch, claimed) = _registry.ClaimJoinNotifications(gameId);
        if (joinMatch is not null && claimed)
        {
            foreach (var player in joinMatch.Players)
            {
                if (await DeliverToAllAsync(joinMatch, GameManagerNotification.PlayerJoinCompleted, _payloads.PlayerJoinCompleted(gameId, player.Client!.PersonaId), "PlayerJoinCompleted") == joinMatch.Players.Count)
                {
                    _registry.MarkJoinNotificationsDelivered(gameId, player.PlayerId);
                }
            }
        }
        await PublishSessionUpdateAsync(gameId);
        await PublishPregameAsync(gameId);
    }

    private async Task PublishConnectionValidationAsync(long gameId, string playerId)
    {
        var (match, claimed) = _registry.ClaimConnectionValidation(gameId, playerId);
        if (match is null || !claimed || match.Entry(playerId) is not { } recipient)
        {
            return;
        }
        if (await recipient.Client!.TrySendAsync(Notify(GameManager, GameManagerNotification.MatchmakingSessionConnectionValidated, _payloads.ConnectionValidated(match, recipient))))
        {
            _log.LogInformation("FIRE2 TX NOTIFY GameManager.MatchmakingSessionConnectionValidated gameId={GameId} player={Player} sessionId={Session}", gameId, playerId, recipient.SessionId);
        }
    }

    /// <summary>
    /// PlatformHostInitialized stays off unless enabled: sent after endpoint self-acceptance it made the
    /// guest disconnect before either directed edge was reported. The gate is always logged.
    /// </summary>
    private async Task PublishPlatformHostAsync(long gameId)
    {
        var (hostReady, initialized, alreadyNotified) = _registry.Read(gameId, match => match is null
            ? (false, string.Empty, false)
            : (match.SessionUpdatedPlayers.Contains(match.Host.PlayerId), string.Join(",", match.MeshInitializedPlayers.Order()), match.PlatformHostInitializedNotified));
        _log.LogInformation("MATCHMAKING NOTIFY71 GATE gameId={GameId} enabled={Enabled} hostReady={HostReady} initialized={Initialized} alreadyNotified={Already}",
            gameId, _switches.PlatformHostNotification, hostReady, initialized, alreadyNotified);
        if (!_switches.PlatformHostNotification)
        {
            return;
        }
        var (match, claimed) = _registry.ClaimPlatformHostInitialized(gameId);
        if (match is not null && claimed)
        {
            await DeliverToAllAsync(match, GameManagerNotification.PlatformHostInitialized, MatchmakingPayloads.PlatformHostInitialized(gameId, match.Host.Client!.PersonaId, 0), "PlatformHostInitialized");
        }
    }

    /// <summary>GameSessionUpdated goes to the guests only.</summary>
    private async Task PublishSessionUpdateAsync(long gameId)
    {
        var (match, hostPayload, claimed) = _registry.ClaimSessionUpdateNotification(gameId);
        if (match is null || hostPayload is null || !claimed)
        {
            return;
        }
        var notification = Notify(GameManager, GameManagerNotification.GameSessionUpdated, MatchmakingPayloads.GameSessionUpdated(gameId, hostPayload));
        foreach (var recipient in match.Players.Skip(1))
        {
            if (await recipient.Client!.TrySendAsync(notification))
            {
                _log.LogInformation("FIRE2 TX NOTIFY GameManager.GameSessionUpdated gameId={GameId} recipient={Player}", gameId, recipient.PlayerId);
            }
        }
    }

    private async Task PublishPregameAsync(long gameId)
    {
        var (match, claimed) = _registry.ClaimPregameIfReady(gameId);
        if (match is null || !claimed)
        {
            return;
        }
        await Task.Delay(NotificationGap);
        await DeliverToAllAsync(match, GameManagerNotification.GameStateChange, MatchmakingPayloads.GameStateChange(gameId, GameState.PreGame), "GameStateChange");
        _log.LogInformation("MATCHMAKING STATE ADVANCED gameId={GameId} state=130 source=pregame-gate", gameId);
        ScheduleInGameProbe(gameId);
    }

    /// <summary>
    /// Diagnostic only: pushes IN_GAME a while after PRE_GAME to locate the boundary FIFA will not cross.
    /// Pushed live it was delivered to both clients and ignored, so the kit-screen gate is client-local.
    /// </summary>
    private void ScheduleInGameProbe(long gameId)
    {
        if (_switches.InGameProbeSeconds <= 0)
        {
            return;
        }
        Observe(Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(_switches.InGameProbeSeconds));
            if (!_registry.ProbeToInGame(gameId) || _registry.MatchFor(gameId) is not { } match)
            {
                _log.LogInformation("MATCHMAKING INGAME PROBE gameId={GameId} skipped", gameId);
                return;
            }
            var delivered = await DeliverToAllAsync(match, GameManagerNotification.GameStateChange, MatchmakingPayloads.GameStateChange(gameId, GameState.InGame), "GameStateChange");
            _log.LogInformation("MATCHMAKING INGAME PROBE gameId={GameId} pushed GSTA=131 delivered={Delivered}/{Count}", gameId, delivered, match.Players.Count);
        }), "in-game-probe", gameId);
    }

    /// <summary>
    /// Background work nobody awaits: a failure is logged instead of being lost as an unobserved task
    /// exception. The in-game probe is an opt-in diagnostic, so no shutdown cancellation is threaded through.
    /// </summary>
    private void Observe(Task work, string operation, long gameId) =>
        _ = work.ContinueWith(task => _log.LogError(task.Exception!.GetBaseException(),
                "MATCHMAKING BACKGROUND FAILED operation={Operation} gameId={GameId}", operation, gameId),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>A relay promotion packet counts as a CONNECTED mesh edge from its source to its target.</summary>
    private async Task AcknowledgeRelayPeerLinkAsync(long gameId, string sourceId, string targetId)
    {
        if (_registry.MatchFor(gameId)?.Entry(targetId) is not { } target)
        {
            return;
        }
        var (match, _, first, completedNow) = _registry.AcknowledgeMeshTarget(sourceId, gameId, target.Client!.PersonaId, MatchmakingRegistry.MeshConnectedStatus);
        _log.LogInformation("MATCHMAKING TRANSPORT EDGE gameId={GameId} source={Source} target={Target} first={First} meshCompletedNow={CompletedNow} meshComplete={Mesh} evidence=relay-promotion-80xx",
            gameId, sourceId, targetId, first, completedNow, match?.MeshComplete ?? false);
        if (completedNow)
        {
            await PublishMeshCompletionAsync(gameId, "relay-promotion-bidirectional");
        }
    }
}
