using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Store pack mechanics (migration 049 onward) on synthetic packs, since migrations 095-097 retired the ported packs 48-66:
/// sale currencies and admin-only packs, the identity pack, red picks and the TOTW reward packs.
/// </summary>
public sealed class StorePackMechanicsTests
{
    private const string G = "/ut/game/fifa21";

    private static TestRuntime Create()
    {
        // Legacy promo-only packs need a broad test schedule; production starts with TOTW 1–3 only. The event links every
        // promo an event may reference (ContentSnapshot.ValidatePromoReferences): active, not a standard pool, standard card type
        // (PromoEdits.StandardTypes) or icon tier, and neither FUT Champions nor the grant-only Developer Cards.
        var rt = TestRuntime.Create(marketTarget: 0, seedSql: """
            INSERT INTO ultimateteam.promo_events(code,name,enabled,starts_at,ends_at)
            VALUES ('legacy_pack_test','Legacy pack test',true,'2020-01-01','2100-01-01');
            INSERT INTO ultimateteam.promo_event_promos(event_id,promo_id)
            SELECT schedule.event_id,promo.promo_id
            FROM ultimateteam.promo_events schedule CROSS JOIN ultimateteam.promos promo
            WHERE schedule.code='legacy_pack_test' AND promo.status = 'active' AND promo.category <> 'standard_pool'
              AND promo.category <> 'icon_tier' AND promo.code NOT IN ('fut_champions', 'developer_cards',
                  'bronze_common', 'gold_common', 'silver_common', 'bronze_rare', 'gold_rare', 'silver_rare');
            -- Synthetic store packs: sale currencies (from ported packs 49, 50 and 58), an admin-only pack (57) and the
            -- identity pack (66, migration 053).
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,points_price,allowed_currencies,admin_only,size,
                gold_weight,silver_weight,bronze_weight,rare_slots,store_group)
            VALUES
                (90,'store',true,'Synthetic points-only pack',1000,250,ARRAY['points'],false,12,100,0,0,1,'gold'),
                (91,'store',true,'Synthetic coins-only pack',1000,NULL,ARRAY['coins'],false,12,100,0,0,1,'gold'),
                (92,'store',true,'Synthetic coins and points pack',1000,80000,ARRAY['coins','points'],false,12,100,0,0,1,'gold'),
                (93,'store',true,'Synthetic admin-only pack',1000,NULL,ARRAY['coins'],true,12,100,0,0,1,'gold');
            INSERT INTO ultimateteam.token_store_listings(pack_id,offer_id,price,enabled,untradeable)
            VALUES(90,1000,250,true,false),(92,1001,80000,true,false);
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,
                rare_slots,store_group,identity_pack,always_present)
            VALUES(97,'store',true,'Synthetic identity pack',10000,12,100,0,0,0,'gold','{"balls": 2, "badges": 3, "kits": 4, "stadiums": 3}',true);
            """);
        // The points-only pack needs FIFA Points purchases.
        rt.Game.Rules.Club.FifaPointsPurchases = true;
        rt.Game.Store().AddCredits(100_000_000);
        rt.Game.Store().AddFifaPoints(1_000_000);
        return rt;
    }

    private static List<JsonObject> Buy(TestRuntime rt, long packId, string currency = "COINS")
    {
        // The player deals with Unassigned first: the store refuses a pack while it holds anything (Python server 328a5b1).
        rt.Game.Store().DeletePile("unassigned");
        var opened = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = packId, ["currency"] = currency });
        Assert.Equal("PURCHASECOMPLETE", opened["state"]!.GetValue<string>());
        return rt.Game.Store().ListItems("unassigned");
    }

    private static long Rareflag(TestRuntime rt, JsonObject item) => rt.Game.Players.Find(item.Int("resourceId"))!.Rareflag;

    [Fact]
    public void TheStoreListsOnlyAllowedCurrenciesAndNeverAnAdminPack()
    {
        using var rt = Create();
        var tiles = rt.CallObject("GET", $"{G}/store/purchasegroup/all")["purchase"].Objects().ToDictionary(tile => tile["id"].L());

        Assert.DoesNotContain(93L, tiles.Keys);
        Assert.Equal(new[] { "points" }, tiles[1000]["currencies"].Objects().Select(currency => currency["name"]!.GetValue<string>()));
        Assert.Equal(250, tiles[1000]["currencies"].Objects().Single()["funds"].L());
        Assert.Equal(new[] { "coins" }, tiles[91]["currencies"].Objects().Select(currency => currency["name"]!.GetValue<string>()));
        Assert.Equal(new[] { "coins" }, tiles[92]["currencies"].Objects().Select(currency => currency["name"]!.GetValue<string>()));
        Assert.Equal(0, tiles[92]["pointsPrice"].L());
        Assert.Equal(80000, tiles[1001]["pointsPrice"].L());

        // A currency the pack is not sold for, and an admin-only pack, are refused without charge.
        var (credits, points) = (rt.Game.Store().Credits(), rt.Game.Store().FifaPoints());
        Assert.Equal(400, rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 1000, ["currency"] = "COINS" }).Status);
        Assert.Equal(400, rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 91, ["currency"] = "POINTS" }).Status);
        Assert.Equal(400, rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 93, ["currency"] = "COINS" }).Status);
        Assert.Equal((credits, points), (rt.Game.Store().Credits(), rt.Game.Store().FifaPoints()));

        // The points-only pack pays in points.
        Assert.Equal(12, Buy(rt, 1000, "POINTS").Count);
        Assert.Equal(points - 250, rt.Game.Store().FifaPoints());
    }

    [Fact]
    public async Task RedPicksAreGeneratedCopiesOnlyTheFutChampsPickOffers()
    {
        using var rt = Create();
        var reds = rt.Game.Players.Entries.Where(entry => entry.Value.Rareflag == 18).ToList();

        // One red copy per player of their best Team of the Week (revision 20) and Team of the Season (revision 21) card, and
        // (migration 068) of the 18 TOTW 1-3 cards whose player's best Team of the Week card is another one (revision 22).
        Assert.Equal(833 + 18, reds.Count);
        const long Revision = 16_777_216;
        Assert.All(reds, red => Assert.Contains(red.Key / Revision, new long[] { 20, 21, 22 }));
        Assert.Equal(18, reds.Count(red => red.Key / Revision == 22));
        Assert.All(reds, red => Assert.Equal(red.Value.Int("assetId"), red.Key % Revision));
        Assert.All(reds, red => Assert.True(red.Value.Bool("untradeable")));

        // Only Champions reward packs issue red picks.
        var token = OpenOwned(rt, 550).Single();
        var choices = (await rt.Fut.Picks.RedeemAsync(token.Int("id"))).OfType<JsonObject>().ToList();
        Assert.Equal(5, choices.Count);
        Assert.All(choices, choice => Assert.Equal(18, Rareflag(rt, choice)));

        // Ordinary packs never draw one: the store's Gold and Premium Gold packs (migration 095).
        foreach (var packId in new[] { 74, 75 })
        {
            foreach (var item in Buy(rt, packId))
            {
                Assert.NotEqual(18, item.Text("itemType") == "player" ? Rareflag(rt, item) : 0);
            }
        }
    }

    [Fact]
    public void TheIdentityPackHoldsCosmeticsTheClubDoesNotOwn()
    {
        using var rt = Create();
        var defaults = rt.Game.Rules.Club.RenderedDefaultActiveItems(1).Select(item => item.Int("resourceId")).ToHashSet();

        var first = Buy(rt, 97);

        // 2 balls, 3 badges, 4 kits and 3 stadium cards, each once, none of them the club's default cosmetics.
        Assert.Equal((2, 3, 4, 3), (first.Count(item => item.Text("itemType") == "ball"), first.Count(item => item.Text("itemType") == "custom"),
            first.Count(item => item.Text("itemType") == "kit"), first.Count(item => item.Text("itemType") == "stadium")));
        Assert.Equal(12, first.Select(item => item.Int("resourceId")).Distinct().Count());
        Assert.DoesNotContain(first, item => defaults.Contains(item.Int("resourceId")));
        // Stored and read back with their card shape: native ids, localisation-key names, the venue as a stadium's asset.
        Assert.All(first.Where(item => item.Text("itemType") == "stadium"), item =>
        {
            Assert.Equal(6_200_000 + item.Int("assetId"), item.Int("resourceId"));
            Assert.Equal($"StadiumName_{item.Int("assetId")}", item.Text("name"));
            Assert.Equal(83, item.Int("rating"));
        });
        Assert.All(first.Where(item => item.Text("itemType") == "ball"), item => Assert.Equal(8_120_000 + item.Int("assetId"), item.Int("resourceId")));
        Assert.All(first.Where(item => item.Text("itemType") == "custom"), item => Assert.Equal($"TeamName_Abbr15_{item.Int("teamid")}", item.Text("name")));

        // Moved to the club, a second pack draws cosmetics it does not own yet.
        rt.Call("PUT", "/ut/game/fifa21/item", new JsonObject { ["itemData"] = new JsonArray(first.Select(item => (JsonNode?)new JsonObject
        {
            ["id"] = item.Int("id"), ["pile"] = "club",
        }).ToArray()) });
        var second = Buy(rt, 97);
        Assert.Empty(second.Select(item => item.Int("resourceId")).Intersect(first.Select(item => item.Int("resourceId"))));
    }

    private static List<JsonObject> OpenOwned(TestRuntime rt, long packId)
    {
        rt.Game.Store().DeletePile("unassigned");
        rt.Game.InUnit(uow => uow.AddUnopenedPackAsync(packId, "test"));
        Assert.Equal(200, rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = packId }).Status);
        return rt.Game.Store().ListItems("unassigned");
    }

    [Fact]
    public void TeamOfTheWeekRewardsDrawFromTheLiveTeamOfTheWeek()
    {
        // Packs 540/541 hold fixed TOTW 1–3 cards; 550–553 offer their FUT Champions red copies.
        // Both packs hold 12 cards: 11 or 3 TOTW cards, with Gold Rare cards filling the other slots.
        using var rt = Create();
        var totw = rt.Fut.Pools.Named("totw_1_3")!;
        Assert.True(totw.Count >= 11, $"the fixed TOTW 1–3 pool holds {totw.Count} catalogue cards");
        var ultimate = OpenOwned(rt, 541);
        Assert.Equal(12, ultimate.Count);
        Assert.Equal(11, ultimate.Count(item => totw.Contains(item.Int("resourceId"))));
        var premium = OpenOwned(rt, 540);
        Assert.Equal(12, premium.Count);
        Assert.Equal(3, premium.Count(item => totw.Contains(item.Int("resourceId"))));

        foreach (var (packId, choices, maxRating) in new[] { (550L, 5, 99L), (551L, 2, 84L), (552L, 4, 99L), (553L, 3, 99L) })
        {
            var token = Assert.Single(OpenOwned(rt, packId));
            var offered = rt.CallObject("POST", $"{G}/item/nontargeted?itemId={token.Int("id")}")["choiceData"]!.AsArray().OfType<JsonObject>().ToList();
            Assert.Equal(choices, offered.Count);
            Assert.All(offered, choice => Assert.True(choice.Int("rating") <= maxRating, $"pack {packId}"));
            // A red pool with enough cards in range is the only source; one too small is ignored.
            if (rt.Fut.Pools.ForPick("totw_1_3_red", 0, maxRating, choices, out var ignored) is { } red)
            {
                Assert.All(offered, choice => Assert.Contains(choice.Int("resourceId"), red));
            }
            else
            {
                Assert.True(ignored);
            }
            rt.Call("POST", $"{G}/playerpicks/item/{offered[0].Int("resourceId")}/select");
        }
        // An unknown pool is empty, no pool is none.
        Assert.Empty(rt.Fut.Pools.Named("no_such_pool")!);
        Assert.Null(rt.Fut.Pools.Named(null));
    }
}
