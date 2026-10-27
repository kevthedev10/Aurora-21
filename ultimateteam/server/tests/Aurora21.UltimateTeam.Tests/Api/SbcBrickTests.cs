using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Locked SBC squad slots (bricks), copied from retail (FUTBIN's FIFA 21 SBC pages). Captured EA responses encode them as
/// playerRequirements playerType "DEFAULT" (free) / "BRICK" (locked, empty) / "CUSTOM_BRICK" (locked, a card that counts
/// toward chemistry); CardsDLL's challenge parser only honours them when the challenge's "type" is BRICK_CHALLENGE or
/// CUSTOM_BRICK_CHALLENGE. Each challenge carries its retail formation, and only its free slots can be filled.
/// </summary>
public sealed class SbcBrickTests
{
    private const string G = "/ut/game/fifa21";

    private static SbcChallenge Challenge(TestRuntime rt, string key) => rt.Game.Sbc.Challenges.Single(c => c.Key == key);

    private static JsonObject[] Requirements(TestRuntime rt, SbcChallenge challenge) =>
        rt.CallObject("POST", $"{G}/sbs/challenge/{challenge.Id}")["playerRequirements"]!.AsArray().OfType<JsonObject>().ToArray();

    private static JsonObject Squad(TestRuntime rt, SbcChallenge challenge) =>
        rt.CallObject("POST", $"{G}/sbs/challenge/{challenge.Id}")["squad"]!.AsObject();

    private static JsonObject[] SquadPlayers(TestRuntime rt, SbcChallenge challenge) =>
        Squad(rt, challenge)["players"]!.AsArray().OfType<JsonObject>().ToArray();

    private static JsonObject Listed(TestRuntime rt, SbcChallenge challenge) =>
        rt.Fut.Sbc.ChallengesResponse(challenge.SetId)["challenges"]!.AsArray().OfType<JsonObject>().Single(entry => entry.Int("id") == challenge.Id);

    private static JsonObject SaveBody(params (int Index, long Id)[] slots) => new()
    {
        ["players"] = new JsonArray(slots.Select(slot => (JsonNode)new JsonObject { ["index"] = slot.Index, ["itemData"] = new JsonObject { ["id"] = slot.Id } }).ToArray()),
    };

    [Fact]
    public void TheFirstExchangeLocksTenEmptySlotsAndLeavesOneFree()
    {
        using var rt = TestRuntime.Create();
        var challenge = Challenge(rt, "CH_1");
        Assert.Equal("BRICK_CHALLENGE", Listed(rt, challenge).Text("type"));
        var requirements = Requirements(rt, challenge);
        Assert.Equal(11, requirements.Length);
        Assert.Equal(10, requirements.Count(entry => entry.Text("playerType") == "BRICK"));
        Assert.Equal("DEFAULT", requirements.Single(entry => entry.Int("index") == 10).Text("playerType"));

        // Empty locked slots: no card is pre-placed anywhere in the 23-slot buffer.
        var players = SquadPlayers(rt, challenge);
        Assert.Equal(23, players.Length);
        Assert.All(players, entry => Assert.Equal(0, JsonValues.Int(entry["itemData"]!["id"])));
    }

    [Fact]
    public void BrickSlotsMatchesRetailWithFourLeagueAndNationPlaceholders()
    {
        using var rt = TestRuntime.Create();
        var challenge = Challenge(rt, "CH_7");
        Assert.Equal("Brick Slots", challenge.Source.Text("name"));
        var listed = Listed(rt, challenge);
        Assert.Equal("CUSTOM_BRICK_CHALLENGE", listed.Text("type"));
        Assert.Equal("f4141", listed.Text("formation"));
        Assert.Equal("f4141", Squad(rt, challenge).Text("formation"));

        // FUTBIN's retail layout in FIFA's f4141 slot order (GK, RB, RCB, LCB, LB, CDM, RM, RCM, LCM, LM, ST):
        // CDM and ST free; RCB, LCB, RCM and LCM hold league/nation placeholders; the rest are empty locks.
        var types = Requirements(rt, challenge).ToDictionary(entry => entry.Int("index"), entry => entry.Text("playerType"));
        Assert.Equal(new long[] { 5, 10 }, types.Where(pair => pair.Value == "DEFAULT").Select(pair => pair.Key).Order());
        Assert.Equal(new long[] { 2, 3, 7, 8 }, types.Where(pair => pair.Value == "CUSTOM_BRICK").Select(pair => pair.Key).Order());
        Assert.Equal(new long[] { 0, 1, 4, 6, 9 }, types.Where(pair => pair.Value == "BRICK").Select(pair => pair.Key).Order());

        var expected = new Dictionary<long, (long League, long Nation)> { [2] = (14, 14), [3] = (13, 14), [7] = (61, 21), [8] = (60, 21) };
        var cards = SquadPlayers(rt, challenge).Where(entry => JsonValues.Int(entry["itemData"]!["id"]) != 0).ToList();
        Assert.Equal(4, cards.Count);
        var store = rt.Game.Store();
        Assert.All(cards, entry =>
        {
            var item = entry["itemData"]!.AsObject();
            var (league, nation) = expected[entry.Int("index")];
            Assert.Equal(league, item.Int("leagueId"));
            Assert.Equal(nation, item.Int("nation"));
            Assert.True(item.Int("id") >= rt.Game.Rules.Sbc.BrickItemIdBase, "a placeholder card sits in the brick id range");
            Assert.True(item.Bool("untradeable"));
            Assert.DoesNotContain(store.ListItems("club").Concat(store.ListItems("squad")), owned => owned.Int("id") == item.Int("id"));
        });
    }

    /// <summary>
    /// FIFA draws a placeholder from its slot's own elgReq (NATION_ID 10, LEAGUE_ID 11), not from the squad item: without
    /// them the four slots showed N/A, linked to nobody, and Brick Slots' chemistry 43 was out of reach (live 10-03).
    /// </summary>
    [Fact]
    public void BrickSlotsPlaceholdersCarryTheirLeagueAndNation()
    {
        using var rt = TestRuntime.Create();
        var challenge = Challenge(rt, "CH_7");
        var expected = new Dictionary<long, (long League, long Nation)> { [2] = (14, 14), [3] = (13, 14), [7] = (61, 21), [8] = (60, 21) };
        foreach (var entry in Requirements(rt, challenge))
        {
            var eligibility = entry["elgReq"]!.AsArray().OfType<JsonObject>().ToDictionary(e => e.Int("eligibilityKey"), e => e.Int("eligibilityValue"));
            if (expected.TryGetValue(entry.Int("index"), out var dummy))
            {
                Assert.Equal(new Dictionary<long, long> { [11] = dummy.League, [10] = dummy.Nation }, eligibility);
            }
            else
            {
                Assert.Empty(eligibility);
            }
        }
    }

    /// <summary>
    /// A retail solution: an English Premier League CDM links to both English placeholder CBs and a German Bundesliga ST to
    /// both German placeholder CMs. Two leagues, two nations and chemistry 48 (the model scores the ST's two nation links at
    /// the full factor anyway, so the Bundesliga side loses nothing to a League One German, who does not exist).
    /// </summary>
    [Fact]
    public async Task BrickSlotsCanBeCompletedThroughItsPlaceholders()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var challenge = Challenge(rt, "CH_7");
        long Mint(string position, Func<PlayerMeta, bool> wanted)
        {
            var resourceId = rt.Game.Players.Entries
                .Where(entry => !entry.Value.IsSpecial && !rt.Game.Cards.IsGrantOnly(entry.Key) && entry.Value.Position == position && wanted(entry.Value))
                .OrderByDescending(entry => entry.Value.Rating).First().Key;
            return rt.Fut.Items.MakePlayer(resourceId, "club").Int("id");
        }
        var cdm = Mint("CDM", meta => meta.Int("nation") == 14 && meta.Int("leagueId") == 13);
        var st = Mint("ST", meta => meta.Int("nation") == 21 && meta.Int("leagueId") == 19);
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody((5, cdm), (10, st))).Status);
        var submit = await rt.Fut.Sbc.SubmitAsync(challenge.Id);
        Assert.True(submit.Body!["status"]?.S() != "INVALID", "submit refused: " + submit.Body!["message"]?.S());
        Assert.Equal(new[] { "CH_7" }, rt.Game.Store().SbcCompleted());
    }

    /// <summary>
    /// FUTBIN's top retail solution for Brick Slots (analysis-scratch/futbin-2026-10-02/sets/sq-brick.html): Marsh (CDM,
    /// Premier League, England) and Kaya (ST, 3. Liga, Germany). FUTBIN shows them at chemistry 7 and 10, which is what
    /// FIFA's model gives with the four league/nation placeholders; the challenge needs 43.
    /// </summary>
    [Fact]
    public async Task FutbinsRetailBrickSlotsSolutionCompletes()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var challenge = Challenge(rt, "CH_7");
        var cdm = rt.Fut.Items.MakePlayer(241036, "club").Int("id");
        var st = rt.Fut.Items.MakePlayer(243445, "club").Int("id");
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody((5, cdm), (10, st))).Status);
        var submit = await rt.Fut.Sbc.SubmitAsync(challenge.Id);
        Assert.True(submit.Body!["status"]?.S() != "INVALID", "submit refused: " + submit.Body!["message"]?.S());
        Assert.Equal(new[] { "CH_7" }, rt.Game.Store().SbcCompleted());
    }

    /// <summary>
    /// The server must score the same squad the client is given. FIFA's chemistry walks the squad's items (CardsDLL 0x24b560
    /// never reads the per-slot requirements), so a CUSTOM_BRICK slot's served card is what the client scores: scoring the
    /// served squad with the server's own ChemistryModel must agree with the submit decision.
    /// </summary>
    [Fact]
    public async Task BrickSlotsSubmitAgreesWithTheChemistryOfTheSquadItServes()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var challenge = Challenge(rt, "CH_7");
        var cdmItem = rt.Fut.Items.MakePlayer(241036, "club");
        var stItem = rt.Fut.Items.MakePlayer(243445, "club");
        var squad = Squad(rt, challenge);
        var starters = new JsonObject?[11];
        foreach (var entry in squad["players"]!.AsArray().OfType<JsonObject>())
        {
            var index = entry.Int("index");
            if (index < 11 && JsonValues.Int(entry["itemData"]!["id"]) != 0) starters[index] = entry["itemData"]!.AsObject();
        }
        starters[5] = rt.Fut.Native.PlayerItem(cdmItem);
        starters[10] = rt.Fut.Native.PlayerItem(stItem);
        var servedChemistry = rt.Game.Chemistry.SquadChemistry(squad.Text("formation"), starters, null);
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody((5, cdmItem.Int("id")), (10, stItem.Int("id")))).Status);
        var submit = await rt.Fut.Sbc.SubmitAsync(challenge.Id);
        var accepted = submit.Body!["status"]?.S() != "INVALID";
        Assert.True(accepted == servedChemistry >= 43,
            $"served squad scores {servedChemistry} (needs 43) but submit accepted={accepted}: {submit.Body!["message"]?.S()}");
    }

    /// <summary>
    /// Each Brick Slots placeholder is served as a card of its slot's league and nation, playing in its slot's base
    /// position (the side variants read the fit table as the central position), and its elgReq names that same league and
    /// nation, so FutPlayerItemSmall's placeholder text (drawn from the served card where an id is set) shows them.
    /// </summary>
    [Fact]
    public void BrickSlotsPlaceholdersAreServedInTheirSlotsPosition()
    {
        using var rt = TestRuntime.Create();
        var challenge = Challenge(rt, "CH_7");
        var expected = new Dictionary<long, string> { [2] = "CB", [3] = "CB", [7] = "CM", [8] = "CM" };
        var requirements = Requirements(rt, challenge).ToDictionary(entry => entry.Int("index"));
        var cards = SquadPlayers(rt, challenge).Where(entry => JsonValues.Int(entry["itemData"]!["id"]) != 0).ToList();
        Assert.Equal(expected.Keys.Order(), cards.Select(entry => entry.Int("index")).Order());
        Assert.All(cards, entry =>
        {
            var item = entry["itemData"]!.AsObject();
            Assert.Equal(expected[entry.Int("index")], item.Text("preferredPosition"));
            var eligibility = requirements[entry.Int("index")]["elgReq"]!.AsArray().OfType<JsonObject>()
                .ToDictionary(e => e.Int("eligibilityKey"), e => e.Int("eligibilityValue"));
            Assert.Equal(item.Int("leagueId"), eligibility[11]);
            Assert.Equal(item.Int("nation"), eligibility[10]);
        });
    }

    /// <summary>
    /// FIFA's requirement counters (CardsDLL 0x2da610 NATION_COUNT, 0x2da980 LEAGUE_COUNT and siblings) skip a card its
    /// item reader flagged as a custom brick, which it does only for rareflag 29 (0x322eb2). Without it the four
    /// placeholders' leagues were counted and "Leagues: Exactly 2" could never go green (live 10-04). The reader then
    /// stops looking the card up, so its league, nation and club ride on the item.
    /// </summary>
    [Fact]
    public void BrickSlotsPlaceholdersAreFlaggedAsCustomBricks()
    {
        using var rt = TestRuntime.Create();
        var challenge = Challenge(rt, "CH_7");
        var cards = SquadPlayers(rt, challenge).Where(entry => JsonValues.Int(entry["itemData"]!["id"]) != 0).ToList();
        Assert.Equal(4, cards.Count);
        Assert.All(cards, entry =>
        {
            var item = entry["itemData"]!.AsObject();
            Assert.Equal(29, item.Int("rareflag"));
            var card = rt.Game.Players.Find(item.Int("resourceId"))!;
            Assert.Equal(card.Int("leagueId"), item.Int("leagueId"));
            Assert.Equal(card.Int("nation"), item.Int("nation"));
            Assert.Equal(card.Int("teamid"), item.Int("teamid"));
        });
    }

    /// <summary>
    /// FIFA gives a custom brick its own negative id (CardsDLL 0x322f02) and saves it with the squad: those slots are
    /// skipped, not refused as a player in a locked slot.
    /// </summary>
    [Fact]
    public void ASaveCarryingTheClientsCustomBrickIdsKeepsTheFreeSlots()
    {
        using var rt = TestRuntime.Create();
        var challenge = Challenge(rt, "CH_7");
        var owned = SbcFixtures.AnyOwnedPlayerItemId(rt);
        var body = SaveBody((2, -1), (3, -2), (5, owned), (7, -3), (8, -4));
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", body).Status);
        Assert.Equal(1, rt.Game.Store().SbcSquadCount(challenge.Id));
    }

    [Fact]
    public void EveryRetailChallengeServesItsOwnFormation()
    {
        using var rt = TestRuntime.Create();
        var retail = rt.Game.Sbc.Challenges.Where(challenge => challenge.Source.Obj("retail") is not null && rt.Game.Sbc.IsLive(challenge.SetKey)).ToList();
        Assert.Equal(28, retail.Count);
        Assert.All(retail, challenge =>
        {
            var formation = challenge.Source.Text("formation");
            Assert.True(rt.Game.Chemistry.Formations.ContainsKey(formation), $"{challenge.Key} formation {formation}");
            Assert.Equal(formation, Listed(rt, challenge).Text("formation"));
        });
    }

    [Fact]
    public void AnUpgradeIsAnOpenChallengeInItsRetailFormation()
    {
        using var rt = TestRuntime.Create();
        var upgrade = rt.Game.Sbc.Sets.Single(set => set.Key == "UPG_BRONZE").Challenges.Single();
        var listed = Listed(rt, upgrade);
        Assert.Equal("OPEN_CHALLENGE", listed.Text("type"));
        Assert.Equal("f41212", listed.Text("formation"));
        Assert.All(Requirements(rt, upgrade), entry => Assert.Equal("DEFAULT", entry.Text("playerType")));
        Assert.All(SquadPlayers(rt, upgrade), entry => Assert.Equal(0, JsonValues.Int(entry["itemData"]!["id"])));
    }

    [Fact]
    public void TheMadeUpAdvancedSetIsNotLive()
    {
        using var rt = TestRuntime.Create();
        Assert.False(rt.Game.Sbc.IsLive("FND_ADVANCED"));
        Assert.True(rt.Game.Sbc.IsLive("FND_LETS_MOVE_FORWARD"));
    }

    [Fact]
    public void ASaveOfTheFreeSlotStoresOnePlayer()
    {
        using var rt = TestRuntime.Create();
        var challenge = Challenge(rt, "CH_1");
        var owned = SbcFixtures.AnyOwnedPlayerItemId(rt);
        var body = SaveBody(Enumerable.Range(0, 23).Select(index => (index, index == 10 ? owned : 0L)).ToArray());
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", body).Status);
        Assert.Equal(1, rt.Game.Store().SbcSquadCount(challenge.Id));
    }

    /// <summary>
    /// FIFA saves the squad when the player backs out of a challenge and reads an error status as a server failure: a 400
    /// sent the player back through login (live 10-02). A refused save answers 200 and forgets the squad instead.
    /// </summary>
    [Fact]
    public void APlayerInALockedSlotIsForgottenWithoutAnErrorStatus()
    {
        using var rt = TestRuntime.Create();
        var challenge = Challenge(rt, "CH_1");
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody((10, SbcFixtures.OwnedPlayerItemId(rt, "bronze")))).Status);
        Assert.Equal(1, rt.Game.Store().SbcSquadCount(challenge.Id));
        var save = rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody((0, SbcFixtures.AnyOwnedPlayerItemId(rt))));
        Assert.Equal(200, save.Status);
        Assert.Equal(0, rt.Game.Store().SbcSquadCount(challenge.Id));
    }

    /// <summary>FIFA keeps subs in the SBC buffer (live 10-02: a First Exchange save held its player and a sub).</summary>
    [Fact]
    public void SubstitutesInTheSaveAreIgnored()
    {
        using var rt = TestRuntime.Create();
        var challenge = Challenge(rt, "CH_1");
        var store = rt.Game.Store();
        var bronze = SbcFixtures.OwnedPlayerItemId(rt, "bronze");
        var sub = store.ListItems("club").Concat(store.ListItems("squad"))
            .First(item => item.Text("itemType") == "player" && item.Int("id") != bronze).Int("id");
        var save = rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody((10, bronze), (11, sub)));
        Assert.Equal(200, save.Status);
        Assert.Equal(1, store.SbcSquadCount(challenge.Id));
    }

    [Fact]
    public async Task SubmittingAShortSquadIsRefusedWithTheRetailInvalidBody()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var challenge = Challenge(rt, "CH_1");
        // Nothing was saved for the free slot, so the submit falls short of the challenge's one player.
        var refused = await rt.Fut.Sbc.SubmitAsync(challenge.Id);
        Assert.Equal(200, refused.Status);
        var body = refused.Body!.AsObject();
        Assert.Equal("INVALID", body.Text("status"));
        Assert.False(body.Bool("success"));
        Assert.False(body.Bool("completed"));
        Assert.Contains("Squad needs 1 players (0 placed)", body.Text("message"));
        Assert.Empty(rt.Game.Store().SbcCompleted());
    }

    [Fact]
    public async Task TheBrickWallFlowPaysOnceAndConsumesOnlyTheFreeSlot()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var challenge = Challenge(rt, "CH_1");
        // First Exchange asks for exactly one bronze player.
        var owned = SbcFixtures.OwnedPlayerItemId(rt, "bronze");
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SaveBody((10, owned))).Status);
        var submit = rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject());
        Assert.Equal(200, submit.Status);
        Assert.Equal("CH_1", Assert.Single(rt.Game.Store().SbcCompleted()));
        // The submitted squad clears on pay-out; only the free slot's player was ever stored.
        Assert.Equal(0, rt.Game.Store().SbcSquadCount(challenge.Id));
        // The consumed player is gone from the club.
        var store = rt.Game.Store();
        Assert.DoesNotContain(store.ListItems("club").Concat(store.ListItems("squad")), item => item.Int("id") == owned);
        Assert.Single(store.UnopenedPackIds());
    }
}
