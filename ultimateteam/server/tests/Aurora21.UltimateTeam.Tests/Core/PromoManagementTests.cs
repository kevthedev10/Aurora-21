using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Tests.Api;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class PromoManagementTests
{
    [Fact]
    public async Task Pre118DraftPublishesWithoutClearingNewMarks()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        var original = await Read(rt, new PromoArea(), "gold_rare");
        var edited = original.DeepClone().AsObject();
        edited["name"] = "Renamed Gold Rare";
        edited.Remove("card_reductions");
        await changes.SetChangeAsync("promo", Key("gold_rare"), ContentOperation.Update, edited, null);
        await rt.Game.UnitOfWork.RunAsync(0, "synthetic-pre118-draft", work => work.ExecuteAsync("""
            UPDATE ultimateteam.content_changes SET before_value=before_value-'card_reductions'
            WHERE area='promo' AND target_key->>'code'='gold_rare'
            """));
        await changes.PublishAsync(null, null);
        var current = await Read(rt, new PromoArea(), "gold_rare");
        Assert.Equal("Renamed Gold Rare", current["name"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(original["card_reductions"], current["card_reductions"]));
    }

    [Fact]
    public async Task CardReductionsPreviewPublishMoveClearAndRevertWithoutChangingBucketOdds()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        var area = new PromoArea();
        var original = await Read(rt,area,"gold_rare");
        var edited = original.DeepClone().AsObject();
        edited["card_reductions"]!["225100"] = 10m;
        await changes.SetChangeAsync("promo",Key("gold_rare"),ContentOperation.Update,edited,null);
        var preview = await changes.PreviewAsync();
        var eligible = new HashSet<long> { 225100,228618 };
        var distribution = preview.PromoCardSelector.Distribution("gold_rare",eligible);
        Assert.Equal(2,distribution[228618]/distribution[225100],10);
        Assert.True(JsonNode.DeepEquals(original,await Read(rt,area,"gold_rare")));
        var published = await changes.PublishAsync(null,null);
        var current = await Read(rt,area,"gold_rare");
        Assert.Equal(10m,current["card_reductions"]!["225100"]!.GetValue<decimal>());
        Assert.True(JsonNode.DeepEquals(original["buckets"],current["buckets"]));

        var moving = current.DeepClone().AsObject();
        var donor = moving["buckets"]!.AsArray().OfType<JsonObject>().Single(b => PromoEdits.Cards(b).Contains(225100));
        donor["cards"]!.AsArray().Remove(donor["cards"]!.AsArray().Single(card => card!.GetValue<long>()==225100));
        moving["buckets"]!.AsArray().Add(new JsonObject { ["code"]="synthetic_single",["name"]="Synthetic singleton",
            ["draw_weight"]=1m,["enabled"]=true,["cards"]=new JsonArray(225100L) });
        // A payload from before the new field existed preserves marks while moving cards.
        moving.Remove("card_reductions");
        await changes.SetChangeAsync("promo",Key("gold_rare"),ContentOperation.Update,moving,null);
        await changes.PublishAsync(null,null);
        Assert.Equal(10m,(await Read(rt,area,"gold_rare"))["card_reductions"]!["225100"]!.GetValue<decimal>());

        var clearing = (await Read(rt,area,"gold_rare")).DeepClone().AsObject();
        clearing["card_reductions"] = new JsonObject();
        await changes.SetChangeAsync("promo",Key("gold_rare"),ContentOperation.Update,clearing,null);
        await changes.PublishAsync(null,null);
        Assert.Empty((await Read(rt,area,"gold_rare"))["card_reductions"]!.AsObject());
        // Restore the first publication's baseline, including its original marks and bucket membership.
        await changes.RevertAsync(published.Changeset.Id,null);
        await changes.PublishAsync(null,null);
        Assert.True(JsonNode.DeepEquals(original,await Read(rt,area,"gold_rare")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1000000")]
    [InlineData("1.1234567")]
    [InlineData("\"5\"")]
    [InlineData("null")]
    public async Task InvalidCardReductionsCannotBeStaged(string json)
    {
        using var rt = TestRuntime.Create(marketTarget:0);
        var edited = await Read(rt,new PromoArea(),"gold_rare");
        edited["card_reductions"]!["225100"] = JsonNode.Parse(json);
        var changes = new ContentChangesets(rt.Game,ContentAreas.Default);
        Assert.Equal(400,(await Assert.ThrowsAsync<ContentChangeException>(() =>
            changes.SetChangeAsync("promo",Key("gold_rare"),ContentOperation.Update,edited,null))).Status);
    }

    [Fact]
    public async Task ReductionsMustNameMembersAndGoldCommonRemainsRatingOnly()
    {
        using var rt = TestRuntime.Create(marketTarget:0);
        var changes = new ContentChangesets(rt.Game,ContentAreas.Default);
        foreach (var (pool,id) in new[] { ("gold_rare",259431L),("gold_common",157481L) })
        {
            var edited = await Read(rt,new PromoArea(),pool);
            edited["card_reductions"] = new JsonObject { [id.ToString()] = 5m };
            Assert.Equal(400,(await Assert.ThrowsAsync<ContentChangeException>(() =>
                changes.SetChangeAsync("promo",Key(pool),ContentOperation.Update,edited,null))).Status);
        }
    }

    private static JsonObject Key(string code) => new() { ["code"] = code };
    private static async Task<JsonObject> Read(TestRuntime rt, IContentArea area, string code) =>
        (await rt.Game.UnitOfWork.RunReadAsync(0, w => area.ReadAsync(w, Key(code))))!;
    private static JsonObject Window(string promo, bool rotation = false) => new()
    {
        ["name"] = "Synthetic window", ["enabled"] = true, ["starts_at"] = "2026-10-01T00:00:00Z", ["ends_at"] = "2026-10-08T00:00:00Z",
        [rotation ? "promo" : "promos"] = rotation ? JsonValue.Create(promo) : new JsonArray(promo),
    };

    [Fact]
    public async Task EmptyBucketsAndDisabledMembershipSurviveStagingPublicationAndRevert()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        var area = new PromoArea();
        var initial = await Read(rt, area, "totw_1");
        var disabled = initial.DeepClone().AsObject();
        disabled["buckets"]![0]!["enabled"] = false;
        await changes.SetChangeAsync("promo", Key("totw_1"), ContentOperation.Update, disabled, null);
        await changes.PublishAsync(null, null);
        var original = await Read(rt, area, "totw_1");
        Assert.False(original["buckets"]![0]!["enabled"]!.GetValue<bool>());
        var disabledCode = original["buckets"]![0]!["code"]!.GetValue<string>();
        var allCards = original["buckets"]!.AsArray().SelectMany(b => PromoEdits.Cards(b!.AsObject())).Order().ToArray();
        var edit = original.DeepClone().AsObject();
        edit["buckets"]!.AsArray().Add(new JsonObject { ["code"] = "empty_manual", ["name"] = "Manual cards", ["draw_weight"] = 0.000001m, ["enabled"] = true, ["cards"] = new JsonArray() });
        var donor = edit["buckets"]!.AsArray().OfType<JsonObject>().First(b => b["enabled"]!.GetValue<bool>() && b["cards"]!.AsArray().Count > 0);
        var moving = donor["cards"]!.DeepClone();
        donor["cards"] = new JsonArray();
        edit["buckets"]!.AsArray().Add(new JsonObject { ["code"] = "range_seeded", ["name"] = "Rating seeded", ["draw_weight"] = 2m, ["enabled"] = true, ["cards"] = moving });
        await changes.SetChangeAsync("promo", Key("totw_1"), ContentOperation.Update, edit, null);
        Assert.True((await changes.ValidateAsync()).Ok);
        Assert.True(JsonNode.DeepEquals(original, await Read(rt, area, "totw_1")));
        var published = await changes.PublishAsync(null, null);
        var after = await Read(rt, area, "totw_1");
        Assert.Equal(allCards, after["buckets"]!.AsArray().SelectMany(b => PromoEdits.Cards(b!.AsObject())).Order());
        Assert.False(after["buckets"]!.AsArray().Single(b => b!["code"]!.GetValue<string>() == disabledCode)!["enabled"]!.GetValue<bool>());
        Assert.Contains(after["buckets"]!.AsArray(), b => b!["code"]!.GetValue<string>() == "empty_manual" && b["cards"]!.AsArray().Count == 0);
        await changes.RevertAsync(published.Changeset.Id, null);
        await changes.PublishAsync(null, null);
        Assert.True(JsonNode.DeepEquals(original, await Read(rt, area, "totw_1")));
    }

    [Fact]
    public async Task BucketMovesRemovalAndRevertAreAtomicAndNeverDeletePlayerDefinitions()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        var area = new PromoArea();
        var original = await Read(rt, area, "totw_1");
        var edited = original.DeepClone().AsObject();
        var buckets = edited["buckets"]!.AsArray();
        var donor = buckets.OfType<JsonObject>().First(b => b["cards"]!.AsArray().Count > 2);
        var resource = donor["cards"]![0]!.GetValue<long>();
        donor["cards"]!.AsArray().RemoveAt(0);
        var removed = donor["cards"]![0]!.GetValue<long>();
        donor["cards"]!.AsArray().RemoveAt(0);
        buckets.Add(new JsonObject { ["code"] = "single_player", ["name"] = "Single player", ["draw_weight"] = 0.1m, ["cards"] = new JsonArray(resource) });
        await changes.SetChangeAsync("promo", Key("totw_1"), ContentOperation.Update, edited, null);
        var preview = await changes.PreviewAsync();
        var promoId = preview.Catalog.Promos.Single(p => p.Code == "totw_1").PromoId;
        Assert.DoesNotContain(preview.Catalog.PromoCards.Where(m => m.PromoId == promoId), m => preview.Catalog.DefinitionsById[m.PlayerCardDefinitionId].NativeResourceId!.Value.Value == removed);
        Assert.NotNull(preview.Players.Find(removed));
        Assert.True(JsonNode.DeepEquals(original, await Read(rt, area, "totw_1")));
        var published = await changes.PublishAsync(null, null);
        Assert.NotNull(rt.Game.Players.Find(removed));
        Assert.NotNull(rt.Game.Players.Find(resource));
        await changes.RevertAsync(published.Changeset.Id, null);
        await changes.PublishAsync(null, null);
        Assert.True(JsonNode.DeepEquals(original, await Read(rt, area, "totw_1")));
        Assert.DoesNotContain(rt.Game.Live.Current.Catalog.PromoBuckets, b => b.PromoId == promoId && b.Code == "single_player" && b.Enabled);
        await using var db = NpgsqlDataSource.Create(rt.ConnectionString);
        await using var delete = db.CreateCommand($"DELETE FROM ultimateteam.promo_cards WHERE promo_id={promoId}");
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task NewPromoAndEventCanPublishTogetherAndRestrictedEventsAreRefused()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        var promo = await Read(rt, new PromoArea(), "totw_5");
        promo["name"] = "Synthetic promo";
        await changes.SetChangeAsync("promo", Key("synthetic_promo"), ContentOperation.Insert, promo, null);
        await changes.SetChangeAsync("promo_event", Key("synthetic_event"), ContentOperation.Insert, Window("synthetic_promo"), null);
        Assert.True((await changes.ValidateAsync()).Ok);
        var published = await changes.PublishAsync(null, null);
        var at = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        Assert.Contains(published.Snapshot.Catalog.Promos.Single(p => p.Code == "synthetic_promo").PromoId, published.Snapshot.PromoAvailability.ActivePromoIds(at));
        foreach (var restricted in new[] { "mid_icons", "fut_champions", "developer_cards", "gold_rare" })
            await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync("promo_event", Key("bad_event"), ContentOperation.Insert, Window(restricted), null));
        await changes.RevertAsync(published.Changeset.Id, null);
        await changes.PublishAsync(null, null);
        Assert.DoesNotContain(rt.Game.Live.Current.Catalog.PromoEvents, e => e.Code == "synthetic_event" && e.Enabled);
    }

    [Fact]
    public async Task IconRotationReplacesTheTierAndOverlapValidationRollsBack()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        // This test authors its own calendar, replacing the default seeded windows atomically.
        foreach (var rotation in rt.Game.Live.Current.Catalog.IconRotations)
            await changes.SetChangeAsync("icon_rotation", Key(rotation.Code), ContentOperation.Hide, null, null);
        await changes.SetChangeAsync("icon_rotation", Key("mid_window"), ContentOperation.Insert, Window("mid_icons", true), null);
        await changes.PublishAsync(null, null);
        var snapshot = rt.Game.Live.Current;
        var start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var end = start.AddDays(7);
        Assert.Equal("base_icons", snapshot.Catalog.Promos.Single(p => p.PromoId == snapshot.PromoAvailability.IconPromoId(start.AddTicks(-1))).Code);
        Assert.Equal("mid_icons", snapshot.Catalog.Promos.Single(p => p.PromoId == snapshot.PromoAvailability.IconPromoId(start)).Code);
        Assert.Equal("base_icons", snapshot.Catalog.Promos.Single(p => p.PromoId == snapshot.PromoAvailability.IconPromoId(end)).Code);
        var baseId = snapshot.Catalog.Promos.Single(p => p.Code == "base_icons").PromoId;
        var baseCard = snapshot.Catalog.PromoCards.First(m => m.PromoId == baseId).PlayerCardDefinitionId;
        Assert.False(snapshot.PromoAvailability.IsAvailable(baseCard, start));
        Assert.True(snapshot.PromoAvailability.IsAvailable(baseCard, end));
        await changes.SetChangeAsync("icon_rotation", Key("overlap"), ContentOperation.Insert, Window("prime_icons", true), null);
        var validation = await changes.ValidateAsync();
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, e => e.Contains("overlap"));
        Assert.Equal(422, (await Assert.ThrowsAsync<ContentChangeException>(() => changes.PublishAsync(null, null))).Status);
        Assert.Same(snapshot, rt.Game.Live.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialIconCalendarUsesAmsterdamTimesAndPreservesOperatorRotations(bool custom)
    {
        using var first = TestRuntime.Create(marketTarget: 0);
        var snapshot = first.Game.Live.Current;
        Assert.Equal(4, snapshot.Catalog.IconRotations.Count(r => r.Enabled));
        string Tier(string time) => snapshot.Catalog.Promos.Single(p => p.PromoId == snapshot.PromoAvailability.IconPromoId(DateTimeOffset.Parse(time))).Code;
        Assert.Equal("base_icons", Tier("2026-09-30T17:00:00Z"));
        Assert.Equal("base_icons", Tier("2027-01-01T17:59:59Z"));
        Assert.Equal("mid_icons", Tier("2027-01-01T18:00:00Z"));
        Assert.Equal("prime_icons", Tier("2027-03-01T18:00:00Z"));
        Assert.Equal("prime_icon_moments", Tier("2027-05-01T17:00:00Z"));
        Assert.Equal("base_icons", Tier("2028-01-01T18:00:00Z"));
        Assert.Equal(3, snapshot.Catalog.PromoEventPromos.Count(l => l.Enabled));
        if (!custom) return;
        await first.Game.RunAsync("prepare-custom-icon-calendar", async work =>
        {
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                UPDATE ultimateteam.icon_rotations SET enabled=false;
                INSERT INTO ultimateteam.icon_rotations(code,name,promo_id,enabled,starts_at,ends_at)
                SELECT 'operator_icons','Operator calendar',promo_id,true,'2026-10-01T00:00:00Z','2027-10-01T00:00:00Z'
                FROM ultimateteam.promos WHERE code='mid_icons';
                """);
        }, CancellationToken.None);
        using var reopened = TestRuntime.Create(connectionString: first.Detach(), marketTarget: 0);
        var active = reopened.Game.Live.Current.Catalog.IconRotations.Where(r => r.Enabled).ToArray();
        Assert.Single(active);
        Assert.Equal("operator_icons", active[0].Code);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z").UtcDateTime, active[0].StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2027-10-01T00:00:00Z").UtcDateTime, active[0].EndsAt);
    }

    [Fact]
    public async Task InitialTotwWindowEndsAtTotwFourBoundaryOnFreshSetupAndRestart()
    {
        using var first = TestRuntime.Create(marketTarget: 0);
        void Check(TestRuntime rt)
        {
            var snapshot = rt.Game.Live.Current;
            var ev = snapshot.Catalog.PromoEvents.Single(e => e.Code == "initial_totw_1_3");
            var icon = snapshot.Catalog.IconRotations.Single(r => r.Code == "initial_base_icons");
            Assert.Equal(icon.StartsAt, ev.StartsAt);
            // Migration 174 ends TOTW 1–3 at the next TOTW release, independently of Icons.
            Assert.Equal(DateTimeOffset.Parse("2027-01-01T18:00:00Z").UtcDateTime, icon.EndsAt);
            Assert.Equal(DateTimeOffset.Parse("2026-09-30T17:00:00Z").UtcDateTime, ev.StartsAt);
            Assert.Equal(DateTimeOffset.Parse("2026-10-07T18:00:00Z").UtcDateTime, ev.EndsAt);
            var promos = snapshot.Catalog.PromoEventPromos.Where(l => l.EventId == ev.EventId && l.Enabled)
                .Select(l => snapshot.Catalog.Promos.Single(p => p.PromoId == l.PromoId)).ToArray();
            Assert.Equal(new[] { "totw_1", "totw_2", "totw_3" }, promos.Select(p => p.Code).Order());
            foreach (var promo in promos)
            {
                Assert.DoesNotContain(promo.PromoId, snapshot.PromoAvailability.ActivePromoIds(new DateTimeOffset(ev.StartsAt.AddTicks(-1))));
                Assert.Contains(promo.PromoId, snapshot.PromoAvailability.ActivePromoIds(new DateTimeOffset(ev.StartsAt)));
                Assert.Contains(promo.PromoId, snapshot.PromoAvailability.ActivePromoIds(new DateTimeOffset(ev.EndsAt.AddTicks(-1))));
                Assert.DoesNotContain(promo.PromoId, snapshot.PromoAvailability.ActivePromoIds(new DateTimeOffset(ev.EndsAt)));
            }
        }
        Check(first);
        using var reopened = TestRuntime.Create(connectionString: first.Detach(), marketTarget: 0);
        Check(reopened);
    }

    [Fact]
    public async Task InvalidMembershipAndDatesAreRejectedBeforeStaging()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        var promo = await Read(rt, new PromoArea(), "totw_1");
        var bucket = promo["buckets"]![0]!.AsObject();
        bucket["cards"]!.AsArray().Add(bucket["cards"]![0]!.DeepClone());
        await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync("promo", Key("totw_1"), ContentOperation.Update, promo, null));
        bucket["cards"] = new JsonArray(9876543210L);
        await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync("promo", Key("totw_1"), ContentOperation.Update, promo, null));
        var window = Window("totw_1");
        window["starts_at"] = "2026-10-01T00:00:00";
        await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync("promo_event", Key("bad_date"), ContentOperation.Insert, window, null));
        await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync("promo", Key("gold_rare"), ContentOperation.Hide, null, null));
        Assert.Null(await changes.OpenAsync());
    }

    [Fact]
    public async Task ExistingClubsAndPendingPreviewsRebuildNamedRewardPoolsForTheirRevision()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        var original = rt.Game.Rules.Totw.Week(1)!.Starters[0].ResourceId;
        var replacement = rt.Game.Rules.Totw.Week(4)!.Starters.Select(c => c.ResourceId).First(id => !rt.Fut.Pools.TotwOneToThreeCards.Contains(id));
        var oldPool = rt.Fut.Pools.TotwOneToThreeCards;
        Assert.Contains(original, rt.Fut.Pools.TotwOneToThreeCards);
        await changes.SetChangeAsync("rules", new JsonObject { ["file"] = "sqbt_totw", ["path"] = new JsonArray("weeks", 0, "starters", 0, "resourceId") },
            ContentOperation.Update, new JsonObject { ["value"] = replacement }, null);
        var preview = await changes.PreviewAsync();
        Assert.Contains(replacement, rt.Fut.Pools.Named("totw_1_3", preview)!);
        Assert.DoesNotContain(original, rt.Fut.Pools.Named("totw_1_3", preview)!);
        Assert.Contains(original, rt.Fut.Pools.TotwOneToThreeCards);
        await changes.PublishAsync(null, null);
        Assert.Contains(replacement, rt.Fut.Pools.TotwOneToThreeCards);
        Assert.DoesNotContain(original, rt.Fut.Pools.TotwOneToThreeCards);
        Assert.NotSame(oldPool, rt.Fut.Pools.TotwOneToThreeCards);
    }

    [Fact]
    public async Task SeededPackAndPickPreviewsAreReproducibleWithoutClubWrites()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var before = rt.Game.Store().ListItems("unassigned").Count;
        var snapshot = rt.Game.Live.Current;
        var at = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        foreach (var config in new[]
        {
            // Names its TOTW promo rather than relying on an event being live at the fixed time: with no live TOTW,
            // a TOTW-only rare slot falls back to a standard rare.
            new JsonObject { ["size"] = 3, ["gold_weight"] = 100, ["rare_slots"] = 3, ["include_general_pool"] = false,
                ["promo_codes"] = new JsonArray("totw_1"), ["category_chances"] = new JsonObject { ["totw"] = 1 } },
            new JsonObject { ["size"] = 1, ["include_general_pool"] = false, ["promo_codes"] = new JsonArray("totw_1"),
                ["player_picks"] = new JsonArray(new JsonObject { ["choice_count"] = 3, ["count"] = 1, ["promo_only"] = true }) },
        })
        {
            var first = rt.Fut.Packs.PreviewPlayerPack(config, GameRandom.Seeded("test-preview", 17), snapshot, at);
            var second = rt.Fut.Packs.PreviewPlayerPack(config, GameRandom.Seeded("test-preview", 17), snapshot, at);
            Assert.True(JsonNode.DeepEquals(first, second));
            Assert.All(first.OfType<JsonObject>(), item => Assert.True(item.Int("rareflag") > 1));
        }
        Assert.Equal(before, rt.Game.Store().ListItems("unassigned").Count);
        Assert.Same(snapshot, rt.Game.Live.Current);
    }
}
