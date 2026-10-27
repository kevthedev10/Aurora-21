using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// Relational query budgets. Counts must remain constant as request and pack sizes grow.
/// </summary>
public sealed class QueryBudgetBaselineTests
{
    private const long Now = 1_700_000_000;
    private const long CustomPackId = 90;

    [Theory]
    [InlineData(1, 4)]
    [InlineData(25, 4)]
    public void MarketStatusHasAConstantQueryBudget(int listingCount, int expectedCommands)
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = Runtime(counter);
        var tradeIds = SeedAiListings(runtime, listingCount, watch: false);
        Array.Reverse(tradeIds);
        var mark = counter.Mark();

        var response = runtime.Fut.Market.Status(tradeIds);

        Assert.Equal(listingCount, response["auctionInfo"]!.AsArray().Count);
        Assert.Equal(tradeIds, response["auctionInfo"]!.AsArray().OfType<JsonObject>().Select(row => row.Int("tradeId")));
        Assert.Equal(expectedCommands, counter.Since(mark).Count);
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(50, 6)]
    public void WatchlistHasAConstantQueryBudget(int listingCount, int expectedCommands)
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = Runtime(counter);
        _ = SeedAiListings(runtime, listingCount, watch: true);
        var mark = counter.Mark();

        var response = runtime.Fut.Market.Watchlist();

        Assert.Equal(listingCount, response["auctionInfo"]!.AsArray().Count);
        Assert.Equal(expectedCommands, counter.Since(mark).Count);
    }

    [Fact]
    public void HubUsesFixedCostSummaryQueries()
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = Runtime(counter);
        _ = SeedAiListings(runtime, 50, watch: true);
        var mark = counter.Mark();

        var response = runtime.Fut.Club.Hub();

        Assert.Equal(50, response["watchlist"]!.AsObject().Int("count"));
        // The first load counts the whole market; later loads within the cache lifetime reuse that count.
        // The stored stadium comes with the slot bindings, not from a separate club column read.
        // The Squad Battles, Rivals and Weekend League tiles (Python server 56145ed, f354894) read all of this club's competition
        // rows in one query. Their live standings (every club's week) are loaded once into the process-wide cache; a week
        // already known settled costs nothing, and here none is due. The Weekend League tile's place (Python server 764ef6e)
        // comes from the same cache: one more standings load when cold.
        Assert.Equal(11 + 1 + 3, counter.Since(mark).Count);
        mark = counter.Mark();
        _ = runtime.Fut.Club.Hub();
        var warm = counter.Since(mark);
        // Warm: the one competition read, and no scan of every club's week.
        Assert.Equal(10 + 1, warm.Count);
        Assert.DoesNotContain(warm, sql => sql.Contains("rivals.week-rows", StringComparison.Ordinal) || sql.Contains("sqbt.week.rows", StringComparison.Ordinal)
                                           || sql.Contains("champions.board", StringComparison.Ordinal));
        Assert.DoesNotContain(warm, sql => sql.Contains("club.column.selected_stadium_id", StringComparison.Ordinal));
    }

    [Fact]
    public void SquadListComputesEveryChemistryWithAConstantQueryBudget()
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = Runtime(counter);
        (int Squads, IReadOnlyList<string> Commands) List()
        {
            var mark = counter.Mark();
            var list = runtime.Game.InUnit(uow => runtime.Fut.Club.SquadListAsync(uow));
            return (list["squad"]!.AsArray().Count, counter.Since(mark));
        }
        var one = List();
        var first = runtime.Game.Store().GetSquad(1)!;
        for (var copy = 1; copy <= 3; copy++)
        {
            var id = runtime.CallObject("POST", "/ut/game/fifa21/squad", new JsonObject { ["squadName"] = $"Copy {copy}" }).Int("id");
            var squad = first.DeepClone().AsObject();
            squad["id"] = id; squad["squadId"] = id; squad["squadName"] = $"Copy {copy}"; squad.Remove("active");
            runtime.Call("PUT", $"/ut/game/fifa21/squad/{id}", squad);
        }

        var four = List();

        // Every squad's starters and managers are fetched together, so more squads add rows, not round trips.
        Assert.Equal((1, 4), (one.Squads, four.Squads));
        Assert.Equal(one.Commands.Count, four.Commands.Count);
        // Club lock, squad rows, view shape and metadata, then the cards (headers and manager state): the squad view the
        // login document already loads. Before the list computed chemistry it read the squad rows only.
        Assert.True(four.Commands.Count == 6, string.Join("\n----\n", four.Commands.Select(sql => sql.Length > 160 ? sql[..160] : sql)));
    }

    // Includes the relational item and duplicate checks. The command count must stay flat as
    // the pack grows; each card must not add another database round trip. Two fewer since a pack
    // no longer clears Unassigned first (Python server 328a5b1).
    [Theory]
    [InlineData(1, 9)]
    [InlineData(12, 9)]
    [InlineData(50, 9)]
    public void PackPersistenceHasAConstantQueryBudget(int packSize, int expectedCommands)
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = TestRuntime.Create(time: new TestRuntime.FixedTime(Now), marketTarget: 0,
            commandCounter: counter, seedSql: $$"""
                INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,manager_chance,chem_style_chance)
                VALUES({{CustomPackId}},'store',true,'Synthetic query budget',0,{{packSize}},100,0,0,0,0,0)
                """);
        var mark = counter.Mark();

        var response = runtime.Fut.Packs.CreatePack(CustomPackId);

        Assert.Equal(packSize, response["itemData"]!.AsArray().Count);
        Assert.Equal(expectedCommands, counter.Since(mark).Count);
    }

    // Unsold listings end in one statement, however many expire at once.
    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    public async Task ExpiringUnsoldListingsHasAConstantQueryBudget(int listingCount)
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = Runtime(counter);
        var tradeIds = SeedAiListings(runtime, listingCount, watch: false, endTime: Now - 1);
        var mark = counter.Mark();

        Assert.Equal(listingCount, await runtime.Fut.Market.ExpireDueAsync());

        var commands = counter.Since(mark);
        Assert.Equal(1, commands.Count(sql => sql.Contains("market.end.many", StringComparison.Ordinal)));
        Assert.DoesNotContain(commands, sql => sql.Contains("UPDATE ultimateteam.market_listings SET state = @state", StringComparison.Ordinal)
            && !sql.Contains("market.end.many", StringComparison.Ordinal));
        Assert.Equal(tradeIds.Length, (await runtime.Game.ReadAsync(w => w.ScalarAsync<long>(
            "SELECT count(*) FROM ultimateteam.market_listings WHERE state = 'expired'"), CancellationToken.None)));
    }

    // "Store all" after a pack: the item reads, listing check and duplicate check are batched, so the command
    // count stays flat as the pack grows.
    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void StoringAPackHasAConstantQueryBudget(int packSize)
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = TestRuntime.Create(time: new TestRuntime.FixedTime(Now), marketTarget: 0,
            commandCounter: counter, seedSql: $$"""
                INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,manager_chance,chem_style_chance)
                VALUES({{CustomPackId}},'store',true,'Synthetic query budget',0,{{packSize}},100,0,0,0,0,0)
                """);
        _ = runtime.Fut.Packs.CreatePack(CustomPackId);
        var ids = runtime.Game.Store().ListItems("unassigned").Select(item => (JsonNode?)new JsonObject { ["id"] = item.Int("id"), ["pile"] = "club" }).ToArray();
        Assert.Equal(packSize, ids.Length);
        var mark = counter.Mark();

        var stored = runtime.CallObject("PUT", "/ut/game/fifa21/item", new JsonObject { ["itemData"] = new JsonArray(ids) });

        Assert.Equal(packSize, stored["itemData"]!.AsArray().Count);
        Assert.DoesNotContain(runtime.Game.Store().ListItems("unassigned"), item => !item.Bool("duplicate"));
        Assert.Equal(StorePackCommands, counter.Since(mark).Count);
    }

    // Club lock, the batch's items, their live listings, the owned cards of their players, then one batched save.
    // Before batching: 13 statements for one card and 123 for twelve.
    private const int StorePackCommands = 11;

    [Fact]
    public async Task MarketViewsUseOneTransactionWhenNothingIsDue()
    {
        using var runtime = Runtime(new DatabaseCommandCounter());
        var tradeIds = SeedAiListings(runtime, 5, watch: true);
        var begins = 0;
        var measuring = new AsyncLocal<bool>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == DatabaseTimings.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (!measuring.Value || instrument.Name != "ultimateteam.database.stage") return;
            foreach (var tag in tags)
                if (tag.Key == "stage" && Equals(tag.Value, "begin")) Interlocked.Increment(ref begins);
        });
        listener.Start();
        async Task<int> Transactions(Func<Task> view)
        {
            begins = 0;
            measuring.Value = true;
            await view();
            measuring.Value = false;
            return begins;
        }
        // The trade check and the view share one read transaction; expiry runs only when a listing is due.
        Assert.Equal(1, await Transactions(async () => await runtime.Fut.Market.StatusAsync(tradeIds)));
        Assert.Equal(1, await Transactions(async () => await runtime.Fut.Market.WatchlistAsync(null)));
        Assert.Equal(1, await Transactions(async () => await runtime.Fut.Market.TradePileAsync(null)));
    }

    [Fact]
    public void OpeningANewSecondClubSkipsSchemaCompatibilityQueries()
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = Runtime(counter);
        using var players = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions { MaxOpenClubs = 2 }, shared: true);
        var mark = counter.Mark();

        using var player = players.Acquire(PlayerIdentity.Of("222222"));

        var commands = counter.Since(mark);
        // New clubs receive no starter consumables; hosted club opening also reads the name chosen in FIFA.
        // Saving the starter squad now synchronizes its Balanced tactic formation (db1a8a33), one fixed query.
        Assert.Equal(38 + 1, commands.Count);
        Assert.Equal(1, commands.Count(sql => sql.Contains("UPDATE ultimateteam.squad_tactic_settings", StringComparison.Ordinal)));
        Assert.Equal(1, commands.Count(sql => sql.Contains("club.flags.set", StringComparison.Ordinal)));
        Assert.DoesNotContain(commands, sql => sql.Contains("schema_migrations", StringComparison.Ordinal));
    }

    [Fact]
    public void ReopeningAClubAtTheCurrentRevisionSkipsSchemaAndMaintenanceWork()
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = Runtime(counter);
        var mark = counter.Mark();

        using var game = runtime.Game.ForPlayer(runtime.Game.Player);
        _ = game.Services();

        var commands = counter.Since(mark);
        // Includes the club identity chosen in FIFA's club creation (onboarding.chosen-club, migration 065).
        Assert.Equal(5, commands.Count);
        Assert.Equal(1, commands.Count(sql => sql.Contains("onboarding.chosen-club", StringComparison.Ordinal)));
        Assert.DoesNotContain(commands, sql => sql.Contains("schema_migrations", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, sql => sql.Contains("SELECT id, data FROM items", StringComparison.Ordinal));
    }

    [Fact]
    public void AHubAsksNothingAboutWeeksKnownToBeSettled()
    {
        var counter = new DatabaseCommandCounter();
        // Thursday 2026-10-08 12:00 UTC: Rivals weeks 2960-2961, Weekend Leagues 2960-2961 and Squad Battles weeks 2959-2960
        // are all past their rewards time.
        using var runtime = TestRuntime.Create(time: new TestRuntime.FixedTime(1_791_460_800), marketTarget: 0, commandCounter: counter);
        _ = runtime.Fut.Club.Hub();
        Assert.Equal(6L, runtime.Game.InUnit(uow => uow.ScalarAsync<long>("""
            SELECT (SELECT count(*) FROM ultimateteam.rivals_week_settlements) + (SELECT count(*) FROM ultimateteam.champion_settlements)
                 + (SELECT count(*) FROM ultimateteam.squad_battles_settlements)
            """)));
        // The next load finds them settled and remembers it; after that no load asks.
        _ = runtime.Fut.Club.Hub();
        var mark = counter.Mark();
        _ = runtime.Fut.Club.Hub();
        var warm = counter.Since(mark);
        Assert.Equal(10 + 1, warm.Count);
        Assert.DoesNotContain(warm, sql => sql.Contains(".settled\n", StringComparison.Ordinal));
    }

    private static TestRuntime Runtime(DatabaseCommandCounter counter) =>
        TestRuntime.Create(time: new TestRuntime.FixedTime(Now), marketTarget: 0, commandCounter: counter);

    private static long[] SeedAiListings(TestRuntime runtime, int count, bool watch, long endTime = Now + 3_600)
    {
        var resourceId = runtime.Game.Players.Ids.First();
        var player = runtime.Game.Players.Find(resourceId)!;
        var tradeIds = new long[count];
        for (var index = 0; index < count; index++)
        {
            var preview = runtime.Fut.Items.BuildPlayer(resourceId, 800_000_000_000 + index, "market");
            var tradeId = runtime.Game.Store().InsertMarketListing(
                MarketListingKind.Ai,
                "Phase 4 baseline",
                null,
                resourceId,
                preview,
                player.Rating,
                150,
                1_000,
                endTime - 3_600,
                endTime);
            tradeIds[index] = tradeId;
            if (watch)
            {
                runtime.Game.Store().WatchMarketListing(tradeId);
            }
        }
        return tradeIds;
    }

}
