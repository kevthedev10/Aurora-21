using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class ObjectiveMatchRequirementsTests
{
    private static GrindObjective Objective(string stat = "goals") => new()
    {
        Id = 90001, Name = "League scoring", Stat = stat, Target = 6, Xp = 0,
        Award = new ObjectiveReward { Coins = 100 },
        Requirements = new ObjectiveMatchRequirements
        {
            Modes = [RewardModes.Rivals, RewardModes.WeekendLeague],
            StartingSquad = new ObjectiveStartingSquad { LeagueId = 39, MinimumCount = 3 },
            Player = new ObjectivePlayerRestriction { LeagueId = 39 },
        },
    };

    private static (JsonObject Body, Dictionary<long, JsonObject> Owned) Report()
    {
        var owned = Enumerable.Range(1, 12).ToDictionary(i => (long)i, i => new JsonObject
        { ["assetId"] = 1000 + i, ["leagueId"] = i <= 3 || i == 12 ? 39 : 14, ["nation"] = 25 });
        var body = new JsonObject
        {
            ["myMatchStats"] = new JsonObject { ["goals"] = 3 },
            ["items"] = new JsonArray(Enumerable.Range(1, 12).Select(i => (JsonNode)new JsonObject
            { ["id"] = i, ["goals"] = i <= 2 ? 1 : i == 4 ? 1 : 0, ["assists"] = i == 1 ? 1 : 0 }).ToArray()),
        };
        return (body, owned);
    }

    [Theory]
    [InlineData(RewardModes.Rivals, 2)]
    [InlineData(RewardModes.WeekendLeague, 2)]
    [InlineData(RewardModes.SquadBattles, 0)]
    [InlineData(RewardModes.DraftOnline, 0)]
    [InlineData(null, 0)]
    public void ModeAndScorersAreCheckedAgainstOwnedMetadata(string? mode, long expected)
    {
        var (body, owned) = Report();
        Assert.Equal(expected, ObjectiveMatchProgress.Count(Objective(), body, owned, mode, true, new()));
        // Client-supplied league/nation fields are not consulted.
        body["items"]![3]!["leagueId"] = 39;
        Assert.Equal(expected, ObjectiveMatchProgress.Count(Objective(), body, owned, mode, true, new()));
    }

    [Fact]
    public void BenchCardsUnknownStartersAndDuplicateIdsDoNotSatisfyTheStartingSquad()
    {
        var (body, owned) = Report();
        owned[3]["leagueId"] = 14; // Two qualifying starters plus the qualifying bench card.
        Assert.Equal(0, ObjectiveMatchProgress.Count(Objective("matches"), body, owned, RewardModes.Rivals, true, new()));
        owned[3]["leagueId"] = 39;
        owned.Remove(11);
        Assert.Equal(0, ObjectiveMatchProgress.Count(Objective("matches"), body, owned, RewardModes.Rivals, true, new()));
        body["items"]![10]!["id"] = 1;
        Assert.Equal(0, ObjectiveMatchProgress.Count(Objective("matches"), body, owned, RewardModes.Rivals, true, new()));
    }

    [Fact]
    public void WinUsesTheSettledOutcomeAndNationRestrictionsAreApplied()
    {
        var (body, owned) = Report();
        var objective = Objective("wins");
        objective.Requirements!.StartingSquad = new() { Nation = 25, MinimumCount = 4 };
        Assert.Equal(1, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, true, new()));
        Assert.Equal(0, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, false, new()));
        objective.Requirements.StartingSquad.Nation = 21;
        Assert.Equal(0, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, true, new()));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    public void BasicScoringMatchesCountOnceWithoutPlayerOrEventMetadata(long goals, long expected)
    {
        var objective = Objective("scoringMatches");
        objective.Requirements = new() { Modes = [RewardModes.Rivals] };
        var body = new JsonObject { ["myMatchStats"] = new JsonObject { ["goals"] = goals } };
        Assert.Equal(expected, ObjectiveMatchProgress.Count(objective, body, new Dictionary<long, JsonObject>(), RewardModes.Rivals, false, new()));
        Assert.Equal(0, ObjectiveMatchProgress.Count(objective, body, new Dictionary<long, JsonObject>(), RewardModes.SquadBattles, true, new()));
    }

    [Fact]
    public void BasicTotalGoalsUseTheMatchScoreWithoutPlayerOrEventMetadata()
    {
        var objective = Objective();
        objective.Requirements = new() { Modes = [RewardModes.Rivals] };
        var body = new JsonObject { ["myMatchStats"] = new JsonObject { ["goals"] = 3 } };
        Assert.Equal(3, ObjectiveMatchProgress.Count(objective, body, new Dictionary<long, JsonObject>(), RewardModes.Rivals, false, new()));
    }

    private static JsonObject Event(string name, long player, params long[] flags) => new()
    {
        ["attributes"] = new JsonArray(new[]
        {
            new JsonObject { ["key"] = "name", ["value"] = name },
            new JsonObject { ["key"] = "player_id", ["value"] = player.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        }.Concat(flags.Select(flag => new JsonObject { ["key"] = "flag", ["value"] = flag })).Select(node => (JsonNode)node).ToArray()),
    };

    [Fact]
    public void SpecialScoringFailsClosedAndCountsOnlyMatchingEventsOnceWithinReportedTotals()
    {
        var (body, owned) = Report();
        var objective = Objective();
        objective.Requirements!.GoalType = "finesse";
        // 900 is a synthetic adapter-test flag, not a claim about FIFA's finesse flag.
        body["events"] = new JsonArray(Event("goal", 1001, 900, 900), Event("assist", 1002, 900), Event("goal", 1004, 900), Event("goal", 1002, 901));
        Assert.Equal(0, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, true, new()));
        Assert.Equal(0, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, true, new() { Finesse = 900 }));
        Assert.Equal(1, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, true, new() { Finesse = 900, PlayerIdKind = "assetId" }));
        Assert.Equal(0, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, true, new() { Finesse = 900, PlayerIdKind = "itemId" }));
    }

    [Theory]
    [InlineData("cross")]
    [InlineData("through_ball")]
    public void SpecialAssistsRequireTheAssistEventAndRespectThePlayersLeague(string type)
    {
        var (body, owned) = Report();
        var objective = Objective("assists");
        objective.Requirements!.AssistType = type;
        body["events"] = new JsonArray(Event("goal", 1001, 900), Event("assist", 1001, 900), Event("assist", 1004, 900));
        Assert.Equal(1, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.WeekendLeague, true,
            new() { Cross = 900, ThroughBall = 900, PlayerIdKind = "assetId" }));
    }

    [Fact]
    public void ClubRestrictionsCountOnlyThatClubsPlayers()
    {
        var (body, owned) = Report();
        foreach (var (id, player) in owned) player["clubId"] = id <= 2 ? 10 : 11;
        var objective = Objective();
        objective.Requirements!.StartingSquad = null;
        objective.Requirements.Player = new ObjectivePlayerRestriction { ClubId = 10 };
        Assert.Equal(2, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, true, new()));
        objective.Requirements.Player = new ObjectivePlayerRestriction { ClubId = 11 };
        Assert.Equal(1, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, true, new()));
        objective.Requirements.Player = new ObjectivePlayerRestriction { ClubId = 10, LeagueId = 14 };
        Assert.Equal(0, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, true, new()));
    }

    [Fact]
    public void ScoringMatchesWithAPlayerRestrictionNeedAMatchingScorer()
    {
        var (body, owned) = Report();
        var objective = Objective("scoringMatches");
        objective.Requirements!.StartingSquad = null;
        Assert.Equal(1, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, false, new()));
        objective.Requirements.Player = new ObjectivePlayerRestriction { LeagueId = 99 };
        Assert.Equal(0, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, false, new()));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 0)]
    public void CleanSheetsCountWhenTheOpponentDidNotScoreAndTheSquadQualifies(long against, long expected)
    {
        var (body, owned) = Report();
        body["opponentMatchStats"] = new JsonObject { ["goals"] = against };
        var objective = Objective("cleanSheets");
        objective.Requirements!.Player = null;
        Assert.Equal(expected, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, false, new()));
        objective.Requirements.StartingSquad!.MinimumCount = 4;
        Assert.Equal(0, ObjectiveMatchProgress.Count(objective, body, owned, RewardModes.Rivals, false, new()));
    }

    private static string RulesWith(JsonObject requirements, string stat)
    {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "rules", "objectives.json")))!.AsObject();
        var objective = document["grindGroups"]![0]!["objectives"]![0]!.AsObject();
        objective["stat"] = stat;
        objective["requirements"] = requirements;
        return document.ToJsonString();
    }

    [Fact]
    public void RulesAcceptClubAndCleanSheetConditionsAndRefuseAPlayerRestrictionOnMatchStats()
    {
        var club = new JsonObject { ["modes"] = new JsonArray("rivals"), ["player"] = new JsonObject { ["clubId"] = 1 } };
        var rules = RulesJson.Deserialize<ObjectiveRules>(RulesWith(club, "goals"), "objectives");
        Assert.Equal(1, rules.GrindGroups[0].Objectives[0].Requirements!.Player!.ClubId);
        RulesJson.Deserialize<ObjectiveRules>(RulesWith(new JsonObject
        {
            ["modes"] = new JsonArray("squadBattles"), ["startingSquad"] = new JsonObject { ["nation"] = 14, ["minimumCount"] = 5 },
        }, "cleanSheets"), "objectives");
        Assert.ThrowsAny<Exception>(() => RulesJson.Deserialize<ObjectiveRules>(RulesWith(
            new JsonObject { ["modes"] = new JsonArray("rivals"), ["player"] = new JsonObject { ["leagueId"] = 13 } }, "wins"), "objectives"));
        Assert.ThrowsAny<Exception>(() => RulesJson.Deserialize<ObjectiveRules>(RulesWith(
            new JsonObject { ["modes"] = new JsonArray("rivals"), ["player"] = new JsonObject() }, "goals"), "objectives"));
    }
}
