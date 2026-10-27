using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// FutLeaderboards.swf finds each selection by group position: category 0, view 1, period 2. Sent as category, period,
/// view, the info panel showed the period and view as "undefined" (live 10-06; migration 162). The groups are FIFA 21's own
/// Leaderboards options (Match Earnings, Transfer Profit, Club Value, Top Squad; Top 100 or Friends; Weekly, Monthly,
/// All-Time) plus Division Rivals; migration 163 replaces the stored FUT Champions ones (regions, Current/Previous).
/// </summary>
public sealed class LeaderboardOptionOrderTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public void OptionsListCategoryThenViewThenPeriod()
    {
        using var rt = TestRuntime.Create();
        var options = rt.CallObject("GET", $"{G}/leaderboards/options");
        Assert.Equal(["category", "view", "period"], options["options"]!.AsArray().Select(group => group!["id"]!.GetValue<string>()));
        Assert.Equal("competitor", options["selection"]!["category"]!.GetValue<string>());
        Assert.Equal("TOP100", options["selection"]!["view"]!.GetValue<string>());
        Assert.Equal("Weekly", options["selection"]!["period"]!.GetValue<string>());
        Assert.Equal(5 * 2 * 3, options["permutations"]!.AsArray().Count);
        Assert.Contains(options["permutations"]!.AsArray(), permutation =>
            permutation!["url"]!.GetValue<string>() == "/period/AllTime/category/trader/view/FRIENDS");
    }

    [Fact]
    public void MigrationsReplaceStoredChampionsOptions()
    {
        using var rt = TestRuntime.Create();
        rt.Game.InUnit(async work =>
        {
            // The stored rules as live had them: the FUT Champions regions, Weekly/Monthly, Current/Previous.
            var seasons = RuleRows.Rebuild((await RuleRows.ReadAsync(work, "seasons"))["seasons"]);
            seasons["leaderboards"]!["groups"] = JsonNode.Parse("""
                [
                  { "id": "category", "default": "TOP", "options": [
                    { "id": "TOP", "display": "LOC_LB_CAT_WW", "description": "LOC_LB_DESC_WW", "image": "worldwideIcon.swf" },
                    { "id": "EUR", "display": "LOC_LB_CAT_EURAFRICA", "description": "LOC_LB_DESC_EURAFRICA", "image": "europeIcon.swf" } ] },
                  { "id": "period", "default": "Weekly", "options": [
                    { "id": "Weekly", "display": "LOC_LB_PER_WEEKLY", "description": "LOC_LB_PER_WEEKLY_DESC", "image": "weeklyIcon.swf" },
                    { "id": "Monthly", "display": "LOC_LB_PER_MONTHLY", "description": "LOC_LB_PER_MONTHLY_DESC", "image": "monthlyIcon.swf" } ] },
                  { "id": "view", "default": "Current", "options": [
                    { "id": "Current", "display": "LOC_LB_CURRENT", "description": "LOC_LB_CURRENT_DESC", "image": "currentIcon.swf" },
                    { "id": "Previous", "display": "LOC_LB_PREVIOUS", "description": "LOC_LB_PREVIOUS_DESC", "image": "previousIcon.swf" } ] }
                ]
                """);
            await RuleRows.WriteAsync(work, "seasons", seasons);
            Assert.Equal(["category", "period", "view"], await GroupIdsAsync(work));

            await RunMigrationAsync(work, 162, "162-leaderboard-option-order.sql");
            Assert.Equal(["category", "view", "period"], await GroupIdsAsync(work));
            Assert.Equal("Current", (await LeaderboardsAsync(work))["groups"]![1]!["default"]!.GetValue<string>());

            await RunMigrationAsync(work, 163, "163-leaderboard-categories.sql");
            await RuleRows.FillAsync(work, GameRules.ReadFiles(TestRuntime.DataRoot), NullLogger.Instance);
            var groups = (await LeaderboardsAsync(work))["groups"]!.AsArray();
            Assert.Equal(["category", "view", "period"], groups.Select(group => group!["id"]!.GetValue<string>()));
            Assert.Equal(["competitor", "trader", "collector", "builder", "rivals"],
                groups[0]!["options"]!.AsArray().Select(option => option!["id"]!.GetValue<string>()));
            Assert.Equal("LOC_LB_SCORE_TRADER", groups[0]!["options"]![1]!["scoreLabel"]!.GetValue<string>());
            Assert.Equal("allTimeIcon.swf", groups[2]!["options"]![2]!["image"]!.GetValue<string>());
        });
    }

    private static async ValueTask RunMigrationAsync(IUnitOfWork work, int version, string file)
    {
        await work.ExecuteAsync($"DELETE FROM ultimateteam.schema_migrations WHERE version={version}");
        using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream($"DatabaseSetup/migrations/{file}")!;
        using var reader = new StreamReader(stream);
        await work.ExecuteAsync(await reader.ReadToEndAsync());
    }

    private static async ValueTask<JsonNode> LeaderboardsAsync(IUnitOfWork work) =>
        RuleRows.Rebuild((await RuleRows.ReadAsync(work, "seasons"))["seasons"])["leaderboards"]!;

    private static async ValueTask<string[]> GroupIdsAsync(IUnitOfWork work) =>
        (await LeaderboardsAsync(work))["groups"]!.AsArray().Select(group => group!["id"]!.GetValue<string>()).ToArray();
}
