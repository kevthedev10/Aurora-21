using System.Text;
using System.Threading.Channels;

namespace Aurora21.UltimateTeam.Core.Helpers;

/// <summary>
/// Appends text lines to a file from one background task, so a caller never waits on disk I/O or a shared lock.
/// Accepted lines keep their enqueue order. Bounded buffers reserve separate room for important lines,
/// and flush in timed batches. With a size cap, the file
/// rotates to <c>{path}.1</c> (replacing an older one) before a line would take it past the cap.
/// Lines still queued when the process is killed are lost; <see cref="Dispose"/> drains the queue first.
/// </summary>
public sealed class BackgroundLineWriter : IDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    // Long enough for an exception with its full stack trace.
    private const int MaximumLineCharacters = 64 * 1024;
    private readonly object _gate = new();
    private readonly Queue<Line> _lines = new();
    private readonly Queue<Line> _important = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private sealed record Line(long Sequence, string Text);
    private readonly int _capacity;
    private readonly int _importantCapacity;
    private readonly int _maximumQueuedCharacters;
    private readonly int _maximumImportantCharacters;
    private readonly TimeSpan _flushInterval;
    private int _queuedCharacters;
    private int _importantCharacters;
    private long _sequence;
    private bool _stopped;
    private long _dropped;
    private long _droppedImportant;
    private long _unreportedDrops;
    private long _unreportedImportantDrops;
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly Task _drain;
    private StreamWriter _writer;
    private long _bytes;

    /// <param name="maxBytes">Rotate before the file would exceed this size; 0 never rotates.</param>
    public BackgroundLineWriter(string path, long maxBytes = 0, int capacity = 32768, int importantCapacity = 4096,
        int maximumQueuedCharacters = 16 * 1024 * 1024, int maximumImportantCharacters = 4 * 1024 * 1024,
        TimeSpan? flushInterval = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(importantCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumQueuedCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumImportantCharacters);
        _capacity = capacity;
        _importantCapacity = importantCapacity;
        _maximumQueuedCharacters = maximumQueuedCharacters;
        _maximumImportantCharacters = maximumImportantCharacters;
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(200);
        ArgumentOutOfRangeException.ThrowIfLessThan(_flushInterval, TimeSpan.Zero);
        _path = path;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = Open(out _bytes);
        _drain = Task.Run(DrainAsync);
    }

    public string FilePath => _path;
    public long DroppedLines => Interlocked.Read(ref _dropped);
    public long DroppedImportantLines => Interlocked.Read(ref _droppedImportant);

    /// <summary>Queues a line without waiting. A line queued after <see cref="Dispose"/> is dropped.</summary>
    public bool WriteLine(string line, bool important = false)
    {
        // Bound each entry as well as the total count/characters; diagnostics cannot retain
        // an arbitrarily large response or exception when the disk stops keeping up.
        if (line.Length > MaximumLineCharacters) line = line[..MaximumLineCharacters] + " [truncated]";
        lock (_gate)
        {
            if (_stopped) return false;
            var queue = important ? _important : _lines;
            var characters = important ? _importantCharacters : _queuedCharacters;
            var limit = important ? _maximumImportantCharacters : _maximumQueuedCharacters;
            if (queue.Count >= (important ? _importantCapacity : _capacity) || line.Length > limit - characters)
            {
                if (important)
                {
                    Interlocked.Increment(ref _droppedImportant);
                    Interlocked.Increment(ref _unreportedImportantDrops);
                }
                else
                {
                    Interlocked.Increment(ref _dropped);
                    Interlocked.Increment(ref _unreportedDrops);
                }
                return false;
            }
            queue.Enqueue(new Line(++_sequence, line));
            if (important) _importantCharacters += line.Length;
            else _queuedCharacters += line.Length;
            _wake.Writer.TryWrite(0);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            _wake.Writer.TryComplete();
        }
        _drain.Wait(DrainTimeout);
        // The drain owns the writer, including disposal. A timeout must not close a file
        // underneath an in-progress background write.
    }

    private StreamWriter Open(out long length)
    {
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        length = stream.Length;
        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var _ in _wake.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await Task.Delay(_flushInterval).ConfigureAwait(false);
                do
                {
                    // Flush even when a busy producer never lets the queue become empty.
                    for (var count = 0; count < 256 && TakeLine() is { } line; count++)
                    {
                        try { Write(line); }
                        catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
                        {
                            // A logging file cannot report its own failure; keep draining.
                        }
                    }
                    try
                    {
                        var dropped = Interlocked.Exchange(ref _unreportedDrops, 0);
                        var important = Interlocked.Exchange(ref _unreportedImportantDrops, 0);
                        if (dropped + important > 0)
                            Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} WARNING LOG BUFFER skipped routine={dropped} important={important} lines (bounded buffer full)");
                        _writer.Flush();
                    }
                    catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
                    {
                    }
                } while (HasLines());
            }
        }
        finally
        {
            try { _writer.Dispose(); }
            catch (IOException) { }
        }
    }

    private string? TakeLine()
    {
        lock (_gate)
        {
            if (_lines.Count == 0 && _important.Count == 0) return null;
            var important = _lines.Count == 0 ||
                            (_important.Count > 0 && _important.Peek().Sequence < _lines.Peek().Sequence);
            var line = (important ? _important : _lines).Dequeue().Text;
            if (important) _importantCharacters -= line.Length;
            else _queuedCharacters -= line.Length;
            return line;
        }
    }

    private bool HasLines()
    {
        lock (_gate) return _lines.Count > 0 || _important.Count > 0;
    }

    private void Write(string line)
    {
        var size = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        if (_maxBytes > 0 && _bytes > 0 && _bytes + size > _maxBytes)
        {
            _writer.Dispose();
            File.Move(_path, _path + ".1", overwrite: true);
            _writer = Open(out _bytes);
        }
        _writer.WriteLine(line);
        _bytes += size;
    }
}
