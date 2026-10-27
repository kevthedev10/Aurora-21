using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The reward-eligibility gate (PLAN 4.3; rules/seasons.json rewardEligibility): only Rivals, the Weekend League, Squad
/// Battles and Draft pay; a paid result needs the minimum length; the quitter gets nothing, the player who stays wins; a
/// desync is void with consolation coins; completion rewards are independent of difficulty; the pair caps stop
/// collusion.
/// </summary>
public sealed class EligibilityTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Tuesday 2026-09-29 12:00 UTC: Squad Battles week 2960, no Weekend League live.</summary>
    private const long Tuesday = 1_790_683_200;

    /// <summary>Friday 2026-10-02 08:00 UTC: Weekend League 2961 is live.</summary>
    private const long DuringWeekendLeague = 1_790_928_000;

    /// <summary>FIFA's game clock at full time (09-24 captures: 5,461-5,621).</summary>
    private const long FullTime = 5461;

    private const long Opponent = 1_234_567;

    private static TestRuntime Create(long now) => TestRuntime.Create(time: new TestRuntime.MovableTime(now));

    private static TestRuntime Create(TestRuntime.MovableTime clock) => TestRuntime.Create(time: clock);

    private static JsonObject End(TestRuntime rt, long matchId, string reason, long mine, long theirs, long secondsPlayed, long flags = 0) =>
        rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = reason,
            ["matchId"] = matchId,
            ["matchStatusFlags"] = flags,
            ["secondsPlayed"] = secondsPlayed,
            ["myMatchStats"] = new JsonObject { ["goals"] = mine },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = theirs },
        });

    private static void SquadBattles(TestRuntime rt, long slot, long difficulty) =>
        rt.Call("POST", $"{G}/match", new JsonObject { ["sqbtEventId"] = 2960, ["sqbtOpponentSquadId"] = slot, ["sqbtMatchDifficulty"] = difficulty });

    private static void Pair(TestRuntime rt, long gameId, long opponent = Opponent) =>
        rt.Game.InUnit(uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", opponent, gameId), 1000));

    private static int CompletionPacks(TestRuntime rt) => rt.Game.Store().UnopenedPackIds().Count(id => rt.Game.Rules.Seasons.MatchCompletionRewards.Values.Any(reward => reward.PackId == id));

    private static long Matches(TestRuntime rt) => rt.Game.Store().Objectives().Stat("matches");

    private static long Coins(TestRuntime rt, string mode = RewardModes.Rivals) => rt.Game.Rules.Seasons.MatchCompletionRewards[mode].Coins;

    /// <summary>Turns on the PLAN 4.3 pair caps for this runtime (the shipped rules have none, owner 2026-09-29).</summary>
    private static TestRuntime CappingPairs(TestRuntime rt)
    {
        Assert.Empty(rt.Game.Rules.Seasons.RewardEligibility.PairCaps);
        rt.Game.Rules.Seasons.RewardEligibility.PairCaps.AddRange(new[]
        {
            new PairCapRules { Modes = [RewardModes.Rivals], Games = 3, WindowSeconds = 86_400 },
            new PairCapRules { Modes = [RewardModes.DraftOnline], Games = 3, WindowSeconds = 86_400 },
            new PairCapRules { Modes = [RewardModes.WeekendLeague], Games = 2, PerWeekendLeague = true },
        });
        return rt;
    }

    [Fact]
    public void TheMatchCoinsAreTheEconomyV2Values()
    {
        using var rt = TestRuntime.Create();
        var rewards = rt.Game.Rules.Seasons.MatchCompletionRewards;
        Assert.Equal((2500L, 3000L, 2500L, 1500L, 1500L), (rewards["rivals"].Coins, rewards["weekendLeague"].Coins,
            rewards["draftOnline"].Coins, rewards["draftSinglePlayer"].Coins, rewards["squadBattles"].Coins));
        Assert.All(rewards.Values, reward => Assert.True(reward.Untradeable));
        Assert.Equal(75, rewards["rivals"].PackId);
        Assert.Equal(74, rewards["squadBattles"].PackId);
        var eligibility = rt.Game.Rules.Seasons.RewardEligibility;
        Assert.Equal((480L, 3600L, 1000L), (eligibility.MinRealSeconds, eligibility.MinGameSeconds, eligibility.DesyncCoins));
    }

    [Fact]
    public void AFriendlyPaysNothingAndCountsNoProgress()
    {
        using var rt = Create(Tuesday);
        var credits = rt.Game.Store().Credits();

        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 });
        var ended = End(rt, 1, "WIN", 3, 0, FullTime);

        Assert.Equal((0L, 0L), (ended.Int("matchCoins"), ended.Int("participationAward")));
        Assert.Empty(ended["matchCoinPartials"]!.AsArray());
        Assert.Equal(credits, rt.Game.Store().Credits());
        Assert.Equal((0L, 0L), (Matches(rt), rt.Game.Store().Objectives().UserXp));
        Assert.Equal(0, CompletionPacks(rt));
        // It is still a result: the club record counts it.
        Assert.Equal(1, ended.Int("won"));
    }

    [Fact]
    public void APaidResultNeedsTheMinimumRealTimeSinceKickoff()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = Create(clock);
        var credits = rt.Game.Store().Credits();

        // 30 in-game minutes, straight after the CreateMatch: too short, nothing at all.
        SquadBattles(rt, 0, 3);
        var early = End(rt, 1, "WIN", 1, 0, 1800);
        Assert.Equal(0, early.Int("matchCoins"));
        Assert.Equal((credits, 0L, 0), (rt.Game.Store().Credits(), Matches(rt), CompletionPacks(rt)));

        // The real clock runs from kickoff (the first PUT after the CreateMatch): 400 s after it is still too short.
        SquadBattles(rt, 1, 3);
        clock.Now += 400;
        rt.Call("PUT", $"{G}/match", new JsonObject { ["gid"] = 2, ["matchId"] = 2 });
        clock.Now += 400;
        Assert.Equal(0, End(rt, 2, "WIN", 1, 0, 1800).Int("matchCoins"));

        // Eight real minutes pay, whatever the game clock says.
        SquadBattles(rt, 2, 3);
        clock.Now += 480;
        var real = End(rt, 3, "WIN", 1, 0, 1800);
        Assert.Equal(Coins(rt, RewardModes.SquadBattles), real.Int("matchCoins"));

        // A claimed game clock pays nothing: secondsPlayed is a client claim, so an end straight after the CreateMatch
        // is too short however long it says the match ran (the farmed-report hole this gate closed).
        SquadBattles(rt, 3, 3);
        Assert.Equal(0, End(rt, 4, "WIN", 1, 0, 3600).Int("matchCoins"));
        SquadBattles(rt, 4, 3);
        Assert.Equal(0, End(rt, 5, "LOSS", 0, 1, FullTime).Int("matchCoins"));
        Assert.Equal(credits + real.Int("matchCoins"), rt.Game.Store().Credits());
        Assert.Equal((1L, 1), (Matches(rt), CompletionPacks(rt)));
    }

    [Fact]
    public void TheQuitterLosesAndIsPaidNothing()
    {
        using var rt = Create(Tuesday);
        var credits = rt.Game.Store().Credits();

        Pair(rt, 11);
        var quit = End(rt, 11, "QUIT", 2, 0, 4568);

        Assert.Equal((1L, 0L, 1L), (quit.Int("loss"), quit.Int("matchCoins"), quit.Int("gamesPlayed")));
        // A Rivals loss all the same: rated and in the club record.
        Assert.True(quit.ContainsKey("rivalsScore"));
        Assert.Equal(1, rt.Game.InUnit(uow => uow.RivalsRecordAsync())!.Losses);
        Assert.Equal((credits, 0L, 0), (rt.Game.Store().Credits(), Matches(rt), CompletionPacks(rt)));

        // A disconnect from a game (DNF with a game id) is the same.
        Pair(rt, 12);
        var dropped = End(rt, 12, "DNF", 0, 0, 4568);
        Assert.Equal((1L, 0L), (dropped.Int("loss"), dropped.Int("matchCoins")));
        Assert.Equal(credits, rt.Game.Store().Credits());
    }

    [Fact]
    public void ThePlayerRemovedForInactivityLosesAndIsPaidNothing()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = Create(clock);
        var credits = rt.Game.Store().Credits();

        // FIFA sends DNF_AFK with no match stats: read by its goals it was a 0-0 draw, paid in full (club 59 losing 1-5,
        // 2026-10-05). It is a quit, past the minimum all the same.
        Pair(rt, 13);
        clock.Now += 480;
        var afk = rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "DNF_AFK",
            ["matchId"] = 13,
            ["matchStatusFlags"] = 0,
            ["secondsPlayed"] = 4568,
        });

        Assert.Equal((0L, 0L, 1L, 0L), (afk.Int("won"), afk.Int("draw"), afk.Int("loss"), afk.Int("matchCoins")));
        Assert.Equal(1, rt.Game.InUnit(uow => uow.RivalsRecordAsync())!.Losses);
        Assert.Equal((credits, 0L, 0), (rt.Game.Store().Credits(), Matches(rt), CompletionPacks(rt)));
    }

    [Fact]
    public void ThePlayerWhoStaysWinsPastTheMinimumAndHasANoContestBeforeIt()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = Create(clock);
        var credits = rt.Game.Store().Credits();

        // The opponent quit (matchStatusFlags 2) at 0-0 after 76 minutes of a match the server's clock also
        // watched past the minimum: a win, full coins and a Match Pack.
        Pair(rt, 21);
        clock.Now += 480;
        var stayed = End(rt, 21, "DNF_DRAW", 0, 0, 4568, flags: 2);
        Assert.Equal((1L, 0L, Coins(rt)), (stayed.Int("won"), stayed.Int("draw"), stayed.Int("matchCoins")));
        Assert.True(stayed.ContainsKey("rivalsScore"));
        Assert.Equal((1L, 1), (Matches(rt), CompletionPacks(rt)));

        // A DNF_WIN after 15 in-game minutes and seconds of real time: no contest, nothing for either side, not rated.
        Pair(rt, 22);
        var early = End(rt, 22, "DNF_WIN", 3, 0, 900);
        Assert.Equal((0L, 0L, 0L), (early.Int("won"), early.Int("gamesPlayed"), early.Int("matchCoins")));
        Assert.False(early.ContainsKey("rivalsScore"));
        Assert.Equal((credits + Coins(rt), 1L, 1), (rt.Game.Store().Credits(), Matches(rt), CompletionPacks(rt)));
    }

    [Fact]
    public void ADesyncIsVoidAndPaysTheConsolationPastTheMinimum()
    {
        using var rt = Create(DuringWeekendLeague);
        rt.Game.InUnit(uow => uow.AddWeekendLeagueEntryAsync(2961, 0, []));
        var credits = rt.Game.Store().Credits();
        var desyncCoins = rt.Game.Rules.Seasons.RewardEligibility.DesyncCoins;

        // Rivals: not rated, no result, the desync coins, no Match Pack, XP or objective progress.
        Pair(rt, 31);
        rt.PlayedRealSeconds();
        var rivals = End(rt, 31, "DNF_DRAW", 0, 0, 4000, flags: 16);
        Assert.Equal((desyncCoins, 0L, 0L), (rivals.Int("matchCoins"), rivals.Int("gamesPlayed"), rivals.Int("draw")));
        Assert.False(rivals.ContainsKey("rivalsScore"));

        // The Weekend League: it uses no game.
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["type"] = "ONLINE", ["championId"] = 1 });
        rt.PlayedRealSeconds();
        var league = End(rt, 32, "DNF_DRAW", 0, 0, 4000, flags: 16);
        Assert.Equal(desyncCoins, league.Int("matchCoins"));
        Assert.Equal(0, rt.CallObject("GET", $"{G}/champion/user/stats").Int("gamesPlayed"));

        // Before the minimum a desync pays nothing.
        Pair(rt, 33);
        Assert.Equal(0, End(rt, 33, "DNF_DRAW", 0, 0, 100, flags: 16).Int("matchCoins"));

        Assert.Equal(credits + 2 * desyncCoins, rt.Game.Store().Credits());
        Assert.Equal((0L, 0), (Matches(rt), CompletionPacks(rt)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public void SquadBattlesCompletionRewardsAndProgressIgnoreDifficulty(long difficulty)
    {
        using var rt = Create(Tuesday);
        var credits = rt.Game.Store().Credits();

        SquadBattles(rt, 0, difficulty);
        rt.PlayedRealSeconds();
        var ended = End(rt, 1, "WIN", 2, 0, FullTime);

        var award = Coins(rt, RewardModes.SquadBattles);
        Assert.Equal((award, award), (ended.Int("participationAward"), ended.Int("matchCoins")));
        Assert.Empty(ended["matchCoinPartials"]!.AsArray());
        Assert.Equal(credits + award, rt.Game.Store().Credits());
        Assert.Equal((1L, 1), (Matches(rt), CompletionPacks(rt)));
    }

    [Theory]
    [InlineData("BEGINNER")]
    [InlineData("PRO")]
    public void SinglePlayerDraftCompletionRewardsAndProgressIgnoreDifficulty(string difficulty)
    {
        using var rt = Create(Tuesday);
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = difficulty });
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        rt.PlayedRealSeconds();
        var credits = rt.Game.Store().Credits();

        var ended = End(rt, 1, "WIN", 1, 0, FullTime);

        var award = Coins(rt, RewardModes.DraftSinglePlayer);
        Assert.Equal((award, award), (ended.Int("participationAward"), ended.Int("matchCoins")));
        Assert.Equal(credits + award, rt.Game.Store().Credits());
        Assert.Equal((1L, 1), (Matches(rt), CompletionPacks(rt)));
        Assert.Equal(1, rt.Game.Store().DraftRun()!.Int("wins"));
    }

    [Fact]
    public void WithoutPairCapsEveryGameAgainstOneOpponentPays()
    {
        using var rt = Create(Tuesday);
        for (var game = 1; game <= 5; game++)
        {
            Pair(rt, game);
            rt.PlayedRealSeconds();
            Assert.Equal(Coins(rt), End(rt, game, "WIN", 1, 0, FullTime).Int("matchCoins"));
        }
        Assert.Equal(5L, Matches(rt));
    }

    [Fact]
    public void RivalsStopsPayingAfterThreeRewardedGamesAgainstOnePairADay()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = CappingPairs(Create(clock));
        var credits = rt.Game.Store().Credits();
        for (var game = 1; game <= 3; game++)
        {
            Pair(rt, game);
            rt.PlayedRealSeconds();
            Assert.Equal(Coins(rt), End(rt, game, "WIN", 1, 0, FullTime).Int("matchCoins"));
        }
        var rated = rt.Game.InUnit(uow => uow.RivalsRecordAsync())!.MatchesPlayed;

        // The fourth against the same opponent that day is flagged: no coins, pack or progress, not rated, no result.
        Pair(rt, 4);
        rt.PlayedRealSeconds();
        var capped = End(rt, 4, "WIN", 1, 0, FullTime);
        Assert.Equal((0L, 0L, 0L), (capped.Int("matchCoins"), capped.Int("won"), capped.Int("gamesPlayed")));
        Assert.Equal(rated, rt.Game.InUnit(uow => uow.RivalsRecordAsync())!.MatchesPlayed);
        Assert.Equal((credits + 3 * (Coins(rt)), 3L, 3), (rt.Game.Store().Credits(), Matches(rt), CompletionPacks(rt)));
        Assert.Equal(1, rt.Game.InUnit(uow => uow.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.match_rewards WHERE club_id=@club AND verdict='PairCapped'")));

        // Another opponent still pays, and so does the same one a day later.
        Pair(rt, 5, opponent: 7_654_321);
        rt.PlayedRealSeconds();
        Assert.Equal(Coins(rt), End(rt, 5, "WIN", 1, 0, FullTime).Int("matchCoins"));
        clock.Now += 86_400;
        Pair(rt, 6);
        rt.PlayedRealSeconds();
        Assert.Equal(Coins(rt), End(rt, 6, "WIN", 1, 0, FullTime).Int("matchCoins"));
    }

    [Fact]
    public void TheWeekendLeagueCountsTwoGamesPerPairPerWeekend()
    {
        using var rt = CappingPairs(Create(DuringWeekendLeague));
        rt.Game.InUnit(uow => uow.AddWeekendLeagueEntryAsync(2961, 0, []));
        JsonObject Play(long gameId, long opponent = Opponent)
        {
            rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["type"] = "ONLINE", ["championId"] = 1 });
            Pair(rt, gameId, opponent);
            rt.PlayedRealSeconds();
            return End(rt, gameId, "WIN", 2, 1, FullTime);
        }

        Assert.Equal(1, Play(41).Int("won"));
        Assert.Equal(1, Play(42).Int("won"));
        var third = Play(43);
        Assert.Equal((0L, 0L), (third.Int("won"), third.Int("matchCoins")));
        Assert.Equal(2, rt.CallObject("GET", $"{G}/champion/user/stats").Int("gamesPlayed"));
        // Another opponent counts.
        Assert.Equal(1, Play(44, opponent: 7_654_321).Int("won"));
        Assert.Equal(3, rt.CallObject("GET", $"{G}/champion/user/stats").Int("gamesPlayed"));
    }

    [Fact]
    public void TheGateReadsFifasEndReasonsAndFlags()
    {
        using var rt = TestRuntime.Create();
        var rules = rt.Game.Rules.Seasons.RewardEligibility;
        var seasons = rt.Game.Rules.Seasons;
        MatchEndFacts Facts(string reason, long flags, long seconds, long? real = null) =>
            MatchEndFacts.From(new JsonObject { ["endReason"] = reason, ["matchStatusFlags"] = flags, ["secondsPlayed"] = seconds }, real, rules);

        Assert.True(Facts("QUIT", 1, FullTime).Quitter);
        Assert.True(Facts("DNF", 0, FullTime).Quitter);
        Assert.True(Facts("DNF_WIN", 2050, FullTime).OpponentLeft);
        Assert.True(Facts("DNF_LOSS", 4, FullTime).OpponentLeft);
        Assert.False(Facts("DNF_DRAW", 8, FullTime).OpponentLeft);
        Assert.True(Facts("DNF_DRAW", 16, FullTime).Desync);
        Assert.False(Facts("QUIT", 16, FullTime).Desync);
        Assert.False(Facts("WIN", 0, 3599).LongEnough);
        Assert.True(Facts("WIN", 0, 3599, real: 480).LongEnough);
        // The claimed game clock never decides: not alone, and not in place of missing or short real time.
        Assert.False(Facts("WIN", 0, FullTime).LongEnough);
        Assert.False(Facts("WIN", 0, FullTime, real: 479).LongEnough);

        RewardDecision Decide(string? mode, MatchEndFacts facts, long difficulty = 3, bool capped = false, bool restricted = false) =>
            RewardEligibility.Decide(seasons, mode, facts, MatchService_Win, capped, restricted);
        Assert.Equal(RewardVerdict.Friendly, Decide(null, Facts("WIN", 0, FullTime)).Verdict);
        Assert.Equal(RewardVerdict.RestrictedSquad, Decide(RewardModes.Rivals, Facts("WIN", 0, FullTime), restricted: true).Verdict);
        Assert.Equal(RewardVerdict.PairCapped, Decide(RewardModes.Rivals, Facts("WIN", 0, FullTime), capped: true).Verdict);
        Assert.Equal(RewardVerdict.Quitter, Decide(RewardModes.Rivals, Facts("QUIT", 1, FullTime)).Verdict);
        var desync = Decide(RewardModes.WeekendLeague, Facts("DNF_DRAW", 16, FullTime, real: 480));
        Assert.Equal((RewardVerdict.DesyncVoid, 1000L, false), (desync.Verdict, desync.FlatCoins, desync.GrantsPack));
        Assert.Equal(RewardVerdict.TooShort, Decide(RewardModes.Rivals, Facts("DNF_WIN", 2, 900)).Verdict);
        var beginner = Decide(RewardModes.SquadBattles, Facts("WIN", 0, FullTime, real: 480), difficulty: 0);
        Assert.Equal((RewardVerdict.Paid, true, true), (beginner.Verdict, beginner.CountsProgress, beginner.GrantsPack));
        var rivals = Decide(RewardModes.Rivals, Facts("WIN", 0, FullTime, real: 480), difficulty: 0);
        Assert.Equal((true, true), (rivals.CountsProgress, rivals.GrantsPack));
    }

    private const MatchService.MatchOutcome MatchService_Win = MatchService.MatchOutcome.Win;
}
