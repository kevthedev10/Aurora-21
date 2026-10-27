using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// A player who leaves an online match (owner, 2026-10-03): Alt+F4 sends nothing from the leaver's client, so the match
/// end of the player who stayed settles the leaver's loss in the leaver's own club at once; the player who stayed gets
/// no result if losing (a loss from the 10th minute when the relay heard the opponent to the end), a win if level or
/// ahead from the 10th minute, and no result before it.
/// </summary>
public sealed class OpponentLeftTests
{
    private const string G = "/ut/game/fifa21";
    private const string LeaverId = "222222";

    /// <summary>Friday 2026-10-02 08:00 UTC: Weekend League 2961 is live.</summary>
    private const long DuringWeekendLeague = 1_790_928_000;

    private sealed class TestRoster : IMatchRoster
    {
        private readonly Dictionary<string, OnlineOpponent> _pairs = new(StringComparer.Ordinal);

        /// <summary>What the relay heard: per player, how long before its last datagram the opponent's last one came.</summary>
        public Dictionary<string, TimeSpan> Quiet { get; } = new(StringComparer.Ordinal);

        public void Pair(string home, long homePersona, string away, long awayPersona, long gameId)
        {
            _pairs[home] = new OnlineOpponent(true, away, awayPersona, gameId);
            _pairs[away] = new OnlineOpponent(false, home, homePersona, gameId);
        }

        public OnlineOpponent? OpponentOf(string playerId) => _pairs.GetValueOrDefault(playerId);

        public bool IsSearching(string playerId) => false;

        public TimeSpan? OpponentQuietFirstBy(string playerId, long gameId) =>
            _pairs.GetValueOrDefault(playerId)?.GameId == gameId && Quiet.TryGetValue(playerId, out var gap) ? gap : null;

        /// <summary>The players whose opponent left the live Blaze game before them.</summary>
        public HashSet<string> OpponentLeftFirst { get; } = new(StringComparer.Ordinal);

        public bool OpponentLeftGameFirst(string playerId, long gameId) =>
            _pairs.GetValueOrDefault(playerId)?.GameId == gameId && OpponentLeftFirst.Contains(playerId);
    }

    private static JsonObject Call(FutRouter router, string method, string path, JsonNode? body = null)
    {
        var response = router.Handle(new FutRequest(method, path, [], body is null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString())));
        Assert.True(response.Status == 200, $"{method} {path} -> {response.Status}: {Encoding.UTF8.GetString(response.Body)}");
        return FutJson.ParseObject(Encoding.UTF8.GetString(response.Body))!;
    }

    private static JsonObject End(string reason, long mine, long theirs, long secondsPlayed, long flags, long matchId) => new()
    {
        ["endReason"] = reason,
        ["gid"] = matchId,
        ["matchId"] = matchId,
        ["matchStatusFlags"] = flags,
        ["secondsPlayed"] = secondsPlayed,
        ["myMatchStats"] = new JsonObject { ["goals"] = mine },
        ["opponentMatchStats"] = new JsonObject { ["goals"] = theirs },
    };

    private static void Played(GameContext game, long seconds) =>
        game.InUnit(uow => uow.ExecuteAsync(
            "-- query: test.match-clock.played\nUPDATE ultimateteam.match_clock SET created_at = @now - @played, kickoff_at = @now - @played WHERE club_id = @club",
            ("now", uow.NowSeconds), ("played", seconds)));

    private static long Losses(GameContext game) =>
        game.InUnit(uow => uow.ScalarAsync<long>("SELECT coalesce((SELECT loss FROM ultimateteam.club_records WHERE club_id = @club), 0)"));

    /// <summary>Two paired clubs, each having created and kicked off the same online match.</summary>
    private sealed class Pairing : IDisposable
    {
        public required TestRuntime Stayer { get; init; }
        public required PlayerRegistry Players { get; init; }
        public required PlayerLease LeaverLease { get; init; }
        public required TestRoster Roster { get; init; }
        public PlayerSession Leaver => LeaverLease.Session;

        public static Pairing Start(JsonObject create, long realSeconds, long gameId)
        {
            var rt = TestRuntime.Create(time: new TestRuntime.MovableTime(DuringWeekendLeague));
            var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
            var lease = players.Acquire(PlayerIdentity.Of(LeaverId));
            var roster = new TestRoster();
            players.MatchRoster = roster;
            roster.Pair(PlayerIdentity.SinglePlayerId, rt.Game.Config.PersonaId, LeaverId, lease.Session.Game.Config.PersonaId, gameId);
            foreach (var (game, router) in new[] { (rt.Game, rt.Router), (lease.Session.Game, lease.Session.Router) })
            {
                game.InUnit(uow => uow.AddWeekendLeagueEntryAsync(2961, 0, []));
                Call(router, "POST", $"{G}/match", create);
                Call(router, "PUT", $"{G}/match", create);
                Played(game, realSeconds);
            }
            return new Pairing { Stayer = rt, Players = players, LeaverLease = lease, Roster = roster };
        }

        public void Dispose()
        {
            LeaverLease.Dispose();
            Players.Dispose();
            Stayer.Dispose();
        }
    }

    private static readonly JsonObject WeekendLeague = new() { ["squadId"] = 1, ["type"] = "ONLINE", ["championId"] = 1 };
    private static readonly JsonObject Rivals = new() { ["squadId"] = 1, ["matchSubType"] = "RIVALS_MATCH" };

    private static WeekendLeagueRecord Record(GameContext game) => game.InUnit(uow => uow.WeekendLeagueRecordAsync(2961));

    /// <summary>
    /// The 2026-10-03 00:40 Weekend League match: 1-0 up in the 39th minute after 335 real seconds, the opponent pressed
    /// Alt+F4. The player who stayed wins (it was voided as too short); the leaver, who sent nothing, loses at once.
    /// </summary>
    [Fact]
    public void AnAltF4InTheWeekendLeagueIsTheLeaversLossAndTheStayersWin()
    {
        using var match = Pairing.Start(WeekendLeague, 335, 17470624441795);
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", 1, 0, 2336, 4, 17470624441795));
        Assert.Equal((1L, match.Stayer.Game.Rules.Seasons.MatchCompletionRewards["weekendLeague"].Coins), (stayed.Int("won"), stayed.Int("participationAward")));
        Assert.Equal((1L, 1L, 0L), (Record(match.Stayer.Game).Games, Record(match.Stayer.Game).Wins, Record(match.Stayer.Game).Losses));

        // The leaver's club already shows the loss: a Weekend League game used, a club-record loss, its match closed.
        var leaver = match.Leaver.Game;
        Assert.Equal((1L, 0L, 1L), (Record(leaver).Games, Record(leaver).Wins, Record(leaver).Losses));
        Assert.Equal(1L, Losses(leaver));
        Assert.Null(leaver.InUnit(uow => uow.MatchClockAsync()));

        // Should its own end still arrive, it is a replay: nothing counts twice.
        Call(match.Leaver.Router, "POST", $"{G}/match/end", End("QUIT", 0, 1, 2336, 0, 17470624441795));
        Assert.Equal((1L, 1L), (Record(leaver).Games, Losses(leaver)));
    }

    /// <summary>
    /// The 2026-10-04 Weekend League match: 0-2 down in the 46th minute after 506 real seconds, the opponent's PC dropped.
    /// A hard drop carries no opponent-left bit, only the lost-connection bit (8) FIFA sets on both ends, so the stayer's
    /// DNF_LOSS was settled as its loss and the leaver had nothing. The relay heard the leaver go quiet 15 s before the
    /// stayer: the stayer, losing, gets no result; the leaver loses at once.
    /// </summary>
    [Fact]
    public void AHardDropTheRelayHeardIsTheLeaversLossAndNotTheStayers()
    {
        using var match = Pairing.Start(WeekendLeague, 506, 18038785095168);
        match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.FromSeconds(15);
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_LOSS", 0, 2, 2765, 8, 18038785095168));
        Assert.Equal((0L, 0L), (stayed.Int("won"), stayed.Int("loss")));
        Assert.Equal(0L, Record(match.Stayer.Game).Games);
        Assert.Equal(0L, Losses(match.Stayer.Game));

        var leaver = match.Leaver.Game;
        Assert.Equal((1L, 1L), (Record(leaver).Games, Record(leaver).Losses));
        Assert.Equal(1L, Losses(leaver));
        Assert.Null(leaver.InUnit(uow => uow.MatchClockAsync()));
    }

    /// <summary>
    /// Level in the 45th minute when the opponent's connection died (the 2026-10-04 12:00 match): the player who stayed
    /// gets the owner's rule, a win; the player whose connection failed takes the loss (owner, 2026-10-04).
    /// </summary>
    [Fact]
    public void ALevelHardDropPastTheWinMinuteIsTheStayersWin()
    {
        using var match = Pairing.Start(WeekendLeague, 600, 18038785029629);
        match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.FromSeconds(16);
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_DRAW", 1, 1, 2685, 8, 18038785029629));
        Assert.Equal(1L, stayed.Int("won"));
        Assert.Equal((1L, 1L), (Record(match.Stayer.Game).Games, Record(match.Stayer.Game).Wins));
        Assert.Equal((1L, 1L), (Record(match.Leaver.Game).Games, Record(match.Leaver.Game).Losses));
    }

    /// <summary>
    /// A lost connection the relay cannot pin on the opponent stays the caller's result: both kept sending to the end (only
    /// the link between them broke), the caller went quiet first, or the relay has no record. A losing player who cuts the
    /// game link gains nothing, and the opponent is not settled.
    /// </summary>
    [Theory]
    [InlineData(0.2)]
    [InlineData(4.9)]
    [InlineData(-15.0)]
    [InlineData(null)]
    public void ALostConnectionTheRelayDoesNotPinOnTheOpponentStaysTheCallersResult(double? quietSeconds)
    {
        using var match = Pairing.Start(WeekendLeague, 506, 4444);
        if (quietSeconds is { } seconds)
        {
            match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.FromSeconds(seconds);
        }
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_LOSS", 0, 2, 2765, 8, 4444));
        Assert.Equal(1L, stayed.Int("loss"));
        Assert.Equal((1L, 1L), (Record(match.Stayer.Game).Games, Record(match.Stayer.Game).Losses));
        Assert.Equal(0L, Record(match.Leaver.Game).Games);
        Assert.Equal(0L, Losses(match.Leaver.Game));
    }

    /// <summary>
    /// An alt-tab disconnect (live from 2026-10-08 17:40 UTC; game 298427461, Rivals 0-0 at 27', 237 real seconds): the
    /// alt-tabbed FIFA reports, leaves the live game and never sends its match end; both gameplay streams stop in the same
    /// instant (relay quiet 0.0 s) and the stayer's FIFA ends with a lost-connection DNF a second later. It was settled as
    /// too short and the alt-tabber had nothing. The opponent left the Blaze game first: level from the win minute is the
    /// stayer's win, and the leaver loses at once.
    /// </summary>
    [Fact]
    public void AnAltTabDisconnectIsTheAltTabbersLossAndTheStayersWin()
    {
        using var match = Pairing.Start(Rivals, 237, 19557742101248);
        match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.Zero;
        match.Roster.OpponentLeftFirst.Add(PlayerIdentity.SinglePlayerId);
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_DRAW", 0, 0, 1650, 2056, 19557742101248));
        Assert.Equal((1L, 0L), (stayed.Int("won"), stayed.Int("loss")));
        Assert.Equal(1L, Losses(match.Leaver.Game));
        Assert.Null(match.Leaver.Game.InUnit(uow => uow.MatchClockAsync()));
    }

    /// <summary>
    /// Losing when the opponent alt-tabbed out (game 19557758420266, 0-4 at 44'): no result for the stayer, not the score's
    /// loss the relay's "heard to the end" would give, and the alt-tabber still loses.
    /// </summary>
    [Fact]
    public void LosingWhenTheOpponentAltTabsIsNoResultAndTheirLoss()
    {
        using var match = Pairing.Start(Rivals, 477, 19557758420266);
        match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.FromSeconds(0.1);
        match.Roster.OpponentLeftFirst.Add(PlayerIdentity.SinglePlayerId);
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_LOSS", 0, 4, 2648, 2056, 19557758420266));
        Assert.Equal((0L, 0L), (stayed.Int("won"), stayed.Int("loss")));
        Assert.Equal(0L, Losses(match.Stayer.Game));
        Assert.Equal(1L, Losses(match.Leaver.Game));
    }

    /// <summary>
    /// Leaving the Blaze game first counts only on a lost-connection end: a mutual timeout (the opponent-timed-out bit, no
    /// lost-connection bit) still settles no leaver, whoever's report reached Blaze first.
    /// </summary>
    [Fact]
    public void LeavingTheGameFirstDoesNotTurnAMutualTimeoutIntoALeave()
    {
        using var match = Pairing.Start(Rivals, 600, 18731092656522);
        match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.FromSeconds(0.02);
        match.Roster.OpponentLeftFirst.Add(PlayerIdentity.SinglePlayerId);
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_DRAW", 1, 1, 809, 4, 18731092656522));
        Assert.Equal((0L, 0L), (stayed.Int("won"), stayed.Int("loss")));
        Assert.Equal(0L, Losses(match.Leaver.Game));
        Assert.NotNull(match.Leaver.Game.InUnit(uow => uow.MatchClockAsync()));
    }

    /// <summary>
    /// The 2026-10-06 Rivals match (mourad): level 1-1 at 13', both clients ended within 11 ms while the relay still heard
    /// both (quiet 0.3 s each), each end carrying the opponent-timed-out bit. Nobody left: neither player is given a QUIT,
    /// and a level mutual timeout is no result for either (both were paid a win until 2026-10-08).
    /// </summary>
    [Fact]
    public void ABrokenLinkBothSidesHeardToTheEndSettlesNoLeaver()
    {
        using var match = Pairing.Start(Rivals, 600, 18731092656521);
        match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.FromSeconds(0.02);
        match.Roster.Quiet[LeaverId] = TimeSpan.FromSeconds(-0.02);
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_DRAW", 1, 1, 809, 4, 18731092656521));
        Assert.Equal((0L, 0L, 0L), (stayed.Int("won"), stayed.Int("loss"), stayed.Int("earned")));
        var other = match.Leaver.Game;
        Assert.Equal(0L, Losses(other));
        Assert.NotNull(other.InUnit(uow => uow.MatchClockAsync()));

        var own = Call(match.Leaver.Router, "POST", $"{G}/match/end", End("DNF_DRAW", 1, 1, 809, 4, 18731092656521));
        Assert.Equal((0L, 0L, 0L), (own.Int("won"), own.Int("loss"), own.Int("earned")));
        Assert.Equal(0L, Losses(other));
    }

    /// <summary>
    /// The 2026-10-07 17:35 UTC Rivals match (mn1cr4m23 3-2 at 35'): one game stalled about 20 s (an alt-tab), FIFA ended
    /// both, each end carried the opponent-timed-out bit and the relay heard both to the end. The score's result stands
    /// (rating and club record) but neither end pays coins or the Match Pack (owner, 2026-10-08).
    /// </summary>
    [Fact]
    public void AMutualTimeoutKeepsTheScoresResultAndPaysNothing()
    {
        using var match = Pairing.Start(Rivals, 600, 19149202401879);
        match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.FromSeconds(-0.1);
        match.Roster.Quiet[LeaverId] = TimeSpan.FromSeconds(0.1);
        var leaderRating = match.Stayer.Game.InUnit(uow => match.Stayer.Fut.Rivals.ProgressAsync(uow)).SkillRating;
        var leader = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", 3, 2, 2100, 4, 19149202401879));
        Assert.Equal((1L, 0L, 0L), (leader.Int("won"), leader.Int("award"), leader.Int("earned")));
        Assert.True(match.Stayer.Game.InUnit(uow => match.Stayer.Fut.Rivals.ProgressAsync(uow)).SkillRating > leaderRating);
        Assert.Equal(0L, Losses(match.Leaver.Game));

        var trailer = Call(match.Leaver.Router, "POST", $"{G}/match/end", End("DNF_LOSS", 2, 3, 2100, 4, 19149202401879));
        Assert.Equal((0L, 1L, 0L), (trailer.Int("won"), trailer.Int("loss"), trailer.Int("earned")));
        Assert.Equal(1L, Losses(match.Leaver.Game));
    }

    [Fact]
    public void OnlyATimedOutEndTheRelayHeardOnBothSidesIsAMutualTimeout()
    {
        using var rt = TestRuntime.Create();
        var rules = rt.Game.Rules.Seasons.RewardEligibility;
        bool Mutual(string reason, long flags, double? quiet) => MatchEndFacts.From(
            new JsonObject { ["endReason"] = reason, ["matchStatusFlags"] = flags, ["secondsPlayed"] = 2100 }, 600, rules,
            quiet is { } seconds ? TimeSpan.FromSeconds(seconds) : null).MutualTimeout;
        Assert.True(Mutual("DNF_WIN", 4, 0.1));
        Assert.True(Mutual("DNF_LOSS", 4, -0.1));
        Assert.True(Mutual("DNF_DRAW", 4, 4.9));
        // An opponent who quit (2, with or without 4), went quiet first, or no relay record: an opponent who left.
        Assert.False(Mutual("DNF_WIN", 2 | 2048, 0.0));
        Assert.False(Mutual("DNF_WIN", 6, 0.0));
        Assert.False(Mutual("DNF_WIN", 4, 5));
        Assert.False(Mutual("DNF_WIN", 4, null));
        Assert.False(Mutual("DNF_WIN", 4 | 16, 0.0));
        Assert.False(Mutual("WIN", 4, 0.0));
        Assert.False(Mutual("QUIT", 4, 0.0));
    }

    [Fact]
    public void OnlyALostConnectionEndReadsTheRelay()
    {
        using var rt = TestRuntime.Create();
        var rules = rt.Game.Rules.Seasons.RewardEligibility;
        Assert.Equal((8L, 5.0), (rules.MatchStatusFlags.ConnectionLost, rules.OpponentLeft.RelayQuietSeconds));
        bool Left(string reason, long flags, double? quiet) => MatchEndFacts.From(
            new JsonObject { ["endReason"] = reason, ["matchStatusFlags"] = flags, ["secondsPlayed"] = 2700 }, 600, rules,
            quiet is { } seconds ? TimeSpan.FromSeconds(seconds) : null).OpponentLeft;
        Assert.True(Left("DNF_LOSS", 8, 5));
        Assert.True(Left("DNF_DRAW", 8, 15));
        Assert.False(Left("DNF_LOSS", 8, 4.9));
        Assert.False(Left("DNF_LOSS", 0, 15));
        Assert.False(Left("LOSS", 8, 15));
        Assert.False(Left("DNF", 8, 15));
        Assert.False(Left("DNF_LOSS", 8 | 16, 15));
    }

    [Theory]
    // Losing when the opponent left: no result, at any minute.
    [InlineData(0, 1, 2400, 600, "none")]
    // Level before the 10th minute: no result.
    [InlineData(0, 0, 540, 600, "none")]
    // Level or ahead from the 10th minute: a win, even short of the 8-minute minimum.
    [InlineData(0, 0, 2400, 335, "win")]
    [InlineData(2, 1, 5000, 900, "win")]
    [InlineData(0, 0, 600, 600, "win")]
    // The owner's 10-04 Weekend League game: 3-0 when the opponent quit at 34:07, 388 s after kickoff (no result under the
    // old 35th-minute rule).
    [InlineData(3, 0, 2047, 388, "win")]
    // Ahead before the 10th minute: no result.
    [InlineData(3, 0, 540, 600, "none")]
    // A claimed game clock beyond what the real time allows (7 s a minute) never reaches the 10th minute.
    [InlineData(0, 0, 5400, 60, "none")]
    public void ThePlayerWhoStaysGetsTheOwnersRuleAndTheLeaverAlwaysLoses(long mine, long theirs, long gameSeconds, long realSeconds, string expected)
    {
        using var match = Pairing.Start(Rivals, realSeconds, 99);
        var stayer = match.Stayer.Game;
        var stayerRating = stayer.InUnit(uow => match.Stayer.Fut.Rivals.ProgressAsync(uow)).SkillRating;
        var leaverRating = match.Leaver.Game.InUnit(uow => match.Leaver.Fut.Rivals.ProgressAsync(uow)).SkillRating;
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", mine, theirs, gameSeconds, 2, 99));

        Assert.Equal(expected == "win" ? 1L : 0L, stayed.Int("won"));
        Assert.Equal(expected == "win", stayed.ContainsKey("rivalsScore"));
        Assert.Equal(expected == "win", stayer.InUnit(uow => match.Stayer.Fut.Rivals.ProgressAsync(uow)).SkillRating > stayerRating);
        // Whatever the stayer gets, the leaver lost: rated down and in its club record.
        Assert.True(match.Leaver.Game.InUnit(uow => match.Leaver.Fut.Rivals.ProgressAsync(uow)).SkillRating < leaverRating);
        Assert.Equal(1L, Losses(match.Leaver.Game));
    }

    /// <summary>
    /// The 2026-10-06 Rivals match (toxicz.v2 0-3 down vs nf_636): the leader quit and the losing player's end, in the same
    /// second, carried the opponent-left bit, so the leader took the quit's loss and the player who stalled had no result.
    /// Losing from the 10th minute while the relay heard the opponent to the end is now the score's loss; an opponent the
    /// relay heard go quiet first, no relay record, or an end before the 10th minute stays no result.
    /// </summary>
    [Theory]
    [InlineData(0.0, 2400, 600, "loss")]
    [InlineData(-0.1, 2400, 600, "loss")]
    [InlineData(4.9, 2400, 600, "loss")]
    [InlineData(15.0, 2400, 600, "none")]
    [InlineData(null, 2400, 600, "none")]
    [InlineData(0.0, 540, 600, "none")]
    public void LosingWhenAnOpponentTheRelayHeardToTheEndLeftIsALoss(double? quietSeconds, long gameSeconds, long realSeconds, string expected)
    {
        using var match = Pairing.Start(Rivals, realSeconds, 18731095278073);
        if (quietSeconds is { } seconds)
        {
            match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.FromSeconds(seconds);
        }
        var stayer = match.Stayer.Game;
        var rating = stayer.InUnit(uow => match.Stayer.Fut.Rivals.ProgressAsync(uow)).SkillRating;
        var leader = Call(match.Leaver.Router, "POST", $"{G}/match/end", End("QUIT", 3, 0, gameSeconds, 0, 18731095278073));
        Assert.Equal(1L, leader.Int("loss"));

        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_LOSS", 0, 3, gameSeconds, 2, 18731095278073));
        var lost = expected == "loss";
        Assert.Equal((0L, lost ? 1L : 0L), (stayed.Int("won"), stayed.Int("loss")));
        Assert.Equal(lost ? 1L : 0L, Losses(stayer));
        Assert.Equal(lost, stayer.InUnit(uow => match.Stayer.Fut.Rivals.ProgressAsync(uow)).SkillRating < rating);
        // The leader's quit is its own loss, settled once.
        Assert.Equal(1L, Losses(match.Leaver.Game));
    }

    /// <summary>The losing Weekend League player whose opponent's game the relay heard to the end loses the game too.</summary>
    [Fact]
    public void LosingInTheWeekendLeagueWhenTheRelayHeardTheOpponentToTheEndIsALoss()
    {
        using var match = Pairing.Start(WeekendLeague, 600, 18994528394468);
        match.Roster.Quiet[PlayerIdentity.SinglePlayerId] = TimeSpan.FromSeconds(0.1);
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_LOSS", 0, 7, 4625, 4, 18994528394468));
        Assert.Equal(1L, stayed.Int("loss"));
        Assert.Equal((1L, 1L), (Record(match.Stayer.Game).Games, Record(match.Stayer.Game).Losses));
        // The relay heard the opponent until the end: it is not settled as a leaver.
        Assert.Equal(0L, Losses(match.Leaver.Game));
    }

    /// <summary>The item id in a club's squad-1 slot range (the XI below the starter count, the bench from it), made a loan.</summary>
    private static long MakeLoan(GameContext game, long games, bool starter)
    {
        var store = game.Store();
        var starters = game.Rules.Club.Squad.Starters;
        var id = (store.GetSquad(1)!.Arr("players") ?? new JsonArray()).OfType<JsonObject>()
            .Select(slot => (Index: slot.Int("index"), Id: JsonValues.Int((slot.Obj("itemData") ?? slot).GetOr("id", slot.Get("itemId")))))
            .Where(slot => slot.Id > 0 && (slot.Index < starters) == starter && store.GetItem(slot.Id)?.Text("itemType") == "player")
            .OrderBy(slot => slot.Index).First().Id;
        var loan = store.GetItem(id)!;
        loan["loans"] = games;
        loan["contract"] = 0;
        loan["contracts"] = 0;
        loan["untradeable"] = true;
        store.SaveItem(loan);
        return id;
    }

    private static long GamesPlayed(GameContext game, long id) =>
        game.Store().GetItem(id)!["statsList"]!.AsArray().OfType<JsonObject>().Single(row => row.Int("index") == 0).Int("value");

    /// <summary>
    /// The leaver's loss settled from the stayer's end uses a loan game exactly as its own quit would: its starting XI took
    /// part, an unused substitute did not (a real quit names the bench with the unused marker). Its own quit, should it still
    /// arrive, is a replay and uses nothing more.
    /// </summary>
    [Fact]
    public void TheLeaversSettledQuitUsesALoanGameOfItsStartingEleven()
    {
        using var match = Pairing.Start(Rivals, 600, 4242);
        var starter = MakeLoan(match.Leaver.Game, 5, starter: true);
        // A spare club player on the first bench slot (the seeded squad holds only its XI), then made a loan too.
        var leaverStore = match.Leaver.Game.Store();
        var squad = leaverStore.GetSquad(1)!;
        var inSquad = (squad.Arr("players") ?? new JsonArray()).OfType<JsonObject>()
            .Select(slot => JsonValues.Int((slot.Obj("itemData") ?? slot).GetOr("id", slot.Get("itemId")))).ToHashSet();
        var spare = leaverStore.ListItems("club").First(item => item.Text("itemType") == "player" && !inSquad.Contains(item.Int("id"))).Int("id");
        var benchIndex = match.Leaver.Game.Rules.Club.Squad.Starters;
        var players = (squad.Arr("players") ?? new JsonArray()).OfType<JsonObject>().Where(slot => slot.Int("index") != benchIndex)
            .Select(slot => (JsonNode?)slot.DeepClone()).ToList();
        players.Add(new JsonObject { ["index"] = benchIndex, ["itemData"] = new JsonObject { ["id"] = spare } });
        squad["players"] = new JsonArray(players.ToArray());
        Call(match.Leaver.Router, "PUT", $"{G}/squad/1", squad);
        var bench = MakeLoan(match.Leaver.Game, 5, starter: false);
        Assert.Equal(spare, bench);
        Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", 1, 0, 2400, 2, 4242));
        Assert.Equal(1L, Losses(match.Leaver.Game));
        Assert.Equal((4L, 5L), (match.Leaver.Game.Store().GetItem(starter)!.Int("loans"), match.Leaver.Game.Store().GetItem(bench)!.Int("loans")));

        var own = End("QUIT", 0, 1, 2400, 0, 4242);
        own["items"] = new JsonArray(new JsonObject { ["id"] = starter, ["fitness"] = 1 });
        Call(match.Leaver.Router, "POST", $"{G}/match/end", own);
        Assert.Equal(4L, match.Leaver.Game.Store().GetItem(starter)!.Int("loans"));
    }

    /// <summary>
    /// Only a match that settles to a result uses a loan game and adds a game to the card: the stayer's no-result ends
    /// (losing when the opponent left, level or ahead before the 10th minute) use and count nothing; a counted win does both.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 2400, 600, false)]
    [InlineData(0, 0, 540, 600, false)]
    [InlineData(3, 0, 540, 600, false)]
    [InlineData(0, 0, 2400, 335, true)]
    public void OnlyAnEndThatSettlesToAResultUsesTheStayersLoanGame(long mine, long theirs, long gameSeconds, long realSeconds, bool counts)
    {
        using var match = Pairing.Start(Rivals, realSeconds, 4343);
        var stayer = match.Stayer.Game;
        var loan = MakeLoan(stayer, 5, starter: true);
        var games = GamesPlayed(stayer, loan);
        var body = End("DNF_WIN", mine, theirs, gameSeconds, 2, 4343);
        body["items"] = new JsonArray(new JsonObject { ["id"] = loan, ["fitness"] = 1 });
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", body);
        Assert.Equal(counts ? 1L : 0L, stayed.Int("won"));
        Assert.Equal(counts ? 4L : 5L, stayer.Store().GetItem(loan)!.Int("loans"));
        Assert.Equal(games + (counts ? 1 : 0), GamesPlayed(stayer, loan));
    }

    [Fact]
    public void TheMatchMinuteIsTheGameClockCappedByRealTime()
    {
        using var rt = TestRuntime.Create();
        var rules = rt.Game.Rules.Seasons.RewardEligibility;
        Assert.Equal((10L, 7.0), (rules.OpponentLeft.WinFromMinute, rules.OpponentLeft.RealSecondsPerMinute));
        MatchEndFacts Facts(long gameSeconds, long? real) =>
            MatchEndFacts.From(new JsonObject { ["endReason"] = "DNF_WIN", ["matchStatusFlags"] = 4, ["secondsPlayed"] = gameSeconds }, real, rules);
        Assert.Equal((38L, true), (Facts(2336, 335).Minute, Facts(2336, 335).PastLeftWinMinute));
        Assert.Equal((14L, true), (Facts(5400, 100).Minute, Facts(5400, 100).PastLeftWinMinute));
        Assert.Equal((8L, false), (Facts(5400, 60).Minute, Facts(5400, 60).PastLeftWinMinute));
        Assert.Equal((9L, false), (Facts(599, 600).Minute, Facts(599, 600).PastLeftWinMinute));
        Assert.Equal((0L, false), (Facts(5400, null).Minute, Facts(5400, null).PastLeftWinMinute));
        var paid = RewardEligibility.Decide(rt.Game.Rules.Seasons, RewardModes.Rivals, Facts(2336, 335), MatchService.MatchOutcome.Win, false, false);
        Assert.Equal(RewardVerdict.Paid, paid.Verdict);
    }

    /// <summary>Early leaves already logged for the leaver, <paramref name="hoursAgo"/> before now, each against another stayer.</summary>
    private static void EarlierLeaves(GameContext game, int count, double hoursAgo, bool early = true)
    {
        for (var i = 0; i < count; i++)
        {
            game.InUnit(uow => uow.ExecuteAsync("""
                -- query: test.match-leaves.seed
                INSERT INTO ultimateteam.match_leaves(leaver_player, leaver_persona, stayer_player, stayer_club_id, mode, match_key, minute,
                    stayer_goals, leaver_goals, early, serial, stayer_outcome, left_at)
                VALUES (@leaver, NULL, @stayer, @club, 'rivals', @match, 4, 1, 0, @early, false, 'None', @at)
                """, ("leaver", LeaverId), ("stayer", $"5555{i}"), ("match", $"id:seed{i}"), ("early", early),
                ("at", uow.NowSeconds - (long)(hoursAgo * 3600))));
        }
    }

    private static (long Minute, bool Early, bool Serial, string Outcome) LoggedLeave(GameContext game, long matchId) =>
        game.InUnit(async uow => (await uow.QueryAsync("""
            SELECT minute, early, serial, stayer_outcome FROM ultimateteam.match_leaves WHERE leaver_player = @leaver AND match_key = @match
            """, row => ((long)row.GetInt32(0), row.GetBoolean(1), row.GetBoolean(2), row.GetString(3)), ("leaver", LeaverId), ("match", $"id:{matchId}"))).Single());

    /// <summary>
    /// A player who keeps leaving before the 10th minute denied every opponent a result (owner, 2026-10-08). With three early
    /// leaves in the last 24 hours, the next early leave counts as one past the win minute: the stayer, ahead in the 5th
    /// minute, wins and is paid; the leaver loses as always. Two earlier leaves, or three older than the window, are not
    /// enough: the stayer gets no result. Every leave is logged with its minute.
    /// </summary>
    [Theory]
    [InlineData(3, 2.0, true)]
    [InlineData(5, 20.0, true)]
    [InlineData(2, 2.0, false)]
    [InlineData(3, 30.0, false)]
    public void ASerialLeaversEarlyLeaveIsTheStayersWin(int earlier, double hoursAgo, bool serial)
    {
        using var match = Pairing.Start(Rivals, 100, 5151);
        EarlierLeaves(match.Stayer.Game, earlier, hoursAgo);
        var stayed = Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", 1, 0, 300, 4, 5151));
        Assert.Equal(serial ? 1L : 0L, stayed.Int("won"));
        Assert.Equal(serial ? match.Stayer.Game.Rules.Seasons.MatchCompletionRewards["rivals"].Coins : 0L, stayed.Int("participationAward"));
        Assert.Equal(1L, Losses(match.Leaver.Game));
        Assert.Equal((5L, true, serial, serial ? "Win" : "None"), LoggedLeave(match.Stayer.Game, 5151));
    }

    /// <summary>Penalties the leaver already received, <paramref name="hoursAgo"/> before now.</summary>
    private static void EarlierPenalties(GameContext game, int count, double hoursAgo, bool lifted = false)
    {
        for (var i = 0; i < count; i++)
        {
            game.InUnit(uow => uow.ExecuteAsync("""
                -- query: test.leaver-penalties.seed
                INSERT INTO ultimateteam.leaver_penalties(player, offence, match_key, started_at, until, lifted_at)
                VALUES (@player, @offence, @match, @at, @at + 3600, @lifted)
                """, ("player", LeaverId), ("offence", i + 1), ("match", $"id:penalty{i}"), ("at", uow.NowSeconds - (long)(hoursAgo * 3600) - i),
                ("lifted", lifted ? uow.NowSeconds - (long)(hoursAgo * 3600) + 60 : (long?)null)));
        }
    }

    private static (int Offence, long? Until)? LoggedPenalty(GameContext game, long matchId) =>
        game.InUnit(async uow => (await uow.QueryAsync("""
            SELECT offence, until FROM ultimateteam.leaver_penalties WHERE player = @player AND match_key = @match
            """, row => ((int Offence, long? Until)?)(row.GetInt32(0), row.IsDBNull(1) ? null : row.GetInt64(1)),
            ("player", LeaverId), ("match", $"id:{matchId}"))).SingleOrDefault());

    /// <summary>
    /// Leaver penalties (owner, 2026-10-09): a second early leave within the hour is an offence. The first bars online
    /// searches for an hour, the second for a day, the third is a ban until lifted. Lifted penalties and leaves before the
    /// last penalty do not count; one early leave, or two an hour apart, is no offence.
    /// </summary>
    [Theory]
    [InlineData(1, 0.5, 0, false, 1, 3600L)]
    [InlineData(1, 0.5, 1, false, 2, 86400L)]
    [InlineData(1, 0.5, 2, false, 3, null)]
    [InlineData(1, 0.5, 2, true, 1, 3600L)]
    [InlineData(0, 0.0, 0, false, null, null)]
    [InlineData(1, 1.5, 0, false, null, null)]
    public void RepeatEarlyLeavesEscalateToABan(int earlier, double hoursAgo, int penalties, bool lifted, int? offence, long? seconds)
    {
        using var match = Pairing.Start(Rivals, 100, 9191);
        var game = match.Stayer.Game;
        // Penalties from yesterday: the earlier leave this hour still counts.
        EarlierPenalties(game, penalties, 20, lifted);
        EarlierLeaves(game, earlier, hoursAgo);
        Call(match.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", 1, 0, 300, 4, 9191));
        var logged = LoggedPenalty(game, 9191);
        var barred = game.LeaverCooldown.Until(LeaverId, game.NowSeconds);
        if (offence is null)
        {
            Assert.Null(logged);
            Assert.Null(barred);
            return;
        }
        Assert.Equal(offence, logged?.Offence);
        if (seconds is null)
        {
            Assert.Null(logged?.Until);
            Assert.Equal(long.MaxValue, barred);
        }
        else
        {
            Assert.InRange(logged?.Until ?? 0, game.NowSeconds + seconds.Value - 5, game.NowSeconds + seconds.Value);
            Assert.Equal(logged?.Until, barred);
        }
    }

    /// <summary>A leave past the win minute is no offence, and leaves before the last penalty never count again.</summary>
    [Fact]
    public void OnlyFreshEarlyLeavesAreOffences()
    {
        using (var late = Pairing.Start(Rivals, 600, 9292))
        {
            EarlierLeaves(late.Stayer.Game, 1, 0.2);
            Call(late.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", 2, 0, 2400, 4, 9292));
            Assert.Null(LoggedPenalty(late.Stayer.Game, 9292));
        }
        using (var after = Pairing.Start(Rivals, 100, 9393))
        {
            EarlierLeaves(after.Stayer.Game, 1, 0.5);
            EarlierPenalties(after.Stayer.Game, 1, 0.2);
            Call(after.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", 1, 0, 300, 4, 9393));
            Assert.Null(LoggedPenalty(after.Stayer.Game, 9393));
        }
    }

    /// <summary>
    /// The serial rule never makes a losing stayer a winner, a minute-0 leave (a link that died at kickoff) neither counts
    /// nor is penalised, and a leave past the win minute is logged but is no early leave.
    /// </summary>
    [Fact]
    public void TheSerialRuleOnlyCountsRealEarlyLeaves()
    {
        using (var behind = Pairing.Start(Rivals, 100, 6161))
        {
            EarlierLeaves(behind.Stayer.Game, 3, 1);
            var stayed = Call(behind.Stayer.Router, "POST", $"{G}/match/end", End("DNF_LOSS", 0, 1, 300, 4, 6161));
            Assert.Equal((0L, 0L), (stayed.Int("won"), stayed.Int("loss")));
            Assert.Equal((5L, true, true, "None"), LoggedLeave(behind.Stayer.Game, 6161));
        }
        using (var kickoff = Pairing.Start(Rivals, 5, 7171))
        {
            EarlierLeaves(kickoff.Stayer.Game, 3, 1);
            var stayed = Call(kickoff.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", 0, 0, 20, 4, 7171));
            Assert.Equal(0L, stayed.Int("won"));
            Assert.Equal((0L, false, false, "None"), LoggedLeave(kickoff.Stayer.Game, 7171));
        }
        using (var late = Pairing.Start(Rivals, 600, 8181))
        {
            var stayed = Call(late.Stayer.Router, "POST", $"{G}/match/end", End("DNF_WIN", 2, 0, 2400, 4, 8181));
            Assert.Equal(1L, stayed.Int("won"));
            Assert.Equal((40L, false, false, "Win"), LoggedLeave(late.Stayer.Game, 8181));
        }
    }
}
