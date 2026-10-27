using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Hosting;

/// <summary>One overall deadline for listener stop, owned work and leases; cleanup is idempotent.</summary>
public sealed class HostShutdown
{
    private readonly object _gate = new();
    private Task<bool>? _task;
    private readonly TimeSpan _timeout;
    private readonly ILogger _log;
    private readonly Action _closeAdmission;
    private readonly Func<CancellationToken, Task> _stopListeners;
    private readonly Func<Task> _stopWork;
    private readonly Func<CancellationToken, Task<bool>> _drain;
    private readonly Action _release;

    public HostShutdown(TimeSpan timeout, ILogger log, Action closeAdmission, Func<CancellationToken, Task> stopListeners,
        Func<Task> stopWork, Func<CancellationToken, Task<bool>> drain, Action release)
    {
        _timeout = timeout;
        _log = log;
        _closeAdmission = closeAdmission;
        _stopListeners = stopListeners;
        _stopWork = stopWork;
        _drain = drain;
        _release = release;
    }

    public Task<bool> StopAsync()
    {
        lock (_gate) return _task ??= RunAsync();
    }

    private async Task<bool> RunAsync()
    {
        using var deadline = new CancellationTokenSource(_timeout);
        Task? work = null;
        var clean = false;
        try
        {
            _closeAdmission();
            _log.LogInformation("SHUTDOWN admission closed; cancelling owned work and stopping listeners");
            // Start both before awaiting either. The listener receives the same overall deadline.
            try { work = _stopWork(); }
            catch (Exception error) { work = Task.FromException(error); }
            Task listeners;
            try { listeners = _stopListeners(deadline.Token); }
            catch (Exception error) { listeners = Task.FromException(error); }
            var all = Task.WhenAll(work, listeners);
            _ = all.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await all.WaitAsync(deadline.Token);
            if (!await _drain(deadline.Token).WaitAsync(deadline.Token))
            {
                _log.LogError("SHUTDOWN_INCOMPLETE club leases did not drain; forced exit");
            }
            else
            {
                clean = true;
            }
        }
        catch (Exception error)
        {
            _log.LogError("SHUTDOWN_INCOMPLETE deadlineMs={Deadline} unresolvedWork={Unresolved} errorType={Error}; forced exit, not a clean drain",
                _timeout.TotalMilliseconds, work is { IsCompleted: false }, error.GetType().Name);
        }
        finally
        {
            try
            {
                _release();
                _log.LogInformation("SHUTDOWN relay allocations released");
            }
            catch (Exception error)
            {
                clean = false;
                _log.LogError("SHUTDOWN_INCOMPLETE relay cleanup errorType={Error}", error.GetType().Name);
            }
        }
        if (clean) _log.LogInformation("SHUTDOWN_CLEAN listeners, owned units/jobs/downloads/opens and leases finished");
        return clean;
    }
}
