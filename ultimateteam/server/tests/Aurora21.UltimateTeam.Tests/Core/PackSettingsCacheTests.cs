using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>The runtime exposes a stable, copied view of packs loaded from PostgreSQL.</summary>
public sealed class PackSettingsCacheTests
{
    [Fact]
    public void LoadedPackSettingsAreCopiedForCallers()
    {
        using var runtime = TestRuntime.Create();
        var packs = runtime.Game.Packs;
        var original = packs.GetPackConfig(74).Int("price");
        packs.Load().Obj("packs")!.Obj("74")!["price"] = 1;
        packs.GetPackConfig(74)["price"] = 2;
        Assert.Equal(original, packs.GetPackConfig(74).Int("price"));
        Assert.Equal(original, packs.Load().Obj("packs")!.Obj("74")!.Int("price"));
    }
}
