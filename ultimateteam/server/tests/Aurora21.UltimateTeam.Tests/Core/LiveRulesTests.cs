using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class LiveRulesTests
{
    [Fact]
    public void EveryBundledRulesFileRoundTripsThroughRows()
    {
        foreach (var (file, text) in GameRules.ReadFiles(TestRuntime.DataRoot))
        {
            var document = JsonNode.Parse(text)!;
            var rebuilt = RuleRows.Rebuild(RuleRows.Flatten(file, document));
            Assert.True(JsonNode.DeepEquals(document, rebuilt), file);
        }
    }

    [Fact]
    public async Task MarketRulePublishesLiveAndUnknownKeyIsRefused()
    {
        using var runtime = TestRuntime.Create();
        var changes = new ContentChangesets(runtime.Game, ContentAreas.Default);
        var before = runtime.Game.Rules.Market.MaintenanceIntervalSeconds;
        var key = Key("market", "maintenanceIntervalSeconds");

        await changes.SetChangeAsync("rules", key, ContentOperation.Update, Value(before + 1), null);
        Assert.Equal(before, runtime.Game.Rules.Market.MaintenanceIntervalSeconds);
        var validation = await changes.ValidateAsync();
        Assert.True(validation.Ok, string.Join("; ", validation.Errors));
        Assert.Equal(before, runtime.Game.Rules.Market.MaintenanceIntervalSeconds);

        await changes.PublishAsync("market interval", "test");
        Assert.Equal(before + 1, runtime.Game.Rules.Market.MaintenanceIntervalSeconds);

        var error = await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(
            "rules", Key("market", "unknownRule"), ContentOperation.Insert, Value(1), null));
        Assert.Equal(400, error.Status);

        var imported = await changes.ImportRulesAsync("market", "test");
        Assert.Equal(1, imported.Staged);
        Assert.True((await changes.ValidateAsync()).Ok);
        await changes.PublishAsync("restore bundled market interval", null);
        Assert.Equal(before, runtime.Game.Rules.Market.MaintenanceIntervalSeconds);
    }

    [Fact]
    public async Task ImportsProtectPendingEditsAndAllowUnrelatedFiles()
    {
        using var runtime = TestRuntime.Create();
        var changes = new ContentChangesets(runtime.Game, ContentAreas.Default);
        var key = Key("market", "maintenanceIntervalSeconds");
        var before = runtime.Game.Rules.Market.MaintenanceIntervalSeconds;
        await changes.SetChangeAsync("rules", key, ContentOperation.Update, Value(before + 1), null);
        await changes.SetChangeAsync("rules", Key("cards", "_comment"), ContentOperation.Update,
            new JsonObject { ["value"] = "Synthetic changed comment" }, null);
        await changes.PublishAsync("synthetic import baseline", "test");
        await changes.SetChangeAsync("rules", key, ContentOperation.Update, Value(before + 2), null);

        var error = await Assert.ThrowsAsync<ContentChangeException>(() => changes.ImportRulesAsync("market", "test"));
        Assert.Equal(409, error.Status);
        Assert.Equal(before + 1, runtime.Game.Rules.Market.MaintenanceIntervalSeconds);
        Assert.True((await changes.ImportRulesAsync("cards", "test")).Staged > 0);
        var open = await changes.OpenAsync();
        var edit = Assert.Single(open!.Changes, c => c.Area == "rules" && c.Key["file"]!.GetValue<string>() == "market");
        Assert.Equal(before + 2, edit.After!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task PublishedRemovalOfBundledCommentSurvivesRestart()
    {
        var runtime = TestRuntime.Create();
        var key = Key("market", "_comment");
        var changes = new ContentChangesets(runtime.Game, ContentAreas.Default);
        await changes.SetChangeAsync("rules", key, ContentOperation.Hide, null, null);
        Assert.True((await changes.ValidateAsync()).Ok);
        await changes.PublishAsync("remove comment", "test");

        var connection = runtime.Detach();
        using var restarted = TestRuntime.Create(connectionString: connection);
        var stored = await restarted.Game.UnitOfWork.RunReadAsync(0,
            work => new RulesArea().ReadAsync(work, key));
        Assert.Null(stored);
    }

    private static JsonObject Key(string file, string name) =>
        new() { ["file"] = file, ["path"] = new JsonArray(name) };

    private static JsonObject Value(int value) => new() { ["value"] = value };
}
