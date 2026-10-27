using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Tests.Api;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// The SBC catalogue and the live list (migration 142): every set stays in the catalogue, and the game shows one only while
/// it is listed and switched on. The list replaces rules/sbc.json hiddenSets and the panel's per-set hide.
/// </summary>
public sealed class SbcLiveTests
{
    private static ContentChangesets Changes(TestRuntime rt) =>
        new(rt.Game, ContentAreas.Default.Concat<IContentArea>([new SbcSetArea(rt.Game), new SbcLiveArea(rt.Game)]).ToList());

    private static JsonObject Listing(params (string Key, bool Enabled)[] entries) => new()
    {
        ["listing"] = new JsonArray(entries.Select(entry => (JsonNode)new JsonObject { ["setKey"] = entry.Key, ["enabled"] = entry.Enabled }).ToArray()),
    };

    private static JsonObject LiveKey => new() { ["scope"] = "live" };

    private static JsonObject SetKey(string key) => new() { ["setKey"] = key };

    [Fact]
    public async Task UpgradeFrom140ListsWhatTheGameShowedAndDropsTheHiddenSetsRule()
    {
        using var rt = TestRuntime.Create();
        var custom = (JsonObject)rt.Game.Sbc.BundledSet("UPG_BRONZE")!.DeepClone();
        custom["name"] = "Panel set";
        custom["challenges"]![0]!["id"] = "PANEL_1";
        // The state before migration 142: an operator's stored hiddenSets (only the player picks), a bundled set hidden in
        // the panel, a set added in the panel and one added and hidden there.
        var connectionString = PostgresTestCluster.CreateDatabase();
        try
        {
            await using var source = NpgsqlDataSource.Create(connectionString);
            await Migration140Database.InstallAsync(source);
            await using (var command = source.CreateCommand("""
                DELETE FROM ultimateteam.content_rules WHERE file='sbc' AND path->>0='hiddenSets';
                INSERT INTO ultimateteam.content_rules(file,path,value,ordinal) VALUES ('sbc','["hiddenSets",0]','"UP_*"',9000);
                INSERT INTO ultimateteam.sbc_sets(set_key, definition, hidden) VALUES
                    ('FND_LETS_GET_STARTED', @started::jsonb, true), ('PANEL_SET', @custom::jsonb, false), ('PANEL_OFF', @off::jsonb, true);
                """))
            {
                command.Parameters.AddWithValue("started", rt.Game.Sbc.BundledSet("FND_LETS_GET_STARTED")!.ToJsonString());
                command.Parameters.AddWithValue("custom", custom.ToJsonString());
                command.Parameters.AddWithValue("off", Renamed(custom, "PANEL_OFF_1").ToJsonString());
                await command.ExecuteNonQueryAsync();
            }
            using var upgraded = TestRuntime.Create(connectionString: connectionString);

            var live = upgraded.Game.Sbc.Live;
            var expected = upgraded.Game.Sbc.Sets.Select(set => set.Key).Where(key => !key.StartsWith("UP_", StringComparison.Ordinal)).ToList();
            Assert.Equal(expected.Order(StringComparer.Ordinal), live.Keys.Order(StringComparer.Ordinal));
            Assert.False(live["FND_LETS_GET_STARTED"]);
            Assert.False(live["PANEL_OFF"]);
            Assert.True(live["PANEL_SET"]);
            Assert.True(live["LEAGUE_PL"]);
            Assert.False(upgraded.Game.Sbc.IsLive("UP_82"));
            Assert.Contains(upgraded.Game.Sbc.Sets, set => set.Key == "UP_82");
            var rules = upgraded.Game.InUnit(async work => (await RuleRows.DocumentsAsync(work))["sbc"]);
            Assert.DoesNotContain("hiddenSets", rules, StringComparison.Ordinal);
            Assert.False(upgraded.Game.InUnit(work => work.ScalarAsync<bool>("""
                SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'ultimateteam' AND table_name = 'sbc_sets' AND column_name = 'hidden')
                """)));

            // An ordinary restart leaves an operator's list alone.
            upgraded.Game.InUnit(work => work.ExecuteAsync("""
                DELETE FROM ultimateteam.sbc_live WHERE set_key <> 'UPG_GOLD';
                """));
            using var restarted = TestRuntime.Create(connectionString: upgraded.Detach());
            Assert.Equal(["UPG_GOLD"], restarted.Game.Sbc.Live.Keys);
        }
        finally { PostgresTestCluster.DropDatabase(connectionString); }
    }

    private static JsonObject Renamed(JsonObject set, string challengeKey)
    {
        var copy = (JsonObject)set.DeepClone();
        copy["challenges"]![0]!["id"] = challengeKey;
        return copy;
    }

    [Fact]
    public async Task TheLiveListDecidesWhatTheGameShows()
    {
        using var rt = TestRuntime.Create();
        Assert.False(rt.Fut.Sbc.IsHidden(rt.Game.Sbc.FindSet("UPG_GOLD")!));
        var listing = rt.Game.Sbc.Live.Where(entry => entry.Key != "UPG_SILVER")
            .Select(entry => (entry.Key, entry.Key != "UPG_GOLD")).Append(("UP_82", true)).ToArray();
        var changes = Changes(rt);
        await changes.SetChangeAsync(SbcLiveArea.AreaName, LiveKey, ContentOperation.Update, Listing(listing), null);
        Assert.True((await changes.ValidateAsync()).Ok);
        await changes.PublishAsync("live SBCs", "test");

        Assert.True(rt.Game.Sbc.IsLive("UP_82"));
        Assert.False(rt.Game.Sbc.IsLive("UPG_GOLD"));
        Assert.False(rt.Game.Sbc.Live["UPG_GOLD"]);
        Assert.False(rt.Game.Sbc.Live.ContainsKey("UPG_SILVER"));
        Assert.True(rt.Fut.Sbc.IsHidden(rt.Game.Sbc.FindSet("UPG_GOLD")!));
        Assert.Contains(rt.Game.Sbc.Sets, set => set.Key == "UPG_SILVER");

        var unknown = Changes(rt);
        await unknown.SetChangeAsync(SbcLiveArea.AreaName, LiveKey, ContentOperation.Update, Listing(("NO_SUCH_SET", true)), null);
        Assert.False((await unknown.ValidateAsync()).Ok);
        var duplicate = await Assert.ThrowsAsync<ContentChangeException>(() => Changes(rt).SetChangeAsync(SbcLiveArea.AreaName, LiveKey,
            ContentOperation.Update, Listing(("UPG_GOLD", true), ("UPG_GOLD", false)), null));
        Assert.Equal(400, duplicate.Status);
    }

    [Fact]
    public async Task ChallengesKeepTheirIdsWhenReorderedRemovedOrAdded()
    {
        using var rt = TestRuntime.Create();
        var set = rt.Game.Sbc.FindSet("FND_LETS_GET_STARTED")!;
        var ids = set.Challenges.ToDictionary(challenge => challenge.Key, challenge => challenge.Id);
        // An edit of the bundled set (its retail provenance kept): CH_3 first, CH_1 removed, a new challenge last.
        var definition = (JsonObject)rt.Game.Sbc.BundledSet("FND_LETS_GET_STARTED")!.DeepClone();
        var challenges = definition["challenges"]!.AsArray();
        var third = challenges[2]!.DeepClone();
        var added = challenges[1]!.DeepClone();
        added["id"] = "CH_NEW_1";
        definition["challenges"] = new JsonArray(third, challenges[1]!.DeepClone(), added);
        var changes = Changes(rt);
        await changes.SetChangeAsync(SbcSetArea.AreaName, SetKey("FND_LETS_GET_STARTED"), ContentOperation.Update,
            new JsonObject { ["definition"] = definition }, null);
        var validation = await changes.ValidateAsync();
        Assert.True(validation.Ok, string.Join("; ", validation.Errors));
        await changes.PublishAsync("reorder", "test");

        var edited = rt.Game.Sbc.FindSet("FND_LETS_GET_STARTED")!;
        Assert.Equal(set.Id, edited.Id);
        Assert.Equal(["CH_3", "CH_2", "CH_NEW_1"], edited.Challenges.Select(challenge => challenge.Key));
        Assert.Equal(ids["CH_3"], edited.Challenges[0].Id);
        Assert.Equal(ids["CH_2"], edited.Challenges[1].Id);
        // Number 1 belonged to the removed CH_1: the new challenge takes the next unused number.
        Assert.Equal(set.Id + 4, edited.Challenges[2].Id);
        Assert.Null(rt.Game.Sbc.FindChallenge(ids["CH_1"]));
    }

    [Fact]
    public async Task OnlyAnUnlistedPanelSetIsRemovedAndABundledSetIsReverted()
    {
        using var rt = TestRuntime.Create();
        var custom = Renamed(rt.Game.Sbc.BundledSet("UPG_BRONZE")!, "PANEL_ONLY_1");
        custom.Remove("id");
        var changes = Changes(rt);
        await changes.SetChangeAsync(SbcSetArea.AreaName, SetKey("PANEL_ONLY"), ContentOperation.Insert, new JsonObject { ["definition"] = custom }, null);
        await changes.SetChangeAsync(SbcLiveArea.AreaName, LiveKey, ContentOperation.Update,
            Listing(rt.Game.Sbc.Live.Select(entry => (entry.Key, entry.Value)).Append(("PANEL_ONLY", true)).ToArray()), null);
        Assert.True((await changes.ValidateAsync()).Ok);
        await changes.PublishAsync("add a set", "test");
        Assert.True(rt.Game.Sbc.IsLive("PANEL_ONLY"));

        var listed = await Assert.ThrowsAsync<ContentChangeException>(() => Changes(rt).SetChangeAsync(SbcSetArea.AreaName, SetKey("PANEL_ONLY"), ContentOperation.Hide, null, null));
        Assert.Equal(409, listed.Status);
        var bundled = await Assert.ThrowsAsync<ContentChangeException>(() => Changes(rt).SetChangeAsync(SbcSetArea.AreaName, SetKey("UPG_SILVER"), ContentOperation.Hide, null, null));
        Assert.Equal(409, bundled.Status);

        var off = Changes(rt);
        await off.SetChangeAsync(SbcLiveArea.AreaName, LiveKey, ContentOperation.Update,
            Listing(rt.Game.Sbc.Live.Where(entry => entry.Key != "PANEL_ONLY").Select(entry => (entry.Key, entry.Value)).ToArray()), null);
        await off.PublishAsync("unlist", "test");
        var remove = Changes(rt);
        await remove.SetChangeAsync(SbcSetArea.AreaName, SetKey("PANEL_ONLY"), ContentOperation.Hide, null, null);
        await remove.PublishAsync("remove", "test");
        Assert.Null(rt.Game.Sbc.FindSet("PANEL_ONLY"));
    }
}
