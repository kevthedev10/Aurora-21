using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Aurora21.UltimateTeam.Core.Content;
using Xunit;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class SbcDailyAndRequirementsTests
{
    private const string G = "/ut/game/fifa21";

    [Theory]
    [InlineData(3, false)]
    [InlineData(5, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task LimitedSbcStopsAtItsPerAccountCapIncludingConcurrentFinalSubmissions(int limit, bool daily)
    {
        var time = new TestRuntime.MovableTime(DateTimeOffset.Parse("2026-10-07T17:59:59Z").ToUnixTimeSeconds());
        using var rt = TestRuntime.Create(time: time, marketTarget: 0);
        var set = rt.Game.Sbc.FindSet("FND_LETS_GET_STARTED")!;
        set.Source["repeatable"] = true;
        set.Source["maxCompletions"] = limit;
        set.Source["repeatableDaily"] = daily;
        Assert.Null(new SbcSetArea(rt.Game).Problem(set.Key, set.Source));
        async Task<JsonObject> NativeSet()
        {
            var response = await rt.Fut.Sbc.SetsResponseAsync();
            return response["categories"]!.AsArray()
                .SelectMany(category => category!["sets"]!.AsArray())
                .Single(candidate => candidate!.AsObject().Int("id") == set.Id)!.AsObject();
        }
        var nativeSet = await NativeSet();
        Assert.Equal(!daily, nativeSet.Bool("repeatable"));
        Assert.Contains(daily ? limit == 1 ? "1 completion per account per day." : $"Up to {limit} completions per account per day."
            : $"Up to {limit} completions per account in total.", nativeSet.Text("description"));
        for (var round = 0; round < limit; round++)
        foreach (var challenge in set.Challenges)
        {
            SbcFixtures.SaveSatisfyingSquad(rt, challenge);
            var results = await Task.WhenAll(rt.Fut.Sbc.SubmitAsync(challenge.Id), rt.Fut.Sbc.SubmitAsync(challenge.Id));
            Assert.Single(results, result => result.Body!.AsObject().Int("grantedItemsCount") > 0);
        }
        foreach (var challenge in set.Challenges)
            Assert.Equal(0, (await rt.Fut.Sbc.SubmitAsync(challenge.Id)).Body!.AsObject().Int("grantedItemsCount"));
        var counts = await rt.Game.ReadAsync(work => work.SbcCompletionCountsAsync(), CancellationToken.None);
        Assert.All(set.Challenges, challenge => Assert.Equal(limit, counts[challenge.Key]));
        Assert.All(rt.Fut.Sbc.ChallengesResponse(set.Id)["challenges"]!.AsArray(), c => Assert.Equal("COMPLETED", c!.AsObject().Text("status")));
        Assert.False((await NativeSet()).Bool("repeatable"));
        foreach (var invalid in new JsonNode[] { JsonValue.Create(0)!, JsonValue.Create(1.5)!, JsonValue.Create("3")! })
        {
            set.Source["maxCompletions"] = invalid;
            Assert.NotNull(new SbcSetArea(rt.Game).Problem(set.Key, set.Source));
        }
        set.Source["maxCompletions"] = limit;
        if (daily)
        {
            if (limit == 1)
            {
                // Rehearse the upgrade from the original one-completion daily table.
                rt.Game.InUnit(async work =>
                {
                    await work.ExecuteAsync("ALTER TABLE ultimateteam.sbc_daily_completed DROP COLUMN completion_count; DELETE FROM ultimateteam.schema_migrations WHERE version>=172");
                    return 0;
                });
                await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
                await RelationalSchemaBootstrap.InstallForRuntimeAsync(source);
                await using var check = source.CreateCommand("SELECT min(completion_count) FROM ultimateteam.sbc_daily_completed");
                Assert.Equal(1L, await check.ExecuteScalarAsync());
            }
            time.Now++;
            Assert.All(rt.Fut.Sbc.ChallengesResponse(set.Id)["challenges"]!.AsArray(), c => Assert.NotEqual("COMPLETED", c!.AsObject().Text("status")));
            Assert.False((await NativeSet()).Bool("repeatable"));
            foreach (var challenge in set.Challenges)
            {
                SbcFixtures.SaveSatisfyingSquad(rt, challenge);
                Assert.True((await rt.Fut.Sbc.SubmitAsync(challenge.Id)).Body!.AsObject().Int("grantedItemsCount") > 0);
            }
            counts = await rt.Game.ReadAsync(work => work.SbcCompletionCountsAsync(), CancellationToken.None);
            Assert.All(set.Challenges, challenge => Assert.Equal(limit + 1, counts[challenge.Key]));
        }
    }

    [Fact]
    public async Task OneTimeAndUnlimitedSbcLabelsDescribeTheirCompletionLimits()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var set = rt.Game.Sbc.FindSet("FND_LETS_GET_STARTED")!;
        set.Source.Remove("maxCompletions");
        set.Source["repeatableDaily"] = false;
        foreach (var repeatable in new[] { false, true })
        {
            set.Source["repeatable"] = repeatable;
            var response = await rt.Fut.Sbc.SetsResponseAsync();
            var tile = response["categories"]!.AsArray()
                .SelectMany(category => category!["sets"]!.AsArray())
                .Single(candidate => candidate!.AsObject().Int("id") == set.Id)!.AsObject();
            Assert.Equal(repeatable, tile.Bool("repeatable"));
            Assert.Contains(repeatable ? "Repeatable with no completion limit." : "1 completion per account in total.", tile.Text("description"));
        }
    }

    [Theory]
    [InlineData("2026-10-07T17:59:59Z", "2026-10-06T18:00:00Z")]
    [InlineData("2026-10-07T18:00:00Z", "2026-10-07T18:00:00Z")]
    [InlineData("2026-10-25T18:59:59Z", "2026-10-24T18:00:00Z")]
    [InlineData("2026-10-25T19:00:00Z", "2026-10-25T19:00:00Z")]
    [InlineData("2027-03-28T17:59:59Z", "2027-03-27T19:00:00Z")]
    [InlineData("2027-03-28T18:00:00Z", "2027-03-28T18:00:00Z")]
    public void DailyResetFollowsSevenPmLondonAcrossBothClockChanges(string now, string reset) =>
        Assert.Equal(DateTimeOffset.Parse(reset).ToUnixTimeSeconds(), SbcService.DailyResetAt(DateTimeOffset.Parse(now)));

    [Fact]
    public async Task DailySbcPaysOncePerDayPreservesHistoryAndRestartsAtSevenPm()
    {
        var time = new TestRuntime.MovableTime(DateTimeOffset.Parse("2026-10-07T17:59:59Z").ToUnixTimeSeconds());
        using var rt = TestRuntime.Create(time: time, marketTarget: 0);
        var set = rt.Game.Sbc.FindSet("FND_LETS_GET_STARTED")!;
        set.Source["repeatable"] = true;
        set.Source["repeatableDaily"] = true;
        var first = set.Challenges[0];
        SbcFixtures.SaveSatisfyingSquad(rt, first);
        var submissions = await Task.WhenAll(rt.Fut.Sbc.SubmitAsync(first.Id), rt.Fut.Sbc.SubmitAsync(first.Id));
        Assert.Single(submissions, result => result.Body!.AsObject().Int("grantedItemsCount") > 0);
        foreach (var challenge in set.Challenges.Skip(1))
        {
            SbcFixtures.SaveSatisfyingSquad(rt, challenge);
            var result = rt.CallObject("PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject());
            Assert.True(result.Int("grantedItemsCount") > 0);
            if (challenge == set.Challenges[^1]) Assert.NotEmpty(result["grantedSetAwards"]!.AsArray());
        }
        Assert.All(rt.Fut.Sbc.ChallengesResponse(set.Id)["challenges"]!.AsArray(), c => Assert.True(c!.AsObject().Text("status") == "COMPLETED"));
        Assert.Equal(0, rt.CallObject("PUT", $"{G}/sbs/challenge/{first.Id}", new JsonObject()).Int("grantedItemsCount"));
        time.Now++;
        Assert.All(rt.Fut.Sbc.ChallengesResponse(set.Id)["challenges"]!.AsArray(), c => Assert.False(c!.AsObject().Text("status") == "COMPLETED"));
        foreach (var challenge in set.Challenges)
        {
            SbcFixtures.SaveSatisfyingSquad(rt, challenge);
            var result = rt.CallObject("PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject());
            Assert.True(result.Int("grantedItemsCount") > 0);
            if (challenge == set.Challenges[^1]) Assert.NotEmpty(result["grantedSetAwards"]!.AsArray());
        }
        var counts = await rt.Game.ReadAsync(work => work.SbcCompletionCountsAsync(), CancellationToken.None);
        Assert.All(set.Challenges, challenge => Assert.Equal(2, counts[challenge.Key]));
        // Rehearse an upgrade from 169 with existing completion history and edited settings.
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using (var command = source.CreateCommand("""
            SELECT set_config('ultimateteam.content_publish','on',true);
            DROP TABLE ultimateteam.sbc_daily_completed;
            DELETE FROM ultimateteam.schema_migrations WHERE version>=170;
            DELETE FROM ultimateteam.content_rules WHERE file='sbc' AND path->>0='requirements'
                AND path->>1 IN ('LEAGUE_PLAYERS','NATION_PLAYERS','CLUB_PLAYERS','QUALITY_PLAYERS');
            UPDATE ultimateteam.content_rules SET value='"Custom nation label: {value}"'::jsonb
                WHERE file='sbc' AND path='["requirements","NATIONS","label"]'::jsonb;
            """)) await command.ExecuteNonQueryAsync();
        await RelationalSchemaBootstrap.InstallForRuntimeAsync(source);
        await RelationalSchemaBootstrap.InstallForRuntimeAsync(source);
        var retained = await rt.Game.ReadAsync(work => work.SbcCompletionCountsAsync(), CancellationToken.None);
        Assert.All(set.Challenges, challenge => Assert.Equal(2, retained[challenge.Key]));
        await using var check = source.CreateCommand("""SELECT value #>> '{}' FROM ultimateteam.content_rules WHERE file='sbc' AND path='["requirements","NATIONS","label"]'::jsonb""");
        Assert.Equal("Custom nation label: {value}", await check.ExecuteScalarAsync());
        await using var selector = source.CreateCommand("""SELECT value #>> '{}' FROM ultimateteam.content_rules WHERE file='sbc' AND path='["requirements","LEAGUE_PLAYERS","selector"]'::jsonb""");
        Assert.Equal("leagueId", await selector.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SpecificSelectionsAndEveryComparisonAreEnforcedBeforeConsumingPlayers()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var set = rt.Game.Sbc.FindSet("FND_LETS_GET_STARTED")!;
        set.Source["repeatable"] = true;
        // Use the first one-player challenge; keep its native locked slots.
        var challenge = set.Challenges[0];
        var slot = SbcFixtures.FreeSlots(rt, challenge).Single();
        var candidate = rt.Game.Players.Entries.First(p => p.Value.Int("teamid") > 0 && p.Value.Int("leagueId") > 0 && p.Value.Int("nation") > 0 && p.Value.Rareflag == 1);
        var meta = candidate.Value;
        var cases = new (string Type, string? Selector, object? Value)[] {
            ("LEAGUE_PLAYERS", "leagueId", meta.Int("leagueId")), ("NATION_PLAYERS", "nationId", meta.Int("nation")),
            ("CLUB_PLAYERS", "clubId", meta.Int("teamid")), ("QUALITY_PLAYERS", "quality", meta.Tier),
            ("CLUBS", null, null), ("LEAGUES", null, null), ("NATIONS", null, null),
            ("SAME_CLUB", null, null), ("SAME_LEAGUE", null, null), ("SAME_NATION", null, null), ("RARE", null, null),
        };
        foreach (var (type, selector, value) in cases)
        foreach (var comparison in new[] { "min", "max", "exact" })
        {
            var item = rt.Fut.Items.MakePlayer(candidate.Key, "club");
            var attributes = new JsonObject { [comparison] = comparison == "max" ? 0 : 2 };
            if (selector is not null) attributes[selector] = JsonSerializerNode(value);
            var rules = new JsonArray(new JsonObject { ["type"] = "PLAYERS", ["attributes"] = new JsonObject { ["min"] = 1, ["max"] = 1 } },
                new JsonObject { ["type"] = type, ["attributes"] = attributes });
            challenge.Source["requirements"] = new JsonObject { ["rules"] = rules };
            var translated = SbcText.Requirements(rt.Game.Rules.Sbc, challenge.Source).Single();
            Assert.Equal(comparison == "max" ? SbcScope.AtMost : comparison == "min" ? SbcScope.AtLeast : SbcScope.Exact, translated.Scope);
            if (selector is not null) Assert.Equal(type == "QUALITY_PLAYERS" ? rt.Game.Rules.Sbc.Requirements[type].Qualities[meta.Tier] : Convert.ToInt64(value), translated.Value);
            rt.CallObject("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", new JsonObject { ["players"] = new JsonArray(new JsonObject { ["index"] = slot, ["itemData"] = new JsonObject { ["id"] = item.Int("id") } }) });
            Assert.Equal("INVALID", rt.CallObject("PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject()).Text("status"));
            Assert.NotNull(rt.Game.Store().GetItem(item.Int("id")));
            attributes[comparison] = 1;
            var submitted = rt.CallObject("PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject());
            Assert.True(submitted.Int("grantedItemsCount") > 0, $"{type} {comparison}: {submitted}");
            Assert.Null(rt.Game.Store().GetItem(item.Int("id")));
            // Advance the other challenges through this round before submitting the first again.
            await rt.Game.RunAsync("test-sbc-round", async work =>
            {
                foreach (var other in set.Challenges.Skip(1)) await work.RecordRepeatableSbcCompletionAsync(other.Key);
            }, CancellationToken.None);
        }
        var area = new SbcSetArea(rt.Game);
        foreach (var (type, selector, value) in cases.Where(c => c.Selector is not null))
        {
            var definition = (JsonObject)set.Source.DeepClone();
            var selected = new JsonObject { ["min"] = 1, [selector!] = JsonSerializerNode(value) };
            definition["challenges"]![0]!["requirements"]!["rules"] = new JsonArray(
                new JsonObject { ["type"] = "PLAYERS", ["attributes"] = new JsonObject { ["min"] = 1, ["max"] = 1 } },
                new JsonObject { ["type"] = type, ["attributes"] = selected });
            Assert.Null(area.Problem(set.Key, definition));
            selected[selector!] = selector == "quality" ? JsonValue.Create("platinum") : JsonValue.Create(999999999L);
            Assert.Contains("needs a valid", area.Problem(set.Key, definition));
        }
        var invalid = (JsonObject)set.Source.DeepClone();
        invalid["repeatable"] = false;
        invalid["repeatableDaily"] = true;
        Assert.Contains("Daily SBCs must be repeatable", new SbcSetArea(rt.Game).Problem(set.Key, invalid));
    }

    [Fact]
    public void EveryChallengeFitsTheSixRequirementLinesFifaShows()
    {
        // FIFA 21 lists only the first six requirement lines; Advanced (CH_20) sent eight and hid its Chemistry 100 (10-08).
        using var rt = TestRuntime.Create(marketTarget: 0);
        var sbc = rt.Game.Rules.Sbc;
        foreach (var set in rt.Game.Sbc.Sets)
        foreach (var challenge in set.Challenges)
            Assert.True(SbcText.Requirements(sbc, challenge.Source).Count(r => r.Key is not null) <= 6, $"{challenge.Key} sends more than six requirement lines");
        var advanced = rt.Game.Sbc.FindSet("FND_LEAGUE_NATION_HYBRID")!.Challenges.Single(c => c.Key == "CH_20");
        var lines = SbcText.Requirements(sbc, advanced.Source).ToList();
        Assert.Contains(lines, r => r.Key == SbcEligibilityKey.Chemistry && r.Scope == SbcScope.AtLeast && r.Value == 100);
        Assert.Contains(lines, r => r.Key == SbcEligibilityKey.TeamRating && r.Scope == SbcScope.AtLeast && r.Value == 79);
        Assert.DoesNotContain(lines, r => r.Scope == SbcScope.AtMost && r.Key is SbcEligibilityKey.Chemistry or SbcEligibilityKey.TeamRating);
        Assert.Contains(lines, r => r.Key == SbcEligibilityKey.SameNation && r.Scope == SbcScope.AtMost && r.Value == 5);
    }

    private static JsonNode? JsonSerializerNode(object? value) => System.Text.Json.JsonSerializer.SerializeToNode(value);
}
