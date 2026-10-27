using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Port of runtime-source/ultimateteam21/test_offline_api.py: direct in-process API checks against
/// an isolated runtime. The scenario is sequential, like the original script.
/// </summary>
public sealed class OfflineApiTests
{
    private const string G = "/ut/game/fifa21";

    // The active default stadium card is the club's home venue's card, 6200000 + venue (migration 053; 292b6a58). Its
    // assetId is the venue FIFA loads from slot 5: the tier 1 FUT Stadium until the club selects another (Sanderson Park,
    // 261, rendered a bare pitch).
    private const long StadiumCardBase = 6200000;
    private const long FutStadiumVenueId = 443;

    // Match venue for Squad Battles and Draft: physical venue 2 on the Santiago Bernabeu card. The card's assetId is the
    // venue FIFA loads; the old asset 1 loaded a bare pitch.
    private const long VenueStadiumId = 2;
    private const long VenueCardResourceId = 6200001;
    private const long VenueCardAssetId = VenueStadiumId;

    [Fact]
    public void CatalogAndDraftRulesHaveReviewedDefaults()
    {
        using var rt = TestRuntime.Create();
        var players = rt.Game.Players;
        var managers = rt.Game.Managers;

        Assert.True(players.Count >= 22500);
        Assert.Equal(311, managers.Count);
        Assert.True(managers.Entries.Min(e => e.Key) >= 1_000_000);
        Assert.Equal("Antonio Conte", managers.Find(1001067)!.Text("name"));

        var summerStarsMessi = players.Find(184707399)!;
        Assert.Equal(158023, summerStarsMessi.Int("assetId"));
        Assert.Equal(99, summerStarsMessi.Rating);
        Assert.Equal(61, summerStarsMessi.Rareflag);
        Assert.Equal("Summer Stars", summerStarsMessi.Text("rarityName"));
        // The relational catalog retains the resource ID but no longer carries scraped source-card provenance.

        // Zero-revision custom avatar 246525 collides with a base player; Icon Moments keep it in the low 24 bits.
        var momentsPeleId = PlayerCatalog.ResourceRevisionSize + 246525;
        Assert.False(players.Contains(246525));
        var moments = players.Find(momentsPeleId)!;
        Assert.Equal("Pelé", moments.Text("name"));
        Assert.Equal(54, moments.Int("nation"));
        Assert.Equal("CF", moments.Text("preferredPosition"));
        Assert.Equal(84, moments.Rareflag);
        Assert.Equal(246525, moments.Int("dynamicImageResourceId"));
        Assert.Equal(momentsPeleId, players.ResourceIdMigrations[246525]);
        Assert.All(players.Entries.Where(e => e.Value.Rareflag == 84), e => Assert.True(e.Key >= PlayerCatalog.ResourceRevisionSize));

        var pools = players.SpecialPoolCounts();
        Assert.True(pools.Count >= 59);
        Assert.Equal(807, pools[3]);
        Assert.Equal(36, pools[61]);

        var ratingExamples = new Dictionary<long, long>();
        foreach (var (id, meta) in players.Entries)
        {
            ratingExamples.TryAdd(meta.Rating, id);
        }
        // The elite-rating boost is off since migration 068 (phase-1 odds draw by rating band): every rating weighs 1.
        Assert.All(new long[] { 89, 90, 92, 99 }, rating => Assert.Equal(1.0, rt.Game.Cards.PackWeight(ratingExamples[rating])));

        // Phase 1 turns the guaranteed-99 rounds off (rules/draft.json elite99Chance 0): no run has one.
        Assert.Equal(0, rt.Game.Rules.Draft.Picks.Elite99Chance);
        Assert.All(Enumerable.Range(1, 1000), seed => Assert.Empty(rt.Fut.Draft.Elite99PickOrdinals(new JsonObject { ["seed"] = seed })));
        // With the old chance, about 45% of Draft runs have four or five guaranteed 99 rounds, stable per seed.
        rt.Game.Rules.Draft.Picks.Elite99Chance = 0.45;
        var elite = 0;
        for (var seed = 1; seed <= 10_000; seed++)
        {
            var ordinals = rt.Fut.Draft.Elite99PickOrdinals(new JsonObject { ["seed"] = seed });
            Assert.Contains(ordinals.Count, new[] { 0, 4, 5 });
            Assert.Equal(ordinals.Count, ordinals.Distinct().Count());
            Assert.All(ordinals, o => Assert.InRange(o, 0, 22));
            elite += ordinals.Count > 0 ? 1 : 0;
        }
        Assert.InRange(elite / 10_000.0, 0.43, 0.47);
        for (var seed = 1; seed <= 100; seed++)
        {
            var profile = new JsonObject { ["seed"] = seed, ["formation"] = "f442" };
            var bad = rt.Fut.Draft.BadPickOrdinals(profile);
            Assert.Contains(bad.Count, new[] { 2, 3 });
            Assert.Empty(bad.Intersect(rt.Fut.Draft.Elite99PickOrdinals(profile)));
        }
        rt.Game.Rules.Draft.Picks.Elite99Chance = 0;


    }

    [Fact]
    public void FullOfflineScenario()
    {
        using var rt = CreatePackScenarioRuntime();
        var store = rt.Game.Store();

        Assert.Equal("http", rt.Call("GET", "/ut/auth")["protocol"].S());

        var account = rt.CallObject("GET", $"{G}/user/accountinfo");
        var club = account["userAccountInfo"]!["personas"]![0]!["userClubList"]![0]!;
        Assert.Equal(rt.Game.Config.ClubName, club["clubName"].S());
        Assert.Equal(rt.Game.Config.ClubId, club["assetId"].L());

        var settings = rt.CallObject("GET", $"{G}/settings")["configs"].Objects().ToDictionary(e => e["type"].S(), e => e["value"].S());
        foreach (var flag in new[] { "enableMyStadium", "myStadium", "enableDraftMode", "enableOfflineDraftMode", "enableSinglePlayerDraftMode" })
        {
            Assert.Equal("1", settings[flag]);
        }
        // The Transfer List's capacity: FIFA reads it from here, not from /clientdata/pilesize.
        Assert.Equal("100", settings["transferListSize"]);

        var draftCoins = DraftScenario(rt);
        StadiumScenario(rt);
        var (squad, user) = UserAndSquadScenario(rt);
        OnboardingScenario(rt, squad, user);
        var clubPage = ClubScenario(rt, draftCoins);
        SquadBattlesScenario(rt);
        StoreAndSbcScenario(rt);
        PacksAndCustomisationScenario(rt);

        var (rejectedStatus, rejected, _) = rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["useCredits"] = 1 });
        Assert.Equal(400, rejectedStatus);
        Assert.Equal("400", rejected["code"].S());
        _ = clubPage;
        _ = store;
    }

    private static TestRuntime CreatePackScenarioRuntime() => TestRuntime.Create(seedSql: """
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,manager_chance)
            VALUES(90,'store',true,'Synthetic manager pack',7500,12,100,0,0,3,1);
            -- Every rare slot draws a Summer Stars card: the odds table gives the two Summer Stars promos the whole slot.
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,manager_chance,
                promo_codes,category_chances)
            VALUES(91,'store',true,'Synthetic Summer Stars pack',7500,12,100,0,0,3,0,ARRAY['summer_stars_team_1','summer_stars_team_2'],
                '{"summer_stars_team_1": 0.5, "summer_stars_team_2": 0.5}');
            -- Copies of packs migration 097 retired from the store (migration 041 packs 4, 8 and 9).
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,
                manager_chance,min_rating,store_group,guaranteed_specials,all_consumables,all_kits,always_present)
            VALUES
                (92,'store',true,'Synthetic Elite Ten 82+ pack',50000,10,100,0,0,10,0,82,'special',1,NULL,NULL,true),
                (93,'store',true,'Synthetic all consumables pack',1,20,100,0,0,3,0,NULL,'special',NULL,true,NULL,false),
                (96,'store',true,'Synthetic all kits pack',1,30,100,0,0,3,0,NULL,'special',NULL,NULL,true,false)
            """);

    /// <summary>Returns the run reward's coins: the level's 1,500 plus the drawn choice's.</summary>
    private static long DraftScenario(TestRuntime rt)
    {
        var store = rt.Game.Store();
        var draft = rt.Fut.Draft;

        var state = rt.Call("GET", $"{G}/squad/mode/draft/state?mode=SINGLE_PLAYER");
        Assert.True(state.JsonEquals(JsonNode.Parse("""
            [{"entranceCriteria":{"COINS":15000,"DRAFT_TOKEN":1,"POINTS":300},"gamesWonCurrentMatch":0,"roundsInfo":[],
              "squadState":"INVALID","stateParam1":"INVALID","stateParam2":"0"}]
            """)));

        var creditsBefore = store.Credits();
        var purchase = rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS", ["usePreOrder"] = 0 });
        Assert.True(purchase.JsonEquals(new JsonArray(new JsonObject { ["COINS"] = creditsBefore - 15000, ["POINTS"] = 0, ["DRAFT_TOKEN"] = 0 })));

        // Draft offers use standard cards and active schedules, with no guaranteed 99 rounds.
        var inPool = draft.PoolFilter();
        void AssertPhaseLegal(IEnumerable<JsonObject> offered) => Assert.All(offered, c =>
        {
            var item = c["itemData"]!.AsObject();
            Assert.True(item.Int("rating") < 99 && inPool(item.Int("resourceId"), item.Int("rareflag")), item.ToJsonString());
        });

        var difficulties = rt.Call("GET", $"{G}/squad/mode/draft/choices/difficulty")["choices"].Objects().Select(c => c["difficultyName"].S());
        Assert.Equal(new[] { "BEGINNER", "AMATEUR", "SEMIPRO", "PRO", "WORLDCLASS", "LEGENDARY", "ULTIMATE" }, difficulties);
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "PRO" });
        // Each round names the pick: FIFA plays the match at roundsInfo's difficulty, Ultimate when the list is empty.
        Assert.True(rt.Call("GET", $"{G}/squad/mode/draft/state?mode=SINGLE_PLAYER")[0]!["roundsInfo"].JsonEquals(JsonNode.Parse("""
            [{"round":1,"difficulty":"PRO"},{"round":2,"difficulty":"PRO"},{"round":3,"difficulty":"PRO"},{"round":4,"difficulty":"PRO"}]
            """)));
        Assert.Equal(5, rt.Call("GET", $"{G}/squad/mode/draft/choices/formation")["choices"].A().Count);
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });

        Assert.Empty(draft.Elite99PickOrdinals(store.DraftRun()!));
        var captainChoices = rt.Call("GET", $"{G}/squad/mode/draft/choices/captain")["choices"].Objects().ToList();
        Assert.Equal(5, captainChoices.Count);
        AssertPhaseLegal(captainChoices);
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 10 });

        for (var position = 0; position < 23; position++)
        {
            if (position == 10)
            {
                continue;
            }
            var active = store.DraftRun()!;
            var allowed = rt.Fut.Draft.AllowedPositions(active, position);
            var choices = rt.Call("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = position })["choices"].Objects().ToList();
            Assert.Equal(5, choices.Count);
            if (allowed is not null)
            {
                Assert.All(choices, c => Assert.Contains(c["itemData"]!["preferredPosition"].S().ToUpperInvariant(), allowed));
            }
            AssertPhaseLegal(choices);
            rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = position });
        }

        var managerChoices = rt.Call("GET", $"{G}/squad/mode/draft/choices/manager")["choices"].Objects().ToList();
        Assert.Equal(5, managerChoices.Count);
        Assert.All(managerChoices, c => Assert.Equal("manager", c["itemData"]!["itemType"].S()));
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });

        var ready = rt.Call("GET", $"{G}/squad/mode/draft/state?mode=SINGLE_PLAYER")[0]!;
        Assert.Equal("READY_FOR_MATCH", ready["squadState"].S());
        Assert.Equal(23, ready["squad"]!["players"].Objects().Count(s => s["itemData"].IsObject()));
        Assert.Single(ready["squad"]!["manager"].A());

        var draftSquad = rt.CallObject("GET", $"{G}/squad/900001");
        Assert.Equal("DRAFT_SQUAD", draftSquad["squadType"].S());
        // The user's Draft side keeps the club's own stadium (the FUT Stadium default): home Draft games are played there.
        Assert.Equal(FutStadiumVenueId, draftSquad["stadiumAssetId"].L());
        Assert.Equal(FutStadiumVenueId, draftSquad["stadiumId"].L());
        var clubStadiumId = rt.Fut.SquadBattles.MatchActives(rt.Game, opponent: false).First(i => i.Text("itemState") == "activeStadium").Int("id");
        foreach (var collection in new[] { "actives", "club" })
        {
            var stadium = draftSquad[collection].Objects().First(i => i["itemType"]?.GetValue<string>() == "stadium");
            Assert.Equal(6200000 + FutStadiumVenueId, stadium["resourceId"].L());
            Assert.Equal(FutStadiumVenueId, stadium["assetId"].L());
            Assert.Equal(clubStadiumId, stadium["id"].L());
        }

        var opponent = rt.CallObject("GET", $"{G}/offlinesquad/1");
        Assert.Equal(1, opponent["id"].L());
        Assert.Equal(1, opponent["squadId"].L());
        Assert.True(opponent["valid"].B());
        Assert.True(opponent["home"].B());
        Assert.True(opponent["isHomeTeam"].B());
        Assert.Equal(VenueStadiumId, opponent["stadiumAssetId"].L());
        Assert.Equal(VenueStadiumId, opponent["stadiumId"].L());
        Assert.Equal(23, opponent["players"].A().Count);
        Assert.Equal(11, opponent["players"].Objects().Take(11).Count(s => s["itemData"].IsObject()));

        var match = rt.CallObject("POST", $"{G}/match", new JsonObject());
        rt.PlayedRealSeconds();
        Assert.Equal("DRAFT_SQUAD", match["squad"]!["squadType"].S());
        Assert.Equal(1, match["opponentTeamId"].L());
        Assert.False(match["home"].B());
        Assert.False(match["isHomeTeam"].B());
        Assert.False(match["squad"]!["home"].B());
        Assert.False(match["squad"]!["isHomeTeam"].B());
        Assert.Equal(VenueStadiumId, match["stadiumAssetId"].L());
        Assert.True(match["opponentSquad"].JsonEquals(match["awaySquad"]) && match["awaySquad"].JsonEquals(match["opponent"]));
        Assert.Equal(VenueStadiumId, match["opponentSquad"]!["stadiumAssetId"].L());
        foreach (var collection in new[] { "actives", "club" })
        {
            var stadium = match["opponentSquad"]![collection].Objects().First(i => i["itemType"]?.GetValue<string>() == "stadium");
            Assert.Equal(VenueCardResourceId, stadium["resourceId"].L());
            Assert.Equal(VenueCardAssetId, stadium["assetId"].L());
            Assert.NotEqual(clubStadiumId, stadium["id"].L());
        }
        Assert.True(match["squad"]!["active"].B());
        Assert.True(match["squad"]!["valid"].B());
        Assert.Equal(100, match["squad"]!["chemistry"].L());
        Assert.Equal(5, match["squad"]!["starRating"].L());
        Assert.Equal(5, match["squad"]!["kicktakers"].A().Count);
        Assert.All(match["squad"]!["players"].Objects().Where(s => s["itemData"].IsObject()), s => Assert.Equal("free", s["itemData"]!["itemState"].S()));

        var creditsBeforeEnd = store.Credits();
        // A full match by FIFA's game clock (the reward gate pays nothing shorter); the run is Professional, so unscaled.
        var result = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "LOSS", ["secondsPlayed"] = 5461 });
        Assert.True(result["draftEliminated"].B());
        // The match pays a loss's coins now; the run reward waits for the prizes screen's claim: a single-player run with
        // no wins pays its level's 1,500 coins and one drawn choice (a Gold Pack, or a Draft Token as 15,000 coins), whatever
        // the difficulty.
        Assert.Equal(rt.Game.Rules.Seasons.MatchCompletionRewards["draftSinglePlayer"].Coins, result["participationAward"].L());
        Assert.Equal(result["matchCoins"].L(), result["rewardCoins"].L());
        Assert.Equal(creditsBeforeEnd + result["matchCoins"].L(), store.Credits());
        var payout = draft.RunPayout(store.DraftRun(DraftSlot.SinglePlayer)!, DraftSlot.SinglePlayer);
        Assert.Equal(1500, payout.Level.Coins);
        Assert.Equal(payout.Coins, store.DraftRun(DraftSlot.SinglePlayer)!.Int("pendingCoinReward"));
        Assert.Equal(store.Credits(), result["sessionCoinsBankBalance"].L());
        Assert.Equal(1, result["opponentTeamId"].L());
        Assert.True(rt.Game.UserSessionRefresh.IsSet);
        rt.Game.UserSessionRefresh.Clear();

        var packsBefore = store.UnopenedPackIds().Count;
        var award = rt.Call("POST", $"{G}/squad/mode/1/draft/grant/award", new JsonObject());
        Assert.True(award.JsonEquals(new JsonArray(new JsonObject { ["halId"] = 0, ["type"] = 0, ["value"] = payout.Coins })));
        Assert.Equal(packsBefore + payout.Packs.Count, store.UnopenedPackIds().Count);
        // The match's own coins stay on top of the run reward.
        Assert.Equal(creditsBefore - 15000 + payout.Coins + result["matchCoins"].L(), store.Credits());
        Assert.Null(store.DraftRun());
        return payout.Coins;
    }

    private static void StadiumScenario(TestRuntime rt)
    {
        var stadium = rt.CallObject("GET", $"{G}/stadium");
        Assert.Equal(FutStadiumVenueId, stadium["stadiumId"].L());
        Assert.Equal(1, stadium["lastUsedTierLevel"].L());
        Assert.Single(stadium["tiers"].A());
        Assert.Equal(1, stadium["tiers"]![0]!["tier"].L());
        Assert.Equal(FutStadiumVenueId, stadium["tiers"]![0]!["stadiumDefinitionId"].L());
        var allSlots = stadium["tiers"]![0]!["slots"].Objects().ToList();
        // Retail My Stadium (MatchDayStadiumTests covers its sections, locks and Stadium Development): 36 slots in FIFA's order.
        // The Match Day slots have FIFA's fixed ids (ball 1, badge 2, home kit 3, away kit 4, stadium 5) and are filled from the
        // club's actives; the vanity slots start empty.
        Assert.Equal(36, allSlots.Count);
        Assert.Equal(Enumerable.Range(1, 36).Select(i => (long)i), allSlots.Select(s => s["sortOrder"].L()));
        Assert.All(allSlots.Where(s => s["slotId"].L() > 5), s => Assert.True(s["itemData"] is null && s["vanityTypes"].A().Count > 0));
        var slots = allSlots.Where(s => s["slotId"].L() <= 5).ToList();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, slots.Select(s => s["slotId"].L()));
        Assert.Equal(slots.Select(s => s["slotId"].L()), slots.Select(s => s["clientSlotId"].L()));
        // vanityTypes are integer card subtypes; strings parsed as type 0 in CardsDLL.
        Assert.Equal(new long[] { 30, 11, 9, 9, 10 }, slots.Select(s => s["vanityTypes"].A().Single().L()));
        var locked = slots.ToDictionary(s => s["itemData"]!["itemState"].S(), s => s["locked"].B());
        Assert.Equal(new Dictionary<string, bool>
        {
            ["activeHomeKit"] = false,
            ["activeAwayKit"] = false,
            ["activeBadge"] = false,
            ["activeBall"] = false,
            ["activeStadium"] = false,
        }, locked);
        Assert.Equal(new[] { "FUT_STADIUM_BALL", "Badge", "Home_Kit", "Away_Kit", "FUT_STADIUM_MATCH" }, slots.Select(s => s["slotName"].S()));
        Assert.All(slots, s => Assert.True(s["itemData"]!["id"].L() > 0));
        Assert.All(slots, s => Assert.True(s["itemData"]!.AsObject().ContainsKey("cardSubTypeId")));
        var stadiumItem = slots.Select(s => s["itemData"]!).First(i => i["itemState"].S() == "activeStadium");
        // The default card wears the tier's own face (292b6a58): 6200000 + venue, the starter FUT Stadium for a new club.
        Assert.Equal(StadiumCardBase + FutStadiumVenueId, stadiumItem["resourceId"].L());
        Assert.Equal(StadiumCardBase + FutStadiumVenueId, stadiumItem["carddbid"].L());
        Assert.Equal(FutStadiumVenueId, stadiumItem["assetId"].L());

        // A home Squad Battles fixture keeps the club's own stadium (dbda9bc4); the CPU opponent plays in the match venue.
        var homeStadium = rt.Fut.SquadBattles.MatchActives(rt.Game, opponent: false).First(i => i.Text("itemState") == "activeStadium");
        Assert.Equal(StadiumCardBase + FutStadiumVenueId, homeStadium.Int("resourceId"));
        Assert.Equal(FutStadiumVenueId, homeStadium.Int("assetId"));
        var matchStadium = rt.Fut.SquadBattles.MatchActives(rt.Game, opponent: true).First(i => i.Text("itemState") == "activeStadium");
        Assert.Equal(VenueCardResourceId, matchStadium.Int("resourceId"));
        Assert.Equal(VenueCardAssetId, matchStadium.Int("assetId"));
        Assert.Equal(VenueStadiumId, matchStadium.Int("stadiumid"));

        var saved = rt.CallObject("PUT", $"{G}/stadium", new JsonObject { ["myStadium_tier"] = 1, ["selectedStadiumId"] = VenueStadiumId });
        Assert.Equal(1, saved["lastUsedTierLevel"].L());
        Assert.Equal(VenueStadiumId, saved["stadiumId"].L());
        var savedItem = saved["tiers"]![0]!["slots"].Objects().Where(s => s["itemData"] is not null).Select(s => s["itemData"]!).First(i => i["itemState"].S() == "activeStadium");
        // Only a FUT Stadium tier has its own default card face; any other chosen venue keeps the shared default card,
        // as 6200000 + that venue is another stadium's card (ClubPresentationService.ClubItemsAsync).
        Assert.Equal(StadiumCardBase + 58, savedItem["resourceId"].L());
        Assert.Equal(VenueStadiumId, savedItem["assetId"].L());
        Assert.Equal(VenueStadiumId, savedItem["stadiumid"].L());
        // Sanderson Park (261) loads a bare pitch, so selecting it keeps the FUT Stadium.
        var bare = rt.CallObject("PUT", $"{G}/stadium", new JsonObject { ["selectedStadiumId"] = 261 });
        Assert.Equal(FutStadiumVenueId, bare["stadiumId"].L());
    }

    private static (JsonObject Squad, JsonObject User) UserAndSquadScenario(TestRuntime rt)
    {
        var user = rt.CallObject("GET", $"{G}/user");
        Assert.Equal(250, user["clubPlayers"].L());
        Assert.Equal(1, user["pileManagers"].L());
        Assert.True(user["squadBattleEnabled"].B());
        Assert.True(user["squadBattleCouchPlayEnabled"].B());
        Assert.True(user["enableMyStadium"].B());
        Assert.Equal(rt.Game.Rules.Club.MyStadiumSlots.Count, user["myStadium_slot"].A().Count); // five Match Day and 31 vanity slots
        Assert.Equal(11, user["pileSquadPlayers"].L());
        var activeKits = user["actives"].Objects().Where(i => i["itemType"]?.GetValue<string>() == "kit").ToList();
        Assert.Equal(2, activeKits.Count);
        Assert.All(activeKits, k => Assert.True(k["stackCount"].L() == 1 && k["islocked"].L() == 0 && k["pile"].S() == "club"));
        Assert.Equal(rt.Game.Config.ClubName, user["clubName"].S());
        Assert.Single(user["squadList"]!["squad"].A());
        Assert.Equal("REGULAR_SQUAD", user["squadList"]!["squad"]![0]!["squadType"].S());

        var squad = rt.CallObject("GET", $"{G}/squad/active");
        var occupied = squad["players"].Objects().Where(s => s["itemData"].IsObject()).ToList();
        Assert.Equal(1, squad["id"].L());
        Assert.Equal(rt.Game.Config.ClubName.Replace("FC", "XI", StringComparison.Ordinal), squad["squadName"].S());
        Assert.Equal(11, occupied.Count);
        Assert.All(occupied, s => Assert.True(rt.Game.Players.Contains(s["itemData"]!["resourceId"].L())));
        var manager = squad["manager"]![0]!;
        Assert.Single(squad["manager"].A());
        Assert.Equal("manager", manager["itemType"].S());
        Assert.Equal(4, manager["cardsubtypeid"].L());
        Assert.True(rt.Game.Managers.Contains(manager["resourceId"].L()));
        Assert.Equal(manager["carddbid"].L(), manager["resourceId"].L());
        Assert.Equal(1, manager["stackCount"].L());
        Assert.Equal("free", manager["itemState"].S());
        Assert.Equal("club", manager["pile"].S());
        Assert.True(manager["contract"].L() > 0);
        return (squad, user);
    }

    private static void OnboardingScenario(TestRuntime rt, JsonObject squad, JsonObject user)
    {
        var store = rt.Game.Store();
        var mass = rt.CallObject("GET", $"{G}/userMassInfo?screen=futlivemsggen4");
        Assert.True(mass["userInfo"]!["squadList"].JsonEquals(user["squadList"]));
        Assert.True(mass["squad"].JsonEquals(squad));
        var loans = mass["loanPlayers"]!["loans"].Objects().ToList();
        Assert.Equal(40, loans.Count);
        Assert.Equal(40, loans.Select(o => o["defId"].L()).Distinct().Count());
        Assert.All(loans, o => Assert.True(o["ranking"].L() is >= 1 and <= 5 && o["loans"].L() == 7));

        var selectedOffer = loans[0];
        var signed = rt.CallObject("PUT", $"{G}/loan/player/{selectedOffer["defId"].L()}");
        Assert.Equal(selectedOffer["itemData"]!["resourceId"].L(), signed["resourceId"].L());
        Assert.NotEqual(selectedOffer["defId"].L(), signed["id"].L());

        var replacementOffer = loans[1];
        var replacement = rt.CallObject("PUT", $"{G}/loan/player/{replacementOffer["defId"].L()}");
        Assert.NotEqual(signed["id"].L(), replacement["id"].L());
        Assert.NotNull(store.GetItem(signed["id"].L()));
        rt.Call("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = replacement["id"].L(), ["pile"] = "squad" }) });
        var replacementSquad = store.ListSquads()[0];
        replacementSquad["players"]![0]!["itemId"] = replacement["id"].L();
        rt.Call("PUT", $"{G}/squad/1?onboarding=true", replacementSquad);
        Assert.Null(store.GetItem(signed["id"].L()));
        Assert.Equal("squad", store.GetItem(replacement["id"].L())!.Text("pile"));

        // Kits and badges: a random choice per club (rules/onboarding.json); the picks are granted and equipped.
        var kits = rt.CallObject("GET", $"{G}/onboarding/kits");
        Assert.Equal(rt.Game.Rules.Onboarding.Kits.HomeChoices, kits["homeItemDataList"].A().Count);
        Assert.Equal(rt.Game.Rules.Onboarding.Kits.AwayChoices, kits["awayItemDataList"].A().Count);
        var home = kits["homeItemDataList"]![0]!;
        var away = kits["awayItemDataList"]![0]!;
        var selectedKits = rt.CallObject("POST", $"{G}/onboarding/kits", new JsonObject { ["homeKitId"] = home["resourceId"].L(), ["awayKitId"] = away["resourceId"].L() });
        Assert.True(selectedKits["homeKitId"].L() > 0);
        Assert.True(selectedKits["awayKitId"].L() > 0);
        Assert.NotEqual(selectedKits["homeKitId"].L(), selectedKits["awayKitId"].L());
        Assert.Equal(home["resourceId"].L(), store.GetItem(selectedKits["homeKitId"].L())!["resourceId"].L());
        Assert.Equal("activeHomeKit", store.GetItem(selectedKits["homeKitId"].L())!.Text("itemState"));

        var badges = rt.CallObject("GET", $"{G}/onboarding/badges");
        Assert.Equal(rt.Game.Rules.Onboarding.Badges.Choices, badges["badgeItemDataList"].A().Count);
        var badge = badges["badgeItemDataList"]![0]!;
        var selectedBadge = rt.Call("POST", $"{G}/onboarding/badge/{badge["resourceId"].L()}");
        var ownedBadge = store.GetItem(selectedBadge["badgeId"].L())!;
        Assert.Equal(badge["resourceId"].L(), ownedBadge["resourceId"].L());
        Assert.Equal("activeBadge", ownedBadge.Text("itemState"));
        Assert.True(rt.Fut.Onboarding.IsComplete());

        var completedMass = rt.CallObject("GET", $"{G}/userMassInfo?screen=futlivemsggen4");
        Assert.True(completedMass["loanPlayers"].JsonEquals(new JsonObject { ["loans"] = new JsonArray() }));
        Assert.True(rt.Call("GET", $"{G}/loan/players").JsonEquals(new JsonObject { ["loans"] = new JsonArray() }));
        var progress = rt.Call("GET", $"{G}/clientdata/onboarding");
        Assert.True(progress.JsonEquals(new JsonObject
        {
            ["entries"] = new JsonArray(
                new JsonObject { ["key"] = 0, ["value"] = 3 },
                new JsonObject { ["key"] = 1, ["value"] = home["resourceId"].L() },
                new JsonObject { ["key"] = 2, ["value"] = away["resourceId"].L() },
                new JsonObject { ["key"] = 3, ["value"] = badge["resourceId"].L() }),
        }));
        var loanProgress = rt.Call("GET", $"{G}/clientdata/loanplayerreward");
        Assert.Equal(replacement["assetId"].L(), loanProgress["entries"]![0]!["value"].L());
        Assert.True(completedMass["onboardingClientData"].JsonEquals(progress));
        Assert.True(completedMass["loanPlayerClientData"].JsonEquals(loanProgress));
        var squadProgress = rt.Call("PUT", $"{G}/clientdata/squad", new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["key"] = 5, ["value"] = 0 }) });
        Assert.True(squadProgress.JsonEquals(JsonNode.Parse("""{"entries":[{"key":5,"value":0}]}""")));
        Assert.True(rt.Call("GET", $"{G}/clientdata/squad").JsonEquals(squadProgress));

        // Dynamic special-card images are cached binary DDS assets.
        const long dynamicResource = 184707399;
        var dynamicPath = Path.Combine(rt.Game.Paths.ContentCacheDirectory, "fut", "playerheads", "g4", "single", $"p{dynamicResource}.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(dynamicPath)!);
        var expected = "DDS "u8.ToArray().Concat(new byte[124]).ToArray();
        File.WriteAllBytes(dynamicPath, expected);
        var head = rt.Router.Handle(new Aurora21.UltimateTeam.Core.Web.FutRequest("GET", $"/fut/playerheads/g4/single/p{dynamicResource}.dds", Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
        Assert.Equal(200, head.Status);
        Assert.Equal("application/octet-stream", head.ContentType);
        Assert.Equal(expected, head.Body);
    }

    private static JsonObject ClubScenario(TestRuntime rt, long draftCoins)
    {
        var store = rt.Game.Store();
        var clubPage = rt.CallObject("GET", $"{G}/club?count=20");
        Assert.Equal(250, clubPage["clubPlayers"].L());
        Assert.Equal(20, clubPage["itemData"].A().Count);
        Assert.All(clubPage["itemData"].Objects(), i => Assert.True(rt.Game.Players.Contains(i["resourceId"].L())));

        var managerPage = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "manager", ["sort"] = "desc", ["start"] = 0, ["count"] = 91 });
        Assert.Equal(1, managerPage["pileManagers"].L());
        Assert.Equal(1, managerPage["itemCount"].L());
        var ownedManager = managerPage["itemData"]![0]!;
        Assert.Equal("manager", ownedManager["itemType"].S());
        Assert.Equal(4, ownedManager["cardsubtypeid"].L());
        Assert.True(rt.Game.Managers.Contains(ownedManager["resourceId"].L()));
        Assert.True(ownedManager["resourceId"].L() >= 1_000_000);
        Assert.Equal(1, ownedManager["stackCount"].L());
        var ownedManagerId = ownedManager["id"].L();
        Assert.NotEmpty(ownedManager["firstName"].S());
        Assert.NotEmpty(ownedManager["lastName"].S());

        // The two starting kits plus the pair chosen during onboarding, which is granted and equipped in their place.
        var kitPage = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "kit", ["start"] = 0, ["count"] = 20 });
        Assert.Equal(4, kitPage["kitCount"].L());
        Assert.Equal(4, kitPage["itemData"].A().Count);
        Assert.Equal(new[] { "activeAwayKit", "activeHomeKit", "free", "free" }, kitPage["itemData"].Objects().Select(i => i["itemState"].S()).OrderBy(s => s));
        Assert.All(kitPage["itemData"].Objects(), i => Assert.True(i["stackCount"].L() == 1 && i["islocked"].L() == 0));

        var stadiumKits = rt.CallObject("POST", $"{G}/club", new JsonObject
        {
            ["searchCriteria"] = new JsonObject { ["type"] = "clubInfo", ["category"] = "kit", ["start"] = 0, ["count"] = 20 },
        });
        Assert.Equal(4, stadiumKits["itemCount"].L());
        Assert.All(stadiumKits["itemData"].Objects(), i => Assert.True(i["itemType"].S() == "kit" && i["cardsubtypeid"].L() == 9));

        var stadiumBadges = rt.CallObject("GET", $"{G}/club?type=clubInfo&cat=badge&count=20");
        Assert.True(stadiumBadges["itemCount"].L() >= 1);
        Assert.All(stadiumBadges["itemData"].Objects(), i => Assert.True(i["itemType"].S() == "custom" && i["cardsubtypeid"].L() == 11));

        // A manager saved as id 0 is repaired to the owned manager.
        var nullManagerSquad = store.ListSquads()[0];
        nullManagerSquad["manager"] = new JsonArray(new JsonObject { ["id"] = 0, ["dream"] = false });
        var savedWithManager = rt.Fut.Inventory.SaveSquad(nullManagerSquad);
        Assert.True(savedWithManager["manager"].JsonEquals(new JsonArray(new JsonObject { ["id"] = ownedManagerId, ["dream"] = false })));
        var activeWithManager = rt.CallObject("GET", $"{G}/squad/active")["manager"]![0]!;
        Assert.Equal(ownedManagerId, activeWithManager["id"].L());
        Assert.Equal("manager", activeWithManager["itemType"].S());
        Assert.Equal(4, activeWithManager["cardsubtypeid"].L());
        Assert.Equal("free", activeWithManager["itemState"].S());
        Assert.Equal("club", activeWithManager["pile"].S());

        var page1 = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "player", ["sort"] = "desc", ["start"] = 0, ["count"] = 10 });
        var page2 = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "player", ["sort"] = "desc", ["start"] = 10, ["count"] = 10 });
        Assert.Equal(250, page1["itemCount"].L());
        Assert.Equal(10, page1["itemData"].A().Count);
        Assert.Equal(10, page2["itemData"].A().Count);
        Assert.Empty(page1["itemData"].Objects().Select(i => i["id"].L()).Intersect(page2["itemData"].Objects().Select(i => i["id"].L())));

        var owned = rt.Fut.Inventory.OwnedPlayerResourceIds();
        var candidateResource = rt.Game.Players.Ids.First(id => !owned.Contains(id));
        var candidate = rt.Fut.Items.MakePlayer(candidateResource, "unassigned");
        var moved = rt.CallObject("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = candidate.Int("id"), ["pile"] = "club" }) });
        Assert.True(moved["itemData"].JsonEquals(new JsonArray(new JsonObject { ["id"] = candidate.Int("id"), ["pile"] = "club", ["success"] = true })));
        var found = rt.CallObject("POST", $"{G}/club", new JsonObject { ["maskedDefId"] = candidate.Int("assetId"), ["start"] = 0, ["count"] = 91 });
        Assert.Contains(candidate.Int("id"), found["itemData"].Objects().Select(i => i["id"].L()));

        var lookupIds = clubPage["itemData"].Objects().Take(2).Select(i => i["id"].L()).ToList();
        var lookup = rt.CallObject("GET", $"{G}/item?idList={lookupIds[0]},{lookupIds[1]},999999999999");
        Assert.Equal(lookupIds, lookup["itemData"].Objects().Select(i => i["id"].L()));
        Assert.All(lookup["itemData"].Objects(), i => Assert.True(rt.Game.Players.Contains(i["resourceId"].L()) && !i.ContainsKey("pile")));
        var single = rt.CallObject("GET", $"{G}/item/{lookupIds[0]}");
        Assert.Equal(new[] { lookupIds[0] }, single["itemData"].Objects().Select(i => i["id"].L()));
        var (missingStatus, missing, _) = rt.CallRaw("GET", $"{G}/item/999999999999");
        Assert.Equal(404, missingStatus);
        Assert.True(missing.JsonEquals(new JsonObject { ["reason"] = "item not found" }));

        // A persisted but completely empty squad is repaired without replacing non-empty lineups.
        var empty = store.ListSquads()[0];
        empty["players"] = new JsonArray();
        rt.Fut.Inventory.SaveSquad(empty);
        rt.Fut.Inventory.RepairEmptyActiveSquad();
        Assert.Equal(11, SquadDocuments.ItemIds(store.ListSquads()[0]).Count);

        var credits = rt.CallObject("GET", $"{G}/user/credits");
        // Includes the Draft scenario: the 15,000 entry, its lost match's coins (every Draft match pays match coins) and the
        // no-win run reward.
        Assert.Equal(70000000 - 15000 + draftCoins + rt.Game.Rules.Seasons.MatchCompletionRewards["draftSinglePlayer"].Coins, credits["currencies"]![0]!["funds"].L());
        return clubPage;
    }

    private static void SquadBattlesScenario(TestRuntime rt)
    {
        var store = rt.Game.Store();
        var hub = rt.CallObject("GET", $"{G}/sqbt/user/hub");
        // The retail week (Python server f354894): 40 community matches, Monday to Monday.
        Assert.Equal(0, hub["gamesPlayed"].L());
        Assert.Equal(rt.Game.Rules.SquadBattles.Competition.MaxRegularGames, hub["maxRegularGames"].L());
        Assert.True(hub["startTime"].L() <= hub["currentTime"].L() && hub["currentTime"].L() < hub["endTime"].L());
        Assert.Equal(7 * 24 * 3600, hub["endTime"].L() - hub["startTime"].L());
        var summaries = hub["sqbtOppSquadList"]!["sqbtOppSquads"].Objects().ToList();
        Assert.Equal(4, summaries.Count);
        // No other club on this server: every opponent is a generated squad.
        Assert.All(summaries, o =>
        {
            Assert.Equal(VenueStadiumId, o["sqbt2data"]!["stadiumAssetId"].L());
            Assert.True(o["sqbt2data"]!["isHomeTeam"].B());
            Assert.False(o.ContainsKey("topPlayers"));
            Assert.Equal(3, o["sqbt2data"]!["topPlayers"].A().Count);
        });

        // Home or away comes from the parity of the summary fields CardsDLL sums, the kit team ids included (even: the user
        // hosts): the first set's even slots come to the user.
        bool UserHosts(JsonObject o) => (long.Parse(o["badgeAssetId"].S()) + o["rating"].L() + o["chemistry"].L() + 16
            + long.Parse(o["estDate"].S()) + o["sqbt2data"]!["stadiumAssetId"].L()
            + (o["sqbt2data"]!["homeKitData"]?["teamId"]?.GetValue<long>() ?? 0) + (o["sqbt2data"]!["awayKitData"]?["teamId"]?.GetValue<long>() ?? 0)) % 2 == 0;
        Assert.Equal(2, summaries.Count(UserHosts));
        Assert.All(summaries, o => Assert.Equal(o["id"].L() % 2 == 0, UserHosts(o)));

        var lineups = new List<string>();
        foreach (var summary in summaries)
        {
            var opponent = rt.CallObject("GET", $"{G}/sqbt/user/opponentsquad/{summary["id"].L()}");
            // A CPU persona, so FIFA keeps the squad's own My Stadium for the fixtures it hosts.
            Assert.Equal(rt.Game.Rules.SquadBattles.Sets.SetPersonaBase + summary["id"].L(), opponent["personaId"].L());
            Assert.Equal(summary["estDate"].S(), opponent["estDate"].S());
            var cpuStadium = opponent["myStadiumInfo"]!["tiers"]![0]!["slots"].Objects().Single(slot => slot["slotId"].L() == 5)["itemData"]!;
            Assert.Equal(VenueStadiumId, cpuStadium["assetId"].L());
            Assert.Equal(23, opponent["players"].A().Count);
            Assert.Equal(23, opponent["players"].Objects().Select(p => p["index"].L()).Distinct().Count());
            Assert.Equal(VenueStadiumId, opponent["stadiumAssetId"].L());
            Assert.Equal(VenueStadiumId, opponent["stadiumId"].L());
            Assert.True(opponent["isHomeTeam"].B());
            var kits = opponent["actives"].Objects().Where(i => i["itemType"]?.GetValue<string>() == "kit").Select(i => i["resourceId"].L()).ToHashSet();
            Assert.True(kits.SetEquals(new long[] { 6301491, 6301492 }));
            var opponentStadium = opponent["actives"].Objects().First(i => i["itemType"]?.GetValue<string>() == "stadium");
            Assert.Equal(VenueCardResourceId, opponentStadium["resourceId"].L());
            Assert.Equal(VenueCardAssetId, opponentStadium["assetId"].L());
            Assert.Equal(VenueStadiumId, opponentStadium["stadiumid"].L());
            Assert.Equal(930000000090 + summary["id"].L(), opponentStadium["id"].L());
            var lineup = opponent["players"].Objects().Select(p => p["itemData"]!["resourceId"].L()).ToList();
            Assert.All(lineup, id => Assert.True(id <= 0x00FFFFFF));
            Assert.All(lineup, id => Assert.Contains(rt.Game.Players.Find(id)!.IntOr("rareflag", 1), new long[] { 0, 1 }));
            var top = summary["sqbt2data"]!["topPlayers"].Objects().ToList();
            Assert.All(top, p => Assert.Equal(new[] { "itemData" }, p.Select(kv => kv.Key)));
            Assert.All(top, p => Assert.True(p["itemData"]!["itemType"].S() == "player" && p["itemData"]!["id"].L() > 0));
            var expectedTop = lineup.Take(11).OrderByDescending(id => rt.Game.Players.Find(id)!.Rating).Take(3);
            Assert.Equal(expectedTop, top.Select(p => p["itemData"]!["resourceId"].L()));
            var byId = opponent["players"].Objects().ToDictionary(p => p["itemData"]!["id"].L(), p => p["itemData"]!["resourceId"].L());
            Assert.All(top, p => Assert.Equal(byId[p["itemData"]!["id"].L()], p["itemData"]!["resourceId"].L()));
            lineups.Add(string.Join(",", lineup));
        }
        Assert.Equal(4, lineups.Distinct().Count());
        var repeated = rt.CallObject("GET", $"{G}/sqbt/user/opponentsquad/0");
        Assert.Equal(lineups[0], string.Join(",", repeated["players"].Objects().Select(p => p["itemData"]!["resourceId"].L())));

        var defIds = repeated["players"].Objects().Select(p => p["itemData"]!["resourceId"].L()).ToList();
        var metadata = rt.Call("GET", $"{G}/attributes/metadata?defIds={string.Join(",", defIds)}").A();
        Assert.Equal(defIds, metadata.Objects().Select(r => r["defId"].L()));
        Assert.All(metadata.Objects(), r =>
        {
            Assert.Equal(new[] { "defId", "ingameattribs", "attributeArray", "rating", "trait1", "trait2", "version" }.OrderBy(k => k), r.Select(kv => kv.Key).OrderBy(k => k));
            Assert.Equal(34, r["ingameattribs"].A().Count);
            Assert.Equal(6, r["attributeArray"].A().Count);
        });

        // The developers' Pro cards carry their published record; their base cards keep their own for a club without the Pro card
        // (every owner's base Jota used to get 99s, in the details screen and in squads).
        long[] aquaIds = { 167996618, 168019987 };
        var aqua = rt.Call("GET", $"{G}/attributes/metadata?defIds={string.Join(",", aquaIds)}").A();
        Assert.Equal(aquaIds, aqua.Objects().Select(r => r["defId"].L()));
        Assert.All(aqua.Objects(), r =>
        {
            Assert.Equal(99, r["rating"].L());
            Assert.All(r["attributeArray"].A(), v => Assert.Equal(99, v.L()));
            // 066 maxes outfield stats and keeps inherited GK values: Jota's are unknown,
            // while Olise keeps 047's FIFA record (base 247827). Published records remain authoritative.
            var goalkeeperStats = r["defId"].L() == 168019987
                ? new long[] { 9, 11, 9, 10, 10 } : new long[] { 0, 0, 0, 0, 0 };
            Assert.Equal(Enumerable.Repeat(99L, 29).Concat(goalkeeperStats),
                r["ingameattribs"].A().Select(v => v.L()));
            Assert.Equal(CardAttributeRecordTests.ContentVersion(r), r["version"].L());
        });
        var baseJota = rt.Call("GET", $"{G}/attributes/metadata?defIds=224458").A().Objects().Single();
        Assert.Equal((224458L, rt.Game.Players.Find(224458)!.Rating), (baseJota["defId"].L(), baseJota["rating"].L()));
        Assert.Contains(baseJota["ingameattribs"].A(), v => v.L() != 99);
        JsonObject aquaItem = null!;
        foreach (var id in new long[] { 168019987, 167996618 })
        {
            aquaItem = rt.Fut.Items.BuildPlayer(id, 999000 + id, "club");
            Assert.Equal(5, aquaItem.Int("weakFoot"));
            Assert.Equal(5, aquaItem.Int("skillMoves"));
        }
        Assert.Equal("ST", rt.Fut.Items.BuildPlayer(167996618, 167996618, "club").Text("preferredPosition"));
        Assert.True(aquaItem["attributeList"].JsonEquals(new JsonArray(Enumerable.Range(0, 6).Select(i => (JsonNode?)new JsonObject { ["index"] = i, ["value"] = 99 }).ToArray())));

        var match = rt.CallObject("POST", $"{G}/match", new JsonObject
        {
            ["squadId"] = 1,
            ["type"] = "OFFLINE",
            ["sqbtEventId"] = hub["sqbtEventId"].L(),
            ["sqbtOpponentSquadId"] = 0,
            ["sqbtMatchDifficulty"] = 3,
        });
        Assert.True(match["startDateTime"].L() > 0);
        Assert.Equal(VenueStadiumId, match["stadiumAssetId"].L());
        Assert.Equal(VenueStadiumId, match["stadiumId"].L());
        Assert.True(match["isHomeTeam"].B());
        Assert.True(match["homeSquad"].JsonEquals(match["squad"]));
        Assert.True(match["opponentSquad"].JsonEquals(match["awaySquad"]) && match["awaySquad"].JsonEquals(match["opponent"]));
        Assert.Equal(VenueStadiumId, match["opponentSquad"]!["sqbt2data"]!["stadiumAssetId"].L());
        foreach (var collection in new[] { "actives", "club" })
        {
            var away = match["opponentSquad"]![collection].Objects().First(i => i["itemType"]?.GetValue<string>() == "stadium");
            Assert.Equal(VenueCardResourceId, away["resourceId"].L());
            Assert.Equal(VenueCardAssetId, away["assetId"].L());
            var homeIds = match["squad"]![collection].Objects().Where(i => i["itemType"]?.GetValue<string>() == "stadium").Select(i => i["id"].L());
            Assert.DoesNotContain(away["id"].L(), homeIds);
        }
        Assert.Equal(store.ListSquads()[0].Int("id"), match["squad"]!["id"].L());
        foreach (var collection in new[] { "actives", "club" })
        {
            var kits = match["squad"]![collection].Objects().Where(i => i["itemType"]?.GetValue<string>() == "kit").ToList();
            Assert.Equal(2, kits.Count);
            // The pair chosen during onboarding (OnboardingScenario) is the one equipped.
            Assert.True(kits.Select(k => k["resourceId"].L()).ToHashSet().SetEquals(store.Onboarding().KitChoices));
            Assert.Equal(2, kits.Select(k => k["id"].L()).Distinct().Count());
            Assert.All(kits, k => Assert.True(k["year"].L() == 0 && k["resourceGameYear"].L() == 2021 && k["pile"].S() == "club" && k["stackCount"].L() == 1));
            // The home side keeps the club's own stadium (dbda9bc4): the FUT Stadium the stadium scenario left selected.
            var homeStadium = match["squad"]![collection].Objects().First(i => i["itemType"]?.GetValue<string>() == "stadium");
            Assert.Equal(StadiumCardBase + FutStadiumVenueId, homeStadium["resourceId"].L());
            Assert.Equal(FutStadiumVenueId, homeStadium["stadiumid"].L());
        }
    }

    private static void StoreAndSbcScenario(TestRuntime rt)
    {
        var store = rt.Game.Store();
        var purchase = rt.CallObject("GET", $"{G}/store/purchasegroup/all?ppInfo=true")["purchase"].Objects().ToList();
        var draftEntries = purchase.Where(p => p["packType"]?.GetValue<string>() == "DRAFTENTRY").ToList();
        Assert.True(draftEntries.Select(p => p["id"].L()).ToHashSet().SetEquals(new long[] { 20024, 20026, 20027 }));
        Assert.All(draftEntries, p => Assert.Equal(15000, p["finalPrice"].L()));
        var promo = purchase.First(p => p["id"].L() == 92);
        Assert.Equal("Synthetic Elite Ten 82+ pack", promo["name"].S());
        Assert.Equal(50000, promo["finalPrice"].L());
        Assert.True(promo["displayGroup"].JsonEquals(new JsonObject { ["value"] = "special", ["priority"] = 4 }));
        Assert.Equal("special", promo["purchaseGroup"].S());
        Assert.True(promo["categoryList"].JsonEquals(new JsonObject { ["categoryId"] = 4, ["orderInCategory"] = 92 }));
        Assert.True(promo["packContentInfo"].JsonEquals(JsonNode.Parse("""{"itemQuantity":10,"goldQuantity":10,"silverQuantity":0,"bronzeQuantity":0,"rareQuantity":10}""")));

        // Every bundled pack reward names an existing pack by id (SbcRewardPackConversionTests checks each one).
        var playerRewards = 0;
        foreach (var challenge in rt.Game.Sbc.Challenges)
        {
            foreach (var reward in challenge.Source.Obj("rewards")!.Arr("entries")!.Objects())
            {
                var kind = reward.Text("type").ToUpperInvariant();
                var attributes = reward.Obj("attributes") ?? new JsonObject();
                if (kind == "PACK")
                {
                    Assert.NotEmpty(rt.Game.Packs.GetPackConfig(SbcService.RewardPackId(attributes)));
                }
                else if (kind == "PLAYER")
                {
                    playerRewards++;
                    var resourceId = rt.Fut.Sbc.PlayerResourceId(reward);
                    var meta = rt.Game.Players.Find(resourceId)!;
                    var intended = SbcService.NormalizeName(attributes.Get("name"));
                    var actual = SbcService.NormalizeName(meta.Get("name"));
                    Assert.True(actual.Length > 0 && (intended.Contains(actual, StringComparison.Ordinal) || actual.Contains(intended, StringComparison.Ordinal)), $"{challenge.Key} {intended} {actual}");
                    var award = rt.Fut.Sbc.AwardJson(reward, challenge.SetKey)!;
                    var exactResource = intended.Length == 0 && attributes.Get("resourceId") is not null;
                    if (exactResource) Assert.Equal(attributes.Int("resourceId"), resourceId);
                    var rating = exactResource ? meta.Rating : attributes.Int("rating");
                    var position = exactResource ? meta.Position : attributes.Text("position");
                    Assert.Equal(rating, award.Int("rating"));
                    Assert.Equal(position, award.Text("position"));
                    Assert.Equal(rating, award.Obj("itemData")!.Int("rating"));
                    Assert.Equal(position, award.Obj("itemData")!.Text("preferredPosition"));
                }
            }
        }
        Assert.Equal(18, playerRewards); // The original 15 plus the three disabled POTM_1 challenges.

        // The player-pick upgrades carry real requirements (SbcPhaseOneTests checks each). None is on the live SBC list in this phase;
        // their reward contract still has to hold.
        rt.Game.Sbc.LiveKeys.UnionWith(rt.Game.Sbc.Sets.Select(set => set.Key));
        foreach (var setId in new[] { "UP_82", "UP_84", "UP_86" })
        {
            var upgrade = rt.Game.Sbc.Sets.First(s => s.Key == setId).Challenges[0];
            Assert.NotEmpty(rt.Fut.Sbc.EligibilityRequirements(upgrade));
            var json = rt.Fut.Sbc.ChallengesResponse(upgrade.SetId)["challenges"]![0]!;
            Assert.Equal(1, json["packsAmount"].L());
            Assert.Equal(0, json["itemsAmount"].L());
            var award = json["awards"]![0]!;
            Assert.Equal("pack", award["type"].S());
            Assert.True(award["isPlayerPick"].B());
            Assert.Equal("PLAYER_PICK", award["packType"].S());
            // Any itemData, even empty, makes the reward decoders draw a player card instead of the pack.
            Assert.False(award.AsObject().ContainsKey("itemData"));
            Assert.Equal(award["assetId"].L(), award["halId"].L());
        }

        var pickUpgrade = rt.Game.Sbc.Sets.First(s => s.Key == "UP_82").Challenges[0];
        var before = store.UnopenedPackIds().Count(id => id == 513);
        var granted = rt.Fut.Sbc.GrantChallengeRewards(pickUpgrade);
        Assert.True(ClubPresentationService.ToArray(granted).JsonEquals(JsonNode.Parse("""[{"itemType":"pack","packId":513}]""")));
        Assert.Equal(before + 1, store.UnopenedPackIds().Count(id => id == 513));
        Assert.Empty(rt.Game.InUnit(ClubInventoryService.PendingPlayerPickTokensAsync));
        var ownedPickPack = rt.CallObject("GET", $"{G}/store/purchasegroup/mypacks")["purchase"].Objects().First(p => p["id"].L() == 513);
        Assert.Equal("82+ Player Pick", ownedPickPack["name"].S());
        Assert.True(store.TakeUnopenedPack(513));

        var token = rt.Fut.Items.MakePlayerPick(86, 4);
        Assert.Equal("misc", token.Text("itemType"));
        Assert.Equal(237, token.Int("cardsubtypeid"));
        Assert.Equal("UNLOCKS", token.Text("CARD_TYPE"));
        Assert.Equal("PLAYER_PICK", token.Text("UNLOCKABLE_TYPE"));
        Assert.Equal("1 of 4 86+ Player Pick", token.Text("name"));
        var redeemed = rt.CallObject("POST", $"{G}/item/nontargeted?itemId={token.Int("id")}");
        Assert.Equal(1, redeemed["availablePicks"].L());
        var choiceData = redeemed["choiceData"].Objects().ToList();
        Assert.Equal(4, choiceData.Count);
        Assert.Equal(4, choiceData.Select(i => i["id"].L()).Distinct().Count());
        Assert.Equal(4, choiceData.Select(i => i["resourceId"].L()).Distinct().Count());
        Assert.All(choiceData, i => Assert.True(i["rating"].L() >= 86));
        Assert.Equal(new long[] { 0, 1, 2, 3 }, choiceData.Select(i => i["globalChoiceIndex"].L()));
        Assert.Equal(new long[] { 0, 1, 2, 3 }, choiceData.Select(i => i["localChoiceIndex"].L()));
        var pending = rt.CallObject("GET", $"{G}/playerpicks");
        Assert.Equal(choiceData.Select(i => i["id"].L()), pending["choiceData"].Objects().Select(i => i["id"].L()));
        var selected = choiceData[2];
        var confirmed = rt.CallObject("POST", $"{G}/playerpicks/item/{selected["resourceId"].L()}/select");
        Assert.Equal(selected["id"].L(), confirmed["id"].L());
        Assert.Equal(selected["resourceId"].L(), confirmed["resourceId"].L());
        Assert.Null(store.GetItem(token.Int("id")));
        var storedPick = store.GetItem(selected["id"].L())!;
        Assert.True(storedPick.Bool("untradeable"));
        Assert.Equal("unassigned", storedPick.Text("pile"));

        token = rt.Fut.Items.MakePlayerPick(86, 4);
        redeemed = rt.CallObject("POST", $"{G}/item/nontargeted?itemId={token.Int("id")}");
        selected = redeemed["choiceData"]![2]!.AsObject();
        confirmed = rt.CallObject("POST", $"{G}/playerpicks/{token.Int("id")}", new JsonObject { ["globalChoiceIndex"] = 2 });
        Assert.Equal(selected["resourceId"].L(), confirmed["resourceId"].L());
        Assert.Null(store.GetItem(token.Int("id")));
        storedPick = store.GetItem(selected["id"].L())!;
        Assert.True(storedPick.Bool("untradeable"));
        Assert.Equal("unassigned", storedPick.Text("pile"));
        Assert.False(storedPick.ContainsKey("playerPickItemId"));
        Assert.False(storedPick.ContainsKey("globalChoiceIndex"));

        var queued = rt.Fut.Packs.CreatePack(515);
        Assert.Equal("PLAYER_PICK", queued.Text("packType"));
        Assert.Single(queued.Arr("itemData")!);
        Assert.Equal(237, queued["itemData"]![0]!["cardsubtypeid"].L());
        var queuedChoices = rt.Fut.Picks.Redeem(queued["itemData"]![0]!["id"].L());
        rt.Fut.Picks.Select(queued["itemData"]![0]!["id"].L(), queuedChoices[0]!["id"]!.DeepClone());

        // An SBC puts its pack in My Packs, opens for free, consumes one copy and cannot pay twice.
        var firstExchange = rt.Game.Sbc.Challenges.First(c => c.Key == "CH_1");
        var unopenedBefore = store.UnopenedPackIds().Count;
        var creditsBefore = store.Credits();
        // CH_1 consumes exactly one owned player. The previous literal 999001 was never granted to
        // this club, so the submission was refused as SBC_PLAYER_NOT_OWNED before reaching the
        // reward assertions below. Take a real club card that satisfies the eligibility rules.
        // First Exchange asks for exactly one bronze player; the starter club owns none, so the fixture mints one.
        var exchangedId = SbcFixtures.OwnedPlayerItemId(rt, "bronze");
        var exchanged = store.ListItems("club").Concat(store.ListItems("squad")).First(candidate => candidate.Int("id") == exchangedId);
        rt.Call("PUT", $"{G}/sbs/challenge/{firstExchange.Id}/squad", new JsonObject { ["players"] = new JsonArray(new JsonObject { ["index"] = 10, ["itemData"] = new JsonObject { ["id"] = exchanged.Int("id") } }) });
        var submitted = rt.CallObject("PUT", $"{G}/sbs/challenge/{firstExchange.Id}", new JsonObject());
        Assert.True(submitted["success"].B());
        Assert.Equal(unopenedBefore + 1, submitted["unopenedPacks"].L());
        Assert.Equal(1, store.UnopenedPackIds().Count(id => id == 100));
        var bronze = rt.CallObject("GET", $"{G}/store/purchasegroup/mypacks")["purchase"].Objects().First(p => p["id"].L() == 100);
        Assert.Equal(1, bronze["quantity"].L());
        Assert.True(bronze["packContentInfo"].JsonEquals(JsonNode.Parse("""{"itemQuantity":12,"goldQuantity":0,"silverQuantity":0,"bronzeQuantity":12,"rareQuantity":1}""")));
        // The store refuses a pack while Unassigned holds anything (Python server 328a5b1): the player deals with it first.
        store.DeletePile("unassigned");
        var opened = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 100, ["useCredits"] = 0, ["usePreOrder"] = 1, ["currency"] = "PREORDER" });
        Assert.Equal(12, opened["itemList"].A().Count);
        Assert.Equal(creditsBefore, store.Credits());
        Assert.Equal(unopenedBefore, store.UnopenedPackIds().Count);
        rt.Call("PUT", $"{G}/sbs/challenge/{firstExchange.Id}", new JsonObject());
        Assert.Equal(unopenedBefore, store.UnopenedPackIds().Count);

        Assert.IsType<JsonObject>(rt.Call("GET", $"{G}/club/stats/summary"));

        var rarities = rt.CallObject("GET", "/fut/items/images/backgrounds/itemBGs/futitemraritytunables.json");
        var rarityIds = rarities["rarities"].Objects().Select(e => JsonValues.Int(e["id"], 0)).ToHashSet();
        var usedRarities = rt.Game.Players.Entries.Select(e => e.Value.IntKeep("rareflag")).ToHashSet();
        Assert.Equal(1, rarities["version"].L());
        Assert.Subset(rarityIds, new HashSet<long> { 0, 1, 999 });
        Assert.Subset(rarityIds, usedRarities);

        var opening = rt.CallObject("GET", "/fut/packs/packopening/packopeningsetting.json");
        Assert.All(opening["revealItemTimingList"].Objects(), p => Assert.Equal(14, p["timingSec"].A().Count));
        var byRarity = opening["itemList"].Objects().ToDictionary(e => JsonValues.Int(e["rarity"]), e => e);
        Assert.Equal(2, byRarity[3]["walkOutBucketId"].A()[^1].L());
        Assert.Equal(1, byRarity[61]["walkOutBucketId"].A()[^1].L());
        Assert.False(byRarity[3]["sparkJetsColor1"].A()[^1].JsonEquals(byRarity[61]["sparkJetsColor1"].A()[^1]));
        Assert.All(rarities["rarities"].Objects().Where(e => !JsonValues.TryInt(e.GetOr("type", 1), out var type) || type != 0), e => Assert.True(e.ContainsKey("shell")));

        var current = store.ListSquads()[0];
        var saved = rt.CallObject("PUT", $"{G}/squad/1", current);
        Assert.Equal(1, saved["id"].L());
        var persisted = store.ListSquads()[0];
        Assert.Equal(current.Text("squadName"), persisted.Text("squadName"));
        Assert.Equal(11, persisted.Arr("players")!.Count);
    }

    private static void PacksAndCustomisationScenario(TestRuntime rt)
    {
        var store = rt.Game.Store();
        var beforeCredits = store.Credits();
        store.DeletePile("unassigned");
        var purchased = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 90, ["useCredits"] = 1, ["usePreOrder"] = 0, ["currency"] = "COINS" });
        Assert.Equal("PURCHASECOMPLETE", purchased["state"].S());
        Assert.Equal("CARDPACK", purchased["purchasePackType"].S());
        Assert.True(purchased["transactionId"].L() > 0);
        Assert.Equal(90, purchased["createPackResponse"]!["purchasedPackId"].L());
        Assert.Equal(12, purchased["createPackResponse"]!["itemList"].A().Count);
        var packedManagers = purchased["itemList"].Objects().Where(i => i["itemType"]?.GetValue<string>() == "manager").ToList();
        Assert.Single(packedManagers);
        Assert.Equal(4, packedManagers[0]["cardsubtypeid"].L());
        Assert.True(rt.Game.Managers.Contains(packedManagers[0]["resourceId"].L()));
        Assert.All(purchased["itemList"].Objects().Where(i => i["itemType"]?.GetValue<string>() == "player"), i => Assert.True(rt.Game.Players.Contains(i["resourceId"].L())));
        Assert.Equal(beforeCredits - rt.Game.Packs.GetPackConfig(90).Int("price"), store.Credits());

        var unassigned = rt.CallObject("GET", $"{G}/purchased/items");
        Assert.Equal(12, unassigned["numberItems"].L());
        Assert.Equal(1, unassigned["itemData"].Objects().Count(i => i["itemType"]?.GetValue<string>() == "manager"));
        var packedManager = unassigned["itemData"].Objects().First(i => i["itemType"]?.GetValue<string>() == "manager");
        var storedManager = rt.CallObject("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = packedManager["id"].L(), ["pile"] = "club" }) });
        Assert.True(storedManager["itemData"].JsonEquals(new JsonArray(new JsonObject { ["id"] = packedManager["id"].L(), ["pile"] = "club", ["success"] = true })));
        var managersAfter = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "manager", ["start"] = 0, ["count"] = 91 });
        Assert.Contains(packedManager["id"].L(), managersAfter["itemData"].Objects().Select(i => i["id"].L()));

        var promoBefore = store.Credits();
        store.DeletePile("unassigned");
        var promo = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 92, ["useCredits"] = 1, ["usePreOrder"] = 0, ["currency"] = "COINS" });
        Assert.Equal(10, promo["itemList"].A().Count);
        Assert.All(promo["itemList"].Objects(), i => Assert.True(i["rating"].L() >= 82));
        Assert.True(promo["itemList"].Objects().Count(i => i["rareflag"].L() > 1) >= 1);
        Assert.Equal(promoBefore - 50000, store.Credits());

        store.DeletePile("unassigned");
        var summer = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 91, ["useCredits"] = 1, ["usePreOrder"] = 0, ["currency"] = "COINS" });
        var summerSpecials = summer["itemList"].Objects().Where(i => i["rareflag"].L() == 61).ToList();
        Assert.Equal(3, summerSpecials.Count);
        Assert.All(summerSpecials, i => Assert.Equal("Summer Stars", rt.Game.Players.Find(i["resourceId"].L())!.Text("rarityName")));

        var modifierPack = rt.Fut.Packs.CreatePack(93);
        var modifiers = modifierPack.Arr("itemData")!.Objects().ToList();
        Assert.Equal(20, modifiers.Count);
        Assert.True(modifiers.Select(i => i["resourceId"].L()).ToHashSet().SetEquals(rt.Game.Cards.PositionModifiers.Select(d => d.ResourceId)));
        Assert.All(modifiers, i => Assert.Equal("development", i["itemType"].S()));
        // A new club starts without consumables: they come from packs. A second modifier pack gives each
        // definition two stacks, and one of each chemistry style is owned for the style checks below.
        var secondModifiers = rt.Fut.Packs.CreatePack(93).Arr("itemData")!.Objects().ToList();
        foreach (var item in modifiers.Concat(secondModifiers))
        {
            Assert.True(rt.Fut.Inventory.UpdatePile(item["id"].L(), "club"));
        }
        rt.Game.InUnit(async w =>
        {
            foreach (var style in rt.Game.Cards.ChemistryStyles)
            {
                await rt.Fut.Items.MakeConsumableAsync(w, style.ResourceId);
            }
        });

        // Club > Consumables lists catalogue stacks: one row per definition, every owned copy summed
        // (one card from each modifier pack).
        var development = rt.CallObject("GET", $"{G}/club/consumables/development");
        var developmentRows = development["itemData"].Objects().ToList();
        var chemistryStyleCount = rt.Game.Cards.ChemistryStyles.Count;
        Assert.Equal(20 + chemistryStyleCount, developmentRows.Count);
        Assert.Equal(developmentRows.Count, development["total"].L());
        Assert.All(developmentRows, row => Assert.True(row.Select(e => e.Key).ToHashSet().SetEquals(new[] { "item", "resourceGameYear", "resourceId", "discardValue", "count", "untradeableCount" })));
        Assert.All(developmentRows, row => Assert.True(row["item"]!.AsObject().Select(e => e.Key).ToHashSet().SetEquals(new[]
        {
            "amount", "cardassetid", "cardsubtypeid", "pile", "rareflag", "rating", "resourceGameYear", "resourceId", "weightrare", "bronze", "silver", "gold",
        })));
        var modifierRows = developmentRows.Where(row => rt.Game.Cards.PositionModifier(row["resourceId"].L()) is not null).ToList();
        Assert.Equal(20, modifierRows.Count);
        Assert.All(modifierRows, row => Assert.True(row["count"].L() == 2 && row["untradeableCount"].L() == 0 && row["item"]!["cardassetid"].L() == 34));
        // A chemistry style's amount is its position group, which FIFA files it under (1 attackers, 2 midfielders, 3 defenders,
        // 4 every position, 5 goalkeepers), never the stack size: a stack of 3 would put every style under defenders.
        var styleRows = developmentRows.Where(row => rt.Game.Cards.ChemistryStyle(row["resourceId"].L()) is not null).ToList();
        Assert.Equal(chemistryStyleCount, styleRows.Count);
        Assert.All(styleRows, row => Assert.Equal(rt.Game.Cards.ChemistryStyle(row["resourceId"].L())!.PositionGroup, row["item"]!["amount"].L()));
        var expectedGroups = new Dictionary<string, long>
        {
            ["Basic"] = 4, ["Sniper"] = 1, ["Hawk"] = 1, ["Artist"] = 2, ["Engine"] = 2, ["Sentinel"] = 3, ["Anchor"] = 3,
            ["Hunter"] = 4, ["Catalyst"] = 4, ["Shadow"] = 4, ["Wall"] = 5, ["GK Basic"] = 5,
        };
        Assert.All(expectedGroups, pair => Assert.Equal(pair.Value, rt.Game.Cards.ChemistryStyles.Single(style => style.Name == pair.Key).PositionGroup));
        var ownedStyle = store.ListItems().First(item => rt.Game.Cards.ChemistryStyle(item.Int("resourceId")) is not null);
        var nativeStyle = rt.Fut.Native.PackItem(ownedStyle);
        Assert.Equal(rt.Game.Cards.ChemistryStyle(ownedStyle.Int("resourceId"))!.PositionGroup, nativeStyle.Int("amount"));
        Assert.Equal(ownedStyle.Int("stackCount"), nativeStyle.Int("stackCount"));
        var training = rt.CallObject("GET", $"{G}/club/consumables/training");
        Assert.Equal(chemistryStyleCount, training["itemData"].A().Count);
        var consumableStats = rt.CallObject("GET", $"{G}/club/stats/consumables");
        // FIFA's native stats shape: CardsDLL's stats parser needs a known category (without one it indexes its stats table
        // with -1 and the game crashes) and reads each counter from the stat list.
        Assert.Equal(new[] { "category", "stat" }, consumableStats.Select(e => e.Key));
        Assert.Equal("consumables", consumableStats["category"].S());
        var consumableStat = consumableStats["stat"].Objects().ToDictionary(stat => stat["type"].S(), stat => stat["typeValue"].L());
        Assert.All(consumableStats["stat"].Objects(), stat => Assert.True(stat.Select(e => e.Key).SequenceEqual(new[] { "type", "typeValue", "contextId", "contextValue" })));
        Assert.Equal(20 * 2, consumableStat["consumablesPosition"]);
        Assert.Equal(chemistryStyleCount, consumableStat["consumablesTrainingPlayerPlayStyle"] + consumableStat["consumablesTrainingGkPlayStyle"]);
        Assert.Equal(0, consumableStat["consumablesTraining"]);
        Assert.Equal(consumableStat["consumablesPosition"] + consumableStat["consumablesTrainingPlayerPlayStyle"] + consumableStat["consumablesTrainingGkPlayStyle"], consumableStat["consumables"]);

        var target = store.ListItems().First(item => item.Text("itemType", "player") == "player"
            && rt.Game.Cards.PositionModifiers.Any(d => d.Source == item.Text("preferredPosition")));
        var modifier = modifiers.First(i => rt.Game.Cards.PositionModifier(i["resourceId"].L())!.Source == target.Text("preferredPosition"));
        var expectedPosition = rt.Game.Cards.PositionModifier(modifier["resourceId"].L())!.Target;
        var (applyStatus, applied, _) = rt.CallRaw("POST", $"{G}/item/resource/{modifier["resourceId"].L()}", new JsonObject { ["apply"] = new JsonArray(new JsonObject { ["id"] = target.Int("id") }) });
        Assert.Equal(200, applyStatus);
        Assert.Equal(new[] { "itemData" }, applied.AsObject().Select(e => e.Key));
        Assert.Equal(expectedPosition, store.GetItem(target.Int("id"))!.Text("preferredPosition"));
        // One card comes off the oldest club stack (the first pack's card); the second pack's card is untouched.
        var newer = secondModifiers.Single(i => i["resourceId"].L() == modifier["resourceId"].L());
        var stacks = store.ItemsWithResource(modifier["resourceId"].L()).Select(row => store.GetItem(row.Id)!).ToList();
        Assert.Equal(1, stacks.Sum(item => item.Int("stackCount")));
        Assert.Null(store.GetItem(modifier["id"].L()));
        Assert.NotNull(store.GetItem(newer["id"].L()));

        // A chemistry style sets the player's playStyle; goalkeeper styles are refused for outfield players.
        var outfieldStyle = rt.Game.Cards.ChemistryStyles.First(style => !style.Goalkeeper && style.PlayStyle != 250);
        var goalkeeperStyle = rt.Game.Cards.ChemistryStyles.First(style => style.Goalkeeper);
        var (styleStatus, _, _) = rt.CallRaw("PUT", $"{G}/item/resource/{outfieldStyle.ResourceId}", new JsonObject { ["applyTo"] = target.Int("id") });
        Assert.Equal(200, styleStatus);
        Assert.Equal(outfieldStyle.PlayStyle, store.GetItem(target.Int("id"))!.Int("playStyle"));
        var (refusedStatus, refused, _) = rt.CallRaw("POST", $"{G}/item/resource/{goalkeeperStyle.ResourceId}", new JsonObject { ["applyTo"] = target.Int("id") });
        Assert.Equal(400, refusedStatus);
        Assert.Equal("goalkeeper styles apply only to goalkeepers", refused["reason"].S());

        var kitPack = rt.Fut.Packs.CreatePack(96);
        var kits = kitPack.Arr("itemData")!.Objects().ToList();
        Assert.Equal(30, kits.Count);
        Assert.Equal(30, kits.Select(i => i["resourceId"].L()).Distinct().Count());
        Assert.All(kits, i => Assert.True(i["itemType"].S() == "kit" && i["cardsubtypeid"].L() == 9));
        var equippedKit = kits[0];
        Assert.True(rt.Fut.Inventory.UpdatePile(equippedKit["id"].L(), "club"));
        var equipResult = rt.CallObject("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = equippedKit["id"].L(), ["pile"] = "club", ["itemState"] = "activeHomeKit" }) });
        Assert.True(equipResult["itemData"]![0]!["success"].B());
        Assert.Contains(rt.Fut.Club.ActiveClubItems(), item => item.Int("id") == equippedKit["id"].L() && item.Text("itemState") == "activeHomeKit");

        var grant = rt.CallObject("POST", "/ultimateteam/customisation/grant-all", new JsonObject());
        Assert.True(grant["success"].B());
        Assert.Equal(rt.Game.Kits.Count, grant["available"]!["kits"].L());
        Assert.Equal(rt.Game.Stadiums.Count, grant["available"]!["stadiums"].L());
        var ownedKitResources = store.ListItems("club").Where(i => i.Text("itemType") == "kit").Select(i => i.Int("resourceId")).ToHashSet();
        Assert.Subset(ownedKitResources, rt.Game.Kits.Kits.Select(k => k.Int("resourceId")).ToHashSet());
        Assert.Equal(0, rt.CallObject("POST", "/ultimateteam/customisation/grant-all", new JsonObject())["grantedCount"].L());
        foreach (var (itemType, itemState) in new[] { ("custom", "activeBadge"), ("ball", "activeBall") })
        {
            var cosmetic = store.ListItems("club").First(i => i.Text("itemType") == itemType);
            var equipped = rt.CallObject("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = cosmetic.Int("id"), ["pile"] = "club", ["itemState"] = itemState }) });
            Assert.True(equipped["itemData"].JsonEquals(new JsonArray(new JsonObject { ["id"] = cosmetic.Int("id"), ["pile"] = "club", ["itemState"] = itemState, ["success"] = true })));
        }
    }
}
