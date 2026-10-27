using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Server.Panel;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    private static async Task<(int Status, JsonNode? Json)> ReleaseAvailabilityAsync(Setup setup, string kind, string target)
    {
        var schedule = OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var start = setup.Runtime.Game.Time.GetUtcNow().AddHours(-1);
        var drop = await schedule.SaveDropAsync(null, null, "Synthetic dated availability", start, start.AddHours(2), false, 0);
        var reply = await Send(setup.Panel, "PUT", $"/panel/api/content/changeset/changes?dropId={drop.Id}", Body(new
        {
            area = "content_window", key = new { kind, target }, operation = "update", after = new { enabled = true },
        }));
        if (reply.Status != 200) return (reply.Status, reply.Json);
        var current = await schedule.GetDropAsync(drop.Id);
        var published = await Send(setup.Panel, "POST", $"/panel/api/content/drops/{drop.Id}/publish", Body(new { version = current.Version }));
        return (published.Status, published.Json);
    }

    [Fact]
    public async Task PanelAvailabilityRequiresDatesWhileDefinitionAndExistingLiveEditsRemainImmediate()
    {
        using var setup = await CreateAsync();
        // The seed already has live token offers. Use a newly created definition, not a seeded live pack.
        var created = await Send(setup.Panel, "POST", "/panel/api/packs/catalog?apply=1", Body(new
        {
            revision = await CatalogRevisionAsync(setup), pack = new { name = "Synthetic date-gated offer", size = 1, gold_weight = 100, rare_slots = 0 },
        }));
        Assert.True(created.Status == 201, created.Body);
        var packId = created.Json!["id"]!.GetValue<long>();
        var baseline = setup.Runtime.Game.Packs.TokenOffers().DeepClone();
        var revision = await CatalogRevisionAsync(setup);
        var rejected = await Send(setup.Panel, "PUT", "/panel/api/packs/tokens-lineup?apply=1", Body(new
        {
            revision, listing = new[] { new { packId, price = 25, enabled = true, untradeable = true } },
        }));
        Assert.Equal(422, rejected.Status);
        Assert.True(JsonNode.DeepEquals(baseline, setup.Runtime.Game.Packs.TokenOffers()));
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/tokens-lineup?apply=1", Body(new
        {
            revision, listing = new[] { new { packId, price = 25, enabled = false, untradeable = true } },
        }))).Status);
        var live = await ReleaseAvailabilityAsync(setup, "token", packId.ToString());
        Assert.True(live.Status == 200, live.Json?.ToJsonString());
        Assert.Equal("live", live.Json!["status"]!.GetValue<string>());
        Assert.Equal(25, setup.Runtime.Game.Packs.Capture(long.Parse(setup.Runtime.Game.Packs.TokenOffers().Single().Key)).Config.Int("points_price"));
        Assert.True(DateTimeOffset.Parse(live.Json["startsAt"]!.GetValue<string>()) < setup.Runtime.Game.Time.GetUtcNow().AddMinutes(-30));
        var end = DateTimeOffset.Parse(live.Json["endsAt"]!.GetValue<string>());
        var projection = await OperatorPanel.CreateContentSchedule(setup.Runtime.Game).ProjectAsync(end.AddMinutes(1));
        Assert.False(projection.Rules.Packs.TokenOffers.Single().Value!["enabled"]!.GetValue<bool>());
        var edited = await Send(setup.Panel, "PUT", "/panel/api/packs/tokens-lineup?apply=1", Body(new
        {
            revision = await CatalogRevisionAsync(setup), listing = new[] { new { packId, price = 50, enabled = true, untradeable = false } },
        }));
        Assert.True(edited.Status == 200, edited.Body);
        Assert.Equal(50, setup.Runtime.Game.Packs.Capture(long.Parse(setup.Runtime.Game.Packs.TokenOffers().Single().Key)).Config.Int("points_price"));
        var sbcs = (await Send(setup.Panel, "GET", "/panel/api/sbc")).Json!["live"]!.DeepClone().AsArray();
        sbcs.OfType<JsonObject>().First(row => row["enabled"]!.GetValue<bool>() == false)["enabled"] = true;
        var rawSbc = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes?apply=1", Body(new
        {
            area = "sbc_live", key = new { scope = "live" }, operation = "update", after = new { listing = sbcs },
        }));
        Assert.Equal(422, rawSbc.Status);
        var objective = setup.Runtime.Game.Rules.Objectives.GroupIds.First(id => !setup.Runtime.Game.Rules.Objectives.IsLive(id));
        var rawObjective = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes?apply=1", Body(new
        {
            area = "rules", key = new { file = "objectives", path = new[] { "live" } }, operation = "update",
            after = new { value = new[] { new { group = objective, enabled = true } } },
        }));
        Assert.Equal(422, rawObjective.Status);
    }

    [Fact]
    public async Task AvailabilityWindowsRejectMissingEndDatesAndFutureManualPublication()
    {
        using var setup = await CreateAsync();
        var schedule = OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var start = setup.Runtime.Game.Time.GetUtcNow().AddHours(1);
        var drop = await schedule.SaveDropAsync(null, null, "Synthetic date validation", start, null, false, 0);
        string Change() => Body(new { area = "content_window", key = new { kind = "sbc", target = "POTM_1" }, operation = "update", after = new { enabled = true } });
        Assert.Equal(422, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes?apply=1", Change())).Status);
        Assert.Equal(422, (await Send(setup.Panel, "PUT", $"/panel/api/content/changeset/changes?dropId={drop.Id}", Change())).Status);
        drop = await schedule.SaveDropAsync(drop.Id, null, drop.Name, start, start.AddHours(1), false, drop.Version);
        Assert.Equal(200, (await Send(setup.Panel, "PUT", $"/panel/api/content/changeset/changes?dropId={drop.Id}", Change())).Status);
        drop = await schedule.GetDropAsync(drop.Id);
        Assert.Equal(422, (await Send(setup.Panel, "POST", $"/panel/api/content/drops/{drop.Id}/publish", Body(new { version = drop.Version }))).Status);
        Assert.Equal("draft", (await schedule.GetDropAsync(drop.Id)).Status);
        var scheduled = await Send(setup.Panel, "POST", $"/panel/api/content/drops/{drop.Id}/schedule", Body(new { version = drop.Version }));
        Assert.True(scheduled.Status == 200, scheduled.Body);
    }
}
