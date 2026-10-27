using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// Most tests start from a copy of a database installed once per run (<see cref="PostgresTestCluster.CreateInstalledDatabase"/>).
/// These keep the paths that the copy skips covered, and check the copy is the same as a fresh install.
/// </summary>
public sealed class TestDatabaseTemplateTests
{

    [Fact]
    public async Task InstalledCatalogHasOnlyTheConfirmedRttfCards()
    {
        var copy = PostgresTestCluster.CreateInstalledDatabase();
        try
        {
            await using var source = NpgsqlDataSource.Create(copy);
            await using var command = source.CreateCommand("""
                SELECT definition_id, native_resource_id FROM ultimateteam.item_definitions
                WHERE definition_id IN (20381, 20520, 21475, 21561) ORDER BY definition_id
                """);
            await using var rows = await command.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(21475L, rows.GetInt64(0));
            Assert.Equal(67297667L, rows.GetInt64(1));
            Assert.True(await rows.ReadAsync());
            Assert.Equal(21561L, rows.GetInt64(0));
            Assert.Equal(67317672L, rows.GetInt64(1));
            Assert.False(await rows.ReadAsync());
        }
        finally
        {
            PostgresTestCluster.DropDatabase(copy);
        }
    }

    [Fact]
    public async Task RuntimeInstallsIntoAnEmptyDatabaseAndServesTheClub()
    {
        using var rt = TestRuntime.Create(connectionString: PostgresTestCluster.CreateDatabase(), marketTarget: 0);

        // The runtime installed every migration a template copy has.
        var installed = await rt.Game.ReadAsync(w => w.ScalarAsync<int>("SELECT max(version) FROM ultimateteam.schema_migrations"), CancellationToken.None);
        var copy = PostgresTestCluster.CreateInstalledDatabase();
        try
        {
            await using var source = NpgsqlDataSource.Create(copy);
            await using var command = source.CreateCommand("SELECT max(version) FROM ultimateteam.schema_migrations");
            Assert.Equal((int)(await command.ExecuteScalarAsync())!, installed);
        }
        finally
        {
            PostgresTestCluster.DropDatabase(copy);
        }
        Assert.NotNull(rt.Fut.Club.Hub()["watchlist"]);
    }

    [Fact]
    public async Task InstalledCopyHasTheSameRowsAsAFreshInstall()
    {
        var fresh = PostgresTestCluster.CreateDatabase();
        var copy = PostgresTestCluster.CreateInstalledDatabase();
        try
        {
            await using (var source = NpgsqlDataSource.Create(fresh))
            {
                Assert.Equal(RelationalBootstrapResult.Installed, await RelationalSchemaBootstrap.InstallFreshAsync(source));
            }
            await using (var source = NpgsqlDataSource.Create(copy))
            {
                Assert.Equal(RelationalBootstrapResult.AlreadyInstalled, await RelationalSchemaBootstrap.InstallFreshAsync(source));
            }
            var freshRows = await RowFingerprintsAsync(fresh);
            Assert.NotEmpty(freshRows);
            Assert.Equal(freshRows, await RowFingerprintsAsync(copy));
        }
        finally
        {
            PostgresTestCluster.DropDatabase(fresh);
            PostgresTestCluster.DropDatabase(copy);
        }
    }

    /// <summary>Per table: row count and an order-independent hash of every row, leaving out the install-time timestamps.</summary>
    private static async Task<SortedDictionary<string, string>> RowFingerprintsAsync(string connectionString)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        // Columns defaulting to now() (applied_at, imported_at and so on) differ between two installs by design.
        var tables = new List<(string Table, string Row)>();
        await using (var command = source.CreateCommand("""
            SELECT c.table_name, '(' || string_agg('t.' || quote_ident(c.column_name), ', ' ORDER BY c.ordinal_position) || ')'
            FROM information_schema.columns c JOIN pg_tables p ON p.schemaname = c.table_schema AND p.tablename = c.table_name
            WHERE c.table_schema = 'ultimateteam' AND c.data_type <> 'timestamp with time zone'
            GROUP BY c.table_name ORDER BY c.table_name
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) tables.Add((reader.GetString(0), reader.GetString(1)));
        }
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (table, row) in tables)
        {
            await using var command = source.CreateCommand(
                $"SELECT count(*) || ':' || COALESCE(md5(string_agg(md5({row}::text), ',' ORDER BY md5({row}::text))), '') FROM ultimateteam.{table} t");
            result[table] = (string)(await command.ExecuteScalarAsync())!;
        }
        return result;
    }
}
