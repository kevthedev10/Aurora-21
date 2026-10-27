using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalInventoryTests
{
    [Fact]
    public async Task EveryFamilyAndChildrenRoundTripAcrossRunnersWithScopedNativeNumbers()
    {
        await using var db = await Database.Create();
        var club = await db.Open(1101);
        var other = await db.Open(1102);
        var rows = Enumerable.Range(1, 5).Select(f => db.Item(club, f, new NativeItemId(9000 + f))).ToArray();
        var player = rows[0];
        rows[0] = player with {
            Player = player.Player! with { Contracts = 21, LoanGames = 4, PositionOverride = "ST", Assists = 2,
                StyleDefinitionId = db.Catalog.Consumables.First(c => c.ConsumableKind == "chemistry_style").DefinitionId,
                NativePlayStyle = 0, DuplicateAcknowledged = true },
            Stats = new[] { new PlayerStatValue(0, default, "current", 0, 3), new PlayerStatValue(0, default, "lifetime", 4, 12) },
            Extension = new ItemExtensionState(default, 1, new CatalogJson("{\"testUnknown\":4}"), new CatalogJson("{\"contract\":\"21\"}"))
        };
        var saved = await db.Runner().RunAsync(club, "save-all", w => w.SaveRelationalItemsAsync(rows));
        Assert.All(saved, h => Assert.NotEqual(h.Id.Value, h.NativeItemId.Value));
        await db.Runner().RunAsync(other, "save-other", w => w.SaveRelationalItemsAsync(new[] { db.Item(other, 1, rows[0].Header.NativeItemId) }));
        var loaded = await db.Runner().RunReadAsync(club, w => w.ListRelationalItemsAsync());
        Assert.Equal(5, loaded.Count);
        Assert.Equal(21, loaded[0].Player!.Contracts);
        Assert.Equal(4, loaded[0].Player!.LoanGames);
        Assert.Equal("ST", loaded[0].Player!.PositionOverride);
        Assert.Equal(rows[0].Player!.StyleDefinitionId, loaded[0].Player!.StyleDefinitionId);
        Assert.True(loaded[0].Player!.DuplicateAcknowledged);
        Assert.Equal(new long[] { 3, 12 }, loaded[0].Stats!.Select(s => s.Value));
        Assert.Contains("testUnknown", loaded[0].Extension!.Payload.Text);
        Assert.Equal(7, loaded[1].Manager!.Contracts);
        Assert.Equal(3, loaded[2].Consumable!.Quantity);
        Assert.True(loaded[3].Customisation!.IsLockedOverride);
        Assert.Equal("unrevealed", loaded[4].Pick!.State);
        var foreign = loaded[0] with { Header = loaded[0].Header with { ClubId = other } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Runner().RunAsync(other, "foreign-key", w => w.SaveRelationalItemsAsync(new[] { foreign })));
        Assert.Equal(7, (await db.Runner().RunReadAsync(other, w => w.GetRelationalItemAsync(rows[0].Header.NativeItemId)))!.Player!.Contracts);
        var changed = loaded[0] with { Header = loaded[0].Header with { Pile = "unassigned" }, Stats = Array.Empty<PlayerStatValue>(),
            Extension = null };
        await db.Runner().RunAsync(club, "replace", w => w.SaveRelationalItemsAsync(new[] { changed }));
        var reopened = (await db.Runner().RunReadAsync(club, w => w.GetRelationalItemAsync(changed.Header.NativeItemId)))!;
        Assert.Equal("unassigned", reopened.Header.Pile);
        Assert.Empty(reopened.Stats!);
        Assert.Null(reopened.Extension);
        Assert.True(await db.Runner().RunAsync(club, "delete", w => w.DeleteRelationalItemAsync(changed.Header.NativeItemId)));
        Assert.NotNull(await db.Runner().RunReadAsync(other, w => w.GetRelationalItemAsync(changed.Header.NativeItemId)));
    }

    [Fact]
    public async Task BulkQueriesAreConstantAndNarrowOwnershipPreservesClubBeforeSquad()
    {
        await using var db = await Database.Create();
        var club = await db.Open(1201);
        var commands = new List<string>();
        var runner = db.Runner(commands.Add);
        var one = db.Item(club, 1, new NativeItemId(100));
        await runner.RunAsync(club, "one", w => w.SaveRelationalItemsAsync(new[] { one }));
        var saveCount = commands.Count;
        commands.Clear();
        var fifty = Enumerable.Range(1, 50).Select(i => db.Item(club, 1, new NativeItemId(i))).ToArray();
        fifty[0] = fifty[0] with { Header = fifty[0].Header with { Pile = "squad" } };
        await runner.RunAsync(club, "fifty", w => w.SaveRelationalItemsAsync(fifty));
        Assert.Equal(saveCount, commands.Count);
        commands.Clear();
        Assert.Equal(51, (await runner.RunReadAsync(club, w => w.ListRelationalItemsAsync())).Count);
        // Shared club lock + one header read that joins player state and extensions.
        Assert.Equal(2, commands.Count);
        commands.Clear();
        var identities = await runner.RunReadAsync(club, w => w.RelationalOwnedPlayerIdentitiesAsync());
        Assert.Single(commands);
        Assert.DoesNotContain("json", commands[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, identities[0].NativeItemId.Value);
        Assert.Equal(1, identities[^1].NativeItemId.Value);
        Assert.Equal(51, await runner.RunReadAsync(club, w => w.CountRelationalItemsAsync("club")) +
            await runner.RunReadAsync(club, w => w.CountRelationalItemsAsync("squad")));
        // Every family present, and the budget still must not grow with rows or families.
        await runner.RunAsync(club, "families", w => w.SaveRelationalItemsAsync(
            Enumerable.Range(2, 4).Select(f => db.Item(club, f, new NativeItemId(200 + f))).ToArray()));
        commands.Clear();
        var mixed = await runner.RunReadAsync(club, w => w.ListRelationalItemsAsync());
        Assert.Equal(55, mixed.Count);
        Assert.Equal(6, commands.Count); // Shared club lock + header (with player state and extensions) + four family queries.
        Assert.Equal(7, mixed.Single(i => i.Header.NativeItemId.Value == 202).Manager!.Contracts);
        Assert.Equal(3, mixed.Single(i => i.Header.NativeItemId.Value == 203).Consumable!.Quantity);
        Assert.True(mixed.Single(i => i.Header.NativeItemId.Value == 204).Customisation!.IsLockedOverride);
        Assert.Equal("unrevealed", mixed.Single(i => i.Header.NativeItemId.Value == 205).Pick!.State);
    }

    [Fact]
    public async Task FailureRollsBackPackBalanceAllocationAndEveryChildAndInvalidFamilyFailsAtCommit()
    {
        await using var db = await Database.Create();
        var club = await db.Open(1301);
        var runner = db.Runner();
        await runner.RunAsync(club, "reward", w => w.AddRelationalUnopenedPackAsync(9, "test", 123));
        var before = await runner.RunReadAsync(club, w => w.RelationalAccountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(club, "fault", async w => {
            Assert.NotNull(await w.TakeRelationalUnopenedPackAsync(9));
            Assert.True(await w.TrySpendRelationalCreditsAsync(80));
            var id = await w.NextNativeItemIdAsync();
            await w.SaveRelationalItemsAsync(new[] { db.Item(club, 1, id) });
            throw new InvalidOperationException("injected before commit");
        }));
        Assert.Equal(before, await runner.RunReadAsync(club, w => w.RelationalAccountAsync()));
        Assert.Single(await runner.RunReadAsync(club, w => w.RelationalUnopenedPacksAsync()));
        Assert.Empty(await runner.RunReadAsync(club, w => w.ListRelationalItemsAsync()));
        var mismatched = db.Item(club, 1, new NativeItemId(7));
        mismatched = mismatched with { Header = mismatched.Header with { DefinitionId = db.Definition(2) } };
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(club, "invalid-definition", w => w.SaveRelationalItemsAsync(new[] { mismatched })));
        await Assert.ThrowsAsync<PostgresException>(() => runner.RunAsync(club, "missing-family-child", w => w.ExecuteAsync("""
            INSERT INTO ultimateteam.items(club_id,native_item_id,definition_id,family_id,timestamp,pile,item_state,untradeable,owners,last_sale_price)
            VALUES(@club,777,@definition,1,123,'club','free',false,1,0)
            """, ("definition", db.Definition(1).Value))));
        var badQuantity = db.Item(club, 3, new NativeItemId(8));
        badQuantity = badQuantity with { Consumable = badQuantity.Consumable! with { UntradeableQuantity = 4 } };
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(club, "invalid-quantity", w => w.SaveRelationalItemsAsync(new[] { badQuantity })));
        Assert.Empty(await runner.RunReadAsync(club, w => w.ListRelationalItemsAsync()));
    }

    [Fact]
    public async Task RewardConsumptionIsOnceOnlyAndOldestAndIsolated()
    {
        await using var db = await Database.Create();
        var club = await db.Open(1401);
        var other = await db.Open(1402);
        var a = db.Runner(); var b = db.Runner();
        var oldest = await a.RunAsync(club, "reward-a", w => w.AddRelationalUnopenedPackAsync(4, "first", 20));
        var newest = await a.RunAsync(club, "reward-b", w => w.AddRelationalUnopenedPackAsync(4, "second", 10));
        await a.RunAsync(other, "foreign", w => w.AddRelationalUnopenedPackAsync(4, "other"));
        Assert.Equal(oldest, await a.RunAsync(club, "consume-oldest", w => w.TakeRelationalUnopenedPackAsync(4)));
        var results = await Task.WhenAll(a.RunAsync(club, "consume-a", w => w.TakeRelationalUnopenedPackAsync(4)),
            b.RunAsync(club, "consume-b", w => w.TakeRelationalUnopenedPackAsync(4)));
        Assert.Single(results, r => r is not null);
        Assert.Contains(newest, results);
        Assert.Single(await a.RunReadAsync(other, w => w.RelationalUnopenedPacksAsync()));
    }

    private sealed class Database : IAsyncDisposable
    {
        private readonly string _connection = PostgresTestCluster.CreateInstalledDatabase();
        private Database() => Source = NpgsqlDataSource.Create(_connection);
        public NpgsqlDataSource Source { get; }
        public RelationalCatalogSnapshot Catalog { get; private set; } = null!;
        public static async Task<Database> Create()
        {
            var db = new Database();
            try {
                // Migration 048 installs the Player Pick token definition (family 5, no native resource number).
                await RelationalSchemaBootstrap.InstallFreshAsync(db.Source);
                db.Catalog = await RelationalCatalogLoader.LoadAsync(db.Source);
                return db;
            } catch { await db.DisposeAsync(); throw; }
        }
        public ClubUnitOfWorkRunner Runner(Action<string>? observer = null) => new(Source, TimeProvider.System, 0, NullLogger.Instance, observer);
        public async Task<long> Open(long persona) => (await Runner().RunAsync(0, "open", w => w.EnsureRelationalClubAsync(new ClubIdentity(persona, persona), 100))).Account.ClubId;
        public DefinitionId Definition(int family) => Catalog.Definitions.First(d => d.FamilyId == family).DefinitionId;
        public RelationalItemAggregate Item(long club, int family, NativeItemId native)
        {
            var header = new RelationalItemHeader(default, club, native, Definition(family), (short)family, 123, "club", "free", false, 1, 0);
            return family switch {
                1 => new(header, Player: new(default, 7, 99, "none", 0, 0, 0, 50, 0, null, null, 0, 0, 0, 1, false)),
                2 => new(header, Manager: new(default, 7, null)),
                3 => new(header, Consumable: new(default, 3, 1)),
                4 => new(header, Customisation: new(default, true)),
                5 => new(header, Pick: new(default, Catalog.Players[0].DefinitionId, 0, 2, 9, "unrevealed", null)),
                _ => throw new ArgumentOutOfRangeException(nameof(family))
            };
        }
        public async ValueTask DisposeAsync() { await Source.DisposeAsync(); PostgresTestCluster.DropDatabase(_connection); }
    }
}
