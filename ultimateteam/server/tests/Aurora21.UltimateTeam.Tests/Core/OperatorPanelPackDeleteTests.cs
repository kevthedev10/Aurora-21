using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>Deleting a store pack definition from the operator panel: staged, validated and published like any pack edit.</summary>
public sealed partial class OperatorPanelTests
{
    private static string Body(object value) => JsonSerializer.Serialize(value);

    private static async Task<string> CatalogRevisionAsync(Setup setup) =>
        (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!["revision"]!.GetValue<string>();

    /// <summary>Creates and publishes a store pack (hidden and grant-only until the lineup says otherwise).</summary>
    private static async Task<long> PublishNewPackAsync(Setup setup, string name)
    {
        var create = await Send(setup.Panel, "POST", "/panel/api/packs/catalog",
            Body(new { revision = await CatalogRevisionAsync(setup), pack = new { name, size = 3, gold_weight = 100, rare_slots = 1 } }));
        Assert.Equal(201, create.Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        return create.Json!["id"]!.GetValue<long>();
    }

    private static async Task<(int Status, string Body, JsonNode? Json, Microsoft.AspNetCore.Http.IHeaderDictionary Headers)> DeletePackAsync(
        Setup setup, long id, bool force = false, long? copies = null, long? clubs = null) =>
        await Send(setup.Panel, "POST", $"/panel/api/packs/catalog/{id}/delete", force
            ? Body(new { revision = await CatalogRevisionAsync(setup), force = true, copies, clubs })
            : Body(new { revision = await CatalogRevisionAsync(setup) }));

    private static async Task<long> ClubAsync(Setup setup) => await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<long>(
        "SELECT id FROM ultimateteam.clubs WHERE persona_id=@persona", ("persona", PlayerIdentity.Of(DiscordId).PersonaId)));

    private static async Task GiveCopiesAsync(Setup setup, long pack, int count)
    {
        var club = await ClubAsync(setup);
        await setup.Runtime.Game.UnitOfWork.RunAsync(club, "test-copies", async work =>
        {
            for (var index = 0; index < count; index++)
                await work.ExecuteAsync("INSERT INTO ultimateteam.unopened_packs(club_id,pack_id,source,created) VALUES(@club,@pack,'test',1)",
                    ("club", club), ("pack", pack));
            return 0;
        });
    }

    private static async Task<long> CountAsync(Setup setup, string sql, params (string, object?)[] parameters) =>
        await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<long>(sql, parameters));

    [Fact]
    public async Task AStorePackWithNoDependentsIsDeletedThroughTheReviewedChangeset()
    {
        using var setup = await CreateAsync();
        var id = await PublishNewPackAsync(setup, "Delete me");
        var club = await ClubAsync(setup);
        await setup.Runtime.Game.UnitOfWork.RunAsync(club, "test-purchases", async work =>
        {
            await work.ExecuteAsync("INSERT INTO ultimateteam.club_pack_purchases(club_id,pack_id,purchases) VALUES(@club,@pack,2)", ("club", club), ("pack", id));
            await work.ExecuteAsync("INSERT INTO ultimateteam.community_pack_purchases(pack_id,purchases) VALUES(@pack,5)", ("pack", id));
            return 0;
        });
        var check = (await Send(setup.Panel, "GET", $"/panel/api/packs/catalog/{id}/delete-check")).Json!;
        Assert.True(check["canDelete"]!.GetValue<bool>(), check.ToJsonString());
        Assert.False(check["canForceDelete"]!.GetValue<bool>());
        Assert.Empty(check["blockers"]!.AsArray());

        var staged = await DeletePackAsync(setup, id);
        Assert.Equal(200, staged.Status);
        Assert.Equal("after-review", staged.Json!["applies"]!.GetValue<string>());
        // Nothing changes until publish; the pending banner sees the change.
        Assert.NotEmpty(setup.Runtime.Game.Packs.GetPackConfig(id));
        var open = (await Send(setup.Panel, "GET", "/panel/api/content/changeset")).Json!;
        var change = open["changeset"]!["changes"]!.AsArray().Single()!;
        Assert.Equal("store_pack_delete", change["area"]!.GetValue<string>());
        Assert.Equal(id, change["key"]!["packId"]!.GetValue<long>());
        Assert.True((await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}")).Json!["ok"]!.GetValue<bool>());

        var publish = await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{\"note\":\"delete pack\"}");
        Assert.Equal(200, publish.Status);
        Assert.Empty(setup.Runtime.Game.Packs.GetPackConfig(id));
        Assert.Equal(0, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.store_packs WHERE pack_id=@id", ("id", id)));
        Assert.Equal(0, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.club_pack_purchases WHERE pack_id=@id", ("id", id)));
        Assert.Equal(0, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.community_pack_purchases WHERE pack_id=@id", ("id", id)));
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        Assert.DoesNotContain(catalog["packs"]!.AsArray(), pack => pack!["id"]!.GetValue<long>() == id);
        var history = (await Send(setup.Panel, "GET", "/panel/api/content/history")).Json!;
        Assert.Contains(history["changesets"]!.AsArray().SelectMany(entry => entry!["changes"]!.AsArray()), row => row!["area"]!.GetValue<string>() == "store_pack_delete");
        Assert.Equal(404, (await Send(setup.Panel, "GET", $"/panel/api/packs/catalog/{id}/delete-check")).Status);
    }

    [Fact]
    public async Task DeleteIsRefusedWhileAClubOwnsAnUnopenedCopy()
    {
        using var setup = await CreateAsync();
        var id = await PublishNewPackAsync(setup, "Owned pack");
        await GiveCopiesAsync(setup, id, 2);
        var check = (await Send(setup.Panel, "GET", $"/panel/api/packs/catalog/{id}/delete-check")).Json!;
        Assert.False(check["canDelete"]!.GetValue<bool>());
        Assert.True(check["canForceDelete"]!.GetValue<bool>());
        Assert.Equal(2, check["unopened"]!["copies"]!.GetValue<int>());
        Assert.Equal(1, check["unopened"]!["clubs"]!.GetValue<int>());
        var refused = await DeletePackAsync(setup, id);
        Assert.Equal(409, refused.Status);
        Assert.Contains("unopened cop", refused.Json!["error"]!.GetValue<string>());
        // The generic changeset route cannot get around it either.
        var generic = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes",
            new JsonObject { ["area"] = "store_pack_delete", ["key"] = new JsonObject { ["packId"] = id }, ["operation"] = "hide" }.ToJsonString());
        Assert.Equal(409, generic.Status);
        Assert.Equal(2, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.unopened_packs WHERE pack_id=@id", ("id", id)));
        Assert.NotEmpty(setup.Runtime.Game.Packs.GetPackConfig(id));
    }

    [Fact]
    public async Task ForceDeleteRemovesTheCopiesAndTheDefinitionTogetherAndRecordsAReceipt()
    {
        using var setup = await CreateAsync();
        var id = await PublishNewPackAsync(setup, "Force me");
        var other = await PublishNewPackAsync(setup, "Keep my copies");
        await GiveCopiesAsync(setup, id, 3);
        await GiveCopiesAsync(setup, other, 1);
        // The confirmation must match what is really there.
        var wrong = await DeletePackAsync(setup, id, force: true, copies: 2, clubs: 1);
        Assert.Equal(409, wrong.Status);
        Assert.Equal(3, wrong.Json!["unopened"]!["copies"]!.GetValue<int>());
        Assert.Equal(400, (await Send(setup.Panel, "POST", $"/panel/api/packs/catalog/{id}/delete",
            Body(new { revision = await CatalogRevisionAsync(setup), force = true }))).Status);
        Assert.Equal(200, (await DeletePackAsync(setup, id, force: true, copies: 3, clubs: 1)).Status);
        var open = (await Send(setup.Panel, "GET", "/panel/api/content/changeset")).Json!;
        Assert.Equal("store_pack_force_delete", open["changeset"]!["changes"]!.AsArray().Single()!["area"]!.GetValue<string>());
        // Still nothing removed before publish.
        Assert.Equal(3, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.unopened_packs WHERE pack_id=@id", ("id", id)));

        // A copy that arrives after review makes the staged change conflict: the operator confirms the new counts.
        await GiveCopiesAsync(setup, id, 1);
        var stale = (await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}")).Json!;
        Assert.False(stale["ok"]!.GetValue<bool>());
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/discard", "{}")).Status);
        Assert.Equal(200, (await DeletePackAsync(setup, id, force: true, copies: 4, clubs: 1)).Status);
        Assert.True((await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}")).Json!["ok"]!.GetValue<bool>());
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);

        Assert.Equal(0, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.unopened_packs WHERE pack_id=@id", ("id", id)));
        Assert.Equal(0, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.store_packs WHERE pack_id=@id", ("id", id)));
        Assert.Equal(1, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.unopened_packs WHERE pack_id=@id", ("id", other)));
        Assert.Equal(1, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.store_packs WHERE pack_id=@id", ("id", other)));
        var receipt = await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync(
            "SELECT result::text FROM ultimateteam.panel_action_receipts WHERE operation='pack.force_delete'",
            row => JsonNode.Parse(row.GetString(0))!));
        var saved = Assert.Single(receipt);
        Assert.Equal(id, saved["packId"]!.GetValue<long>());
        Assert.Equal(4, saved["copies"]!.GetValue<int>());
        Assert.Equal(1, saved["clubs"]!.GetValue<int>());
        Assert.Equal("Force me", saved["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteIsRefusedForPacksInTheLineupBuiltInPacksRewardsAndRuleReferences()
    {
        using var setup = await CreateAsync();
        // In the published store lineup: Store Gold Pack.
        var inLineup = await DeletePackAsync(setup, 74);
        Assert.Equal(409, inLineup.Status);
        Assert.Contains("lineup", inLineup.Json!["error"]!.GetValue<string>());
        var lineupCheck = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog/74/delete-check")).Json!;
        Assert.False(lineupCheck["canDelete"]!.GetValue<bool>());
        Assert.False(lineupCheck["canForceDelete"]!.GetValue<bool>());

        // A mode completion pack cannot be deleted while its rule names it.
        var reward = await DeletePackAsync(setup, 75);
        Assert.Equal(409, reward.Status);
        Assert.Contains("seasons.matchCompletionRewards", reward.Json!["error"]!.GetValue<string>());

        // Built-in packs stay, even when hidden and unowned.
        var builtIn = await PublishNewPackAsync(setup, "Built-in pack");
        await setup.Runtime.Game.UnitOfWork.RunAsync(0, "test-always-present", async work =>
        {
            await work.ExecuteAsync("UPDATE ultimateteam.store_packs SET always_present=true WHERE pack_id=@id", ("id", builtIn));
            return 0;
        });
        var locked = await DeletePackAsync(setup, builtIn);
        Assert.Equal(409, locked.Status);
        Assert.Contains("built-in", locked.Json!["error"]!.GetValue<string>());

        // A reward that pays the pack (here an SBC reward) blocks both plain and force delete.
        var referenced = await PublishNewPackAsync(setup, "Reward target");
        await GiveCopiesAsync(setup, referenced, 1);
        var bronze = (JsonObject)setup.Runtime.Game.Sbc.BundledSet("UPG_BRONZE")!.DeepClone();
        bronze["challenges"]![0]!["rewards"]!["entries"]![0]!["attributes"]!["packId"] = referenced;
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = "sbc_set", ["key"] = new JsonObject { ["setKey"] = "UPG_BRONZE" },
            ["operation"] = "update", ["after"] = new JsonObject { ["definition"] = bronze },
        }.ToJsonString())).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var check = (await Send(setup.Panel, "GET", $"/panel/api/packs/catalog/{referenced}/delete-check")).Json!;
        Assert.False(check["canForceDelete"]!.GetValue<bool>());
        var message = string.Join(" ", check["blockers"]!.AsArray().Select(blocker => blocker!["message"]!.GetValue<string>()));
        Assert.Contains("SBC Bronze Upgrade (UPG_BRONZE)", message);
        Assert.Equal(409, (await DeletePackAsync(setup, referenced)).Status);
        var force = await DeletePackAsync(setup, referenced, force: true, copies: 1, clubs: 1);
        Assert.Equal(409, force.Status);
        Assert.Contains("UPG_BRONZE", force.Json!["error"]!.GetValue<string>());
        Assert.Equal(1, await CountAsync(setup, "SELECT count(*) FROM ultimateteam.unopened_packs WHERE pack_id=@id", ("id", referenced)));
        Assert.NotEmpty(setup.Runtime.Game.Packs.GetPackConfig(referenced));
    }

    [Fact]
    public void RuleReferencesAreFoundByKeyAndByValueButNotByUnrelatedNumbers()
    {
        var rules = JsonNode.Parse("""
            {
              "_note": { "pack": 700 },
              "rewards": [ { "packId": 700, "count": 1 }, { "packId": 701, "count": 700 } ],
              "draft": { "runRewards": { "byWins": [ { "coins": 700, "packs": [300, 700] } ], "neverPacks": [702] } },
              "tradeability": { "packs": { "700": true }, "sourcePacks": { "objective": { "703": true } } },
              "sbc": { "rewardPacks": { "gold": 300, "special": 704 }, "playerPickPacks": { "82": 705 } },
              "packs": { "rewardPacks": { "544": { "name": "Match Pack", "size": 700 } }, "maxPackSize": 700, "nextPackId": 700 }
            }
            """)!;
        string[] Find(long id) { var hits = new List<string>(); Aurora21.UltimateTeam.Server.Panel.PackReferences.Find(rules, id, "doc", hits); return [.. hits]; }
        Assert.Equal(["doc.rewards[0].packId", "doc.draft.runRewards.byWins[0].packs", "doc.tradeability.packs.700"], Find(700));
        Assert.Equal(["doc.rewards[1].packId"], Find(701));
        Assert.Equal(["doc.draft.runRewards.neverPacks"], Find(702));
        Assert.Equal(["doc.tradeability.sourcePacks.objective.703"], Find(703));
        Assert.Equal(["doc.sbc.rewardPacks.special"], Find(704));
        Assert.Equal(["doc.sbc.playerPickPacks.82"], Find(705));
        Assert.Empty(Find(6));
    }
}
