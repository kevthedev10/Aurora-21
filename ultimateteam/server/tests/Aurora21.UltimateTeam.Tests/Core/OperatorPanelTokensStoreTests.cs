using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed partial class OperatorPanelTests
{
    [Fact]
    public async Task TokenLineupPublishesIndependentPriceAndTradeabilityWithoutChangingTheCoinOffer()
    {
        using var setup = await CreateAsync();
        var original = (await CataloguePackAsync(setup, 74))["config"]!.DeepClone();
        var reply = await Send(setup.Panel, "PUT", "/panel/api/packs/tokens-lineup", Body(new
        {
            revision = await CatalogRevisionAsync(setup),
            listing = new[] { new { packId = 74, price = 25, enabled = false, untradeable = true } },
        }));
        Assert.Equal(200, reply.Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(200, (await ReleaseAvailabilityAsync(setup, "token", "74")).Status);
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        Assert.Equal(25, catalog["tokenListing"]![0]!["price"]!.GetValue<long>());
        Assert.True(JsonNode.DeepEquals(original, (await CataloguePackAsync(setup, 74))["config"]));
        var offer = setup.Runtime.Game.Packs.TokenOffers().Single();
        var offerId = long.Parse(offer.Key);
        Assert.True(offerId > 563);
        var view = setup.Runtime.Game.Packs.Capture(offerId);
        Assert.True(view.ForSale);
        Assert.Equal(74, view.Config.Int("base_pack_id"));
        Assert.Equal(25, view.Config.Int("points_price"));
        Assert.True(view.Config.Bool("store_untradeable"));
        Assert.Equal("gold", (await CataloguePackAsync(setup, 74))["art"]!.GetValue<string>());

        // Removing the offer stops sales but reserves its native id. Readding it keeps that id.
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/tokens-lineup",
            Body(new { revision = await CatalogRevisionAsync(setup), listing = Array.Empty<object>() }))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Empty(setup.Runtime.Game.Packs.TokenOffers());
        Assert.False(setup.Runtime.Game.Packs.Capture(offerId).ForSale);
        var newPack = await CreatePackAsync(setup, new { revision = await CatalogRevisionAsync(setup), pack = new { name = "ID reservation check" } });
        Assert.Equal(201, newPack.Status);
        Assert.True(newPack.Json!["id"]!.GetValue<long>() > offerId);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/tokens-lineup", Body(new
        {
            revision = await CatalogRevisionAsync(setup), listing = new[] { new { packId = 74, price = 50, enabled = false, untradeable = false } },
        }))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(offer.Key, setup.Runtime.Game.Packs.TokenOffers().Single().Key);
        Assert.False(setup.Runtime.Game.Packs.Capture(offerId).ForSale);
        Assert.Equal(50, setup.Runtime.Game.Packs.Capture(offerId).Config.Int("points_price"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100000000)]
    public async Task TokenLineupRequiresAPositiveBoundedTokenPrice(long price)
    {
        using var setup = await CreateAsync();
        var before = setup.Runtime.Game.Packs.TokenOffers();
        var reply = await Send(setup.Panel, "PUT", "/panel/api/packs/tokens-lineup", Body(new
        {
            revision = await CatalogRevisionAsync(setup), listing = new[] { new { packId = 74, price, enabled = true, untradeable = false } },
        }));
        Assert.Equal(400, reply.Status);
        Assert.True(JsonNode.DeepEquals(before, setup.Runtime.Game.Packs.TokenOffers()));
    }

    [Fact]
    public async Task LegacyNullTokenTradeabilityUsesTheUntradeableLineupDefault()
    {
        using var setup = await CreateAsync();
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/tokens-lineup", Body(new
        {
            revision = await CatalogRevisionAsync(setup), listing = new[] { new { packId = 74, price = 25, enabled = false, untradeable = (bool?)null } },
        }))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(200, (await ReleaseAvailabilityAsync(setup, "token", "74")).Status);
        Assert.True(setup.Runtime.Game.Packs.TokenOffers().Single().Value!.AsObject().Bool("store_untradeable"));
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        Assert.True(catalog["tokenListing"]![0]!["untradeable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PackEditorCannotSetStorePriceOrPlacement()
    {
        using var setup = await CreateAsync();
        foreach (var field in new[] { "price", "points_price", "store_section", "enabled" })
        {
            var value = field == "store_section" ? (object)"tokens" : field == "enabled" ? true : 25;
            var reply = await Send(setup.Panel, "PUT", "/panel/api/packs/catalog/74", Body(new
            {
                revision = await CatalogRevisionAsync(setup), pack = new Dictionary<string, object> { [field] = value },
            }));
            Assert.Equal(400, reply.Status);
            Assert.Contains("Store Lineup", reply.Json!["error"]!.GetValue<string>());
        }
    }
}
