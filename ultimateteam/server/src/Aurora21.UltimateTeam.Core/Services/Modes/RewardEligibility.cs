using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>Why a match end pays what it pays (logged, and kept in match_rewards).</summary>
internal enum RewardVerdict
{
    /// <summary>An eligible result: its coins, and XP, objective stats and a Match Pack where they apply.</summary>
    Paid,

    /// <summary>A mode that never pays (friendlies and anything unrecognised).</summary>
    Friendly,

    /// <summary>Called off (NO_CONTEST), or a result the mode voids (an undecided level Weekend League end).</summary>
    NoContest,

    /// <summary>Ended before the minimum length by anything but the player's own quit.</summary>
    TooShort,

    /// <summary>The player quit or disconnected: a loss that pays nothing (B26).</summary>
    Quitter,

    /// <summary>A desync: void, with the consolation coins once past the minimum.</summary>
    DesyncVoid,

    /// <summary>Both games timed each other out while the relay heard both: the result stands, nothing is paid.</summary>
    MutualTimeout,

    /// <summary>Beyond the rewarded games allowed against this opponent: flagged, counts nothing.</summary>
    PairCapped,

    /// <summary>The squad held a Pro, custom or grant-only card.</summary>
    RestrictedSquad,
}

/// <summary>What a match-end body says about how the match ended, read against rules/seasons.json rewardEligibility.</summary>
internal sealed record MatchEndFacts(string EndReason, long Flags, long GameSeconds, long? RealSeconds, bool LongEnough, bool Quitter, bool Desync,
    bool OpponentLeft, long Minute = 0, bool PastLeftWinMinute = false, bool OpponentDropped = false, bool OpponentHeardToEnd = false,
    bool MutualTimeout = false, bool SerialLeaver = false)
{
    /// <summary>
    /// The opponent's leave counts for the stayer: past the win minute, or an early leave by a serial leaver
    /// (<see cref="OpponentLeftRules.SerialLeaver"/>, set by <see cref="MatchService.NoteSerialLeaverAsync"/>).
    /// </summary>
    public bool LeaveCounts => PastLeftWinMinute || SerialLeaver;

    /// <summary>A leave before the win minute (from serialLeaver.fromMinute) the relay did not hear the leaver play through.</summary>
    public bool EarlyLeave(OpponentLeftRules rules) =>
        OpponentLeft && !OpponentHeardToEnd && !PastLeftWinMinute && Minute >= rules.SerialLeaver.FromMinute;

    /// <summary>
    /// The facts of a match end: secondsPlayed is FIFA's in-game clock as the client reports it (a full 90 reports about
    /// 5,500 — a claim, not evidence); the real time runs from the server's kickoff record. The quitter is the player who
    /// sent QUIT, DNF from a game, or DNF_AFK (FIFA removed them for inactivity); the player whose opponent left gets DNF_WIN, or a DNF result carrying an
    /// opponentLeft bit, or a lost-connection DNF result where the gameplay relay heard the opponent go quiet first
    /// (<paramref name="opponentQuietFirstBy"/>, at least opponentLeft.relayQuietSeconds): a crash, pulled cable or dead
    /// connection sets only the connectionLost bit, the same on both ends (owner, 2026-10-04). The opponent was heard to the end
    /// when the relay has its record and it went quiet less than relayQuietSeconds before the caller: whatever FIFA's bits
    /// claim, its game was still running when the caller's ended. A timed-out opponent (the opponentTimedOut bit, no quit
    /// bit) the relay heard to the end never left: FIFA ended a stalled match on both sides (<see cref="MutualTimeout"/>).
    /// </summary>
    public static MatchEndFacts From(JsonObject body, long? realSeconds, RewardEligibilityRules rules, TimeSpan? opponentQuietFirstBy = null)
    {
        var reason = body.Str("endReason").ToUpperInvariant();
        var flags = body.Int("matchStatusFlags");
        var gameSeconds = Math.Max(0, body.Int("secondsPlayed"));
        var quitter = Abandoned(reason);
        var desync = !quitter && (flags & rules.MatchStatusFlags.Desync) != 0;
        var dropped = !quitter && !desync && LostConnection(reason, flags, rules)
            && opponentQuietFirstBy?.TotalSeconds >= rules.OpponentLeft.RelayQuietSeconds;
        var left = dropped || (!quitter && !desync && (reason == "DNF_WIN" || (reason.StartsWith("DNF_", StringComparison.Ordinal) && (flags & rules.MatchStatusFlags.OpponentLeft) != 0)));
        // Only the server's match clock decides: a crafted match end can name any game clock, so secondsPlayed never
        // substitutes for real time since kickoff. A match without a server clock (no CreateMatch, no pairing) pays nothing.
        var longEnough = realSeconds >= rules.MinRealSeconds;
        var heardToEnd = opponentQuietFirstBy?.TotalSeconds < rules.OpponentLeft.RelayQuietSeconds;
        var bits = rules.MatchStatusFlags;
        var mutualTimeout = left && !dropped && heardToEnd && reason.StartsWith("DNF_", StringComparison.Ordinal)
            && (flags & bits.OpponentTimedOut) != 0 && (flags & bits.OpponentLeft & ~bits.OpponentTimedOut) == 0;
        // The match minute when the opponent left: FIFA's game clock, never more than the real time since kickoff allows.
        var minute = Math.Min(gameSeconds / 60, (long)((realSeconds ?? 0) / rules.OpponentLeft.RealSecondsPerMinute));
        return new MatchEndFacts(reason, flags, gameSeconds, realSeconds, longEnough, quitter, desync, left, minute,
            minute >= rules.OpponentLeft.WinFromMinute, dropped, heardToEnd, mutualTimeout);
    }

    /// <summary>
    /// The player left the match: QUIT, DNF, or DNF_AFK. FIFA sends DNF_AFK with no match stats, so read by its goals it
    /// was a 0-0 draw and paid in full (club 59 losing 1-5, 2026-10-05).
    /// </summary>
    public static bool Abandoned(string endReason) => endReason.ToUpperInvariant() is "QUIT" or "DNF" or "DNF_AFK";

    /// <summary>A DNF result of a game whose peer link was lost (the connectionLost bit): who left needs the relay's word.</summary>
    public static bool LostConnection(string endReason, long flags, RewardEligibilityRules rules) =>
        endReason.ToUpperInvariant().StartsWith("DNF_", StringComparison.Ordinal) && (flags & rules.MatchStatusFlags.ConnectionLost) != 0;
}

/// <summary>The gate's answer for one match end.</summary>
internal sealed record RewardDecision(RewardVerdict Verdict, string? Mode, long FlatCoins, bool CountsProgress, bool GrantsPack)
{
    /// <summary>Whether the mode's completion coins are paid.</summary>
    public bool PaysResult => Verdict == RewardVerdict.Paid;

    public static RewardDecision Nothing(RewardVerdict verdict, string? mode) => new(verdict, mode, 0, false, false);
}

/// <summary>
/// The one reward-eligibility gate (PLAN 4.3) for match coins, Season XP, objective stats and the Match Pack: which modes
/// pay, the minimum length, who gets what when a match ends early, difficulty scaling and the pair caps. Pure: the match
/// end services read the state it needs and act on its answer.
/// </summary>
internal static class RewardEligibility
{
    /// <summary>
    /// The result of the player whose opponent left (owner, 2026-10-03; rules/seasons.json rewardEligibility.opponentLeft):
    /// losing at the time, no result; level or ahead from the win minute, a win; earlier, no result. The leaver's own loss
    /// is settled in its club (<see cref="MatchService.SettleLeaverAsync"/>).
    /// Losing from the win minute while the relay heard the opponent to the end is the score's loss: the opponent's game
    /// was running when the caller's ended, so it either quit a match it led or never left (a link the caller cut). Stalling
    /// a lost match (pause, alt-tab freeze) until the leader quit, or cutting the link, cost nothing before 2026-10-07 while
    /// the leader took the quit's loss (toxicz.v2, Rivals 25-1-0 with four such ends behind 0-2, 0-3, 1-3 and 1-2). An
    /// opponent the relay heard go quiet first really dropped (the owner's 0-2 Weekend League game, 2026-10-04): no result.
    /// A level <see cref="MatchEndFacts.MutualTimeout"/> is no result: both ends read as the stayer's, so both players won
    /// (17:04 UTC 2026-10-07, 1-1, each paid a win; owner, 2026-10-08).
    /// </summary>
    public static MatchService.MatchOutcome StayerOutcome(MatchEndFacts facts, long mine, long theirs) =>
        !facts.LeaveCounts ? MatchService.MatchOutcome.None
        : facts.MutualTimeout && mine == theirs ? MatchService.MatchOutcome.None
        : mine >= theirs ? MatchService.MatchOutcome.Win
        : facts.OpponentHeardToEnd ? MatchService.MatchOutcome.Loss
        : MatchService.MatchOutcome.None;

    /// <summary>
    /// The result of a paired match without a mode verdict of its own (Rivals and online Draft; the Weekend League and
    /// Squad Battles settle their own): a pair-capped or called-off match and a desync are no result; the quitter loses;
    /// the player whose opponent left gets <see cref="StayerOutcome"/>; any other end before the minimum is no result;
    /// otherwise the score decides (<paramref name="scored"/>).
    /// </summary>
    public static MatchService.MatchOutcome PairedOutcome(MatchEndFacts facts, MatchService.MatchOutcome scored, bool pairCapped, long mine, long theirs) =>
        pairCapped || facts.EndReason == "NO_CONTEST" ? MatchService.MatchOutcome.None
        : facts.Quitter ? MatchService.MatchOutcome.Loss
        : facts.Desync ? MatchService.MatchOutcome.None
        : facts.OpponentLeft ? StayerOutcome(facts, mine, theirs)
        : !facts.LongEnough ? MatchService.MatchOutcome.None
        : scored;

    /// <summary>
    /// What a match end pays. <paramref name="mode"/> is null for a match no paying mode claimed; <paramref name="outcome"/>
    /// is the settled result (None for a void one).
    /// </summary>
    public static RewardDecision Decide(SeasonRules seasons, string? mode, MatchEndFacts facts,
        MatchService.MatchOutcome outcome, bool pairCapped, bool restricted)
    {
        var rules = seasons.RewardEligibility;
        if (mode is null || !rules.PayingModes.Contains(mode))
        {
            return RewardDecision.Nothing(RewardVerdict.Friendly, mode);
        }
        if (restricted)
        {
            return RewardDecision.Nothing(RewardVerdict.RestrictedSquad, mode);
        }
        if (pairCapped)
        {
            return RewardDecision.Nothing(RewardVerdict.PairCapped, mode);
        }
        if (facts.EndReason == "NO_CONTEST")
        {
            return RewardDecision.Nothing(RewardVerdict.NoContest, mode);
        }
        if (facts.Quitter)
        {
            return RewardDecision.Nothing(RewardVerdict.Quitter, mode);
        }
        if (facts.Desync)
        {
            return RewardDecision.Nothing(RewardVerdict.DesyncVoid, mode) with { FlatCoins = facts.LongEnough ? rules.DesyncCoins : 0 };
        }
        // A stalled match FIFA ended on both sides keeps its result but pays nothing: the leader could alt-tab for about 20 s
        // and be paid the opponent-left win (mn1cr4m23 3-2, 2026-10-07; owner, 2026-10-08).
        if (facts.MutualTimeout)
        {
            return RewardDecision.Nothing(RewardVerdict.MutualTimeout, mode);
        }
        // A win over an opponent who left past the win minute pays however short the match ran; only an online mode has an
        // opponent who can leave, so a CPU match always needs the minimum length.
        if (!facts.LongEnough && !(RewardModes.Online.Contains(mode) && facts.OpponentLeft && facts.LeaveCounts
                                   && outcome == MatchService.MatchOutcome.Win))
        {
            return RewardDecision.Nothing(RewardVerdict.TooShort, mode);
        }
        if (outcome == MatchService.MatchOutcome.None)
        {
            return RewardDecision.Nothing(RewardVerdict.NoContest, mode);
        }
        return new RewardDecision(RewardVerdict.Paid, mode, 0, true, seasons.MatchCompletionRewards[mode].PackId is not null);
    }

    /// <summary>The flat completion award; performance statistics and difficulty do not affect it.</summary>
    public static (long Award, long Earned, JsonArray Partials) Coins(SeasonRules seasons, RewardDecision decision, MatchService.MatchOutcome outcome, JsonObject body)
    {
        var coins = decision.PaysResult ? seasons.MatchCompletionRewards[decision.Mode!].Coins : decision.FlatCoins;
        return (coins, coins, new JsonArray());
    }
}
