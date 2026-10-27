using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Services;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalRuntimeTests
{
    private static async Task<T> Scalar<T>(string connection, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static void SelectBackend(GameContext game, string backend)
    {
        var store = new UserConfigStore(game.Paths);
        var config = store.Read("config.json", store.ReadExample("config.json")!)!;
        config["storage_backend"] = backend;
        File.WriteAllText(Path.Combine(game.Paths.UserConfigRoot, "config.json"), config.ToJsonString());
    }

    [Fact]
    public async Task DefaultRuntimeUsesRelationalTablesAndSharedClubsReopen()
    {
        using var rt = TestRuntime.Create();
        Assert.False(await Scalar<bool>(rt.ConnectionString, "SELECT to_regclass('public.items') IS NOT NULL"));
        var inventory = await rt.Game.ReadAsync(w => w.ListItemsAsync(), CancellationToken.None);
        Assert.NotEmpty(inventory);
        using var player = await rt.Game.ForPlayerAsync(PlayerIdentity.Of("910090"));
        Assert.NotEqual(rt.Game.ClubId, player.ClubId);
        await FutServices.CreateAsync(player);
        await player.RunAsync("test-credit", w => w.AddCreditsToAsync(w.ClubId, 321), CancellationToken.None);
        using var reopened = await rt.Game.ForPlayerAsync(player.Player);
        await FutServices.CreateAsync(reopened);
        Assert.Equal(player.ClubId, reopened.ClubId);
        Assert.Equal(player.Config.InitialCredits + 321, await reopened.ReadAsync(w => w.CreditsAsync(), CancellationToken.None));
        Assert.Equal(rt.Game.Config.InitialCredits, await rt.Game.ReadAsync(w => w.CreditsAsync(), CancellationToken.None));
        Assert.Equal(2L, await Scalar<long>(rt.ConnectionString, "SELECT count(*) FROM ultimateteam.clubs"));
    }

    [Fact]
    public async Task RetiredBackendSelectionFailsWithoutChangingClub()
    {
        using var rt = TestRuntime.Create();
        var before = await Scalar<long>(rt.ConnectionString, "SELECT count(*) FROM ultimateteam.items");
        SelectBackend(rt.Game, "legacy");
        await Assert.ThrowsAsync<RuntimeSetupException>(() => GameContext.CreateAsync(rt.Game.Paths,
            NullLoggerFactory.Instance, true, connectionString: rt.ConnectionString));
        Assert.Equal(before, await Scalar<long>(rt.ConnectionString, "SELECT count(*) FROM ultimateteam.items"));
    }

    [Fact]
    public async Task IncompleteRelationalStorageFailsWithoutResettingClub()
    {
        using var rt = TestRuntime.Create();
        var count = await Scalar<long>(rt.ConnectionString, "SELECT count(*) FROM ultimateteam.items");
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using (var command = source.CreateCommand("DROP TABLE ultimateteam.sbc_squad_items"))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<RuntimeSetupException>(() => GameContext.CreateAsync(rt.Game.Paths,
            NullLoggerFactory.Instance, true, connectionString: rt.ConnectionString));
        Assert.Equal(count, await Scalar<long>(rt.ConnectionString, "SELECT count(*) FROM ultimateteam.items"));
    }
}
