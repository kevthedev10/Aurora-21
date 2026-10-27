using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Server.Panel;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task ImmediateContentApiPublishesOnlyItsOwnEditAndPreservesOpenAndScheduledDrafts()
    {
        using var setup = await CreateAsync();
        var changes = new ContentChangesets(setup.Runtime.Game, ContentAreas.Default);
        var pending = (await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => new PromoArea().ReadAsync(w, new JsonObject { ["code"] = "gold_common" })))!;
        pending["name"] = "Synthetic unrelated pending edit";
        var open = await changes.SetChangeAsync("promo", new JsonObject { ["code"] = "gold_common" }, ContentOperation.Update, pending, null);
        var schedule = OperatorPanel.CreateContentSchedule(setup.Runtime.Game);
        var start = setup.Runtime.Game.Time.GetUtcNow().AddHours(1);
        var drop = await schedule.SaveDropAsync(null, null, "Synthetic scheduled edit", start, start.AddHours(1), false, 0);
        var future = (await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => new PromoArea().ReadAsync(w, new JsonObject { ["code"] = "gold_rare" })))!;
        future["name"] = "Synthetic future edit";
        var draft = await schedule.Draft(drop.ChangesetId).SetChangeAsync("promo", new JsonObject { ["code"] = "gold_rare" }, ContentOperation.Update, future, null);
        var row = (await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => new PromoArea().ReadAsync(w, new JsonObject { ["code"] = "silver_common" })))!;
        var before = row.DeepClone();
        row["name"] = "Synthetic live edit";
        var revision = setup.Runtime.Game.Live.Current.Revision;
        var reply = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes?apply=1", new JsonObject
        {
            ["area"] = "promo", ["key"] = new JsonObject { ["code"] = "silver_common" }, ["operation"] = "update", ["after"] = row
        }.ToJsonString());
        Assert.True(reply.Status == 200, reply.Body);
        Assert.Equal("published", reply.Json!["changeset"]!["status"]!.GetValue<string>());
        var saved = Assert.Single((await changes.HistoryAsync(10)));
        Assert.NotEqual(open!.Id, saved.Id);
        Assert.NotEqual(draft!.Id, saved.Id);
        Assert.True(JsonNode.DeepEquals(before, Assert.Single(saved.Changes).Before));
        Assert.True(JsonNode.DeepEquals(row, Assert.Single(saved.Changes).After));
        Assert.Equal(revision + 1, setup.Runtime.Game.Live.Current.Revision);
        Assert.Equal("Synthetic live edit", setup.Runtime.Game.Live.Current.Catalog.Promos.Single(p => p.Code == "silver_common").Name);
        Assert.NotEqual("Synthetic unrelated pending edit", setup.Runtime.Game.Live.Current.Catalog.Promos.Single(p => p.Code == "gold_common").Name);
        Assert.NotEqual("Synthetic future edit", setup.Runtime.Game.Live.Current.Catalog.Promos.Single(p => p.Code == "gold_rare").Name);
        Assert.Equal(open.Id, (await changes.OpenAsync())!.Id);
        Assert.True(JsonNode.DeepEquals(pending, Assert.Single((await changes.OpenAsync())!.Changes).After));
        Assert.True(JsonNode.DeepEquals(future, Assert.Single((await schedule.Draft(drop.ChangesetId).OpenAsync())!.Changes).After));
    }

    [Fact]
    public async Task ImmediateContentApiFailureRollsBackTheWriteHistoryAndLiveSnapshot()
    {
        var area = new ImmediateFailureArea();
        using var setup = await CreateAsync(ContentAreas.Default.Concat<IContentArea>([area]).ToArray());
        var card = ContentChangesetTests.Card(setup.Runtime, 0);
        var key = ContentChangesetTests.Key(card.Definition);
        var before = (await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => area.ReadAsync(w, key)))!;
        var revision = setup.Runtime.Game.Live.Current.Revision;
        var count = await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => w.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.content_changesets"));
        var reply = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes?apply=1", new JsonObject
        {
            ["area"] = area.Name, ["key"] = key, ["operation"] = "update", ["after"] = ContentChangesetTests.Rating(card.Rating == 99 ? 98 : card.Rating + 1)
        }.ToJsonString());
        Assert.Equal(422, reply.Status);
        Assert.True(JsonNode.DeepEquals(before, await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => area.ReadAsync(w, key))));
        Assert.Equal(revision, setup.Runtime.Game.Live.Current.Revision);
        Assert.Equal(card.Rating, setup.Runtime.Game.Players.Find(card.Resource)!.Rating);
        Assert.Equal(count, await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => w.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.content_changesets")));
        Assert.Empty(await new ContentChangesets(setup.Runtime.Game, ContentAreas.Default).HistoryAsync(10));
    }

    [Fact]
    public async Task ImmediateContentApiPackSaveUsesLiveDefinitionWithoutMergingLegacyPendingFields()
    {
        using var setup = await CreateAsync();
        var catalog = await Send(setup.Panel, "GET", "/panel/api/packs/catalog");
        Assert.Equal(200, catalog.Status);
        var existing = catalog.Json!["packs"]!.AsArray().First(p => p!["viewOnlyReason"] is null)!;
        var id = existing["id"]!.GetValue<long>();
        var originalBio = existing["config"]!["bio"]?.DeepClone();
        var revision = catalog.Json["revision"]!.GetValue<string>();
        var staged = await Send(setup.Panel, "PUT", $"/panel/api/packs/catalog/{id}", new JsonObject
        {
            ["revision"] = revision, ["pack"] = new JsonObject { ["bio"] = "Synthetic unpublished pack blurb" }
        }.ToJsonString());
        Assert.True(staged.Status == 200, staged.Body);
        var saved = await Send(setup.Panel, "PUT", $"/panel/api/packs/catalog/{id}?apply=1", new JsonObject
        {
            ["revision"] = revision, ["pack"] = new JsonObject { ["name"] = "Synthetic immediate pack name" }
        }.ToJsonString());
        Assert.True(saved.Status == 200, saved.Body);
        var live = await Send(setup.Panel, "GET", "/panel/api/packs/catalog");
        var actual = live.Json!["packs"]!.AsArray().Single(p => p!["id"]!.GetValue<long>() == id)!["config"]!;
        Assert.Equal("Synthetic immediate pack name", actual["name"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(originalBio, actual["bio"]));
        var pending = await Send(setup.Panel, "GET", "/panel/api/content/changeset");
        Assert.Equal(1, pending.Json!["pending"]!.GetValue<int>());
        Assert.Equal("Synthetic unpublished pack blurb", Assert.Single(pending.Json["changeset"]!["changes"]!.AsArray())!["after"]!["pack"]!["bio"]!.GetValue<string>());
    }

    private sealed class ImmediateFailureArea : ContentChangesetTests.RatingArea
    {
        public override string Name => "test_immediate_failure";
        public override async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            await base.ApplyAsync(work, operation, key, after);
            throw new InvalidDataException("Synthetic failure after writing content.");
        }
    }
}
