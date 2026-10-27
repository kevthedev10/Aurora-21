using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Server.Diagnostics;
using Aurora21.UltimateTeam.Server.Http;
using Aurora21.UltimateTeam.Server.Hosting;

namespace Aurora21.UltimateTeam.Server.Blaze;

/// <summary>
/// Port 44321: a persistent Blaze Fire2 session (with a one-shot HTTP fallback). Each connection
/// belongs to one player's club for its whole lifetime. The session polls every 100 ms so an HTTP
/// match settlement can push UserUpdated immediately. In the session identity mode a connection
/// starts with a verified Login, or resumes a still-open connection's session with that login's
/// key (<see cref="BlazeSessionKeys"/>).
/// </summary>
public sealed class BlazeConnectionHandler : ConnectionHandler
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The scheduling gap between a game report acknowledgement and its result notification.</summary>
    private static readonly TimeSpan ReportResultDelay = TimeSpan.FromMilliseconds(20);

    private readonly PlayerRegistry _players;
    private readonly RivalsMatchmaking _matchmaking;
    private readonly PacketCapture _capture;
    private readonly TlsProbeOptions _tlsProbe;
    private readonly PlayerPresence? _presence;
    private readonly ConnectionLatencyAdmission? _latencyAdmission;
    private readonly ILogger _log;
    private readonly object _activeGate = new();
    /// <summary>Per shared player: the connection holding the player's one Blaze slot.</summary>
    private readonly Dictionary<string, BlazeClient> _activePlayers = new(StringComparer.Ordinal);
    private readonly BlazeSessionKeys _sessionKeys = new();

    public BlazeConnectionHandler(PlayerRegistry players, RivalsMatchmaking matchmaking, PacketCapture capture,
        TlsProbeOptions? tlsProbe = null, PlayerPresence? presence = null, ConnectionLatencyAdmission? latencyAdmission = null)
    {
        _players = players;
        _matchmaking = matchmaking;
        _capture = capture;
        _tlsProbe = tlsProbe ?? TlsProbeOptions.Off;
        _presence = presence;
        _latencyAdmission = latencyAdmission;
        _log = players.Local.Game.Logger("UltimateTeam.Blaze");
    }

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var peer = connection.RemoteEndPoint?.ToString() ?? "unknown";
        var input = connection.Transport.Input;
        var output = connection.Transport.Output;
        var aborted = connection.ConnectionClosed;
        var peerAddress = Ipv4Of(connection.RemoteEndPoint);
        var serverAddress = Ipv4Of(connection.LocalEndPoint);

        BlazeClient? client = null;
        string? activePlayer = null;
        IDisposable? online = null;
        try
        {
            Fire2Frame? verifiedLogin = null;
            string? resumeKey = null;
            PlayerIdentity player;
            if (_players.NativeSessions is not null)
            {
                var verified = await AuthenticateSessionAsync(input, output, peer, aborted);
                if (verified is null)
                {
                    return;
                }
                (player, verifiedLogin, resumeKey) = verified.Value;
            }
            else
            {
                player = FutEndpoint.PlayerFor(peerAddress, _players);
            }
            // Admission only, after the TLS handshake (so TCP_INFO has an RTT sample) and before the club lease.
            // A Blaze resume is never refused: the player is reconnecting into a live match after a network
            // stall, exactly when the smoothed RTT is worst, and refusing it would end that match.
            if (resumeKey is null && _latencyAdmission is { MaximumPingMilliseconds: > 0 } admission)
            {
                if (!admission.IsAllowed(connection, out var ping))
                {
                    _log.LogWarning("BLAZE LATENCY REJECT player={Player} peer={Peer} pingMs={PingMs:0.###} limitMs={LimitMs}",
                        player.Id, peer, ping, admission.MaximumPingMilliseconds);
                    connection.Abort(new ConnectionAbortedException("Game connection exceeds the server latency limit."));
                    return;
                }
                if (ping is null)
                    _log.LogWarning("BLAZE LATENCY UNAVAILABLE player={Player} peer={Peer} limitMs={LimitMs}; allowing connection without a measurement",
                        player.Id, peer, admission.MaximumPingMilliseconds);
                else
                    _log.LogInformation("BLAZE LATENCY ACCEPT player={Player} peer={Peer} pingMs={PingMs:0.###} limitMs={LimitMs}",
                        player.Id, peer, ping, admission.MaximumPingMilliseconds);
            }
            using var lease = await _players.AcquireAsync(player, aborted);
            var session = lease.Session;
            if (verifiedLogin is null)
            {
                using var firstReadTimeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                firstReadTimeout.CancelAfter(IdleTimeout);
                var first = await input.ReadAsync(firstReadTimeout.Token);
                if (first.Buffer.IsEmpty && first.IsCompleted)
                {
                    return;
                }
                var looksLikeHttp = RawHttpExchange.LooksLikeHttp(first.Buffer);
                input.AdvanceTo(first.Buffer.Start);
                if (looksLikeHttp)
                {
                    await RawHttpExchange.HandleAsync(input, output, session.Router.HandleAsync, session.Game.Sid, aborted);
                    return;
                }
            }
            var sessionKey = resumeKey ?? BlazeSessionKeys.NewKey();
            client = new BlazeClient(session, new BlazeResponses(session.Fut, _tlsProbe) { SessionKey = sessionKey }, output,
                peerAddress?.ToString() ?? string.Empty, serverAddress?.ToString() ?? "127.0.0.1");
            var owner = new BlazeSessionKeys.Owner(client, connection.Abort, aborted);
            BlazeSessionKeys.Owner? resumed = null;
            if (resumeKey is not null)
            {
                resumed = _sessionKeys.TakeOver(resumeKey, owner);
                if (resumed is null)
                {
                    _log.LogWarning("Blaze resume refused from {Peer}: session key {Key} closed or was taken during the handover",
                        peer, BlazeSessionKeys.Fingerprint(resumeKey));
                    return;
                }
                client.MachineId = resumed.Client.MachineId;
            }
            if (_players.IsShared)
            {
                lock (_activeGate)
                {
                    if (resumed is null && _activePlayers.ContainsKey(session.Player.Id))
                    {
                        _log.LogWarning("Refusing second Blaze connection for shared player {Player}; peer/NAT identity is already active", session.Player.Id);
                        return;
                    }
                    _activePlayers[session.Player.Id] = client;
                    activePlayer = session.Player.Id;
                }
            }
            if (resumed is not null)
            {
                // The new connection takes over before the old one is closed, so the old close no longer ends the game.
                var (searches, games) = _matchmaking.Rebind(resumed.Client, client);
                resumed.Abort();
                _log.LogInformation("Blaze session resumed for player {Player}: {OldPeer} -> {Peer} key={Key} searches={Searches} games={Games}",
                    session.Player.Id, resumed.Client.PeerAddress, peer, BlazeSessionKeys.Fingerprint(sessionKey), searches, games);
            }
            else if (verifiedLogin is not null)
            {
                _sessionKeys.Register(sessionKey, owner);
            }
            if (verifiedLogin is not null)
            {
                // Only a verified login counts as a present player (launcher health, friends list, panel).
                online = _presence?.Enter(session.Player.Id, connection.Abort, peerAddress?.ToString());
                await HandleFrameAsync(client, verifiedLogin, peer, aborted);
            }
            await RunSessionAsync(client, input, peer, aborted);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("44321 client {Peer} timed out", peer);
        }
        catch (ConnectionResetException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            online?.Dispose();
            if (client is not null)
            {
                var grace = _matchmaking.ResumeGrace;
                if (_matchmaking.InLiveGame(client) && _sessionKeys.Linger(client, grace))
                {
                    // Mid-match: keep the game, the relay and the key for a resume on a new connection. If none takes the
                    // match over in time, it ends as any lost connection does; after a resume both calls are no-ops.
                    _log.LogInformation("Blaze link lost for player {Player} in a live match; holding it {Seconds} s for a resume",
                        client.PlayerId, grace.TotalSeconds);
                    _ = ReleaseAfterGraceAsync(client, grace);
                }
                else
                {
                    _sessionKeys.Release(client);
                    _matchmaking.Disconnect(client);
                }
            }
            if (activePlayer is not null)
            {
                lock (_activeGate)
                {
                    // A resumed connection may already hold the slot; only the holder frees it.
                    if (_activePlayers.TryGetValue(activePlayer, out var holder) && ReferenceEquals(holder, client))
                    {
                        _activePlayers.Remove(activePlayer);
                    }
                }
            }
        }
    }

    private async Task ReleaseAfterGraceAsync(BlazeClient client, TimeSpan grace)
    {
        try
        {
            await Task.Delay(grace);
        }
        finally
        {
            _sessionKeys.Release(client);
            _matchmaking.Disconnect(client);
        }
    }

    private static IPAddress? Ipv4Of(EndPoint? endpoint) =>
        endpoint is IPEndPoint ip ? ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address : null;

    /// <summary>
    /// Answer only stateless setup requests until a Login carries a registered code, or a request carries the Fire2 metadata
    /// SKEY of a still-open connection's session (a resume, which skips PreAuth and Login). The returned frame is the login or
    /// the first resumed request; the key is set for a resume.
    /// </summary>
    private async Task<(PlayerIdentity Player, Fire2Frame First, string? ResumeKey)?> AuthenticateSessionAsync(
        PipeReader input, PipeWriter output, string peer, CancellationToken aborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        timeout.CancelAfter(IdleTimeout);
        var setup = new BlazeResponses(_players.Local.Fut, _tlsProbe);
        var setupFrames = 0;
        while (true)
        {
            var result = await input.ReadAsync(timeout.Token);
            var buffer = result.Buffer;
            while (Fire2Frame.TryRead(ref buffer, out var frame))
            {
                if (++setupFrames > 32)
                {
                    _log.LogWarning("Blaze login refused from {Peer}: too many unauthenticated frames", peer);
                    input.AdvanceTo(buffer.Start, buffer.End);
                    return null;
                }
                if (frame!.Type == Fire2MessageType.Ping)
                {
                    await output.WriteAsync(Fire2Frame.Build(frame.Component, frame.Command, frame.MessageNumber, Fire2MessageType.PingReply), timeout.Token);
                    continue;
                }
                if (frame.Type != Fire2MessageType.Message)
                {
                    input.AdvanceTo(buffer.Start, buffer.End);
                    return null;
                }
                if (frame.Metadata.Length > 0 && TdfReader.FindString(frame.Metadata, "SKEY") is { Length: > 0 } resumeKey)
                {
                    if (_sessionKeys.Find(resumeKey) is not { } owner)
                    {
                        _log.LogWarning("Blaze resume refused from {Peer}: session key {Key} is unknown or its connection is closed",
                            peer, BlazeSessionKeys.Fingerprint(resumeKey));
                        input.AdvanceTo(buffer.Start, buffer.End);
                        return null;
                    }
                    input.AdvanceTo(buffer.Start, buffer.Start);
                    return (owner.Client.Session.Player, frame, resumeKey);
                }
                if (frame.Component == BlazeComponent.Authentication && frame.Command == AuthenticationCommand.Login)
                {
                    var player = BlazeLoginIdentity.PlayerFor(frame.Payload, _players.NativeSessions!);
                    input.AdvanceTo(buffer.Start, buffer.Start);
                    if (player is null)
                    {
                        _log.LogWarning("Blaze login refused from {Peer}: AUTH is missing, malformed, expired, or unknown", peer);
                        return null;
                    }
                    _log.LogInformation("Blaze login verified for player {Player}", player.Id);
                    return (player, frame, null);
                }
                if (frame.Component != BlazeComponent.Util || frame.Command is not
                    (UtilCommand.Ping or UtilCommand.PreAuth or UtilCommand.FetchClientConfig))
                {
                    _log.LogWarning("Blaze prelogin command refused from {Peer}: c={Component} cmd={Command}", peer, frame.Component, frame.Command);
                    input.AdvanceTo(buffer.Start, buffer.End);
                    return null;
                }
                var reply = await setup.RespondAsync(frame, timeout.Token);
                await output.WriteAsync(Fire2Frame.Build(frame.Component, frame.Command, frame.MessageNumber, Fire2MessageType.Reply, reply.Payload), timeout.Token);
            }
            input.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return null;
            }
        }
    }

    private async Task RunSessionAsync(BlazeClient client, PipeReader input, string peer, CancellationToken aborted)
    {
        var idleDeadline = DateTime.UtcNow + IdleTimeout;
        using var poller = new PeriodicTimer(PollInterval);
        using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var pollTask = Task.Run(async () =>
        {
            while (await poller.WaitForNextTickAsync(pollCancellation.Token).ConfigureAwait(false))
            {
                input.CancelPendingRead();
            }
        }, pollCancellation.Token);

        try
        {
            while (true)
            {
                var result = await input.ReadAsync(aborted);
                var buffer = result.Buffer;
                if (!result.IsCanceled)
                {
                    idleDeadline = DateTime.UtcNow + IdleTimeout;
                }

                while (true)
                {
                    Fire2Frame? frame;
                    try
                    {
                        if (!Fire2Frame.TryRead(ref buffer, out frame))
                        {
                            break;
                        }
                    }
                    catch (FormatException error)
                    {
                        var saved = _capture.SaveBytes("blaze44321-unparsed", peer, buffer.ToArray());
                        _log.LogError("44321 Fire2 framing failed; saved {File}: {Error}", saved, error.Message);
                        return;
                    }
                    await HandleFrameAsync(client, frame!, peer, aborted);
                }
                input.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCanceled)
                {
                    await PushPendingUserUpdateAsync(client, " (idle push)", aborted);
                    if (DateTime.UtcNow >= idleDeadline)
                    {
                        _log.LogInformation("44321 client {Peer} timed out", peer);
                        return;
                    }
                }
                if (result.IsCompleted)
                {
                    return;
                }
            }
        }
        finally
        {
            pollCancellation.Cancel();
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task HandleFrameAsync(BlazeClient client, Fire2Frame frame, string peer, CancellationToken aborted)
    {
        var saved = _capture.SaveFrame(frame, peer, evidence: _matchmaking.IsEvidence(frame));
        _log.LogDebug("FIRE2 RX c={Component} cmd={Command}(0x{CommandHex:X}) msg={Message} type={Type} payload={Payload} metadata={Metadata} saved={Saved}",
            frame.Component, frame.Command, frame.Command, frame.MessageNumber, (int)frame.Type, frame.Payload.Length, frame.Metadata.Length, saved);

        // A connected player is an active one: its launch code keeps working for FUT re-auth and a Blaze re-login, as a
        // fresh Origin code would in retail (a Rivals or Weekend League stretch makes little FUT traffic).
        _players.NativeSessions?.Touch(client.PlayerId);
        if (frame.Type == Fire2MessageType.Ping)
        {
            await client.SendAsync(Fire2Frame.Build(frame.Component, frame.Command, frame.MessageNumber, Fire2MessageType.PingReply), aborted);
            return;
        }
        if (frame.Type != Fire2MessageType.Message)
        {
            return;
        }
        // One content revision per frame (every club context shares the live content), never per connection.
        using var content = _players.Local.Game.Live.Pin();
        if (frame.Component == BlazeComponent.Authentication)
        {
            if (_players.NativeSessions is not null && frame.Command == AuthenticationCommand.Login &&
                BlazeLoginIdentity.PlayerFor(frame.Payload, _players.NativeSessions)?.Id != client.PlayerId)
            {
                _log.LogWarning("Blaze login refused on an already bound connection for player {Player}", client.PlayerId);
                throw new InvalidDataException("Blaze AUTH changed or expired during this connection.");
            }
            // Which credential the login carries, redacted (ultimateteam/native-hosting-investigation.md).
            _log.LogInformation("AUTH EVIDENCE blaze peer={Peer} c={Component} cmd=0x{Command:X} fields: {Fields}",
                peer, frame.Component, frame.Command, Core.Services.Auth.CredentialEvidence.DescribeTdf(frame.Payload, client.Session.Game));
        }

        await _matchmaking.BeforeReplyAsync(client, frame);
        var reply = _matchmaking.Respond(client, frame) ?? await client.Responses.RespondAsync(frame, aborted);
        await client.SendAsync(Fire2Frame.Build(frame.Component, frame.Command, frame.MessageNumber, Fire2MessageType.Reply, reply.Payload), aborted);
        _log.LogInformation("FIRE2 TX {Label} c={Component} cmd={Command} msg={Message} payload={Payload}",
            reply.Label, frame.Component, frame.Command, frame.MessageNumber, reply.Payload.Length);
        await _matchmaking.AfterReplyAsync(client, frame);

        if (frame.Component == BlazeComponent.GameReporting && frame.Command is GameReportingCommand.SubmitOfflineGameReport or GameReportingCommand.SubmitGameReport)
        {
            var reportId = TdfReader.FindInteger(frame.Payload, "GRID") ?? 0;
            // The async completion follows the RPC acknowledgement after a small scheduling gap. The ONLINE report (28/1) has
            // the same contract: without the result FIFA freezes about 45 s at full time until its report times out.
            await Task.Delay(ReportResultDelay, aborted);
            await SendNotificationAsync(client, BlazeResponses.GameReportingResult(reportId),
                $" reportId={reportId} online={frame.Command == GameReportingCommand.SubmitGameReport}", aborted);
        }
        else
        {
            await PushPendingUserUpdateAsync(client, string.Empty, aborted);
        }

        if (reply.FollowUp == BlazeFollowUp.LoginNotifications)
        {
            foreach (var notification in client.Responses.LoginNotifications())
            {
                await SendNotificationAsync(client, notification, string.Empty, aborted);
            }
        }

        if (reply.Notification is { } values)
        {
            await SendNotificationAsync(client, values, string.Empty, aborted);
        }

        if (reply.FollowUp == BlazeFollowUp.Unknown)
        {
            var summary = TdfDebug.Summarize(frame.Payload);
            _log.LogWarning("Unhandled Blaze request c={Component} cmd={Command}(0x{CommandHex:X}) msg={Message} payload={Payload} :: {Summary}",
                frame.Component, frame.Command, frame.Command, frame.MessageNumber, frame.Payload.Length, summary);
            _capture.SaveUnknownRequest(frame, summary);
        }
    }

    /// <summary>Sends UserUpdated when an HTTP match settlement asked this player's session to refresh.</summary>
    private async ValueTask PushPendingUserUpdateAsync(BlazeClient client, string context, CancellationToken aborted)
    {
        if (client.Session.Game.UserSessionRefresh.TryConsume())
        {
            await SendNotificationAsync(client, client.Responses.UserUpdated(), " POST-MATCH" + context, aborted);
        }
    }

    private async ValueTask SendNotificationAsync(BlazeClient client, BlazeNotification notification, string detail, CancellationToken aborted)
    {
        await client.SendAsync(Fire2Frame.Build(notification.Component, notification.Command, 0, Fire2MessageType.Notification, notification.Payload), aborted);
        _log.LogInformation("FIRE2 TX NOTIFY {Label} c={Component} cmd={Command} payload={Payload}{Detail}",
            notification.Label, notification.Component, notification.Command, notification.Payload.Length, detail);
    }
}
