using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>Cards' non-player tabs: consumables, club customisation and managers, listed and granted (never edited).</summary>
public sealed partial class OperatorPanelTests
{
    private static async Task<JsonArray> ItemRowsAsync(Setup setup, string tab)
    {
        var reply = await Send(setup.Panel, "GET", $"/panel/api/cards/items?tab={tab}");
        Assert.Equal(200, reply.Status);
        return reply.Json!["items"]!.AsArray();
    }

    private static string GrantItem(string key, int count = 1, bool? untradeable = null) =>
        Body(untradeable is null ? new { op = "grantItem", item = key, count } : (object)new { op = "grantItem", item = key, count, untradeable });

    private static async Task<JsonArray> NewItemsAsync(Setup setup, long persona) =>
        (await Send(setup.Panel, "GET", $"/panel/api/players/{persona}/inventory?pile=new")).Json!["piles"]!["new"]!.AsArray();

    /// <summary>
    /// A manager the club does not hold yet. Opening a club that FIFA did not create tops it up with the best manager
    /// (ClubMaintenanceService), so that one is never picked either.
    /// </summary>
    private static async Task<JsonNode> UnownedManagerAsync(Setup setup, long persona)
    {
        var owned = (await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync("""
            SELECT d.native_resource_id FROM ultimateteam.items i JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
            JOIN ultimateteam.clubs c ON c.id = i.club_id WHERE c.persona_id = @persona AND i.family_id = 2
            """, row => row.GetInt64(0), ("persona", persona)), CancellationToken.None)).ToHashSet();
        if (setup.Runtime.Game.Managers.BestManagerId() is { } starter) owned.Add(starter);
        return (await ItemRowsAsync(setup, "managers")).First(row => !owned.Contains(row!["resourceId"]!.GetValue<long>()))!;
    }

    [Fact]
    public async Task ItemTabsListOnlyTheirOwnKinds()
    {
        using var setup = await CreateAsync();
        var consumables = await ItemRowsAsync(setup, "consumables");
        Assert.NotEmpty(consumables);
        Assert.All(consumables, row => Assert.Contains(row!["type"]!.GetValue<string>(), new[] { "chemistry_style", "position_modifier", "manager_league" }));
        Assert.Contains(consumables, row => row!["type"]!.GetValue<string>() == "position_modifier");
        Assert.Contains(consumables, row => row!["type"]!.GetValue<string>() == "manager_league");
        Assert.All(consumables, row => Assert.Equal(24, row!["countMax"]!.GetValue<int>()));

        var managers = await ItemRowsAsync(setup, "managers");
        Assert.NotEmpty(managers);
        Assert.All(managers, row => Assert.NotNull(row!["league"]));
        Assert.All(managers, row => Assert.Equal(1, row!["countMax"]!.GetValue<int>()));

        var customisation = await ItemRowsAsync(setup, "customisation");
        Assert.Contains(customisation, row => row!["type"]!.GetValue<string>() == "kit");
        Assert.All(customisation, row => Assert.Equal(1, row!["countMax"]!.GetValue<int>()));
        // Badges, balls and stadium cards store FIFA's localisation keys; the panel names them after the club, ball and venue.
        Assert.DoesNotContain(customisation, row => row!["name"]!.GetValue<string>().Contains("Name_", StringComparison.Ordinal));
        var badge = customisation.First(row => row!["type"]!.GetValue<string>() == "badge")!;
        Assert.Equal($"{badge["team"]!.GetValue<string>()} badge", badge["name"]!.GetValue<string>());
        Assert.All(customisation.Where(row => row!["type"]!.GetValue<string>() == "ball"), row => Assert.Matches("^Ball \\d+$", row!["name"]!.GetValue<string>()));
        var vanity = customisation.Where(row => row!["type"]!.GetValue<string>().StartsWith("vanity:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(vanity);
        Assert.All(vanity, row => Assert.True(row!["forcedUntradeable"]!.GetValue<bool>()));
        var starters = setup.Runtime.Game.Rules.Club.Vanity.StarterItems.Select(item => $"vanity:{item.Subtype}:{item.Asset}").ToHashSet();
        Assert.DoesNotContain(vanity, row => starters.Contains(row!["key"]!.GetValue<string>()));

        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/cards/items?tab=players")).Status);
        Assert.Equal(405, (await Send(setup.Panel, "POST", "/panel/api/cards/items?tab=managers", "{}")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumablesAndManagersAreGrantedWithTheSelectedTradeability(bool untradeable)
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var consumable = (await ItemRowsAsync(setup, "consumables")).First(row => row!["type"]!.GetValue<string>() == "position_modifier")!;
        var manager = await UnownedManagerAsync(setup, persona);
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(consumable["key"]!.GetValue<string>(), 3, untradeable))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(manager["key"]!.GetValue<string>(), 1, untradeable))).Status);

        var items = await NewItemsAsync(setup, persona);
        var granted = items.Where(item => item!["resourceId"]!.GetValue<long>() == consumable["resourceId"]!.GetValue<long>()).ToList();
        Assert.Equal(3, granted.Count);
        Assert.All(granted, item => Assert.Equal(untradeable, item!["untradeable"]!.GetValue<bool>()));
        var boss = Assert.Single(items, item => item!["resourceId"]!.GetValue<long>() == manager["resourceId"]!.GetValue<long>());
        Assert.Equal(untradeable, boss!["untradeable"]!.GetValue<bool>());
        Assert.Equal("manager", boss["kind"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AManagerLeagueCardIsGrantedAsAConsumable(bool untradeable)
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var league = (await ItemRowsAsync(setup, "consumables")).First(row => row!["type"]!.GetValue<string>() == "manager_league")!;
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(league["key"]!.GetValue<string>(), 2, untradeable))).Status);
        var granted = (await NewItemsAsync(setup, persona)).Where(item => item!["resourceId"]!.GetValue<long>() == league["resourceId"]!.GetValue<long>()).ToList();
        Assert.Equal(2, granted.Count);
        Assert.All(granted, item => Assert.Equal("consumable", item!["kind"]!.GetValue<string>()));
        Assert.All(granted, item => Assert.Equal(untradeable, item!["untradeable"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task ACustomisationItemIsGrantedOnceAndBadRequestsChangeNothing()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var rules = setup.Runtime.Game.Rules.Club;
        var owned = rules.InitialKits.Select(kit => kit.ResourceId)
            .Concat(rules.RenderedDefaultActiveItems(0).Select(item => item.Int("resourceId"))).ToHashSet();
        var kit = (await ItemRowsAsync(setup, "customisation"))
            .First(row => row!["type"]!.GetValue<string>() == "kit" && !owned.Contains(row["resourceId"]!.GetValue<long>()))!;
        var key = kit["key"]!.GetValue<string>();
        var before = (await NewItemsAsync(setup, persona)).Count;

        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(key, 2))).Status);
        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem("kit:1"))).Status);
        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem("players:158023"))).Status);
        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}",
            Body(new { op = "grantItem", item = key, count = 1, card = "158023:93:1" }))).Status);
        Assert.Equal(before, (await NewItemsAsync(setup, persona)).Count);

        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(key, 1, true))).Status);
        var item = Assert.Single(await NewItemsAsync(setup, persona), item => item!["resourceId"]!.GetValue<long>() == kit["resourceId"]!.GetValue<long>());
        Assert.True(item!["untradeable"]!.GetValue<bool>());
        Assert.Equal(409, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(key))).Status);
    }

    [Fact]
    public async Task AMyStadiumItemIsGrantedOnceAsAClubUnlock()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var vanity = (await ItemRowsAsync(setup, "customisation")).First(row => row!["type"]!.GetValue<string>().StartsWith("vanity:", StringComparison.Ordinal))!;
        var key = vanity["key"]!.GetValue<string>();
        var parts = key.Split(':');

        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(key, 2))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(key, 1, false))).Status);
        Assert.Equal(1L, await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<long>("""
            SELECT count(*) FROM ultimateteam.club_vanity_items v JOIN ultimateteam.clubs c ON c.id = v.club_id
            WHERE c.persona_id = @persona AND v.native_subtype = @subtype AND v.native_asset_id = @asset AND NOT v.stored
            """, ("persona", persona), ("subtype", int.Parse(parts[1])), ("asset", long.Parse(parts[2]))), CancellationToken.None));
        Assert.Equal(409, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(key))).Status);

        var starter = setup.Runtime.Game.Rules.Club.Vanity.StarterItems[0];
        Assert.Equal(409, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem($"vanity:{starter.Subtype}:{starter.Asset}"))).Status);
    }

    [Fact]
    public async Task AClubHoldsEachManagerOnce()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var manager = (await UnownedManagerAsync(setup, persona))["key"]!.GetValue<string>();
        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(manager, 2))).Status);
        var first = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(manager));
        Assert.True(first.Status == 200, first.Body);
        var again = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(manager));
        Assert.Equal(409, again.Status);
        Assert.Contains("each manager once", again.Body);
    }

    [Fact]
    public async Task TheInventoryNamesNonPlayerItemsAndListsGrantedMyStadiumItems()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var consumable = (await ItemRowsAsync(setup, "consumables")).First(row => row!["type"]!.GetValue<string>() == "manager_league")!;
        var customisation = await ItemRowsAsync(setup, "customisation");
        var vanity = customisation.First(row => row!["type"]!.GetValue<string>().StartsWith("vanity:", StringComparison.Ordinal))!;
        foreach (var key in new[] { consumable["key"]!.GetValue<string>(), vanity["key"]!.GetValue<string>() })
            Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(key))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", Body(new { op = "grantCard", card = "158023:93:1", count = 1 }))).Status);

        var reply = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}/inventory?pile=new");
        var items = reply.Json!["piles"]!["new"]!.AsArray();
        var league = Assert.Single(items, item => item!["resourceId"]!.GetValue<long>() == consumable["resourceId"]!.GetValue<long>())!;
        Assert.Equal(consumable["name"]!.GetValue<string>(), league["name"]!.GetValue<string>());
        Assert.Equal("Manager league", league["typeName"]!.GetValue<string>());
        Assert.False(league["removable"]!.GetValue<bool>());
        Assert.Null(league["attributes"]);
        // A player card carries its six face stats, as the Cards page shows them.
        var messi = Assert.Single(items, item => item!["resourceId"]!.GetValue<long>() == 158023)!;
        Assert.Equal(setup.Runtime.Game.Players.Find(158023)!.Get("face")!.AsArray().Select(value => value!.GetValue<long>()),
            messi["attributes"]!.AsArray().Select(value => value!.GetValue<long>()));
        Assert.Equal(6, messi["attributes"]!.AsArray().Count);
        var stadium = Assert.Single(items, item => item!["typeName"]?.GetValue<string>().StartsWith("My Stadium:", StringComparison.Ordinal) == true)!;
        Assert.Equal("clubItem", stadium["kind"]!.GetValue<string>());
        Assert.True(stadium["untradeable"]!.GetValue<bool>());
        Assert.False(stadium["removable"]!.GetValue<bool>());
        var detail = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}");
        Assert.True(detail.Json!["inventory"]!["new"]!.GetValue<long>() >= 2);
    }

    [Fact]
    public async Task RemovingItemsTakesOnlyPlayerCardsAndRefusesWhole()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var consumable = (await ItemRowsAsync(setup, "consumables")).First(row => row!["type"]!.GetValue<string>() == "position_modifier")!;
        var manager = await UnownedManagerAsync(setup, persona);
        var rules = setup.Runtime.Game.Rules.Club;
        var owned = rules.InitialKits.Select(kit => kit.ResourceId)
            .Concat(rules.RenderedDefaultActiveItems(0).Select(item => item.Int("resourceId"))).ToHashSet();
        var kit = (await ItemRowsAsync(setup, "customisation"))
            .First(row => row!["type"]!.GetValue<string>() == "kit" && !owned.Contains(row["resourceId"]!.GetValue<long>()))!;
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", Body(new { op = "grantCard", card = "158023:93:1", count = 1 }))).Status);
        foreach (var key in new[] { consumable["key"]!.GetValue<string>(), manager["key"]!.GetValue<string>(), kit["key"]!.GetValue<string>() })
            Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", GrantItem(key))).Status);
        var items = await NewItemsAsync(setup, persona);
        long IdOf(long resource) => items.Single(item => item!["resourceId"]!.GetValue<long>() == resource)!["id"]!.GetValue<long>();
        var card = IdOf(158023);
        var modifier = IdOf(consumable["resourceId"]!.GetValue<long>());
        var boss = IdOf(manager["resourceId"]!.GetValue<long>());
        var shirt = IdOf(kit["resourceId"]!.GetValue<long>());
        string Remove(params long[] ids) => Body(new { op = "removeItems", items = ids });

        // Refused whole: the card stays when the list also names a consumable, a manager, a kit or an unknown item.
        foreach (var refused in new[] { Remove(card, modifier), Remove(card, boss), Remove(card, shirt), Remove(card, 999_999_999) })
            Assert.Equal(409, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", refused)).Status);
        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", Remove())).Status);
        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", Body(new { op = "removeItems", items = new[] { card }, coins = 5 }))).Status);
        Assert.Equal(4, (await NewItemsAsync(setup, persona)).Count(item => new[] { card, modifier, boss, shirt }.Contains(item!["id"]!.GetValue<long>())));

        var coins = (await Send(setup.Panel, "GET", $"/panel/api/players/{persona}")).Json!["club"]!["coins"]!.GetValue<long>();
        var removed = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", Remove(card));
        Assert.Equal(200, removed.Status);
        Assert.Equal(coins, removed.Json!["club"]!["coins"]!.GetValue<long>());
        var left = (await NewItemsAsync(setup, persona)).Select(item => item!["id"]!.GetValue<long>()).ToHashSet();
        Assert.DoesNotContain(card, left);
        Assert.Contains(modifier, left);
        Assert.Contains(boss, left);
        Assert.Contains(shirt, left);
    }
}
