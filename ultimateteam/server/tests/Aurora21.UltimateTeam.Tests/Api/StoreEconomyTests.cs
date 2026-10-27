using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// A pack the club does not own is only ever sold by the store, and always paid for (issues.md E-1 to E-3).
/// Owned reward packs keep opening for free from My Packs.
/// </summary>
public sealed class StoreEconomyTests
{
    private const string G = "/ut/game/fifa21";
    private const long DisabledPackId = 95;

    private static TestRuntime Create() => TestRuntime.Create(marketTarget: 0, seedSql: $$"""
        INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,manager_chance,chem_style_chance)
        VALUES({{DisabledPackId}},'store',false,'Synthetic disabled pack',0,12,100,0,0,0,0,0)
        ;UPDATE ultimateteam.store_packs SET enabled=true WHERE pack_id=74
        """);

    private static (long Credits, long Points, int Unassigned) Balances(TestRuntime rt)
    {
        var store = rt.Game.Store();
        return (store.Credits(), store.FifaPoints(), store.ListItems("unassigned").Count);
    }

    [Fact]
    public void AnUnownedPackIsChargedEvenWhenTheRequestDeclinesCredits()
    {
        using var rt = Create();
        var (packId, price) = rt.Game.Packs.Load().Obj("packs")!
            .Select(entry => (Id: long.Parse(entry.Key, CultureInfo.InvariantCulture), Config: entry.Value!.AsObject()))
            .Where(entry => entry.Config.Bool("enabled", true) && entry.Config.IntKeep("price", 0) > 0)
            .Select(entry => (entry.Id, Price: entry.Config.IntKeep("price", 0))).First();
        var before = Balances(rt);

        var opened = rt.CallObject("POST", $"{G}/purchased/items",
            new JsonObject { ["packId"] = packId, ["useCredits"] = 0, ["usePreOrder"] = 0, ["currency"] = "COINS" });

        Assert.Equal("PURCHASECOMPLETE", opened["state"]!.GetValue<string>());
        Assert.Equal(before.Credits - price, Balances(rt).Credits);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("reward")]
    [InlineData("disabled")]
    public void PacksTheStoreDoesNotSellAreRefused(string kind)
    {
        using var rt = Create();
        var packId = kind switch
        {
            "unknown" => 987_654,
            "reward" => RewardOnlyPackId(rt),
            _ => DisabledPackId,
        };
        var before = Balances(rt);

        var (status, _, _) = rt.CallRaw("POST", $"{G}/purchased/items",
            new JsonObject { ["packId"] = packId, ["useCredits"] = 1, ["currency"] = "COINS" });

        Assert.Equal(400, status);
        Assert.Equal(before, Balances(rt));
    }

    /// <summary>A pack only rewards use (objectives, SBC, player picks): the store lineup does not sell it.</summary>
    private static long RewardOnlyPackId(TestRuntime rt) =>
        rt.Game.Packs.Load().Obj("packs")!.Select(entry => long.Parse(entry.Key, CultureInfo.InvariantCulture))
            .Where(id => id >= 100 && !rt.Game.Packs.IsForSale(id)).Order().First();
}
