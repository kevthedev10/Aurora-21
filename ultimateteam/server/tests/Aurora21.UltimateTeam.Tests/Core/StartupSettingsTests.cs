using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Services.Market;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class StartupSettingsTests
{
    private static JsonObject Defaults => JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "config.example.json")))!.AsObject();

    [Fact]
    public void MissingLimitsUseBundledDefaultsAndDoNotChangeTheInput()
    {
        var personal = JsonNode.Parse("""{"statement_timeout_ms":45000,"custom":"keep"}""")!.AsObject();
        var before = personal.ToJsonString();
        var database = DatabaseSettings.From(personal, Defaults["database"]!.AsObject());
        Assert.Equal(45000, database.StatementTimeoutMilliseconds);
        Assert.Equal(5, database.ConnectionReserve);
        Assert.Equal(5432, database.ManagedPort);
        Assert.Equal("ultimateteam", database.ManagedPassword);
        Assert.Equal(10000, ShutdownSettings.From(null, Defaults["shutdown"]!.AsObject()).DrainTimeoutMilliseconds);
        Assert.Equal(before, personal.ToJsonString());
    }

    [Theory]
    [InlineData("managed_port", "-1")]
    [InlineData("managed_port", "65536")]
    [InlineData("managed_password", "null")]
    [InlineData("managed_password", "true")]
    [InlineData("managed_password", "\"\"")]
    [InlineData("max_pool_size", "0")]
    [InlineData("max_pool_size", "501")]
    [InlineData("max_pool_size", "1.5")]
    [InlineData("max_pool_size", "true")]
    [InlineData("max_pool_size", "null")]
    [InlineData("max_pool_size", "\"wrong\"")]
    [InlineData("connection_reserve", "-1")]
    [InlineData("connection_reserve", "10001")]
    [InlineData("max_auto_prepare", "-1")]
    [InlineData("max_auto_prepare", "1001")]
    [InlineData("max_auto_prepare", "\"wrong\"")]
    [InlineData("connection_timeout_ms", "4000")]
    [InlineData("statement_timeout_ms", "4000")]
    public void InvalidDatabaseLimitsFailWithTheKey(string key, string value)
    {
        var input = new JsonObject { [key] = JsonNode.Parse(value) };
        var error = Assert.Throws<RuntimeSetupException>(() => DatabaseSettings.From(input, Defaults["database"]!.AsObject()));
        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(300000, true)]
    [InlineData(300001, false)]
    public void ShutdownTimeoutBoundaries(long value, bool valid)
    {
        var input = new JsonObject { ["drain_timeout_ms"] = value };
        if (valid)
        {
            Assert.Equal(value, ShutdownSettings.From(input, Defaults["shutdown"]!.AsObject()).DrainTimeoutMilliseconds);
        }
        else
        {
            Assert.Throws<RuntimeSetupException>(() => ShutdownSettings.From(input, Defaults["shutdown"]!.AsObject()));
        }
    }

    [Fact]
    public void ExactTimeoutEqualityAndPoolReserveBoundariesAreValid()
    {
        var settings = DatabaseSettings.From(JsonNode.Parse("""{"lock_timeout_ms":1000,"statement_timeout_ms":1000,"connection_timeout_ms":1000,"max_pool_size":500,"connection_reserve":0}""")!.AsObject(), Defaults["database"]!.AsObject());
        Assert.Equal(1000, settings.ConnectionTimeoutMilliseconds);
        Assert.Equal(0, settings.ConnectionReserve);
    }

    [Fact]
    public void AutoPrepareDefaultAndOverrideReachThePooledConnectionString()
    {
        var defaults = Defaults["database"]!.AsObject();
        Assert.Equal(100, DatabaseSettings.From(null, defaults).MaxAutoPrepare);
        var settings = DatabaseSettings.From(new JsonObject { ["max_auto_prepare"] = 0 }, defaults);
        var pooled = new NpgsqlConnectionStringBuilder(settings.PooledConnectionString("Host=127.0.0.1;Username=u;Password=p"));
        Assert.Equal(0, pooled.MaxAutoPrepare);
    }

    [Fact]
    public void PlayerAndJobLimitsAreValidatedWithoutStartingARuntime()
    {
        new PlayerRegistryOptions().Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerRegistryOptions { MaxOpenClubs = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerRegistryOptions { ConcurrentOpens = 33 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerRegistryOptions { IdleTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<RuntimeSetupException>(() => MarketMaintenanceJob.ValidateInterval(TimeSpan.Zero));
        Assert.Throws<RuntimeSetupException>(() => MarketMaintenanceJob.ValidateInterval(TimeSpan.FromDays(2)));
        MarketMaintenanceJob.ValidateInterval(TimeSpan.FromDays(1));
    }
}
