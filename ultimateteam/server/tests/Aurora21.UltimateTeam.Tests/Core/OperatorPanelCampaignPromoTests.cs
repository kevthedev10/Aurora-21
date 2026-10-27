using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Server.Panel;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task CampaignPromoTimingApiRejectsIndependentIconsAndTotwWithoutChangingDraft()
    {
        using var setup = await CreateAsync();
        var schedule = OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var start = setup.Runtime.Game.Time.GetUtcNow().AddDays(1);
        var campaign = await schedule.SaveCampaignAsync(null, "Synthetic bounded campaign", start.AddDays(5), 0, startsAt: start);
        var drop = await schedule.SaveDropAsync(null, campaign.Id, "Synthetic special release", start.AddDays(1), start.AddDays(2), false, 0);
        var path = $"/panel/api/content/changeset/changes?dropId={drop.Id}";
        foreach (var promo in new[] { "totw_1", "totw_999999", "silver_totw" })
        {
            var reply = await Send(setup.Panel, "PUT", path, CampaignTimingChange("promo_event", "synthetic_event", CampaignTimingWindow(start, promo)).ToJsonString());
            Assert.Equal(400, reply.Status);
        }
        var icon = CampaignTimingWindow(start, "base_icons");
        icon.Remove("promos"); icon["promo"] = "base_icons";
        Assert.Equal(400, (await Send(setup.Panel, "PUT", path, CampaignTimingChange("icon_rotation", "synthetic_rotation", icon).ToJsonString())).Status);
        Assert.Equal(400, (await Send(setup.Panel, "PUT", path, CampaignTimingChange("icon_rotation", "synthetic_rotation", null, "hide").ToJsonString())).Status);
        var existing = (await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => new PromoTimingArea(false).ReadAsync(w, new JsonObject { ["code"] = "initial_totw_1_3" })))!;
        Assert.NotNull(existing);
        Assert.Equal(400, (await Send(setup.Panel, "PUT", path, CampaignTimingChange("promo_event", "initial_totw_1_3", null, "hide").ToJsonString())).Status);
        existing["promos"] = new JsonArray("synthetic_regular_special");
        Assert.Equal(400, (await Send(setup.Panel, "PUT", path, CampaignTimingChange("promo_event", "initial_totw_1_3", existing, "update").ToJsonString())).Status);
        Assert.Empty((await schedule.Draft(drop.ChangesetId).OpenAsync())!.Changes);
        Assert.Equal(drop.Version, (await schedule.GetDropAsync(drop.Id)).Version);
        // Replaying existing history still uses publication validation rather than the new editor restriction.
        await setup.Runtime.Game.UnitOfWork.RunAsync(0, "synthetic-history-validation", async work =>
        {
            await work.ExecuteAsync("SELECT set_config('ultimateteam.draft_id',@id,true),set_config('ultimateteam.content_publish','on',true)", ("id", drop.ChangesetId.ToString()));
            await new PromoTimingArea(true).ValidateAsync(work, ContentOperation.Insert, new JsonObject { ["code"] = "synthetic_replayed_rotation" }, icon);
            return 0;
        });
        // A custom category alias is also TOTW, even without a native TOTW code.
        var custom = new JsonObject
        {
            ["name"] = "Synthetic TOTW alias", ["odds_category"] = "totw", ["draw_weight"] = 1m, ["status"] = "active",
            ["buckets"] = new JsonArray(new JsonObject { ["code"] = "main", ["name"] = "Main", ["draw_weight"] = 1m, ["enabled"] = true, ["cards"] = new JsonArray() })
        };
        Assert.Equal(200, (await Send(setup.Panel, "PUT", path, CampaignTimingChange("promo", "synthetic_totw_alias", custom).ToJsonString())).Status);
        Assert.Equal(400, (await Send(setup.Panel, "PUT", path, CampaignTimingChange("promo_event", "synthetic_event", CampaignTimingWindow(start, "synthetic_totw_alias")).ToJsonString())).Status);
        Assert.Single((await schedule.Draft(drop.ChangesetId).OpenAsync())!.Changes);
    }

    [Fact]
    public async Task CampaignPromoTimingRestrictionsPreserveIndependentAndLegacyStandaloneEditors()
    {
        using var setup = await CreateAsync();
        var schedule = OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var start = setup.Runtime.Game.Time.GetUtcNow().AddDays(1);
        var standalone = await schedule.SaveDropAsync(null, null, "Synthetic standalone rotation", start, start.AddDays(1), false, 0);
        var legacyCampaign = await schedule.SaveCampaignAsync(null, "Synthetic legacy campaign", null, 0);
        var legacyDrop = await schedule.SaveDropAsync(null, legacyCampaign.Id, "Synthetic legacy TOTW", start, start.AddDays(1), false, 0);
        var icon = CampaignTimingWindow(start, "base_icons"); icon.Remove("promos"); icon["promo"] = "base_icons";
        var ordinal = 0;
        foreach (var scope in new[] { "", "?apply=1", $"?dropId={standalone.Id}", $"?dropId={legacyDrop.Id}" })
        {
            ordinal++;
            Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes" + scope,
                CampaignTimingChange("icon_rotation", $"synthetic_independent_rotation_{ordinal}", icon.DeepClone().AsObject()).ToJsonString())).Status);
            Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes" + scope,
                CampaignTimingChange("promo_event", $"synthetic_independent_totw_{ordinal}", CampaignTimingWindow(start, "totw_1")).ToJsonString())).Status);
        }
        Assert.Contains(setup.Runtime.Game.Live.Current.Catalog.IconRotations, r => r.Code == "synthetic_independent_rotation_2");
        Assert.Contains(setup.Runtime.Game.Live.Current.Catalog.PromoEvents, e => e.Code == "synthetic_independent_totw_2");
        Assert.Equal(2, (await schedule.Draft(standalone.ChangesetId).OpenAsync())!.Changes.Count);
        Assert.Equal(2, (await schedule.Draft(legacyDrop.ChangesetId).OpenAsync())!.Changes.Count);
        var overview = await Send(setup.Panel, "GET", "/panel/api/content/schedule");
        Assert.Equal("totw", overview.Json!["legacy"]!.AsArray().Single(e => e!["code"]!.GetValue<string>() == "synthetic_independent_totw_2")!["rotationType"]!.GetValue<string>());
        var alias = new JsonObject
        {
            ["name"] = "Synthetic classified TOTW alias", ["odds_category"] = "totw", ["draw_weight"] = 1m, ["status"] = "active",
            ["buckets"] = new JsonArray(new JsonObject { ["code"] = "main", ["name"] = "Main", ["draw_weight"] = 1m, ["enabled"] = true, ["cards"] = new JsonArray() })
        };
        var aliasReply = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes?apply=1", CampaignTimingChange("promo", "synthetic_classified_totw_alias", alias).ToJsonString());
        Assert.True(aliasReply.Status == 200, aliasReply.Body);
        var aliasEvent = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes?apply=1", CampaignTimingChange("promo_event", "synthetic_independent_totw_2", CampaignTimingWindow(start, "synthetic_classified_totw_alias"), "update").ToJsonString());
        Assert.True(aliasEvent.Status == 200, aliasEvent.Body);
        overview = await Send(setup.Panel, "GET", "/panel/api/content/schedule");
        Assert.Equal("totw", overview.Json!["legacy"]!.AsArray().Single(e => e!["code"]!.GetValue<string>() == "synthetic_independent_totw_2")!["rotationType"]!.GetValue<string>());
        var ordinary = setup.Runtime.Game.Live.Current.Catalog.Promos.First(p => p.OddsCategory != "totw" && !p.Code.StartsWith("totw_", StringComparison.Ordinal)
            && p.Code != "silver_totw" && !PromoEdits.StandardTypes.ContainsKey(p.Code) && !Aurora21.UltimateTeam.Core.Catalog.PromoAvailability.IsIconTier(p.Code)
            && p.Code is not ("fut_champions" or "developer_cards"));
        var ordinaryEvent = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes?apply=1", CampaignTimingChange("promo_event", "synthetic_independent_totw_2", CampaignTimingWindow(start, ordinary.Code), "update").ToJsonString());
        Assert.True(ordinaryEvent.Status == 200, ordinaryEvent.Body);
        overview = await Send(setup.Panel, "GET", "/panel/api/content/schedule");
        Assert.Equal("promo", overview.Json!["legacy"]!.AsArray().Single(e => e!["code"]!.GetValue<string>() == "synthetic_independent_totw_2")!["rotationType"]!.GetValue<string>());
        var currentEvent = setup.Runtime.Game.Live.Current.Catalog.PromoEvents.Single(e => e.Code == "synthetic_independent_totw_2");
        var aliasPromo = setup.Runtime.Game.Live.Current.Catalog.Promos.Single(p => p.Code == "synthetic_classified_totw_alias");
        Assert.Contains(setup.Runtime.Game.Live.Current.Catalog.PromoEventPromos, link => link.EventId == currentEvent.EventId && link.PromoId == aliasPromo.PromoId && !link.Enabled);

    }

    [Fact]
    public async Task CampaignPromoTimingApiRejectsMovingSavedRotationsAndPromotingLegacyCampaignsWithoutWrites()
    {
        using var setup = await CreateAsync();
        var schedule = OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var start = setup.Runtime.Game.Time.GetUtcNow().AddDays(1);
        var campaign = await schedule.SaveCampaignAsync(null, "Synthetic dated campaign", start.AddDays(4), 0, startsAt: start);
        var icon = CampaignTimingWindow(start, "base_icons"); icon.Remove("promos"); icon["promo"] = "base_icons";
        var standalone = await schedule.SaveDropAsync(null, null, "Synthetic independent rotation", start.AddDays(1), start.AddDays(2), false, 0);
        await schedule.Draft(standalone.ChangesetId).SetChangeAsync("icon_rotation", new JsonObject { ["code"] = "synthetic_saved_rotation" }, ContentOperation.Insert, icon, null);
        standalone = await schedule.GetDropAsync(standalone.Id);
        var move = await Send(setup.Panel, "PUT", $"/panel/api/content/drops/{standalone.Id}", new JsonObject
        {
            ["campaignId"] = campaign.Id, ["name"] = standalone.Name, ["startsAt"] = standalone.StartsAt.ToString("O"),
            ["endsAt"] = standalone.EndsAt!.Value.ToString("O"), ["inheritEnd"] = false, ["version"] = standalone.Version
        }.ToJsonString());
        Assert.Equal(400, move.Status);
        var unchanged = await schedule.GetDropAsync(standalone.Id);
        Assert.Null(unchanged.CampaignId);
        Assert.Equal(standalone.Version, unchanged.Version);
        Assert.Single((await schedule.Draft(standalone.ChangesetId).OpenAsync())!.Changes);
        var legacy = await schedule.SaveCampaignAsync(null, "Synthetic legacy calendar", start.AddDays(4), 0);
        var legacyDrop = await schedule.SaveDropAsync(null, legacy.Id, "Synthetic existing TOTW", start.AddDays(1), start.AddDays(2), false, 0);
        await schedule.Draft(legacyDrop.ChangesetId).SetChangeAsync("promo_event", new JsonObject { ["code"] = "synthetic_saved_totw" }, ContentOperation.Insert, CampaignTimingWindow(start, "totw_1"), null);
        var promote = await Send(setup.Panel, "PUT", $"/panel/api/content/campaigns/{legacy.Id}", new JsonObject
        {
            ["name"] = legacy.Name, ["startsAt"] = start.ToString("O"), ["endsAt"] = legacy.EndsAt!.Value.ToString("O"), ["version"] = legacy.Version
        }.ToJsonString());
        Assert.Equal(400, promote.Status);
        var preserved = await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, async work => (await ContentSchedule.CampaignsAsync(work)).Single(c => c.Id == legacy.Id));
        Assert.Null(preserved.StartsAt);
        Assert.Equal(legacy.Version, preserved.Version);
        var aliasDrop = await schedule.SaveDropAsync(null, null, "Synthetic category-alias event", start.AddDays(1), start.AddDays(2), false, 0);
        var aliasDefinition = new JsonObject
        {
            ["name"] = "Synthetic TOTW alias", ["odds_category"] = "totw", ["draw_weight"] = 1m, ["status"] = "active",
            ["buckets"] = new JsonArray(new JsonObject { ["code"] = "main", ["name"] = "Main", ["draw_weight"] = 1m, ["enabled"] = true, ["cards"] = new JsonArray() })
        };
        await schedule.Draft(aliasDrop.ChangesetId).SetChangeAsync("promo", new JsonObject { ["code"] = "synthetic_pending_totw_alias" }, ContentOperation.Insert, aliasDefinition, null);
        await schedule.Draft(aliasDrop.ChangesetId).SetChangeAsync("promo_event", new JsonObject { ["code"] = "synthetic_pending_alias_event" }, ContentOperation.Insert, CampaignTimingWindow(start, "synthetic_pending_totw_alias"), null);
        aliasDrop = await schedule.GetDropAsync(aliasDrop.Id);
        var aliasMove = await Send(setup.Panel, "PUT", $"/panel/api/content/drops/{aliasDrop.Id}", new JsonObject
        {
            ["campaignId"] = campaign.Id, ["name"] = aliasDrop.Name, ["startsAt"] = aliasDrop.StartsAt.ToString("O"),
            ["endsAt"] = aliasDrop.EndsAt!.Value.ToString("O"), ["inheritEnd"] = false, ["version"] = aliasDrop.Version
        }.ToJsonString());
        Assert.Equal(400, aliasMove.Status);
        Assert.Null((await schedule.GetDropAsync(aliasDrop.Id)).CampaignId);
        Assert.Equal(aliasDrop.Version, (await schedule.GetDropAsync(aliasDrop.Id)).Version);
        // A category supplied by a preceding queued release must also remain independent.
        var predecessor = await schedule.SaveDropAsync(null, null, "Synthetic preceding alias definition", start, start.AddDays(3), false, 0);
        await schedule.Draft(predecessor.ChangesetId).SetChangeAsync("promo", new JsonObject { ["code"] = "synthetic_preceding_totw_alias" }, ContentOperation.Insert, aliasDefinition.DeepClone().AsObject(), null);
        var candidate = await schedule.SaveDropAsync(null, campaign.Id, "Synthetic later event", start.AddDays(1), start.AddDays(2), false, 0);
        await schedule.Draft(candidate.ChangesetId).SetChangeAsync("promo_event", new JsonObject { ["code"] = "synthetic_later_alias_event" }, ContentOperation.Insert, CampaignTimingWindow(start, "synthetic_preceding_totw_alias"), null);
        predecessor = await schedule.GetDropAsync(predecessor.Id);
        await schedule.ScheduleAsync(predecessor.Id, predecessor.Version);
        candidate = await schedule.GetDropAsync(candidate.Id);
        var dependentValidation = await schedule.ValidateAsync(candidate.Id, candidate.Version);
        Assert.False(dependentValidation.Ok);
        Assert.Contains(dependentValidation.Errors, error => error.Contains("TOTW events", StringComparison.Ordinal));
        Assert.Equal(422, (await Assert.ThrowsAsync<ContentChangeException>(() => schedule.ScheduleAsync(candidate.Id, candidate.Version))).Status);
        // An upgraded stored draft can already have such an assignment. Validate/Schedule must still refuse it.
        await setup.Runtime.Game.UnitOfWork.RunAsync(0, "synthetic-upgraded-campaign-draft", work => work.ExecuteAsync(
            "UPDATE ultimateteam.content_drops SET campaign_id=@campaign WHERE drop_id=@id", ("campaign", campaign.Id), ("id", standalone.Id)));
        var validation = await schedule.ValidateAsync(standalone.Id, standalone.Version);
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, error => error.Contains("Icon rotations", StringComparison.Ordinal));
        // Scheduling rejects this known assignment in its direct timing validation (400),
        // before projection; a category discovered only in queued content uses 422 above.
        var rejected = await Assert.ThrowsAsync<ContentChangeException>(() => schedule.ScheduleAsync(standalone.Id, standalone.Version));
        Assert.Equal(400, rejected.Status);
        Assert.Contains("Icon rotations", rejected.Message, StringComparison.Ordinal);
        Assert.Equal("draft", (await schedule.GetDropAsync(standalone.Id)).Status);
        Assert.Equal(standalone.Version, (await schedule.GetDropAsync(standalone.Id)).Version);
    }

    private static JsonObject CampaignTimingChange(string area, string code, JsonObject? row, string operation = "insert") => new()
    {
        ["area"] = area, ["key"] = new JsonObject { ["code"] = code }, ["operation"] = operation, ["after"] = row
    };
    private static JsonObject CampaignTimingWindow(DateTimeOffset start, string promo) => new()
    {
        ["name"] = "Synthetic independent window", ["enabled"] = false, ["starts_at"] = start.ToString("O"),
        ["ends_at"] = start.AddDays(1).ToString("O"), ["promos"] = new JsonArray(promo)
    };
}
