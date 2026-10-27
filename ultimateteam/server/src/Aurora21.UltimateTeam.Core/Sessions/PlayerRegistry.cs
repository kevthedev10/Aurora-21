using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Core.Sessions;

/// <summary>One player's open club: its context, services and router.</summary>
public sealed class PlayerSession
{
    private static long _useSequence;

    private int _handlers;
    private long _lastActiveTicks;
    private long _lastUse;

    internal PlayerSession(FutServices fut, PlayerRegistry registry)
    {
        Fut = fut;
        fut.Players = registry;
        Router = new FutRouter(fut);
        Touch();
    }

    public PlayerIdentity Player => Game.Player;
    public GameContext Game => Fut.Game;
    public FutServices Fut { get; }
    public FutRouter Router { get; }

    /// <summary>How many connections or requests currently use this club; a club in use is never closed.</summary>
    public int Handlers => Volatile.Read(ref _handlers);

    internal long LastActiveTicks => Interlocked.Read(ref _lastActiveTicks);

    /// <summary>Orders sessions by last use (the clock is too coarse to order uses within one tick).</summary>
    internal long LastUse => Interlocked.Read(ref _lastUse);

    internal void Enter()
    {
        Interlocked.Increment(ref _handlers);
        Touch();
    }

    internal void Leave()
    {
        Interlocked.Decrement(ref _handlers);
        Touch();
    }

    private void Touch()
    {
        Interlocked.Exchange(ref _lastActiveTicks, Environment.TickCount64);
        Interlocked.Exchange(ref _lastUse, Interlocked.Increment(ref _useSequence));
    }
}

/// <summary>A player session in use; disposing it releases the club for idle reaping.</summary>
public readonly struct PlayerLease : IDisposable
{
    internal PlayerLease(PlayerSession session)
    {
        Session = session;
        session.Enter();
    }

    public PlayerSession Session { get; }

    public void Dispose() => Session.Leave();
}

public sealed record PlayerRegistryOptions
{
    /// <summary>Clubs idle this long (with no request using them) are closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The most clubs kept open; the least recently used idle club is closed to make room.</summary>
    public int MaxOpenClubs { get; init; } = 20;

    /// <summary>How many clubs may be opened (and maintained) at the same time.</summary>
    public int ConcurrentOpens { get; init; } = 4;

    public TimeSpan ReapInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether a player's new club waits for FIFA to create it (the first-run country and club screens) instead of being
    /// seeded when it opens. The hosted profile sets it; see ultimateteam/onboarding.md.
    /// </summary>
    public bool NativeClubCreation { get; init; }

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxOpenClubs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxOpenClubs, 10000);
        ArgumentOutOfRangeException.ThrowIfLessThan(ConcurrentOpens, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ConcurrentOpens, 32);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(IdleTimeout, TimeSpan.FromDays(1));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReapInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReapInterval, TimeSpan.FromDays(1));
    }
}

/// <summary>
/// The players' clubs. A local server has exactly one, the configured player's, which is always
/// open. A shared server opens each player's club on first use (once, however many requests arrive
/// together) and closes clubs that stay idle. Catalogs and rules are shared by every club.
/// </summary>
public sealed class PlayerRegistry : IDisposable
{
    private readonly FutServices _server;
    private readonly PlayerRegistryOptions _options;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, PlayerSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lazy<Task<PlayerSession>>> _opening = new(StringComparer.Ordinal);
    private readonly HashSet<long> _openingPersonas = [];
    private readonly Dictionary<long, TaskCompletionSource> _resetting = [];
    private readonly SemaphoreSlim _openSlots;
    private readonly Timer? _reaper;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stopToken;
    private Task _cancellationTask = Task.CompletedTask;
    private bool _accepting = true;
    private Task? _stopTask;
    private bool _disposed;

    /// <summary>A local registry: the server's own club is the only one.</summary>
    public PlayerRegistry(FutServices server) : this(server, new PlayerRegistryOptions(), shared: false)
    {
    }

    public PlayerRegistry(FutServices server, PlayerRegistryOptions options, bool shared)
    {
        options.Validate();
        _stopToken = _stopping.Token;
        _server = server;
        _options = options;
        IsShared = shared;
        _log = server.Game.Logger("UltimateTeam.Players");
        _openSlots = new SemaphoreSlim(options.ConcurrentOpens);
        Local = new PlayerSession(server, this);
        _sessions[Local.Player.Id] = Local;
        if (shared)
        {
            _reaper = new Timer(_ => ReapIdle(), null, options.ReapInterval, options.ReapInterval);
        }
    }

    /// <summary>Whether requests are assigned to players by identity (otherwise every request is the local player's).</summary>
    public bool IsShared { get; }

    /// <summary>Live online matches, when the server runs matchmaking.</summary>
    public IMatchRoster? MatchRoster { get; set; }

    /// <summary>
    /// Verified player sessions in the session identity mode (a shared registry); null in the single and peer-IP modes.
    /// </summary>
    public NativeSessions? NativeSessions { get; set; }

    /// <summary>Allows replacement LSX to use the sole configured player in an explicit loopback test.</summary>
    public bool LocalSessionTest { get; set; }

    /// <summary>The server's own configured club.</summary>
    public PlayerSession Local { get; }

    public int OpenCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// The local player's session on a local server; the identity's own club on a shared one. Concurrent requests
    /// for a club that is not open share one open, which the registry owns once it completes; cancelling one request
    /// stops only that request's wait.
    /// </summary>
    public async Task<PlayerLease> AcquireAsync(PlayerIdentity player, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task<PlayerSession>? openingTask = null;
            Task? resetTask = null;
            lock (_gate)
            {
                ThrowIfStopped();
                var local = !IsShared || player.IsSinglePlayer;
                if (_resetting.TryGetValue(local ? Local.Player.PersonaId : player.PersonaId, out var resetting))
                    resetTask = resetting.Task;
                else if (local)
                    return new PlayerLease(Local);
                else if (_sessions.TryGetValue(player.Id, out var open))
                {
                    return new PlayerLease(open);
                }
                else
                {
                    if (!_opening.TryGetValue(player.Id, out var opening))
                    {
                        opening = new Lazy<Task<PlayerSession>>(() => OpenAndAdoptAsync(player), LazyThreadSafetyMode.ExecutionAndPublication);
                        _opening[player.Id] = opening;
                        _openingPersonas.Add(player.PersonaId);
                    }
                    openingTask = opening.Value;
                }
            }
            if (resetTask is not null)
            {
                await resetTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            var session = await openingTask!.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                ThrowIfStopped();
                if (_resetting.ContainsKey(player.PersonaId)) continue;
                // Capacity eviction may close a club that nobody has leased yet; open it again in that case.
                if (_sessions.TryGetValue(player.Id, out var current) && current == session)
                {
                    return new PlayerLease(session);
                }
            }
        }
    }

    /// <summary>
    /// Opens a club and adds it to the open sessions, closing the least recently used idle club when full. The
    /// asynchronous database open and startup maintenance share bounded owner slots; requester cancellation
    /// cancels only a wait, whereas owner shutdown cancels queued and active opens.
    /// </summary>
    private async Task<PlayerSession> OpenAndAdoptAsync(PlayerIdentity player)
    {
        PlayerSession? session = null;
        PlayerSession? evicted = null;
        try
        {
            await _openSlots.WaitAsync(_stopToken).ConfigureAwait(false);
            try
            {
                session = await OpenAsync(player).ConfigureAwait(false);
            }
            finally
            {
                _openSlots.Release();
            }
            lock (_gate)
            {
                _opening.Remove(player.Id);
                _openingPersonas.Remove(player.PersonaId);
                ThrowIfStopped();
                if (_sessions.Count >= _options.MaxOpenClubs)
                {
                    evicted = _sessions.Values.Where(candidate => candidate != Local && candidate.Handlers == 0).MinBy(candidate => candidate.LastUse)
                              ?? throw new InvalidOperationException("Aurora 21 player capacity reached");
                    _sessions.Remove(evicted.Player.Id);
                }
                _sessions[player.Id] = session;
            }
        }
        catch
        {
            lock (_gate)
            {
                _opening.Remove(player.Id);
                _openingPersonas.Remove(player.PersonaId);
            }
            session?.Game.Dispose();
            throw;
        }
        evicted?.Game.Dispose();
        return session;
    }

    /// <summary>Whether this player's club is open right now; it returns no session, so nothing can outlive a close.</summary>
    public bool IsOpen(string playerId)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(playerId);
        }
    }

    /// <summary>
    /// A lease on this player's club when it is already open, or null (a lookup never opens a club). The
    /// lease is taken under the registry lock, so idle reaping and capacity eviction, which only close
    /// clubs without handlers, cannot close the club while the caller reads it.
    /// </summary>
    public PlayerLease? TryAcquireExisting(string playerId)
    {
        lock (_gate)
        {
            if (!_accepting || _disposed)
            {
                return null;
            }
            return _sessions.TryGetValue(playerId, out var session) && !_resetting.ContainsKey(session.Player.PersonaId)
                ? new PlayerLease(session) : null;
        }
    }

    /// <summary>Exclusively resets an idle club. New leases wait; reaping cannot close its context during the reset.</summary>
    public ClubResetLease BeginClubReset(long personaId)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            var session = _sessions.Values.SingleOrDefault(s => s.Player.PersonaId == personaId);
            if (_resetting.ContainsKey(personaId) || _openingPersonas.Contains(personaId) || session?.Handlers > 0)
                throw new InvalidOperationException("The player must leave FIFA 21 before resetting their club.");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _resetting.Add(personaId, completion);
            session?.Enter();
            return new ClubResetLease(this, personaId, session, completion);
        }
    }

    public sealed class ClubResetLease : IDisposable
    {
        private readonly PlayerRegistry _registry;
        private readonly long _persona;
        private readonly PlayerSession? _session;
        private readonly TaskCompletionSource _completion;
        private int _disposed;

        internal ClubResetLease(PlayerRegistry registry, long persona, PlayerSession? session, TaskCompletionSource completion)
        {
            _registry = registry;
            _persona = persona;
            _session = session;
            _completion = completion;
        }

        public void RefreshAccount(ClubAccount account) => _session?.Game.RefreshResetAccount(account);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_registry._gate)
            {
                _session?.Leave();
                _registry._resetting.Remove(_persona);
            }
            _completion.TrySetResult();
        }
    }

    /// <summary>
    /// Waits until no request or connection holds any club, for at most <paramref name="timeout"/>.
    /// Returns false (after logging the clubs still held) when the timeout passes first.
    /// </summary>
    public async Task<bool> WaitForDrainAsync(TimeSpan timeout, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var interval = pollInterval ?? DrainPollInterval;
        while (true)
        {
            List<PlayerSession> held;
            lock (_gate)
            {
                held = _sessions.Values.Where(session => session.Handlers > 0).ToList();
            }
            if (held.Count == 0)
            {
                return true;
            }
            if (Environment.TickCount64 >= deadline)
            {
                foreach (var session in held)
                {
                    _log.LogWarning("SHUTDOWN_LEASE_HELD persona={PersonaId} handlers={Handlers}", session.Player.PersonaId, session.Handlers);
                }
                return false;
            }
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(50);

    private async Task<PlayerSession> OpenAsync(PlayerIdentity player)
    {
        GameContext? game = null;
        try
        {
            game = await _server.Game.ForPlayerAsync(player, _stopToken, _options.NativeClubCreation);
            var session = new PlayerSession(await FutServices.CreateAsync(game, _stopToken), this);
            _log.LogInformation("Opened club {ClubId} for player persona {PersonaId}", game.ClubId, player.PersonaId);
            return session;
        }
        catch
        {
            game?.Dispose();
            throw;
        }
    }

    /// <summary>Closes clubs no connection has used for the idle timeout.</summary>
    public void ReapIdle()
    {
        var cutoff = Environment.TickCount64 - (long)_options.IdleTimeout.TotalMilliseconds;
        List<PlayerSession> idle;
        lock (_gate)
        {
            idle = _sessions.Values.Where(session => session != Local && session.Handlers == 0 && session.LastActiveTicks < cutoff).ToList();
            foreach (var session in idle)
            {
                _sessions.Remove(session.Player.Id);
            }
        }
        foreach (var session in idle)
        {
            session.Game.Dispose();
            _log.LogInformation("Closed idle club for player persona {PersonaId}", session.Player.PersonaId);
        }
    }

    private void ThrowIfStopped()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_accepting)
        {
            throw new OperationCanceledException("Player registry is stopping.", _stopToken);
        }
    }

    public void RequestStop()
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                return;
            }
            _accepting = false;
            _reaper?.Dispose();
            _cancellationTask = _stopping.CancelAsync();
        }
    }

    /// <summary>Closes admission, cancels owner opens (including semaphore waiters), and awaits them.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            RequestStop();
            return _stopTask ??= AwaitOpensAsync(_opening.Values.Where(entry => entry.IsValueCreated).Select(entry => entry.Value).ToArray());
        }
    }

    private async Task AwaitOpensAsync(Task<PlayerSession>[] opens)
    {
        try
        {
            await Task.WhenAll(opens.Cast<Task>().Append(_cancellationTask)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested)
        {
            // All owner tasks have finished. Ordinary owner cancellation is a clean stop.
        }
        finally
        {
            _stopping.Dispose();
            _openSlots.Dispose();
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
        _reaper?.Dispose();
        List<PlayerSession> sessions;
        lock (_gate)
        {
            // An open still running is disposed by OpenAndAdoptAsync instead of being adopted.
            _disposed = true;
            sessions = _sessions.Values.Where(session => session != Local).ToList();
            _sessions.Clear();
        }
        foreach (var session in sessions)
        {
            session.Game.Dispose();
        }
        // StopAsync disposes the semaphore only after every started/queued open has finished.
    }
}
