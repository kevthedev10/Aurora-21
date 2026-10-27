using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The club row lock is taken once per transaction, and the inventory read is a single query joining
/// the headers to every child state table. Both trade round trips for held state, so these pin the
/// budgets and the correctness those trades must not lose.
/// </summary>
public sealed class ClubLockAndFamilyReadTests
{
    private const long Now = 1700000000;

    private static TestRuntime Create(Action<string>? observer = null, long persona = 910911)
    {
        var connection = PostgresTestCluster.CreateInstalledDatabase();
        using (var source = NpgsqlDataSource.Create(connection))
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
        return TestRuntime.Create(connectionString: connection, relationalSlice: true,
            time: new TestRuntime.FixedTime(Now), commandObserver: observer, marketTarget: 0,
            prepareRoot: root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "user-config"));
                File.WriteAllText(Path.Combine(root, "user-config", "config.json"), new JsonObject
                {
                    ["persona_id"] = persona,
                    ["nucleus_id"] = persona + 100000,
                    ["display_name"] = "Locks" + persona,
                    ["database"] = new JsonObject { ["deadlock_retries"] = 0 },
                }.ToJsonString());
            });
    }

    private static int Count(List<string> commands, string fragment) =>
        commands.Count(sql => sql.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public async Task TheClubRowLockIsTakenOncePerTransactionAndStillUpgradesToExclusive()
    {
        var commands = new List<string>();
        using var rt = Create(commands.Add);
        await rt.Game.RunAsync("lock-reuse", async uow =>
        {
            commands.Clear();
            Assert.True(await uow.EnsureRelationalClubLockAsync(exclusive: false));
            Assert.Equal(1, Count(commands, "FOR SHARE"));

            // Repeated shared requests are answered from the record.
            Assert.True(await uow.EnsureRelationalClubLockAsync(exclusive: false));
            Assert.True(await uow.EnsureRelationalClubLockAsync(exclusive: false));
            Assert.Equal(1, Count(commands, "FOR SHARE"));

            // A held shared lock never answers an exclusive request: the upgrade must reach the database.
            await uow.LockRelationalClubAsync();
            Assert.Equal(1, Count(commands, "FOR UPDATE"));

            // Once exclusive is held it covers both, so neither statement runs again.
            await uow.LockRelationalClubAsync();
            Assert.True(await uow.EnsureRelationalClubLockAsync(exclusive: false));
            Assert.Equal(1, Count(commands, "FOR UPDATE"));
            Assert.Equal(1, Count(commands, "FOR SHARE"));
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ARolledBackSavepointRewindsTheHeldLockRecord()
    {
        var commands = new List<string>();
        using var rt = Create(commands.Add);
        await rt.Game.RunAsync("lock-savepoint", async uow =>
        {
            commands.Clear();
            // A lock taken inside a step that rolls back is released by PostgreSQL, so the record must
            // forget it and the next request must take it again.
            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.SavepointAsync(async () =>
            {
                await uow.LockRelationalClubAsync();
                Assert.Equal(1, Count(commands, "FOR UPDATE"));
                throw new InvalidOperationException("step fails");
            }).AsTask());
            Assert.False(uow.HeldClubLocks.ContainsKey(uow.ClubId));

            await uow.LockRelationalClubAsync();
            Assert.Equal(2, Count(commands, "FOR UPDATE"));

            // A step that succeeds keeps its lock: RELEASE SAVEPOINT does not drop row locks.
            await uow.SavepointAsync(async () => await uow.EnsureRelationalClubLockAsync(exclusive: false));
            Assert.True(uow.HeldClubLocks[uow.ClubId]);
            await uow.LockRelationalClubAsync();
            Assert.Equal(2, Count(commands, "FOR UPDATE"));
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ALockTakenBeforeAFailedStepSurvivesThatStepsRollback()
    {
        using var rt = Create();
        await rt.Game.RunAsync("lock-savepoint-outer", async uow =>
        {
            // The outer lock predates the savepoint, so rolling back to it does not release the lock
            // and the record must keep saying so.
            await uow.LockRelationalClubAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => uow.SavepointAsync(() =>
                throw new InvalidOperationException("step fails")).AsTask());
            Assert.True(uow.HeldClubLocks[uow.ClubId]);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OneInventoryReadCoversEveryFamilyWhicheverArePresent()
    {
        var commands = new List<string>();
        using var rt = Create(commands.Add);
        var resource = rt.Game.Players.Entries.First().Key;
        await rt.Game.RunAsync("family-fixture", async uow =>
        {
            var player = rt.Fut.Items.BuildPlayer(resource, await uow.NextItemIdAsync(), "club");
            await uow.SaveItemAsync(player);
            return true;
        }, CancellationToken.None);

        // Player state arrives with the headers; a player-only pile never asks the families it has no headers for.
        commands.Clear();
        var players = await rt.Game.ReadAsync(uow => uow.ListItemsAsync("club"), CancellationToken.None);
        Assert.NotEmpty(players);
        Assert.Equal(1, Count(commands, "inventory.headers"));
        Assert.Equal(0, Count(commands, "inventory.player-state"));
        Assert.Equal(0, Count(commands, "inventory.consumable-state"));
        Assert.Equal(0, Count(commands, "inventory.player-pick-state"));

        // A mixed pile still hydrates every family present, and the documents keep their own state.
        await rt.Game.RunAsync("family-fixture-mixed", async uow =>
        {
            var style = rt.Game.Cards.ChemistryStyles.First().ResourceId;
            await uow.SaveItemAsync(rt.Fut.Items.BuildConsumable(style, await uow.NextItemIdAsync(), "club"));
            return true;
        }, CancellationToken.None);
        commands.Clear();
        var mixed = await rt.Game.ReadAsync(uow => uow.ListItemsAsync("club"), CancellationToken.None);
        Assert.Equal(1, Count(commands, "inventory.headers"));
        Assert.Equal(0, Count(commands, "inventory.player-state"));
        Assert.Equal(1, Count(commands, "inventory.consumable-state"));
        Assert.Contains(mixed, item => item.Text("itemType") == "player");
        Assert.Contains(mixed, item => item.Text("itemType") != "player");
        // The player documents are unchanged by the narrowing.
        Assert.Equal(players.Select(item => item.ToJsonString()).ToArray(),
            mixed.Where(item => item.Text("itemType") == "player").Select(item => item.ToJsonString()).ToArray());

        // An empty pile reads no separate state tables at all.
        commands.Clear();
        Assert.Empty(await rt.Game.ReadAsync(uow => uow.ListItemsAsync("trade"), CancellationToken.None));
        Assert.Equal(1, Count(commands, "inventory.headers"));
        Assert.Equal(0, Count(commands, "inventory.player-state"));
        Assert.Equal(0, Count(commands, "inventory.consumable-state"));
    }
}
