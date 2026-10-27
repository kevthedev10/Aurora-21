using Npgsql;
using Aurora21.UltimateTeam.Core.Config;

namespace Aurora21.UltimateTeam.Core.Data;

public enum RelationalBootstrapResult { Installed, AlreadyInstalled }

/// <summary>Installs or validates the relational database schema.</summary>
public static class RelationalSchemaBootstrap
{
    public const long AdvisoryLockKey = 202100219901;
    // Retain the historical lock key while older server processes may still be running.
    private const long FormerSchemaMigrationLockKey = 2021_0021_0001;

    public static Task<RelationalBootstrapResult> InstallFreshAsync(NpgsqlDataSource source,
        CancellationToken cancellationToken = default) => InstallFreshAsync(source, afterFile: null, cancellationToken);

    /// <summary>Installs the relational schema without changing retained public-schema tables.</summary>
    public static Task<RelationalBootstrapResult> InstallForRuntimeAsync(NpgsqlDataSource source,
        CancellationToken cancellationToken = default) => InstallAsync(source, null, cancellationToken, allowLegacy: true);

    // Fault hook runs inside the same transaction, only through the friend test assembly.
    internal static async Task<RelationalBootstrapResult> InstallFreshAsync(NpgsqlDataSource source,
        Func<string, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? afterFile, CancellationToken cancellationToken)
        => await InstallAsync(source, afterFile, cancellationToken, allowLegacy: false);

    private static async Task<RelationalBootstrapResult> InstallAsync(NpgsqlDataSource source,
        Func<string, NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? afterFile,
        CancellationToken cancellationToken, bool allowLegacy)
    {
        // Validate all bytes before opening a connection or running any DDL.
        var package = RelationalSetupPackage.Load();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await WaitForLockAsync(connection, transaction, AdvisoryLockKey, cancellationToken);
        // Also serialize against today's legacy initializer. Its gate must stay held
        // until this install commits, otherwise two initializers could both see empty storage.
        await WaitForLockAsync(connection, transaction, FormerSchemaMigrationLockKey, cancellationToken);
        var readiness = await RelationalStorageReadiness.InspectAsync(connection, transaction, package, cancellationToken, allowLegacy);
        if (readiness.State == RelationalStorageState.RelationalReady)
        {
            await RelationalCatalogMetadataMigration.ApplyAsync(connection, transaction, cancellationToken, afterFile);
            await transaction.CommitAsync(cancellationToken);
            return RelationalBootstrapResult.AlreadyInstalled;
        }
        if (readiness.State != RelationalStorageState.Empty && !(allowLegacy && readiness.State == RelationalStorageState.Legacy))
            throw new RuntimeSetupException($"Cannot install relational storage: {readiness.Detail}");
        await RelationalStorageReadiness.ScalarAsync(connection, transaction,
            "SET LOCAL standard_conforming_strings = on", cancellationToken);
        await RelationalStorageReadiness.ScalarAsync(connection, transaction,
            "CREATE SCHEMA IF NOT EXISTS ultimateteam;" + package.LedgerSql, cancellationToken);
        foreach (var file in package.Files)
        {
            await using var command = new NpgsqlCommand(file.Sql, connection, transaction) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (afterFile is not null) await afterFile(file.Path, connection, transaction, cancellationToken);
        }
        await RelationalStorageReadiness.ScalarAsync(connection, transaction, """
            INSERT INTO ultimateteam.setup_artifact_versions(version_code, package_sha256)
            VALUES(@version, @hash)
            """, cancellationToken, ("version", package.VersionCode), ("hash", package.PackageSha256));
        await RelationalCatalogMetadataMigration.ApplyAsync(connection, transaction, cancellationToken, afterFile);
        await transaction.CommitAsync(cancellationToken);
        return RelationalBootstrapResult.Installed;
    }

    // A waiter sits here for the holder's whole install (every setup file, up to 120 s each), so the wait has no
    // command timeout of its own; the caller's token still cancels it, and the lock is freed if the holder disconnects.
    private static async Task WaitForLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
