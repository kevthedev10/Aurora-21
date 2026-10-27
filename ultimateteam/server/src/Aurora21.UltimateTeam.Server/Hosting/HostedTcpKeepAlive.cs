using System.Net.Sockets;
using Aurora21.UltimateTeam.Core.Config;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Hosting;

/// <summary>TCP probes only; no application messages or changes to HTTP/TLS framing.</summary>
internal static class HostedTcpKeepAlive
{
    public static void Configure(ListenOptions endpoint, TcpKeepAliveSettings settings, string listener, ILogger log)
    {
        if (!settings.Enabled) return;
        log.LogInformation("TCP_KEEPALIVE_CONFIG listener={Listener} idleSeconds={Idle} intervalSeconds={Interval} retries={Retries}",
            listener, settings.IdleSeconds, settings.IntervalSeconds, settings.RetryCount);
        endpoint.Use(next => async connection =>
        {
            try
            {
                var socket = connection.Features.Get<IConnectionSocketFeature>()?.Socket
                    ?? throw new PlatformNotSupportedException("Socket transport feature is unavailable.");
                Apply(socket, settings);
                log.LogDebug("TCP_KEEPALIVE_APPLIED listener={Listener}", listener);
            }
            catch (Exception error) when (error is SocketException or PlatformNotSupportedException or ObjectDisposedException)
            {
                // A diagnostic must not prevent login on an unsupported platform. Report that the experiment is inactive.
                log.LogWarning("TCP_KEEPALIVE_FAILED listener={Listener} error={Error}", listener, error.Message);
            }
            await next(connection);
        });
    }

    internal static void Apply(Socket socket, TcpKeepAliveSettings settings)
    {
        if (!settings.Enabled) return;
        // Enable last, so a failed timing option cannot turn on OS-default probes accidentally.
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, settings.IdleSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, settings.IntervalSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, settings.RetryCount);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }
}
