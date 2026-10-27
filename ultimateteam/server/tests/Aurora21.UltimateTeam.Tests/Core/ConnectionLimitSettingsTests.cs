using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class ConnectionLimitSettingsTests
{
    private static JsonObject Defaults => new() { ["max_ping_ms"] = 200 };

    [Fact]
    public void AbsentSettingsUseTheBundledLimit()
    {
        Assert.Equal(200, ConnectionLimitSettings.From(null, Defaults).MaximumPingMilliseconds);
        Assert.Equal(200, ConnectionLimitSettings.From(new JsonObject(), Defaults).MaximumPingMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(60_000)]
    public void ConfiguredLimitCanBeChangedOrDisabled(int maximum)
    {
        Assert.Equal(maximum, ConnectionLimitSettings.From(new JsonObject { ["max_ping_ms"] = maximum }, Defaults).MaximumPingMilliseconds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("60001")]
    [InlineData("200.5")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"200\"")]
    public void MalformedOrOutOfRangeSettingsAreRejected(string value)
    {
        var values = new JsonObject { ["max_ping_ms"] = JsonNode.Parse(value) };
        Assert.Throws<RuntimeSetupException>(() => ConnectionLimitSettings.From(values, Defaults));
    }
}
