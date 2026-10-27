using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// One kind of pack (docs/pack-odds-v3.md): a new pack is unassigned, the store lineup sells packs, published rules award
/// them, and the catalogue shows where each one is used. View-only packs keep their draw; unused packs can be deleted.
/// </summary>
public sealed partial class OperatorPanelTests
{
    private static async Task<(int Status, JsonNode? Json)> CreatePackAsync(Setup setup, object body)
    {
        var reply = await Send(setup.Panel, "POST", "/panel/api/packs/catalog", Body(body));
        return (reply.Status, reply.Json);
    }

    private static async Task<JsonObject> CataloguePackAsync(Setup setup, long id) =>
        (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!["packs"]!.AsArray()
            .Single(pack => pack!["id"]!.GetValue<long>() == id)!.AsObject();

    private static async Task<long[]> LineupAsync(Setup setup) =>
        (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!["publicPackIds"]!.AsArray().Select(node => node!.GetValue<long>()).ToArray();

    private static async Task<(int Status, JsonNode? Json)> StageLineupAsync(Setup setup, IEnumerable<long> ids)
    {
        var reply = await Send(setup.Panel, "PUT", "/panel/api/packs/lineup", Body(new { revision = await CatalogRevisionAsync(setup), publicPackIds = ids.ToArray() }));
        return (reply.Status, reply.Json);
    }

    private static async Task<(int Status, JsonNode? Json)> StagePricedLineupAsync(Setup setup, long id, long price)
    {
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        var listing = catalog["listing"]!.DeepClone().AsArray();
        listing.Add(new JsonObject { ["packId"] = id, ["price"] = price, ["enabled"] = false, ["untradeable"] = false });
        var reply = await Send(setup.Panel, "PUT", "/panel/api/packs/lineup", new JsonObject
        { ["revision"] = catalog["revision"]!.DeepClone(), ["listing"] = listing }.ToJsonString());
        return (reply.Status, reply.Json);
    }

    [Fact]
    public async Task ANewPackIsUnassignedWithItsArtUntilTheLineupSellsItAtAPrice()
    {
        using var setup = await CreateAsync();
        var expectedId = setup.Runtime.Game.Packs.Rules.NextPackId;
        var create = await CreatePackAsync(setup, new { revision = await CatalogRevisionAsync(setup),
            pack = new { name = "Unassigned test pack", size = 3, gold_weight = 100, rare_slots = 1, store_group = "gold",
                category_chances = new { totw = 0.006, icon = 0.0005 } } });
        Assert.Equal(201, create.Status);
        var id = create.Json!["id"]!.GetValue<long>();
        // The next id after every pack (the Icon Swaps tokens end at 563), clear of the red-pick ids.
        Assert.Equal(expectedId, id);
        Assert.False(setup.Runtime.Game.Packs.TokenOffers().ContainsKey(id.ToString()));
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);

        var row = await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync(
            "SELECT kind, store_group, enabled FROM ultimateteam.store_packs WHERE pack_id=@id",
            reader => (Kind: reader.GetString(0), Group: reader.GetString(1), Enabled: reader.GetBoolean(2)), ("id", id)));
        Assert.Equal(("store", "gold", false), row.Single());
        Assert.False(setup.Runtime.Game.Packs.IsForSale(id));
        // The art is the chosen category's, not the id band's.
        Assert.Equal(setup.Runtime.Game.Packs.Rules.StoreGroupArtIds["gold"], setup.Runtime.Game.Packs.Rules.StoreArtId(id, setup.Runtime.Game.Packs.GetPackConfig(id)));
        var listed = await CataloguePackAsync(setup, id);
        Assert.False(listed["inStore"]!.GetValue<bool>());
        Assert.Empty(listed["usedBy"]!.AsArray());
        Assert.Equal("gold", listed["art"]!.GetValue<string>());

        // A pack with no price is never sold for nothing.
        var free = await StageLineupAsync(setup, (await LineupAsync(setup)).Append(id));
        Assert.Equal(400, free.Status);
        Assert.Contains("price", free.Json!["error"]!.GetValue<string>());
        Assert.Equal(400, (await StagePricedLineupAsync(setup, id, 0)).Status);
        Assert.False(setup.Runtime.Game.Packs.IsForSale(id));
        Assert.Equal(200, (await StagePricedLineupAsync(setup, id, 2500)).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(200, (await ReleaseAvailabilityAsync(setup, "store", id.ToString())).Status);
        Assert.True(setup.Runtime.Game.Packs.IsForSale(id));
        Assert.True((await CataloguePackAsync(setup, id))["inStore"]!.GetValue<bool>());

        // Grant only keeps a pack out of the lineup.
        Assert.Equal(200, (await Send(setup.Panel, "PUT", $"/panel/api/packs/catalog/{id}",
            Body(new { revision = await CatalogRevisionAsync(setup), pack = new { admin_only = true } }))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.False(setup.Runtime.Game.Packs.IsForSale(id));
        Assert.Equal(400, (await StageLineupAsync(setup, (await LineupAsync(setup)).Append(id))).Status);
    }

    [Fact]
    public async Task AFormerRewardPackShowsWhereItIsUsedAndTheStoreCanSellIt()
    {
        using var setup = await CreateAsync();
        var rivals = await CataloguePackAsync(setup, 515);
        // The Rivals rewards are tables since migration 131: the pack is listed by where it is paid.
        Assert.Contains(rivals["usedBy"]!.AsArray(), path => path!.GetValue<string>() == "Rivals promotion: Division 1");
        Assert.False(rivals["inStore"]!.GetValue<bool>());
        Assert.Equal(0.05, rivals["config"]!["category_chances"]!["totw"]!.GetValue<double>());

        // Jumbo Gold 302 is a reward today; given a price, the lineup can sell it as well.
        Assert.False(setup.Runtime.Game.Packs.IsForSale(302));
        Assert.Equal(200, (await StagePricedLineupAsync(setup, 302, 20000)).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(200, (await ReleaseAvailabilityAsync(setup, "store", 302.ToString())).Status);
        Assert.True(setup.Runtime.Game.Packs.IsForSale(302));
        var jumbo = await CataloguePackAsync(setup, 302);
        Assert.True(jumbo["inStore"]!.GetValue<bool>());

        // Tradeability says how a pack is granted, not that anything awards it: the rule adds no usage.
        static List<string> UsedBy(JsonObject pack) => pack["usedBy"]!.AsArray().Select(path => path!.GetValue<string>()).ToList();
        var usedBefore = UsedBy(await CataloguePackAsync(setup, 319));
        var used534Before = UsedBy(await CataloguePackAsync(setup, 534));
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = "rules", ["key"] = new JsonObject { ["file"] = "tradeability", ["path"] = new JsonArray("packs", "534") },
            ["operation"] = "insert", ["after"] = new JsonObject { ["value"] = true },
        }.ToJsonString())).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(used534Before, UsedBy(await CataloguePackAsync(setup, 534)));
        Assert.Equal(usedBefore, UsedBy(await CataloguePackAsync(setup, 319)));
    }

    [Fact]
    public async Task AViewOnlyPackKeepsItsDrawButCanBeRenamedAndPriced()
    {
        using var setup = await CreateAsync();
        foreach (var id in new long[] { 540, 550, 560 })
            Assert.False(string.IsNullOrEmpty((await CataloguePackAsync(setup, id))["viewOnlyReason"]?.GetValue<string>()), $"pack {id} should be view only");
        Assert.Null((await CataloguePackAsync(setup, 302))["viewOnlyReason"]);
        // The Match Pack is retired (migration 141): only stored copies open, and the catalogue no longer lists it.
        Assert.DoesNotContain((await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!["packs"]!.AsArray(),
            pack => pack!["id"]!.GetValue<long>() == 544);
        var refused = await Send(setup.Panel, "PUT", "/panel/api/packs/catalog/550",
            Body(new { revision = await CatalogRevisionAsync(setup), pack = new { size = 9 } }));
        Assert.Equal(400, refused.Status);
        Assert.Contains("red pick", refused.Json!["error"]!.GetValue<string>());
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/catalog/550",
            Body(new { revision = await CatalogRevisionAsync(setup), pack = new { name = "Champions Red Pick" } }))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal("Champions Red Pick", setup.Runtime.Game.Packs.GetPackConfig(550).Text("name"));
    }

    [Fact]
    public async Task AnUnusedFormerRewardPackCanBeDeletedButOneARuleAwardsCannot()
    {
        using var setup = await CreateAsync();
        // Mixed Players 534 is now a Foundations set reward. Remove that reference in this
        // fixture first; the 86+ pick (515) remains a Rivals and objective reward.
        var referenced = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog/534/delete-check")).Json!;
        Assert.False(referenced["canDelete"]!.GetValue<bool>());
        Assert.Contains(referenced["blockers"]!.AsArray(), blocker => blocker!["code"]!.GetValue<string>() == "referenced");
        var foundation = setup.Runtime.Game.Sbc.Sets.Single(set => set.Key == "FND_LEAGUE_NATION_BASICS").Source.DeepClone().AsObject();
        foundation["rewards"] = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject { ["type"] = "COINS", ["value"] = 1 }),
        };
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = "sbc_set", ["key"] = new JsonObject { ["setKey"] = "FND_LEAGUE_NATION_BASICS" },
            ["operation"] = "update", ["after"] = new JsonObject { ["definition"] = foundation },
        }.ToJsonString())).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var unused = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog/534/delete-check")).Json!;
        Assert.True(unused["canDelete"]!.GetValue<bool>(), unused.ToJsonString());
        var used = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog/515/delete-check")).Json!;
        Assert.False(used["canDelete"]!.GetValue<bool>());
        Assert.Contains(used["blockers"]!.AsArray(), blocker => blocker!["code"]!.GetValue<string>() == "referenced");
        Assert.Equal(200, (await DeletePackAsync(setup, 534)).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Empty(setup.Runtime.Game.Packs.GetPackConfig(534));
        Assert.Equal(0, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.store_packs WHERE pack_id=534"));
        // Start-up no longer needs particular pack rows: the database still reads as ready.
        await using var source = Npgsql.NpgsqlDataSource.Create(setup.Runtime.ConnectionString);
        var readiness = await Aurora21.UltimateTeam.Core.Data.RelationalStorageReadiness.InspectForRuntimeAsync(source);
        Assert.Equal(Aurora21.UltimateTeam.Core.Data.RelationalStorageState.RelationalReady, readiness.State);
    }

    [Fact]
    public async Task ASpecialsOnlyPackNeedsOddsThatAddUpToEveryone()
    {
        using var setup = await CreateAsync();
        var revision = await CatalogRevisionAsync(setup);
        var specials = new { name = "Specials only", size = 3, rare_slots = 3, gold_weight = 100, promo_only = true, price = 100 };
        var partial = await CreatePackAsync(setup, new { revision, pack = new { specials.name, specials.size, specials.rare_slots, specials.gold_weight,
            specials.promo_only, category_chances = new { totw = 0.5, icon = 0.1 } } });
        Assert.Equal(400, partial.Status);
        Assert.Contains("100%", partial.Json!["error"]!.GetValue<string>());
        Assert.Equal(201, (await CreatePackAsync(setup, new { revision, pack = new { specials.name, specials.size, specials.rare_slots,
            specials.gold_weight, specials.promo_only, category_chances = new { totw = 0.9, icon = 0.1 } } })).Status);
    }

    [Fact]
    public async Task TheStoreListingSellsListedSwitchedOnPacksWithTheirOwnTradeability()
    {
        using var setup = await CreateAsync();
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        var listing = catalog["listing"]!.AsArray().OfType<JsonObject>().ToList();
        // Migration 132: today's lineup is the listing, every pack switched on and keeping the store default.
        Assert.Equal(catalog["publicPackIds"]!.AsArray().Select(id => id!.GetValue<long>()), listing.Select(entry => entry["packId"]!.GetValue<long>()));
        Assert.All(listing, entry => Assert.True(entry["enabled"]!.GetValue<bool>()));
        Assert.All(listing, entry => Assert.False(entry["untradeable"]!.GetValue<bool>()));
        var packs = catalog["packs"]!.AsArray().OfType<JsonObject>().ToDictionary(pack => pack["id"]!.GetValue<long>());
        var off = listing.Select(entry => entry["packId"]!.GetValue<long>()).First(id => !packs[id]["locked"]!.GetValue<bool>());
        var untradeable = listing.Select(entry => entry["packId"]!.GetValue<long>()).First(id => id != off && !packs[id]["locked"]!.GetValue<bool>() && packs[id]["config"]!["price"] is not null);

        // Switch one pack off (still listed) and sell another untradeable.
        var edited = new JsonArray(listing.Select(entry =>
        {
            var id = entry["packId"]!.GetValue<long>();
            return (JsonNode?)new JsonObject { ["packId"] = id, ["enabled"] = id != off, ["untradeable"] = id == untradeable ? true : null };
        }).ToArray());
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/lineup",
            new JsonObject { ["revision"] = catalog["revision"]!.DeepClone(), ["listing"] = edited }.ToJsonString())).Status);
        Assert.True(setup.Runtime.Game.Packs.IsForSale(off));
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.False(setup.Runtime.Game.Packs.IsForSale(off));
        var offPack = await CataloguePackAsync(setup, off);
        Assert.True(offPack["listed"]!.GetValue<bool>());
        Assert.False(offPack["config"]!["enabled"]!.GetValue<bool>());
        Assert.True(setup.Runtime.Game.Packs.IsForSale(untradeable));

        // A purchase opens what its listing says.
        setup.Runtime.Game.InUnit(work => work.AddCreditsToAsync(work.ClubId, 10_000_000));
        setup.Runtime.Game.Store().DeletePile("unassigned");
        var bought = setup.Runtime.CallRaw("POST", "/ut/game/fifa21/purchased/items", new JsonObject { ["packId"] = untradeable });
        Assert.True(bought.Status == 200, bought.Body.ToJsonString());
        var items = setup.Runtime.Game.Store().ListItems("unassigned");
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.True(item.Bool("untradeable"), item.ToJsonString()));

        // Unlisting stops the sale and forgets the choice; a required built-in pack cannot be switched off.
        var unlisted = new JsonArray(edited.OfType<JsonObject>().Where(entry => entry["packId"]!.GetValue<long>() != untradeable)
            .Select(entry => entry.DeepClone()).ToArray());
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/lineup",
            new JsonObject { ["revision"] = await CatalogRevisionAsync(setup), ["listing"] = unlisted }.ToJsonString())).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.False(setup.Runtime.Game.Packs.IsForSale(untradeable));
        Assert.Equal(0, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.store_packs WHERE pack_id=@id AND (store_listed OR store_untradeable=true)", ("id", untradeable)));
        if (packs.Values.FirstOrDefault(pack => pack["locked"]!.GetValue<bool>()) is { } locked)
        {
            var lockedOff = new JsonArray(unlisted.OfType<JsonObject>().Select(entry =>
            {
                var copy = entry.DeepClone().AsObject();
                if (copy["packId"]!.GetValue<long>() == locked["id"]!.GetValue<long>()) copy["enabled"] = false;
                return (JsonNode?)copy;
            }).ToArray());
            Assert.Equal(400, (await Send(setup.Panel, "PUT", "/panel/api/packs/lineup",
                new JsonObject { ["revision"] = await CatalogRevisionAsync(setup), ["listing"] = lockedOff }.ToJsonString())).Status);
        }

        // An old client cannot reactivate a listing without dates; a dated release keeps its tradeability.
        var ids = (await LineupAsync(setup)).Append(off).ToArray();
        Assert.Equal(422, (await StageLineupAsync(setup, ids)).Status);
        Assert.Equal(200, (await ReleaseAvailabilityAsync(setup, "store", off.ToString())).Status);
        Assert.True(setup.Runtime.Game.Packs.IsForSale(off));
    }

}
