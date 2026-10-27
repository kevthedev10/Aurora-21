using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Draft odds v2 (rules/draft.json picks, migration 115). Live, 48% of Draft offers were Icons and 21% TOTW in-forms:
/// specials weighed 4.5x a standard category and the 87+ floor left little but Icons at most positions.
/// </summary>
public sealed class DraftOddsTests
{
    private static List<List<JsonObject>> Screens(TestRuntime rt, int runs)
    {
        var draft = rt.Fut.Draft;
        var formations = new[] { "f442", "f433", "f4231", "f352", "f41212" };
        var screens = new List<List<JsonObject>>();
        for (var seed = 1; seed <= runs; seed++)
        {
            var run = draft.NewRun();
            run["seed"] = seed;
            run["formation"] = formations[seed % formations.Length];
            screens.Add(draft.PlayerChoices(run, 0, captain: true));
            for (var position = 0; position < 18; position++) screens.Add(draft.PlayerChoices(run, position));
        }
        return screens;
    }

    [Fact]
    public void IconsAreAFindAndScreensStayStrong()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var screens = Screens(rt, 30);
        var offers = screens.SelectMany(screen => screen).ToList();
        double Share(Func<JsonObject, bool> test) => offers.Count(test) / (double)offers.Count;

        // Icons: about 1 offered a run (live: 46). TOTW in-forms: about 3 offered a run (live: 20), a highlight, not a team.
        Assert.InRange(Share(item => item.Int("rareflag") == 12), 0.003, 0.025);
        Assert.InRange(Share(item => item.Int("rareflag") == 3), 0.015, 0.05);
        // Still a strong mode: the best card on a screen averages 86+, and base gold rares reach 88+.
        Assert.True(screens.Where(screen => screen.Count > 0).Average(screen => screen.Max(item => item.Int("rating"))) >= 86);
        Assert.Contains(offers, item => item.Int("rareflag") == 1 && item.Int("rating") >= 88);
    }


}
