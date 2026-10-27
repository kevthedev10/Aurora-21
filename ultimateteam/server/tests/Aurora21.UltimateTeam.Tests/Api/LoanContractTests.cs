using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class LoanContractTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public async Task LoansExpireOnceWhilePermanentCardsRemainPlayable()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var items = rt.Game.Store().ListItems("club").Where(i => i.Text("itemType") == "player").Take(2).ToArray();
        var loan = items[0];
        loan["loans"] = 1;
        loan["contract"] = 0;
        loan["contracts"] = 0;
        loan["untradeable"] = true;
        rt.Game.Store().SaveItem(loan);
        var permanent = items[1];
        permanent["contract"] = 0;
        permanent["contracts"] = 0;
        rt.Game.Store().SaveItem(permanent);
        Assert.Equal(rt.Game.Rules.Cards.PermanentContracts, rt.Fut.Native.PlayerItem(permanent).Int("contract"));
        Assert.Equal(rt.Game.Rules.Cards.PermanentContracts, rt.Fut.Native.PackItem(permanent).Int("contract"));
        var body = new JsonObject { ["matchId"] = 9001, ["endReason"] = "WIN",
            ["items"] = new JsonArray(new JsonObject { ["id"] = loan.Int("id") }, new JsonObject { ["id"] = permanent.Int("id") }) };
        rt.CallObject("POST", $"{G}/match/end", body);
        var saved = await rt.Game.RunAsync("loan-read", w => w.GetItemAsync(loan.Int("id")), CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(0, saved!.Int("loans"));
        Assert.True(saved.Bool("expiredLoan"));
        var native = rt.Fut.Native.PlayerItem(saved);
        Assert.Equal(0, native.Int("contract"));
        Assert.True(native.Int("loans") > 0); // Native loan flag remains set at expiry.
        Assert.Equal(0, native.Int("discardValue"));
        var packWire = rt.Fut.Native.PackItem(saved);
        Assert.Equal(0, packWire.Int("contract"));
        Assert.False(packWire.ContainsKey("expiredLoan"));
        rt.CallObject("POST", $"{G}/match/end", body);
        saved = await rt.Game.RunAsync("loan-replay-read", w => w.GetItemAsync(loan.Int("id")), CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True(saved!.Bool("expiredLoan"));
        Assert.Equal(0, saved.Int("loans"));
        var squad = rt.Game.Store().GetSquad(1)!;
        squad["players"] = new JsonArray(new JsonObject { ["index"] = 0, ["itemData"] = new JsonObject { ["id"] = loan.Int("id") } });
        rt.Call("PUT", $"{G}/squad/1", squad);
        var refused = rt.CallRaw("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1 });
        Assert.Equal(400, refused.Status);
        Assert.Contains("expired loan", refused.Body["reason"].S());

        // Live 10-05: an expired loan among the reserves refused every match; only starters and substitutes play.
        squad = rt.Game.Store().GetSquad(1)!;
        squad["players"] = new JsonArray(new JsonObject { ["index"] = 21, ["itemData"] = new JsonObject { ["id"] = loan.Int("id") } });
        rt.Call("PUT", $"{G}/squad/1", squad);
        Assert.Equal(200, rt.CallRaw("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1 }).Status);
        squad["players"] = new JsonArray(new JsonObject { ["index"] = 17, ["itemData"] = new JsonObject { ["id"] = loan.Int("id") } });
        rt.Call("PUT", $"{G}/squad/1", squad);
        Assert.Equal(400, rt.CallRaw("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1 }).Status);
    }

    [Fact]
    public async Task ClubSearchPagesShowLoanGamesAsContracts()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var starters = rt.Game.Store().ListItems("club").Where(i => i.Text("itemType") == "player").Take(2).ToArray();
        var loan = starters[0];
        loan["loans"] = 5;
        loan["contract"] = 0;
        loan["contracts"] = 0;
        loan["untradeable"] = true;
        rt.Game.Store().SaveItem(loan);
        var expired = starters[1];
        expired["loans"] = 1;
        expired["contract"] = 0;
        expired["contracts"] = 0;
        expired["untradeable"] = true;
        rt.Game.Store().SaveItem(expired);
        rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["matchId"] = 9200, ["endReason"] = "WIN",
            ["items"] = new JsonArray(new JsonObject { ["id"] = expired.Int("id") }) });

        // The club list the squad screen's player picker reads (type=player) and the bucket views (no type,
        // type=all) must all carry a loan's games as its contract: FIFA shows a loan's matches left from the
        // contract field, and the stored contract is 0. An expired loan keeps its native loan flag instead.
        foreach (var query in new[] { "type=player&count=500", "count=500", "type=all&count=500" })
        {
            var page = rt.CallObject("GET", $"{G}/club?{query}");
            var card = page["itemData"].Objects().Single(item => item["id"].L() == loan.Int("id"));
            Assert.Equal(5, card["contract"].L());
            Assert.Equal(5, card["loans"].L());
            var dead = page["itemData"].Objects().Single(item => item["id"].L() == expired.Int("id"));
            Assert.Equal(0, dead["contract"].L());
            Assert.Equal(1, dead["loans"].L());
            Assert.False(dead.ContainsKey("expiredLoan"));
        }
    }

    [Fact]
    public async Task UnusedSubstitutesNoContestsAndCancelledSearchesDoNotConsumeLoans()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var player = rt.Game.Store().ListItems("club").First(i => i.Text("itemType") == "player");
        player["loans"] = 3;
        player["contract"] = 0;
        player["contracts"] = 0;
        player["untradeable"] = true;
        rt.Game.Store().SaveItem(player);
        var entries = new JsonArray();
        for (var i = 0; i < 11; i++) entries.Add(new JsonObject { ["id"] = 0, ["fitness"] = 80 });
        entries.Add(new JsonObject { ["id"] = player.Int("id"), ["fitness"] = 99 });
        rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["matchId"] = 9100, ["endReason"] = "WIN", ["items"] = entries });
        foreach (var reason in new[] { "NO_CONTEST", "DNF" })
            rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = reason,
                ["items"] = new JsonArray(new JsonObject { ["id"] = player.Int("id") }) });
        var saved = await rt.Game.RunAsync("loan-unused-read", w => w.GetItemAsync(player.Int("id")), CancellationToken.None);
        Assert.Equal(3, saved!.Int("loans"));
        rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["matchId"] = 9101, ["endReason"] = "QUIT",
            ["items"] = new JsonArray(new JsonObject { ["id"] = player.Int("id") }, new JsonObject { ["id"] = player.Int("id") }) });
        saved = await rt.Game.RunAsync("loan-quit-read", w => w.GetItemAsync(player.Int("id")), CancellationToken.None);
        Assert.Equal(2, saved!.Int("loans")); // Duplicate report rows consume one match.
        rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["matchId"] = 9101, ["endReason"] = "QUIT",
            ["items"] = new JsonArray(new JsonObject { ["id"] = player.Int("id") }) });
        saved = await rt.Game.RunAsync("loan-quit-replay-read", w => w.GetItemAsync(player.Int("id")), CancellationToken.None);
        Assert.Equal(2, saved!.Int("loans"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABoughtCopyOfALoanedPlayerJoinsTheClubBesideTheLoan(bool expired)
    {
        // Retail: a club can own a card next to its loan, so the loan is never what a bought card duplicates (10-08).
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var loan = store.ListItems("club").First(i => i.Text("itemType") == "player");
        loan["loans"] = expired ? 0 : 5;
        if (expired) loan["expiredLoan"] = true;
        loan["contract"] = 0;
        loan["contracts"] = 0;
        loan["untradeable"] = true;
        store.SaveItem(loan);
        var bought = await rt.Game.RunAsync("loan-copy", async w =>
        {
            var item = rt.Fut.Items.BuildPlayer(loan.Int("resourceId"), await w.NextItemIdAsync(), "unassigned");
            item["untradeable"] = false;
            await w.SaveItemAsync(item);
            return item;
        }, CancellationToken.None);

        Assert.False(rt.Fut.Inventory.WouldDuplicateOwnedPlayer(bought.Int("id")));
        var response = rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = bought.Int("id"), ["pile"] = "club" }),
        });

        var result = Assert.Single(response["itemData"]!.AsArray().OfType<JsonObject>());
        Assert.Equal("club", result.Text("pile"));
        Assert.False(result.Bool("IS_DUPLICATE"));
        Assert.Equal("club", store.GetItem(bought.Int("id"))!.Text("pile"));
        Assert.Equal("club", store.GetItem(loan.Int("id"))!.Text("pile"));
    }
}
