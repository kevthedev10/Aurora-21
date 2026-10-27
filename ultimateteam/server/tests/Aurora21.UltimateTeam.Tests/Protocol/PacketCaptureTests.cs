using System.Collections.Concurrent;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Server.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

public sealed class PacketCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutineTelemetryIsOnlyEvidenceWhenFullCaptureIsExplicitlyEnabled(bool fullCapture)
    {
        await using var capture = new PacketCapture("unused", enabled: false);
        var registry = new MatchmakingRegistry(null, NullLogger.Instance);
        var matchmaking = new RivalsMatchmaking(registry, new MatchmakingPayloads(BlazeSettings.Load(), () => 0),
            null, new FeatureSwitches { CaptureAllFrames = fullCapture }, capture, NullLogger.Instance);
        Fire2Frame Request(int command) =>
            Fire2Frame.Parse(Fire2Frame.Build(BlazeComponent.GameManager, command, 1, Fire2MessageType.Message));
        Assert.Equal(fullCapture, matchmaking.IsEvidence(Request(GameManagerCommand.ConnectionTelemetry)));
        Assert.True(matchmaking.IsEvidence(Request(GameManagerCommand.StartMatchmakingScenario)));
    }

    [Fact]
    public async Task SlowDiskCannotBlockRequestsAndTheQueueIsBounded()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var written = new ConcurrentQueue<byte[]>();
        await using var capture = new PacketCapture("unused", enabled: true, capacity: 1,
            write: (_, bytes, _) =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                written.Enqueue(bytes);
            });
        try
        {
            var original = new byte[] { 1, 2, 3 };
            Assert.NotNull(capture.SaveEvidence("first", original));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            original[0] = 99;
            Assert.NotNull(capture.SaveEvidence("second", new byte[] { 4 }));
            Assert.Null(capture.SaveEvidence("overflow", new byte[] { 5 }));
            Assert.Equal(1, capture.DroppedCaptures);
        }
        finally { release.Set(); }
        await capture.StopAsync();
        Assert.Equal(2, written.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, written.First());
    }

    [Fact]
    public async Task ByteBudgetIncludesTheCaptureBeingWritten()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var capture = new PacketCapture("unused", enabled: true, maximumQueuedBytes: 3,
            write: (_, _, _) =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            });
        try
        {
            Assert.NotNull(capture.SaveEvidence("first", new byte[3]));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(capture.SaveEvidence("overflow", new byte[1]));
        }
        finally { release.Set(); }
        await capture.StopAsync();
        Assert.Equal(1, capture.DroppedCaptures);
    }

    [Fact]
    public async Task FailedDiskWritesDoNotKillTheWorker()
    {
        var attempts = 0;
        await using var capture = new PacketCapture("unused", enabled: true,
            write: (_, _, _) => { if (Interlocked.Increment(ref attempts) == 1) throw new IOException("test disk full"); });
        Assert.NotNull(capture.SaveEvidence("first", new byte[] { 1 }));
        Assert.NotNull(capture.SaveEvidence("second", new byte[] { 2 }));
        await capture.StopAsync();
        Assert.Equal(2, attempts);
        Assert.Equal(1, capture.FailedCaptures);
        Assert.Null(capture.SaveEvidence("after-stop", new byte[] { 3 }));
    }

    [Fact]
    public async Task DisabledCaptureStillKeepsEvidenceAndWritesTheSameBytesAndHex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aurora21-capture-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var capture = new PacketCapture(directory, enabled: false);
            var frame = Fire2Frame.Parse(Fire2Frame.Build(4, 1, 1, Fire2MessageType.Message, new byte[] { 0xab, 0xcd }));
            Assert.Null(capture.SaveFrame(frame, "test"));
            var filename = capture.SaveFrame(frame, "test", evidence: true);
            Assert.NotNull(filename);
            await capture.StopAsync();
            var path = Path.Combine(directory, filename);
            Assert.Equal(frame.Raw, await File.ReadAllBytesAsync(path));
            Assert.Equal(string.Join(" ", frame.Raw.Select(value => value.ToString("x2"))),
                await File.ReadAllTextAsync(Path.ChangeExtension(path, ".hex.txt")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
