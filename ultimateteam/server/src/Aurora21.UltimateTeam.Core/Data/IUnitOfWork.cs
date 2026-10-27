using Npgsql;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>
/// One club's database work: the statements the repositories run, bound to one connection and (for
/// durable work) one transaction. <c>@club</c> in a statement is bound to <see cref="ClubId"/>.
/// </summary>
/// <remarks>
/// <see cref="UnitOfWork"/> leases a pooled connection and runs every statement asynchronously.
/// An open club owns logical state only; the runner returns the connection after each operation.
/// Repository methods are extensions of this interface, so services call them as <c>await uow.CreditsAsync()</c>.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>The relational item store bound after catalog loading.</summary>
    RelationalItemCompatibilityStore RelationalItems { get; }
    /// <summary>The club that <c>@club</c> refers to.</summary>
    long ClubId { get; }

    long NowSeconds { get; }

    long NowMilliseconds { get; }

    /// <summary>Whether a transaction is open; row locks are only meaningful inside one.</summary>
    bool InTransaction { get; }

    /// <summary>
    /// Club row locks this transaction already holds, keyed by club id; true when the lock is exclusive.
    /// A club-scoped unit shares its root's record, because the lock belongs to the transaction rather
    /// than to the scope. Rolling back to a savepoint releases row locks taken inside it, so
    /// <see cref="SavepointAsync"/> rewinds this record with the transaction.
    /// </summary>
    IDictionary<long, bool> HeldClubLocks { get; }

    CancellationToken CancellationToken { get; }

    ValueTask<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters);

    ValueTask<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params (string Name, object? Value)[] parameters);

    ValueTask<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters);

    /// <summary>
    /// Runs one recoverable step. A failed step rolls back to its savepoint and rethrows, so the caller may
    /// handle the error and continue the surrounding transaction.
    /// </summary>
    ValueTask SavepointAsync(Func<ValueTask> step);
}
