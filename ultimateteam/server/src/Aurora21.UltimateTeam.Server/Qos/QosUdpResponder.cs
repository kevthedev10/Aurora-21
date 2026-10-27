using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Qos;

/// <summary>DirtySDK QoS UDP responder (ping, firewall and bandwidth probes) requested after Blaze PreAuth.</summary>
public sealed class QosUdpResponder : IDisposable
{
    private const uint PingRequestId = 1;
    private const uint PingRequestSecret = 0;
    private const uint BandwidthRequestId = 1234;
    private const uint BandwidthRequestSecret = 5678;
    private const int FirewallProbeSize = 8;
    private const int MinimumProbeSize = 12;
    private const int ShortPingSize = 18;
    private const int PingHeaderSize = 20;
    private const int PingResponseSize = 30;
    private const int BandwidthResponseSize = 1200;

    // Fixed values the local responder reports in every bandwidth reply.
    private const uint BandwidthServerTimestamp = 123456789;
    private const ushort BandwidthServerPort = 3659;

    private readonly UdpClient _socket;
    private readonly ILogger _log;

    public QosUdpResponder(IPAddress host, int port, ILogger log)
    {
        _socket = new UdpClient(new IPEndPoint(host, port));
        if (OperatingSystem.IsWindows())
        {
            // Ignore ICMP port-unreachable resets so one vanished client cannot stop the loop.
            const int SioUdpConnReset = unchecked((int)0x9800000C);
            _socket.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
        }
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            var reply = BuildResponse(received.Buffer, received.RemoteEndPoint);
            if (reply is null)
            {
                _log.LogInformation("QOS17502 UDP dropped unrecognized {Bytes}B datagram from {Peer}", received.Buffer.Length, received.RemoteEndPoint);
                continue;
            }
            try
            {
                await _socket.SendAsync(reply.Value.Response, received.RemoteEndPoint, cancellation);
                _log.LogInformation("QOS17502 UDP {Kind} {In}B -> {Out}B for {Peer}", reply.Value.Kind, received.Buffer.Length, reply.Value.Response.Length, received.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    public static (byte[] Response, string Kind)? BuildResponse(byte[] data, IPEndPoint peer)
    {
        var address = peer.Address.IsIPv4MappedToIPv6 ? peer.Address.MapToIPv4() : peer.Address;
        if (address.AddressFamily != AddressFamily.InterNetwork || peer.Port is <= 0 or > 0xFFFF)
        {
            return null;
        }

        if (data.Length == FirewallProbeSize)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(data);
            var secret = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
            return id == BandwidthRequestId && secret == BandwidthRequestSecret ? (data.ToArray(), "firewall") : null;
        }
        if (data.Length < MinimumProbeSize)
        {
            return null;
        }

        var requestId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        var requestSecret = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        if (data.Length is ShortPingSize or PingHeaderSize && requestId == PingRequestId && requestSecret == PingRequestSecret)
        {
            var response = new byte[PingResponseSize];
            data.AsSpan(0, Math.Min(data.Length, PingHeaderSize)).CopyTo(response);
            address.GetAddressBytes().CopyTo(response, PingHeaderSize);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(PingHeaderSize + 4), (ushort)peer.Port);
            return (response, "ping");
        }
        if (data.Length == BandwidthResponseSize && requestId == BandwidthRequestId && requestSecret == BandwidthRequestSecret)
        {
            var response = new byte[BandwidthResponseSize];
            data.AsSpan(0, MinimumProbeSize).CopyTo(response);
            var requestCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
            var totalCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(12), unchecked(requestCount + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(16), totalCount);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(20), BandwidthServerTimestamp);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(24), BandwidthServerPort);
            return (response, "bandwidth");
        }
        return null;
    }

    public void Dispose() => _socket.Dispose();
}
