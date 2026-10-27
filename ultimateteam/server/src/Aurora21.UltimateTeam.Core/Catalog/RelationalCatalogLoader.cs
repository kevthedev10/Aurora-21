using System.Data;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>Catalog startup work before a club is opened; does not initialize or switch storage.</summary>
public static class RelationalCatalogLoader
{
    public static Task<RelationalCatalogSnapshot> LoadAsync(NpgsqlDataSource source,
        CancellationToken cancellationToken = default) => LoadAsync(source, observer: null, cancellationToken);

    public static Task<RelationalCatalogSnapshot> LoadForRuntimeAsync(NpgsqlDataSource source,
        CancellationToken cancellationToken = default) => LoadAsync(source, null, cancellationToken, allowLegacy: true);

    /// <summary>
    /// The catalog, the store pack configuration and the rules documents (migration 073) from one read transaction.
    /// Loading packs on their own connection doubled the connections every start-up needs, which exhausted shared
    /// memory when many runtimes existed at once.
    /// </summary>
    public static Task<(RelationalCatalogSnapshot Snapshot, Rules.PackRules Packs, IReadOnlyDictionary<string, string> Rules,
        Rules.ModeRewardTables RewardTables, SbcContentRows SbcSets)> LoadContentAsync(
        NpgsqlDataSource source, CancellationToken cancellationToken = default, bool allowLegacy = false) =>
        LoadBothAsync(source, null, cancellationToken, allowLegacy);

    internal static async Task<RelationalCatalogSnapshot> LoadAsync(NpgsqlDataSource source,
        Action<string>? observer, CancellationToken cancellationToken, bool allowLegacy = false)
    {
        var package = RelationalSetupPackage.Load();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await RelationalStorageReadiness.ScalarAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken);
        var status = await RelationalStorageReadiness.InspectAsync(connection, transaction, package, cancellationToken, allowLegacy);
        if (status.State != RelationalStorageState.RelationalReady)
            throw new RuntimeSetupException($"Cannot load relational catalog: {status.Detail}");
        if (!(bool)(await RelationalStorageReadiness.ScalarAsync(connection, transaction,
            "SELECT to_regclass('ultimateteam.catalog_compatibility_metadata') IS NOT NULL", cancellationToken))!)
            throw new RuntimeSetupException("Catalog metadata upgrade is pending; run relational bootstrap before loading the catalog.");
        var migrationVersion = Convert.ToInt32(await RelationalStorageReadiness.ScalarAsync(connection, transaction,
            "SELECT COALESCE(max(version),0) FROM ultimateteam.schema_migrations", cancellationToken));
        if (migrationVersion != RelationalCatalogMetadataMigration.CurrentVersion)
            throw new RuntimeSetupException("Catalog upgrade is pending; run relational bootstrap before loading the catalog.");
        var unit = new UnitOfWork(connection, transaction, 0, TimeProvider.System, observer, cancellationToken);
        var snapshot = await unit.LoadCatalogSnapshotAsync();
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    internal static async Task<(RelationalCatalogSnapshot Snapshot, Rules.PackRules Packs, IReadOnlyDictionary<string, string> Rules,
        Rules.ModeRewardTables RewardTables, SbcContentRows SbcSets)> LoadBothAsync(
        NpgsqlDataSource source, Action<string>? observer, CancellationToken cancellationToken, bool allowLegacy)
    {
        var package = RelationalSetupPackage.Load();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await RelationalStorageReadiness.ScalarAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken);
        var status = await RelationalStorageReadiness.InspectAsync(connection, transaction, package, cancellationToken, allowLegacy);
        if (status.State != RelationalStorageState.RelationalReady)
            throw new RuntimeSetupException($"Cannot load relational catalog: {status.Detail}");
        var migrationVersion = Convert.ToInt32(await RelationalStorageReadiness.ScalarAsync(connection, transaction,
            "SELECT COALESCE(max(version),0) FROM ultimateteam.schema_migrations", cancellationToken));
        if (migrationVersion != RelationalCatalogMetadataMigration.CurrentVersion)
            throw new RuntimeSetupException("Catalog upgrade is pending; run relational bootstrap before loading the catalog.");
        var unit = new UnitOfWork(connection, transaction, 0, TimeProvider.System, observer, cancellationToken);
        var snapshot = await unit.LoadCatalogSnapshotAsync();
        var packs = (await StorePackCatalog.LoadAsync(unit, cancellationToken)).Rules;
        var rules = await Rules.RuleRows.DocumentsAsync(unit);
        var tables = await Data.Repositories.ModeRewardRepository.TablesAsync(unit);
        var sbcSets = await SbcContentRows.ReadAsync(unit);
        await transaction.CommitAsync(cancellationToken);
        return (snapshot, packs, rules, tables, sbcSets);
    }
}
