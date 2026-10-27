using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using System.Text.Json.Nodes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalCatalogAndAccountTests
{
    [Fact]
    public void CorrectedCatalogNamesReachOwnedPlayerItems()
    {
        using var runtime = TestRuntime.Create(marketTarget: 0);
        using var customs = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "custom-players.json")));
        foreach (var card in customs.RootElement.EnumerateObject())
        {
            var id = long.Parse(card.Name, System.Globalization.CultureInfo.InvariantCulture);
            var meta = runtime.Game.Players.Find(id)!;
            Assert.True(meta.Has("commonName"));
            Assert.False(meta.Has("commonname"));
            var item = runtime.Fut.Items.BuildPlayer(id, 123, "club");
            Assert.Equal(card.Value.GetProperty("commonName").GetString(), item.Str("commonname"));
            Assert.Equal(card.Value.GetProperty("lastName").GetString(), item.Str("lastName"));
        }
        foreach (var (id, first, last, dob) in new[]
        {
            (219670L, "Gonzalo", "Piovi", "1994-09-08"),
            (16996886L, "Gonzalo", "Piovi", "1994-09-08"),
            (17022277L, "Julián", "López", "2000-01-08")
        })
        {
            var item = runtime.Fut.Items.BuildPlayer(id, 123, "club");
            Assert.Equal(first, item.Str("firstName"));
            Assert.Equal(last, item.Str("lastName"));
            Assert.Equal(dob, runtime.Game.Players.Find(id)!.Str("dateOfBirth"));
        }
    }

    [Fact]
    public async Task LinkedCardTraitsReachFifaMetadataAsBothBitmasks()
    {
        await using var db=new Database();
        await RelationalSchemaBootstrap.InstallFreshAsync(db.Source);
        using var runtime=TestRuntime.Create(connectionString:db.ConnectionString,relationalSlice:true,marketTarget:0);
        // Masks verified against user card lists/native imports; include word 2 and zero-trait cases.
        var cases=new (long Resource,long Trait1,long Trait2)[] {
            (16967259,67109120,0), // 91 Pele
            (237068,67109128,0), // 95 Pele
            (237067,67117320,0), // 98 Pele
            (17023741,83895048,0), // 99 Pele, alias import
            (158023,58865920,0), // Rare gold Messi
            (109493,0,2), // Native GK Saves With Feet bit
            (240,0,1), // Preserve hidden/unknown native word-2 bit
            (10899,0,0) // Verified native empty masks
        };
        var response=Assert.IsType<JsonArray>(runtime.Call("GET","/ut/game/fifa21/attributes/metadata?defIds="+string.Join(",",cases.Select(c=>c.Resource))));
        Assert.Equal(cases.Length,response.Count);
        for(var i=0;i<cases.Length;i++)
        {
            var row=response[i]!.AsObject();
            Assert.Equal(cases[i].Resource,row.IntKeep("defId"));
            Assert.Equal(cases[i].Trait1,row.IntKeep("trait1"));
            Assert.Equal(cases[i].Trait2,row.IntKeep("trait2"));
            // Migration 047 completed these cards' attributes (17023741 under its seeded id), so their records carry a version:
            // the one their content gives them (Python server 4736f1a).
            Assert.Equal(CardAttributeRecordTests.ContentVersion(row),row.IntKeep("version"));
        }
        Assert.Equal(0L,await db.Scalar<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='player_card_definitions' AND column_name IN ('native_trait_1','native_trait_2','native_version')"));
    }

    [Fact]
    public async Task FullCatalogSnapshotPreservesGameplayMetadataOrderingAndImageMigrations()
    {
        await using var db = new Database();
        await RelationalSchemaBootstrap.InstallFreshAsync(db.Source);
        var commands = new List<string>();
        var snapshot = await RelationalCatalogLoader.LoadAsync(db.Source, commands.Add, CancellationToken.None);
        Assert.Equal(20, commands.Count); // Fixed catalogue queries, including buckets, events, icon rotation and revision; independent of card count.
        Assert.All(commands, sql => Assert.Contains("FROM ultimateteam.", sql));
        Assert.Equal(24245 + 833 + 18 + 854 + 3 + 42 - 3 + 3, snapshot.Definitions.Count); // existing additions, less RTTF (075) and Tevez (104) duplicates, plus FUT Stadium tier cards (139)
        Assert.Equal(16057, snapshot.Footballers.Count);
        Assert.Equal(22503 + 833 + 18 + 3 - 3, snapshot.Players.Count); // red picks and Pro shells added; duplicate RTTF (075) and Tevez (104) removed
        Assert.Equal(311, snapshot.Managers.Count);
        Assert.Equal(44 + 42, snapshot.Consumables.Count); // 20 position modifiers, 24 chemistry styles, 42 manager league cards (071)
        Assert.Equal(42, snapshot.ManagerLeagueCards().Count);
        Assert.Equal(snapshot.Footballers.Count, snapshot.Footballers.Select(f => f.NativeFootballerId).Distinct().Count());
        var legacy = PlayerCatalog.Load(TestRuntime.DataRoot, NullLogger.Instance);
        var projected = PlayerCatalog.FromRelationalSnapshot(snapshot);
        // The catalogue files hold every card but the red picks, which migration 051 generates (rarity 18).
        // Migrations 075 and 104 remove duplicate resources from the database catalogue.
        var removedDuplicates = new HashSet<long> { 33743235, 33763240, 50474649 };
        Assert.Equal(833 + 18, projected.Entries.Count(entry => entry.Value.Rareflag == 18));
        Assert.Equal(legacy.Ids.Where(id => !removedDuplicates.Contains(id)), projected.Ids.Where(id => projected.Find(id)!.Rareflag != 18));
        Assert.Equal(legacy.ResourceIdMigrations.OrderBy(p => p.Key), projected.ResourceIdMigrations.OrderBy(p => p.Key));
        Assert.True(legacy.NinetyNinePositions.SetEquals(projected.NinetyNinePositions));
        var expectedPools = legacy.SpecialPoolCounts().ToDictionary(pool => pool.Key, pool => pool.Value);
        foreach (var id in removedDuplicates) expectedPools[legacy.Find(id)!.Rareflag]--;
        Assert.Equal(expectedPools, projected.SpecialPoolCounts().Where(pool => pool.Key != 18));
        var rules = GameRules.Load(TestRuntime.DataRoot);
        var before = new Cards(legacy, rules.Cards, rules.Packs, rules.Market);
        var after = new Cards(projected, rules.Cards, rules.Packs, rules.Market);
        var textKeys = new[] { "name", "firstName", "lastName", "commonName", "preferredPosition",
            "preferredFoot", "workRateAttack", "workRateDefense", "tier" };
        var integerKeys = new[] { "resourceId", "assetId", "rating", "weakFoot", "skillMoves", "leagueId", "teamid",
            "nation", "cardsubtypeid", "dynamicImageResourceId", "version" };
        // Migration 047 applies card-corrections.json and fills attributes from FIFA's own table: corrected fields are
        // expected as corrected; attribute values and the record version are checked by CardAttributeRecordTests.
        var corrections = JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "card-corrections.json")))!["cards"]!.AsObject();
        foreach (var (id, old) in legacy.Entries)
        {
            if (removedDuplicates.Contains(id)) continue;
            var current = projected.Find(id)!;
            var fix = corrections[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            foreach (var key in textKeys) Assert.Equal(fix?[key] is { } fixedText ? fixedText.GetValue<string>() : old.Str(key), current.Str(key));
            foreach (var key in integerKeys)
            {
                if (key != "resourceId") Assert.Equal(old.Has(key), current.Has(key));
                Assert.Equal(key == "resourceId" ? id : fix?[key] is { } fixedValue ? fixedValue.GetValue<long>() : old.Int(key), current.Int(key));
            }
            Assert.Equal(old.Special, current.Special);
            Assert.Equal(old.Has("untradeable"), current.Has("untradeable"));
            Assert.Equal(old.Bool("untradeable"), current.Bool("untradeable"));
            var expectedMetadata = before.Metadata(id);
            var traitCard = projected.Find(after.AquaProResourceId(id) ?? id)!;
            expectedMetadata["trait1"] = traitCard.Int("trait1");
            expectedMetadata["trait2"] = traitCard.Int("trait2");
            if (fix?["face"] is JsonArray fixedFace) expectedMetadata["attributeArray"] = fixedFace.DeepClone();
            var actualMetadata = after.Metadata(id);
            foreach (var record in new[] { expectedMetadata, actualMetadata }) { record.Remove("ingameattribs"); record.Remove("version"); }
            Assert.True(JsonNode.DeepEquals(expectedMetadata, actualMetadata), $"Attribute response differs for {id}");
            Assert.Equal(before.DiscardValue(id), after.DiscardValue(id));
            Assert.Equal(before.MarketPriceBounds(id), after.MarketPriceBounds(id));
        }
        var asp = snapshot.ResolveDefinition(2021, 1, new NativeResourceId(50524277))!;
        var aspCard = snapshot.Players.Single(p => p.DefinitionId == asp.DefinitionId);
        Assert.NotEqual(asp.DefinitionId.Value, asp.NativeResourceId!.Value.Value);
        Assert.Contains(snapshot.Variants(aspCard.FootballerId), p => p.Rating == 84);
        Assert.Null(snapshot.ResolveDefinition(2021, 2, new NativeResourceId(50524277)));
        Assert.Null(snapshot.ResolveDefinition(2020, 1, new NativeResourceId(50524277)));
        var foreignDefinition = asp with { DefinitionId = new DefinitionId(99999999), GameYear = 2020 };
        var mixed = new RelationalCatalogSnapshot(snapshot.Footballers, snapshot.Nations,
            snapshot.Leagues, snapshot.Teams, snapshot.Families, snapshot.Definitions.Append(foreignDefinition).ToArray(),
            snapshot.CardTypes, snapshot.Promos, snapshot.PromoCards,
            snapshot.PromoBuckets, snapshot.PromoEvents, snapshot.PromoEventPromos,
            snapshot.RarityGroups, snapshot.Players.Append(aspCard with { DefinitionId = foreignDefinition.DefinitionId, Rating = 99 }).ToArray(),
            snapshot.Managers, snapshot.Consumables, snapshot.Customisations, snapshot.Extensions);
        Assert.Equal(86, PlayerCatalog.FromRelationalSnapshot(mixed).Find(50524277)!.Rating);
        Assert.Equal(99, PlayerCatalog.FromRelationalSnapshot(mixed, gameYear: 2020).Find(50524277)!.Rating);
        Assert.Throws<InvalidOperationException>(() => projected.Find(50524277)!.Override("rating", 1));
        var copy = projected.Find(50524277)!.ToJsonObject();
        copy["rating"] = 1;
        Assert.Equal(86, projected.Find(50524277)!.Rating);
        var clone = projected.Find(50524277)!.CloneWithOverrides();
        clone.Override("rating", 1);
        Assert.Equal(86, projected.Find(50524277)!.Rating);

        var staff = ManagerCatalog.FromRelationalSnapshot(snapshot);
        var oldStaff = ManagerCatalog.Load(TestRuntime.DataRoot, NullLogger.Instance);
        Assert.Equal(oldStaff.Count, staff.Count);
        Assert.Equal(oldStaff.BestManagerId(), staff.BestManagerId());
        foreach (var (id, old) in oldStaff.Entries)
        {
            var current = staff.Find(id)!;
            foreach (var key in new[] { "rating", "rareflag", "nation", "teamid", "negotiation", "carddbid" })
                Assert.Equal(old.Int(key), current.Int(key));
            foreach (var key in new[] { "firstName", "lastName", "commonname" }) Assert.Equal(old.Str(key), current.Str(key));
        }
        Assert.Equal(31, staff.Find(1001067)!.Int("leagueId")); // Supplied High-confidence Antonio Conte -> Serie A.
        Assert.Equal(13, staff.Find(1250425)!.Int("leagueId")); // User-chosen Premier League default.
        Assert.Equal(350, staff.Find(1250664)!.Int("leagueId")); // User correction: Vitor Campelos -> MBS Pro League.
        staff.Find(1001067)!["rating"] = 1;
        Assert.Equal(80, staff.Find(1001067)!.Int("rating"));
        var kits = KitCatalog.FromRelationalSnapshot(snapshot);
        var oldKits = KitCatalog.Load(TestRuntime.DataRoot, NullLogger.Instance).Kits;
        Assert.Equal(oldKits.Select(k => k.Int("resourceId")).Distinct().Order(), kits.Kits.Select(k => k.Int("resourceId")).Order());
        foreach (var kit in kits.Kits)
            Assert.Contains(oldKits, old => JsonNode.DeepEquals(old, kit));
        kits.Kits[0]["name"] = "changed";
        Assert.NotEqual("changed", kits.Kits[0].Str("name"));
        Assert.Equal(snapshot.RevisionSha256, (await RelationalCatalogLoader.LoadAsync(db.Source)).RevisionSha256);
        await db.Execute("INSERT INTO ultimateteam.promos(code,name,status) VALUES('synthetic','Synthetic Promo','draft')");
        var updated = await RelationalCatalogLoader.LoadAsync(db.Source);
        Assert.Equal(snapshot.Promos.Count + 1, updated.Promos.Count); // migration 076's seeded groups remain alongside this new row
        Assert.Contains(updated.Promos, promo => promo.Code == "synthetic");
        Assert.NotEqual(snapshot.RevisionSha256, updated.RevisionSha256);
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('public.clubs') IS NOT NULL"));
    }

    [Fact]
    public async Task LoaderDoesNotInitializeEmptyOrLegacyStorage()
    {
        await using var db = new Database();
        await Assert.ThrowsAsync<RuntimeSetupException>(() => RelationalCatalogLoader.LoadAsync(db.Source));
        await db.Execute("CREATE TABLE public.clubs(id int)");
        await Assert.ThrowsAsync<RuntimeSetupException>(() => RelationalCatalogLoader.LoadAsync(db.Source));
        Assert.False(await db.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname='ultimateteam')"));
    }

    [Fact]
    public async Task AccountsRoundTripWithoutOverwritingBalancesOrCrossingClubs()
    {
        await using var db = new Database();
        await RelationalSchemaBootstrap.InstallFreshAsync(db.Source);
        var runner = db.Runner();
        var identity = new ClubIdentity(900000001, 900000002);
        var first = await runner.RunAsync(0, "open-relational", w => w.EnsureRelationalClubAsync(identity, 100, "Synthetic"));
        var repeat = await runner.RunAsync(0, "reopen-relational", w => w.EnsureRelationalClubAsync(identity, 999, "Replacement"));
        Assert.True(first.Created);
        Assert.False(repeat.Created);
        Assert.Equal(first.Account, repeat.Account);
        var other = await runner.RunAsync(0, "other-relational", w => w.EnsureRelationalClubAsync(new ClubIdentity(900000003, 900000004), 200));
        await Assert.ThrowsAsync<RuntimeSetupException>(() => runner.RunAsync(0, "wrong-identity",
            w => w.EnsureRelationalClubAsync(new ClubIdentity(identity.PersonaId, 123456), 999)));
        Assert.Equal(first.Account.ClubId, await runner.RunReadAsync(first.Account.ClubId, w => w.RelationalClubIdOfPersonaAsync(identity.PersonaId)));
        await runner.RunAsync(first.Account.ClubId, "account-updates", async w =>
        {
            Assert.False(await w.TrySpendRelationalCreditsAsync(101));
            Assert.True(await w.TrySpendRelationalCreditsAsync(40));
            Assert.Equal(80, await w.AddRelationalCreditsAsync(20));
            Assert.Equal(50, await w.AddRelationalFifaPointsAsync(50));
            Assert.True(await w.TrySpendRelationalFifaPointsAsync(10));
            Assert.False(await w.TrySpendRelationalFifaPointsAsync(41));
            await w.SetRelationalDisplayNameIfMissingAsync("Ignored");
            await w.SetRelationalSelectedStadiumAsync(261);
            await w.SetRelationalDailyDropDayAsync("2026-09-17");
            await w.SetRelationalLastPurchaseTransactionAsync(987);
            await w.SetRelationalLastMatchCreateIsDraftAsync(true);
            await w.SetRelationalLastDraftSlotAsync(DraftSlot.Online);
            Assert.Equal(1, await w.SetRelationalFlagAsync("synthetic-flag"));
            Assert.Equal(0, await w.SetRelationalFlagAsync("synthetic-flag"));
        });
        var account = await runner.RunReadAsync(first.Account.ClubId, w => w.RelationalAccountAsync());
        Assert.Equal(80, account.Credits);
        Assert.Equal(40, account.FifaPoints);
        Assert.Equal("Synthetic", account.DisplayName);
        Assert.Equal(261, account.SelectedStadiumId);
        Assert.Equal("2026-09-17", account.DailyDropDay);
        Assert.Equal(987, account.LastPurchaseTransactionId);
        Assert.True(account.LastMatchCreateIsDraft);
        Assert.Equal(DraftSlot.Online, account.LastDraftSlot);
        Assert.Equal(first.Account.Sid, account.Sid);
        Assert.Equal(first.Account.Token, account.Token);
        Assert.True(await runner.RunReadAsync(account.ClubId, w => w.RelationalFlagAsync("synthetic-flag")));
        Assert.False(await runner.RunReadAsync(other.Account.ClubId, w => w.RelationalFlagAsync("synthetic-flag")));
        Assert.Equal(200, (await runner.RunReadAsync(other.Account.ClubId, w => w.RelationalAccountAsync())).Credits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(account.ClubId, "rollback-account", async w =>
        {
            await w.AddRelationalCreditsAsync(1000);
            await w.ReserveNativeItemIdsAsync(5);
            throw new InvalidOperationException("synthetic fault");
        }));
        Assert.Equal(account, await runner.RunReadAsync(account.ClubId, w => w.RelationalAccountAsync()));
        Assert.Equal(new ClubFinancialSummary(80, 40, 0), await runner.RunReadAsync(account.ClubId, w => w.RelationalFinancialSummaryAsync()));
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('public.clubs') IS NOT NULL"));
    }

    [Fact]
    public async Task IndependentRunnersAllocateDisjointNativeRangesAndCannotOverspend()
    {
        await using var db = new Database();
        await RelationalSchemaBootstrap.InstallFreshAsync(db.Source);
        var a = db.Runner();
        var b = db.Runner();
        var identity = new ClubIdentity(900000001, 900000002);
        var opens = await Task.WhenAll(a.RunAsync(0, "open-a", w => w.EnsureRelationalClubAsync(identity, 100)),
            b.RunAsync(0, "open-b", w => w.EnsureRelationalClubAsync(identity, 100)));
        Assert.Single(opens, o => o.Created);
        Assert.Equal(opens[0].Account, opens[1].Account);
        var club = opens[0].Account.ClubId;
        var ranges = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? a : b)
            .RunAsync(club, "reserve-native-range", w => w.ReserveNativeItemIdsAsync(3))));
        var ids = ranges.SelectMany(r => Enumerable.Range(0, r.Count).Select(i => r.First.Value + i)).Order().ToArray();
        Assert.Equal(60, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 60).Select(i => ClubRepository.FirstItemIdOf(club) + i), ids);
        var payments = await Task.WhenAll(a.RunAsync(club, "pay-a", w => w.TrySpendRelationalCreditsAsync(80)),
            b.RunAsync(club, "pay-b", w => w.TrySpendRelationalCreditsAsync(80)));
        Assert.Single(payments, p => p);
        Assert.Equal(20, (await a.RunReadAsync(club, w => w.RelationalAccountAsync())).Credits);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => a.RunAsync(club, "bad-range", w => w.ReserveNativeItemIdsAsync(0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => a.RunAsync(club, "bad-spend", w => w.TrySpendRelationalCreditsAsync(-1)));
    }

    private sealed class Database : IAsyncDisposable
    {
        private readonly string _connection = PostgresTestCluster.CreateDatabase();
        public Database() => Source = NpgsqlDataSource.Create(_connection);
        public NpgsqlDataSource Source { get; }
        public string ConnectionString => _connection;
        public ClubUnitOfWorkRunner Runner() => new(Source, TimeProvider.System, 0, NullLogger.Instance);
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
