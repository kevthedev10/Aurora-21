using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Every club squad document carries <c>rating</c> and <c>squadType</c>, and a squad the client never saved is rated from its
/// starting XI (production Python server, SQUAD_MATCH_PARITY's SQUAD_FIELDS and SQUAD_IDENTITY_RATE_UNSAVED).
/// </summary>
public sealed class SquadFieldsTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public void TheOverallAddsTheAboveAverageExcessAndNeedsAFullEleven()
    {
        Assert.Equal(84, SquadDocuments.Overall(Enumerable.Repeat(84L, 11), 11));
        // 960 in total, average 87.27: ten players 2.73 above it add 27.27, and floor(987.27 / 11) is 89.
        Assert.Equal(89, SquadDocuments.Overall(Enumerable.Repeat(90L, 10).Append(60), 11));
        Assert.Equal(0, SquadDocuments.Overall(Enumerable.Repeat(90L, 10), 11));
        // An unrated slot is not a starter.
        Assert.Equal(0, SquadDocuments.Overall(Enumerable.Repeat(90L, 10).Append(0), 11));
        Assert.Equal(99, SquadDocuments.Overall(Enumerable.Repeat(99L, 10).Append(250), 11));
    }

    [Fact]
    public void AnUnsavedSquadIsRatedFromItsStartingEleven()
    {
        using var rt = TestRuntime.Create();
        var stored = rt.Game.InUnit(uow => uow.GetSquadAsync(1))!;
        Assert.Equal((0L, 0L), (stored.Int("rating"), stored.Int("starRating")));

        var squad = rt.CallObject("GET", $"{G}/squad/active");
        var starters = squad["players"].Objects().Take(11).Select(slot => slot["itemData"]!.AsObject().Int("rating")).ToList();
        var overall = SquadDocuments.Overall(starters, 11);
        Assert.InRange(overall, starters.Min(), starters.Max());
        Assert.Equal((overall, overall, "REGULAR_SQUAD"), (squad.Int("starRating"), squad.Int("rating"), squad.Text("squadType")));
        // The two fields follow starRating, as in the client's own squad save.
        var keys = squad.Select(entry => entry.Key).ToList();
        Assert.Equal((keys.IndexOf("starRating") + 1, keys.IndexOf("starRating") + 2), (keys.IndexOf("rating"), keys.IndexOf("squadType")));

        // Every club render path carries them, the streamed hub squad included, and the squad list rates it alike.
        Assert.Equal(overall, rt.CallObject("GET", $"{G}/squad/1").Int("rating"));
        var hub = rt.CallObject("GET", $"{G}/hub")["squad"]!.AsObject();
        Assert.Equal((overall, "REGULAR_SQUAD"), (hub.Int("rating"), hub.Text("squadType")));
        Assert.Equal(overall, rt.CallObject("GET", $"{G}/usermassinfo")["squad"]!.AsObject().Int("starRating"));
        Assert.Equal(overall, rt.CallObject("GET", $"{G}/squad/list")["squad"]![0]!.AsObject().Int("rating"));
    }

    [Fact]
    public void ASavedSquadKeepsTheNumbersTheClientSent()
    {
        using var rt = TestRuntime.Create();
        var saved = rt.Game.InUnit(uow => uow.GetSquadAsync(1))!;
        saved["rating"] = 96;
        saved["starRating"] = 95;
        saved["squadType"] = "REGULAR_SQUAD";
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/squad/1", saved).Status);

        var squad = rt.CallObject("GET", $"{G}/squad/active");
        Assert.Equal((95L, 96L), (squad.Int("starRating"), squad.Int("rating")));
        Assert.Equal(96, rt.CallObject("GET", $"{G}/squad/list")["squad"]![0]!.AsObject().Int("rating"));

        // A squad saved with a star rating only reports it as its rating too (a save keeps stored fields it leaves out).
        saved["rating"] = 0;
        saved["starRating"] = 91;
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/squad/1", saved).Status);
        Assert.Equal(91, rt.CallObject("GET", $"{G}/squad/active").Int("rating"));
    }

    [Fact]
    public void AnEmptyUnsavedSquadKeepsTheDefaults()
    {
        using var rt = TestRuntime.Create();
        // A new squad has no eleven to rate: the default star rating stands.
        var created = rt.CallObject("POST", $"{G}/squad", new JsonObject { ["squadName"] = "Empty" }).Int("id");
        var squad = rt.CallObject("GET", $"{G}/squad/{created}");
        var defaults = rt.Game.Rules.Club.Squad;
        Assert.Equal((defaults.DefaultStarRating, defaults.DefaultStarRating, "REGULAR_SQUAD"), (squad.Int("starRating"), squad.Int("rating"), squad.Text("squadType")));
    }
}
