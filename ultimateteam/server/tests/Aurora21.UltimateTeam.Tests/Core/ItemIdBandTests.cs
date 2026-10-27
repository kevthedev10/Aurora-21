using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>Every club issues item ids from its own band (migration 045), so no two clubs share an item id.</summary>
public sealed class ItemIdBandTests
{
    private static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task NewClubsStartInTheirOwnBandAndStopAtItsEnd()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var other = (await rt.Game.RunAsync("fixture-other", w => w.EnsureClubAsync(new ClubIdentity(910501, 910502), 1000), CancellationToken.None)).ClubId;
        var first = await rt.Game.UnitOfWork.RunAsync(rt.Game.ClubId, "reserve-a", w => w.ReserveNativeItemIdsAsync(1), CancellationToken.None);
        var second = await rt.Game.UnitOfWork.RunAsync(other, "reserve-b", w => w.ReserveNativeItemIdsAsync(1), CancellationToken.None);

        // next_item_id holds the last id issued, so a new club's first card is one past its band start.
        Assert.Equal(ClubRepository.FirstItemIdOf(other) + 1, second.First.Value);
        Assert.InRange(first.First.Value, ClubRepository.FirstItemIdOf(rt.Game.ClubId), ClubRepository.FirstItemIdOf(rt.Game.ClubId + 1) - 1);

        // A club never issues an id from the next club's band or its own reserved virtual ids at the top of the band.
        var reserved = ClubRepository.FirstItemIdOf(other + 1) - ClubRepository.VirtualItemIdReserve;
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await ExecuteAsync(source, $"UPDATE ultimateteam.clubs SET next_item_id={reserved - 2} WHERE id={other}");
        var last = await rt.Game.UnitOfWork.RunAsync(other, "reserve-last", w => w.ReserveNativeItemIdsAsync(2), CancellationToken.None);
        Assert.Equal(reserved - 1, last.First.Value);
        Assert.Equal(reserved + 1, rt.Game.Rules.Club.VirtualItemId(other, "activeStadium"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rt.Game.UnitOfWork.RunAsync(other, "reserve-past", w => w.ReserveNativeItemIdsAsync(1), CancellationToken.None));
    }

    [Fact]
    public async Task TwoClubsServeTheirOwnVirtualCosmeticIds()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var other = (await rt.Game.RunAsync("fixture-other", w => w.EnsureClubAsync(new ClubIdentity(910601, 910602), 1000), CancellationToken.None)).ClubId;
        var mine = await rt.Game.ReadAsync(uow => rt.Fut.Club.ActiveClubItemsAsync(uow), CancellationToken.None);
        var theirs = rt.Game.Rules.Club.RenderedDefaultActiveItems(other);

        // Items the club owns (its starter kits) keep their own ids; every unowned virtual cosmetic uses the club's reserved id.
        var owned = rt.Game.Store().ListItems().Select(item => item.Int("id")).ToHashSet();
        Assert.All(mine.Where(item => !owned.Contains(item.Int("id"))),
            item => Assert.Equal(rt.Game.Rules.Club.VirtualItemId(rt.Game.ClubId, item.Text("itemState")), item.Int("id")));
        Assert.Contains(mine, item => item.Text("itemState") == "activeBadge" && item.Int("id") == rt.Game.Rules.Club.VirtualItemId(rt.Game.ClubId, "activeBadge"));
        Assert.All(theirs, item => Assert.Equal(rt.Game.Rules.Club.VirtualItemId(other, item.Text("itemState")), item.Int("id")));
        Assert.Empty(mine.Select(item => item.Int("id")).Intersect(theirs.Select(item => item.Int("id"))));
    }


}
