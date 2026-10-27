using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class DatabasePreflightTests
{
    internal static DatabaseSettings Settings => DatabaseSettings.From(null,
        JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "config.example.json")))!["database"]!.AsObject());

    [Theory]
    [InlineData(30, 5, 25, true)]
    [InlineData(30, 5, 26, false)]
    [InlineData(30, 30, 1, false)]
    [InlineData(30, 31, 1, false)]
    [InlineData(2, 0, 2, true)]
    public void BudgetBoundaries(int maximum, int reserve, int pool, bool valid)
    {
        var settings = Settings with { ConnectionReserve = reserve, MaxPoolSize = pool };
        if (valid)
        {
            Assert.Equal(maximum - reserve, DatabasePreflight.ValidateBudget(settings, maximum));
        }
        else
        {
            var error = Assert.Throws<RuntimeSetupException>(() => DatabasePreflight.ValidateBudget(settings, maximum));
            Assert.Contains("database.max_pool_size", error.Message);
            Assert.Contains("database.connection_reserve", error.Message);
        }
    }

    [Fact]
    public async Task PreflightNeedsNoSchemaRejectsUnsafeBudgetsAndReturnsItsLease()
    {
        var connectionString = PostgresTestCluster.CreateDatabase();
        try
        {
            var settings = Settings with { MaxPoolSize = 1 };
            await using var source = NpgsqlDataSource.Create(settings.PooledConnectionString(connectionString));
            var maximum = await DatabasePreflight.CheckAsync(source, settings, NullLogger.Instance);
            await Assert.ThrowsAsync<RuntimeSetupException>(() => DatabasePreflight.CheckAsync(source,
                settings with { MaxPoolSize = maximum }, NullLogger.Instance));
            await Assert.ThrowsAsync<RuntimeSetupException>(() => DatabasePreflight.CheckAsync(source,
                settings with { ConnectionReserve = maximum }, NullLogger.Instance));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabasePreflight.CheckAsync(source, settings, NullLogger.Instance, cancelled.Token));

            await using var connection = await source.OpenConnectionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await using var command = new NpgsqlCommand("SELECT to_regclass('public.schema_migrations') IS NULL AND to_regclass('public.clubs') IS NULL", connection);
            Assert.Equal(true, await command.ExecuteScalarAsync());
        }
        finally
        {
            PostgresTestCluster.DropDatabase(connectionString);
        }
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotExposeConnectionSecrets()
    {
        var connectionString = PostgresTestCluster.CreateDatabase();
        try
        {
            var incorrect = new NpgsqlConnectionStringBuilder(connectionString) { Password = "DO-NOT-LOG-SYNTHETIC-SECRET", Timeout = 1 };
            await using var source = NpgsqlDataSource.Create(incorrect.ConnectionString);
            var error = await Assert.ThrowsAsync<RuntimeSetupException>(() => DatabasePreflight.CheckAsync(source, Settings, NullLogger.Instance));
            Assert.Contains("capacity preflight failed", error.Message);
            Assert.DoesNotContain("DO-NOT-LOG", error.ToString());
            Assert.DoesNotContain("Password=", error.ToString());
        }
        finally
        {
            PostgresTestCluster.DropDatabase(connectionString);
        }
    }
}
