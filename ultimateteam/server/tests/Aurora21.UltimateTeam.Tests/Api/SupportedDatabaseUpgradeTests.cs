using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Rules;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class SupportedDatabaseUpgradeTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 141)]
    [InlineData(true, 141)]
    [InlineData(false, 142)]
    [InlineData(true, 142)]
    [InlineData(false, 143)]
    [InlineData(true, 143)]
    public async Task SupportedUpgradePreservesClubStateAndRetriesAnInterruptedUpgrade(bool interrupt, int developVersion)
    {
        var connectionString = PostgresTestCluster.CreateDatabase();
        try
        {
            await using var source = NpgsqlDataSource.Create(connectionString);
            await Migration140Database.InstallAsync(source);
            async Task<T> Scalar<T>(string sql)
            {
                await using var command = source.CreateCommand(sql);
                return (T)(await command.ExecuteScalarAsync())!;
            }
            async Task Execute(string sql)
            {
                await using var command = source.CreateCommand(sql);
                await command.ExecuteNonQueryAsync();
            }
            var develop141 = developVersion >= 141;
            if (develop141)
            {
                // Develop used 141 for this exact migration; the merged history appends it as 150.
                using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream(
                    "DatabaseSetup/migrations/150-non-player-drop-table.sql")!;
                using var reader = new StreamReader(stream);
                await Execute((await reader.ReadToEndAsync()).Replace("VALUES(150)", "VALUES(141)", StringComparison.Ordinal));
                await Execute("""
                    INSERT INTO ultimateteam.content_rules(file,path,value,ordinal)
                    VALUES('pack_odds','["nonPlayerDrops","rareWeight"]','0.75',99999);
                    -- Even an operator marking 544 grant-only cannot hide develop's retained reward rules.
                    SELECT set_config('ultimateteam.content_publish','on',false);
                    UPDATE ultimateteam.store_packs SET admin_only=true WHERE pack_id=544;
                    SELECT set_config('ultimateteam.content_publish','',false);
                    """);
            }
            if (developVersion >= 142)
            {
                foreach (var migration in new[] { "156-gold-pack-mix.sql", "157-club-items-on-market.sql" }.Take(developVersion - 141))
                {
                    using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/" + migration)!;
                    using var reader = new StreamReader(stream);
                    var oldVersion = int.Parse(migration[..3]) - 14;
                    await Execute((await reader.ReadToEndAsync()).Replace($"VALUES({migration[..3]})", $"VALUES({oldVersion})", StringComparison.Ordinal));
                }
                // Preserve an operator edit made after develop's gold-mix migration, rather than applying it twice.
                await Execute("""
                    SELECT set_config('ultimateteam.content_publish','on',false);
                    UPDATE ultimateteam.store_packs SET mixed_contents=jsonb_set(mixed_contents,'{players}','{"min":7,"max":7}') WHERE pack_id=75;
                    SELECT set_config('ultimateteam.content_publish','',false);
                    """);
            }
            var goldMix = await Scalar<string>("SELECT mixed_contents::text FROM ultimateteam.store_packs WHERE pack_id=75");
            var baselineVersion = develop141 ? developVersion : Migration140Database.Version;
            Assert.Equal(baselineVersion, await Scalar<int>("SELECT max(version) FROM ultimateteam.schema_migrations"));
            Assert.False(await Scalar<bool>("SELECT to_regclass('ultimateteam.sbc_live') IS NOT NULL"));
            await Execute("""
                INSERT INTO ultimateteam.clubs(persona_id,nucleus_id,credits,fifa_points,next_item_id,sid,token,
                    established,last_purchase_transaction_id,last_match_create_is_draft,last_draft_slot)
                VALUES(987654321,987654322,123456,789,900000000001,'synthetic','synthetic',1,0,false,0);
                WITH owned AS (
                    INSERT INTO ultimateteam.items(club_id,native_item_id,definition_id,family_id,timestamp,
                        pile,item_state,untradeable,owners,last_sale_price)
                    SELECT c.id,900000000001,d.definition_id,1,1700000000,'club','free',false,1,0
                    FROM ultimateteam.clubs c CROSS JOIN LATERAL (
                        SELECT definition_id FROM ultimateteam.player_card_definitions ORDER BY definition_id LIMIT 1
                    ) d RETURNING id
                )
                INSERT INTO ultimateteam.player_items(item_id,contracts,fitness,injury_games,suspension,
                    loan_games,morale,training,assists,lifetime_assists,duplicate_acknowledged)
                SELECT id,7,99,0,0,0,0,0,0,0,false FROM owned;
                INSERT INTO ultimateteam.club_vanity_items(club_id,native_subtype,native_asset_id,stored)
                SELECT id,500,asset,asset=1 FROM ultimateteam.clubs CROSS JOIN generate_series(1,2) asset;
                INSERT INTO ultimateteam.unopened_packs(club_id,pack_id,source,created,untradeable,contents)
                SELECT id,75,'synthetic',1700000000,false,'[{"synthetic":true}]' FROM ultimateteam.clubs;
                INSERT INTO ultimateteam.objectives(club_id,user_xp,reward_claimed) SELECT id,6000,false FROM ultimateteam.clubs;
                INSERT INTO ultimateteam.objective_claims(club_id,scope,claim_id,ordinal) SELECT id,'level',1,0 FROM ultimateteam.clubs;
                INSERT INTO ultimateteam.match_settlements(club_id,match_key,response) SELECT id,'synthetic','{"settled":true}' FROM ultimateteam.clubs;
                """);
            const string Snapshot = """
                SELECT jsonb_build_object(
                    'club',(SELECT jsonb_agg(jsonb_build_array(id,credits,fifa_points,next_item_id)) FROM ultimateteam.clubs WHERE persona_id=987654321),
                    'items',(SELECT jsonb_agg(jsonb_build_array(i.id,i.native_item_id,i.definition_id,i.family_id,i.timestamp,i.pile,i.item_state,i.untradeable,i.owners,i.last_sale_price,i.reward_source)) FROM ultimateteam.items i JOIN ultimateteam.clubs c ON c.id=i.club_id WHERE c.persona_id=987654321),
                    'players',(SELECT jsonb_agg(jsonb_build_array(p.item_id,p.contracts,p.fitness,p.injury_games,p.suspension,p.loan_games,p.morale,p.training,p.assists,p.lifetime_assists,p.duplicate_acknowledged,p.match_stats)) FROM ultimateteam.player_items p JOIN ultimateteam.items i ON i.id=p.item_id JOIN ultimateteam.clubs c ON c.id=i.club_id WHERE c.persona_id=987654321),
                    'search',(SELECT jsonb_agg(jsonb_build_array(p.item_id,p.club_id,p.native_item_id,p.pile,p.rating,p.native_rareflag,p.resource_id)) FROM ultimateteam.club_player_search p JOIN ultimateteam.clubs c ON c.id=p.club_id WHERE c.persona_id=987654321),
                    'vanity',(SELECT jsonb_agg(jsonb_build_array(native_subtype,native_asset_id,stored) ORDER BY native_asset_id) FROM ultimateteam.club_vanity_items v JOIN ultimateteam.clubs c ON c.id=v.club_id WHERE c.persona_id=987654321),
                    'packs',(SELECT jsonb_agg(jsonb_build_array(pack_id,source,untradeable,contents)) FROM ultimateteam.unopened_packs p JOIN ultimateteam.clubs c ON c.id=p.club_id WHERE c.persona_id=987654321),
                    'xp',(SELECT user_xp FROM ultimateteam.objectives o JOIN ultimateteam.clubs c ON c.id=o.club_id WHERE c.persona_id=987654321),
                    'claims',(SELECT jsonb_agg(jsonb_build_array(scope,claim_id)) FROM ultimateteam.objective_claims o JOIN ultimateteam.clubs c ON c.id=o.club_id WHERE c.persona_id=987654321),
                    'settlements',(SELECT jsonb_agg(jsonb_build_array(match_key,response::jsonb)) FROM ultimateteam.match_settlements s JOIN ultimateteam.clubs c ON c.id=s.club_id WHERE c.persona_id=987654321)
                )::text
                """;
            var before = await Scalar<string>(Snapshot);
            var tokenPrices = JsonNode.Parse(await Scalar<string>("""
                SELECT coalesce(jsonb_agg(jsonb_build_object('packId',pack_id,'price',points_price,'enabled',coalesce(enabled,true),'untradeable',coalesce(store_untradeable,false))
                    ORDER BY pack_id),'[]'::jsonb)::text FROM ultimateteam.store_packs
                WHERE points_price>0 AND coalesce(store_listed,false) AND NOT coalesce(admin_only,false)
                """));
            if (interrupt)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => RelationalSchemaBootstrap.InstallFreshAsync(source,
                    (file, _, _, _) => file.StartsWith($"migrations/{RelationalCatalogMetadataMigration.CurrentVersion:D3}-", StringComparison.Ordinal)
                        ? throw new InvalidOperationException("synthetic upgrade interruption") : Task.CompletedTask,
                    CancellationToken.None));
                Assert.Equal(baselineVersion, await Scalar<int>("SELECT max(version) FROM ultimateteam.schema_migrations"));
                Assert.Equal(before, await Scalar<string>(Snapshot));
                Assert.False(await Scalar<bool>("SELECT to_regclass('ultimateteam.sbc_live') IS NOT NULL"));
                Assert.True(await Scalar<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='seasons' AND path->>0='matchPack')"));
            }
            using var upgraded = TestRuntime.Create(connectionString: connectionString, marketTarget: 0);
            Assert.Equal(before, await Scalar<string>(Snapshot));
            Assert.Equal(RelationalCatalogMetadataMigration.CurrentVersion, await Scalar<int>("SELECT max(version) FROM ultimateteam.schema_migrations"));
            if (develop141) Assert.Equal(0.75, upgraded.Game.Rules.PackOdds.NonPlayerDrops.RareWeight);
            if (developVersion >= 142) Assert.Equal(goldMix, await Scalar<string>("SELECT mixed_contents::text FROM ultimateteam.store_packs WHERE pack_id=75"));
            Assert.All(upgraded.Game.Rules.Objectives.GrindGroups.Where(group => group.SourceKey is not null), group => Assert.False(upgraded.Game.Rules.Objectives.IsLive(group.Id)));
            Assert.False(upgraded.Game.Sbc.IsLive("POTM_1"));
            Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name IN ('store_packs','owned_legacy_pack_definitions') AND column_name='store_section'"));
            Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM ultimateteam.store_packs WHERE store_section IS NOT NULL"));
            var migratedTokenPrices = JsonNode.Parse(await Scalar<string>("""
                SELECT coalesce(jsonb_agg(jsonb_build_object('packId',pack_id,'price',price,'enabled',enabled,'untradeable',untradeable)
                    ORDER BY pack_id),'[]'::jsonb)::text FROM ultimateteam.token_store_listings WHERE listed
                """));
            Assert.True(JsonNode.DeepEquals(tokenPrices, migratedTokenPrices));
            Assert.Equal(2500, upgraded.Game.Rules.Seasons.MatchCompletionRewards[RewardModes.Rivals].Coins);
            Assert.All(upgraded.Game.Rules.Seasons.MatchCompletionRewards.Values, mode => Assert.Equal(300, mode.Xp));
            var documents = upgraded.Game.InUnit(RuleRows.DocumentsAsync);
            var seasons = JsonNode.Parse(documents["seasons"])!.AsObject();
            Assert.False(seasons.ContainsKey("matchPack"));
            Assert.False(seasons.ContainsKey("matchRewards"));
            Assert.False(seasons.ContainsKey("matchCoinBonuses"));
            Assert.Null(seasons["rewardEligibility"]!["difficulty"]);
            Assert.False(JsonNode.Parse(documents["objectives"])!.AsObject().ContainsKey("xp"));
            Assert.DoesNotContain("hiddenSets", documents["sbc"], StringComparison.Ordinal);
            await Execute("""
                INSERT INTO ultimateteam.objective_claims(club_id,scope,claim_id,ordinal)
                SELECT id,'level_option',201,1 FROM ultimateteam.clubs WHERE persona_id=987654321
                """);
            var after = await Scalar<string>(Snapshot);
            using var reopened = TestRuntime.Create(connectionString: upgraded.Detach(), marketTarget: 0);
            Assert.Equal(after, await Scalar<string>(Snapshot));
        }
        finally { PostgresTestCluster.DropDatabase(connectionString); }
    }
}
