using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Tools;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Durable commands are atomic: a failure before any of their writes leaves the club exactly as it was,
/// and retried or concurrent commands pay once.
/// </summary>
public sealed class AtomicCommandTests
{
    private const string G = "/ut/game/fifa21";
    private const int MaxFaultPositions = 400;
    private const long SpareTokenOffer = 9001;
    private const long TokenPrice = 25;

    [Theory]
    [InlineData("coins")]
    [InlineData("points")]
    [InlineData("owned-reward")]
    [InlineData("player-pick")]
    [InlineData("kit-stadium")]
    [InlineData("all-consumables")]
    public void PackOpeningLeavesNothingBehindWhenAnyWriteFails(string kind)
    {
        var faults = new DatabaseFaultInjector();
        // Migration 097 retired the store's pick and all-consumables packs, so those cases seed copies of the originals
        // (migration 041 packs 21 and 8); the Player Pick token definition itself ships with migration 048.
        var seedSql = kind switch
        {
            "kit-stadium" => """
                INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,kit_stadium_pack)
                VALUES(90,'store',true,'Synthetic kit and stadium fixture',1,4,true)
                """,
            "player-pick" => """
                INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,rare_slots,category_chances,manager_chance,
                    min_rating,store_group,player_pick_count)
                VALUES(90,'store',true,'Synthetic 82+ player pick fixture',1,1,100,3,'{"totw": 0.03, "icon": 0.0005}',0,82,'special',4)
                """,
            "all-consumables" => """
                INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,
                    manager_chance,store_group,all_consumables)
                VALUES(90,'store',true,'Synthetic all consumables fixture',1,20,100,0,0,3,0,'special',true)
                """,
            // Aurora Tokens buy only Token Store listings, each at its own price (migration 148): the Gold Pack's listing, at 25.
            "points" => $"""
                INSERT INTO ultimateteam.token_store_listings(pack_id,offer_id,price,enabled,listed,untradeable)
                VALUES(74,{SpareTokenOffer},{TokenPrice},true,true,false)
                ON CONFLICT (pack_id) DO UPDATE SET price=excluded.price, enabled=true, listed=true, untradeable=false
                """,
            _ => null,
        };
        using var rt = TestRuntime.Create(faultInjector: faults, seedSql: seedSql);
        // FIFA Points purchases are off in phase 1 (rules/club.json); the points path is still atomic when switched on.
        rt.Game.Rules.Club.FifaPointsPurchases = kind == "points";
        var store = rt.Game.Store();
        store.AddCredits(1_000_000);
        store.AddFifaPoints(100_000);
        var packId = kind switch
        {
            // The store's Gold Pack (migration 095), sold for coins and FIFA Points.
            "coins" or "owned-reward" => 74,
            "points" => long.Parse(rt.Game.Packs.Rules.TokenOffers.Single(offer => offer.Value!["base_pack_id"]!.GetValue<long>() == 74).Key,
                System.Globalization.CultureInfo.InvariantCulture),
            "player-pick" => StorePack(rt, "player_pick_count"),
            "kit-stadium" => StorePack(rt, "kit_stadium_pack"),
            _ => StorePack(rt, "all_consumables"),
        };
        var payload = kind switch
        {
            "points" => new JsonObject { ["packId"] = packId, ["usePoints"] = 1 },
            "owned-reward" => new JsonObject { ["packId"] = packId },
            _ => new JsonObject { ["packId"] = packId, ["useCredits"] = 1, ["currency"] = "COINS" },
        };
        if (kind == "owned-reward")
        {
            store.AddUnopenedPack(packId, "test");
        }
        var (credits, points) = (store.Credits(), store.FifaPoints());

        var failures = FailAtEveryWrite(rt, faults, () => rt.CallRaw("POST", $"{G}/purchased/items", payload).Status == 200);

        Assert.True(failures > 2, $"only {failures} fault positions were exercised");
        Assert.Equal(1, store.Objectives().Stat("packs"));
        var price = rt.Game.Packs.GetPackConfig(packId).IntKeep("price", 0);
        switch (kind)
        {
            case "points":
                Assert.Equal(points - TokenPrice, store.FifaPoints());
                Assert.Equal(credits, store.Credits());
                break;
            case "owned-reward":
                Assert.Equal(credits, store.Credits());
                Assert.DoesNotContain(packId, store.UnopenedPackIds());
                break;
            default:
                Assert.Equal(credits - price, store.Credits());
                break;
        }
    }

    [Fact]
    public void SbcSubmissionGrantsNothingWhenAnyWriteFailsAndPaysOnce()
    {
        var faults = new DatabaseFaultInjector();
        using var rt = TestRuntime.Create(faultInjector: faults);
        var store = rt.Game.Store();
        // The last challenge must also be one a real squad can satisfy: submission now enforces the
        // player count and the authored requirements, so a set whose final challenge needs TOTW or
        // rare cards a starter club does not own could never reach the fault paths under test.
        var set = rt.Game.Sbc.Sets.First(candidate => candidate.Challenges.Count > 1
            && HasRewards(candidate.Challenges[^1].Source)
            && SbcFixtures.CanBuildSquad(candidate.Challenges[^1]));
        // This runtime's catalogue copy gets its own set rewards (coins and two packs) so that path is failed too.
        const long setCoins = 5000;
        set.Source["rewards"] = new JsonObject
        {
            ["entries"] = new JsonArray(
                new JsonObject { ["type"] = "COINS", ["value"] = setCoins },
                new JsonObject { ["type"] = "PACK", ["attributes"] = new JsonObject { ["pack"] = "gold", ["count"] = 2 } }),
        };
        foreach (var earlier in set.Challenges.SkipLast(1))
        {
            Assert.True(store.TryMarkSbcCompleted(earlier.Key));
        }
        var last = set.Challenges[^1];
        SbcFixtures.SaveSatisfyingSquad(rt, last);
        var (credits, packs) = (store.Credits(), store.UnopenedPackIds().Count);

        var failures = FailAtEveryWrite(rt, faults, () =>
        {
            var (status, body, _) = rt.CallRaw("PUT", $"{G}/sbs/challenge/{last.Id}", new JsonObject());
            return status == 200 && body is JsonObject submitted && submitted.ContainsKey("grantedItemsCount");
        });

        Assert.True(failures > 2, $"only {failures} fault positions were exercised");
        Assert.Contains(last.Key, store.SbcCompleted());
        Assert.Equal(1, store.Objectives().Stat("sbcs"));
        Assert.True(store.Credits() >= credits + setCoins);
        Assert.True(store.UnopenedPackIds().Count >= packs + 2);

        // A retry of the completed challenge pays nothing more.
        var paid = ClubStateSnapshot.Capture(rt.ConnectionString, store.ClubId);
        Assert.Equal(0, rt.CallObject("PUT", $"{G}/sbs/challenge/{last.Id}", new JsonObject())["grantedItemsCount"].L());
        Assert.Equal(paid, ClubStateSnapshot.Capture(rt.ConnectionString, store.ClubId));
    }

    /// <summary>
    /// Atomic grants make a broken reward block its challenge, so every bundled reward must grant.
    ///
    /// This exercises the reward path directly rather than through PUT /sbs/challenge/{id}. Submission
    /// now enforces the squad, and many bundled challenges require cards a starter club cannot own
    /// (TOTW counts, rare minimums, exact club/quality rules), so no single club could legitimately
    /// submit all of them. Requiring an end-to-end submission per challenge would mean weakening that
    /// validation, which is exactly what the plan forbids. The reward coverage this test exists for is
    /// unchanged; end-to-end submission is covered for specific challenges by the tests around it.
    /// </summary>
    [Fact]
    public async Task EveryBundledSbcChallengeAndSetRewardGrants()
    {
        using var rt = TestRuntime.Create();
        var challenges = rt.Game.Sbc.Sets.SelectMany(set => set.Challenges).ToList();
        Assert.NotEmpty(challenges);
        var rewarded = 0;
        foreach (var challenge in challenges)
        {
            var granted = await rt.Fut.Sbc.GrantChallengeRewardsAsync(challenge);
            if (HasRewards(challenge.Source))
            {
                Assert.True(granted.Count > 0, $"challenge {challenge.Id} ({challenge.Key}) granted nothing");
                rewarded++;
            }
        }
        Assert.True(rewarded > 0, "no bundled challenge carried a reward");

        var setsRewarded = 0;
        foreach (var set in rt.Game.Sbc.Sets.Where(set => HasRewards(set.Source)))
        {
            var granted = await rt.Fut.Sbc.GrantSetRewardsAsync(set);
            var onlyCoins = set.Source.Obj("rewards")!.Arr("entries")!.OfType<JsonObject>().All(entry => entry.Text("type") == "COINS");
            Assert.True(onlyCoins || granted.Count > 0, $"set {set.Id} ({set.Key}) granted nothing");
            setsRewarded++;
        }
        Assert.True(setsRewarded > 0, "no bundled set carried a group reward");
    }

    [Fact]
    public void ObjectiveClaimsLeaveNothingBehindWhenAnyWriteFailsAndPayOnce()
    {
        var faults = new DatabaseFaultInjector();
        using var rt = TestRuntime.Create(faultInjector: faults);
        var store = rt.Game.Store();
        // Kick Off pays once the club has played a match (owner, 2026-09-29).
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));

        FailAtEveryWrite(rt, faults, () => !rt.Fut.Objectives.Claim(null, null)["alreadyClaimed"].B());
        Assert.True(store.Objectives().RewardClaimed);
        Assert.True(rt.Fut.Objectives.Claim(null, null)["alreadyClaimed"].B());

        store.AddObjectivesXp(rt.Game.Rules.Objectives.SeasonProgress.XpPerLevel * 2);
        FailAtEveryWrite(rt, faults, () => !rt.Fut.Objectives.ClaimLevel(2).ContainsKey("alreadyClaimed"));
        Assert.Contains(2L, store.Objectives().ClaimedLevels);
        var claimed = ClubStateSnapshot.Capture(rt.ConnectionString, store.ClubId);
        Assert.True(rt.Fut.Objectives.ClaimLevel(2)["alreadyClaimed"].B());
        Assert.Equal(claimed, ClubStateSnapshot.Capture(rt.ConnectionString, store.ClubId));
    }

    [Fact]
    public void DraftPurchaseMatchEndAndRewardLeaveNothingBehindWhenAnyWriteFails()
    {
        var faults = new DatabaseFaultInjector();
        using var rt = TestRuntime.Create(faultInjector: faults);
        var store = rt.Game.Store();
        var credits = store.Credits();

        FailAtEveryWrite(rt, faults, () =>
            rt.CallRaw("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" }).Status == 200);
        Assert.Equal(credits - rt.Game.Rules.Draft.Entry.Coins, store.Credits());

        // A Draft match end settles only a run that is ready to play, and the match its CreateMatch created. A Professional
        // match played to full time also counts objective progress (the reward gate).
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "PRO" });
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = rt.Game.Rules.Draft.SquadId });
        rt.PlayedRealSeconds();
        FailAtEveryWrite(rt, faults, () => rt.Fut.Draft.EndMatch(DraftSlot.SinglePlayer, new JsonObject { ["endReason"] = "LOSS", ["secondsPlayed"] = 5461 })?.Bool("draftEliminated") == true);
        var run = store.DraftRun(DraftSlot.SinglePlayer)!;
        Assert.Equal("READY_FOR_REWARDS", run.Text("state"));
        var reward = run.Int("pendingCoinReward");
        Assert.True(reward > 0);
        Assert.Equal(1, store.Objectives().Stat("matches"));

        credits = store.Credits();
        FailAtEveryWrite(rt, faults, () => rt.Fut.Draft.GrantAward(DraftSlot.SinglePlayer).Count == 1);
        Assert.Equal(credits + reward, store.Credits());
        Assert.Null(store.DraftRun(DraftSlot.SinglePlayer));
        Assert.Empty(rt.Fut.Draft.GrantAward(DraftSlot.SinglePlayer));
        Assert.Equal(credits + reward, store.Credits());
    }

    [Fact]
    public void MatchEndSettlesOnceUnderFaultsRetriesAndConcurrentServers()
    {
        var faults = new DatabaseFaultInjector();
        using var rt = TestRuntime.Create(faultInjector: faults);
        var store = rt.Game.Store();
        // A full-length 2-0 Rivals win (the reward gate pays it): the result award plus two goals in the coin rows.
        var win = rt.Game.Rules.Seasons.MatchCompletionRewards["rivals"].Coins;
        void Pair(long gameId)
        {
            rt.Game.InUnit(uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_234_567, gameId), 1000));
            rt.PlayedRealSeconds();
        }
        JsonObject End(TestRuntime runtime, long matchId) => runtime.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "WIN",
            ["matchId"] = matchId,
            ["secondsPlayed"] = 5461,
            ["myMatchStats"] = new JsonObject { ["goals"] = 2 },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
        });
        var credits = store.Credits();

        Pair(41);
        FailAtEveryWrite(rt, faults, () => rt.CallRaw("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "WIN",
            ["matchId"] = 41,
            ["secondsPlayed"] = 5461,
            ["myMatchStats"] = new JsonObject { ["goals"] = 2 },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
        }).Status == 200);
        Assert.Equal(credits + win, store.Credits());
        for (var retry = 0; retry < 3; retry++)
        {
            End(rt, 41);
        }
        Assert.Equal(credits + win, store.Credits());
        Assert.Equal(1, store.Objectives().Stat("matches"));

        // Two server processes on one database receive the same report at the same moment.
        var second = TestRuntime.Create(connectionString: rt.ConnectionString);
        try
        {
            Pair(42);
            using var barrier = new Barrier(2);
            var responses = new JsonObject[2];
            var runtimes = new[] { rt, second };
            Parallel.For(0, 2, index =>
            {
                barrier.SignalAndWait();
                responses[index] = End(runtimes[index], 42);
            });
            // Only the server that settles the match emits objective progress deltas.
            Assert.Single(responses, response => response.ContainsKey("dynamicObjectivesUpdates"));
            var settled = responses.Select(response => response.DeepClone().AsObject()).ToArray();
            foreach (var response in settled) response.Remove("dynamicObjectivesUpdates");
            Assert.True(JsonNode.DeepEquals(settled[0], settled[1]));
            Assert.Equal(credits + 2 * win, store.Credits());
            Assert.Equal(2, store.Objectives().Stat("matches"));
        }
        finally
        {
            second.Detach();
        }
    }

    [Fact]
    public void PlayerPickSelectionLeavesTheTokenWhenAnyWriteFails()
    {
        var faults = new DatabaseFaultInjector();
        using var rt = TestRuntime.Create(faultInjector: faults);
        var store = rt.Game.Store();
        var tokenId = rt.Fut.Items.MakePlayerPick(86, 4).IntKeep("id");
        var choices = rt.Fut.Picks.Redeem(tokenId).OfType<JsonObject>().ToList();
        // Owning the first choice makes the selection also write its duplicate marker.
        rt.Fut.Items.MakePlayer(choices[0].Int("resourceId"), "club");

        Assert.Throws<Aurora21.UltimateTeam.Core.Models.ClubRuleException>(() => rt.Fut.Picks.Select(tokenId, JsonValue.Create(99)));
        Assert.Equal(ChoiceIds(choices), ChoiceIds(rt.Fut.Picks.Redeem(tokenId).OfType<JsonObject>()));

        FailAtEveryWrite(rt, faults, () => rt.Fut.Picks.Select(tokenId, JsonValue.Create(0)).Int("id") == choices[0].Int("id"));

        Assert.False(store.ItemExists(tokenId));
        Assert.Null(store.GetPlayerPick(tokenId));
        Assert.True(store.GetItem(choices[0].Int("id"))!.Bool("duplicate"));
    }

    [Fact]
    public async Task GrantAllCardsRollsBackOnlyTheCardThatFailed()
    {
        var faults = new DatabaseFaultInjector(() => new InvalidOperationException("injected card failure"));
        using var rt = TestRuntime.Create(faultInjector: faults);
        var owned = rt.Fut.Inventory.OwnedPlayerResourceIds();
        var catalog = rt.Game.Players.Ids.Where(id => !owned.Contains(id)).Take(5).ToList();

        // Write 1 locks the club; each card then reserves an item id and saves the item.
        faults.FailOnWrite(6);
        var output = new StringWriter();
        Assert.Equal(2, await ClubCommands.GrantAllCardsAsync(rt.Game, rt.Fut, output, catalog));
        Assert.True(faults.Triggered);
        var failed = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(FutJson.ParseObject)
            .OfType<JsonObject>()
            .Where(line => line.Text("status") == "failed")
            .Select(line => line.Int("resourceId"))
            .ToList();
        var failedCard = Assert.Single(failed);
        var granted = GrantedIds(rt, catalog);
        Assert.Equal(4, granted.Count);
        Assert.DoesNotContain(failedCard, granted.Keys);
        Assert.Equal(3, granted.Values.Max() - granted.Values.Min());

        // A rerun grants only the missing card, with the next id: the failed card consumed none.
        Assert.Equal(0, await ClubCommands.GrantAllCardsAsync(rt.Game, rt.Fut, new StringWriter(), catalog));
        var all = GrantedIds(rt, catalog);
        Assert.Equal(5, all.Count);
        Assert.Equal(granted.Values.Max() + 1, all[failedCard]);
    }

    /// <summary>
    /// Fails <paramref name="command"/> before its 1st, 2nd, … write or row lock until it completes. Every
    /// failed attempt must report failure and leave the club's committed rows unchanged. Returns the number
    /// of failed positions.
    /// </summary>
    private static int FailAtEveryWrite(TestRuntime rt, DatabaseFaultInjector faults, Func<bool> command)
    {
        var clubId = rt.Game.Store().ClubId;
        for (var position = 1; position <= MaxFaultPositions; position++)
        {
            var before = ClubStateSnapshot.Capture(rt.ConnectionString, clubId);
            faults.FailOnWrite(position);
            var succeeded = false;
            Exception? thrown = null;
            try
            {
                succeeded = command();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                thrown = error;
            }
            finally
            {
                faults.Disarm();
            }
            if (!faults.Triggered)
            {
                Assert.True(thrown is null && succeeded, $"the command failed without an injected fault: {thrown}");
                return position - 1;
            }
            Assert.False(succeeded && thrown is null, $"write {position.ToString(CultureInfo.InvariantCulture)} failed but the command reported success");
            Assert.Equal(before, ClubStateSnapshot.Capture(rt.ConnectionString, clubId));
        }
        throw new InvalidOperationException($"the command still wrote after {MaxFaultPositions} fault positions");
    }

    private static long StorePack(TestRuntime rt, string marker) =>
        long.Parse((rt.Game.Packs.Load().Obj("packs") ?? new JsonObject())
            .First(entry => entry.Value is JsonObject config && config.Bool("enabled", true) && JsonValues.HasValue(config.Get(marker)))
            .Key, CultureInfo.InvariantCulture);

    private static bool HasRewards(JsonObject source) => (source.Obj("rewards")?.Arr("entries")?.Count ?? 0) > 0;

    private static List<long> ChoiceIds(IEnumerable<JsonObject> choices) => choices.Select(choice => choice.Int("id")).ToList();

    private static Dictionary<long, long> GrantedIds(TestRuntime rt, IReadOnlyCollection<long> catalog) =>
        rt.Game.Store().ListItems("club")
            .Where(item => catalog.Contains(item.Int("resourceId")))
            .ToDictionary(item => item.Int("resourceId"), item => item.Int("id"));
}
