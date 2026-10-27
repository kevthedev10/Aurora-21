using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class LocalPostgresTests
{
    [Fact]
    public void ConfiguredPortAndPasswordSurviveRestartAndUpgradeWithoutLosingData()
    {
        // Keep this independent cluster outside the shared harness cleanup namespace (ultimateteam21-pg-*).
        var root = Path.Combine(Path.GetTempPath(), "ultimateteam21-stability-" + Guid.NewGuid().ToString("N"));
        var layout = new LocalPostgresLayout(Path.Combine(PostgresTestCluster.BinariesRoot(), RuntimePaths.PostgresPlatform, "bin"),
            Path.Combine(root,"data"), Path.Combine(root,"ultimateteam.password"), Path.Combine(root,"postgres.log"));
        var cluster = new LocalPostgres(layout, NullLogger.Instance);
        try
        {
            var initial = cluster.Start();
            var oldPassword = File.ReadAllText(layout.PasswordPath).Trim();
            using(var connection = new NpgsqlConnection(initial.ConnectionString))
            {
                connection.Open();
                using var command = new NpgsqlCommand("CREATE TABLE public.stability_marker(value integer); INSERT INTO public.stability_marker VALUES(42)", connection);
                command.ExecuteNonQuery();
            }
            cluster.Stop();
            var settings = DatabasePreflightTests.Settings;
            cluster = new LocalPostgres(layout with { Port=initial.Port, Password=settings.ManagedPassword }, NullLogger.Instance);
            for(var attempt=0; attempt<2; attempt++)
            {
                var instance = cluster.Start();
                Assert.Equal(initial.Port, instance.Port);
                Assert.Equal(settings.ManagedPassword, File.ReadAllText(layout.PasswordPath).Trim());
                using(var connection = new NpgsqlConnection(instance.ConnectionString))
                {
                    connection.Open();
                    using var command = new NpgsqlCommand("SELECT value FROM public.stability_marker", connection);
                    Assert.Equal(42, (int)command.ExecuteScalar()!);
                }
                Assert.False(cluster.Start().StartedHere);
                cluster.Stop();
            }
            Assert.NotEqual(oldPassword, File.ReadAllText(layout.PasswordPath).Trim());
        }
        finally
        {
            cluster.Stop();
            if(Directory.Exists(root)) Directory.Delete(root, recursive:true);
        }
    }
}
