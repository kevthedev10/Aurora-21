using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>The modes a match end can belong to, as rules/seasons.json names them (rewardEligibility, matchCompletionRewards).</summary>
public static class RewardModes
{
    public const string Rivals = "rivals";
    public const string WeekendLeague = "weekendLeague";
    public const string SquadBattles = "squadBattles";
    public const string DraftOnline = "draftOnline";
    public const string DraftSinglePlayer = "draftSinglePlayer";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Rivals, WeekendLeague, SquadBattles, DraftOnline, DraftSinglePlayer };

    /// <summary>
    /// The modes played against another person, where an opponent can leave. Squad Battles and single-player Draft are
    /// played against the CPU: a DNF_WIN there can only be a crafted match end.
    /// </summary>
    public static readonly IReadOnlySet<string> Online = new HashSet<string>(StringComparer.Ordinal)
        { Rivals, WeekendLeague, DraftOnline };
}

/// <summary>Anti-collusion: rewarded games against one opponent within a window (or one Weekend League).</summary>
public sealed class PairCapRules
{
    public required List<string> Modes { get; set; }
    public required int Games { get; set; }

    /// <summary>A rolling window in seconds; ignored when <see cref="PerWeekendLeague"/>.</summary>
    public long WindowSeconds { get; set; }

    /// <summary>Counts within the Weekend League being played rather than a rolling window.</summary>
    public bool PerWeekendLeague { get; set; }
}

/// <summary>The matchStatusFlags bits the gate reads (seasons.json rewardEligibility).</summary>
public sealed class EligibilityFlagRules
{
    /// <summary>The bits FIFA sets on the end of the player whose opponent quit (2) or dropped (4).</summary>
    public required long OpponentLeft { get; set; }

    /// <summary>The desync bit (16).</summary>
    public required long Desync { get; set; }

    /// <summary>
    /// The opponent-left bit FIFA sets when its peer stopped answering (4), as against the quit bit (2). Both ends of one
    /// match carry it when a game stalls about 20 s (an alt-tab): FIFA ends both and each calls the other the leaver.
    /// </summary>
    public long OpponentTimedOut { get; set; } = 4;

    /// <summary>
    /// The bit FIFA sets when the peer link was lost (8): the same on both ends whichever player dropped, so on its own it
    /// does not say who left (see <see cref="OpponentLeftRules.RelayQuietSeconds"/>).
    /// </summary>
    public long ConnectionLost { get; set; } = 8;
}

/// <summary>
/// The one reward-eligibility gate for match coins, Season XP, objective stats and the Match Pack (PLAN 4.3; seasons.json
/// _rewardEligibilityComment).
/// </summary>
public sealed class RewardEligibilityRules : IJsonOnDeserialized
{
    public required List<string> PayingModes { get; set; }
    /// <summary>Real seconds the server's match clock must show since kickoff before a match pays anything.</summary>
    public required long MinRealSeconds { get; set; }
    /// <summary>FIFA's in-game clock, as the match end claims it (kept for the log line and rules compatibility). Not a
    /// gate input: a crafted end can name any value, so the minimum is enforced on <see cref="MinRealSeconds"/> alone.</summary>
    public required long MinGameSeconds { get; set; }
    public required EligibilityFlagRules MatchStatusFlags { get; set; }
    public required long DesyncCoins { get; set; }
    public required List<PairCapRules> PairCaps { get; set; }

    /// <summary>Whether competitive modes refuse Pro, custom and grant-only cards (PLAN 7.1); off by owner decision.</summary>
    public bool RestrictGrantOnlyCards { get; set; }

    /// <summary>What the player whose opponent left gets (the leaver always loses): see <see cref="OpponentLeftRules"/>.</summary>
    public OpponentLeftRules OpponentLeft { get; set; } = new();

    /// <summary>The pair cap covering a mode, or null when its games are never capped.</summary>
    public PairCapRules? PairCapFor(string mode) => PairCaps.FirstOrDefault(cap => cap.Modes.Contains(mode));

    public void OnDeserialized()
    {
        var modes = PayingModes.Concat(PairCaps.SelectMany(cap => cap.Modes)).ToList();
        if (modes.Any(mode => !RewardModes.All.Contains(mode)) || MinRealSeconds < 0 || MinGameSeconds < 0 || DesyncCoins < 0
            || PairCaps.Any(cap => cap.Games < 0 || (!cap.PerWeekendLeague && cap.WindowSeconds <= 0))
            || PairCaps.SelectMany(cap => cap.Modes).GroupBy(mode => mode).Any(group => group.Count() > 1)
            || OpponentLeft.WinFromMinute is < 0 or > 120 || OpponentLeft.RealSecondsPerMinute <= 0 || OpponentLeft.RelayQuietSeconds <= 0
            || OpponentLeft.SerialLeaver.EarlyLeaves < 0 || OpponentLeft.SerialLeaver.WindowHours <= 0 || OpponentLeft.SerialLeaver.FromMinute < 0
            || OpponentLeft.QueueCooldown.EarlyLeaves < 0 || OpponentLeft.QueueCooldown.WindowSeconds <= 0
            || OpponentLeft.QueueCooldown.LadderSeconds.Any(seconds => seconds <= 0) || OpponentLeft.QueueCooldown.OffenceMemoryDays <= 0)
        {
            throw new InvalidDataException("rules/seasons.json rewardEligibility needs known modes, non-negative lengths and coins, one pair cap per mode with a window.");
        }
    }
}

/// <summary>
/// The player whose opponent left (owner, 2026-10-03; from the 10th minute since 2026-10-04, was the 35th): losing at the time, the match does not count for them
/// (from <see cref="WinFromMinute"/> it is a loss when the relay heard the opponent to the end, since 2026-10-07); level or
/// ahead from <see cref="WinFromMinute"/>, a win (paid even before the minimum length); earlier, it does not count. The
/// minute is FIFA's game clock, never more than the server's real time since kickoff allows at
/// <see cref="RealSecondsPerMinute"/> (online halves run about 8.5 real seconds a game minute), so a crafted end
/// cannot claim it.
/// </summary>
public sealed class OpponentLeftRules
{
    public long WinFromMinute { get; set; } = 10;
    public double RealSecondsPerMinute { get; set; } = 7;

    /// <summary>
    /// A lost-connection DNF end is the opponent leaving when the gameplay relay heard nothing from the opponent for at least
    /// this many seconds before the caller's own last datagram (a crash, a pulled cable or a dead connection carries no
    /// opponent-left bit). FIFA reports the lost peer about 15 s after its last datagram and keeps sending meanwhile; during
    /// play datagrams are never more than half a second apart (owner capture, 2026-10-04).
    /// </summary>
    public double RelayQuietSeconds { get; set; } = 5;

    public SerialLeaverRules SerialLeaver { get; set; } = new();

    public QueueCooldownRules QueueCooldown { get; set; } = new();
}

/// <summary>
/// Leaving before <see cref="OpponentLeftRules.WinFromMinute"/> is rare in honest play; players do it because a leave that
/// early gives the opponent no result (owner, 2026-10-09). <see cref="EarlyLeaves"/> early leaves (the ones match_leaves
/// logs) within <see cref="WindowSeconds"/> are an offence (leaves before an earlier offence never count again). Offences
/// escalate: the n-th within <see cref="OffenceMemoryDays"/> bars online searches for <see cref="LadderSeconds"/>[n-1] from
/// the leave; the one after the ladder is a ban until an operator lifts it (migration 180 leaver_penalties,
/// Sessions.LeaverCooldown). A lifted penalty does not count. <see cref="EarlyLeaves"/> 0 turns it off.
/// </summary>
public sealed class QueueCooldownRules
{
    public int EarlyLeaves { get; set; } = 2;
    public long WindowSeconds { get; set; } = 3600;
    public List<long> LadderSeconds { get; set; } = [3600, 86400];
    public long OffenceMemoryDays { get; set; } = 30;

    /// <summary>How long the <paramref name="offence"/>-th offence bars searches; null is a ban.</summary>
    public long? PenaltySeconds(int offence) => offence >= 1 && offence <= LadderSeconds.Count ? LadderSeconds[offence - 1] : null;
}

/// <summary>
/// A player who keeps leaving before <see cref="OpponentLeftRules.WinFromMinute"/> denies every opponent a result (owner,
/// 2026-10-08). Each early leave is logged (match_leaves); once a player has <see cref="EarlyLeaves"/> of them within
/// <see cref="WindowHours"/>, each further early leave from <see cref="FromMinute"/> counts as if past the win minute: the
/// stayer level or ahead wins. A leave the relay heard the leaver play through is never counted (the stayer may have cut
/// the link). <see cref="EarlyLeaves"/> 0 only logs.
/// </summary>
public sealed class SerialLeaverRules
{
    public int EarlyLeaves { get; set; } = 3;
    public double WindowHours { get; set; } = 24;

    /// <summary>A leave at minute 0 (often a dead link before kickoff settled) neither counts nor is penalised.</summary>
    public long FromMinute { get; set; } = 1;
}
