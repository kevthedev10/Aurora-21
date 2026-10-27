using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// A single-player Draft match is played at the picked difficulty: the draft state's roundsInfo carries it. CardsDLL reads
/// roundsInfo[min(gamesWonCurrentMatch, n - 1)].difficulty before the match (0x180209b30) and plays Ultimate (6) when the
/// list is empty, which every offline Draft did while the state sent an empty list.
/// </summary>
public sealed class DraftDifficultyTests
{
    private const string G = "/ut/game/fifa21";
    private const long DraftSquad = 900001;

    // The entry parser's names (0x1802ad034); any other name is 6.
    private static readonly string[] ClientNames = { "BEGINNER", "AMATEUR", "SEMIPRO", "PRO", "WORLDCLASS", "LEGENDARY" };

    /// <summary>The DifficultyLevel FIFA sets for the next single-player Draft match from a draft-state reply.</summary>
    private static long ClientDifficulty(JsonNode state)
    {
        var entry = state[0]!.AsObject();
        var rounds = entry.Arr("roundsInfo")!;
        if (rounds.Count == 0)
        {
            return 6;
        }
        var index = (int)Math.Min(entry.Int("gamesWonCurrentMatch"), rounds.Count - 1);
        var name = rounds[index]!.AsObject().Text("difficulty");
        var known = Array.IndexOf(ClientNames, name);
        return known < 0 ? 6 : known;
    }

    private static JsonNode State(TestRuntime rt, string mode = "SINGLE_PLAYER") =>
        rt.Call("GET", $"{G}/squad/mode/draft/state?mode={mode}");

    private static void Purchase(TestRuntime rt, bool online = false) =>
        rt.Call("POST", $"{G}/purchase/mode/{(online ? 0 : 1)}/draft", new JsonObject { ["currency"] = "COINS" });

    private static void Pick(TestRuntime rt, string name) =>
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = name });

    [Fact]
    public void EveryOfferedDifficultyReachesTheMatchThroughFourRounds()
    {
        using var rt = TestRuntime.Create();
        Purchase(rt);
        var offered = rt.Call("GET", $"{G}/squad/mode/draft/choices/difficulty")["choices"].Objects().Select(c => c["difficultyName"].S()).ToList();
        Assert.Equal(7, offered.Count);
        foreach (var (name, level) in offered.Select((name, level) => (name, (long)level)))
        {
            Pick(rt, name);

            var state = State(rt);
            var rounds = state[0]!["roundsInfo"].Objects().ToList();
            Assert.Equal(new long[] { 1, 2, 3, 4 }, rounds.Select(r => r.Int("round")));
            Assert.All(rounds, r => Assert.Equal(name, r.Text("difficulty")));
            // Left out on purpose: the parser's defaults are what the empty list gave the match loader and the hub.
            Assert.All(rounds, r => Assert.Equal(new[] { "round", "difficulty" }, r.Select(p => p.Key)));
            Assert.Equal(level, ClientDifficulty(state));
        }
    }

    [Fact]
    public void ThePickStillAppliesAfterAWin()
    {
        using var rt = TestRuntime.Create();
        Purchase(rt);
        Pick(rt, "BEGINNER");
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        Assert.Equal(0, ClientDifficulty(State(rt)));

        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = DraftSquad });
        rt.PlayedRealSeconds();
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN", ["myMatchStats"] = new JsonObject { ["goals"] = 2 } });

        var state = State(rt);
        Assert.Equal(("READY_FOR_MATCH", 1L), (state[0]!["squadState"].S(), state[0]!["gamesWonCurrentMatch"].L()));
        Assert.Equal(4, state[0]!["roundsInfo"].A().Count);
        Assert.Equal(0, ClientDifficulty(state));
    }

    [Fact]
    public void AnAliasNamesTheClientsDifficulty()
    {
        using var rt = TestRuntime.Create();
        Purchase(rt);
        Pick(rt, "WORLD_CLASS");
        Assert.All(State(rt)[0]!["roundsInfo"].Objects(), r => Assert.Equal("WORLDCLASS", r.Text("difficulty")));
        Assert.Equal(4, ClientDifficulty(State(rt)));
    }

    [Fact]
    public void NoRoundsBeforeThePickWithoutARunOrOnline()
    {
        using var rt = TestRuntime.Create();
        Assert.Empty(State(rt)[0]!["roundsInfo"].A());

        Purchase(rt);
        var picking = State(rt);
        Assert.Equal("PICK_DIFFICULTY", picking[0]!["squadState"].S());
        Assert.Empty(picking[0]!["roundsInfo"].A());

        Purchase(rt, online: true);
        rt.Call("POST", $"{G}/squad/mode/0/draft/autocomplete");
        var online = State(rt, "ONLINE");
        Assert.Equal("READY_FOR_MATCH", online[0]!["squadState"].S());
        Assert.Empty(online[0]!["roundsInfo"].A());
    }
}
