using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class TcpKeepAliveSettingsTests
{
    private static JsonObject Defaults => new()
        { ["idle_seconds"] = 0, ["interval_seconds"] = 10, ["retry_count"] = 3 };

    [Fact]
    public void ExistingConfigsLeaveTheExperimentDisabled()
    {
        Assert.False(TcpKeepAliveSettings.From(null, Defaults).Enabled);
        Assert.False(TcpKeepAliveSettings.From(new JsonObject(), Defaults).Enabled);
    }

    [Fact]
    public void OnlyIdleSecondsNeedOverridingToEnableProbes()
    {
        var settings = TcpKeepAliveSettings.From(new JsonObject { ["idle_seconds"] = 30 }, Defaults);
        Assert.True(settings.Enabled);
        Assert.Equal(new TcpKeepAliveSettings(30, 10, 3), settings);
    }

    [Fact]
    public void DeploymentOverrideEnablesExistingConfigWithoutMutatingIt()
    {
        var values = new JsonObject { ["idle_seconds"] = 0, ["interval_seconds"] = 15, ["retry_count"] = 4 };
        Assert.Equal(new TcpKeepAliveSettings(30, 15, 4), TcpKeepAliveSettings.From(values, Defaults, "30"));
        Assert.Equal(0, values["idle_seconds"]!.GetValue<int>());
    }

    [Fact]
    public void DeploymentRollbackDisablesAnEnabledConfig()
    {
        Assert.False(TcpKeepAliveSettings.From(new JsonObject { ["idle_seconds"] = 30 }, Defaults, "0").Enabled);
        Assert.False(TcpKeepAliveSettings.From(null, Defaults, "").Enabled);
        Assert.Equal(new TcpKeepAliveSettings(30, 10, 3), TcpKeepAliveSettings.From(null, Defaults, "30"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3601")]
    [InlineData("30.5")]
    [InlineData("true")]
    [InlineData("2147483648")]
    public void InvalidDeploymentOverrideIsRejected(string value)
    {
        Assert.Throws<RuntimeSetupException>(() => TcpKeepAliveSettings.From(null, Defaults, value));
    }

    [Theory]
    [InlineData("idle_seconds", "-1")]
    [InlineData("idle_seconds", "3601")]
    [InlineData("interval_seconds", "0")]
    [InlineData("interval_seconds", "301")]
    [InlineData("retry_count", "0")]
    [InlineData("retry_count", "11")]
    [InlineData("idle_seconds", "30.5")]
    [InlineData("idle_seconds", "\"30\"")]
    [InlineData("interval_seconds", "null")]
    [InlineData("retry_count", "true")]
    public void InvalidSettingsAreRejected(string key, string value)
    {
        Assert.Throws<RuntimeSetupException>(() => TcpKeepAliveSettings.From(
            new JsonObject { [key] = JsonNode.Parse(value) }, Defaults));
    }
}
