using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Data;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Each Draft request acts only in the step it belongs to (audit 10-07): autocomplete never revives a finished run, the
/// difficulty pick never rewinds a drafted one, a pick takes only its own step's offer, offers exist only for real slots,
/// the stats count the run's own losses and a claim on a run still going is a quit paid by its wins.
/// </summary>
public sealed class DraftFlowGuardTests
{
    private const string G = "/ut/game/fifa21";
    private const long DraftSquad = 900001;

    private static JsonObject State(TestRuntime rt) => rt.Call("GET", $"{G}/squad/mode/draft/state?mode=SINGLE_PLAYER")[0]!.AsObject();

    private static void Start(TestRuntime rt)
    {
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "PRO" });
    }

    /// <summary>Drafts the formation and the captain (into pitch slot 0): the run is then in the player draft.</summary>
    private static void DraftCaptain(TestRuntime rt)
    {
        rt.Call("GET", $"{G}/squad/mode/draft/choices/formation");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/captain");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });
        Assert.Equal("PLAYER_DRAFT", State(rt).Text("squadState"));
    }

    private static void Play(TestRuntime rt, string result)
    {
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = DraftSquad });
        rt.PlayedRealSeconds();
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = result, ["secondsPlayed"] = 5400 });
    }

    private static bool Filled(JsonObject state, int slot) => state["squad"]!["players"]![slot]!["itemData"] is JsonObject;

    [Fact]
    public void AutocompleteNeverRevivesAnEliminatedRun()
    {
        using var rt = TestRuntime.Create();
        Start(rt);
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        Play(rt, "LOSS");
        Assert.Equal("READY_FOR_REWARDS", State(rt).Text("squadState"));

        var reply = rt.CallObject("POST", $"{G}/squad/mode/1/draft/autocomplete");
        Assert.Equal("READY_FOR_REWARDS", reply.Text("squadState"));
        Assert.Equal("READY_FOR_REWARDS", State(rt).Text("squadState"));
        Assert.Equal(409, rt.CallRaw("POST", $"{G}/match", new JsonObject { ["squadId"] = DraftSquad }).Status);
    }

    [Fact]
    public void TheDifficultyPickNeverRewindsADraftedRun()
    {
        using var rt = TestRuntime.Create();
        Start(rt);
        // Before the formation it may still change.
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "AMATEUR" });
        Assert.Equal(("FORMATION_DRAFT", "AMATEUR"), (State(rt).Text("squadState"), State(rt)["roundsInfo"]![0]!.AsObject().Text("difficulty")));
        DraftCaptain(rt);

        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "BEGINNER" });
        var state = State(rt);
        Assert.Equal("PLAYER_DRAFT", state.Text("squadState"));
        Assert.All(state["roundsInfo"].Objects(), round => Assert.Equal("AMATEUR", round.Text("difficulty")));
    }

    [Fact]
    public void ACaptainOfferNeverFillsAPlayerSlot()
    {
        using var rt = TestRuntime.Create();
        Start(rt);
        DraftCaptain(rt);
        // The captain screen asked for again during the player draft, then confirmed on another slot.
        rt.Call("PUT", $"{G}/squad/mode/draft/choices/captain", new JsonObject { ["positionId"] = 5 });
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 1, ["positionId"] = 5 });
        Assert.False(Filled(State(rt), 5));

        // Its own offer still fills it.
        rt.Call("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 5 });
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 1, ["positionId"] = 5 });
        Assert.True(Filled(State(rt), 5));
    }

    [Fact]
    public void TheCaptainAlwaysStarts()
    {
        using var rt = TestRuntime.Create();
        Start(rt);
        rt.Call("GET", $"{G}/squad/mode/draft/choices/formation");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/captain");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 18 });
        var state = State(rt);
        Assert.False(Filled(state, 18));
        Assert.True(Filled(state, 10));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(23)]
    public void NoOfferForASlotTheSquadDoesNotHave(int positionId)
    {
        using var rt = TestRuntime.Create();
        Start(rt);
        DraftCaptain(rt);
        var offer = rt.CallObject("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = positionId });
        Assert.Empty(offer.Arr("choices")!);
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = positionId });
        var state = State(rt);
        // Only the captain is drafted.
        Assert.Equal(1, Enumerable.Range(0, 23).Count(slot => Filled(state, slot)));
    }

    [Fact]
    public void AChampionsStatsShowNoLoss()
    {
        using var rt = TestRuntime.Create();
        Start(rt);
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        for (var round = 0; round < 4; round++)
        {
            Play(rt, "WIN");
        }
        var stats = rt.CallObject("GET", $"{G}/draft/mode/1/stats");
        Assert.Equal((4L, 0L, 1L, 1L), (stats.Int("gamesWon"), stats.Int("gamesLost"), stats.Int("draftChampion"), stats.Int("draftsCompleted")));
    }

    [Fact]
    public void AClaimOnARunStillGoingIsAQuitPaidByItsWins()
    {
        using var rt = TestRuntime.Create();
        Start(rt);
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        Play(rt, "WIN");
        var run = rt.Game.Store().DraftRun()!;
        var payout = rt.Fut.Draft.RunPayout(run, DraftSlot.SinglePlayer);
        var credits = rt.Game.Store().Credits();
        var packs = rt.Game.Store().UnopenedPackIds().Count;

        var award = rt.Call("POST", $"{G}/squad/mode/1/draft/grant/award").AsArray();
        Assert.Equal(payout.Coins, award[0]!["value"]!.GetValue<long>());
        Assert.Equal(credits + payout.Coins, rt.Game.Store().Credits());
        Assert.Equal(packs + payout.Packs.Count, rt.Game.Store().UnopenedPackIds().Count);
        Assert.Equal("INVALID", State(rt).Text("squadState"));
    }
}
