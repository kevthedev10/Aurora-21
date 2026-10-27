using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// SBC group rewards (owner, 10-05): the Foundations sets carry their retail set rewards in sbc.json, the submit that
/// finishes a set pays it once, and migration 159 pays clubs that finished a set before the sets had one.
/// </summary>
public sealed class SbcSetRewardTests
{
    private const string G = "/ut/game/fifa21";

    private static bool HasRewards(JsonObject source) => (source.Obj("rewards")?.Arr("entries")?.Count ?? 0) > 0;

    [Fact]
    public void FoundationsSetsCarryTheirGroupRewards()
    {
        using var rt = TestRuntime.Create();
        var expected = new Dictionary<string, long>
        {
            ["FND_LETS_GET_STARTED"] = 82,
            ["FND_LEAGUE_NATION_BASICS"] = 534,
            ["FND_LETS_KEEP_GOING"] = 305,
            ["FND_HYBRID_NATIONS"] = 404,
            ["FND_HYBRID_LEAGUES"] = 404,
            ["FND_LETS_MOVE_FORWARD"] = 82,
        };
        foreach (var (key, packId) in expected)
        {
            var set = rt.Game.Sbc.Sets.Single(candidate => candidate.Key == key);
            var entry = set.Source.Obj("rewards")!.Arr("entries")!.OfType<JsonObject>().Single();
            Assert.Equal(packId, entry.Obj("attributes")!.Int("packId"));
        }
        var hybrid = rt.Game.Sbc.Sets.Single(candidate => candidate.Key == "FND_LEAGUE_NATION_HYBRID");
        var coins = hybrid.Source.Obj("rewards")!.Arr("entries")!.OfType<JsonObject>().Single();
        Assert.Equal(("COINS", 13000L), (coins.Text("type"), coins.Int("value")));
    }

    [Fact]
    public void ACoinGroupRewardTileSaysCoin()
    {
        // FIFA's SBC award parser maps type through its key table and knows only coin, item and pack; "coins" made
        // League and Nation Hybrid's 13,000 coins show as a default pack with no name.
        using var rt = TestRuntime.Create();
        var hybrid = rt.Game.Sbc.Sets.Single(candidate => candidate.Key == "FND_LEAGUE_NATION_HYBRID");
        var listed = rt.CallObject("GET", $"{G}/sbs/sets")["categories"]!.AsArray().OfType<JsonObject>()
            .SelectMany(category => category["sets"]!.AsArray().OfType<JsonObject>())
            .Single(set => set.Int("id") == hybrid.Id);

        var award = listed["awards"]!.AsArray().OfType<JsonObject>().Single();
        Assert.Equal(("coin", 13000L), (award.Text("type"), award.Int("value")));
        Assert.Equal(13000, listed.Int("coins"));
    }

    [Fact]
    public void TheSubmitThatFinishesASetPaysItsGroupRewardOnce()
    {
        using var rt = TestRuntime.Create();
        var store = rt.Game.Store();
        var set = rt.Game.Sbc.Sets.First(candidate => candidate.Challenges.Count > 1 && HasRewards(candidate.Source)
            && candidate.Source.Obj("rewards")!.Arr("entries")!.OfType<JsonObject>().Any(entry => entry.Text("type") == "PACK")
            && SbcFixtures.CanBuildSquad(candidate.Challenges[^1]));
        var setPack = set.Source.Obj("rewards")!.Arr("entries")!.OfType<JsonObject>()
            .First(entry => entry.Text("type") == "PACK").Obj("attributes")!.Int("packId");
        foreach (var earlier in set.Challenges.SkipLast(1))
        {
            Assert.True(store.TryMarkSbcCompleted(earlier.Key));
        }
        var last = set.Challenges[^1];
        SbcFixtures.SaveSatisfyingSquad(rt, last);
        var before = store.UnopenedPackIds().Count(id => id == setPack);

        var submitted = rt.CallObject("PUT", $"{G}/sbs/challenge/{last.Id}", new JsonObject());

        Assert.NotEmpty(submitted["grantedSetAwards"]!.AsArray());
        var lastPacks = last.Source.Obj("rewards")?.Arr("entries")?.OfType<JsonObject>()
            .Count(entry => entry.Text("type") == "PACK" && entry.Obj("attributes")!.Int("packId") == setPack) ?? 0;
        Assert.Equal(before + lastPacks + 1, store.UnopenedPackIds().Count(id => id == setPack));

        var paid = ClubStateSnapshot.Capture(rt.ConnectionString, store.ClubId);
        Assert.Equal(0, rt.CallObject("PUT", $"{G}/sbs/challenge/{last.Id}", new JsonObject())["grantedItemsCount"].L());
        Assert.Equal(paid, ClubStateSnapshot.Capture(rt.ConnectionString, store.ClubId));
    }

    [Fact]
    public void AOneChallengeSetSubmitShowsItsAwardsAsTheSetsReward()
    {
        // FIFA returns a one-challenge set to the Sets hub, which never asks for the completed popup; it goes to the
        // set rewards screen only when grantedSetAwards has items and grantedChallengeAwards has none.
        using var rt = TestRuntime.Create();
        var set = rt.Game.Sbc.Sets.First(candidate => candidate.Challenges.Count == 1
            && HasRewards(candidate.Challenges[0].Source) && SbcFixtures.CanBuildSquad(candidate.Challenges[0]));
        rt.Game.Sbc.LiveKeys.Add(set.Key);
        var only = set.Challenges[0];
        SbcFixtures.SaveSatisfyingSquad(rt, only);

        var submitted = rt.CallObject("PUT", $"{G}/sbs/challenge/{only.Id}", new JsonObject());

        Assert.Empty(submitted["grantedChallengeAwards"]!.AsArray());
        Assert.True(submitted["grantedSetAwards"]!.AsArray().Count >= only.Source.Obj("rewards")!.Arr("entries")!.Count);
        Assert.True(submitted.Int("grantedItemsCount") > 0 || submitted.Int("credits") > 0);
    }

    [Fact]
    public void AMultiChallengeSetKeepsItsChallengeAwardsOnTheChallenge()
    {
        using var rt = TestRuntime.Create();
        var set = rt.Game.Sbc.Sets.First(candidate => candidate.Challenges.Count > 1
            && HasRewards(candidate.Challenges[0].Source) && SbcFixtures.CanBuildSquad(candidate.Challenges[0]));
        rt.Game.Sbc.LiveKeys.Add(set.Key);
        var first = set.Challenges[0];
        SbcFixtures.SaveSatisfyingSquad(rt, first);

        var submitted = rt.CallObject("PUT", $"{G}/sbs/challenge/{first.Id}", new JsonObject());

        Assert.NotEmpty(submitted["grantedChallengeAwards"]!.AsArray());
        Assert.Empty(submitted["grantedSetAwards"]!.AsArray());
    }

    [Fact]
    public async Task Migration159PaysFinishedSetsOnce()
    {
        var connection = PostgresTestCluster.CreateInstalledDatabase();
        await using (var install = NpgsqlDataSource.Create(connection))
        {
            await RelationalSchemaBootstrap.InstallFreshAsync(install);
        }
        using var rt = TestRuntime.Create(connectionString: connection, relationalSlice: true, initializeRelationalClub: true);
        var club = rt.Game.Store().ClubId;
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        async Task<long> Scalar(string sql)
        {
            await using var command = source.CreateCommand(sql);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        // Let's Get Started and League and Nation Hybrid finished; League and Nation Basics two of three.
        await using (var command = source.CreateCommand($"""
            DELETE FROM ultimateteam.schema_migrations WHERE version=159;
            DELETE FROM ultimateteam.sbc_completed WHERE club_id={club};
            INSERT INTO ultimateteam.sbc_completed(club_id,challenge_key)
            SELECT {club}, key FROM unnest(ARRAY['CH_1','CH_2','CH_3','CH_4','CH_5','CH_19','CH_20','CH_21','CH_22']) key;
            """)) await command.ExecuteNonQueryAsync();
        var credits = await Scalar($"SELECT credits FROM ultimateteam.clubs WHERE id={club}");
        string Packs(long pack) => $"SELECT count(*) FROM ultimateteam.unopened_packs WHERE club_id={club} AND pack_id={pack} AND source='sbc'";
        var (gold, mixed) = (await Scalar(Packs(82)), await Scalar(Packs(534)));

        // Exercise this backpay migration itself. A current database is not a pre-159 schema:
        // removing every later ledger row re-runs unrelated DDL against tables/sequences already present.
        using (var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/159-sbc-set-rewards-backpay.sql")!)
        using (var reader = new StreamReader(stream))
        await using (var database = await source.OpenConnectionAsync())
        await using (var transaction = await database.BeginTransactionAsync())
        {
            await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(), database, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        await RelationalSchemaBootstrap.InstallForRuntimeAsync(source);
        await RelationalSchemaBootstrap.InstallForRuntimeAsync(source);
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM ultimateteam.schema_migrations WHERE version=159"));

        Assert.Equal(gold + 1, await Scalar(Packs(82)));
        Assert.Equal(mixed, await Scalar(Packs(534)));
        Assert.Equal(credits + 13000, await Scalar($"SELECT credits FROM ultimateteam.clubs WHERE id={club}"));
        Assert.Equal(0L, await Scalar($"SELECT count(*) FROM ultimateteam.unopened_packs WHERE club_id={club} AND source='sbc' AND untradeable"));
    }
}
