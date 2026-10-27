using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// RenderNativePlayer builds the native squad card straight from typed state instead of rendering the
/// full wire document and projecting it. The lenient readers it replaces have fallbacks that are easy
/// to get subtly wrong — a zero contract becomes seven, a zero owners becomes one — so the only thing
/// that makes the short path safe is that it stays byte-identical to the long one.
/// </summary>
public sealed class NativePlayerRenderParityTests
{
    private static TestRuntime Create()
    {
        var connection = PostgresTestCluster.CreateInstalledDatabase();
        using (var source = NpgsqlDataSource.Create(connection))
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
        return TestRuntime.Create(connectionString: connection, relationalSlice: true, marketTarget: 0,
            time: new TestRuntime.FixedTime(1700000000));
    }

    [Fact]
    public async Task TheDirectNativeCardMatchesRenderingThenProjecting()
    {
        using var rt = Create();
        await Seed(rt);
        var compared = 0;
        await rt.Game.ReadAsync(async uow =>
        {
            var mapper = uow.RelationalItems!.Mapper;
            var native = rt.Fut.Native;
            var items = await uow.ListRelationalItemsAsync();
            foreach (var item in items.Where(i => i.Player is not null))
            {
                if (!mapper.CanRenderNativePlayer(item))
                {
                    continue;
                }
                var viaDocument = native.PlayerItem(mapper.Render(item));
                var direct = mapper.RenderNativePlayer(item);
                Assert.Equal(FutJson.Serialize(viaDocument), FutJson.Serialize(direct));
                compared++;
            }
            return true;
        }, CancellationToken.None);

        Assert.True(compared > 0, "the seeded club produced no eligible player cards to compare");
    }

    [Theory]
    // The fallbacks that differ from the obvious typed value.
    [InlineData(0, 0, 0, 0)]        // zero contract -> 7, zero owners -> 1
    [InlineData(7, 1, 99, 50)]
    [InlineData(3, 2, 0, 0)]        // zero fitness and morale stay zero
    [InlineData(99, 5, 50, 100)]
    public async Task TypedEdgeValuesStillMatch(long contracts, long owners, long fitness, long morale)
    {
        using var rt = Create();
        await Seed(rt);
        await rt.Game.ReadAsync(async uow =>
        {
            var mapper = uow.RelationalItems!.Mapper;
            var native = rt.Fut.Native;
            var source = (await uow.ListRelationalItemsAsync()).First(i => i.Player is not null
                && mapper.CanRenderNativePlayer(i));
            var item = source with
            {
                Header = source.Header with { Owners = owners, Timestamp = 0, ItemState = null },
                Player = source.Player! with { Contracts = contracts, Fitness = fitness, Morale = morale },
            };
            Assert.Equal(FutJson.Serialize(native.PlayerItem(mapper.Render(item))),
                FutJson.Serialize(mapper.RenderNativePlayer(item)));
            return true;
        }, CancellationToken.None);
    }

    /// <summary>A fresh relational database owns no cards, so the comparison needs its own.</summary>
    private static async Task Seed(TestRuntime rt)
    {
        await rt.Game.RunAsync("native-parity-seed", async uow =>
        {
            var resources = rt.Game.Players.Entries.Take(40).Select(e => e.Key).ToArray();
            foreach (var resource in resources)
            {
                var item = rt.Fut.Items.BuildPlayer(resource, await uow.NextItemIdAsync(), "club");
                await uow.SaveItemAsync(item);
            }
            return true;
        }, CancellationToken.None);
    }
}
