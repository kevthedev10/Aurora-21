using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Core.Web.Routes;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Blaze;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>Squads, FIFA Points, objectives, Draft runs, concept cards, club search, leaderboards and SBC art.</summary>
public sealed class ClubFeatureTests
{
    /// <summary>The store's Bronze Pack (migration 095), sold for coins and FIFA Points.</summary>
    private const long BronzePack = 70;

    private const string G = "/ut/game/fifa21";

    [Fact]
    public void SquadsCanBeCreatedSwitchedTacticsSavedAndDeleted()
    {
        using var rt = TestRuntime.Create();
        var store = rt.Game.Store();

        var created = rt.CallObject("POST", $"{G}/squad", new JsonObject { ["squadName"] = "A squad name that is far too long to keep" });
        var newId = created["id"].L();
        Assert.Equal(2, newId);
        Assert.Equal("A squad name that is far too l", store.GetSquad(newId)!.Text("squadName"));
        Assert.False(store.GetSquad(newId)!.Bool("active"));
        Assert.Equal(new long[] { 1, 2 }, rt.CallObject("GET", $"{G}/squad/list")["squad"].Objects().Select(s => s["id"].L()).Order());
        Assert.Equal(3, rt.CallObject("PUT", $"{G}/squad/0", new JsonObject())["id"].L());
        Assert.Equal("New Squad", rt.CallObject("GET", $"{G}/squad/0")["squadName"].S());
        Assert.Equal(404, rt.CallRaw("GET", $"{G}/squad/77").Status);

        // Saving squad 1 again keeps squad 1 active; saving the new squad does not steal the flag.
        rt.Call("PUT", $"{G}/squad/1", store.GetSquad(1)!.Also(s => s.Remove("active")));
        rt.Call("PUT", $"{G}/squad/{newId}", store.GetSquad(newId)!.Also(s => s.Remove("active")));
        Assert.Equal(1, store.ActiveSquadId());

        // Playing with a squad makes it active.
        var squadOne = store.GetSquad(1)!;
        squadOne["players"] = squadOne["players"]!.DeepClone();
        rt.Call("PUT", $"{G}/squad/{newId}", squadOne.Also(s => { s.Remove("active"); s["id"] = newId; }));
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = newId, ["sqbtEventId"] = 1 });
        Assert.Equal(newId, store.ActiveSquadId());

        // Any squad can be deleted through both delete forms.
        Assert.DoesNotContain(3L, rt.CallObject("DELETE", $"{G}/squad/3")["squad"].Objects().Select(s => s["id"].L()));
        Assert.Equal(newId, store.ActiveSquadId());

        // Custom tactics are read back from the squad document in mentality order; unknown mentalities are ignored.
        rt.Call("POST", $"{G}/tactics?squadId={newId}", new JsonObject
        {
            ["tactics"] = new JsonArray(
                new JsonObject { ["tactic"] = "ATTACKING", ["width"] = 60 },
                new JsonObject { ["tactic"] = "BALANCED", ["width"] = 50 },
                new JsonObject { ["tactic"] = "RECKLESS", ["width"] = 99 }),
        });
        var tactics = rt.CallObject("GET", $"{G}/squad/active")["tactics"].Objects().ToList();
        Assert.Equal(new[] { "BALANCED", "ATTACKING" }, tactics.Select(t => t["tactic"].S()));
        Assert.False(rt.CallObject("GET", $"{G}/squad/1").ContainsKey("tactics"));
    }

    [Fact]
    public void ActiveSquadCanBeDeletedBeforeTheNextSquadIsSaved()
    {
        using var rt = TestRuntime.Create();
        var store = rt.Game.Store();
        var second = rt.CallObject("POST", $"{G}/squad", new JsonObject { ["squadName"] = "Second" })["id"].L();
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = second, ["sqbtEventId"] = 1 });
        Assert.Equal(second, store.ActiveSquadId());

        // FIFA's order (live 10-05): fetch the squad it switches to, delete the active squad, then save the new one.
        rt.Call("GET", $"{G}/squad/1");
        var list = rt.CallObject("GET", $"/ut/delete/game/fifa21/squad/{second}")["squad"].Objects().ToList();
        Assert.Equal(new long[] { 1 }, list.Select(s => s["id"].L()));
        Assert.Equal(1, store.ActiveSquadId());
        Assert.True(store.GetSquad(1)!.Bool("active"));
        rt.Call("PUT", $"{G}/squad/1", store.GetSquad(1)!.Also(s => s.Remove("active")));
        Assert.Equal(1, store.ActiveSquadId());

        // The last squad stays; an unknown squad answers the list unchanged.
        var refused = rt.CallRaw("GET", "/ut/delete/game/fifa21/squad/1");
        Assert.Equal(400, refused.Status);
        Assert.Equal("LAST_SQUAD_CANNOT_BE_DELETED", refused.Body["reason"].S());
        Assert.Single(rt.CallObject("GET", "/ut/delete/game/fifa21/squad/77")["squad"].Objects());
    }

    [Fact]
    public void SavedSetPieceTakersAndCaptainAreKept()
    {
        using var rt = TestRuntime.Create();
        var squad = rt.CallObject("GET", $"{G}/squad/active");
        var ids = squad["players"].Objects().Where(s => s["itemData"].IsObject()).Select(s => s["itemData"]!["id"].L()).ToList();
        var saved = rt.Game.Store().GetSquad(1)!;
        saved["captain"] = ids[7];
        saved["kicktakers"] = new JsonArray(new JsonObject { ["index"] = 2, ["id"] = ids[9] }, new JsonObject { ["index"] = 4, ["id"] = 123 });
        rt.Call("PUT", $"{G}/squad/1", saved);

        var reloaded = rt.CallObject("GET", $"{G}/squad/active");
        Assert.Equal(ids[7], reloaded["captain"].L());
        Assert.Equal(new[] { ids[0], ids[1], ids[9], ids[3], ids[4] }, reloaded["kicktakers"].Objects().Select(k => k["id"].L()));
    }

    [Fact]
    public void TheSquadServesFifasOwnChemistryOverTheSavedNumber()
    {
        using var rt = TestRuntime.Create();
        var saved = rt.Game.Store().GetSquad(1)!;
        saved["chemistry"] = 1;
        rt.Call("PUT", $"{G}/squad/1", saved);

        var squad = rt.CallObject("GET", $"{G}/squad/active");
        var slots = squad["players"].Objects().ToDictionary(slot => slot["index"].L(), slot => slot["itemData"] as JsonObject);
        var starters = Enumerable.Range(0, 11).Select(index => slots.GetValueOrDefault(index)).ToList();
        var expected = rt.Game.Chemistry.SquadChemistry(squad["formation"]!.GetValue<string>(), starters, squad["manager"].Objects().FirstOrDefault());

        // The starter squad's own chemistry is not the saved 1, so the number served is the computed one.
        Assert.True(expected is { } chemistry && chemistry != 1);
        Assert.Equal(expected!.Value, squad["chemistry"].L());
    }

    [Fact]
    public void ClubSearchLeavesOutTheCardsTheSquadScreenExcludes()
    {
        using var rt = TestRuntime.Create();
        List<long> Search(string extra) => rt.CallObject("GET", $"{G}/club?type=player&count=200{extra}")["itemData"].Objects().Select(item => item["id"].L()).ToList();
        var all = Search("");
        var excluded = all.Take(2).ToList();

        var remaining = Search($"&exclitem={string.Join(",", excluded)}");

        // Paging runs after the filter: the page moves up by the two excluded cards.
        Assert.DoesNotContain(remaining, excluded.Contains);
        Assert.Equal(all.Skip(2), remaining.Take(all.Count - 2));
    }

    [Fact]
    public void CopyingASquadCreatesAnInactiveCopyWithTheSamePlayers()
    {
        using var rt = TestRuntime.Create();
        var original = rt.CallObject("GET", $"{G}/squad/1");

        var copy = rt.CallObject("POST", $"{G}/squad/1/clone");

        var copyId = copy["id"].L();
        Assert.NotEqual(1, copyId);
        Assert.Equal(original["squadName"]!.GetValue<string>() + rt.Game.Rules.Club.Squad.CopyNameSuffix, copy["squadName"]!.GetValue<string>());
        Assert.Equal(original["players"]!.ToJsonString(), copy["players"]!.ToJsonString());
        Assert.Equal(1, rt.CallObject("GET", $"{G}/squad/active")["id"].L());
        Assert.Contains(copyId, rt.CallObject("GET", $"{G}/squad/list")["squad"].Objects().Select(summary => summary["id"].L()));
        Assert.Equal(404, rt.CallRaw("POST", $"{G}/squad/99/clone").Status);
    }

    /// <summary>FIFA 21's "Copy squad" posts {srcId, dstSquadName} to /squad/clone; the copy takes the name the player gave it.</summary>
    [Fact]
    public void CopySquadThroughTheClientsCloneRouteUsesTheGivenName()
    {
        using var rt = TestRuntime.Create();
        var original = rt.CallObject("GET", $"{G}/squad/1");
        var (status, _, response) = rt.CallRaw("POST", $"{G}/squad/clone", new JsonObject { ["srcId"] = 1, ["dstSquadName"] = "  COPY Resistenci " });
        Assert.Equal(200, status);
        Assert.DoesNotContain(response.Headers, header => header.Key == "X-UltimateTeam-Unknown");
        var copy = rt.CallObject("GET", $"{G}/squad/list")["squad"].Objects().Single(summary => summary["id"].L() != 1);
        Assert.Equal("COPY Resistenci", copy["squadName"]!.GetValue<string>());
        Assert.Equal(original["players"]!.ToJsonString(), rt.CallObject("GET", $"{G}/squad/{copy["id"].L()}")["players"]!.ToJsonString());
        Assert.Equal(404, rt.CallRaw("POST", $"{G}/squad/clone", new JsonObject { ["srcId"] = 99, ["dstSquadName"] = "x" }).Status);
        Assert.Equal(404, rt.CallRaw("POST", $"{G}/squad/clone", new JsonObject()).Status);
    }

    /// <summary>
    /// The online kit-select screen's user/list: the caller's own club summary in the "user" array CardsDLL parses
    /// (personaId, clubName, clubAbbr, established, badge, kits); personas the server cannot name are left out.
    /// </summary>
    [Fact]
    public void TheUserListNamesTheCallersClubAndSkipsUnknownPersonas()
    {
        using var rt = TestRuntime.Create();
        var persona = rt.Game.Config.PersonaId;
        var (status, body, response) = rt.CallRaw("GET", $"{G}/user/list?personaIdList={persona},123456789&includeFutChampionsRecord=1");
        Assert.Equal(200, status);
        Assert.DoesNotContain(response.Headers, header => header.Key == "X-UltimateTeam-Unknown");
        var user = Assert.Single(body["user"]!.AsArray().OfType<JsonObject>());
        Assert.Equal(persona, user["personaId"].L());
        Assert.Equal(rt.Game.Config.ClubName, user["clubName"]!.GetValue<string>());
        Assert.Equal(rt.Game.Config.ClubAbbr, user["clubAbbr"]!.GetValue<string>());
        Assert.Equal(rt.Game.Established.ToString(System.Globalization.CultureInfo.InvariantCulture), user["established"]!.GetValue<string>());
        Assert.True(user["badge"]!["resourceId"].L() > 0);
        foreach (var kit in new[] { "homekit", "awaykit" })
        {
            Assert.True(user[kit]!["resourceId"].L() > 0, kit);
        }
        Assert.Equal(2, user["homekit"]!["categoryId"].L());
        Assert.Equal(3, user["awaykit"]!["categoryId"].L());
        Assert.Empty(rt.CallObject("GET", $"{G}/user/list")["user"]!.AsArray());
    }

    /// <summary>Logout is answered (not filed as unknown); a club delete is refused instead of the unknown answer's empty 200.</summary>
    [Fact]
    public void LogoutIsAnsweredAndAClubDeleteIsRefused()
    {
        using var rt = TestRuntime.Create();
        var (status, _, response) = rt.CallRaw("POST", "/ut/delete/auth");
        Assert.Equal(200, status);
        Assert.DoesNotContain(response.Headers, header => header.Key == "X-UltimateTeam-Unknown");
        Assert.Equal(403, rt.CallRaw("POST", "/ut/delete/game/fifa21/user").Status);
        Assert.Equal(1, rt.CallObject("GET", $"{G}/squad/active")["id"].L());
    }

    [Fact]
    public void TheFriendliesHubListsTheNineGameTypes()
    {
        using var rt = TestRuntime.Create();
        var hub = rt.CallObject("GET", $"{G}/houserules/user/hub?subType=BASIC");
        // CardsDLL reads lastPlayedFriend/lastPlayedOnlineOpponent as objects; a scalar there made its parser swallow the
        // closing brace and spin forever (the Friendlies freeze). EA's own sample sends houseRules alone.
        Assert.Equal(new[] { "houseRules" }, hub.Select(member => member.Key));
        var rules = hub["houseRules"].Objects().ToList();
        var basic = rules.Where(rule => rule["subType"]!.GetValue<string>() == "BASIC").ToList();
        Assert.Equal(new long[] { 0, 1, 5, 2, 4, 8, 9, 7, 6 }, basic.Select(rule => rule["houseRuleId"].L()));
        Assert.All(basic, rule => Assert.Equal($"FUT_HR_GAMETYPE_{rule["houseRuleId"].L()}", rule["title"]!.GetValue<string>()));
        // FIFA opens Friendlies with DYNAMIC alone; an empty list froze the screen and BASIC alone left it blank.
        var dynamic = rt.CallObject("GET", $"{G}/houserules/user/hub?subType=DYNAMIC")["houseRules"].Objects();
        Assert.Equal(rules.Select(rule => rule["houseRuleId"].L()), dynamic.Select(rule => rule["houseRuleId"].L()));
    }

    [Fact]
    public void LiveFriendliesTileArtIsATexture()
    {
        using var rt = TestRuntime.Create();
        // The catch-all answered these 200 "{}"; FIFA asks for each event's imageId right after the hub.
        var response = rt.Router.Handle(new FutRequest("GET", "/fut/houserules/background/background_100.dds", Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
        Assert.Equal(200, response.Status);
        var body = response.Body;
        Assert.Equal("DDS "u8.ToArray(), body[..4]);
        Assert.Equal("DXT1"u8.ToArray(), body[84..88]);
        Assert.Equal(128 + (512 / 4) * (256 / 4) * 8, body.Length);
    }

    [Fact]
    public void LiveFriendliesEventsRunTheCurrentWeek()
    {
        // Wednesday 2026-10-07 12:00 UTC: the week runs Monday 10-05 00:00 to Monday 10-12 00:00.
        var live = ClubRoutes.HouseRulesHub(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero))["houseRules"].Objects()
            .Where(rule => rule["subType"]!.GetValue<string>() == "DYNAMIC").ToList();
        Assert.Equal(new long[] { 100, 101, 102 }, live.Select(rule => rule["houseRuleId"].L()));
        Assert.All(live, rule =>
        {
            Assert.Equal("ENABLED", rule["state"]!.GetValue<string>());
            Assert.Equal(1791158400, rule["startTime"].L());
            Assert.Equal(1791763200, rule["endTime"].L());
        });
    }

    [Fact]
    public void RenamingASquadKeepsItsPlayersFormationAndCaptain()
    {
        using var rt = TestRuntime.Create();
        var before = rt.CallObject("GET", $"{G}/squad/1");

        // FIFA renames a squad with only its id and the new name.
        rt.Call("PUT", $"{G}/squad/1", new JsonObject { ["id"] = 1, ["squadName"] = "Renamed" });

        var after = rt.CallObject("GET", $"{G}/squad/1");
        Assert.Equal("Renamed", after["squadName"]!.GetValue<string>());
        foreach (var key in new[] { "players", "formation", "captain", "kicktakers", "manager" })
        {
            Assert.Equal(before[key]!.ToJsonString(), after[key]!.ToJsonString());
        }
        Assert.Equal(1, rt.CallObject("GET", $"{G}/squad/active")["id"].L());
    }

    [Fact]
    public void TheSquadListAndUserInfoShowEachSquadsComputedChemistry()
    {
        using var rt = TestRuntime.Create();
        // A second squad whose saved number is stale, as after a card in it changed outside the squad screen.
        var copy = rt.Game.Store().GetSquad(1)!.DeepClone().AsObject();
        var id = rt.CallObject("POST", $"{G}/squad", new JsonObject { ["squadName"] = "Copy" }).Int("id");
        copy["id"] = id; copy["squadId"] = id; copy["squadName"] = "Copy"; copy["chemistry"] = 1; copy.Remove("active");
        rt.Call("PUT", $"{G}/squad/{id}", copy);
        var onScreen = rt.CallObject("GET", $"{G}/squad/{id}")["chemistry"].L();

        var listed = rt.CallObject("GET", $"{G}/squad/list")["squad"].Objects().Single(summary => summary["id"].L() == id);
        var atLogin = rt.CallObject("GET", $"{G}/user")["squadList"]!["squad"].Objects().Single(summary => summary["id"].L() == id);

        Assert.NotEqual(1, onScreen);
        Assert.Equal(onScreen, listed["chemistry"].L());
        Assert.Equal(onScreen, atLogin["chemistry"].L());
    }

    [Fact]
    public void FifaPointsBuyNothingAndThereIsNoDailyDrop()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000));
        rt.Game.Rules.Club.FifaPointsPurchases = false; // Explicit disabled-policy coverage; the default is now enabled.
        var store = rt.Game.Store();
        Assert.False(rt.Game.Rules.Club.FifaPointsPurchases);
        Assert.Equal(0, store.FifaPoints());

        // The store lists coins only, and no mode can claim a FIFA Points drop.
        var tiles = rt.CallObject("GET", $"{G}/store/purchasegroup/all")["purchase"].Objects().ToList();
        var bronze = tiles.First(p => p["id"].L() == BronzePack);
        Assert.Equal(1, bronze["assetId"].L());
        Assert.Equal(new[] { "coins" }, bronze["currencies"].Objects().Select(currency => currency["name"].S()));
        // No tile offers points, and a pack sold only for points (the ported pack 50) is not listed at all.
        Assert.All(tiles.Where(tile => tile["currencies"] is JsonArray), tile => Assert.DoesNotContain(tile["currencies"].Objects(),
            currency => currency["name"].S() == "points" && tile["packType"]?.GetValue<string>() != "DRAFTENTRY"));
        if (rt.Game.Packs.GetPackConfig(50) is { Count: > 0 } pointsOnly && PackRules.AllowedCurrencies(pointsOnly).SequenceEqual(new[] { "points" }))
        {
            Assert.DoesNotContain(tiles, tile => tile["id"].L() == 50);
        }
        foreach (var (method, path) in new[] { ("GET", "/ultimateteam/daily-drop"), ("POST", "/ultimateteam/daily-drop"),
                     ("POST", "/ultimateteam/daily-drop?unlimited=1"), ("POST", "/ultimateteam/daily-drop/reset") })
        {
            // No route answers: the empty unknown-endpoint reply, and nothing is paid.
            var (_, body, response) = rt.CallRaw(method, path);
            Assert.Contains(response.Headers, header => header.Key == "X-UltimateTeam-Unknown");
            Assert.Empty(body.AsObject());
        }
        Assert.Equal(0, store.FifaPoints());

        // Staff can still grant FIFA Points, but the store refuses them and charges nothing.
        store.AddFifaPoints(1000);
        var creditsBefore = store.Credits();
        foreach (var request in new[] { new JsonObject { ["packId"] = BronzePack, ["usePoints"] = 1 }, new JsonObject { ["packId"] = BronzePack, ["currency"] = "POINTS" } })
        {
            Assert.Equal(400, rt.CallRaw("POST", $"{G}/purchased/items", request).Status);
        }
        Assert.Equal(1000, store.FifaPoints());
        Assert.Equal(creditsBefore, store.Credits());
        Assert.Equal(0, store.Objectives().Stat("packs"));

        var bought = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = BronzePack });
        Assert.Equal("PURCHASECOMPLETE", bought["state"].S());
        Assert.Equal(1000, store.FifaPoints());
    }

    [Fact]
    public void PromoPacksHonourSizeRatingAndOneVersionPerPlayer()
    {
        // Pack 95 copies the retired 84+ x50 pack (migration 041 pack 47; migration 097 removed it from the store).
        using var rt = TestRuntime.Create(seedSql: """
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,min_rating,max_rating,allowed_nations,manager_chance)
            VALUES(94,'store',true,'Synthetic nation pack',1,3,100,0,0,3,75,90,ARRAY[54]::bigint[],0);
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,
                category_chances,manager_chance,min_rating,store_group,guaranteed_specials,allow_unweighted_specials,always_present,display_only)
            VALUES(95,'store',true,'Synthetic 84+ x50 pack',1,50,100,0,0,50,'{"totw": 0.25}',0,84,'special',0,true,true,false)
            """);
        var fifty = rt.Fut.Packs.CreatePack(95).Arr("itemData")!.OfType<JsonObject>().ToList();
        Assert.Equal(50, fifty.Count);
        Assert.All(fifty, item => Assert.True(item.Int("rating") >= 84));
        var assets = fifty.Select(item => rt.Game.Players.Find(item.Int("resourceId"))!.IntOr("assetId", item.Int("resourceId"), item.Int("resourceId")));
        Assert.Equal(50, assets.Distinct().Count());

        const int nation = 54;
        var nationPack = rt.Fut.Packs.CreatePack(94).Arr("itemData")!.OfType<JsonObject>().ToList();
        Assert.Equal(3, nationPack.Count);
        Assert.All(nationPack, item => Assert.True(item.Int("nation") == nation && item.Int("rating") is >= 75 and <= 90));
    }

    [Fact]
    public void GrindObjectivesPayOnceAndLevelsGrantPacks()
    {
        using var rt = TestRuntime.Create();
        var store = rt.Game.Store();
        // Group 4, Collector, counts completed SBCs.
        var packGroup = rt.Game.Rules.Objectives.GrindGroups.First(g => g.Id == 4);

        var campaign = rt.CallObject("GET", $"{G}/scmp/campaign/active");
        var groups = campaign["categories"]![0]!["groups"].Objects().ToList();
        var rules = rt.Game.Rules.Objectives;
        var openGifts = rules.Gifts.Count(gift => rules.IsLive(gift.Id) && gift.IsOpen(rt.Game.NowSeconds));
        Assert.Equal((rules.IsLive(rules.WelcomeGroup.Id) ? 1 : 0) + rules.GrindGroups.Count(group => rules.IsLive(group.Id)) + openGifts, groups.Count);
        foreach (var disabled in rules.GrindGroups.Where(group => !rules.IsLive(group.Id)))
            Assert.DoesNotContain(groups, group => group["groupId"].L() == disabled.Id);
        Assert.All(groups, g => Assert.True(g["groupType"].L() == 3 && g["objectives"].A().Count > 0));
        // Only the levels up to the phase-1 reward cap are served.
        Assert.Equal(rt.Game.Rules.Objectives.SeasonProgress.ServedLevels, campaign["levels"].A().Count);
        Assert.Equal(10, rt.Game.Rules.Objectives.SeasonProgress.ServedLevels);

        for (var i = 0; i < 10; i++)
        {
            rt.Game.InUnit(rt.Fut.Objectives.RecordSbcCompletedAsync);
        }
        var objective = rt.CallObject("GET", $"{G}/scmp/active/category/1/details")["groupsList"].Objects()
            .First(g => g["groupId"].L() == 4)["objectives"].Objects().First(o => o["objectiveId"].L() == 401);
        Assert.Equal("COMPLETED", objective["state"].S());
        Assert.Equal(10, objective["multiplier"].L());
        Assert.Equal(10, objective["currentProgress"].L());
        Assert.False(objective.ContainsKey("progressCount"));

        var credits = store.Credits();
        var claimed = rt.CallObject("POST", $"{G}/scmp/campaign/group/4/objective/401/rewards");
        Assert.Single(claimed["awards"].A());
        Assert.Equal(credits + packGroup.Objectives[0].Award.Coins!.Value, store.Credits());
        Assert.True(rt.CallObject("POST", $"{G}/scmp/campaign/group/4/objective/401/rewards")["alreadyClaimed"].B());
        // The group pays only once every objective in it is claimed: before that the claim is refused, not answered empty.
        var early = rt.CallRaw("POST", $"{G}/scmp/campaign/group/4/rewards");
        Assert.Equal((409, "objective reward not earned yet"), (early.Status, early.Body["reason"].S()));
        Assert.Equal(credits + packGroup.Objectives[0].Award.Coins!.Value, store.Credits());

        // The welcome group delivers its three 82+ player picks to My Packs.
        var packsBefore = store.UnopenedPackIds().Count(id => id == 513);
        // The Welcome group is earned with one match.
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        rt.Call("POST", $"{G}/scmp/campaign/group/1/rewards");
        Assert.Equal(packsBefore + 3, store.UnopenedPackIds().Count(id => id == 513));

        // Level 2 is a pack reward (a Gold Pack) that goes to My Packs.
        store.AddObjectivesXp(2 * rt.Game.Rules.Objectives.SeasonProgress.XpPerLevel);
        var level = rt.CallObject("POST", $"{G}/scmp/campaign/level/2/reward/0");
        Assert.Equal(304, level["awards"]![0]!["packId"].L());
        Assert.Contains(304L, store.UnopenedPackIds());
    }

    [Fact]
    public void DraftRunsAreKeptPerModeAndOtherMatchesDoNotSettleThem()
    {
        using var rt = TestRuntime.Create();
        var store = rt.Game.Store();

        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("POST", $"{G}/purchase/mode/0/draft", new JsonObject { ["currency"] = "COINS" });
        Assert.Equal("PICK_DIFFICULTY", store.DraftRun(DraftSlot.SinglePlayer)!.Text("state"));
        Assert.Equal("FORMATION_DRAFT", store.DraftRun(DraftSlot.Online)!.Text("state"));

        // Offers are remembered: asking again does not re-roll them.
        rt.Call("GET", $"{G}/squad/mode/draft/choices/formation");
        rt.Call("PUT", $"{G}/squad/mode/0/draft/choose", new JsonObject { ["choiceIndex"] = 0 });
        var first = rt.Call("GET", $"{G}/squad/mode/draft/choices/captain")["choices"].Objects().Select(c => c["itemData"]!["resourceId"].L()).ToList();
        var again = rt.Call("GET", $"{G}/squad/mode/draft/choices/captain")["choices"].Objects().Select(c => c["itemData"]!["resourceId"].L()).ToList();
        Assert.Equal(first, again);

        var completed = rt.CallObject("POST", $"{G}/squad/mode/0/draft/autocomplete");
        Assert.Equal("READY_FOR_MATCH", completed["squadState"].S());
        Assert.Equal(900001, completed["squad"]!["id"].L());

        // A Squad Battles match (Professional, played to full time) ends without touching the Draft and pays the match reward.
        var credits = store.Credits();
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1, ["sqbtMatchDifficulty"] = 3 });
        rt.PlayedRealSeconds();
        var ended = rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "WIN",
            ["matchId"] = 7,
            ["secondsPlayed"] = 5461,
            ["myMatchStats"] = new JsonObject { ["goals"] = 3 },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
        });
        Assert.Equal(1, ended["gamesWon"].L());
        Assert.Equal(7, ended["matchId"].L());
        // Flat completion coins; performance statistics do not add coin rows.
        var earned = rt.Game.Rules.Seasons.MatchCompletionRewards["squadBattles"].Coins;
        Assert.Equal(earned, ended["matchCoins"].L());
        Assert.Equal(rt.Game.Rules.Seasons.MatchCompletionRewards["squadBattles"].Coins, ended["participationAward"].L());
        Assert.Empty(ended["matchCoinPartials"]!.AsArray());
        Assert.Equal(credits + earned, store.Credits());
        Assert.Equal("READY_FOR_MATCH", store.DraftRun(DraftSlot.Online)!.Text("state"));
        var stats = store.Objectives();
        Assert.Equal((1, 1, 3, 1), (stats.Stat("matches"), stats.Stat("wins"), stats.Stat("goals"), stats.Stat("cleanSheets")));

        // FIFA can retry the same report. The original response is replayed without paying or
        // counting objective progress twice, including after the database-backed claim exists.
        var replay = rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "WIN",
            ["matchId"] = "7",
            ["secondsPlayed"] = 5461,
            ["myMatchStats"] = new JsonObject { ["goals"] = 3 },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
        });
        // Objective deltas are delivered once; the persisted match fields replay unchanged.
        Assert.True(ended.ContainsKey("dynamicObjectivesUpdates"));
        var settled = ended.DeepClone().AsObject();
        settled.Remove("dynamicObjectivesUpdates");
        Assert.False(replay.ContainsKey("dynamicObjectivesUpdates"));
        Assert.True(JsonNode.DeepEquals(settled, replay));
        Assert.Equal(credits + earned, store.Credits());
        stats = store.Objectives();
        Assert.Equal((1, 1, 3, 1), (stats.Stat("matches"), stats.Stat("wins"), stats.Stat("goals"), stats.Stat("cleanSheets")));

        // A Draft match echoes the client's end reason and settles only its own run.
        var match = rt.CallObject("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001, ["type"] = "ONLINE" });
        Assert.Equal("DRAFT_SQUAD", match["squad"]!["squadType"].S());
        var quit = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "QUIT" });
        Assert.Equal("QUIT", quit["endReason"].S());
        Assert.Equal("READY_FOR_REWARDS", store.DraftRun(DraftSlot.Online)!.Text("state"));
        Assert.Equal("PICK_DIFFICULTY", store.DraftRun(DraftSlot.SinglePlayer)!.Text("state"));
        Assert.Equal(1, store.Objectives().Stat("matches"));

        // Buying a new online Draft pays out the unclaimed rewards first.
        var pending = store.DraftRun(DraftSlot.Online)!.Int("pendingCoinReward");
        credits = store.Credits();
        rt.Call("POST", $"{G}/purchase/mode/0/draft", new JsonObject { ["currency"] = "COINS" });
        Assert.Equal(credits + pending - rt.Game.Rules.Draft.Entry.Coins, store.Credits());
        Assert.Equal("FORMATION_DRAFT", store.DraftRun(DraftSlot.Online)!.Text("state"));
    }

    [Fact]
    public void MatchSettlementReplaySurvivesServerRestart()
    {
        var first = TestRuntime.Create();
        var created = first.CallObject("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1, ["sqbtMatchDifficulty"] = 3 });
        var matchId = created["matchId"].L();
        var credits = first.Game.Store().Credits();
        first.PlayedRealSeconds();
        var ended = first.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "WIN",
            ["secondsPlayed"] = 5461,
            ["myMatchStats"] = new JsonObject { ["goals"] = 2 },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = 1 },
        });
        var paid = first.Game.Store().Credits();
        // A 2-1 win pays the same completion coins as any other full match.
        Assert.Equal(credits + first.Game.Rules.Seasons.MatchCompletionRewards["squadBattles"].Coins, paid);
        var connection = first.Detach();

        using var restarted = TestRuntime.Create(connectionString: connection);
        var replay = restarted.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "DNF",
            ["matchId"] = matchId,
        });
        // Objective deltas are delivered once; the persisted match fields replay unchanged.
        Assert.True(ended.ContainsKey("dynamicObjectivesUpdates"));
        var settled = ended.DeepClone().AsObject();
        settled.Remove("dynamicObjectivesUpdates");
        Assert.False(replay.ContainsKey("dynamicObjectivesUpdates"));
        Assert.True(JsonNode.DeepEquals(settled, replay));
        Assert.Equal(paid, restarted.Game.Store().Credits());
        Assert.Equal(1, restarted.Game.Store().Objectives().Stat("matches"));
    }

    [Fact]
    public void ACancelledSearchEndsWithoutSettlingAMatch()
    {
        // FIFA follows CancelMatchmaking with a DNF match end without gid or match id (Python server 50243f5).
        using var rt = TestRuntime.Create();
        var credits = rt.Game.Store().Credits();
        var matches = rt.Game.Store().Objectives().Stat("matches");

        var ended = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "DNF", ["gid"] = 0 });

        Assert.Equal((0L, 0L, 0L, 0L), (ended.Int("matchCoins"), ended.Int("gamesPlayed"), ended.Int("gamesLost"), ended.Int("loss")));
        Assert.Equal(credits, ended.Int("credits"));
        Assert.Equal(credits, rt.Game.Store().Credits());
        Assert.Equal(matches, rt.Game.Store().Objectives().Stat("matches"));

        // A disconnect from a real game is still a settled loss, which pays the quitter nothing (B26).
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1, ["sqbtMatchDifficulty"] = 3 });
        var lost = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "DNF", ["gid"] = 42, ["secondsPlayed"] = 5461 });
        Assert.Equal((1L, 1L, 0L), (lost.Int("gamesPlayed"), lost.Int("gamesLost"), lost.Int("matchCoins")));
        Assert.Equal(credits, rt.Game.Store().Credits());
    }

    [Fact]
    public async Task ThePickedSquadManagerIsTheOneServedBack()
    {
        // Python server 5ae8c10: a saved manager reference ({"id", "dream"}) was skipped and the best manager served instead.
        using var rt = TestRuntime.Create();
        var picked = await rt.Game.RunAsync("managers", async work =>
        {
            await rt.Fut.Items.MakeManagerAsync(work, rt.Game.Managers.BestManagerId()!.Value, "club");
            var weaker = rt.Game.Managers.Entries.OrderBy(entry => entry.Value.Int("rating")).First().Key;
            var manager = await rt.Fut.Items.MakeManagerAsync(work, weaker, "club");
            var squad = (await work.ListSquadsAsync())[0];
            squad["manager"] = new JsonArray(new JsonObject { ["id"] = manager.Int("id"), ["dream"] = false });
            await work.SaveSquadAsync(squad.Int("id"), squad.Text("squadName"), true, squad);
            return manager.Int("id");
        }, CancellationToken.None);

        Assert.Equal(picked, rt.CallObject("GET", $"{G}/squad/active")["manager"]![0]!.AsObject().Int("id"));
    }

    [Fact]
    public void AQuitIsALossAndANoContestPaysNothing()
    {
        // Python server 5b8b918: a quit's 0-0 body was a draw (+500 coins); a no-contest paid a 500-coin draw. The quitter's
        // loss pays nothing (B26).
        using var rt = TestRuntime.Create();
        var credits = rt.Game.Store().Credits();

        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1, ["sqbtMatchDifficulty"] = 3 });
        var quit = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "QUIT", ["matchId"] = 501, ["secondsPlayed"] = 5461 });
        Assert.Equal((1L, 0L, 0L), (quit.Int("loss"), quit.Int("draw"), quit.Int("matchCoins")));
        Assert.Equal(credits, rt.Game.Store().Credits());

        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 });
        var called = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "NO_CONTEST", ["matchId"] = 502, ["secondsPlayed"] = 5461 });
        Assert.Equal((0L, 0L, 0L, 0L, 0L), (called.Int("matchCoins"), called.Int("won"), called.Int("draw"), called.Int("loss"), called.Int("gamesPlayed")));
        Assert.Equal(credits, rt.Game.Store().Credits());
        var user = rt.CallObject("GET", $"{G}/user");
        Assert.Equal((0L, 0L, 1L), (user.Int("won"), user.Int("draw"), user.Int("loss")));
    }

    [Fact]
    public void ConceptPlayersAreSearchableButCannotPlay()
    {
        using var rt = TestRuntime.Create();
        var search = rt.CallObject("GET", $"{G}/defid?type=playerforward&level=gold&count=5")["itemData"].Objects().ToList();
        Assert.Equal(5, search.Count);
        Assert.All(search, item => Assert.True(item["dream"].B() && item["concept"].B() && rt.Game.Cards.IsConceptItemId(item["id"].L())));
        Assert.All(search, item => Assert.True(rt.Game.Rules.Club.PositionInZone(item["preferredPosition"].S(), "attacker")));

        var concept = search[0];
        var squad = rt.Game.Store().GetSquad(1)!;
        squad.Arr("players")![0] = new JsonObject { ["index"] = 0, ["itemId"] = concept["id"].L() };
        rt.Call("PUT", $"{G}/squad/1", squad);
        var rendered = rt.CallObject("GET", $"{G}/squad/active")["players"]![0]!["itemData"]!;
        Assert.True(rendered["concept"].B());

        var refused = rt.CallRaw("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1 });
        Assert.Equal(400, refused.Status);
        Assert.Equal(471, refused.Body["code"].L());
    }

    [Fact]
    public void ClubSearchFiltersTradeabilityPositionGroupsAndEquippables()
    {
        // Pack 90 copies the retired All Consumables pack (migration 041 pack 8; migration 097 removed it from the store).
        using var rt = TestRuntime.Create(seedSql: """
            INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,
                manager_chance,store_group,all_consumables)
            VALUES(90,'store',true,'Synthetic all consumables fixture',1,20,100,0,0,3,0,'special',true)
            """);
        var store = rt.Game.Store();
        var player = store.ListItems("club").First(ClubInventoryServiceIsPlayer);
        player["untradeable"] = true;
        store.SaveItem(player);

        long Count(JsonObject body) => rt.CallObject("POST", $"{G}/club", body)["itemCount"].L();
        var all = Count(new JsonObject { ["type"] = "player" });
        var untradeable = Count(new JsonObject { ["type"] = "player", ["isUntradeable"] = "true" });
        var tradeable = Count(new JsonObject { ["type"] = "player", ["isUntradeable"] = "false" });
        Assert.True(untradeable >= 1);
        Assert.Equal(all, untradeable + tradeable);
        Assert.Equal(all, Count(new JsonObject { ["type"] = "player", ["nat"] = -1 }));

        var defenders = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "playerDefender" })["itemData"].Objects().ToList();
        Assert.NotEmpty(defenders);
        Assert.All(defenders, item => Assert.True(rt.Game.Rules.Club.PositionInZone(item["preferredPosition"].S(), "defense")));

        var named = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "player", ["defId"] = $"{player.Int("resourceId")},0,0" })["itemData"].Objects().ToList();
        Assert.Contains(player.Int("id"), named.Select(item => item["id"].L()));

        // The home kit slot picker offers other kits only, never the kit already in the slot.
        var homeKit = rt.Fut.Club.ActiveClubItems().First(item => item.Text("itemState") == "activeHomeKit");
        rt.Fut.Packs.CreatePack(90);
        var equippables = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "equippables", ["excldef"] = homeKit.Int("resourceId") })["itemData"].Objects().ToList();
        Assert.All(equippables, item => Assert.Equal("kit", item["itemType"].S()));
        Assert.DoesNotContain(homeKit.Int("resourceId"), equippables.Select(item => item["resourceId"].L()));
        // An empty (Crowds, Sidelines or Structure) slot offers no Match Day items: only the club's own vanity items.
        Assert.All(rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "equippables" })["itemData"].Objects(), item => Assert.Equal("vanity", item["itemType"].S()));
        // The picker names the slot's subtypes (vanitySubTypeIds, CardsDLL 0x2bf970): an empty TIFO slot (500) offers the
        // club's tifos only, never pitch trophies (508, live 10-02), which a new club does not own.
        var tifos = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "equippables", ["vanitySubTypeIds"] = new JsonArray(500) })["itemData"].Objects().ToList();
        Assert.NotEmpty(tifos);
        Assert.All(tifos, item => Assert.Equal(500, item["cardsubtypeid"].L()));
        Assert.Empty(rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "equippables", ["vanitySubTypeIds"] = new JsonArray(508) })["itemData"].Objects());
        // FIFA 21 PC's slot picker sends type vanity (live 10-03): the Stadium Paint slot (509) lists only base paints.
        var paints = rt.CallObject("POST", $"{G}/club", new JsonObject
        {
            ["year"] = "2021", ["vanitySubTypeIds"] = new JsonArray(509), ["type"] = "vanity", ["count"] = 9, ["level"] = "any", ["sortBy"] = "value", ["sort"] = "desc",
        })["itemData"].Objects().ToList();
        Assert.NotEmpty(paints);
        Assert.All(paints, item => Assert.Equal(509, item["cardsubtypeid"].L()));
    }

    [Fact]
    public void PlayersCarryStatRowsRarityGroupsAndChemistryDefaults()
    {
        using var rt = TestRuntime.Create();
        var item = rt.CallObject("GET", $"{G}/squad/active")["players"].Objects().First(s => s["itemData"].IsObject())["itemData"]!;
        Assert.Equal(5, item["statsList"].A().Count);
        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, item["lifetimeStats"].Objects().Select(r => r["index"].L()));
        Assert.Equal(item["preferredPosition"].S() == "GK" ? 273 : 250, item["playStyle"].L());
        if (rt.Game.Cards.RarityGroups(item["rareflag"].L()).Count > 0)
        {
            Assert.Equal(new long[] { 4 }, item["groups"].A().Select(g => g.L()));
        }
        var rare = rt.Game.Rules.Sbc.Requirements["RARE"];
        // FIFA 21 labels and counts PLAYER_RARITY (18) 1 as "Rare"; PLAYER_RARITY_GROUP (25) 4 reads "UEFA Champions League".
        Assert.Equal(18, (int)rare.EligibilityKey!.Value);
        Assert.Equal(1L, rare.EligibilityValue);
        foreach (var (id, _) in rt.Game.Players.Entries.Where(e => rt.Game.Rules.Cards.QuickSell.BaseCompetitionRareflags.Contains(e.Value.Rareflag)).Take(3))
        {
            var meta = rt.Game.Players.Find(id)!;
            Assert.Equal(rt.Game.Rules.Cards.QuickSell.BaseValue(rt.Game.Packs.Rules.TierFor(meta.Rating, meta.Tier), meta.Rating, rare: true),
                rt.Game.Cards.DiscardValue(id));
        }
    }

    [Fact]
    public void LeaderboardsRankClubsAndSbcTilesHaveArt()
    {
        using var rt = TestRuntime.Create();
        var options = rt.CallObject("GET", $"{G}/leaderboards/options");
        Assert.Equal("competitor", options["selection"]!["category"].S());
        Assert.Equal(30, options["permutations"].A().Count);
        Assert.Equal("/period/Weekly/category/competitor/view/TOP100", options["permutations"]![0]!["url"].S());
        Assert.All(options["options"].Objects(), group => Assert.Single(group[group["id"].S()].Objects(), item => item["default"].B()));

        // The ranked Skill Rating: a placed club's under the current rules (Python server 56145ed).
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(new RivalsRecord
        {
            RatingVersion = rt.Game.Rules.Seasons.Rivals.RatingVersion, SkillRating = 1234, Placed = true, Wins = 3, MatchesPlayed = 4,
            WeekDivision = 4, BestDivision = 4, Division = 4,
        }));
        var entries = rt.CallObject("GET", $"{G}/leaderboards/period/Weekly/category/rivals/view/TOP100")["entries"].Objects().ToList();
        var row = Assert.Single(entries);
        Assert.Equal(rt.Game.Config.DisplayName, row["persona"].S());
        Assert.Equal(1234, row["score"]!["value"].L());
        Assert.Equal(3, row["tiebreak"]!["value"].L());
        Assert.Equal("wins", row["tiebreak"]!["icon"].S());
        Assert.True(rt.CallObject("GET", $"{G}/champion/leaderboard").ContainsKey("entries"));
        // The Squad Battles board is the weekly competition's: empty until a club scores (SquadBattlesCompetitionTests).
        Assert.Empty(rt.CallObject("GET", $"{G}/sqbt/leaderboard")["entries"].A());

        var set = rt.Game.Sbc.Sets[0];
        var image = rt.Router.Handle(new Aurora21.UltimateTeam.Core.Web.FutRequest("GET", $"/fut/sbc/pc/sets/images/sbc_set_image_{1000000 + set.Id - 900000}.png", Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
        Assert.Equal(200, image.Status);
        Assert.Equal("image/png", image.ContentType);
        Assert.True(image.Body.AsSpan(1, 3).SequenceEqual("PNG"u8));
        var challenge = rt.Router.Handle(new Aurora21.UltimateTeam.Core.Web.FutRequest("GET", "/fut/sbc/pc/challenges/images/sbc_challenge_image_1000000.png", Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
        Assert.Equal(200, challenge.Status);
        Assert.Contains(challenge.Headers, h => h.Key == "Cache-Control" && h.Value == "no-store");
    }

    [Fact]
    public void IntroMarkersComeBackAtLogin()
    {
        using var rt = TestRuntime.Create();
        var entries = new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["key"] = 3, ["value"] = 1 }) };
        Assert.Empty(rt.CallObject("PUT", $"{G}/clientdata/rivals", entries));
        rt.Call("PUT", $"{G}/clientdata/tutorialpopups", entries.DeepClone());
        rt.Call("PUT", $"{G}/clientdata/userhubdata", new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["key"] = 1, ["value"] = 40 }) });

        var mass = rt.CallObject("GET", $"{G}/usermassinfo");
        Assert.Equal(3, mass["rivalsClientData"]!["entries"]![0]!["key"].L());
        Assert.Equal(3, mass["tutorialClientData"]!["entries"]![0]!["key"].L());
        Assert.Equal(new long[] { 13, 40 }, mass["userHubClientData"]!["entries"].Objects().Select(e => e["value"].L()));
        Assert.Equal("credits", mass.Last().Key);
    }

    [Fact]
    public void BlazeFiltersNamesAndServesStatsLeaderboards()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1_700_000_000));
        var responses = new BlazeResponses(rt.Fut);
        var request = new TdfWriter().ListStruct("TLST", new Action<TdfWriter>[]
        {
            w => w.Int("DIRT", 0).Str("UTXT", "My Squad"),
            w => w.Int("DIRT", 0).Str("UTXT", "Weekend XI"),
        }).ToArray();
        var reply = responses.Respond(Fire2Frame.Parse(Fire2Frame.Build(BlazeComponent.Util, UtilCommand.FilterForProfanity, 1, Fire2MessageType.Message, request)));
        Assert.Equal("Util.FilterForProfanity", reply.Label);
        Assert.Equal(request, reply.Payload);

        var periods = responses.Respond(Fire2Frame.Parse(Fire2Frame.Build(BlazeComponent.Stats, StatsCommand.GetPeriodIds, 1, Fire2MessageType.Message, Array.Empty<byte>())));
        Assert.Equal((1_700_000_000 - 1_262_304_000) / 86_400, TdfReader.FindInteger(periods.Payload, "DLY"));

        var board = responses.Respond(Fire2Frame.Parse(Fire2Frame.Build(BlazeComponent.Stats, StatsCommand.GetLeaderboard, 1, Fire2MessageType.Message, new TdfWriter().Str("NAME", "skill").Int("COUN", 10).ToArray())));
        Assert.Equal(rt.Game.Config.DisplayName, TdfReader.FindString(board.Payload, "NAME"));
        Assert.Equal(1, TdfReader.FindInteger(board.Payload, "RANK"));
    }

    [Fact]
    public void TheClientConfigSkipsTheHospitalitySettingsWait()
    {
        using var rt = TestRuntime.Create();
        var responses = new BlazeResponses(rt.Fut);
        var reply = responses.Respond(Fire2Frame.Parse(Fire2Frame.Build(BlazeComponent.Util, UtilCommand.FetchClientConfig, 1, Fire2MessageType.Message,
            new TdfWriter().Str("CFID", "OSDK_CORE").ToArray())));

        // The boot popup waits this many seconds (default 15) for a CMS login FIFA 21 never makes (Python server 70b22ca).
        Assert.Equal("0", TdfReader.FindStringMap(reply.Payload, "CONF")["PRAN_AUTH_WAIT_TIME"]);
    }

    private static bool ClubInventoryServiceIsPlayer(JsonObject item) => item.Text("itemType", "player") == "player";
}
