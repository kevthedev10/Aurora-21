using System.Text.Json.Nodes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>The panel's Objectives page reply, and a pack a Season Pass level pays cannot be deleted.</summary>
public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task TheObjectivesReplyCarriesTheCatalogueAndTheNextFreeIds()
    {
        using var setup = await CreateAsync();
        var reply = (await Send(setup.Panel, "GET", "/panel/api/objectives")).Json!.AsObject();
        Assert.Equal("objectives", reply["settingsFile"]!.GetValue<string>());
        var rules = reply["rules"]!.AsObject();
        Assert.NotEmpty(rules["grindGroups"]!.AsArray());
        Assert.Equal(30, rules["seasonProgress"]!["levelCount"]!.GetValue<int>());
        var groupIds = rules["grindGroups"]!.AsArray().Select(group => group!["id"]!.GetValue<long>()).Append(rules["welcomeGroup"]!["id"]!.GetValue<long>());
        Assert.True(reply["nextGroupId"]!.GetValue<long>() > groupIds.Max());
        Assert.Contains(reply["stats"]!.AsArray(), stat => stat!.GetValue<string>() == "matches");
        // Every group the rules held when migration 143 ran counts from the club's creation: it is marked started.
        Assert.NotEmpty(reply["started"]!.AsArray());
        // Seasons (migration 166): without custom seasons the default season (the campaign id) is in force, and the next free
        // season id is past it.
        var seasons = reply["seasons"]!.AsObject();
        Assert.Equal((1L, 1L), (seasons["current"]!.GetValue<long>(), seasons["defaultId"]!.GetValue<long>()));
        Assert.True(seasons["nextId"]!.GetValue<long>() >= 2);
        Assert.Empty(rules["seasons"]!.AsArray());

        // Level 2 pays the Gold Pack (304): the pack's delete check names the objectives rules.
        var check = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog/304/delete-check")).Json!;
        var message = string.Join(" ", check["blockers"]!.AsArray().Select(blocker => blocker!["message"]!.GetValue<string>()));
        Assert.Contains("objectives", message, StringComparison.Ordinal);

        // Normal objective text edits save immediately and reach both FIFA display fields.
        var groups = rules["grindGroups"]!.AsArray();
        var group = groups.OfType<JsonObject>().First(row => setup.Runtime.Game.Rules.Objectives.IsLive(row["id"]!.GetValue<long>()));
        var objective = group["objectives"]![0]!.AsObject();
        objective["description"] = "Finish {n} matches with your club.";
        var saved = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes?apply=1", new JsonObject
        {
            ["area"] = "rules", ["operation"] = "update",
            ["key"] = new JsonObject { ["file"] = "objectives", ["path"] = new JsonArray("grindGroups") },
            ["after"] = new JsonObject { ["value"] = groups.DeepClone() },
        }.ToJsonString());
        Assert.Equal(200, saved.Status);
        var servedGroup = setup.Runtime.CallObject("GET", "/ut/game/fifa21/scmp/active/category/1/details")["groupsList"]!.AsArray()
            .OfType<JsonObject>().Single(row => row["groupId"]!.GetValue<long>() == group["id"]!.GetValue<long>());
        var servedObjective = servedGroup["objectives"]!.AsArray().OfType<JsonObject>()
            .Single(row => row["objectiveId"]!.GetValue<long>() == objective["id"]!.GetValue<long>());
        var expectedText = $"Finish {objective["target"]!.GetValue<long>():N0} matches with your club.";
        Assert.Equal(expectedText, servedObjective["description"]!.GetValue<string>());
        Assert.Equal(expectedText, servedObjective["shortDescription"]!.GetValue<string>());
    }
}
