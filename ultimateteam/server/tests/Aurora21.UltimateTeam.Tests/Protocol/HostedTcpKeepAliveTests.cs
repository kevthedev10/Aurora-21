using System.Net;
using System.Net.Sockets;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

public sealed class HostedTcpKeepAliveTests
{
    [Fact]
    public void DisabledSettingsDoNotTouchTheSocket()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Dispose(); // Accessing any option would throw.
        HostedTcpKeepAlive.Apply(socket, new TcpKeepAliveSettings(0, 10, 3));
    }

    [Fact]
    public async Task KestrelAcceptedSocketHasConfiguredProbesBeforeTheNextHandler()
    {
        var measured = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint =>
        {
            HostedTcpKeepAlive.Configure(endpoint, new TcpKeepAliveSettings(30, 10, 3), "test", NullLogger.Instance);
            endpoint.Run(async connection =>
            {
                try
                {
                    var socket = connection.Features.Get<IConnectionSocketFeature>()!.Socket;
                    measured.SetResult(new[]
                    {
                        (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!,
                        (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!,
                        (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!,
                        (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!,
                    });
                    await connection.Transport.Output.WriteAsync(new byte[] { 42 });
                }
                catch (Exception error) { measured.TrySetException(error); }
            });
        }));
        await using var app = builder.Build();
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, address.Port);
            Assert.Equal(new[] { 1, 30, 10, 3 }, await measured.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var received = new byte[1];
            Assert.Equal(1, await client.ReceiveAsync(received, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(42, received[0]);
        }
        finally { await app.StopAsync(); }
    }
}
