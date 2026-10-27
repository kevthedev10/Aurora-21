using System.Diagnostics;
using Aurora21.UltimateTeam.Core.Config;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Core.Services.Market;

/// <summary>Single-owner periodic expiry and AI-listing upkeep for the server-wide market.</summary>
public sealed class MarketMaintenanceJob
{
    private readonly FutServices _fut;
    private readonly ILogger _log;
    private readonly TimeSpan? _fixedInterval;

    /// <param name="interval">A fixed interval (tests); otherwise rules/market.json's, read again before each wait so a content reload applies.</param>
    public MarketMaintenanceJob(FutServices fut, TimeSpan? interval = null)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.MarketMaintenance");
        _fixedInterval = interval;
        ValidateInterval(Interval);
    }

    public static void ValidateInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromDays(1))
        {
            throw new RuntimeSetupException("rules/market.json maintenanceIntervalSeconds must be greater than zero and at most 86400.");
        }
    }

    /// <summary>The wait before the next attempt. A reload validated the rules value before publishing it.</summary>
    public TimeSpan Interval => _fixedInterval ?? TimeSpan.FromSeconds(_fut.Game.Rules.Market.MaintenanceIntervalSeconds);

    /// <summary>Runs one attempt. A second server holding the advisory lock produces a skipped result.</summary>
    public async Task<MarketMaintenanceRun> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var content = _fut.Game.Live.Pin();
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await _fut.Market.RunMaintenanceOnceAsync(cancellationToken);
            timer.Stop();
            // Only passes that changed the market reach the default Information log; the job runs every
            // interval and the file log is not yet size-bounded (AUD-OPS-001).
            if (result.Acquired)
            {
                _log.Log(result.Expired > 0 || result.Seeded > 0 ? LogLevel.Information : LogLevel.Debug,
                    "MARKET_MAINTENANCE durationMs={Duration} expired={Expired} seeded={Seeded}",
                    timer.ElapsedMilliseconds, result.Expired, result.Seeded);
            }
            else
            {
                _log.LogDebug("MARKET_MAINTENANCE_SKIPPED durationMs={Duration} reason=advisory-lock-owned",
                    timer.ElapsedMilliseconds);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            timer.Stop();
            _log.LogError(error, "MARKET_MAINTENANCE_FAILED durationMs={Duration}", timer.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>Waits one interval between attempts and exits promptly when shutdown is requested.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = Interval;
            var next = _fut.Game.Time.GetUtcNow() + interval;
            _log.LogDebug("MARKET_MAINTENANCE_NEXT at={Next:O}", next);
            try
            {
                await Task.Delay(interval, _fut.Game.Time, cancellationToken);
                await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // RunOnceAsync already emitted the exception details; the next interval retries.
            }
        }
    }
}
