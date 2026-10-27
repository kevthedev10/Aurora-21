using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalBootstrapTests
{

    [Fact]
    public async Task ManagerCatalogueUsesTheCorrectNativeLeagues()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        async Task<long> ScalarAsync(string sql)
        {
            await using var command = source.CreateCommand(sql);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        const string Crespo = "(SELECT definition_id FROM ultimateteam.item_definitions WHERE native_resource_id=1007512 AND family_id=2)";
        Task<long> League(long native) => ScalarAsync($"SELECT league_id FROM ultimateteam.leagues WHERE native_league_id={native}");
        var libertadores = await League(1003);

        // All ten corrected managers are on FIFA 21's own league.
        Assert.Equal(libertadores, await ScalarAsync($"SELECT league_id FROM ultimateteam.manager_definitions WHERE definition_id={Crespo}"));
        Assert.Equal(10L, await ScalarAsync("""
            SELECT count(*) FROM ultimateteam.manager_definitions m JOIN ultimateteam.item_definitions d USING(definition_id)
            JOIN ultimateteam.leagues l ON l.league_id=m.league_id
            WHERE (d.native_resource_id, l.native_league_id) IN ((1007512,1003),(1010974,1014),(1143063,1014),(1249253,1014),
                (1249775,54),(1249929,1014),(1249944,1014),(1250470,1003),(1252696,330),(1255029,1014))
            """));

    }

    [Fact]
    public async Task PlayerPickTokenDefinitionIsInstalledOnceAndTokensCanBeGranted()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        async Task<string> RowAsync()
        {
            await using var command = source.CreateCommand("""
                SELECT string_agg(concat_ws('|', native_item_type, native_subtype, coalesce(native_resource_id::text, 'none'), native_card_asset_id), ';')
                FROM ultimateteam.item_definitions WHERE family_id=5
                """);
            return (string)(await command.ExecuteScalarAsync())!;
        }
        // A token is a misc card of subtype 237 with no product id of its own; its resourceId is a preview player.
        Assert.Equal("misc|237|none|34", await RowAsync());

        // Granting one stored nothing before: the token had no definition to be saved against.
        var preview = rt.Game.Players.Entries.First(entry => entry.Value.Rating >= 86).Key;
        var token = rt.Fut.Items.MakePlayerPick(86, 3, previewResourceId: preview);
        Assert.Equal(237, token.IntKeep("cardsubtypeid"));
        Assert.Equal(preview, token.IntKeep("resourceId"));
        await using (var command = source.CreateCommand($"SELECT count(*) FROM ultimateteam.player_pick_items p JOIN ultimateteam.items i ON i.id=p.item_id WHERE i.native_item_id={token.IntKeep("id")}"))
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        Assert.Equal(RelationalBootstrapResult.AlreadyInstalled, await RelationalSchemaBootstrap.InstallFreshAsync(source));
        Assert.Equal("misc|237|none|34", await RowAsync());
    }

    [Fact]
    public void EmbeddedPackageRejectsCorruptSqlAndManifest()
    {
        static byte[] Read(string name)
        {
            using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/" + name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        var manifest = Read("manifest.json");
        var parsed = RelationalSetupPackage.Parse(manifest, Read);
        Assert.Equal(13, parsed.Files.Count);
        Assert.Equal(67, parsed.TableNames.Length);
        Assert.Throws<RuntimeSetupException>(() => RelationalSetupPackage.Parse(manifest,
            name => name == "seed/013-player-definitions.sql" ? new byte[] { 0 } : Read(name)));
        var altered = System.Text.Json.Nodes.JsonNode.Parse(manifest)!;
        altered["executionOrder"]![0] = "seed/018-sequence-state.sql";
        Assert.Throws<RuntimeSetupException>(() => RelationalSetupPackage.Parse(JsonSerializer.SerializeToUtf8Bytes(altered), Read));
        altered = System.Text.Json.Nodes.JsonNode.Parse(manifest)!;
        altered["packageSha256"] = new string('0', 64);
        Assert.Throws<RuntimeSetupException>(() => RelationalSetupPackage.Parse(JsonSerializer.SerializeToUtf8Bytes(altered), Read));
    }

    [Fact]
    public async Task FreshInstallLoadsFullCatalogAndRepeatDoesNotChangeState()
    {
        await using var db = new Database();
        Assert.Equal(RelationalStorageState.Empty, (await RelationalStorageReadiness.InspectAsync(db.Source)).State);
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM pg_namespace WHERE nspname = 'ultimateteam'"));
        Assert.Equal(RelationalBootstrapResult.Installed, await RelationalSchemaBootstrap.InstallFreshAsync(db.Source));
        Assert.Equal(RelationalStorageState.RelationalReady, (await RelationalStorageReadiness.InspectAsync(db.Source)).State);
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('public.schema_migrations') IS NOT NULL"));
        var counts = new Dictionary<string, long>();
        using (var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/manifest.json")!)
        using (var manifest = await JsonDocument.ParseAsync(stream))
        {
            foreach (var row in manifest.RootElement.GetProperty("seedTableCounts").EnumerateObject())
            {
                if (row.Name is "card_designs" or "card_type_designs" or "catalog_sources" or "definition_extensions") continue;
                var actual = await db.Scalar<long>($"SELECT count(*) FROM ultimateteam.{(row.Name == "card_rarity_groups" ? "card_type_rarity_group_memberships" : row.Name)}");
                Assert.True(actual > 0, $"{row.Name} must contain its installed catalogue");
                counts.Add(row.Name, actual);
            }
        }
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='customisation_definitions' AND column_name IN ('native_stadium_id','source_team_id','template_metadata')"));
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('ultimateteam.definition_extensions') IS NOT NULL"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='footballers' AND column_name IN ('identity_status','date_of_birth_source')"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='item_definitions' AND column_name IN ('metadata_schema_revision','source_fields','source_missing_fields','source_card_id','source_card_url','resource_id_source','name_source')"));
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('ultimateteam.legacy_kv') IS NOT NULL"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_definitions' AND column_name IN ('common_name','native_rareflag','native_team_id','native_league_id','native_nation_id','team_id')"));
        Assert.Equal(311L, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.manager_definitions WHERE league_id IS NOT NULL"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.manager_definitions WHERE league_id IS NULL"));
        Assert.Equal(311L, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.manager_definitions m JOIN ultimateteam.native_rarities r ON r.native_rarity_id=m.rarity_id JOIN ultimateteam.nations n USING(nation_id) WHERE r.native_rareflag IN(0,1)"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_definitions' AND column_name IN ('talk_rating','formation_id')"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_items' AND column_name='native_league_override'"));
        Assert.Equal("item_id,contracts,league_id", await db.Scalar<string>("SELECT string_agg(column_name,',' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='manager_items'"));
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('ultimateteam.club_imports') IS NOT NULL"));
        Assert.Equal(62L + 1, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.native_rarities")); // + FUT Champions (051)
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='card_types' AND column_name='native_default_subtype'"));
        Assert.Equal(88L + 3, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.card_types WHERE name IS NOT NULL AND length(trim(name))>0")); // + FUT Champions types (051)
        Assert.Equal("card_type_id,name,card_level,native_rarity_id,is_special", await db.Scalar<string>("SELECT string_agg(column_name,',' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='card_types'"));
        // Migration 076 seeds promo groups and their cards; the other unowned tables still start empty.
        // 089 adds five standard pools, 151 Squad Foundations; 152 removes the empty Libertadores Flashback.
        Assert.Equal(113L + 5 + 1 - 1, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.promos"));
        Assert.True(await db.Scalar<long>("SELECT count(*) FROM ultimateteam.promo_cards") > 0);
        foreach (var table in RelationalSetupPackage.Load().TableNames.Except(counts.Keys).Where(t => t != "setup_artifact_versions" && t != "schema_migrations" && t != "card_designs" && t != "card_type_designs" && t != "catalog_sources" && t != "club_imports" && t != "definition_extensions" && t != "legacy_kv" && t != "market_player_attribute_overrides" && t != "player_attribute_overrides" && t != "player_stat_values" && t != "market_player_stat_values" && t != "squad_extensions" && t != "promos" && t != "promo_cards"))
            Assert.Equal(0L, await db.Scalar<long>($"SELECT count(*) FROM ultimateteam.{table}"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_card_definitions' AND column_name IN ('native_team_id','native_league_id','native_nation_id')"));
        Assert.Equal(RelationalCatalogMetadataMigration.CurrentVersion, await db.Scalar<int>("SELECT max(version) FROM ultimateteam.schema_migrations"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.club_player_search"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_card_definitions' AND column_name='attribute_provenance'"));
        Assert.True(await db.Scalar<bool>("SELECT to_regclass('ultimateteam.setup_artifact_versions') IS NULL"));
        Assert.True(await db.Scalar<bool>("SELECT to_regclass('ultimateteam.market_player_attribute_overrides') IS NULL"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='market_player_snapshots' AND column_name IN ('loan_games','style_definition_id')"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='ultimateteam' AND p.prosrc ~ '(^|[^[:alnum:]_])trade_id([^[:alnum:]_]|$)'"));
        Assert.Equal(4L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name IN ('market_listings','market_item_snapshots','market_player_snapshots','market_player_stat_values','market_watch') AND column_name='market_listing_id'"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name IN ('market_listings','market_item_snapshots','market_player_snapshots','market_player_stat_values','market_watch') AND column_name='trade_id'"));
        Assert.True(await db.Scalar<bool>("SELECT to_regclass('ultimateteam.player_attribute_overrides') IS NULL"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_card_definitions' AND column_name IN ('native_trait_1','native_trait_2','native_version')"));
        // Every current table must remain keyed, with validated foreign keys. Historical totals
        // stopped describing the schema as later migrations added promo/panel tables.
        Assert.Equal(0L, await db.Scalar<long>("""
            SELECT count(*) FROM pg_class c WHERE c.relnamespace='ultimateteam'::regnamespace
                AND c.relkind IN ('r','p') AND NOT EXISTS (
                    SELECT 1 FROM pg_constraint k WHERE k.conrelid=c.oid AND k.contype='p')
            """));
        Assert.Equal(0L, await db.Scalar<long>("""
            SELECT count(*) FROM pg_constraint WHERE connamespace='ultimateteam'::regnamespace
                AND contype='f' AND NOT convalidated
            """));
        const string ConstraintSnapshot = """
            SELECT jsonb_agg(jsonb_build_array(conname,contype,pg_get_constraintdef(oid)) ORDER BY conname)::text
            FROM pg_constraint WHERE connamespace='ultimateteam'::regnamespace
            """;
        var constraintsBefore = await db.Scalar<string>(ConstraintSnapshot);
        Assert.Equal(3L, await db.Scalar<long>("""
            SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relnamespace = 'ultimateteam'::regnamespace AND c.relname LIKE 'lfv4_idx_%'
                AND i.indisunique AND i.indpred IS NOT NULL
            """));
        var before = await db.Scalar<string>("SELECT json_agg(q ORDER BY sequencename)::text FROM (SELECT * FROM pg_sequences WHERE schemaname = 'ultimateteam') q");
        Assert.Equal(RelationalBootstrapResult.AlreadyInstalled, await RelationalSchemaBootstrap.InstallFreshAsync(db.Source));
        Assert.Equal(before, await db.Scalar<string>("SELECT json_agg(q ORDER BY sequencename)::text FROM (SELECT * FROM pg_sequences WHERE schemaname = 'ultimateteam') q"));
        Assert.Equal(constraintsBefore, await db.Scalar<string>(ConstraintSnapshot));
        foreach (var row in counts)
            Assert.Equal(row.Value, await db.Scalar<long>($"SELECT count(*) FROM ultimateteam.{(row.Key == "card_rarity_groups" ? "card_type_rarity_group_memberships" : row.Key)}"));
        var max = await db.Scalar<long>("SELECT max(footballer_id) FROM ultimateteam.footballers");
        await using var connection = await db.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO ultimateteam.footballers(native_footballer_id,first_name,last_name,common_name)
            VALUES(999999999,'Synthetic','Identity','Synthetic Identity') RETURNING footballer_id
            """, connection, transaction);
        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync()) > max);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(async () => { await command.ExecuteScalarAsync(); });
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        await transaction.RollbackAsync();
    }

    public static IEnumerable<object[]> LatestMigrationFailures()
    {
        var resource = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceNames().Single(name =>
            name.StartsWith($"DatabaseSetup/migrations/{RelationalCatalogMetadataMigration.CurrentVersion:D3}-", StringComparison.Ordinal));
        var file = resource["DatabaseSetup/".Length..];
        yield return [file, false];
        yield return [file, true];
    }

    [Theory]
    [InlineData("schema/002-relationships.sql", false)]
    [InlineData("seed/018-sequence-state.sql", true)]
    [MemberData(nameof(LatestMigrationFailures))]
    public async Task FailedOrCancelledInstallRollsBackAndCanRetry(string failAfter, bool cancel)
    {
        await using var db = new Database();
        using var cancellation = new CancellationTokenSource();
        async Task Fault(string file, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        {
            if (file != failAfter) return;
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            await using var command = new NpgsqlCommand("SELECT 1 / 0", connection, transaction);
            await command.ExecuteScalarAsync(token);
        }
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RelationalSchemaBootstrap.InstallFreshAsync(db.Source, Fault, cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => RelationalSchemaBootstrap.InstallFreshAsync(db.Source, Fault, cancellation.Token));
            Assert.Equal("22012", error.SqlState);
        }
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM pg_namespace WHERE nspname = 'ultimateteam'"));
        Assert.Equal(RelationalBootstrapResult.Installed, await RelationalSchemaBootstrap.InstallFreshAsync(db.Source));
    }

    [Fact]
    public async Task ConcurrentInstallersWaitOnSharedLockThenInstallOnce()
    {
        await using var db = new Database();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var first = RelationalSchemaBootstrap.InstallFreshAsync(db.Source, async (file, _, _, token) =>
        {
            if (file != "schema/001-tables.sql") return;
            held.SetResult();
            await release.Task.WaitAsync(token);
        }, timeout.Token);
        Task<RelationalBootstrapResult>? second = null;
        try
        {
            await held.Task.WaitAsync(timeout.Token);
            second = RelationalSchemaBootstrap.InstallFreshAsync(db.Source, timeout.Token);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (await db.Scalar<long>("""
                SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()
                    AND wait_event_type = 'Lock' AND query LIKE '%pg_advisory_xact_lock%'
                """) == 0)
            {
                Assert.False(second.IsCompleted, "Second installer did not wait for the schema lock.");
                Assert.True(DateTime.UtcNow < deadline, "Advisory lock wait was not observed.");
                await Task.Delay(25, timeout.Token);
            }
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (second is not null) await second;
        }
        Assert.Equal(RelationalBootstrapResult.Installed, await first);
        Assert.Equal(RelationalBootstrapResult.AlreadyInstalled, await second!);
        Assert.True(await db.Scalar<bool>("SELECT to_regclass('ultimateteam.setup_artifact_versions') IS NULL"));
    }

    [Theory]
    [InlineData("CREATE SCHEMA ultimateteam; CREATE TABLE ultimateteam.dummy(id int)", RelationalStorageState.Incomplete)]
    [InlineData("CREATE TABLE public.clubs(id int); INSERT INTO public.clubs VALUES(123)", RelationalStorageState.Legacy)]
    [InlineData("CREATE TABLE public.unrelated(id int)", RelationalStorageState.Unsupported)]
    public async Task ExistingStorageIsClassifiedWithoutMutationAndFreshInstallIsRejected(string arrange, RelationalStorageState expected)
    {
        await using var db = new Database();
        await db.Execute(arrange);
        var before = await db.Scalar<string>("SELECT json_agg(q ORDER BY nspname,relname)::text FROM (SELECT n.nspname,c.relname,c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('public','ultimateteam')) q");
        Assert.Equal(expected, (await RelationalStorageReadiness.InspectAsync(db.Source)).State);
        await Assert.ThrowsAsync<RuntimeSetupException>(() => RelationalSchemaBootstrap.InstallFreshAsync(db.Source));
        Assert.Equal(before, await db.Scalar<string>("SELECT json_agg(q ORDER BY nspname,relname)::text FROM (SELECT n.nspname,c.relname,c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('public','ultimateteam')) q"));
        if (expected == RelationalStorageState.Legacy) Assert.Equal(123, await db.Scalar<int>("SELECT id FROM public.clubs"));
    }

    [Theory]
    [InlineData("DROP TABLE ultimateteam.schema_migrations", RelationalStorageState.Incomplete)]
    [InlineData("CREATE TABLE ultimateteam.setup_artifact_versions(id bigint)", RelationalStorageState.Incomplete)]
    [InlineData("INSERT INTO ultimateteam.schema_migrations(version) VALUES(999)", RelationalStorageState.Unsupported)]
    [InlineData("DROP TRIGGER lfv4_trg_club_player_search_items ON ultimateteam.items", RelationalStorageState.Incomplete)]
    [InlineData("DROP TABLE ultimateteam.client_data", RelationalStorageState.Incomplete)]
    [InlineData("CREATE TABLE public.clubs(id int)", RelationalStorageState.UpgradeRequired)]
    public async Task IncompatibleOrIncompleteInstallationCannotBeReseeded(string change, RelationalStorageState expected)
    {
        await using var db = new Database();
        await RelationalSchemaBootstrap.InstallFreshAsync(db.Source);
        var cardsBefore = await db.Scalar<long>("SELECT count(*) FROM ultimateteam.player_card_definitions");
        await db.Execute(change);
        Assert.Equal(expected, (await RelationalStorageReadiness.InspectAsync(db.Source)).State);
        await Assert.ThrowsAsync<RuntimeSetupException>(() => RelationalSchemaBootstrap.InstallFreshAsync(db.Source));
        Assert.Equal(cardsBefore, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.player_card_definitions"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.clubs"));
    }

    [Fact]
    public async Task NativeCardTraitsHaveIndexedUniqueLinksAndRepeatStartupPreservesThem()
    {
        await using var db = new Database();
        await RelationalSchemaBootstrap.InstallFreshAsync(db.Source);
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_card_definitions' AND column_name='design_override_id'"));
        Assert.Equal(33L, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.traits"));
        // 13,863 links from migration 005, plus those migration 047 adds from FIFA's own table for cards that had none; less
        // the 18 links of the cards 050 removes; plus the links 051 copies to the red cards, exactly their source cards' links;
        // plus the 80 links of the six Pro cards' trait masks (056; the three older shells had none); plus the 51 links 068 copies to
        // the 18 phase red copies; minus six links on duplicate RTTF cards (075) and three on duplicate Tevez (104).
        Assert.Equal(24093L - 18 + 1769 + 80 + 51 - 6 - 3, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.player_card_traits"));
        Assert.False(await db.Scalar<bool>("SELECT is_visible FROM ultimateteam.traits WHERE mask_word=1 AND bit_value=32"));
        foreach (var (resource, expected) in new (long, long)[] { (16967259, 2), (237068, 3), (237067, 4), (17023741, 6), (158023, 8) })
            Assert.Equal(expected, await db.Scalar<long>($"SELECT count(*) FROM ultimateteam.player_card_traits t JOIN ultimateteam.item_definitions d USING(definition_id) WHERE d.native_resource_id={resource}"));
        var before = await db.Scalar<string>("SELECT md5(jsonb_agg(to_jsonb(t) ORDER BY id)::text) FROM ultimateteam.player_card_traits t");
        await RelationalSchemaBootstrap.InstallFreshAsync(db.Source);
        Assert.Equal(before, await db.Scalar<string>("SELECT md5(jsonb_agg(to_jsonb(t) ORDER BY id)::text) FROM ultimateteam.player_card_traits t"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("INSERT INTO ultimateteam.player_card_traits(definition_id,trait_id,source_native_player_id,source_sha256) SELECT definition_id,trait_id,source_native_player_id,source_sha256 FROM ultimateteam.player_card_traits LIMIT 1"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("INSERT INTO ultimateteam.player_card_traits(definition_id,trait_id,source_native_player_id,source_sha256) VALUES(-1,1,1,repeat('0',64))"));
        await db.Execute("DROP TABLE ultimateteam.player_card_traits");
        Assert.Equal(RelationalStorageState.Incomplete, (await RelationalStorageReadiness.InspectAsync(db.Source)).State);
    }

    [Fact]
    public async Task DesignRemovalRefusesPopulatedInfrastructureAndRollsBack()
    {
        await using var db = new Database();
        async Task Populate(string file, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        {
            if (file != "migrations/007-sbc-squad-items.sql") return;
            await using var command = new NpgsqlCommand("INSERT INTO ultimateteam.card_designs(code,name,revision,source_id,render_status) VALUES('synthetic','Synthetic design',1,1,'unconfirmed')", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
        }
        var error = await Assert.ThrowsAsync<PostgresException>(() => RelationalSchemaBootstrap.InstallFreshAsync(db.Source, Populate, CancellationToken.None));
        Assert.Contains("Cannot remove populated card design infrastructure", error.MessageText);
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM pg_namespace WHERE nspname='ultimateteam'"));
        await RelationalSchemaBootstrap.InstallFreshAsync(db.Source);
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('ultimateteam.card_designs') IS NOT NULL OR to_regclass('ultimateteam.card_type_designs') IS NOT NULL"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='promo_cards' AND column_name='design_override_id'"));
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.native_rarities WHERE native_rareflag=75 AND name='Next-Gen'"));
        Assert.Equal("FGS Swaps 2", await db.Scalar<string>("SELECT name FROM ultimateteam.native_rarities WHERE native_rareflag=77"));
        Assert.Equal("Professional Player", await db.Scalar<string>("SELECT name FROM ultimateteam.native_rarities WHERE native_rareflag=10"));
        Assert.Equal("Icon Swaps 1", await db.Scalar<string>("SELECT name FROM ultimateteam.native_rarities WHERE native_rareflag=89"));
        Assert.Equal(18L, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.player_card_definitions p JOIN ultimateteam.card_types c USING(card_type_id) JOIN ultimateteam.native_rarities r USING(native_rarity_id) WHERE r.native_rareflag=89"));
        Assert.Equal(3L + 3, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.player_card_definitions p JOIN ultimateteam.card_types c USING(card_type_id) JOIN ultimateteam.native_rarities r USING(native_rarity_id) WHERE r.native_rareflag=10")); // + the Pro Player shells of 056
        Assert.Equal(88L + 3, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.card_types")); // + FUT Champions types (051)
        Assert.Equal(75L + 3, await db.Scalar<long>("SELECT count(*) FROM ultimateteam.card_type_rarity_group_memberships")); // + FUT Champions (051)
    }

    private sealed class Database : IAsyncDisposable
    {
        private readonly string _connection = PostgresTestCluster.CreateDatabase();
        public Database() => Source = NpgsqlDataSource.Create(_connection);
        public NpgsqlDataSource Source { get; }
        public async Task<T> Scalar<T>(string sql)
        {
            await using var command = Source.CreateCommand(sql);
            return (T)(await command.ExecuteScalarAsync())!;
        }
        public async Task Execute(string sql)
        {
            await using var command = Source.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            PostgresTestCluster.DropDatabase(_connection);
        }
    }
}
