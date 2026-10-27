using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>Owned stadium cards on Match Day and the home venue (Python server ca228bc, e2e098f).</summary>
public sealed class MatchDayStadiumTests
{
    private const string G = "/ut/game/fifa21";

    private static JsonObject Slot(JsonObject stadium, long slotId) =>
        stadium["tiers"]![0]!["slots"]!.AsArray().OfType<JsonObject>().Single(slot => slot.Int("slotId") == slotId);

    private static JsonObject Item(JsonObject stadium, long slotId) => Slot(stadium, slotId)["itemData"]!.AsObject();

    private static JsonObject Save(TestRuntime rt, params (long Slot, long Item)[] bindings) =>
        rt.CallObject("PUT", $"{G}/stadium", new JsonObject
        {
            ["tiers"] = new JsonArray(new JsonObject
            {
                ["tier"] = 1,
                ["slots"] = new JsonArray(bindings.Select(b => (JsonNode?)new JsonObject { ["slotId"] = b.Slot, ["itemId"] = b.Item }).ToArray()),
            }),
        });

    [Fact]
    public void AnEquippedStadiumCardIsTheHomeVenueAndTheDefaultStaysPickable()
    {
        // Pack 97 copies the retired identity pack (migration 053 pack 66; migration 097 removed it from the store).
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: """
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,
                rare_slots,store_group,identity_pack,always_present)
            VALUES(97,'store',true,'Synthetic identity pack',10000,12,100,0,0,0,'gold','{"balls": 2, "badges": 3, "kits": 4, "stadiums": 3}',true)
            """);
        rt.Game.Store().AddCredits(1_000_000);
        var venues = rt.Game.Rules.Club.Venues;
        var stadium = rt.CallObject("GET", $"{G}/stadium");
        var defaultStadium = Item(stadium, 5);
        Assert.Equal(venues.DefaultVenueId, defaultStadium.Int("assetId"));
        // Names are FIFA localisation keys, which it looks up (plain text showed with a leading asterisk).
        Assert.Equal($"StadiumName_{venues.DefaultVenueId}", defaultStadium.Text("name"));
        Assert.All(new[] { 3L, 4L, 2L }, slot => Assert.Equal($"TeamName_Abbr15_{Item(stadium, slot).Int("teamid")}", Item(stadium, slot).Text("name")));
        Assert.Equal(venues.DefaultVenueId, rt.Game.InUnit(uow => rt.Fut.Club.HomeVenueAsync(uow)));

        // The identity pack holds three stadium cards and three badges; move them to the club.
        rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 97, ["currency"] = "COINS" });
        var opened = rt.Game.Store().ListItems("unassigned");
        rt.Call("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(opened.Select(item => (JsonNode?)new JsonObject
        {
            ["id"] = item.Int("id"), ["pile"] = "club",
        }).ToArray()) });
        var card = opened.First(item => item.Text("itemType") == "stadium");
        var badge = opened.First(item => item.Text("itemType") == "custom");
        Assert.True(venues.IsMatchVenue(card.Int("assetId")));

        var saved = Save(rt, (5, card.Int("id")), (2, badge.Int("id")));

        // Slot 5 holds the owned card, whose assetId FIFA loads as the venue.
        Assert.Equal((card.Int("id"), card.Int("assetId")), (Item(saved, 5).Int("id"), Item(saved, 5).Int("assetId")));
        Assert.Equal(card.Int("assetId"), saved.Int("stadiumId"));
        Assert.Equal(badge.Int("id"), Item(saved, 2).Int("id"));
        Assert.Equal(card.Int("assetId"), rt.Game.InUnit(uow => rt.Fut.Club.HomeVenueAsync(uow)));

        // The stadium picker lists the club's own default card, free, next to the other owned cards.
        var picker = rt.CallObject("GET", $"{G}/club?type=equippables&excldef={card.Int("resourceId")}&count=90")["itemData"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.All(picker, item => Assert.Equal("stadium", item.Text("itemType")));
        Assert.Contains(picker, item => item.Int("id") == defaultStadium.Int("id") && item.Text("itemState") == "free");
        Assert.DoesNotContain(picker, item => item.Int("id") == card.Int("id"));
        Assert.Equal(3, picker.Count); // the default card and the other two owned cards

        // PUT /item with the club's own badge (a virtual item) puts it back on Match Day.
        var defaultBadge = rt.Game.Rules.Club.RenderedDefaultActiveItems(1).Single(item => item.Text("itemState") == "activeBadge").Int("id");
        var item = rt.CallObject("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = defaultBadge, ["itemState"] = "activeBadge" }) });
        Assert.True(item["itemData"]![0]!["success"]!.GetValue<bool>());
        Assert.Equal(defaultBadge, Item(rt.CallObject("GET", $"{G}/stadium"), 2).Int("id"));

        // Going back to the default card frees the owned one and plays in the FUT Stadium again.
        var restored = Save(rt, (5, defaultStadium.Int("id")));
        Assert.Equal(defaultStadium.Int("id"), Item(restored, 5).Int("id"));
        Assert.Equal(venues.DefaultVenueId, rt.Game.InUnit(uow => rt.Fut.Club.HomeVenueAsync(uow)));
        Assert.Equal("free", rt.Game.Store().ListItems().Single(item => item.Int("id") == card.Int("id")).Text("itemState"));
    }

    private static List<JsonObject> Owned(TestRuntime rt, params long[] subtypes) =>
        rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "equippables", ["count"] = 500, ["vanitySubTypeIds"] = new JsonArray(subtypes.Select(s => (JsonNode?)s).ToArray()) })
            ["itemData"]!.AsArray().OfType<JsonObject>().ToList();

    /// <summary>The New Items pile (GET /purchased/items): where FIFA lands a pack's reveal and an objective claim's items.</summary>
    private static List<JsonObject> Pile(TestRuntime rt) =>
        rt.CallObject("GET", $"{G}/purchased/items")["itemData"]!.AsArray().OfType<JsonObject>().ToList();

    /// <summary>The pile screen's confirm, in the shape the client sends: keep these items (swap 0, tradeId 0).</summary>
    private static void Keep(TestRuntime rt, IEnumerable<JsonObject> items) =>
        rt.Call("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(items.Select(item => (JsonNode?)new JsonObject
        {
            ["id"] = item.Int("id"), ["pile"] = "club", ["swap"] = 0, ["tradeId"] = 0,
        }).ToArray()) });

    private static void Claim(TestRuntime rt, string path) => rt.Call("POST", $"{G}/scmp/campaign/{path}");

    private static void PlayMatches(TestRuntime rt, int count)
    {
        for (var match = 0; match < count; match++)
        {
            rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        }
    }

    /// <summary>
    /// My Stadium as retail FIFA 21 had it: FIFA's own slot ids and labels in four sections, a new club with only the basic
    /// setup open and two starter items, and the Stadium Development milestones opening slots and paying item packs.
    /// </summary>
    [Fact]
    public void ANewClubStartsWithTheBasicsAndStadiumDevelopmentGrowsIt()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var rules = rt.Game.Rules.Club;
        var stadium = rt.CallObject("GET", $"{G}/stadium");
        var slots = stadium["tiers"]![0]!["slots"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(36, slots.Count);
        Assert.Equal(("FUT_STADIUM_TIFO_EAST", 1L, false), (Slot(stadium, 6).Text("slotName"), Slot(stadium, 6).Int("stadiumArea"), Slot(stadium, 6).Bool("locked")));
        Assert.Equal(("FUT_STADIUM_PROP_1", 2L, true), (Slot(stadium, 27).Text("slotName"), Slot(stadium, 27).Int("stadiumArea"), Slot(stadium, 27).Bool("locked")));
        Assert.Equal(("FUT_STADIUM_CLUB_ANTHEM", true), (Slot(stadium, 39).Text("slotName"), Slot(stadium, 39).Bool("locked")));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 23, 26 }, slots.Where(slot => !slot.Bool("locked")).Select(slot => slot.Int("slotId")).Order());

        // Only the two starter items are owned; nothing is equipped.
        var all = Owned(rt, rules.Vanity.Catalogue.Keys.ToArray());
        Assert.Equal(rules.Vanity.StarterItems.Select(item => (item.Subtype, item.Asset)).Order(), all.Select(item => (item.Int("cardsubtypeid"), item.Int("assetId"))).Order());
        Assert.All(slots.Where(slot => slot.Int("slotId") > 5), slot => Assert.Null(slot["itemData"]));
        var tifo = all.Single(item => item.Int("cardsubtypeid") == 500).Int("id");
        Assert.Equal(tifo, Item(Save(rt, (6, tifo)), 6).Int("id"));

        // A locked slot refuses even an owned item of its type: the anthem slot is closed and the club owns no anthem.
        var anthem = rules.Vanity.ItemIdOf(516, rules.Vanity.Catalogue[516][0])!.Value;
        Assert.Null(Slot(Save(rt, (39, anthem)), 39)["itemData"]);

        // One match: Club Anthem opens slot 39 and pays three anthems, which wait in the New Items pile until they are kept.
        PlayMatches(rt, 1);
        Claim(rt, "group/20/objective/2001/rewards");
        Assert.False(Slot(rt.CallObject("GET", $"{G}/stadium"), 39).Bool("locked"));
        var anthems = Pile(rt);
        Assert.Equal(3, anthems.Count);
        Keep(rt, anthems);
        var kept = Owned(rt, 516);
        Assert.Equal(3, kept.Count);
        rt.Call("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = kept[0].Int("id"), ["itemState"] = "vanitySlot39" }) });
        Assert.Equal(kept[0].Int("id"), Item(rt.CallObject("GET", $"{G}/stadium"), 39).Int("id"));

        // The match squad's My Stadium document (the Rivals pre-match fetch) shows the opened slot open with its item: sent
        // locked, the game played no goal song or anthem from an earned slot.
        var matchStadium = rt.CallObject("GET", $"{G}/squad/active/user/{rt.Game.Config.PersonaId}?includeMyStadium=1")["myStadiumInfo"]!.AsObject();
        Assert.False(Slot(matchStadium, 39).Bool("locked"));
        Assert.Equal(kept[0].Int("id"), Item(matchStadium, 39).Int("id"));
        Assert.True(Slot(matchStadium, 38).Bool("locked"));

        // Ten matches: Main Stand Lower TIFO opens slot 35. One card fills one slot, so moving the tifo empties slot 6.
        PlayMatches(rt, 9);
        Claim(rt, "group/20/objective/2003/rewards");
        var moved = Save(rt, (35, tifo));
        Assert.Equal(tifo, Item(moved, 35).Int("id"));
        Assert.Null(Slot(moved, 6)["itemData"]);

        // The Challengers Stadium is closed until Stadium Development is complete, then the club moves into it.
        var challengers = rules.Venues.FutStadiums[1];
        rt.Call("PUT", $"{G}/stadium", new JsonObject { ["selectedStadiumId"] = challengers });
        Assert.NotEqual(challengers, rt.Game.InUnit(uow => rt.Fut.Club.HomeVenueAsync(uow)));
        PlayMatches(rt, 40);
        foreach (var objective in rt.Game.Rules.Objectives.GrindGroups.Single(group => group.Id == 20).Objectives)
        {
            Claim(rt, $"group/20/objective/{objective.Id}/rewards");
        }
        Claim(rt, "group/20/rewards");
        Assert.Equal(challengers, rt.Game.InUnit(uow => rt.Fut.Club.HomeVenueAsync(uow)));
        Assert.All(new long[] { 39, 24, 35, 9, 38, 25, 27 }, id => Assert.False(Slot(rt.CallObject("GET", $"{G}/stadium"), id).Bool("locked")));
    }

    /// <summary>
    /// The two Stadium Development groups each show their own tier's stadium card (they both showed the shared default card),
    /// the Challenger tier a rarity below the Champions tier; and no My Stadium item is the old flat common bronze.
    /// </summary>
    [Fact]
    public void StadiumTierTilesDifferAndItemsCarryTheirRarity()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var groups = rt.CallObject("GET", $"{G}/scmp/campaign/active")["categories"]![0]!["groups"]!.AsArray().OfType<JsonObject>().ToList();
        JsonObject Tile(long id) => groups.Single(group => group.Int("id") == id)["awardsList"]![0]!["itemData"]!.AsObject();
        var (challenger, champions) = (Tile(20), Tile(21));
        Assert.Equal((6200444L, 444L, "StadiumName_444"), (challenger.Int("resourceId"), challenger.Int("assetId"), challenger.Text("name")));
        Assert.Equal((6200445L, 445L, "StadiumName_445"), (champions.Int("resourceId"), champions.Int("assetId"), champions.Text("name")));
        Assert.True(challenger.Int("rating") < champions.Int("rating"));

        var vanity = rt.Game.Rules.Club.Vanity;
        Assert.All(vanity.Items(), item => Assert.True(vanity.RarityOf(item.Subtype, item.Asset).Rating >= 75, $"{item.Subtype}/{item.Asset} is not gold"));
        var nickname = rt.Fut.Vanity.Item(vanity.ItemIdOf(518, vanity.Catalogue[518][0])!.Value, new Dictionary<long, long>());
        var paint = rt.Fut.Vanity.Item(vanity.ItemIdOf(509, vanity.Catalogue[509][0])!.Value, new Dictionary<long, long>());
        Assert.Equal((80L, 0L), (nickname.Int("rating"), nickname.Int("rareflag")));
        Assert.Equal((80L, 1L), (paint.Int("rating"), paint.Int("rareflag")));
    }

    /// <summary>Every locked slot is opened by exactly one Stadium Development reward, and every reward pack has items to give.</summary>
    [Fact]
    public void StadiumDevelopmentOpensEveryLockedSlot()
    {
        var rules = Aurora21.UltimateTeam.Core.Rules.GameRules.Load(TestRuntime.DataRoot);
        var locked = rules.Club.MyStadiumSlots.Where(slot => slot.Locked).Select(rules.Club.SlotIdOf).Order().ToList();
        var rewards = rules.Objectives.GrindGroups.Where(group => group.Id is 20 or 21 or 22)
            .SelectMany(group => group.Objectives.Select(objective => objective.Award).Append(group.Award)).ToList();
        Assert.Equal(locked, rewards.SelectMany(reward => reward.UnlockSlots).Order());
        Assert.All(rewards.Where(reward => reward.Vanity is not null), reward =>
            Assert.True(reward.Vanity!.Subtypes.Sum(subtype => rules.Club.Vanity.Catalogue.GetValueOrDefault(subtype)?.Count ?? 0) >= reward.Vanity.Count, reward.Vanity.Name));
    }

    /// <summary>
    /// The store's My Stadium Pack (pack 83, migration 123): six unowned items, one per slot (line paint, mow pattern,
    /// nickname, goal song, two tifos), never a reward-only item; rewards draw only from the reward-only lists.
    /// </summary>
    [Fact]
    public void TheMyStadiumPackSellsWhatRewardsDoNot()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var vanity = rt.Game.Rules.Club.Vanity;
        var seen = new HashSet<long>();
        for (var open = 0; open < 3; open++)
        {
            var items = rt.Fut.Packs.CreatePack(83).Arr("itemData")!.OfType<JsonObject>().ToList();
            Assert.Equal(new long[] { 500, 500, 505, 506, 517, 518 }, items.Select(item => item.Int("cardsubtypeid")).Order());
            Assert.All(items, item => Assert.False(vanity.IsRewardOnly(item.Int("cardsubtypeid"), item.Int("assetId"))));
            Assert.All(items, item => Assert.True(seen.Add(item.Int("id")), "a pack never repeats an owned item"));
        }
        // The draws wait in the New Items pile (never twice in a pack, never re-drawn while waiting); keeping them moves
        // them to My Stadium.
        Assert.Equal(18, Pile(rt).Count);
        Keep(rt, Pile(rt));
        Assert.Subset(Owned(rt, 500, 505, 506, 517, 518).Select(item => item.Int("id")).ToHashSet(), seen);

        // Club Anthem (1 match) pays anthems from the whole catalogue; the Tifos Pack (10 matches) only reward-only tifos.
        PlayMatches(rt, 10);
        Claim(rt, "group/20/objective/2003/rewards");
        Keep(rt, Pile(rt));
        var rewardTifos = Owned(rt, 500).Where(item => !seen.Contains(item.Int("id")) && !vanity.StarterItems.Any(s => s.Subtype == 500 && s.Asset == item.Int("assetId"))).ToList();
        Assert.Equal(3, rewardTifos.Count);
        Assert.All(rewardTifos, item => Assert.True(vanity.IsRewardOnly(500, item.Int("assetId"))));
    }

    /// <summary>
    /// A My Stadium pack's items are handled like any pile's (live 10-03: the pile screen's keep-all confirm failed with
    /// a server error — the equip path refused the locked-category items and a failed move tears the client's flow down —
    /// and the pile FIFA re-listed never held them): they wait in the New Items pile, worth nothing and never drawn
    /// twice; keeping them moves them to My Stadium; a locked slot still refuses its item (Mow Patterns opens with
    /// Stadium Development II, 60 matches) while TIFO EAST, open from the start, takes a kept tifo at once.
    /// </summary>
    [Fact]
    public void PackedStadiumItemsWaitInThePileAndEquipWhenTheirSlotOpens()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var packed = rt.Fut.Packs.CreatePack(83).Arr("itemData")!.OfType<JsonObject>().ToList();
        Assert.Equal(6, packed.Count);
        var packedIds = packed.Select(item => item.Int("id")).ToHashSet();

        // The six wait in the New Items pile — that is the screen FIFA opens after the reveal. (A pile card carries no
        // pile field: the endpoint itself is the pile.)
        var pile = Pile(rt);
        Assert.Equal(packedIds, pile.Select(item => item.Int("id")).ToHashSet());

        // While they wait they are not in My Stadium yet, the store refuses another pack until they are dealt with, and
        // a new draw still cannot repeat one of them.
        Assert.DoesNotContain(Owned(rt, 505, 506, 517, 518), item => packedIds.Contains(item.Int("id")));
        Assert.Equal(471, rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 83, ["currency"] = "COINS" }).Status);
        var again = rt.Fut.Packs.CreatePack(83).Arr("itemData")!.OfType<JsonObject>().ToList();
        Assert.All(again, item => Assert.DoesNotContain(item.Int("id"), packedIds));

        // A waiting item quick sells for nothing: one of the second draw's tifos goes while it waits, the rest are kept.
        var discarded = again.First(item => item.Int("cardsubtypeid") == 500).Int("id");
        var sold = rt.CallObject("POST", "/ut/delete/game/fifa21/item", new JsonObject { ["itemId"] = discarded });
        Assert.Contains(discarded, sold["items"]!.AsArray().OfType<JsonObject>().Select(item => item.Int("id")));
        Assert.DoesNotContain(discarded, Pile(rt).Select(item => item.Int("id")));

        // The pile screen's confirm (the client's exact shape) keeps them; the pile empties and My Stadium holds all of them.
        Keep(rt, Pile(rt));
        Assert.Empty(Pile(rt));
        Assert.Subset(Owned(rt, 500, 505, 506, 517, 518).Select(item => item.Int("id")).ToHashSet(), packedIds);
        Assert.DoesNotContain(discarded, Owned(rt, 500, 505, 506, 517, 518).Select(item => item.Int("id")));

        var mow = packed.Single(item => item.Int("cardsubtypeid") == 505);
        Assert.True(Slot(rt.CallObject("GET", $"{G}/stadium"), 10).Bool("locked"));
        Assert.Null(Slot(Save(rt, (10, mow.Int("id"))), 10)["itemData"]);

        // TIFO EAST is open from the start: a kept tifo equips at once.
        var tifo = packed.First(item => item.Int("cardsubtypeid") == 500);
        rt.Call("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = tifo.Int("id"), ["itemState"] = "vanitySlot6" }) });
        Assert.Equal(tifo.Int("id"), Item(rt.CallObject("GET", $"{G}/stadium"), 6).Int("id"));

        // Stadium Development II (60 matches) opens Mow Patterns and pays three more; the kept mow equips into it.
        PlayMatches(rt, 60);
        Claim(rt, "group/21/objective/2102/rewards");
        Keep(rt, Pile(rt));
        Assert.False(Slot(rt.CallObject("GET", $"{G}/stadium"), 10).Bool("locked"));
        Assert.Equal(5, Owned(rt, 505).Count); // one from each kept pack and the three the claim paid
        Assert.Equal(mow.Int("id"), Item(Save(rt, (10, mow.Int("id"))), 10).Int("id"));
    }

    /// <summary>Every reward pack has enough reward-only items, and the catalogue holds every goal song FIFA 21 ships.</summary>
    [Fact]
    public void RewardPoolsCoverTheRewardsAndEveryGoalSongIsCatalogued()
    {
        var rules = Aurora21.UltimateTeam.Core.Rules.GameRules.Load(TestRuntime.DataRoot);
        var vanity = rules.Club.Vanity;
        var rewards = rules.Objectives.GrindGroups.SelectMany(group => group.Objectives.Select(objective => objective.Award).Append(group.Award))
            .Where(reward => reward.Vanity is not null).ToList();
        foreach (var (subtype, assets) in vanity.RewardOnly)
        {
            var needed = rewards.Where(reward => reward.Vanity!.Subtypes.Contains(subtype)).Sum(reward => reward.Vanity!.Count);
            Assert.True(assets.Count >= needed, $"rewardOnly {subtype}: {assets.Count} for {needed}");
            Assert.All(assets, asset => Assert.Contains(asset, vanity.Catalogue[subtype]));
        }
        // cards_ng_db CustomizableGoalMusic ships 48 rows (item ids 501-565 with gaps); every one is a goal song item.
        Assert.Equal(48, vanity.Catalogue[517].Count);
        Assert.Equal(13, vanity.UnassignedTifos["animated"].Count);
    }
}
