using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class DatabaseCommandCounterTests
{
    [Fact]
    public void CountsApplicationCommandsAfterRuntimeStartup()
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = TestRuntime.Create(commandCounter: counter);
        var mark = counter.Mark();

        _ = runtime.Game.Store().Credits();

        var commands = counter.Since(mark);
        Assert.Single(commands);
        Assert.Equal("-- query: club.column.credits SELECT credits FROM ultimateteam.clubs WHERE id=@club", commands[0]);
    }

    [Fact]
    public void AdditionalPlayerStoresUseTheSameCounter()
    {
        var counter = new DatabaseCommandCounter();
        using var runtime = TestRuntime.Create(commandCounter: counter);
        using var player = runtime.Game.ForPlayer(PlayerIdentity.Of("222222"));
        var mark = counter.Mark();

        _ = player.Store().Credits();

        Assert.Single(counter.Since(mark));
    }
}
