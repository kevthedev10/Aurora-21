using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Protocol.Lsx;
using Aurora21.UltimateTeam.Server.Diagnostics;
using Aurora21.UltimateTeam.Server.Http;

namespace Aurora21.UltimateTeam.Server.Lsx;

/// <summary>
/// Local replacement for EA App's Origin LSX socket (port 3216). The server speaks first with a
/// plaintext challenge; after the mutual challenge every frame is hex AES-128-ECB XML, NUL-terminated.
/// </summary>
public sealed class LsxConnectionHandler : ConnectionHandler
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private const int RequestLogChars = 6000;
    private const int HandshakeLogChars = 3000;

    private readonly PlayerRegistry _players;
    private readonly LsxSettings _settings;
    private readonly PacketCapture _capture;
    private readonly ILogger _log;

    public LsxConnectionHandler(PlayerRegistry players, PacketCapture capture)
    {
        _players = players;
        _settings = LsxSettings.Load();
        _capture = capture;
        _log = players.Local.Game.Logger("UltimateTeam.Lsx");
    }

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var peer = connection.RemoteEndPoint?.ToString() ?? "unknown";
        var input = connection.Transport.Input;
        var output = connection.Transport.Output;
        var peerAddress = connection.RemoteEndPoint is System.Net.IPEndPoint endpoint ? endpoint.Address : null;
        var localTest = _players.LocalSessionTest && peerAddress is not null && System.Net.IPAddress.IsLoopback(peerAddress);
        using var lease = await _players.AcquireAsync(localTest ? _players.Local.Player : FutEndpoint.PlayerFor(peerAddress, _players), connection.ConnectionClosed);
        var responder = new LsxResponder(lease.Session.Game, _settings);
        _log.LogInformation("Origin LSX client connected from {Peer}", peer);
        connection.Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionSocketFeature>()?.Socket
            .SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, true);

        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
            handshake.CancelAfter(HandshakeTimeout);

            var challengeKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var challenge = responder.Settings.Challenge;
            await SendPlainAsync(output,
                LsxXml.Event("EALS", LsxXml.Element("Challenge", new[] { ("key", challengeKey), ("version", challenge.Version), ("build", challenge.Build) })),
                handshake.Token);

            var frame = await ReadFrameAsync(input, handshake.Token);
            if (frame is null)
            {
                _log.LogWarning("LSX client disconnected before ChallengeResponse");
                return;
            }
            var text = Encoding.UTF8.GetString(frame);
            _log.LogInformation("LSX plaintext handshake request: {Request}", Truncate(text, HandshakeLogChars));
            var request = LsxXml.ParseRequest(text);
            if (request.Name != "ChallengeResponse" || request.Operation is null)
            {
                _log.LogWarning("Expected ChallengeResponse, received {Name}", request.Name);
                return;
            }
            var response = request.Attribute("response");
            var clientKey = request.Attribute("key");
            if (response.Length < 2)
            {
                _log.LogWarning("LSX ChallengeResponse had no usable response attribute");
                return;
            }

            // The response must be AES(primary key 00..0f) of our challenge key; we answer the
            // client's own key the same way. Protocol 3 then derives a session key from the
            // first two ASCII characters of our accept response; protocol 2 keeps the primary key.
            var version = (string?)request.Operation.Attribute("version") ?? "3";
            var primary = new OriginCrypto();
            var expected = primary.EncryptToHex(challengeKey);
            if (!string.Equals(response, expected, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("LSX challenge response did not validate; expected={Expected} got={Actual}", expected, response);
                return;
            }
            if (clientKey.Length == 0)
            {
                _log.LogWarning("LSX ChallengeResponse is missing the client key attribute");
                return;
            }
            var accept = primary.EncryptToHex(clientKey);
            OriginCrypto session;
            uint seed;
            switch (version)
            {
                case "2":
                    seed = 0;
                    session = new OriginCrypto();
                    break;
                case "3":
                    seed = OriginCrypto.SeedFromResponse(accept);
                    session = new OriginCrypto(seed);
                    break;
                default:
                    _log.LogWarning("Unsupported LSX ChallengeResponse version {Version}", version);
                    return;
            }

            await SendPlainAsync(output, LsxXml.Response(request.Id, LsxXml.Element("ChallengeAccepted", new[] { ("response", accept) }), "EALS"), handshake.Token);
            _log.LogInformation("LSX challenge accepted for {Peer}; protocol={Version} session seed={Seed}", peer, version, seed);

            // FIFA 21 checks the signed-in state before it attempts Blaze authentication.
            await SendEncryptedAsync(output, session, LsxXml.Event("EbisuSDK", LsxXml.Element(responder.Settings.LoginEvent, Array.Empty<(string, object)>())),
                connection.ConnectionClosed);

            // An idle game window must not look like an LSX disconnect: no timeout once established.
            _log.LogInformation("LSX session entering idle-safe receive loop for {Peer}", peer);
            while (true)
            {
                frame = await ReadFrameAsync(input, connection.ConnectionClosed);
                if (frame is null)
                {
                    break;
                }
                if (frame.Length == 0)
                {
                    continue;
                }
                string requestXml;
                try
                {
                    requestXml = session.Decrypt(Convert.FromHexString(Encoding.ASCII.GetString(frame)));
                }
                catch (Exception error) when (error is FormatException or CryptographicException or DecoderFallbackException)
                {
                    var saved = _capture.SaveBytes("lsx-decode-fail", peer, frame);
                    _log.LogError("LSX encrypted frame could not be decoded/decrypted ({File}): {Error}", saved, error.Message);
                    break;
                }
                _log.LogInformation("LSX decrypted request: {Request}", Truncate(requestXml, RequestLogChars));
                var reply = responder.Respond(requestXml);
                _log.LogInformation("LSX encrypted response: {Response}", Truncate(reply, RequestLogChars));
                await SendEncryptedAsync(output, session, reply, connection.ConnectionClosed);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Origin LSX client {Peer} timed out/closed", peer);
        }
        catch (Exception error) when (error is IOException or ConnectionResetException)
        {
            _log.LogInformation("Origin LSX client {Peer} disconnected", peer);
        }
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private static async Task<byte[]?> ReadFrameAsync(PipeReader input, CancellationToken cancellation)
    {
        while (true)
        {
            var result = await input.ReadAsync(cancellation);
            var buffer = result.Buffer;
            var position = buffer.PositionOf((byte)0);
            if (position is not null)
            {
                var frame = buffer.Slice(0, position.Value).ToArray();
                input.AdvanceTo(buffer.GetPosition(1, position.Value));
                return frame;
            }
            input.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return null;
            }
        }
    }

    private static async Task SendPlainAsync(PipeWriter output, string xml, CancellationToken cancellation)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        await output.WriteAsync(bytes.Append((byte)0).ToArray(), cancellation);
    }

    private static async Task SendEncryptedAsync(PipeWriter output, OriginCrypto crypto, string xml, CancellationToken cancellation)
    {
        var hex = Encoding.ASCII.GetBytes(crypto.EncryptToHex(xml));
        await output.WriteAsync(hex.Append((byte)0).ToArray(), cancellation);
    }
}
