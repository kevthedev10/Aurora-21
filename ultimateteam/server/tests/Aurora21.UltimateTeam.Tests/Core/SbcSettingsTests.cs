using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// SBCs → Settings: rules/sbc.json edited one value at a time as rules changes. Values FIFA could not show are refused when
/// staged; the default formation and fallback tab must name real content when the change is published.
/// </summary>
public sealed class SbcSettingsTests
{
    private static JsonObject Key(params JsonNode[] path) => new() { ["file"] = "sbc", ["path"] = new JsonArray(path) };

    private static JsonObject Value(JsonNode? value) => new() { ["value"] = value };

    [Fact]
    public async Task EmptyTextAndUnknownRequirementTypesAreRefusedWhenStaged()
    {
        using var rt = TestRuntime.Create();
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        var label = await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(RulesArea.AreaName,
            Key("requirements", "RATING", "label"), ContentOperation.Update, Value(" "), null));
        Assert.Equal(400, label.Status);
        Assert.Contains("RATING requirement needs a label", label.Message, StringComparison.Ordinal);
        var ignored = await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(RulesArea.AreaName,
            Key("ignoredRequirements"), ContentOperation.Update, Value(new JsonArray("NOT_A_TYPE")), null));
        Assert.Contains("NOT_A_TYPE", ignored.Message, StringComparison.Ordinal);
        var text = await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(RulesArea.AreaName,
            Key("text", "shortSquad"), ContentOperation.Update, Value(""), null));
        Assert.Contains("cannot be empty", text.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALabelAndAnIgnoredTypePublishLiveAndAnUnknownFormationIsRefused()
    {
        using var rt = TestRuntime.Create();
        var challenge = rt.Game.Sbc.Challenges.First(candidate => rt.Game.Sbc.IsLive(candidate.SetKey)
            && candidate.Source.Obj("requirements")?["rules"]?.AsArray().Any(rule => rule?["type"]?.GetValue<string>() == "CHEMISTRY") == true);
        Assert.Contains("Min Squad Chemistry", rt.Fut.Sbc.Describe(challenge), StringComparison.Ordinal);

        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("requirements", "CHEMISTRY", "label"), ContentOperation.Update, Value("Chemistry: {value}+"), null);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("formation"), ContentOperation.Update, Value("f4231"), null);
        await changes.PublishAsync("sbc settings", "test");
        Assert.Contains("Chemistry: ", rt.Fut.Sbc.Describe(challenge), StringComparison.Ordinal);
        Assert.Equal("f4231", rt.Game.Rules.Sbc.Formation);

        var off = new ContentChangesets(rt.Game, ContentAreas.Default);
        await off.SetChangeAsync(RulesArea.AreaName, Key("ignoredRequirements"), ContentOperation.Update, Value(new JsonArray("CHEMISTRY")), null);
        await off.PublishAsync("ignore chemistry", "test");
        Assert.DoesNotContain("Chemistry", rt.Fut.Sbc.Describe(challenge), StringComparison.Ordinal);

        var bad = new ContentChangesets(rt.Game, ContentAreas.Default);
        await bad.SetChangeAsync(RulesArea.AreaName, Key("formation"), ContentOperation.Update, Value("f999"), null);
        var validation = await bad.ValidateAsync();
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, error => error.Contains("f999", StringComparison.Ordinal));
        Assert.Equal("f4231", rt.Game.Rules.Sbc.Formation);
    }
}
