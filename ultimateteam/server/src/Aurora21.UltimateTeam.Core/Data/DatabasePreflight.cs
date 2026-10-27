using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>Read-only process-start capacity validation, before schema or club setup.</summary>
public static class DatabasePreflight
{
    public static int ValidateBudget(DatabaseSettings settings, int maximumConnections)
    {
        var available = maximumConnections - settings.ConnectionReserve;
        if (available < 1 || settings.MaxPoolSize > available)
        {
            throw new RuntimeSetupException($"config.json database.max_pool_size={settings.MaxPoolSize} exceeds PostgreSQL max_connections={maximumConnections} minus database.connection_reserve={settings.ConnectionReserve} (usable={available}).");
        }
        return available;
    }

    public static async Task<int> CheckAsync(NpgsqlDataSource source, DatabaseSettings settings, ILogger log,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SHOW max_connections", connection);
            var maximum = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            var available = ValidateBudget(settings, maximum);
            log.LogInformation("DATABASE_LIMITS pool={Pool} serverMax={Maximum} reserve={Reserve} usable={Available} connectionTimeoutMs={ConnectionTimeout} lockTimeoutMs={LockTimeout} statementTimeoutMs={StatementTimeout}",
                settings.MaxPoolSize, maximum, settings.ConnectionReserve, available, settings.ConnectionTimeoutMilliseconds,
                settings.LockTimeoutMilliseconds, settings.StatementTimeoutMilliseconds);
            return maximum;
        }
        catch (NpgsqlException)
        {
            throw new RuntimeSetupException("PostgreSQL capacity preflight failed. Check database connectivity, credentials and server availability; no schema or club setup was performed.");
        }
    }
}
