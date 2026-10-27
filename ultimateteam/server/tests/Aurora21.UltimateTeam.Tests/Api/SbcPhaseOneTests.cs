using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// SBCs in phase 1 (pack-odds-2026-09 PLAN.md 5.5): the any-XI bypass is gone, the player-pick upgrades carry real
/// requirements, and chemistry rules are shown (the client enforces them) but not re-checked by the server. Only the Foundations (FND_*) and the Bronze,
/// Silver and Gold Upgrades (UPG_*) are on the live SBC list (migration 142); LEAGUE_*, NATION_*, UP_* (every player pick),
/// ICON_*, PLAYER_*, CH_*, SP_* and FND_ADVANCED stay in the catalogue but are not listed.
/// </summary>
public sealed class SbcPhaseOneTests
{
    private const string G = "/ut/game/fifa21";

    private static readonly string[] VisiblePrefixes = { "FND_", "UPG_" };

    private static JsonObject Squad(IEnumerable<JsonObject> items) => new()
    {
        ["players"] = new JsonArray(items.Select(item => (JsonNode)new JsonObject { ["itemData"] = new JsonObject { ["id"] = item.Int("id") } }).ToArray()),
    };

    [Fact]
    public void OnlyThePhaseOneSetsAreListedAndHiddenOnesRefuseSavesAndSubmissions()
    {
        using var rt = TestRuntime.Create();
        var listed = rt.CallObject("GET", $"{G}/sbs/sets")["categories"]!.AsArray().OfType<JsonObject>()
            .SelectMany(category => category["sets"]!.AsArray().OfType<JsonObject>()).Select(set => rt.Game.Sbc.FindSet((int)set["setId"].L())!.Key).ToList();
        var expected = rt.Game.Sbc.Sets.Select(set => set.Key)
            .Where(key => VisiblePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)) && rt.Game.Sbc.IsLive(key)).ToList();
        Assert.Equal(expected.Order(), listed.Order());
        Assert.Equal(10, listed.Count);

        foreach (var key in new[] { "UP_82", "UP_85", "UP_86", "UP_88", "LEAGUE_PL", "NATION_ENG", "ICON_Pele", "PLAYER_MESSI", "CH_TOTY", "SP_SILVER" })
        {
            var set = rt.Game.Sbc.Sets.Single(candidate => candidate.Key == key);
            Assert.True(rt.Fut.Sbc.IsHidden(set), key);
            Assert.Empty(rt.Fut.Sbc.ChallengesResponse(set.Id)["challenges"]!.AsArray());
            var challenge = set.Challenges[0];
            var save = rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad",
                new JsonObject { ["players"] = new JsonArray(new JsonObject { ["itemData"] = new JsonObject { ["id"] = SbcFixtures.AnyOwnedPlayerItemId(rt) } }) });
            // A refused save answers 200 (an error status sends FIFA back through login) and stores nothing.
            Assert.Equal(200, save.Status);
            Assert.Equal(0, rt.Game.Store().SbcSquadCount(challenge.Id));
            Assert.Equal(404, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject()).Status);
        }
        Assert.DoesNotContain(rt.Game.Store().SbcCompleted(), key => key is "CH_68" or "CH_70");
    }

    [Theory]
    [InlineData("UP_82", 80, 6, 0, 513)]
    [InlineData("UP_84", 82, 8, 0, 514)]
    [InlineData("UP_86", 84, 10, 1, 515)]
    public async Task AnUpgradeSbcNeedsItsRatingRaresAndTotwBeforeItPaysItsPick(string key, int rating, int rares, int totw, long pick)
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        // The player picks are not on the live SBC list in this phase; their requirements and pick still hold.
        rt.Game.Sbc.LiveKeys.UnionWith(rt.Game.Sbc.Sets.Select(set => set.Key));
        var challenge = rt.Game.Sbc.Sets.Single(set => set.Key == key).Challenges.Single();
        Assert.Equal(new[] { "RATING", "RARE" }.Concat(totw > 0 ? new[] { "TOTW" } : Array.Empty<string>()),
            challenge.Source.Obj("requirements")!.Arr("rules")!.OfType<JsonObject>().Select(rule => rule.Text("type")).Where(type => type != "PLAYERS"));
        var labels = $"Min Squad Rating: {rating}  |  Rare players: Min {rares}" + (totw > 0 ? $"  |  Team of the Week players: Min {totw}" : "");
        Assert.Equal(labels, rt.Fut.Sbc.Describe(challenge));
        Assert.NotEmpty(rt.Fut.Sbc.EligibilityRequirements(challenge));

        // Cards of one exact rating, one per footballer, never sharing a footballer with an earlier pick.
        var used = new HashSet<long>();
        var totwCards = rt.Game.Rules.Totw.Weeks.Where(week => week.Week <= 3).SelectMany(week => week.Starters.Concat(week.Subs)).Select(card => card.ResourceId).ToHashSet();
        List<JsonObject> Cards(int count, long exactRating, Func<long, bool> rarity, bool inform = false) =>
            rt.Game.Players.Entries
                .Where(entry => entry.Value.Rating == exactRating && (inform ? totwCards.Contains(entry.Key) : rarity(entry.Value.Rareflag)) && !rt.Game.Cards.IsGrantOnly(entry.Key))
                .OrderBy(entry => entry.Key)
                .Where(entry => used.Add(entry.Value.Int("assetId")))
                .Take(count).Select(entry => rt.Fut.Items.MakePlayer(entry.Key, "club")).ToList();
        // Non-rare cards are scarce that high, so the best ones are used and the rares carry the squad rating.
        List<JsonObject> Commons(int count) =>
            rt.Game.Players.Entries.Where(entry => entry.Value.Rareflag == 0 && !rt.Game.Cards.IsGrantOnly(entry.Key))
                .OrderByDescending(entry => entry.Value.Rating).ThenBy(entry => entry.Key)
                .Where(entry => used.Add(entry.Value.Int("assetId")))
                .Take(count).Select(entry => rt.Fut.Items.MakePlayer(entry.Key, "club")).ToList();
        List<JsonObject> RatedEnough(List<JsonObject> squad)
        {
            Assert.True(SbcSquadRating.CalculateSbcSquadRating(squad.Select(item => (int)item.Int("rating"))) >= rating, "the fixture squad must meet the rating");
            return squad;
        }
        async Task<FutResult> Submit(List<JsonObject> squad)
        {
            Assert.Equal(11, squad.Count);
            await rt.Fut.Sbc.SaveSquadAsync(challenge.Id, Squad(squad));
            var result = await rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id));
            Assert.True(result.Status == 200, result.Body!.ToJsonString());
            return result;
        }

        // Any complete XI is no longer enough: a refused submit answers the retail 200 INVALID body.
        Assert.Equal("INVALID", (await Submit(Cards(11, 65, flag => flag == 0))).Body!["status"]!.S());
        // One rating point short of the squad rating, with every other requirement met.
        Assert.Equal("INVALID", (await Submit(Cards(11 - totw, rating - 1, flag => flag == 1).Concat(Cards(totw, rating - 1, _ => true, inform: true)).ToList())).Body!["status"]!.S());
        if (rares < 11)
        {
            // One rare short.
            Assert.Equal("INVALID", (await Submit(RatedEnough(Cards(rares - 1, rating + 3, flag => flag == 1).Concat(Commons(12 - rares)).ToList()))).Body!["status"]!.S());
        }
        if (totw > 0)
        {
            // Rated and rare enough, but no Team of the Week card.
            Assert.Equal("INVALID", (await Submit(Cards(11, rating, flag => flag == 1))).Body!["status"]!.S());
        }
        Assert.Empty(rt.Game.Store().SbcCompleted());

        var picksBefore = rt.Game.Store().UnopenedPackIds().Count(id => id == pick);
        // A Team of the Week card is not a rare player (PLAYER_RARITY 1 matches rareflag 1 exactly, as the client counts it).
        var passing = Cards(rares, rares < 11 ? rating + 2 : rating, flag => flag == 1).Concat(Cards(totw, rating, _ => true, inform: true)).Concat(Commons(11 - rares - totw)).ToList();
        Assert.Equal(200, (await Submit(RatedEnough(passing))).Status);
        Assert.Equal(new[] { challenge.Key }, rt.Game.Store().SbcCompleted());
        Assert.Equal(picksBefore + 1, rt.Game.Store().UnopenedPackIds().Count(id => id == pick));
    }

    /// <summary>
    /// FIFA 21 (CardsDLL) labels a PLAYER_RARITY_GROUP value as Player_Group_&lt;n&gt;, so group 4 read "UEFA Champions League", and
    /// its player-count check never matches a group. The Rare rule is a PLAYER_COUNT slot over PLAYER_RARITY 1 ("Rare").
    /// </summary>
    /// <summary>
    /// Every requirement slot names a rule the players match: a PLAYER_COUNT slot with nothing beside it counted the
    /// players matching nothing, so First Exchange's "Players: Exactly 1" stayed red with its one player in (10-03). FIFA
    /// lists "Number of players in the Squad" itself.
    /// </summary>
    [Fact]
    public void EveryRequirementSlotNamesARuleThePlayersMatch()
    {
        using var rt = TestRuntime.Create();
        foreach (var challenge in rt.Game.Sbc.Challenges)
        {
            var slots = rt.Fut.Sbc.EligibilityRequirements(challenge).OfType<JsonObject>().GroupBy(entry => entry["eligibilitySlot"].L());
            Assert.All(slots, slot => Assert.Contains(slot, entry =>
                entry["eligibilityKey"].L() is not ((long)SbcEligibilityKey.PlayerCount or (long)SbcEligibilityKey.Scope)));
        }
        // First Exchange: one slot, its bronze rule over its one player; no slot for the count alone.
        var firstExchange = rt.Game.Sbc.Sets.Single(set => set.Key == "FND_LETS_GET_STARTED").Challenges.Single(challenge => challenge.Source.Text("name") == "First Exchange");
        var entries = rt.Fut.Sbc.EligibilityRequirements(firstExchange).OfType<JsonObject>().ToList();
        Assert.Single(entries.Select(entry => entry["eligibilitySlot"].L()).Distinct());
        Assert.Contains(entries, entry => entry["eligibilityKey"].L() == (long)SbcEligibilityKey.Quality);
        Assert.Contains(entries, entry => entry["eligibilityKey"].L() == (long)SbcEligibilityKey.PlayerCount && entry["eligibilityValue"].L() == 1);
    }

    [Fact]
    public void VisibleRareRequirementsAskForRareflagOneNotARarityGroup()
    {
        using var rt = TestRuntime.Create();
        var visible = rt.Game.Sbc.Challenges.Where(challenge => rt.Game.Sbc.IsLive(challenge.SetKey)).ToList();
        Assert.NotEmpty(visible);
        var withRare = 0;
        foreach (var challenge in visible)
        {
            var slots = rt.Fut.Sbc.EligibilityRequirements(challenge).OfType<JsonObject>().ToList();
            Assert.DoesNotContain(slots, slot => slot["eligibilityKey"].L() == (long)SbcEligibilityKey.RarityGroup);
            foreach (var rare in challenge.Source.Obj("requirements")!.Arr("rules")!.OfType<JsonObject>().Where(rule => rule.Text("type") == "RARE"))
            {
                withRare++;
                var slot = slots.Single(entry => entry["eligibilityKey"].L() == (long)SbcEligibilityKey.Rarity)["eligibilitySlot"].L();
                var entries = slots.Where(entry => entry["eligibilitySlot"].L() == slot)
                    .Select(entry => (entry["eligibilityKey"].L(), entry["eligibilityValue"].L())).ToList();
                Assert.Equal(new[] { (18L, 1L), (2L, rare.Obj("attributes")!.Int("min")), (13L, (long)SbcScope.AtLeast) }, entries);
            }
        }
        // Retail (FUTBIN): The Final Four, Elite Eight, Around the World, Give Me Five, The Whole Nine Yards and
        // First XI ask for rare players.
        Assert.Equal(6, withRare);
    }

    [Fact]
    public async Task OnlyRareflagOneCardsCountAsRarePlayers()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var challenge = rt.Game.Sbc.Sets.Single(set => set.Key == "FND_HYBRID_NATIONS").Challenges[0];
        challenge.Source["requirements"] = new JsonObject
        {
            ["rules"] = new JsonArray(
                new JsonObject { ["type"] = "PLAYERS", ["attributes"] = new JsonObject { ["min"] = 11, ["max"] = 11 } },
                new JsonObject { ["type"] = "RARE", ["attributes"] = new JsonObject { ["min"] = 2 } },
                new JsonObject { ["type"] = "QUALITY", ["attributes"] = new JsonObject { ["value"] = "gold" } })
        };
        Assert.Equal("Rare players: Min 2  |  Player Quality: Gold only", rt.Fut.Sbc.Describe(challenge));
        var used = new HashSet<long>();
        List<JsonObject> Gold(int count, Func<long, bool> rarity) => rt.Game.Players.Entries
            .Where(entry => entry.Value.Rating >= 75 && rarity(entry.Value.Rareflag) && !rt.Game.Cards.IsGrantOnly(entry.Key))
            .OrderBy(entry => entry.Key).Where(entry => used.Add(entry.Value.Int("assetId")))
            .Take(count).Select(entry => rt.Fut.Items.MakePlayer(entry.Key, "club")).ToList();
        var commons = Gold(9, flag => flag == 0);
        var special = Gold(1, flag => flag == 3).Single();
        var rares = Gold(2, flag => flag == 1);
        async Task<FutResult> Submit(IEnumerable<JsonObject> squad)
        {
            await rt.Fut.Sbc.SaveSquadAsync(challenge.Id, Squad(squad));
            return await rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id));
        }
        // One gold rare and a Team of the Week card: the special is not a rare player.
        var refused = await Submit(commons.Append(rares[0]).Append(special));
        Assert.Equal(200, refused.Status);
        Assert.Equal("INVALID", refused.Body!["status"]!.S());
        Assert.Empty(rt.Game.Store().SbcCompleted());
        Assert.Equal(200, (await Submit(commons.Concat(rares))).Status);
        Assert.Equal(new[] { challenge.Key }, rt.Game.Store().SbcCompleted());
    }

    [Fact]
    public void ChemistryRulesAreShownToTheClient()
    {
        using var rt = TestRuntime.Create();
        Assert.DoesNotContain("CHEMISTRY", rt.Game.Rules.Sbc.IgnoredRequirements);
        var withChemistry = rt.Game.Sbc.Challenges.Where(challenge => rt.Game.Sbc.IsLive(challenge.SetKey)
            && challenge.Source.Obj("requirements")!.Arr("rules")!.OfType<JsonObject>().Any(rule => rule.Text("type") == "CHEMISTRY")).ToList();
        Assert.NotEmpty(withChemistry);
        Assert.All(withChemistry, challenge =>
        {
            Assert.Contains("Chemistry", rt.Fut.Sbc.Describe(challenge), StringComparison.Ordinal);
            Assert.Contains(rt.Fut.Sbc.EligibilityRequirements(challenge).OfType<JsonObject>(),
                slot => slot["eligibilityKey"].L() == (long)SbcEligibilityKey.Chemistry);
        });
    }

    [Fact]
    public void AFreshDatabaseListsTheTenPhaseOneSetsAndKeepsTheRestInTheCatalogue()
    {
        using var rt = TestRuntime.Create();
        // Migration 142 builds the list from the patterns rules/sbc.json hiddenSets shipped with; the rule itself is gone.
        Assert.Equal(new[] { "FND_HYBRID_LEAGUES", "FND_HYBRID_NATIONS", "FND_LEAGUE_NATION_BASICS", "FND_LEAGUE_NATION_HYBRID",
            "FND_LETS_GET_STARTED", "FND_LETS_KEEP_GOING", "FND_LETS_MOVE_FORWARD", "UPG_BRONZE", "UPG_GOLD", "UPG_SILVER" },
            rt.Game.Sbc.LiveKeys.Order(StringComparer.Ordinal));
        Assert.Equal(11, rt.Game.Sbc.Live.Count);
        Assert.False(rt.Game.Sbc.Live["POTM_1"]);
        Assert.Equal(57, rt.Game.Sbc.Sets.Count);
        foreach (var key in new[] { "ICON_Pele", "UP_88", "NATION_ENG", "LEAGUE_PL", "FND_ADVANCED" })
            Assert.False(rt.Game.Sbc.IsLive(key));
        Assert.DoesNotContain("hiddenSets", rt.Game.InUnit(async work => (await RuleRows.DocumentsAsync(work))["sbc"]), StringComparison.Ordinal);
    }

    private static FutResponse Get(TestRuntime rt, string path) =>
        rt.Router.Handle(new FutRequest("GET", path, Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));

    [Fact]
    public void EveryListedSetDrawsItsOwnTileArtNotItsTabFallback()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var folder = Path.Combine(TestRuntime.DataRoot, rt.Game.Rules.Sbc.Images.ArtFolder);
        var sets = rt.CallObject("GET", $"{G}/sbs/sets")["categories"]!.AsArray().OfType<JsonObject>()
            .SelectMany(category => category["sets"]!.AsArray().OfType<JsonObject>()).ToList();
        Assert.Contains(sets, set => rt.Game.Sbc.FindSet((int)set["setId"].L())!.Key == "UPG_SILVER");
        Assert.All(sets, listed =>
        {
            var set = rt.Game.Sbc.FindSet((int)listed["setId"].L())!;
            var image = Get(rt, $"/fut/sbc/pc/sets/images/sbc_set_image_{listed["setImageId"].L()}.png");
            Assert.Equal(200, image.Status);
            Assert.Equal(File.ReadAllBytes(Path.Combine(folder, set.Key + ".png")), image.Body);
            Assert.NotEqual(File.ReadAllBytes(Path.Combine(folder, $"tab-{set.Source.Int("tabId")}.png")), image.Body);
        });
    }

    [Fact]
    public void EveryPackAnSbcCanAwardHasItsOwnNameInThePackLabels()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var response = Get(rt, "/fut/packs/loc/storepackdescriptions.en_us.xml");
        Assert.Equal(200, response.Status);
        var labels = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Descendants("trans-unit")
            .ToDictionary(unit => (string)unit.Attribute("resname")!, unit => unit.Element("source")!.Value);
        var packs = rt.Game.Sbc.PackUses().Keys;
        Assert.All(packs, pack =>
        {
            Assert.True(labels.TryGetValue($"FUT_STORE_PACK_{pack}_NAME", out var name), $"pack {pack} has no name label");
            Assert.False(string.IsNullOrWhiteSpace(name), $"pack {pack} has an empty name");
        });
        // The Upgrade rewards are kept out of the store (migration 109: enabled false) yet still need their names.
        Assert.Equal("2 Rare Bronze Players Pack", labels["FUT_STORE_PACK_80_NAME"]);
        Assert.Equal("2 Rare Silver Players Pack", labels["FUT_STORE_PACK_81_NAME"]);
        Assert.Equal("2 Rare Gold Players Pack", labels["FUT_STORE_PACK_82_NAME"]);
        Assert.Equal("2 rare silver players.", labels["FUT_STORE_PACK_81_DESC"]);
        // Still not for sale.
        Assert.DoesNotContain(rt.CallObject("GET", $"{G}/store/purchasegroup/all")["purchase"].Objects(), tile => tile["id"].L() is 80 or 81 or 82);
    }

    [Theory]
    [InlineData("UPG_BRONZE", "bronze", 80, 60, 64)]
    [InlineData("UPG_SILVER", "silver", 81, 65, 74)]
    [InlineData("UPG_GOLD", "gold", 82, 75, 99)]
    public async Task AnUpgradeExchangesElevenOfItsQualityForTwoRarePlayersOfIt(string key, string tier, long pack, int minRating, int maxRating)
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var set = rt.Game.Sbc.Sets.Single(candidate => candidate.Key == key);
        Assert.True(set.Source.Bool("repeatable"));
        var challenge = set.Challenges.Single();
        // Retail upgrades also ask for Min 40 chemistry; this test is about the exchange, so it switches chemistry off.
        Assert.Equal($"Player Quality: {char.ToUpperInvariant(tier[0])}{tier[1..]} only  |  Min Squad Chemistry: 40", rt.Fut.Sbc.Describe(challenge));
        rt.Game.Rules.Sbc.IgnoredRequirements.Add("CHEMISTRY");
        Assert.Equal(pack, SbcService.RewardPackId(challenge.Source.Obj("rewards")!.Arr("entries")![0]!.AsObject().Obj("attributes")!));
        // Migration 109 defines the reward pack, so every database can open it.
        var configured = await rt.Game.RunAsync("configured", async w =>
            (await w.QueryAsync("SELECT pack_id FROM ultimateteam.store_packs WHERE size = 2 AND rare_slots = 2", r => r.GetInt64(0))).ToHashSet(),
            CancellationToken.None);
        Assert.Contains(pack, configured);

        var used = new HashSet<long>();
        List<JsonObject> Cards(int low, int high) => rt.Game.Players.Entries
            .Where(entry => entry.Value.Rating >= low && entry.Value.Rating <= high && entry.Value.Rareflag <= 1 && !rt.Game.Cards.IsGrantOnly(entry.Key))
            .OrderBy(entry => entry.Key)
            .Where(entry => used.Add(entry.Value.Int("assetId")))
            .Take(11).Select(entry => rt.Fut.Items.MakePlayer(entry.Key, "club")).ToList();
        async Task<FutResult> Submit(List<JsonObject> squad)
        {
            Assert.Equal(11, squad.Count);
            await rt.Fut.Sbc.SaveSquadAsync(challenge.Id, Squad(squad));
            return await rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id));
        }

        // Ten of the quality and one of another tier is refused.
        var mixed = Cards(minRating, maxRating).Take(10).Concat(tier == "gold" ? Cards(65, 74).Take(1) : Cards(75, 99).Take(1)).ToList();
        Assert.Equal("INVALID", (await Submit(mixed)).Body!["status"]!.S());
        Assert.Empty(rt.Game.Store().SbcCompleted());

        var before = rt.Game.Store().UnopenedPackIds().Count(id => id == pack);
        var xpBefore = rt.Game.Store().Objectives().UserXp;
        Assert.Equal(200, (await Submit(Cards(minRating, maxRating))).Status);
        Assert.Equal(new[] { challenge.Key }, rt.Game.Store().SbcCompleted());
        Assert.Equal(before + 1, rt.Game.Store().UnopenedPackIds().Count(id => id == pack));
        // The first exchange counts for objectives: one SBC, with no Season XP (migration 146).
        Assert.Equal(1, rt.Game.Store().Objectives().Stat("sbcs"));
        var xpAfterFirst = rt.Game.Store().Objectives().UserXp;
        Assert.Equal(xpBefore, xpAfterFirst);
        // A repeatable set pays untradeable (rules/sbc.json repeatableRewardsUntradeable), as retail paid the upgrade packs.
        var untradeable = await rt.Game.ReadAsync(w => w.QueryAsync(
            "SELECT untradeable FROM ultimateteam.unopened_packs WHERE club_id=@club AND pack_id=@pack AND source='sbc'",
            r => r.GetBoolean(0), ("pack", pack)), CancellationToken.None);
        Assert.NotEmpty(untradeable);
        Assert.All(untradeable, Assert.True);

        // Replaying a submission without a new squad cannot mint another reward.
        var replay = await rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id));
        Assert.Equal(200, replay.Status);
        Assert.Equal("INVALID", replay.Body!["status"]!.S());
        Assert.Equal(before + 1, rt.Game.Store().UnopenedPackIds().Count(id => id == pack));
        var secondSquad = Cards(minRating, maxRating);
        Assert.Equal(200, (await Submit(secondSquad)).Status);
        Assert.Equal(before + 2, rt.Game.Store().UnopenedPackIds().Count(id => id == pack));
        Assert.All(secondSquad, card => Assert.DoesNotContain(rt.Game.Store().ListItems("club"), item => item.Int("id") == card.Int("id")));
        var counts = await rt.Game.ReadAsync(w => w.SbcCompletionCountsAsync(), CancellationToken.None);
        Assert.Equal(2, counts[challenge.Key]);
        // A repeat pays its pack but no longer counts for objectives, so Upgrades cannot farm SBC objectives or Season XP.
        Assert.Equal(1, rt.Game.Store().Objectives().Stat("sbcs"));
        Assert.Equal(xpAfterFirst, rt.Game.Store().Objectives().UserXp);
        var listed = (await rt.Fut.Sbc.ChallengesResponseAsync(set.Id)).Arr("challenges")![0]!.AsObject();
        Assert.Equal("NOT_STARTED", listed.Text("status"));
        Assert.Equal(2, listed.Int("timesCompleted"));

        // The rules' Icon chance only seeds new odds tables: even at 100% it never reaches these standard rare rewards.
        rt.Game.Rules.PackOdds.IconChance = 1;
        var opened = await rt.Game.RunAsync("open-upgrade-reward", w => rt.Fut.Packs.CreatePackAsync(w, pack), CancellationToken.None);
        var items = opened.Arr("items")!.OfType<JsonObject>().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal("player", item.Text("itemType"));
            Assert.Equal(1, item.Int("rareflag"));
            Assert.InRange(item.Int("rating"), tier == "bronze" ? 1 : minRating, maxRating);
        });
    }

    [Fact]
    public async Task RepeatableSetPaysEachChallengeOncePerRoundAndPreservesHistory()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var set = rt.Game.Sbc.Sets.Single(candidate => candidate.Key == "FND_LETS_GET_STARTED");
        set.Source["repeatable"] = true;
        foreach (var challenge in set.Challenges)
        {
            SbcFixtures.SaveSatisfyingSquad(rt, challenge);
            Assert.Equal(200, (await rt.Fut.Sbc.SubmitAsync(challenge.Id)).Status);
            var replay = await rt.Fut.Sbc.SubmitAsync(challenge.Id);
            if (challenge != set.Challenges[^1])
            {
                Assert.Equal(200, replay.Status);
                Assert.Equal(0, replay.Body!.AsObject().Int("grantedCount"));
            }
            else
            {
                Assert.Equal(200, replay.Status);
                Assert.Equal("INVALID", replay.Body!["status"]!.S());
            }
        }
        var counts = await rt.Game.ReadAsync(w => w.SbcCompletionCountsAsync(), CancellationToken.None);
        Assert.All(set.Challenges, challenge => Assert.Equal(1, counts[challenge.Key]));
        foreach (var challenge in set.Challenges)
        {
            SbcFixtures.SaveSatisfyingSquad(rt, challenge);
            Assert.Equal(200, (await rt.Fut.Sbc.SubmitAsync(challenge.Id)).Status);
        }
        counts = await rt.Game.ReadAsync(w => w.SbcCompletionCountsAsync(), CancellationToken.None);
        Assert.All(set.Challenges, challenge => Assert.Equal(2, counts[challenge.Key]));
        var listed = (await rt.Fut.Sbc.ChallengesResponseAsync(set.Id)).Arr("challenges")!.OfType<JsonObject>();
        Assert.All(listed, challenge => Assert.Equal("NOT_STARTED", challenge.Text("status")));
        // Each challenge counted for objectives in the first round only.
        Assert.Equal(set.Challenges.Count, rt.Game.Store().Objectives().Stat("sbcs"));
    }
}
