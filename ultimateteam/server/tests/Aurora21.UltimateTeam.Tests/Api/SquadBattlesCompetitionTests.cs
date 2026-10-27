using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Squad Battles as in FIFA 21 retail (Python server f354894, a4fdc85; migration 060): the retail Battle Points, opponents
/// frozen from the server's real clubs and refreshed on request, the Team of the Week, and each week settled once for every
/// club by rank and percentile and claimed once.
/// </summary>
public sealed class SquadBattlesCompetitionTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Tuesday 2026-09-29 12:00 UTC: week 2960 (Monday 2026-09-28 00:00 to Monday 2026-10-05).</summary>
    private const long Tuesday = 1_790_683_200;
    private const long WeekEnd = 1_791_158_400;

    private static JsonObject Play(TestRuntime rt, JsonObject create, string reason, long mine, long theirs, JsonObject? stats = null,
        long myPens = 0, long theirPens = 0)
    {
        create["sqbtEventId"] = 2960;
        rt.Call("POST", $"{G}/match", create);
        rt.PlayedRealSeconds();
        stats ??= new JsonObject { ["goals"] = mine };
        stats["penaltyGoals"] = myPens;
        return rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = reason,
            ["myMatchStats"] = stats,
            ["opponentMatchStats"] = new JsonObject { ["goals"] = theirs, ["penaltyGoals"] = theirPens },
        });
    }

    private static JsonObject Slot(long slot, long difficulty) => new() { ["sqbtOpponentSquadId"] = slot, ["sqbtMatchDifficulty"] = difficulty };

    private static JsonObject Score(JsonObject ended) => ended["squadBattlesScore"]!.AsObject();

    private static List<JsonObject> Tiles(JsonObject hub) => hub["sqbtOppSquadList"]!["sqbtOppSquads"]!.AsArray().OfType<JsonObject>().ToList();

    [Fact]
    public void TheRetailFormulaScoresTeamRatingResultSkillAndGoals()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        var competition = rt.Game.Rules.SquadBattles.Competition;
        var stats = new JsonObject
        {
            ["goals"] = 2, ["shotsOnTarget"] = 7, ["successfulTackles"] = 25, ["corners"] = 3, ["passingPercentage"] = 85,
            ["possessionPercentage"] = 55, ["manOfTheMatch"] = 1, ["fouls"] = 5, ["yellowCards"] = 1, ["offsides"] = 2,
        };
        // World Class (x1.6): team 85 x 3 + 90 / 2 = 300, win 200, skill 300 less 32 in penalties and 20 for the goal conceded.
        var points = competition.MatchPoints(true, 4, 85, 90, stats, 2, 1);
        Assert.Equal(new SquadBattlesPoints(1397, 300, 200, 248, 200, 1.6), points);
        // The difficulty screen's preview: 3 x 85 + 90 / 2 + 200 + 300.
        Assert.Equal(800, competition.BasePoints(85, 90, true));
        Assert.Equal(1280, competition.DifficultyPoints(85, 90).Win[4]);
        // A loss is 50 for the result at the loss modifier, and never below zero.
        Assert.Equal(0, competition.MatchPoints(false, 0, 10, 0, new JsonObject { ["fouls"] = 20 }, 0, 9).Final);
    }

    [Fact]
    public void AMatchScoresItsSquadOnceAndIsWonOrLost()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        var competition = rt.Game.Rules.SquadBattles.Competition;
        var hub = rt.CallObject("GET", $"{G}/sqbt/user/hub");
        Assert.Equal((2960L, 0L, 40L), (hub.Int("sqbtEventId"), hub.Int("gamesPlayed"), hub.Int("maxRegularGames")));
        Assert.Equal(new[] { 0.5, 0.6, 0.7, 1.0, 1.6, 2.1, 2.6 }, hub["winDifficultyModifiers"]!.AsArray().Select(value => value!.GetValue<double>()));
        var tile = Tiles(hub)[2];
        var (rating, chemistry) = (tile.Int("rating"), tile.Int("chemistry"));
        Assert.Equal(competition.BasePoints(rating, chemistry, true), tile.Int("basePointsWin"));
        Assert.Equal(-1, tile.Int("pointsWon", -1));

        // Pro (x1.0), 3-0: team + 200 + 3 goals x 40 + 6 shots x 5 + a clean sheet, then 3 goals x 60.
        var ended = Play(rt, Slot(2, 3), "WIN", 3, 0, new JsonObject { ["goals"] = 3, ["shotsOnTarget"] = 6, ["cleansheets"] = 1 });
        var score = Score(ended);
        Assert.Equal(new[] { "finalScore", "matchResultScore", "teamRatingScore", "skillRatingScore", "goalsScore", "matchDifficultyScoreModifier" },
            score.Select(member => member.Key));
        var team = SquadBattlesCompetitionRules.TeamRatingScore(rating, chemistry);
        Assert.Equal((team, 200L, 225L, 180L), (score.Int("teamRatingScore"), score.Int("matchResultScore"), score.Int("skillRatingScore"), score.Int("goalsScore")));
        Assert.Equal(team + 200 + 225 + 180, score.Int("finalScore"));
        Assert.Equal(1, ended.Int("gamesPlayed"));

        hub = rt.CallObject("GET", $"{G}/sqbt/user/hub");
        Assert.Equal((score.Int("finalScore"), 1L), (hub.Int("score"), hub.Int("gamesPlayed")));
        var played = Tiles(hub)[2];
        Assert.Equal((score.Int("finalScore"), 3L, 0L, -1L, "PRO"), (played.Int("pointsWon"), played.Int("score"), played.Int("oppScore"), played.Int("penaltyScore"), played.Text("difficulty")));
        var board = rt.CallObject("GET", $"{G}/sqbt/leaderboard")["entries"]!.AsArray().Single()!.AsObject();
        Assert.Equal((1L, "FFA21PCC", score.Int("finalScore")), (board.Int("rank"), board.Text("sku"), board["score"]!.AsObject().Int("value")));

        // The same squad scores nothing again.
        Assert.Equal(0, Score(Play(rt, Slot(2, 3), "WIN", 1, 0)).Int("finalScore"));
        // A level match goes to penalties: 1-1, 3-4 on penalties is a loss (50 for the result), and the tile shows the shootout.
        var shootout = Play(rt, Slot(0, 0), "DRAW", 1, 1, myPens: 3, theirPens: 4);
        Assert.Equal((50L, 1L), (Score(shootout).Int("matchResultScore"), shootout.Int("loss")));
        var lost = Tiles(rt.CallObject("GET", $"{G}/sqbt/user/hub"))[0];
        Assert.Equal((3L, 4L, "BEGINNER"), (lost.Int("penaltyScore"), lost.Int("oppPenaltyScore"), lost.Text("difficulty")));
        // A quit is a loss worth no points, and the squad counts as played.
        var quit = Play(rt, Slot(1, 6), "QUIT", 2, 0);
        Assert.Equal((0L, 1L), (Score(quit).Int("finalScore"), quit.Int("loss")));
        Assert.Equal((0L, 3L), (Tiles(rt.CallObject("GET", $"{G}/sqbt/user/hub"))[1].Int("pointsWon"), rt.CallObject("GET", $"{G}/sqbt/user/hub").Int("gamesPlayed")));

        // Any other match ends without a Squad Battles score.
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 });
        Assert.False(rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN" }).ContainsKey("squadBattlesScore"));
    }

    [Fact]
    public void AGameEndingWithTheLastOnlineMatchIdStillScoresAndReplaysAsItself()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        // An online game that ends in a quit, then a Squad Battles game whose end FIFA sends with that game's matchId (live
        // 10-05: the win was answered as the quit's replay and never scored).
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 });
        var quit = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "QUIT", ["matchId"] = 18490667767301 });
        rt.Call("POST", $"{G}/match", new JsonObject { ["sqbtEventId"] = 2960, ["sqbtOpponentSquadId"] = 0, ["sqbtMatchDifficulty"] = 3 });
        rt.PlayedRealSeconds();
        JsonObject End() => new()
        {
            ["endReason"] = "WIN", ["matchId"] = 18490667767301,
            ["myMatchStats"] = new JsonObject { ["goals"] = 8 }, ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
        };
        var won = rt.CallObject("POST", $"{G}/match/end", End());
        Assert.True(Score(won).Int("finalScore") > 0);
        Assert.Equal(1, rt.CallObject("GET", $"{G}/sqbt/user/hub").Int("gamesPlayed"));
        // A retry of either end is a replay of its own response and settles nothing again.
        // The route adds one-time objective updates after storing the match settlement. Repeating
        // autoRedeemedXp would increment FIFA's cached XP twice; only the settled match fields replay.
        Assert.True(won["dynamicObjectivesUpdates"]!.AsObject().Int("autoRedeemedXp") > 0);
        var settled = won.DeepClone().AsObject();
        settled.Remove("dynamicObjectivesUpdates");
        var credits = rt.Game.InUnit(work => work.CreditsAsync());
        var replay = rt.CallObject("POST", $"{G}/match/end", End());
        Assert.False(replay.ContainsKey("dynamicObjectivesUpdates"));
        Assert.True(JsonNode.DeepEquals(settled, replay));
        Assert.Equal(credits, rt.Game.InUnit(work => work.CreditsAsync()));
        Assert.Equal(1, rt.CallObject("GET", $"{G}/sqbt/user/hub").Int("gamesPlayed"));
        Assert.False(JsonNode.DeepEquals(quit, won));
    }

    [Fact]
    public void ARefreshDrawsNewOpponentsAndCountsTheUnplayedSquads()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock);
        // As in FIFA 21 retail, the player may refresh whenever they want: the refresh time is the draw itself.
        var hub = rt.CallObject("GET", $"{G}/sqbt/user/hub");
        Assert.Equal(Tuesday, hub["sqbtOppSquadList"]!.AsObject().Int("nextSquadRefreshTimeStamp"));
        // A set drawn under the old six-hour timer follows the current rule.
        rt.Game.InUnit(async uow => await uow.SaveSquadBattlesSetAsync((await uow.SquadBattlesSetAsync())! with { NextRefreshAt = Tuesday + 21600 }));
        Assert.Equal(Tuesday, rt.CallObject("GET", $"{G}/sqbt/user/hub")["sqbtOppSquadList"]!.AsObject().Int("nextSquadRefreshTimeStamp"));
        Play(rt, Slot(3, 3), "WIN", 1, 0);

        // Four new squads at once, and the three unplayed ones count toward the week for no points.
        var refreshed = rt.CallObject("PUT", $"{G}/sqbt/v2/user/opponents/refresh");
        Assert.Equal((4L, clock.Now), (refreshed.Int("gamesPlayed"), refreshed.Int("nextSquadRefreshTimeStamp")));
        Assert.All(refreshed["sqbtOppSquads"]!.AsArray().OfType<JsonObject>(), squad => Assert.Equal(-1, squad.Int("pointsWon", -1)));
        Assert.Equal(4, rt.CallObject("GET", $"{G}/sqbt/user/hub").Int("gamesPlayed"));

        // A match created against a set that is replaced before it ends still scores once for its slot.
        clock.Now += 60;
        rt.Call("POST", $"{G}/match", new JsonObject { ["sqbtEventId"] = 2960, ["sqbtOpponentSquadId"] = 1, ["sqbtMatchDifficulty"] = 3 });
        Assert.Equal(8, rt.CallObject("PUT", $"{G}/sqbt/v2/user/opponents/refresh").Int("gamesPlayed"));
        rt.PlayedRealSeconds();
        var ended = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN", ["myMatchStats"] = new JsonObject { ["goals"] = 1 } });
        Assert.True(Score(ended).Int("finalScore") > 0);

        // Once the week's matches are used up, a refresh returns the current set unchanged.
        var max = rt.Game.Rules.SquadBattles.Competition.MaxRegularGames;
        for (var counted = rt.CallObject("GET", $"{G}/sqbt/user/hub").Int("gamesPlayed"); counted < max;)
        {
            counted = rt.CallObject("PUT", $"{G}/sqbt/v2/user/opponents/refresh").Int("gamesPlayed");
        }
        var last = rt.CallObject("GET", $"{G}/sqbt/user/hub")["sqbtOppSquadList"]!.AsObject();
        var refused = rt.CallObject("PUT", $"{G}/sqbt/v2/user/opponents/refresh");
        Assert.Equal(max, refused.Int("gamesPlayed"));
        Assert.True(JsonNode.DeepEquals(last["sqbtOppSquads"], refused["sqbtOppSquads"]));
    }

    [Fact]
    public void OpponentsAreOtherRealClubsFrozenWhenDrawn()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday), prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "state"));
            File.WriteAllText(Path.Combine(root, "state", "player-names.json"), """{ "222222": "Second Team", "333333": "Third Team" }""");
        });
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var second = players.Acquire(PlayerIdentity.Of("222222"));
        using var third = players.Acquire(PlayerIdentity.Of("333333"));
        second.Session.Game.FillMatchdaySquad(second.Session.Fut);
        third.Session.Game.FillMatchdaySquad(third.Session.Fut);
        var ownName = rt.Game.InUnit(uow => uow.DisplayNameAsync());

        var tiles = Tiles(rt.CallObject("GET", $"{G}/sqbt/user/hub"));
        var real = tiles.Where(tile => tile.Text("clubName") is "SecondTeam" or "Second Team" or "ThirdTeam" or "Third Team").ToList();
        Assert.NotEmpty(real);
        Assert.DoesNotContain(tiles, tile => tile.Text("clubName") == ownName);
        foreach (var tile in real)
        {
            var slot = tile.Int("id");
            var squad = rt.CallObject("GET", $"{G}/sqbt/user/opponentsquad/{slot}");
            Assert.Equal(rt.Game.Rules.SquadBattles.Sets.SetPersonaBase + slot, squad.Int("personaId"));
            // The other club's own cards: the same item ids as its active squad.
            var owner = tile.Text("clubName").Contains("Second", StringComparison.Ordinal) ? second : third;
            var ownSquad = owner.Session.Game.InUnit(uow => owner.Session.Fut.Club.NativeSquadAsync(uow));
            Assert.Equal(Ids(ownSquad), Ids(squad));
            Assert.Equal(3, tile["sqbt2data"]!["topPlayers"]!.AsArray().Count);
            Assert.False(string.IsNullOrEmpty(tile.Text("nation")));
            Assert.Equal(30, JsonNode.Parse(tile.Text("custom"))!.AsArray().Count);
            // The match hands FIFA the same frozen squad, so a real club hosts its away fixtures in its own stadium.
            var opponent = rt.CallObject("POST", $"{G}/match", Slot(slot, 0))["opponent"]!.AsObject();
            Assert.Equal(squad.Int("personaId"), opponent.Int("personaId"));
            Assert.Equal(rt.Game.Rules.Club.Venues.DefaultVenueId, opponent.Int("stadiumId"));
            Assert.NotEqual(rt.Game.Rules.SquadBattles.Venue.StadiumId, opponent.Int("stadiumId"));
        }
        // Frozen: the same squad comes back.
        Assert.Equal(tiles.Select(tile => tile.Text("clubName")), Tiles(rt.CallObject("GET", $"{G}/sqbt/user/hub")).Select(tile => tile.Text("clubName")));

        static IEnumerable<long> Ids(JsonObject squad) =>
            squad["players"]!.AsArray().Take(11).OfType<JsonObject>().Select(slot => slot["itemData"]!.AsObject().Int("id"));
    }

    [Fact]
    public void AnUnplayedOpponentWithAnEmptyBenchSlotIsRedrawn()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday), prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "state"));
            File.WriteAllText(Path.Combine(root, "state", "player-names.json"), """{ "222222": "Second Team", "333333": "Third Team" }""");
        });
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var second = players.Acquire(PlayerIdentity.Of("222222"));
        using var third = players.Acquire(PlayerIdentity.Of("333333"));
        second.Session.Game.FillMatchdaySquad(second.Session.Fut);
        third.Session.Game.FillMatchdaySquad(third.Session.Fut);
        Tiles(rt.CallObject("GET", $"{G}/sqbt/user/hub"));

        // A set drawn before bench holes were refused (live 10-05: Chronicc's two 78s each had an empty substitute slot, and
        // FIFA failed every match start against them). One real snapshot gets a hole on the bench, another one among the reserves.
        var set = rt.Game.InUnit(uow => uow.SquadBattlesSetAsync())!;
        var real = set.Squads.OfType<JsonObject>().Where(squad => squad.Int("source") > 0).ToList();
        Assert.NotEmpty(real);
        var (benchSlot, benchSource) = (real[0].Int("id"), real[0].Int("source"));
        real[0]["doc"]!["players"]![12]!.AsObject().Remove("itemData");
        long? reserveSlot = null;
        if (real.Count > 1)
        {
            reserveSlot = real[1].Int("id");
            real[1]["doc"]!["players"]![20]!.AsObject().Remove("itemData");
        }
        rt.Game.InUnit(uow => uow.SaveSquadBattlesSetAsync(set));

        Tiles(rt.CallObject("GET", $"{G}/sqbt/user/hub"));
        var repaired = rt.Game.InUnit(uow => uow.SquadBattlesSetAsync())!;
        var redrawn = repaired.Squads.OfType<JsonObject>().Single(squad => squad.Int("id") == benchSlot);
        Assert.NotEqual(benchSource, redrawn.Int("source"));
        Assert.Null(redrawn.Obj("result"));
        // FIFA is handed a squad with all eighteen matchday cards.
        var served = rt.CallObject("GET", $"{G}/sqbt/user/opponentsquad/{benchSlot}");
        Assert.Equal(18, served["players"]!.AsArray().OfType<JsonObject>().Take(18).Count(slot => slot["itemData"] is JsonObject));
        // An empty reserve slot is fine: that squad stays.
        if (reserveSlot is { } kept)
        {
            Assert.Equal(set.Squads.OfType<JsonObject>().Single(squad => squad.Int("id") == kept).Int("source"),
                repaired.Squads.OfType<JsonObject>().Single(squad => squad.Int("id") == kept).Int("source"));
        }
    }

    [Fact]
    public void TheTeamOfTheWeekIsBrowsableAndOnlyTheCurrentOneScoresItsImprovement()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        var archive = rt.Game.Rules.Totw;
        var current = archive.Current;
        var squadId = 2_000_000 + current;

        var tile = rt.CallObject("GET", $"{G}/featuredsquad/user/stats?featureConsumerId=sqbttotw");
        Assert.Equal((squadId, WeekEnd, $"totw_{current}_v1.png"), (tile.Int("id"), tile.Int("endTimeStamp"), tile.Text("bannerImageName")));
        Assert.Equal(7, tile["difficultyBasedWinPoints"]!.AsArray().Count);
        Assert.Equal(-1, rt.CallObject("GET", $"{G}/featuredsquad/user/stats?featureConsumerId=sqbt").Int("id"));
        var history = rt.Call("GET", $"{G}/featuredsquad/history?featureConsumerId=sqbttotw").AsArray().Select(id => id!.GetValue<long>()).ToList();
        Assert.Equal(archive.Weeks.Count, history.Count);
        Assert.Equal(squadId, history[0]);

        // EA's XI in its 3-4-3 slots (the chemistry table's order), then the bench.
        var squad = rt.CallObject("GET", $"{G}/featuredsquad/{squadId}?featureConsumerId=sqbttotw");
        var week = archive.Week(current)!;
        var slots = rt.Game.Chemistry.Formations[week.Formation].Select(position => rt.Game.Chemistry.Positions[position]).ToList();
        var bySlot = week.Starters.ToDictionary(card => card.Slot!, card => card.ResourceId);
        var cards = squad["players"]!.AsArray().OfType<JsonObject>().Select(slot => slot["itemData"]!.AsObject().Int("resourceId")).ToList();
        Assert.Equal(slots.Select(slot => bySlot[slot]), cards.Take(11));
        Assert.Equal(week.Subs.Select(card => card.ResourceId), cards.Skip(11));
        Assert.Equal((2_100_001_000 + current, false), (squad.Int("personaId"), squad.Bool("isHomeTeam")));
        Assert.Equal(404, rt.CallRaw("GET", $"{G}/featuredsquad/{squadId}?featureConsumerId=sqbt").Status);

        // Played: the TOTW is extra to the week's matches, and a replay counts only by what it improves.
        var first = Score(Play(rt, new JsonObject { ["featuredSquadId"] = squadId, ["sqbtMatchDifficulty"] = 3 }, "WIN", 1, 0)).Int("finalScore");
        Assert.True(first > 0);
        Assert.Equal((first, 0L), (rt.CallObject("GET", $"{G}/sqbt/user/hub").Int("score"), rt.CallObject("GET", $"{G}/sqbt/user/hub").Int("gamesPlayed")));
        Assert.Equal(0, Score(Play(rt, new JsonObject { ["featuredSquadId"] = squadId, ["sqbtMatchDifficulty"] = 3 }, "LOSS", 0, 1)).Int("finalScore"));
        var better = Score(Play(rt, new JsonObject { ["featuredSquadId"] = squadId, ["sqbtMatchDifficulty"] = 6 }, "WIN", 5, 0)).Int("finalScore");
        Assert.True(better > 0);
        var total = rt.CallObject("GET", $"{G}/sqbt/user/hub").Int("score");
        Assert.Equal(first + better, total);
        Assert.Equal(total, rt.CallObject("GET", $"{G}/featuredsquad/user/stats?featureConsumerId=sqbttotw").Int("pointsWon"));
        // An older TOTW can be viewed but scores nothing.
        Assert.Equal(0, Score(Play(rt, new JsonObject { ["featuredSquadId"] = 2_000_001, ["sqbtMatchDifficulty"] = 6 }, "WIN", 5, 0)).Int("finalScore"));

        var banner = rt.Router.Handle(new Aurora21.UltimateTeam.Core.Web.FutRequest("GET", $"/fut/squadBattle/cmn/totw_{current}_v1.png", [], []));
        Assert.Equal((200, "image/png"), (banner.Status, banner.ContentType));
        Assert.True(banner.Body.AsSpan(1, 3).SequenceEqual("PNG"u8));
        Assert.Equal(404, rt.Router.Handle(new Aurora21.UltimateTeam.Core.Web.FutRequest("GET", "/fut/squadBattle/cmn/totw_99_v1.png", [], [])).Status);
    }

    [Fact]
    public void RanksAreTheSixTopSlotsThenRetailPercentileBands()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        var rewards = rt.Game.Rules.SquadBattlesRewards;
        var rows = Enumerable.Range(1, 400).Select(index => new SquadBattlesWeekRow(index, $"club{index:D3}", 10_000 - index, 0, 1)).ToList();
        var table = rewards.Standings(rows);
        // Six Top slots placed by the Top players number (25, migration 133): 1, 2-5, 6-10, 11-15, 16-20, 21-25.
        Assert.Equal(new[] { "rank1 1-1", "top20 2-5", "top40 6-10", "top100 11-15", "ranked5 16-20", "top200 21-25" },
            rewards.RankedBands().Select(band => $"{band.Tier.Id} {band.Start}-{band.End}"));
        Assert.Equal(("rank1", 1), (table[0].Tier!.Id, table[0].Rank));
        Assert.Equal(("top20", 5), (table[4].Tier!.Id, table[4].Rank));
        Assert.Equal(("top200", 25), (table[24].Tier!.Id, table[24].Rank));
        // Position 26 of 400 is the 7th percentile, inside Elite 3's band, and 9,974 points reaches its retail minimum (9,800).
        Assert.Equal(("elite3", 0), (table[25].Tier!.Id, table[25].Rank));
        // Position 201 of 400 is the 51st percentile: Silver 2 (51-60%).
        Assert.Equal(("silver2", 0, 8), (table[200].Tier!.Id, table[200].Rank, table[200].TierLevel));
        Assert.Equal("bronze3", table[^1].Tier!.Id);
        // A lone club has no Top-N ranks: it is the 100th percentile, Bronze 3.
        Assert.Equal("bronze3", rewards.Standings([rows[0]]).Single().Tier!.Id);
    }

    [Fact]
    public void ATierNeedsItsBandAndItsRetailMinimumAndTheHubSendsTheSameThresholds()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        var rewards = rt.Game.Rules.SquadBattlesRewards;
        long[] Starts(JsonObject hub) => hub["prizeTiers"]!.AsArray().OfType<JsonObject>().Where(tier => tier.Text("tierType") == "PERCENTILE")
            .Select(tier => tier.Int("tierStartValue")).ToArray();
        var minimums = rewards.PointsTiers.Select(tier => tier.MinPoints).ToArray();

        // An empty week: the thresholds are retail's minimums, never 1 (FIFA placed a first win of 1,463 points in Elite 1 when
        // every tier started at 1).
        var empty = rt.CallObject("GET", $"{G}/sqbt/user/hub");
        Assert.Equal(minimums, Starts(empty));
        Assert.Equal(0, empty.Int("userTierLevel"));

        // Alone with 1,463 points the club is the whole field, the 100th percentile: Bronze 3, and every better tier's threshold is
        // out of its reach, so FIFA's own placement agrees.
        rt.Game.InUnit(uow => uow.SaveSquadBattlesWeekAsync(SquadBattlesWeek.Blank(2960) with { Points = 1_463, Games = 1, Wins = 1 }));
        var alone = rt.CallObject("GET", $"{G}/sqbt/user/hub");
        Assert.Equal(12, alone.Int("userTierLevel"));
        var starts = Starts(alone);
        Assert.Equal(1_463, starts[^1]);
        Assert.All(starts[..^1], start => Assert.True(start > 1_463));
        Assert.Equal(starts.OrderByDescending(start => start), starts);

        // Ten clubs: a tier's threshold is the higher of its minimum and the score of the last club inside its band.
        var rows = Enumerable.Range(1, 10).Select(index => new SquadBattlesWeekRow(index, $"club{index:D2}", 20_000 - 1_000 * index, 0, 1)).ToList();
        var thresholds = rewards.Thresholds(rows);
        Assert.Equal(19_001, thresholds["elite1"]); // the top 3% of 10 is nobody: one point above the leader
        Assert.Equal(19_000, thresholds["elite3"]); // the top 10% is 1st place
        Assert.Equal(15_000, thresholds["silver1"]); // the top 50% ends at 5th place
        Assert.Equal(10_000, thresholds["bronze3"]);
        // Without Top-N bands: 1st is Elite 3, 5th (15,000) Silver 1, 10th Bronze 3; each holds a tier its points reach.
        var table = rewards.Standings(rows, []);
        Assert.Equal(new[] { "elite3", "silver1", "bronze3" }, new[] { table[0], table[4], table[9] }.Select(standing => standing.Tier!.Id));
        Assert.All(table, standing => Assert.True(standing.Row.Points >= thresholds[standing.Tier!.Id]));
    }

    [Fact]
    public void ThePrizesAreThePhaseOneTable()
    {
        using var rt = TestRuntime.Create();
        var rewards = rt.Game.Rules.SquadBattlesRewards;
        static string Prize(SquadBattlesTier tier) => string.Join("+", tier.Awards.Select(award => award.Type == "coin" ? $"{award.Amount}" : $"{award.PackId}"));
        // PLAN.md 5.3.
        Assert.Equal(new[]
        {
            "rank1 60000+405+402+540", "top20 50000+405+402+542", "top40 45000+405+542+543", "top100 40000+404+542+543",
            "ranked5 40000+404+542+543", "top200 35000+404+542", "elite1 30000+402+542", "elite2 22000+402", "elite3 16000+403", "gold1 12000+400+305",
            "gold2 10000+400", "gold3 8000+319", "silver1 6000+308", "silver2 4000+543", "silver3 3000+305", "bronze1 2000+304",
            "bronze2 1000+304", "bronze3 304",
        }, rewards.Tiers.Select(tier => $"{tier.Id} {Prize(tier)}"));
        // The percentile tiers keep their retail bands and minimum points.
        Assert.Equal("percentile", rewards.Mode);
        Assert.Equal(new long[] { 15_000, 12_000, 9_800, 7_800, 6_200, 4_800, 3_600, 2_700, 1_900, 1_200, 700, 250 }, rewards.PointsTiers.Select(tier => tier.MinPoints));
        Assert.Equal(new[] { 1, 4, 7, 11, 21, 31, 41, 51, 61, 71, 81, 91 }, rewards.PointsTiers.Select(tier => tier.PercentStart));
    }

    [Fact]
    public void TopSlotsFollowTheTopPlayersNumberNotTheField()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var second = players.Acquire(PlayerIdentity.Of("222222"));
        // Week 2959 placed 500 clubs; week 2960 has two with points, so its Top slots show: their places do not depend on either.
        rt.Game.InUnit(uow => uow.ExecuteAsync(
            "INSERT INTO ultimateteam.squad_battles_settlements(week_id, settled_at, participants, ranked) VALUES (2959, @now, 500, true)", ("now", Tuesday)));
        rt.Game.InUnit(uow => uow.SaveSquadBattlesWeekAsync(SquadBattlesWeek.Blank(2960) with { Points = 3000, Games = 1, Wins = 1 }));
        second.Session.Game.InUnit(uow => uow.SaveSquadBattlesWeekAsync(SquadBattlesWeek.Blank(2960) with { Points = 2000, Games = 1, Wins = 1 }));
        string Ranked() => string.Join(",", rt.CallObject("GET", $"{G}/sqbt/user/hub")["prizeTiers"]!.AsArray().OfType<JsonObject>()
            .Where(tier => tier.Text("tierType") == "LB_RANKING_TYPE").Select(tier => $"{tier.Int("tierStart")}-{tier.Int("tierEnd")}"));
        Assert.Equal("1-1,2-5,6-10,11-15,16-20,21-25", Ranked());
        rt.Game.InUnit(uow => uow.ExecuteAsync("UPDATE ultimateteam.squad_battles_settlements SET participants = 0 WHERE week_id = 2959"));
        Assert.Equal("1-1,2-5,6-10,11-15,16-20,21-25", Ranked());
    }

    [Fact]
    public void AClosedWeekSettlesOnceForEveryClubAndIsClaimedOnce()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock, prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "state"));
            File.WriteAllText(Path.Combine(root, "state", "player-names.json"), """{ "222222": "Second Team", "333333": "Third Team" }""");
        });
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var second = players.Acquire(PlayerIdentity.Of("222222"));
        using var third = players.Acquire(PlayerIdentity.Of("333333"));
        second.Session.Game.FillMatchdaySquad(second.Session.Fut);
        third.Session.Game.FillMatchdaySquad(third.Session.Fut);
        SquadBattlesWeek Week(long points) => SquadBattlesWeek.Blank(2960) with { Points = points, Games = 10, Wins = 6, Losses = 4 };
        rt.Game.InUnit(uow => uow.SaveSquadBattlesWeekAsync(Week(3000)));
        second.Session.Game.InUnit(uow => uow.SaveSquadBattlesWeekAsync(Week(2000)));
        third.Session.Game.InUnit(uow => uow.SaveSquadBattlesWeekAsync(Week(1000)));
        var live = rt.CallObject("GET", $"{G}/sqbt/user/hub");
        // Top-N ranks apply from two clubs with points: first place is "ranked #1". The six Top slots show whatever the
        // previous week's field (migration 133), then the 12 percentile tiers.
        Assert.Equal((1L, 0L, false), (live.Int("rank"), live.Int("userTierLevel"), live.Bool("isPrizeAvailable")));
        Assert.Equal(6 + 12, live["prizeTiers"]!.AsArray().Count);
        Assert.Empty(rt.CallObject("GET", $"{G}/sqbt/user/prize")["awardedPrizes"]!.AsArray());

        // The next week, after the settle delay: Rank 1's prize.
        clock.Now = WeekEnd + rt.Game.Rules.SquadBattlesRewards.SettleDelaySeconds;
        Assert.True(rt.CallObject("GET", $"{G}/hub")["sqbtEvent"]!.AsObject().Bool("unclaimedPrizeWaiting"));
        var tier = rt.Game.Rules.SquadBattlesRewards.Tiers.Single(candidate => candidate.Id == "rank1");
        var credits = rt.Game.Store().Credits();
        var packs = rt.Game.Store().Run(uow => uow.UnopenedPackIdsAsync()).Count;
        var prize = Assert.Single(rt.CallObject("GET", $"{G}/sqbt/user/prize")["awardedPrizes"]!.AsArray().OfType<JsonObject>());
        Assert.Equal((2960L, 1L, 0L), (prize.Int("eventId"), prize.Int("rank"), prize.Int("tierLevel")));
        Assert.Equal(credits + tier.Awards.Where(award => award.Type == "coin").Sum(award => award.Amount), rt.Game.Store().Credits());
        Assert.Equal(packs + tier.Awards.Where(award => award.Type == "pack").Sum(award => award.Count), rt.Game.Store().Run(uow => uow.UnopenedPackIdsAsync()).Count);
        Assert.Empty(rt.CallObject("GET", $"{G}/sqbt/user/prize")["awardedPrizes"]!.AsArray());
        Assert.False(rt.CallObject("GET", $"{G}/sqbt/user/hub").Bool("isPrizeAvailable"));
        // The others' results were settled with it. The six Top slots pay whatever the field (migration 133): second and third
        // place both take Rank 2-5 (top20), ranked 2 and 3.
        var secondPlace = Assert.Single(second.Session.Game.InUnit(uow => uow.UnclaimedSquadBattlesResultsAsync(2960, 2960)));
        Assert.Equal(("top20", 2), (secondPlace.TierId, secondPlace.Rank));
        var thirdPlace = Assert.Single(third.Session.Game.InUnit(uow => uow.UnclaimedSquadBattlesResultsAsync(2960, 2960)));
        Assert.Equal(("top20", 3), (thirdPlace.TierId, thirdPlace.Rank));
    }
}
