using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Protocol.Fire2;

namespace Aurora21.UltimateTeam.Server.Diagnostics;

/// <summary>Opt-in raw traffic capture for reverse engineering (off unless diagnostics capture is enabled).</summary>
public sealed class PacketCapture : IDisposable, IAsyncDisposable
{
    private readonly string _directory;
    private readonly Channel<Capture> _queue;
    private readonly Task _drain;
    private readonly int _maximumQueuedBytes;
    private readonly Action<string, byte[], bool> _write;
    private long _queuedBytes;
    private long _dropped;
    private long _failed;
    private long _sequence;

    private sealed record Capture(string Path, byte[] Bytes, bool Hex, Func<byte[], string>? Json, long Weight);

    public PacketCapture(GameContext game, bool enabled)
        : this(game.Paths.LogsDirectory, enabled)
    {
    }

    internal PacketCapture(string directory, bool enabled, int capacity = 256,
        int maximumQueuedBytes = 8 * 1024 * 1024, Action<string, byte[], bool>? write = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumQueuedBytes);
        Enabled = enabled;
        _directory = directory;
        _maximumQueuedBytes = maximumQueuedBytes;
        _write = write ?? WriteFiles;
        _queue = Channel.CreateBounded<Capture>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _drain = Task.Run(DrainAsync);
    }

    public bool Enabled { get; }
    public long DroppedCaptures => Interlocked.Read(ref _dropped);
    public long FailedCaptures => Interlocked.Read(ref _failed);

    public Task StopAsync()
    {
        _queue.Writer.TryComplete();
        return _drain;
    }

    public void Dispose() => StopAsync().Wait(TimeSpan.FromSeconds(5));
    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Saves a frame when capture is on, or when <paramref name="evidence"/> marks it as matchmaking evidence kept by default.</summary>
    public string? SaveFrame(Fire2Frame frame, string peer, string kind = "rx", bool evidence = false)
    {
        if (!Enabled && !evidence)
        {
            return null;
        }
        var stamp = Stamp(peer);
        var path = Path.Combine(_directory, $"fire2-{kind}-c{frame.Component}-k{frame.Command}-m{frame.MessageNumber}-{stamp}.bin");
        return Enqueue(path, frame.Raw, hex: true);
    }

    /// <summary>Matchmaking evidence (such as each GameSetup sent), kept whether or not capture is on.</summary>
    public string? SaveEvidence(string name, byte[] bytes) => Enqueue(Path.Combine(_directory, name + ".bin"), bytes, hex: true);

    /// <summary>Capture is diagnostic only: a full disk must never end a Blaze connection while a match forms.</summary>
    private string? Enqueue(string path, ReadOnlySpan<byte> bytes, bool hex = false,
        Func<byte[], string>? json = null, long extraWeight = 0)
    {
        // Include the worker's active capture in the byte budget, not just queued items.
        var weight = bytes.Length + extraWeight;
        long queued;
        do
        {
            queued = Interlocked.Read(ref _queuedBytes);
            if (weight > _maximumQueuedBytes - queued)
            {
                Interlocked.Increment(ref _dropped);
                return null;
            }
        } while (Interlocked.CompareExchange(ref _queuedBytes, queued + weight, queued) != queued);

        var capture = new Capture(path, bytes.ToArray(), hex, json, weight);
        if (_queue.Writer.TryWrite(capture))
        {
            return Path.GetFileName(path); // Accepted for background writing, not a durability promise.
        }
        Interlocked.Add(ref _queuedBytes, -weight);
        Interlocked.Increment(ref _dropped);
        return null;
    }

    private async Task DrainAsync()
    {
        await foreach (var capture in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (capture.Json is { } render)
                    File.WriteAllText(capture.Path, render(capture.Bytes), Encoding.UTF8);
                else
                    _write(capture.Path, capture.Bytes, capture.Hex);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _failed);
            }
            finally
            {
                Interlocked.Add(ref _queuedBytes, -capture.Weight);
            }
        }
    }

    private static void WriteFiles(string path, byte[] bytes, bool hex)
    {
        File.WriteAllBytes(path, bytes);
        if (hex)
            File.WriteAllText(Path.ChangeExtension(path, ".hex.txt"), SpacedHex(bytes), Encoding.UTF8);
    }

    private static string SpacedHex(byte[] bytes) => string.Create(Math.Max(0, bytes.Length * 3 - 1), bytes, (text, data) =>
    {
        const string digits = "0123456789abcdef";
        for (var i = 0; i < data.Length; i++)
        {
            text[i * 3] = digits[data[i] >> 4];
            text[i * 3 + 1] = digits[data[i] & 15];
            if (i + 1 < data.Length) text[i * 3 + 2] = ' ';
        }
    });

    private string Stamp(string peer) =>
        $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Interlocked.Increment(ref _sequence)}-{Sanitize(peer)}";

    public void SaveUnknownRequest(Fire2Frame frame, string summary)
    {
        if (!Enabled) return;
        var component = frame.Component;
        var command = frame.Command;
        var number = frame.MessageNumber;
        var path = Path.Combine(_directory, $"cards-scout-c{component}-k{command}-m{number}-{Stamp("unknown")}.json");
        Enqueue(path, frame.Payload, json: payload =>
        {
            var document = new JsonObject
            {
                ["component"] = (int)component,
                ["command"] = (int)command,
                ["commandHex"] = $"0x{command:x}",
                ["msgNum"] = number,
                ["payloadHex"] = Convert.ToHexString(payload).ToLowerInvariant(),
                ["summary"] = summary,
            };
            return document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }, extraWeight: summary.Length * 2L);
    }

    /// <summary>Best-effort capture: returns the queued filename, or null when the bounded queue refuses it.</summary>
    public string? SaveBytes(string prefix, string peer, ReadOnlySpan<byte> bytes)
    {
        var path = Path.Combine(_directory, $"{prefix}-{Stamp(peer)}.bin");
        return Enqueue(path, bytes);
    }

    private static string Sanitize(string peer) => peer.Replace(':', '_').Replace('[', '_').Replace(']', '_');
}
