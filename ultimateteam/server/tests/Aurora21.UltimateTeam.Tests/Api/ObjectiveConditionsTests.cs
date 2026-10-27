using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Objectives the panel conditions on a league, nation or club ("Score 3 goals with Premier League players"): goals count only
/// from matching players. FIFA reports a Draft squad's cards by resource id (they are not the club's items), so a Draft match
/// counts them from the catalogue; other modes count only the club's own cards.
/// </summary>
public sealed class ObjectiveConditionsTests
{
    private const long League = 13;
    private const long GroupId = 810;
    private const long ObjectiveId = 81001;

    private static async Task PublishAsync(TestRuntime rt, params string[] modes)
    {
        var document = rt.Game.InUnit(async work => JsonNode.Parse((await RuleRows.DocumentsAsync(work))["objectives"])!.AsObject());
        document["grindGroups"]!.AsArray().Add(new JsonObject
        {
            ["id"] = GroupId, ["title"] = "League goals", ["subTitle"] = "Conditioned", ["award"] = new JsonObject { ["coins"] = 100 },
            ["objectives"] = new JsonArray(new JsonObject
            {
                ["id"] = ObjectiveId, ["name"] = "League scorer", ["stat"] = "goals", ["target"] = 5, ["xp"] = 0,
                ["award"] = new JsonObject { ["coins"] = 100 },
                ["requirements"] = new JsonObject
                {
                    ["modes"] = new JsonArray(modes.Select(mode => (JsonNode?)mode).ToArray()),
                    ["player"] = new JsonObject { ["leagueId"] = League },
                },
            }),
        });
        document["live"]!.AsArray().Add(new JsonObject { ["group"] = GroupId, ["enabled"] = true });
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        foreach (var path in new[] { "grindGroups", "live" })
            await changes.SetChangeAsync(RulesArea.AreaName, new JsonObject { ["file"] = "objectives", ["path"] = new JsonArray(path) },
                ContentOperation.Update, new JsonObject { ["value"] = document[path]!.DeepClone() }, null);
        await changes.PublishAsync("conditioned objective", "test");
    }

    /// <summary>Two league scorers and one from elsewhere, each with one goal; the rows carry the given ids.</summary>
    private static JsonObject Body(IReadOnlyList<long> ids) => new()
    {
        ["myMatchStats"] = new JsonObject { ["goals"] = 3 },
        ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
        ["items"] = new JsonArray(ids.Select((id, i) => (JsonNode)new JsonObject { ["id"] = id, ["goals"] = i < 3 ? 1 : 0 }).ToArray()),
    };

    private static List<long> Resources(TestRuntime rt)
    {
        var used = new HashSet<long>();
        var picked = new List<long>();
        foreach (var inLeague in new[] { true, true, false })
        {
            var entry = rt.Game.Players.Entries.First(entry => (entry.Value.Int("leagueId") == League) == inLeague
                && entry.Value.Rareflag <= 1 && used.Add(entry.Value.Int("assetId")));
            picked.Add(entry.Key);
        }
        return picked;
    }

    private static long Progress(TestRuntime rt) => rt.Game.Store().Objectives().Progress(GroupId, $"objective:{ObjectiveId}");

    [Fact]
    public async Task DraftMatchesCountLeagueGoalsFromTheReportedResourceIds()
    {
        using var rt = TestRuntime.Create();
        await PublishAsync(rt, RewardModes.DraftSinglePlayer);
        var body = Body(Resources(rt));
        rt.Game.InUnit(work => rt.Fut.Objectives.RecordMatchEndAsync(work, "WIN", body, true, true, RewardModes.DraftSinglePlayer));
        Assert.Equal(2, Progress(rt));
    }

    [Fact]
    public async Task OtherModesCountOnlyTheClubsOwnCards()
    {
        using var rt = TestRuntime.Create();
        await PublishAsync(rt, RewardModes.Rivals);
        var resources = Resources(rt);
        // Resource ids are not the club's items outside Draft: nothing counts.
        rt.Game.InUnit(work => rt.Fut.Objectives.RecordMatchEndAsync(work, "SUCCESS", Body(resources), true, false, RewardModes.Rivals));
        Assert.Equal(0, Progress(rt));
        var owned = resources.Select(resource => rt.Fut.Items.MakePlayer(resource, "club").Int("id")).ToList();
        rt.Game.InUnit(work => rt.Fut.Objectives.RecordMatchEndAsync(work, "SUCCESS", Body(owned), true, false, RewardModes.Rivals));
        Assert.Equal(2, Progress(rt));
    }
}
