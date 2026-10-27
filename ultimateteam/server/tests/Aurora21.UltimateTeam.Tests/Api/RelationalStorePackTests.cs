using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Migration 041 moved store pack configuration out of rules/packs.json, store_packs.json and the
/// personal pack_settings.json. These checks prove the seeded rows reproduce what the file merge
/// produced, so the move changed storage and not behaviour.
/// </summary>
public sealed class RelationalStorePackTests
{
    [Fact]
    public async Task TokenOfferCollisionUpgradePreservesEditedRewardPackAndUnopenedGrants()
    {
        using var rt = Create();
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using (var command = source.CreateCommand("""
            DELETE FROM ultimateteam.schema_migrations WHERE version=158;
            UPDATE ultimateteam.token_store_listings SET offer_id=564 WHERE offer_id=571;
            SELECT set_config('ultimateteam.content_publish','on',false);
            UPDATE ultimateteam.store_packs SET name='Edited 75+ reward',size=2 WHERE pack_id=564;
            SELECT set_config('ultimateteam.content_publish','',false);
            INSERT INTO ultimateteam.unopened_packs(club_id,pack_id,source,created,untradeable,contents)
            SELECT id,564,'synthetic',1700000000,true,'[{"synthetic":true}]' FROM ultimateteam.clubs;
            """)) await command.ExecuteNonQueryAsync();
        await using (var command = source.CreateCommand("SELECT count(*) FROM ultimateteam.unopened_packs WHERE pack_id=564"))
            Assert.True((long)(await command.ExecuteScalarAsync())! > 0);
        const string snapshot = """
            SELECT jsonb_build_object(
                'definition',(SELECT to_jsonb(p) FROM ultimateteam.store_packs p WHERE pack_id=564),
                'grants',(SELECT jsonb_agg(to_jsonb(p)) FROM ultimateteam.unopened_packs p WHERE pack_id=564),
                'offers',(SELECT jsonb_agg(to_jsonb(t)-'offer_id' ORDER BY pack_id) FROM ultimateteam.token_store_listings t)
            )::text
            """;
        async Task<string> Snapshot()
        {
            await using var command = source.CreateCommand(snapshot);
            return (string)(await command.ExecuteScalarAsync())!;
        }
        var before = await Snapshot();
        // The fixture already has today's schema. Replay only the target migration rather
        // than removing the ledger for unrelated later migrations whose objects exist.
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream(
                "DatabaseSetup/migrations/158-token-offer-id-collisions.sql")!;
            using var reader = new StreamReader(stream);
            await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(), connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        await RelationalSchemaBootstrap.InstallForRuntimeAsync(source);
        Assert.Equal(before, await Snapshot());
        await using (var command = source.CreateCommand("""
            SELECT count(*) FROM ultimateteam.token_store_listings t
            JOIN ultimateteam.store_packs p ON p.pack_id=t.offer_id
            """)) Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        await RelationalSchemaBootstrap.InstallForRuntimeAsync(source);
        Assert.Equal(before, await Snapshot());
    }

    /// <summary>A runtime on the relational backend, where migration 041's tables live.</summary>
    private static TestRuntime Create(string? seedSql = null)
    {
        var connection = PostgresTestCluster.CreateInstalledDatabase();
        using (var source = NpgsqlDataSource.Create(connection))
        {
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
        }
        return TestRuntime.Create(connectionString: connection, relationalSlice: true, initializeRelationalClub: true, seedSql: seedSql);
    }

    /// <summary>One kind of pack since migration 119: the former reward rows are ordinary packs the store does not sell.</summary>
    [Fact]
    public async Task FormerRewardPacksAreOrdinaryPacksOutsideTheStore()
    {
        using var rt = Create();
        var kinds = await rt.Game.RunAsync("kinds", async w => (await w.QueryAsync(
            "SELECT kind, count(*) FROM ultimateteam.store_packs GROUP BY kind", r => (r.GetString(0), r.GetInt64(1)))).ToDictionary(),
            CancellationToken.None);
        Assert.False(kinds.ContainsKey("reward"));
        foreach (var id in new long[] { 100, 200, 302, 304, 305, 315, 319, 400, 405, 513, 518, 534, 540, 541, 542, 544, 550, 551, 552, 553, 560 })
        {
            var stored = await rt.Game.RunAsync("former-reward-pack", w => w.StorePackAsync(id, "store"), CancellationToken.None);
            Assert.True(stored is not null, $"pack {id} is missing from store_packs");
            Assert.False(rt.Game.Packs.IsForSale(id), $"pack {id} joined the store");
            Assert.Equal(stored!.Text("name"), rt.Game.Rules.Packs.Pack(id)!.Text("name"));
        }
        var template = await rt.Game.RunAsync("template", w => w.StorePackAsync(0, "template"), CancellationToken.None);
        Assert.NotNull(template);
        Assert.Equal(rt.Game.Rules.Packs.CustomPack.Int("size"), template!.Int("size"));
    }

    /// <summary>Every owned pack must reference a configured pack, which the files could not enforce.</summary>
    [Fact]
    public async Task EveryPackIdTheServerCanGrantIsConfigured()
    {
        using var rt = Create();
        var configured = await rt.Game.RunAsync("configured", async w =>
            (await w.QueryAsync("SELECT pack_id FROM ultimateteam.store_packs", r => r.GetInt64(0))).ToHashSet(),
            CancellationToken.None);
        foreach (var packId in rt.Game.Rules.Packs.PlayerPickPacks.Values)
        {
            Assert.Contains(packId, configured);
        }
        foreach (var packId in rt.Game.Rules.Packs.PackNames.Keys)
        {
            Assert.Contains(packId, configured);
        }
    }

    /// <summary>
    /// Store definitions changed in 095; shared tier/art/economy rules and competition rewards retain their contracts.
    /// </summary>
    [Fact]
    public void DatabaseBackedRulesMatchTheFileRules()
    {
        using var rt = Create();
        var fromFiles = Aurora21.UltimateTeam.Core.Rules.GameRules.Load(TestRuntime.DataRoot).Packs;
        var fromDatabase = rt.Game.Rules.Packs;

        Assert.Equal(fromFiles.MaxPackSize, fromDatabase.MaxPackSize);
        Assert.Equal(fromFiles.MaxKitPackSize, fromDatabase.MaxKitPackSize);
        Assert.Equal(fromFiles.SpecialPackArtId, fromDatabase.SpecialPackArtId);
        Assert.Equal(fromFiles.StoreArtMaxPackId, fromDatabase.StoreArtMaxPackId);
        Assert.Equal(fromFiles.StoreGroups.OrderBy(e => e.Key), fromDatabase.StoreGroups.OrderBy(e => e.Key));
        Assert.Equal(fromFiles.StoreGroupArtIds.OrderBy(e => e.Key), fromDatabase.StoreGroupArtIds.OrderBy(e => e.Key));
        Assert.Equal(fromFiles.StoreGroupAliases.OrderBy(e => e.Key), fromDatabase.StoreGroupAliases.OrderBy(e => e.Key));
        Assert.Equal(fromFiles.PlayerPickPacks, fromDatabase.PlayerPickPacks);
        Assert.Equal(fromFiles.DisplayOnlyPackNames.OrderBy(e => e.Key), fromDatabase.DisplayOnlyPackNames.OrderBy(e => e.Key));
        Assert.Empty(fromDatabase.AlwaysPresentPackIds);
        Assert.True(JsonNode.DeepEquals(fromFiles.SpecialRarityWeights, fromDatabase.SpecialRarityWeights));
        foreach (var display in fromFiles.PackNames)
            Assert.Equal(display.Value, fromDatabase.PackNames[display.Key]);

        // Reward pack 564 retains its id; migration 158 moves the colliding Token Store offer to 571.
        Assert.Equal(572, fromDatabase.NextPackId);

        // The behaviour every caller actually invokes, across the id ranges the bands cover.
        foreach (var packId in new long[] { 1, 5, 42, 99, 100, 199, 200, 299, 300, 399, 400, 513, 516, 900 })
        {
            Assert.Equal(fromFiles.ArtId(packId), fromDatabase.ArtId(packId));
        }
        // Pack names come from the pack rows; the files keep only the display-only names.
        Assert.Equal("Gold Players Pack", fromDatabase.PackName(304));
        Assert.Equal("Silver Players Pack", fromDatabase.PackName(200));
        Assert.Equal("Pack 900", fromDatabase.PackName(900));
        foreach (var rating in new long[] { 40, 64, 65, 71, 72, 74, 75, 81, 82, 89, 90, 91, 94, 99 })
        {
            Assert.Equal(fromFiles.TierFor(rating), fromDatabase.TierFor(rating));
            Assert.Equal(fromFiles.EliteWeightFor(rating), fromDatabase.EliteWeightFor(rating));
            foreach (var tier in new[] { "gold", "silver", "bronze" })
            {
                Assert.Equal(fromFiles.IsFallbackRare(tier, rating), fromDatabase.IsFallbackRare(tier, rating));
            }
        }
    }

    /// <summary>Reviewed mixed packs retain the store wire fields and use their tier's art.</summary>
    [Fact]
    public void EnabledMixedPacksRenderTheConfiguredStoreWireFields()
    {
        using var rt = Create("UPDATE ultimateteam.store_packs SET enabled=true WHERE pack_id IN (74,75,78)");
        var purchase = rt.CallObject("GET", "/ut/game/fifa21/store/purchasegroup/all?ppInfo=true")["purchase"]!.AsArray();
        Assert.NotEmpty(purchase);
        // Draft entries share this response but are not packs; check the configured store packs only.
        var configured = rt.Game.Packs.Load().Obj("packs")!
            .Select(entry => long.Parse(entry.Key, CultureInfo.InvariantCulture)).ToHashSet();
        var checkedTiles = 0;
        foreach (var tile in purchase.OfType<JsonObject>())
        {
            var packId = tile["id"]!.GetValue<long>();
            if (!configured.Contains(packId)) continue;
            var config = rt.Game.Packs.GetPackConfig(packId);
            Assert.NotEmpty(config);
            // The wire calls the art band assetId; it comes straight from the rules now in the database.
            Assert.Equal(rt.Game.Rules.Packs.StoreArtId(packId, config), tile["assetId"]!.GetValue<long>());
            Assert.Equal(config.Text("name"), tile["name"]!.GetValue<string>());
            Assert.Equal(config.Text("bio"), tile["packDescription"]!.GetValue<string>());
            checkedTiles++;
        }
        Assert.Equal(10, checkedTiles); // the nine mixed packs and the My Stadium Pack (migration 123)
    }
}
