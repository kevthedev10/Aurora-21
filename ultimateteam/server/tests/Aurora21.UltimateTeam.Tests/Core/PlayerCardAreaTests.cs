using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Npgsql;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// Player card definitions as a content area (live-content-editing-plan.md, step 5): an edit of every loaded column is
/// staged, validated and published, and the running game's catalogue shows it after the reload; bad values are refused
/// when staged.
/// </summary>
public sealed class PlayerCardAreaTests
{
    [Fact]
    public async Task LegacyCardDraftsRemainValidAndRestagingCapturesTheType()
    {
        using var rt = TestRuntime.Create();
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var resource = rt.Game.Players.Entries.First(entry => entry.Value.Rareflag == 1).Key;
        var key = new JsonObject { ["resourceId"] = resource };
        var live = (await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key)))!;
        var card = (JsonObject)live["card"]!.DeepClone();
        card["face_slot_0"] = card["face_slot_0"]!.GetValue<long>() == 99 ? 98 : 99;
        await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update, new JsonObject { ["card"] = card.DeepClone() }, "tester");
        await rt.Game.UnitOfWork.RunAsync(0, "legacy-card-fixture", work => work.ExecuteAsync("UPDATE ultimateteam.content_changes SET before_value=before_value-'cardTypeId' WHERE area='player_card'"));
        Assert.True((await changes.ValidateAsync()).Ok);
        await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update,
            new JsonObject { ["card"] = card, ["cardTypeId"] = live["cardTypeId"]!.DeepClone() }, "tester");
        var pending = (await changes.OpenAsync())!;
        Assert.NotNull(Assert.Single(pending.Changes).Before!["cardTypeId"]);
        Assert.True((await changes.ValidateAsync()).Ok);
        await changes.PublishAsync("publish upgraded draft", "tester");
    }

    [Fact]
    public async Task IconEditorRestrictsTypesAndMinimumRating()
    {
        using var rt = TestRuntime.Create();
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var icon = rt.Game.Players.Entries.First(entry => entry.Value.Rareflag == 12);
        var regular = rt.Game.Players.Entries.First(entry => entry.Value.Rareflag == 1);
        async Task<long> Type(long flag) => await rt.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<long>("""
            SELECT min(t.card_type_id) FROM ultimateteam.card_types t JOIN ultimateteam.native_rarities r USING(native_rarity_id) WHERE r.native_rareflag=@flag
            """, ("flag", flag)));
        async Task<JsonObject> Card(long resource) => (JsonObject)(await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, new JsonObject { ["resourceId"] = resource })))!["card"]!.DeepClone();
        long Free(long asset) { var id = asset + PlayerCatalog.ResourceRevisionSize; while (rt.Game.Players.Find(id) is not null) id += PlayerCatalog.ResourceRevisionSize; return id; }
        var iconCard = await Card(icon.Key);
        iconCard["rating"] = 84;
        Assert.Equal(400, (await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(PlayerCardArea.AreaName,
            new JsonObject { ["resourceId"] = icon.Key }, ContentOperation.Update, new JsonObject { ["card"] = iconCard.DeepClone() }, null))).Status);
        var iconId = Free(icon.Value.Int("assetId"));
        async Task<int> Refused(long from, long id, long type, JsonObject card) => (await Assert.ThrowsAsync<ContentChangeException>(() =>
            changes.SetChangeAsync(PlayerCardArea.AreaName, new JsonObject { ["resourceId"] = id }, ContentOperation.Insert,
                new JsonObject { ["base"] = from, ["cardTypeId"] = type, ["card"] = card.DeepClone() }, null))).Status;
        Assert.Equal(400, await Refused(icon.Key, iconId, await Type(12), iconCard));
        iconCard["rating"] = 85;
        Assert.Equal(400, await Refused(icon.Key, iconId, await Type(3), iconCard));
        Assert.Equal(400, await Refused(regular.Key, Free(regular.Value.Int("assetId")), await Type(12), await Card(regular.Key)));
        Assert.Equal(400, await Refused(regular.Key, Free(regular.Value.Int("assetId")), await Type(89), await Card(regular.Key)));
        await changes.SetChangeAsync(PlayerCardArea.AreaName, new JsonObject { ["resourceId"] = iconId }, ContentOperation.Insert,
            new JsonObject { ["base"] = icon.Key, ["cardTypeId"] = await Type(84), ["card"] = iconCard }, "tester");
        await changes.PublishAsync("test Icon version", "tester");
        Assert.Equal(84, rt.Game.Players.Find(iconId)!.Rareflag);
        Assert.Equal(85, rt.Game.Players.Find(iconId)!.Rating);
    }

    [Fact]
    public async Task InformRatingChangesPreserveSpecialIdentityUntilBronze()
    {
        using var rt = TestRuntime.Create();
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var resource = rt.Game.Players.Entries.First(entry => entry.Value.Rareflag == 3).Key;
        var key = new JsonObject { ["resourceId"] = resource };
        long bronzeChange = 0;
        foreach (var (rating, flag, level) in new[] { (74, 3, "silver"), (75, 3, "gold"), (64, 1, "bronze") })
        {
            var card = (JsonObject)(await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key)))!["card"]!.DeepClone();
            card["rating"] = rating;
            await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update, new JsonObject { ["card"] = card }, "tester");
            var published = await changes.PublishAsync("test Inform rating", "tester");
            if (rating == 64) bronzeChange = published.Changeset.Id;
            Assert.Equal(flag, rt.Game.Players.Find(resource)!.Rareflag);
            var actualLevel = await rt.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<string>("""
                SELECT t.card_level FROM ultimateteam.player_card_definitions p JOIN ultimateteam.card_types t USING(card_type_id)
                JOIN ultimateteam.item_definitions d USING(definition_id) WHERE d.native_resource_id=@resource AND d.family_id=1
                """, ("resource", resource)));
            Assert.Equal(level, actualLevel.ToLowerInvariant());
        }
        await changes.RevertAsync(bronzeChange, "tester");
        await changes.PublishAsync("restore Inform identity", "tester");
        Assert.Equal(3, rt.Game.Players.Find(resource)!.Rareflag);
        Assert.Equal(75, rt.Game.Players.Find(resource)!.Rating);
    }

    [Fact]
    public async Task StandardCardTypeFollowsRatingBandsOnEditAndInsert()
    {
        using var rt = TestRuntime.Create();
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var (_, resource, _) = ContentChangesetTests.Card(rt, 0);
        var key = new JsonObject { ["resourceId"] = resource };
        var rarity = rt.Game.Players.Find(resource)!.Rareflag;
        async Task<string> Level(long id) => await rt.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<string>("""
            SELECT t.card_level FROM ultimateteam.player_card_definitions p JOIN ultimateteam.card_types t USING(card_type_id)
            JOIN ultimateteam.item_definitions d USING(definition_id) WHERE d.native_resource_id=@id AND d.family_id=1
            """, ("id", id)));
        foreach (var (rating, level) in new[] { (64, "bronze"), (65, "silver"), (74, "silver"), (75, "gold") })
        {
            var card = (JsonObject)(await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key)))!["card"]!.DeepClone();
            card["rating"] = rating;
            await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update, new JsonObject { ["card"] = card }, "tester");
            await changes.PublishAsync("test rating band", "tester");
            Assert.Equal(level, (await Level(resource)).ToLowerInvariant());
            Assert.Equal(rarity, rt.Game.Players.Find(resource)!.Rareflag);
        }
        var finalCard = (JsonObject)(await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key)))!["card"]!.DeepClone();
        finalCard["rating"] = 64;
        var type = await rt.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<long>("""
            SELECT p.card_type_id FROM ultimateteam.player_card_definitions p JOIN ultimateteam.item_definitions d USING(definition_id)
            WHERE d.native_resource_id=@id AND d.family_id=1
            """, ("id", resource)));
        var asset = rt.Game.Players.Find(resource)!.Int("assetId");
        var newId = asset + PlayerCatalog.ResourceRevisionSize;
        while (rt.Game.Players.Find(newId) is not null) newId += PlayerCatalog.ResourceRevisionSize;
        await changes.SetChangeAsync(PlayerCardArea.AreaName, new JsonObject { ["resourceId"] = newId }, ContentOperation.Insert,
            new JsonObject { ["base"] = resource, ["cardTypeId"] = type, ["card"] = finalCard }, "tester");
        await changes.PublishAsync("test new bronze card", "tester");
        Assert.Equal("bronze", (await Level(newId)).ToLowerInvariant());
    }

    [Fact]
    public async Task AnEditedCardShowsInTheGameAfterPublish()
    {
        using var rt = TestRuntime.Create();
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var (_, resource, _) = ContentChangesetTests.Card(rt, 0);
        var key = new JsonObject { ["resourceId"] = resource };
        var live = await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key));
        var card = (JsonObject)live!["card"]!.DeepClone();
        var rating = card["rating"]!.GetValue<long>();
        var newRating = rating == 99 ? 98 : rating + 1;
        var position = card["position"]!.GetValue<string>() == "ST" ? "CAM" : "ST";
        card["rating"] = newRating;
        card["position"] = position;
        card["face_slot_0"] = 99;

        var open = await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update, new JsonObject { ["card"] = card }, "tester");
        Assert.Single(open!.Changes);
        Assert.Equal(rating, rt.Game.Players.Find(resource)!.Rating);

        var validation = await changes.ValidateAsync();
        Assert.True(validation.Ok, string.Join("; ", validation.Errors));
        var published = await changes.PublishAsync("edit one card", "tester");

        var edited = rt.Game.Players.Find(resource)!;
        Assert.Equal(newRating, edited.Rating);
        Assert.Equal(position, edited.Position);
        var republished = await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key));
        Assert.Equal(99, republished!["card"]!["face_slot_0"]!.GetValue<long>());

        var reverting = await changes.RevertAsync(published.Changeset.Id, "tester");
        Assert.Single(reverting!.Changes);
        Assert.True((await changes.ValidateAsync()).Ok);
        await changes.PublishAsync("restore the card", "tester");
        Assert.Equal(rating, rt.Game.Players.Find(resource)!.Rating);
    }

    [Fact]
    public async Task BadValuesUnknownFieldsAndOtherOperationsAreRefusedWhenStaged()
    {
        using var rt = TestRuntime.Create();
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var (_, resource, _) = ContentChangesetTests.Card(rt, 0);
        var key = new JsonObject { ["resourceId"] = resource };
        var live = (JsonObject)(await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key)))!["card"]!;

        async Task<ContentChangeException> Refused(ContentOperation operation, Action<JsonObject> edit, JsonObject? forKey = null)
        {
            var card = (JsonObject)live.DeepClone();
            edit(card);
            return await Assert.ThrowsAsync<ContentChangeException>(() =>
                changes.SetChangeAsync(PlayerCardArea.AreaName, forKey ?? key, operation, new JsonObject { ["card"] = card }, null));
        }

        Assert.Equal(400, (await Refused(ContentOperation.Update, card => card["rating"] = 100)).Status);
        Assert.Equal(400, (await Refused(ContentOperation.Update, card => card["weak_foot"] = 6)).Status);
        Assert.Equal(400, (await Refused(ContentOperation.Update, card => card["acceleration"] = "fast")).Status);
        Assert.Equal(400, (await Refused(ContentOperation.Update, card => card["nickname"] = "x")).Status);
        Assert.Equal(400, (await Refused(ContentOperation.Update, card => card.Remove("stamina"))).Status);
        var position = await Refused(ContentOperation.Update, card => card["position"] = "WINGER");
        Assert.Contains("ST", position.Message);
        Assert.Equal(400, (await Refused(ContentOperation.Update, _ => { })).Status);
        Assert.Equal(400, (await Refused(ContentOperation.Hide, _ => { })).Status);
        Assert.Equal(404, (await Refused(ContentOperation.Update, card => card["rating"] = 50, new JsonObject { ["resourceId"] = 9_999_999_999 })).Status);
        Assert.Null(await changes.OpenAsync());
    }

    [Fact]
    public async Task ANewCardFromABaseCardJoinsTheGameAndTheBaseStaysAsItWas()
    {
        using var rt = TestRuntime.Create();
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var (_, baseResource, baseRating) = ContentChangesetTests.Card(rt, 0);
        var asset = rt.Game.Players.Find(baseResource)!.Int("assetId");
        var resource = asset + PlayerCatalog.ResourceRevisionSize;
        while (rt.Game.Players.Find(resource) is not null) resource += PlayerCatalog.ResourceRevisionSize;
        var cardType = await ScalarAsync(rt, "SELECT max(card_type_id) FROM ultimateteam.card_types");
        var card = (JsonObject)(await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, new JsonObject { ["resourceId"] = baseResource })))!["card"]!.DeepClone();
        card["rating"] = 97;

        JsonObject New(long id, long? type = null, long? from = null) => new()
        {
            ["base"] = from ?? baseResource, ["cardTypeId"] = type ?? cardType, ["card"] = card.DeepClone(),
        };
        async Task<int> Refused(long id, JsonObject after) => (await Assert.ThrowsAsync<ContentChangeException>(() =>
            changes.SetChangeAsync(PlayerCardArea.AreaName, new JsonObject { ["resourceId"] = id }, ContentOperation.Insert, after, null))).Status;

        // Outside the player's revision namespace, an existing card, a missing base card and an unknown card type are refused.
        Assert.Equal(400, await Refused(asset + 1, New(asset + 1)));
        Assert.Equal(409, await Refused(baseResource, New(baseResource)));
        Assert.Equal(404, await Refused(resource, New(resource, from: 9_999_999_999)));
        Assert.Equal(400, await Refused(resource, New(resource, type: 999_999)));

        await changes.SetChangeAsync(PlayerCardArea.AreaName, new JsonObject { ["resourceId"] = resource }, ContentOperation.Insert, New(resource), "tester");
        Assert.Null(rt.Game.Players.Find(resource));
        var validation = await changes.ValidateAsync();
        Assert.True(validation.Ok, string.Join("; ", validation.Errors));
        var published = await changes.PublishAsync("a new card", "tester");

        var added = rt.Game.Players.Find(resource)!;
        Assert.Equal(97, added.Rating);
        Assert.Equal(asset, added.Int("assetId"));
        Assert.Equal(baseRating, rt.Game.Players.Find(baseResource)!.Rating);
        Assert.Equal(await ScalarAsync(rt, $"SELECT count(*) FROM ultimateteam.player_card_traits t JOIN ultimateteam.item_definitions d ON d.definition_id=t.definition_id WHERE d.family_id=1 AND d.native_resource_id={baseResource}"),
            await ScalarAsync(rt, $"SELECT count(*) FROM ultimateteam.player_card_traits t JOIN ultimateteam.item_definitions d ON d.definition_id=t.definition_id WHERE d.family_id=1 AND d.native_resource_id={resource}"));
        var refused = await Assert.ThrowsAsync<ContentChangeException>(() => changes.RevertAsync(published.Changeset.Id, "tester"));
        Assert.Equal(409, refused.Status);
        Assert.Null(await changes.OpenAsync());
    }

    [Fact]
    public async Task TraitsChangeTheVisibleOnesOnlyAndAreMarkedAsOperatorEdits()
    {
        using var rt = TestRuntime.Create();
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var (definition, resource, _) = ContentChangesetTests.Card(rt, 0);
        var key = new JsonObject { ["resourceId"] = resource };
        var card = (JsonObject)(await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key)))!["card"]!.DeepClone();
        var hiddenBefore = await ScalarAsync(rt, $"SELECT count(*) FROM ultimateteam.player_card_traits l JOIN ultimateteam.traits t USING(trait_id) WHERE l.definition_id={definition} AND NOT t.is_visible");
        var current = card["traits"]!.AsArray().Select(node => node!.GetValue<long>()).ToList();
        var added = await ScalarAsync(rt, $"SELECT min(trait_id) FROM ultimateteam.traits WHERE is_visible AND trait_id NOT IN ({string.Join(",", current.Append(0))})");
        card["traits"] = new JsonArray([.. current.Skip(1).Append(added).Select(id => (JsonNode?)id)]);

        Assert.Equal(400, (await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update,
            new JsonObject { ["card"] = new JsonObject(card.Select(pair => KeyValuePair.Create(pair.Key, pair.Key == "traits" ? new JsonArray(999_999) : pair.Value?.DeepClone()))) }, null))).Status);
        await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update, new JsonObject { ["card"] = card }, "tester");
        await changes.PublishAsync("traits", "tester");

        var published = (await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key)))!["card"]!["traits"]!.AsArray().Select(node => node!.GetValue<long>()).ToList();
        Assert.Contains(added, published);
        if (current.Count > 0) Assert.DoesNotContain(current[0], published);
        Assert.Equal(hiddenBefore, await ScalarAsync(rt, $"SELECT count(*) FROM ultimateteam.player_card_traits l JOIN ultimateteam.traits t USING(trait_id) WHERE l.definition_id={definition} AND NOT t.is_visible"));
        Assert.Equal(1, await ScalarAsync(rt, $"SELECT count(*) FROM ultimateteam.player_card_traits l JOIN ultimateteam.traits t USING(trait_id) WHERE l.definition_id={definition} AND t.trait_id={added} AND l.source_native_player_id=0"));
        // The game reads traits as bit masks: the added trait's bit is now set.
        var bit = await ScalarAsync(rt, $"SELECT bit_value FROM ultimateteam.traits WHERE trait_id={added}");
        var word = await ScalarAsync(rt, $"SELECT mask_word FROM ultimateteam.traits WHERE trait_id={added}");
        Assert.NotEqual(0, rt.Game.Players.Find(resource)!.Int(word == 1 ? "trait1" : "trait2") & bit);
    }

    [Fact]
    public async Task ACardCanShowAnotherCardsHeadOfTheSamePlayerOrAnUploadedHead()
    {
        using var rt = TestRuntime.Create();
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        // A player with at least two cards: one shows the other's head.
        var versions = rt.Game.Players.Entries.GroupBy(entry => entry.Value.Int("assetId")).First(group => group.Key > 0 && group.Count() >= 2).Select(entry => entry.Key).Order().ToList();
        var (resource, other) = (versions[1], versions[0]);
        var asset = rt.Game.Players.Find(resource)!.Int("assetId");
        var stranger = rt.Game.Players.Entries.First(entry => entry.Value.Int("assetId") != asset).Key;
        var key = new JsonObject { ["resourceId"] = resource };
        var live = (JsonObject)(await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key)))!["card"]!;
        JsonObject With(string column, JsonNode? value) { var card = (JsonObject)live.DeepClone(); card[column] = value; return new JsonObject { ["card"] = card }; }

        Assert.Equal(400, (await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update, With("head_resource_id", stranger), null))).Status);
        await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update, With("head_resource_id", other), null);
        await changes.PublishAsync("another head", null);
        Assert.Equal(other, rt.Game.Players.Find(resource)!.Int("dynamicImageResourceId"));

        // An uploaded head of this player (another player's upload is refused), served to the game for this card.
        var dds = new byte[32528];
        "DDS "u8.CopyTo(dds);
        var head = await ScalarAsync(rt, $"INSERT INTO ultimateteam.custom_player_heads(native_asset_id,label,png,dds) VALUES({asset},'test','\\x89504e470d0a1a0a'::bytea,'\\x{Convert.ToHexStringLower(dds)}'::bytea) RETURNING head_id");
        var foreign = await ScalarAsync(rt, $"INSERT INTO ultimateteam.custom_player_heads(native_asset_id,label,png,dds) VALUES({asset + 1},'test','\\x89504e470d0a1a0a'::bytea,'\\x{Convert.ToHexStringLower(dds)}'::bytea) RETURNING head_id");
        live = (JsonObject)(await rt.Game.UnitOfWork.RunReadAsync(0, work => area.ReadAsync(work, key)))!["card"]!;
        Assert.Equal(400, (await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update, With("custom_head_id", foreign), null))).Status);
        await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update, With("custom_head_id", head), null);
        await changes.PublishAsync("uploaded head", null);
        Assert.Equal(head, rt.Game.Players.Find(resource)!.Int("customHeadId"));
        Assert.Equal(dds, await rt.Game.Content.GetPlayerHeadAsync(resource));
    }

    [Fact]
    public async Task EveryExistingCardKeepsTheHeadItHadBeforeTheEditorsColumns()
    {
        using var rt = TestRuntime.Create();
        // Migration 066's head columns start empty, so the loader changes no existing card's head.
        Assert.Equal(0, await ScalarAsync(rt,
            "SELECT count(*) FROM ultimateteam.player_card_definitions WHERE head_resource_id IS NOT NULL OR custom_head_id IS NOT NULL"));
    }

    private static async Task<long> ScalarAsync(TestRuntime rt, string sql)
    {
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
