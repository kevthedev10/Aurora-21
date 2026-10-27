using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class SquadFoundationsImportTests
{
    private static JsonObject Key(string path) => new() { ["file"] = "objectives", ["path"] = new JsonArray(path) };
    private static IEnumerable<long> DescendantsNamed(JsonNode? node, string key) => node switch
    {
        JsonObject obj => obj.SelectMany(pair => pair.Key == key && pair.Value is JsonValue value
            ? [value.GetValue<long>()] : DescendantsNamed(pair.Value, key)),
        JsonArray array => array.SelectMany(item => DescendantsNamed(item, key)),
        _ => [],
    };
    // Retail FIFA 21 conditions (migration 178, live 10-08); the other imported groups keep 169's basic ones.
    private static readonly Dictionary<string, (long League, string AssistName)> RetailLeagues = new()
    {
        ["eredivisie_squad_foundation"] = (10, "Assistance Required"),
        ["efl_squad_foundation"] = (14, "Assistance Required"),
        ["mls_squad_foundation"] = (39, "Made by MLS"),
    };
    private static async ValueTask ApplyMigration(IUnitOfWork work, string name)
    {
        using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/" + name)!;
        using var reader = new StreamReader(stream);
        await work.ExecuteAsync(await reader.ReadToEndAsync());
    }

    [Fact]
    public void FreshImportIsDisabledAndUsesBasicMatchConditionsWithExactRewardCards()
    {
        using var rt = TestRuntime.Create();
        var rules = rt.Game.Rules.Objectives;
        var groups = rules.GrindGroups.Where(group => group.SourceKey is not null).ToArray();
        Assert.Equal(7, groups.Length);
        Assert.All(groups, group => Assert.False(rules.IsLive(group.Id)));
        Assert.Equal([10L, 10L, 9L, 4L], groups[0].Objectives.Select(objective => objective.Target));
        Assert.Equal([15L, 15L, 15L, 10L], groups[^1].Objectives.Select(objective => objective.Target));
        Assert.All(groups.SelectMany(group => group.Objectives), objective =>
            Assert.Equal(["rivals", "weekendLeague"], objective.Requirements!.Modes));
        var basic = groups.Where(group => !RetailLeagues.ContainsKey(group.SourceKey!)).ToArray();
        Assert.Equal(4, basic.Length);
        Assert.All(basic, group => Assert.Equal(["scoringMatches", "goals", "matches", "wins"], group.Objectives.Select(objective => objective.Stat)));
        foreach (var (source, (league, assistName)) in RetailLeagues)
        {
            var retail = groups.Single(group => group.SourceKey == source);
            Assert.Equal(["goals", "assists", "matches", "wins"], retail.Objectives.Select(objective => objective.Stat));
            Assert.Equal([15L, 15L, 15L, 10L], retail.Objectives.Select(objective => objective.Target));
            Assert.Equal(assistName, retail.Objectives[1].Name);
            Assert.All(retail.Objectives, objective =>
            {
                Assert.Equal(league, objective.Requirements!.StartingSquad!.LeagueId);
                Assert.Equal(7, objective.Requirements.StartingSquad.MinimumCount);
            });
            Assert.Equal([league, league, null, null], retail.Objectives.Select(objective => objective.Requirements!.Player?.LeagueId));
        }
        Assert.All(basic.SelectMany(group => group.Objectives), objective =>
        {
            Assert.Null(objective.Requirements!.StartingSquad);
            Assert.Null(objective.Requirements.Player);
            Assert.Null(objective.Requirements.GoalType);
            Assert.Null(objective.Requirements.AssistType);
        });
        Assert.Equal(50520590, groups[0].Award.Players.Single().ResourceId);
        Assert.Null(rules.ScoringEvents.Finesse);
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "rules", "objectives.json")))!.AsObject();
        document["live"]!.AsArray().Single(node => node!.AsObject().Int("group") == groups[0].Id)!["enabled"] = true;
        var parsed = RulesJson.Deserialize<ObjectiveRules>(document.ToJsonString(), "test");
        Assert.Null(parsed.LiveProblem());
    }

    [Fact]
    public void UpgradePreservesOldLiveOrderAndClaimsAndAllocatesIdsPastRetiredHistory()
    {
        using var rt = TestRuntime.Create();
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                DELETE FROM ultimateteam.content_rules WHERE file='objectives' AND
                    (path->>0='live' OR path->>0='grindGroups' AND (path->>1)::integer>=10);
                INSERT INTO ultimateteam.objective_group_starts(group_id) VALUES(50000);
                INSERT INTO ultimateteam.objective_claims(club_id,scope,claim_id,ordinal) VALUES(@club,'objective',90000,0);
                DELETE FROM ultimateteam.schema_migrations WHERE version=154;
                """);
            await ApplyMigration(work, "154-squad-foundations-objectives.sql");
            return 0;
        });
        JsonObject Document() => rt.Game.InUnit(async work => JsonNode.Parse((await RuleRows.DocumentsAsync(work))["objectives"])!.AsObject());
        var document = Document();
        var groups = document["grindGroups"]!.AsArray().OfType<JsonObject>().Where(group => group["sourceKey"] is not null).ToArray();
        Assert.Equal(7, groups.Length);
        Assert.All(groups, group => Assert.True(group.Int("id") > 50000));
        Assert.All(groups.SelectMany(group => group["objectives"]!.AsArray()), objective => Assert.True(objective!.AsObject().Int("id") > 90000));
        var live = document["live"]!.AsArray();
        Assert.Equal([1L,8L,2L,3L,4L,5L,6L,7L,20L,21L,22L,90L], live.Take(12).Select(node => node!.AsObject().Int("group")));
        Assert.All(live.Skip(12), node => Assert.False(node!.AsObject().Bool("enabled")));
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.objective_claims WHERE club_id=@club AND claim_id=90000)")));
        var before = document.ToJsonString();
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("DELETE FROM ultimateteam.schema_migrations WHERE version=154");
            await ApplyMigration(work,"154-squad-foundations-objectives.sql");
            return 0;
        });
        Assert.Equal(before, Document().ToJsonString());
    }

    [Fact]
    public void BasicUpgradePreservesIdsRewardsLiveStateAndExistingProgressAndClaims()
    {
        using var rt = TestRuntime.Create();
        JsonObject Document() => rt.Game.InUnit(async work => JsonNode.Parse((await RuleRows.DocumentsAsync(work))["objectives"])!.AsObject());
        rt.Game.InUnit(async work =>
        {
            // Recreate the shipped 154 definitions in this throwaway database, then upgrade them.
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                DELETE FROM ultimateteam.content_rules WHERE file='objectives' AND
                    (path->>0='live' OR path->>0='grindGroups' AND (path->>1)::integer>=10);
                DELETE FROM ultimateteam.schema_migrations WHERE version IN (154,169);
                """);
            await ApplyMigration(work, "154-squad-foundations-objectives.sql");
            return 0;
        });
        var before = Document();
        var group = before["grindGroups"]!.AsArray().OfType<JsonObject>().First(g => g["sourceKey"] is not null);
        var id = group["objectives"]![0]!.AsObject().Int("id");
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.objective_counters(club_id,stat_code,value) VALUES(@club,@stat,3) ON CONFLICT(club_id,stat_code) DO UPDATE SET value=3;
                INSERT INTO ultimateteam.objective_claims(club_id,scope,claim_id,ordinal) VALUES(@club,'objective',@id,0);
                """, ("stat", $"objective:{id}"), ("id", id));
            await ApplyMigration(work, "169-basic-foundations-objectives.sql");
            return 0;
        });
        var after = Document();
        Assert.Equal(before["live"]!.ToJsonString(), after["live"]!.ToJsonString());
        var original = before["grindGroups"]!.AsArray();
        var upgraded = after["grindGroups"]!.AsArray();
        Assert.Equal(original.Count, upgraded.Count);
        for (var index = 0; index < original.Count; index++)
        {
            var old = original[index]!.AsObject(); var next = upgraded[index]!.AsObject();
            if (old["sourceKey"] is null) { Assert.Equal(old.ToJsonString(), next.ToJsonString()); continue; }
            Assert.Equal(old.Int("id"), next.Int("id"));
            Assert.Equal(old["award"]!.ToJsonString(), next["award"]!.ToJsonString());
            Assert.Equal(old.Int("xp"), next.Int("xp"));
            for (var objective = 0; objective < old["objectives"]!.AsArray().Count; objective++)
            {
                var oldObjective = old["objectives"]![objective]!.AsObject();
                var nextObjective = next["objectives"]![objective]!.AsObject();
                Assert.Equal(oldObjective.Int("id"), nextObjective.Int("id"));
                Assert.Equal(oldObjective["award"]!.ToJsonString(), nextObjective["award"]!.ToJsonString());
                Assert.Single(nextObjective["requirements"]!.AsObject());
            }
            Assert.Equal("scoringMatches", next["objectives"]![0]!["stat"]!.GetValue<string>());
            Assert.Equal(10, next["objectives"]![0]!.AsObject().Int("target"));
        }
        Assert.Equal(3, rt.Game.InUnit(work => work.ScalarAsync<long>("SELECT value FROM ultimateteam.objective_counters WHERE club_id=@club AND stat_code=@stat", ("stat", $"objective:{id}"))));
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.objective_claims WHERE club_id=@club AND claim_id=@id)", ("id", id))));
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("DELETE FROM ultimateteam.schema_migrations WHERE version=169");
            await ApplyMigration(work, "169-basic-foundations-objectives.sql"); return 0;
        });
        Assert.Equal(after.ToJsonString(), Document().ToJsonString());
    }

    [Fact]
    public void RetailUpgradeRestoresEredivisieAndEflConditionsAndMatchesTheSeed()
    {
        using var rt = TestRuntime.Create();
        JsonObject Document() => rt.Game.InUnit(async work => JsonNode.Parse((await RuleRows.DocumentsAsync(work))["objectives"])!.AsObject());
        var seed = Document();
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                DELETE FROM ultimateteam.content_rules WHERE file='objectives' AND
                    (path->>0='live' OR path->>0='grindGroups' AND (path->>1)::integer>=10);
                DELETE FROM ultimateteam.schema_migrations WHERE version IN (154,169,178);
                """);
            await ApplyMigration(work, "154-squad-foundations-objectives.sql");
            await ApplyMigration(work, "169-basic-foundations-objectives.sql");
            return 0;
        });
        var before = Document();
        var groupAt = (JsonObject document, string source) => document["grindGroups"]!.AsArray().OfType<JsonObject>().Single(g => g["sourceKey"]?.GetValue<string>() == source);
        var id = groupAt(before, "efl_squad_foundation")["objectives"]![0]!.AsObject().Int("id");
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.objective_counters(club_id,stat_code,value) VALUES(@club,@stat,3) ON CONFLICT(club_id,stat_code) DO UPDATE SET value=3;
                INSERT INTO ultimateteam.objective_claims(club_id,scope,claim_id,ordinal) VALUES(@club,'objective',@id,0);
                """, ("stat", $"objective:{id}"), ("id", id));
            await ApplyMigration(work, "178-sf-retail-requirements.sql");
            return 0;
        });
        var after = Document();
        Assert.Equal(before["live"]!.ToJsonString(), after["live"]!.ToJsonString());
        var original = before["grindGroups"]!.AsArray();
        var upgraded = after["grindGroups"]!.AsArray();
        Assert.Equal(original.Count, upgraded.Count);
        for (var index = 0; index < original.Count; index++)
        {
            var old = original[index]!.AsObject(); var next = upgraded[index]!.AsObject();
            if (old["sourceKey"]?.GetValue<string>() is not { } source || !RetailLeagues.ContainsKey(source))
            {
                Assert.Equal(old.ToJsonString(), next.ToJsonString());
                continue;
            }
            Assert.Equal(old.Int("id"), next.Int("id"));
            Assert.Equal(old["award"]!.ToJsonString(), next["award"]!.ToJsonString());
            var fresh = groupAt(seed, source)["objectives"]!.AsArray();
            for (var objective = 0; objective < 4; objective++)
            {
                var oldObjective = old["objectives"]![objective]!.AsObject();
                var nextObjective = next["objectives"]![objective]!.AsObject();
                var seedObjective = fresh[objective]!.AsObject();
                Assert.Equal(oldObjective.Int("id"), nextObjective.Int("id"));
                Assert.Equal(oldObjective["award"]!.ToJsonString(), nextObjective["award"]!.ToJsonString());
                foreach (var key in new[] { "name", "stat", "target", "description", "requirements" })
                    Assert.Equal(seedObjective[key]!.ToJsonString(), nextObjective[key]!.ToJsonString());
            }
        }
        Assert.Equal(3, rt.Game.InUnit(work => work.ScalarAsync<long>("SELECT value FROM ultimateteam.objective_counters WHERE club_id=@club AND stat_code=@stat", ("stat", $"objective:{id}"))));
        Assert.True(rt.Game.InUnit(work => work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.objective_claims WHERE club_id=@club AND claim_id=@id)", ("id", id))));
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("DELETE FROM ultimateteam.schema_migrations WHERE version=178");
            await ApplyMigration(work, "178-sf-retail-requirements.sql"); return 0;
        });
        Assert.Equal(after.ToJsonString(), Document().ToJsonString());
    }

    [Fact]
    public void UpgradeKeepsAnOperatorPack564AndAwardsTheRewardPackFromAFreeId()
    {
        // Live 10-05: the panel had made pack 564 (Guaranteed Icon Pick) before this import, and the deploy refused to start.
        using var rt = TestRuntime.Create();
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                DELETE FROM ultimateteam.content_rules WHERE file='objectives' AND
                    (path->>0='live' OR path->>0='grindGroups' AND (path->>1)::integer>=10);
                UPDATE ultimateteam.store_packs SET name='Guaranteed Icon Pick', min_rating=0, rare_slots=0,
                    category_chances='{"icon": 1}'::jsonb WHERE pack_id=564;
                DELETE FROM ultimateteam.schema_migrations WHERE version=154;
                """);
            await ApplyMigration(work, "154-squad-foundations-objectives.sql");
            return 0;
        });
        var (iconPick, rewardPack, lastBefore) = rt.Game.InUnit(async work => (
            await work.ScalarAsync<string>("SELECT name FROM ultimateteam.store_packs WHERE pack_id=564"),
            await work.ScalarAsync<long>("SELECT pack_id FROM ultimateteam.store_packs WHERE name='75+ Rated Rare Player Pack' AND admin_only AND min_rating=75"),
            await work.ScalarAsync<long>("SELECT max(pack_id) FROM ultimateteam.store_packs WHERE name<>'75+ Rated Rare Player Pack'")));
        Assert.Equal("Guaranteed Icon Pick", iconPick);
        Assert.True(rewardPack > lastBefore);
        var document = rt.Game.InUnit(async work => JsonNode.Parse((await RuleRows.DocumentsAsync(work))["objectives"])!.AsObject());
        var imported = document["grindGroups"]!.AsArray().OfType<JsonObject>().Where(group => group["sourceKey"] is not null).ToArray();
        Assert.Equal(7, imported.Length);
        var packIds = imported.SelectMany(group => DescendantsNamed(group, "packId")).ToArray();
        Assert.NotEmpty(packIds);
        Assert.All(packIds, id => Assert.Equal(rewardPack, id));
    }

    [Fact]
    public async Task BasicFoundationsCountersStartWhenLiveAndCountOneScoringMatchPerEligibleGame()
    {
        using var rt = TestRuntime.Create();
        var group = rt.Game.Rules.Objectives.GrindGroups.Single(g => g.SourceKey == "mls_squad_foundation_ii");
        var used = new HashSet<long>();
        var cards = new List<JsonObject>();
        foreach (var league in new[] {39L,39L,39L,14L,14L,14L,14L,14L,14L,14L,14L})
        {
            var picked = rt.Game.Players.Entries.First(entry => entry.Value.Int("leagueId") == league
                && entry.Value.Rareflag <= 1 && !used.Contains(entry.Value.Int("assetId")));
            used.Add(picked.Value.Int("assetId"));
            cards.Add(rt.Fut.Items.MakePlayer(picked.Key,"club"));
        }
        var body = new JsonObject
        {
            ["myMatchStats"] = new JsonObject { ["goals"] = 2 },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
            ["items"] = new JsonArray(cards.Select((card,i)=>(JsonNode)new JsonObject
                { ["id"] = card.Int("id"), ["goals"] = i < 2 ? 1 : 0, ["assists"] = i == 0 ? 1 : 0 }).ToArray()),
        };
        void Record(string mode) => rt.Game.InUnit(work => rt.Fut.Objectives.RecordMatchEndAsync(work,"SUCCESS",body,true,false,mode));
        Record("rivals"); Record("weekendLeague");
        var document = rt.Game.InUnit(async work => JsonNode.Parse((await RuleRows.DocumentsAsync(work))["objectives"])!.AsObject());
        document["live"]!.AsArray().Single(node=>node!.AsObject().Int("group")==group.Id)!["enabled"] = true;
        var changes = new ContentChangesets(rt.Game,ContentAreas.Default);
        await changes.SetChangeAsync(RulesArea.AreaName,Key("live"),ContentOperation.Update,
            new JsonObject { ["value"] = document["live"]!.DeepClone() },null);
        await changes.PublishAsync("ordinary MLS fixture", "test");
        Assert.All(group.Objectives,o=>Assert.Equal(0,rt.Game.Store().Objectives().Progress(group.Id,o.Counter)));
        Record("squadBattles");
        Assert.All(group.Objectives,o=>Assert.Equal(0,rt.Game.Store().Objectives().Progress(group.Id,o.Counter)));
        Record("rivals"); Record("weekendLeague");
        Assert.Equal([2L,4L,2L,2L],group.Objectives.Select(o=>rt.Game.Store().Objectives().Progress(group.Id,o.Counter)));
        Assert.Equal(5,rt.Game.Store().Objectives().Stat("assists"));
    }

    [Fact]
    public async Task DirectPlayerAndRewardlessObjectivesClaimOnceAndGroupXpPaysOnce()
    {
        using var rt = TestRuntime.Create();
        var document = rt.Game.InUnit(async work => JsonNode.Parse((await RuleRows.DocumentsAsync(work))["objectives"])!.AsObject());
        var groups = document["grindGroups"]!.AsArray();
        groups.Add(new JsonObject
        {
            ["id"] = 800, ["title"] = "Reward test", ["subTitle"] = "Synthetic", ["xp"] = 300,
            ["award"] = new JsonObject { ["players"] = new JsonArray(new JsonObject { ["resourceId"] = 50520590, ["untradeable"] = true }) },
            ["objectives"] = new JsonArray(
                new JsonObject { ["id"] = 80001, ["name"] = "No specified reward", ["stat"] = "matches", ["target"] = 1, ["xp"] = 100, ["award"] = new JsonObject { ["none"] = true } },
                new JsonObject { ["id"] = 80002, ["name"] = "Player reward", ["stat"] = "matches", ["target"] = 1, ["xp"] = 0,
                    ["award"] = new JsonObject { ["players"] = new JsonArray(new JsonObject { ["resourceId"] = 50554529, ["untradeable"] = true }) } })
        });
        document["live"]!.AsArray().Add(new JsonObject { ["group"] = 800, ["enabled"] = true });
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        foreach (var path in new[] { "grindGroups", "live" })
            await changes.SetChangeAsync(RulesArea.AreaName, Key(path), ContentOperation.Update, new JsonObject { ["value"] = document[path]!.DeepClone() }, null);
        await changes.PublishAsync("synthetic reward test", "test");
        rt.Game.InUnit(work => rt.Fut.Objectives.RecordMatchAsync(work, 1, 0, false));
        var xp = rt.Game.Store().Objectives().UserXp;
        var result = await rt.Fut.Objectives.ClaimAsync(800,80001);
        Assert.Equal(200, result.Status);
        var first = (JsonObject)result.Body!;
        Assert.False(first.Bool("alreadyClaimed"));
        Assert.Empty(first["awards"]!.AsArray());
        Assert.True(((JsonObject)(await rt.Fut.Objectives.ClaimAsync(800,80001)).Body!).Bool("alreadyClaimed"));
        await rt.Fut.Objectives.ClaimAsync(800,80002);
        await rt.Fut.Objectives.ClaimAsync(800,null);
        var cards = rt.Game.Store().ListItems("unassigned").Where(item => item.Int("resourceId") is 50520590 or 50554529).ToArray();
        Assert.Equal(2, cards.Length);
        Assert.All(cards, item => Assert.True(item.Bool("untradeable")));
        Assert.Equal(xp+400,rt.Game.Store().Objectives().UserXp);
        Assert.True(((JsonObject)(await rt.Fut.Objectives.ClaimAsync(800,null)).Body!).Bool("alreadyClaimed"));
        Assert.Equal(xp+400,rt.Game.Store().Objectives().UserXp);
    }
}
