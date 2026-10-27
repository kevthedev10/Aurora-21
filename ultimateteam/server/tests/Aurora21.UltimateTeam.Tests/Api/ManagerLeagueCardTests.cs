using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Manager league cards (migration 071, rules/cards.json managerLeagueCards; PLAN 3.8): one generated card per league with base
/// player cards, card asset 32 carrying its league; applied to a manager it moves him to that league for
/// chemistry. Placements: the Consumables Pack (4 of 12), the Match Pack's modifier slot (40%), half the manager hits of the
/// bronze and silver store packs, and each LEAGUE_* SBC.
/// </summary>
public sealed class ManagerLeagueCardTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>The Icons pseudo-league and the CONMEBOL competitions (Libertadores, Sudamericana) have no card.</summary>
    private static readonly long[] NotLeagues = { 2118, 1003, 1014 };

    private readonly ITestOutputHelper _output;

    public ManagerLeagueCardTests(ITestOutputHelper output) => _output = output;


    // A consumables pack and a bronze pack whose manager slot is half league cards, as the phase-1 store set them up
    // (archived launch scripts, owner 2026-09-29; the original bronze pack is migration 041 pack 1).
    private const string LeagueCardPacks = """
        INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,allowed_currencies,size,random_consumable_count,
            consumable_mix,store_group,always_present,display_only)
        VALUES(90,'store',true,'Synthetic consumables pack',3000,ARRAY['coins'],12,12,
            '{"manager_league": 4, "position_modifier": 4, "chemistry_style": 4}','special',false,false);
        INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,allowed_currencies,size,gold_weight,silver_weight,bronze_weight,
            rare_slots,manager_chance,chem_style_chance,manager_league_card_share)
        VALUES(91,'store',true,'Synthetic bronze pack',2000,ARRAY['coins'],12,0,0,100,1,0.2,0.25,0.5);
        """;

    [Fact]
    public async Task EveryLeagueWithBasePlayersHasAGeneratedCardThatRendersItsLeague()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var cards = rt.Game.Cards.ManagerLeagueCards;
        var rules = rt.Game.Rules.Cards.ManagerLeagueCards;

        // The leagues follow from the catalogue itself: every league with base cards (rareflag 0/1), managers or not.
        var baseLeagues = rt.Game.Players.Entries.Where(entry => entry.Value.Rareflag is 0 or 1)
            .Select(entry => entry.Value.Int("leagueId")).Where(id => id > 0).ToHashSet();
        var expected = baseLeagues.Except(NotLeagues).Order().ToList();
        Assert.Equal(expected, cards.Select(card => card.LeagueId).ToList());
        _output.WriteLine($"{cards.Count} manager league cards: " + string.Join("; ", cards.Select(card => $"{card.LeagueId} {card.Name} = {card.ResourceId}")));
        Assert.Subset(cards.Select(card => card.LeagueId).ToHashSet(), new HashSet<long> { 13, 16, 19, 31, 53 });
        // Every card is one FIFA files as a manager league card: a subtype in 300-351 (CardsDLL 0x31FCD0), one per league.
        Assert.All(cards, card => Assert.InRange(rules.Subtype(card.LeagueId) ?? 0, 300, 351));
        Assert.Equal(cards.Count, cards.Select(card => rules.Subtype(card.LeagueId)).Distinct().Count());

        // GENERATED ids: base + league, clear of every other definition.
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        async Task<long> Scalar(string sql)
        {
            await using var command = source.CreateCommand(sql);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        foreach (var card in cards)
        {
            Assert.Equal(rules.ResourceIdBase + card.LeagueId, card.ResourceId);
            Assert.False(string.IsNullOrEmpty(card.Name));
            Assert.Null(rt.Game.Players.Find(card.ResourceId));
            Assert.Null(rt.Game.Managers.Find(card.ResourceId));
            Assert.True(rt.Game.Cards.IsConsumableDefinition(card.ResourceId));
            Assert.Equal(card, rt.Game.Cards.ManagerLeagueCardForLeague(card.LeagueId));
        }
        var ids = string.Join(",", cards.Select(card => card.ResourceId));
        Assert.Equal(cards.Count, await Scalar($"SELECT count(*) FROM ultimateteam.item_definitions WHERE native_resource_id IN ({ids})"));
        Assert.Equal(cards.Count, await Scalar($"""
            SELECT count(*) FROM ultimateteam.item_definitions d JOIN ultimateteam.consumable_definitions c USING(definition_id)
            WHERE d.native_resource_id IN ({ids}) AND d.family_id = 3 AND d.native_card_asset_id = {rules.CardAssetId} AND d.native_item_type = 'training'
              AND d.native_subtype = c.native_effect_id AND c.consumable_kind = 'manager_league'
              AND c.native_consumable_type = {rules.ConsumableType} AND c.rating = {rules.Rating}
            """));

        // The card: asset 32, consumable type 5, quick sell 100, filed by FIFA as a manager league card through its subtype
        // (300 + the league's fcc_leagues row: the Premier League is 304) and carrying the league as leagueId; stored and read back
        // (the relational round trip), then as the pack reveal and Unassigned send it.
        var premier = rt.Game.Cards.ManagerLeagueCardForLeague(13)!;
        var store = rt.Game.Store();
        var built = rt.Fut.Items.BuildConsumable(premier.ResourceId, store.NextItemId(), "club", 2);
        store.SaveItem(built);
        foreach (var item in new[] { store.GetItem(built.Int("id"))!, rt.Fut.Native.PackItem(store.GetItem(built.Int("id"))!) })
        {
            Assert.Equal(premier.ResourceId, item.Int("resourceId"));
            Assert.Equal(32, item.Int("cardassetid"));
            Assert.Equal("training", item.Text("itemType"));
            Assert.Equal(304, item.Int("cardsubtypeid"));
            Assert.Equal(13, item.Int("leagueId"));
            Assert.Equal(5, item.Int("consumableType"));
            Assert.Equal(100, item.Int("discardValue"));
            Assert.Equal(2, item.Int("stackCount"));
            // FIFA reads the card's league from amount (a consumable's effect value), so amount is the league, not the stack size.
            Assert.Equal(13, item.Int("amount"));
            Assert.False(item.Bool("untradeable"));
        }

        // Club > Consumables counts it as a manager league modifier, and the apply screen lists its stack.
        Assert.Equal(2, rt.CallObject("GET", $"{G}/club/stats/consumables")["stat"].Objects()
            .Single(stat => stat["type"].S() == "consumablesTrainingManagerLeagueModifier")["typeValue"].L());
        var stack = rt.CallObject("GET", $"{G}/club/consumables/development")["itemData"].A().OfType<JsonObject>()
            .Single(row => row.Int("resourceId") == premier.ResourceId);
        Assert.Equal(2, stack.Int("count"));
        Assert.Equal(32, stack.Obj("item")!.Int("cardassetid"));
        Assert.Equal(304, stack.Obj("item")!.Int("cardsubtypeid"));
        Assert.Equal(13, stack.Obj("item")!.Int("amount"));

        // The AI market lists base players only, never a league card.
        Assert.DoesNotContain(rt.Fut.Market.AiListable(rt.Game.NowSeconds), id => rt.Game.Cards.ManagerLeagueCard(id) is not null);
    }

    [Fact]
    public async Task QuickSellPaysOneHundredAndAMatchPackCardNothing()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var card = rt.Game.Cards.ManagerLeagueCardForLeague(53)!;
        var fromStore = rt.Fut.Items.BuildConsumable(card.ResourceId, store.NextItemId(), "club");
        var fromMatchPack = rt.Fut.Items.MarkReward(rt.Fut.Items.BuildConsumable(card.ResourceId, store.NextItemId(), "club"), RewardSources.MatchPack, untradeable: true);
        store.SaveItem(fromStore);
        store.SaveItem(fromMatchPack);
        Assert.True(store.GetItem(fromMatchPack.Int("id"))!.Bool("untradeable"));
        Assert.Equal(0, store.GetItem(fromMatchPack.Int("id"))!.Int("discardValue"));

        var credits = store.Credits();
        var (balance, deleted) = await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[] { fromStore.Int("id") });
        Assert.Equal(new[] { fromStore.Int("id") }, deleted);
        Assert.Equal(credits + 100, balance);
        (balance, deleted) = await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[] { fromMatchPack.Int("id") });
        Assert.Single(deleted);
        Assert.Equal(credits + 100, balance);
    }

    [Fact]
    public async Task AnUntradeableConsumablesPackShowsTheQuickSellItPays()
    {
        // An untradeable store pack's league cards quick sell for nothing (rewardUntradeableCap 0); the reveal and Unassigned
        // showed the card's 100 because a stored zero read as missing.
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: LeagueCardPacks + "UPDATE ultimateteam.store_packs SET store_untradeable=true WHERE pack_id=90;");
        var store = rt.Game.Store();
        var cards = rt.Game.Cards;
        store.AddCredits(1_000_000);
        store.DeletePile("unassigned");
        bool IsLeagueCard(JsonObject item) => cards.ManagerLeagueCard(item.Int("resourceId")) is not null;
        List<JsonObject> LeagueCards(JsonObject body) => (body.Arr("itemData") ?? new JsonArray()).OfType<JsonObject>().Where(IsLeagueCard).ToList();
        var opened = LeagueCards(rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 90, ["currency"] = "COINS" }));
        var unassigned = LeagueCards(rt.CallObject("GET", $"{G}/purchased/items"));
        Assert.Equal(4, opened.Count);
        Assert.Equal(4, unassigned.Count);
        Assert.All(opened.Concat(unassigned), item =>
        {
            Assert.True(item.Bool("untradeable"));
            Assert.Equal(0, item.IntKeep("discardValue", -1));
        });

        var credits = store.Credits();
        var (balance, deleted) = await rt.Fut.Inventory.DiscardItemsAsync(unassigned.Select(item => (JsonNode?)JsonValue.Create(item.Int("id"))).ToArray());
        Assert.Equal(4, deleted.Count);
        Assert.Equal(credits, balance);
    }

    [Fact]
    public void ApplyingACardMovesTheManagerToItsLeagueAndChangesChemistry()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var cards = rt.Game.Cards;

        // Eleven gold base Premier League players of different nations, and a manager from another league whose
        // nation none of them shares: the manager adds nothing until he joins the Premier League.
        var premier = cards.ManagerLeagueCardForLeague(13)!;
        var players = rt.Game.Players.Entries
            .Where(entry => entry.Value.Int("leagueId") == 13 && entry.Value.Rareflag is 0 or 1 && entry.Value.Rating >= 75)
            .GroupBy(entry => entry.Value.Int("nation")).Select(group => group.First())
            .Take(11).Select(entry => entry.Key).ToList();
        Assert.Equal(11, players.Count);
        var starters = players.Select(id => (JsonObject?)rt.Fut.Items.BuildPlayer(id, 0, "club")).ToList();
        var nations = starters.Select(item => item!.Int("nation")).ToHashSet();
        var managerId = rt.Game.Managers.Entries.First(entry => entry.Value.Int("leagueId") is > 0 and not 13
            && cards.ManagerLeagueCardForLeague(entry.Value.Int("leagueId")) is not null && !nations.Contains(entry.Value.Int("nation"))).Key;
        var manager = rt.Fut.Items.BuildManager(managerId, store.NextItemId(), "club");
        store.SaveItem(manager);
        var ownLeague = manager.Int("leagueId");
        var stack = rt.Fut.Items.BuildConsumable(premier.ResourceId, store.NextItemId(), "club", 2);
        store.SaveItem(stack);
        var before = rt.Game.Chemistry.SquadChemistry("f442", starters, store.GetItem(manager.Int("id")))!.Value;

        // Refused on a player, and a card of the manager's own league is refused too; nothing is used up.
        var player = store.ListItems("club").First(item => item.Text("itemType", "player") == "player");
        var (playerStatus, playerBody, _) = rt.CallRaw("POST", $"{G}/item/resource/{premier.ResourceId}", new JsonObject { ["apply"] = new JsonArray(new JsonObject { ["id"] = player.Int("id") }) });
        Assert.Equal(400, playerStatus);
        Assert.Equal("manager league card target is not a manager", playerBody["reason"].S());
        var own = cards.ManagerLeagueCardForLeague(ownLeague)!;
        store.SaveItem(rt.Fut.Items.BuildConsumable(own.ResourceId, store.NextItemId(), "club"));
        var (sameStatus, _, _) = rt.CallRaw("POST", $"{G}/item/resource/{own.ResourceId}", new JsonObject { ["apply"] = new JsonArray(new JsonObject { ["id"] = manager.Int("id") }) });
        Assert.Equal(400, sameStatus);
        Assert.Equal(ownLeague, store.GetItem(manager.Int("id"))!.Int("leagueId"));
        Assert.Equal(1, store.ItemsWithResource(own.ResourceId).Sum(row => store.GetItem(row.Id)!.Int("stackCount")));
        // A player card cannot go on a manager either.
        var style = cards.ChemistryStyles.First(candidate => !candidate.Goalkeeper && candidate.PlayStyle != 250);
        Assert.Equal(400, rt.CallRaw("PUT", $"{G}/item/resource/{style.ResourceId}", new JsonObject { ["applyTo"] = manager.Int("id") }).Status);

        // Applied: the reply is only the changed manager, now in the Premier League; one card came off the stack of two.
        var (status, body, _) = rt.CallRaw("POST", $"{G}/item/resource/{premier.ResourceId}", new JsonObject { ["apply"] = new JsonArray(new JsonObject { ["id"] = manager.Int("id") }) });
        Assert.Equal(200, status);
        Assert.Equal(new[] { "itemData" }, body.AsObject().Select(entry => entry.Key));
        var reply = body["itemData"]![0]!.AsObject();
        Assert.Equal("manager", reply.Text("itemType"));
        Assert.Equal(manager.Int("id"), reply.Int("id"));
        Assert.Equal(13, reply.Int("leagueId"));
        var changed = store.GetItem(manager.Int("id"))!;
        Assert.Equal(13, changed.Int("leagueId"));
        Assert.Equal(1, store.GetItem(stack.Int("id"))!.Int("stackCount"));

        // Chemistry reads the new league: every starter not already at the cap gains the manager's point.
        var after = rt.Game.Chemistry.SquadChemistry("f442", starters, changed)!.Value;
        Assert.True(after > before, $"chemistry {before} -> {after}");
        _output.WriteLine($"squad chemistry with the manager on league {ownLeague}: {before}; on the Premier League: {after}");

        // The last card goes with the stack.
        changed["leagueId"] = ownLeague;
        store.SaveItem(changed);
        Assert.Equal(200, rt.CallRaw("POST", $"{G}/item/resource/{premier.ResourceId}", new JsonObject { ["applyTo"] = manager.Int("id") }).Status);
        Assert.Null(store.GetItem(stack.Int("id")));
        Assert.Equal(400, rt.CallRaw("POST", $"{G}/item/resource/{premier.ResourceId}", new JsonObject { ["applyTo"] = manager.Int("id") }).Status);
    }

    [Fact]
    public async Task AMatchPackLeagueCardOpensUntradeable()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var card = rt.Game.Cards.ManagerLeagueCardForLeague(19)!;
        var player = rt.Game.Players.Entries.First(entry => entry.Value.Rareflag == 1 && entry.Value.Rating is >= 75 and <= 81 && entry.Key <= 0x00FFFFFF).Key;
        var stored = new JsonObject { ["tier"] = "standard", ["highlight"] = false, ["players"] = new JsonArray(player), ["managerLeagueCard"] = card.ResourceId };
        Assert.Equal(card.ResourceId, stored.Int("managerLeagueCard"));
        rt.Game.Store().DeletePile("unassigned");
        var opened = await rt.Game.RunAsync("open", uow => rt.Fut.LegacyMatchPacks.OpenRolledAsync(uow, 544, stored, true), CancellationToken.None);
        Assert.Equal(2, opened.Int("numberItems"));
        var item = rt.Game.Store().ListItems("unassigned").Single(candidate => candidate.Int("resourceId") == card.ResourceId);
        Assert.True(item.Bool("untradeable"));
        Assert.Equal(308, item.Int("cardsubtypeid")); // the Bundesliga
    }

    [Fact]
    public async Task AConsumablesPackAndAManagerSlotShareHandOutLeagueCards()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, seedSql: LeagueCardPacks);
        var store = rt.Game.Store();
        var cards = rt.Game.Cards;
        var styles = rt.Game.Rules.Cards.ChemistryStyles;

        // Consumables Pack: 12 distinct cards, 4 of each kind, never Basic, tradeable from the store.
        store.AddCredits(1_000_000);
        store.DeletePile("unassigned");
        Assert.Equal("PURCHASECOMPLETE", rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 90, ["currency"] = "COINS" }).Text("state"));
        var consumables = store.ListItems("unassigned");
        Assert.Equal(12, consumables.Select(item => item.Int("resourceId")).Distinct().Count());
        Assert.Equal(4, consumables.Count(item => cards.ManagerLeagueCard(item.Int("resourceId")) is not null));
        Assert.Equal(4, consumables.Count(item => cards.PositionModifier(item.Int("resourceId")) is not null));
        Assert.Equal(4, consumables.Count(item => cards.ChemistryStyle(item.Int("resourceId")) is { } style
            && style.PlayStyle != styles.OutfieldDefault && style.PlayStyle != styles.GoalkeeperDefault));
        Assert.All(consumables.Where(item => cards.ManagerLeagueCard(item.Int("resourceId")) is not null), item => Assert.False(item.Bool("untradeable")));
        await rt.Fut.Inventory.DiscardItemsAsync(consumables.Select(item => (JsonNode?)JsonValue.Create(item.Int("id"))).ToArray());

        // A manager slot with a 0.5 league card share is half league cards. Every pack draws a manager slot
        // (manager_chance 1) so the split is measured on every roll.
        Assert.Equal(0.5, rt.Game.Packs.GetPackConfig(91).Float("manager_league_card_share"));
        var bronze = rt.Game.Packs.GetPackConfig(91).Copy();
        bronze["manager_chance"] = 1.0;
        const int rolls = 400;
        var (managers, leagueCards) = (0, 0);
        for (var roll = 0; roll < rolls; roll++)
        {
            var opened = await rt.Game.RunAsync("bronze", uow => rt.Fut.Packs.CreatePackAsync(uow, 91, bronze), CancellationToken.None);
            var items = (opened.Arr("itemData") ?? opened.Arr("items") ?? new JsonArray()).OfType<JsonObject>().ToList();
            managers += items.Count(item => item.Text("itemType") == "manager");
            leagueCards += items.Count(item => cards.ManagerLeagueCard(item.Int("resourceId")) is not null);
            Assert.Equal(1, items.Count(item => item.Text("itemType") == "manager" || cards.ManagerLeagueCard(item.Int("resourceId")) is not null));
            if (roll % 50 == 49)
            {
                store.DeletePile("unassigned");
            }
        }
        var margin = 4 * Math.Sqrt(0.25 / rolls);
        Assert.True(Math.Abs((double)leagueCards / rolls - 0.5) <= margin, $"league cards {leagueCards}, managers {managers} of {rolls}");
    }

    [Fact]
    public async Task EachLeagueSbcPaysItsLeaguesCard()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var sets = rt.Game.Sbc.Sets.Where(set => set.Key.StartsWith("LEAGUE_", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, sets.Count);
        foreach (var set in sets)
        {
            var challenge = set.Challenges[^1];
            var entry = challenge.Source.Obj("rewards")!.Arr("entries")!.OfType<JsonObject>().Single(reward => reward.Text("type") == "MANAGER_LEAGUE");
            var card = rt.Game.Cards.ManagerLeagueCardForLeague(entry.Obj("attributes")!.Int("league"))!;
            Assert.NotNull(card);

            // Shown as an item award carrying the card...
            var award = rt.Fut.Sbc.AwardJson(entry, set.Key)!;
            Assert.Equal("item", award.Text("type"));
            Assert.Equal(card.ResourceId, award.Obj("itemData")!.Int("resourceId"));
            Assert.Equal(32, award.Obj("itemData")!.Int("cardassetid"));
            Assert.Equal(rt.Game.Rules.Cards.ManagerLeagueCards.Subtype(card.LeagueId), award.Obj("itemData")!.Int("cardsubtypeid"));
            Assert.Equal(card.LeagueId, award.Obj("itemData")!.Int("leagueId"));

            // ...and paid into Unassigned, tradeable like every SBC grant (rules/tradeability.json).
            var granted = await rt.Fut.Sbc.GrantChallengeRewardsAsync(challenge);
            var item = granted.Single(candidate => candidate.Int("resourceId") == card.ResourceId);
            var owned = rt.Game.Store().GetItem(item.Int("id"))!;
            Assert.Equal("unassigned", owned.Text("pile"));
            Assert.False(owned.Bool("untradeable"));
            Assert.Equal(RewardSources.Sbc, item.Text(RewardSources.ItemKey));
        }
        // The Premier League SBC pays the Premier League card.
        Assert.Equal(13, sets.Single(set => set.Key == "LEAGUE_PL").Challenges[^1].Source.Obj("rewards")!.Arr("entries")!.OfType<JsonObject>()
            .Single(reward => reward.Text("type") == "MANAGER_LEAGUE").Obj("attributes")!.Int("league"));
    }
}
