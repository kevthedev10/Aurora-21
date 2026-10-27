using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;

namespace Aurora21.UltimateTeam.Server.Hosting;

/// <summary>
/// Admission-only limit on the game connection's kernel-measured, smoothed TCP RTT.
/// No ICMP requests, client-reported timestamps, or extra protocol messages are used.
/// Missing measurements fail open, rather than treating unavailable ping as high ping.
/// </summary>
public sealed class ConnectionLatencyAdmission
{
    private readonly Func<ConnectionContext, double?> _measure;

    public ConnectionLatencyAdmission(int maximumPingMilliseconds, Func<ConnectionContext, double?>? measure = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPingMilliseconds, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPingMilliseconds, 60_000);
        MaximumPingMilliseconds = maximumPingMilliseconds;
        _measure = measure ?? ReadLinuxRoundTripTime;
    }

    public int MaximumPingMilliseconds { get; }

    public bool IsAllowed(ConnectionContext connection, out double? pingMilliseconds)
    {
        pingMilliseconds = null;
        if (MaximumPingMilliseconds == 0) return true;
        pingMilliseconds = _measure(connection);
        if (pingMilliseconds is { } measured && (!double.IsFinite(measured) || measured <= 0))
            pingMilliseconds = null;
        // Exactly the configured limit is allowed; only values above it are refused.
        return pingMilliseconds is null || pingMilliseconds <= MaximumPingMilliseconds;
    }

    internal static double? ReadLinuxRoundTripTime(ConnectionContext connection)
    {
        if (!OperatingSystem.IsLinux() || connection.Features.Get<IConnectionSocketFeature>()?.Socket is not { } socket)
            return null;
        try
        {
            // Linux UAPI: IPPROTO_TCP=6, TCP_INFO=11. GetRawSocketOption intentionally
            // bypasses .NET's cross-platform SocketOptionName translation.
            Span<byte> info = stackalloc byte[104];
            var length = socket.GetRawSocketOption(6, 11, info);
            return ParseLinuxTcpInfo(info[..length]);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    internal static double? ParseLinuxTcpInfo(ReadOnlySpan<byte> info)
    {
        // Stable struct tcp_info prefix (include/uapi/linux/tcp.h): TCP_ESTABLISHED=1,
        // tcpi_rtt is the native-endian uint32 at offset 68, measured in microseconds.
        if (info.Length < 72 || info[0] != 1) return null;
        var microseconds = BitConverter.ToUInt32(info.Slice(68, 4));
        return microseconds == 0 ? null : microseconds / 1000d;
    }
}
