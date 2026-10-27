using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Aurora21.UltimateTeam.Server.Hosting;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

public sealed class ConnectionLatencyAdmissionTests : IAsyncLifetime
{
    private readonly DefaultConnectionContext _connection = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    [Theory]
    [InlineData(199.999, true)]
    [InlineData(200.0, true)]
    [InlineData(200.001, false)]
    [InlineData(1200.0, false)]
    public void OnlyPingAboveTheLimitIsRefused(double measured, bool expected)
    {
        var reads = 0;
        var gate = new ConnectionLatencyAdmission(200, _ => { reads++; return measured; });
        Assert.Equal(expected, gate.IsAllowed(_connection, out var ping));
        Assert.Equal(measured, ping);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void MissingMeasurementDoesNotRefuseAPlayer()
    {
        var gate = new ConnectionLatencyAdmission(200, _ => null);
        Assert.True(gate.IsAllowed(_connection, out var ping));
        Assert.Null(ping);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidMeasurementIsUnknownRatherThanHighPing(double measured)
    {
        var gate = new ConnectionLatencyAdmission(200, _ => measured);
        Assert.True(gate.IsAllowed(_connection, out var ping));
        Assert.Null(ping);
    }

    [Fact]
    public void ZeroDisablesTheLimitWithoutReadingTheSocket()
    {
        var gate = new ConnectionLatencyAdmission(0, _ => throw new InvalidOperationException("Disabled gate measured ping"));
        Assert.True(gate.IsAllowed(_connection, out var ping));
        Assert.Null(ping);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60_001)]
    public void InvalidLimitIsRejected(int maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectionLatencyAdmission(maximum));
    }

    [Theory]
    [InlineData(1u, 0.001)]
    [InlineData(199_999u, 199.999)]
    [InlineData(200_000u, 200.0)]
    [InlineData(200_001u, 200.001)]
    public void LinuxKernelMicrosecondsAreConvertedToMilliseconds(uint microseconds, double expected)
    {
        var info = new byte[104];
        info[0] = 1;
        Assert.True(BitConverter.TryWriteBytes(info.AsSpan(68, 4), microseconds));
        Assert.Equal(expected, ConnectionLatencyAdmission.ParseLinuxTcpInfo(info));
    }

    [Fact]
    public void ShortClosedOrUnmeasuredTcpInfoIsUnknown()
    {
        Assert.Null(ConnectionLatencyAdmission.ParseLinuxTcpInfo(new byte[71]));
        var info = new byte[104];
        info[0] = 1;
        Assert.Null(ConnectionLatencyAdmission.ParseLinuxTcpInfo(info));
        Assert.True(BitConverter.TryWriteBytes(info.AsSpan(68, 4), 250_000u));
        info[0] = 7; // TCP_CLOSE is not an established game connection.
        Assert.Null(ConnectionLatencyAdmission.ParseLinuxTcpInfo(info));
    }

    [Fact]
    public void MissingSocketFeatureIsUnknown()
    {
        Assert.Null(ConnectionLatencyAdmission.ReadLinuxRoundTripTime(_connection));
    }

    [LinuxFact]
    public async Task LinuxSocketFeatureProvidesRealKernelRoundTripTime()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndPoint!);
        using var server = await listener.AcceptAsync();
        await server.SendAsync(new byte[] { 1 }, SocketFlags.None);
        Assert.Equal(1, await client.ReceiveAsync(new byte[1], SocketFlags.None));
        _connection.Features.Set<IConnectionSocketFeature>(new SocketFeature(server));
        var ping = ConnectionLatencyAdmission.ReadLinuxRoundTripTime(_connection);
        Assert.NotNull(ping);
        Assert.InRange(ping.Value, 0.001, 1000);
    }

    private sealed record SocketFeature(Socket Socket) : IConnectionSocketFeature;
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Requires the Linux TCP_INFO socket API.";
    }
}
