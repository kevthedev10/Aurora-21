using Npgsql;
using Aurora21.UltimateTeam.Core.Helpers;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>The Phase 3 retry policy for deadlocks and serialization failures, shared by both transaction runners.</summary>
internal static class DeadlockRetry
{
    private const int MinimumPauseMilliseconds = 20;
    private const int MaximumPauseMilliseconds = 120;

    /// <summary>SQLSTATE 40P01 (deadlock detected) or 40001 (serialization failure).</summary>
    public static bool IsRetryable(PostgresException error) =>
        error.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure;

    /// <summary>A bounded random pause before the next attempt.</summary>
    public static TimeSpan NextPause() =>
        TimeSpan.FromMilliseconds(GameRandom.Shared.NextInt64(MinimumPauseMilliseconds, MaximumPauseMilliseconds + 1));
}
