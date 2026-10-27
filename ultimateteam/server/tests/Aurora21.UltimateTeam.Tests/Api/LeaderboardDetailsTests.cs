using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The Leaderboards screen as FIFA 21 ships it (live 10-06: "missing tabs"): categories Match Earnings, Transfer Profit,
/// Club Value and Top Squad (and Division Rivals), Top 100 or Friends, Weekly, Monthly or All-Time. The info panel's rows
/// come from the board's "header" insets, drawn from the category options by id (LeaderboardInfoPanel.publishTitles), and
/// their values from the highlighted row's insetUrl (parser 0x180309aa0: "category" scores and "clubInfo").
/// </summary>
public sealed class LeaderboardDetailsTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public void BoardsCarryTheHeaderAndEachRowsDetailsUrl()
    {
        using var rt = TestRuntime.Create();
        var board = rt.CallObject("GET", $"{G}/leaderboards/period/Monthly/category/collector/view/TOP100");
        var header = board["header"]!;
        Assert.Equal("LOC_LB_RANK", header["rank"]!.GetValue<string>());
        Assert.Equal("LOC_LB_SCORE_COLLECTOR", header["score"]!.GetValue<string>());
        Assert.Equal(["competitor", "trader", "collector", "builder", "rivals"], header["inset"]!.AsArray().Select(inset => inset!["id"]!.GetValue<string>()));
        var row = Assert.Single(board["entries"]!.AsArray())!;
        Assert.Equal($"/period/Monthly/user/{rt.Game.Config.PersonaId}", row["insetUrl"]!.GetValue<string>());
        // FIFA draws the row's crest (BADGE_ID) from "teamId", not "badge".
        Assert.Equal(row["badge"]!.GetValue<long>(), row["teamId"]!.GetValue<long>());
        Assert.True(row["teamId"]!.GetValue<long>() > 0);
        Assert.True(row["score"]!["value"]!.GetValue<long>() > 0, "the club's players have a quick-sell value");
    }

    [Fact]
    public void CategoriesScoreMatchCoinsSalesAndSkill()
    {
        using var rt = TestRuntime.Create();
        rt.Game.InUnit(async uow =>
        {
            await uow.SaveRivalsRecordAsync(new RivalsRecord
            {
                RatingVersion = rt.Game.Rules.Seasons.Rivals.RatingVersion, SkillRating = 1234, Placed = true, Wins = 3, Losses = 2,
                MatchesPlayed = 6, TitleWins = 1, WeekDivision = 4, BestDivision = 4, Division = 4,
            });
            await uow.AddClubRecordAsync(5, 1, 4);
            await uow.AddMatchRewardAsync(new MatchRewardRow("rivals", "Paid", 777, null, 2500, null, false));
            await uow.AddMatchRewardAsync(new MatchRewardRow("rivals", "Paid", 778, null, 1500, null, false));
            await uow.AddMatchRewardAsync(new MatchRewardRow("rivals", "TooShort", 779, null, 0, null, false));
        });

        // Match Earnings: this week's paid match coins; nobody else has played.
        var earnings = rt.CallObject("GET", $"{G}/leaderboards/period/Weekly/category/competitor/view/TOP100");
        Assert.Equal("LOC_LB_SCORE_COMPETITOR", earnings["header"]!["score"]!.GetValue<string>());
        var row = Assert.Single(earnings["entries"]!.AsArray())!;
        Assert.Equal(4000, row["score"]!["value"]!.GetValue<long>());
        Assert.Equal(2, row["tiebreak"]!["value"]!.GetValue<long>());

        // Transfer Profit lists only clubs that traded.
        Assert.Empty(rt.CallObject("GET", $"{G}/leaderboards/period/AllTime/category/trader/view/TOP100")["entries"]!.AsArray());

        // Division Rivals keeps the Skill Rating board; Friends are this club and the clubs it has played.
        var rivals = rt.CallObject("GET", $"{G}/leaderboards/period/Weekly/category/rivals/view/FRIENDS");
        Assert.Equal(1234, Assert.Single(rivals["entries"]!.AsArray())!["score"]!["value"]!.GetValue<long>());

        var details = rt.CallObject("GET", $"{G}/leaderboards/period/Weekly/user/{rt.Game.Config.PersonaId}");
        var scores = details["category"]!.AsArray().ToDictionary(c => c!["id"]!.GetValue<string>(), c => c!["score"]!["value"]!.GetValue<long>());
        Assert.Equal(4000, scores["competitor"]);
        Assert.Equal(0, scores["trader"]);
        Assert.True(scores["collector"] > 0);
        Assert.Equal(1234, scores["rivals"]);
        Assert.True(scores["builder"] > 0, "the active XI is eleven owned cards");
        var club = details["clubInfo"]!;
        Assert.Equal(5, club["won"]!.GetValue<long>());
        Assert.Equal(1, club["draw"]!.GetValue<long>());
        Assert.Equal(4, club["loss"]!.GetValue<long>());
        Assert.Equal(1, club["trophies"]!.GetValue<long>());
        Assert.Equal(0, club["credits"]!.GetValue<long>());

        // The viewer's own row is asked for with FIFA's user id, not a persona: it is the caller's club.
        var own = rt.CallObject("GET", $"{G}/leaderboards/period/AllTime/user/1051020479?group=SEASONS");
        Assert.Equal(4000, own["category"]![0]!["score"]!["value"]!.GetValue<long>());
    }

    [Fact]
    public void TopSquadSkipsAnXiWithConceptCards()
    {
        using var rt = TestRuntime.Create();
        var before = rt.CallObject("GET", $"{G}/leaderboards/period/Weekly/category/builder/view/TOP100");
        Assert.True(Assert.Single(before["entries"]!.AsArray())!["score"]!["value"]!.GetValue<long>() > 0);

        // Squad Builder lets FIFA save a starter the club doesn't own (live 10-07: an XI of concept 96-99s ranked first).
        rt.Game.InUnit(uow => uow.ExecuteAsync("""
            UPDATE ultimateteam.squad_players p SET reference_kind='concept', item_id=NULL, concept_wire_id=1
            FROM ultimateteam.squads s WHERE s.id=p.squad_id AND s.active AND p.slot_index=0;
            """));

        // A fresh period skips the cached Weekly scores.
        var after = rt.CallObject("GET", $"{G}/leaderboards/period/Monthly/category/builder/view/TOP100");
        Assert.Equal(0, Assert.Single(after["entries"]!.AsArray())!["score"]!["value"]!.GetValue<long>());
    }
}
