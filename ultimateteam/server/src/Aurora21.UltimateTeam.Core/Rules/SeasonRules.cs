using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>One Division Rivals division: its Skill Rating floor, relegation line, K-Factor and FUT Champions Points.</summary>
public sealed class RivalsDivision
{
    public required int Division { get; set; }
    public required long Floor { get; set; }
    public long? RelegationLine { get; set; }
    public required long KFactor { get; set; }

    /// <summary>FUT Champions Points per match: win, draw, loss.</summary>
    public required List<long> FcPoints { get; set; }
}

/// <summary>
/// Division Rivals as in FIFA 21 retail (Python server 56145ed, rivals_skill.py): EA's Elo with K-Factors by division,
/// fifauteam's division floors and relegation lines, five placement matches and a weekly competition that turns over
/// Thursday 07:00 UTC. See rules/seasons.json _rivalsComment.
/// </summary>
public sealed class RivalsRules : IJsonOnDeserialized
{
    public required long WeeklyMatches { get; set; }
    public required long PlacementMatches { get; set; }
    public required long PlacementStart { get; set; }
    public required long PlacementK { get; set; }
    public required long RatingVersion { get; set; }
    public required long WeekOffsetSeconds { get; set; }
    public required long TopSkillThreshold { get; set; }
    public required long SoftCapWarning { get; set; }
    public required long PairingTtlSeconds { get; set; }
    public required long FirstRewardWeek { get; set; }
    public required long PrizeWeeksBack { get; set; }
    public required long StandingsCacheSeconds { get; set; }
    public required List<RivalsDivision> Divisions { get; set; }

    public int DivisionCount => Divisions.Count;

    private RivalsDivision Table(long division) => Divisions[(int)Math.Clamp(division, 1, DivisionCount) - 1];

    public long Floor(long division) => Table(division).Floor;

    /// <summary>The next division's floor; null for Division 1.</summary>
    public long? Ceiling(long division) => Math.Clamp(division, 1, DivisionCount) == 1 ? null : Table(division - 1).Floor;

    public long? RelegationLine(long division) => Table(division).RelegationLine;

    public long KFactor(long division) => Table(division).KFactor;

    public long FcPoints(long division, string outcome) => outcome switch
    {
        "win" => Table(division).FcPoints[0],
        "draw" => Table(division).FcPoints[1],
        "loss" => Table(division).FcPoints[2],
        _ => 0,
    };

    /// <summary>The division whose range holds a Skill Rating.</summary>
    public int DivisionFor(long rating) => Divisions.FirstOrDefault(division => rating >= division.Floor)?.Division ?? DivisionCount;

    /// <summary>
    /// The division for the next weekly competition: promotion follows Skill Rating; relegation waits until the rating
    /// falls below the current division's relegation line, then drops to the division that holds it.
    /// </summary>
    public int NextWeekDivision(long weekDivision, long rating)
    {
        var current = (int)Math.Clamp(weekDivision, 1, DivisionCount);
        var earned = DivisionFor(rating);
        if (earned <= current)
        {
            return earned;
        }
        return RelegationLine(current) is { } line && rating >= line ? current : earned;
    }

    /// <summary>The new Skill Rating after one match: EA's formula, rounded as its spreadsheet does, never below 0.</summary>
    public long Rated(long own, long opponent, string outcome, long k)
    {
        var result = outcome switch { "win" => 1.0, "draw" => 0.5, _ => 0.0 };
        var a = Math.Pow(10, own / 400.0);
        var b = Math.Pow(10, opponent / 400.0);
        return Math.Max(0, (long)Math.Round(own + k * (result - a / (a + b)), MidpointRounding.AwayFromZero));
    }

    /// <summary>Rank 5..1 across the division's range (1 the top), for the leaderboards.</summary>
    public long RankInDivision(long rating, long division)
    {
        var low = Floor(division);
        var width = Ceiling(division) is { } high ? high - low : 300;
        var step = Math.Max(1, width / 5);
        return Math.Clamp(5 - (rating - low) / step, 1, 5);
    }

    /// <summary>The weekly competition id for a moment: weeks from the epoch, starting Thursday 07:00 UTC.</summary>
    public long WeekIndex(long now) => (long)Math.Floor((now - WeekOffsetSeconds) / (double)SeasonRules.WeekSeconds);

    public long WeekStart(long week) => week * SeasonRules.WeekSeconds + WeekOffsetSeconds;

    public void OnDeserialized()
    {
        Divisions = Divisions.OrderBy(division => division.Division).ToList();
        if (!Divisions.Select(division => division.Division).SequenceEqual(Enumerable.Range(1, Divisions.Count)) || Divisions.Count == 0
            || Divisions.Any(division => division.FcPoints.Count != 3) || Divisions[^1].Floor != 0)
        {
            throw new InvalidDataException("rivals.divisions needs divisions 1..N, each with three fcPoints, the last with floor 0");
        }
    }
}

/// <summary>The /match/end matchStatusFlags bits a Weekend League end is judged by (Python server 24d7015, c3992c5).</summary>
public sealed class WeekendLeagueMatchFlags
{
    public required long OpponentQuit { get; set; }
    public required long ConnectionLost { get; set; }
    public required long Desync { get; set; }
}

/// <summary>
/// The FUT Champions Weekend League (Python server 56145ed, champions_wl.py): Friday 07:00 to Monday 07:00 UTC,
/// <see cref="MaxMatches"/> matches (retail 30, phase 1 20), entry for FUT Champions Points redeemed before it starts or within the grace hours after, rewards the Monday it
/// ends at <see cref="RewardHourUk"/> UK time (retail paid Thursday 08:00 UTC). See rules/seasons.json _weekendLeagueComment.
/// </summary>
public sealed class WeekendLeagueRules : IJsonOnDeserialized
{
    public void OnDeserialized() => LeaderboardSizes.Validate(TopX, LeaderboardSizes.ChampionsMinimum, "weekendLeague.topX");
    public required long QualificationPoints { get; set; }
    public required long StartOffsetSeconds { get; set; }
    public required long LengthSeconds { get; set; }
    public required long GraceHours { get; set; }
    public required long MaxMatches { get; set; }
    public required long MinMatchesToRank { get; set; }
    public required long FormMin { get; set; }
    public required long FormMax { get; set; }
    public required long RewardHourUk { get; set; }
    public required long PendingMatchSeconds { get; set; }
    /// <summary>The Weekend League venue, the FUT Champions Stadium (the CreateMatch championsWeekendLeagueStadiumId).</summary>
    public required long VenueId { get; set; }
    public required WeekendLeagueMatchFlags MatchStatusFlags { get; set; }
    public required long ConnectionWinAfterSeconds { get; set; }
    public required long ReportMemorySeconds { get; set; }
    public required int TopX { get; set; }
    public required long MonthIdBase { get; set; }
    public required long StandingsCacheSeconds { get; set; }
    public required string LeaderboardSku { get; set; }
    public required long FinishedRemainingMatches { get; set; }
    public required Dictionary<string, List<string>> RegionQueries { get; set; }
    public required List<long> OpenEvents { get; set; }
    public required long FirstRewardEvent { get; set; }
    public required long PrizeEventsBack { get; set; }
    public required Dictionary<string, List<string>> Regions { get; set; }
    public required string DefaultCountry { get; set; }
    public required string DefaultRegion { get; set; }

    public (long Id, long Start, long End) Event(long id) =>
        (id, id * SeasonRules.WeekSeconds + StartOffsetSeconds, id * SeasonRules.WeekSeconds + StartOffsetSeconds + LengthSeconds);

    /// <summary>The most recent Weekend League to have started at or before now.</summary>
    public (long Id, long Start, long End) LatestStarted(long now) =>
        Event((long)Math.Floor((now - StartOffsetSeconds) / (double)SeasonRules.WeekSeconds));

    public (long Id, long Start, long End)? Live(long now) => LatestStarted(now) is var current && current.Start <= now && now < current.End ? current : null;

    public (long Id, long Start, long End) Upcoming(long now) => Event(LatestStarted(now).Id + 1);

    /// <summary>The Weekend League the screens are about: the live one, else the next.</summary>
    public (long Id, long Start, long End) Slot(long now) => Live(now) ?? Upcoming(now);

    public bool InGrace(long now) => Live(now) is { } current && now < current.Start + GraceHours * 3600;

    /// <summary>The Weekend League a redemption now enters: the live one inside its grace hours, otherwise the next.</summary>
    public (long Id, long Start, long End) RedeemTarget(long now) => InGrace(now) ? Live(now)!.Value : Upcoming(now);

    public long FormAfter(long form, string outcome) => Math.Clamp(form + outcome switch { "win" => 1, "loss" => -1, _ => 0 }, FormMin, FormMax);

    /// <summary>
    /// When a Weekend League's rewards are settled: <see cref="RewardHourUk"/> UK time on the day it ends (Monday). UK
    /// time is worked out here, not from the host's time zone data: GMT, or BST (UTC+1) from 01:00 UTC on the last
    /// Sunday of March to 01:00 UTC on the last Sunday of October.
    /// </summary>
    public long RewardsTime(long id)
    {
        var day = DateTimeOffset.FromUnixTimeSeconds(Event(id).End).UtcDateTime.Date;
        var asUtc = new DateTimeOffset(day.AddHours(RewardHourUk), TimeSpan.Zero);
        return asUtc.AddHours(IsBritishSummerTime(asUtc) ? -1 : 0).ToUnixTimeSeconds();
    }

    internal static bool IsBritishSummerTime(DateTimeOffset utc) =>
        utc >= LastSunday(utc.Year, 3).AddHours(1) && utc < LastSunday(utc.Year, 10).AddHours(1);

    private static DateTimeOffset LastSunday(int year, int month)
    {
        var last = new DateTimeOffset(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, TimeSpan.Zero);
        return last.AddDays(-(int)last.DayOfWeek);
    }

    public string RegionFor(string country) =>
        Regions.FirstOrDefault(region => region.Value.Contains(country.ToUpperInvariant())).Key ?? DefaultRegion;

    /// <summary>
    /// The calendar month (UTC) holding <paramref name="now"/>, or <paramref name="back"/> months before it: its id
    /// (monthIdBase + yyyymm, clear of the weekly ids), bounds and the Weekend Leagues that start in it.
    /// </summary>
    public (long Id, long Start, long End, List<long> Events) MonthOf(long now, int back = 0)
    {
        var day = DateTimeOffset.FromUnixTimeSeconds(now).UtcDateTime;
        var first = new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-back);
        var start = new DateTimeOffset(first).ToUnixTimeSeconds();
        var end = new DateTimeOffset(first.AddMonths(1)).ToUnixTimeSeconds();
        var firstEvent = LatestStarted(start - 1).Id + 1;
        var events = Enumerable.Range(0, 6).Select(offset => firstEvent + offset).Where(id => Event(id).Start >= start && Event(id).Start < end).ToList();
        return (MonthIdBase + first.Year * 100 + first.Month, start, end, events);
    }

    /// <summary>Whether a leaderboard region query keeps a club's region: EUR and AMS their own regions, ROW every other.</summary>
    public bool RegionMatches(string query, string region) =>
        RegionQueries.TryGetValue(query, out var regions) ? regions.Contains(region)
        : query != "ROW" || !RegionQueries.Values.SelectMany(values => values).Contains(region);
}

/// <summary>
/// The seasonal CDN document's FUT Champions schedule fields; the Weekend League itself is <see cref="WeekendLeagueRules"/>, whose
/// maxMatches is the document's finalsMaxGames.
/// </summary>
public sealed class ChampionsRules
{
    public required string LocalizedName { get; set; }
    public required long PlayoffMatches { get; set; }
    public required long ScheduleViewPeriodMinutes { get; set; }
    public required long RewardsDelaySeconds { get; set; }
}

public sealed class LeaderboardOption
{
    public required string Id { get; set; }
    public required string Display { get; set; }
    public required string Description { get; set; }
    public required string Image { get; set; }

    /// <summary>A category's score column title (a string key FIFA localises); null for view and period options.</summary>
    public string? ScoreLabel { get; set; }
}

public sealed class LeaderboardOptionGroup
{
    public required string Id { get; set; }
    public required string Default { get; set; }
    public required List<LeaderboardOption> Options { get; set; }
}

public sealed class LeaderboardRowRules
{
    public required long Badge { get; set; }
    public required long Est { get; set; }
    public required string Sku { get; set; }
    public required string ScoreIcon { get; set; }
    public required string TiebreakIcon { get; set; }
}

public sealed class LeaderboardRules
{
    public required List<LeaderboardOptionGroup> Groups { get; set; }
    public required string UrlTemplate { get; set; }
    public required int MaxRows { get; set; }
    public required long CacheSeconds { get; set; }
    public required int MaxNameLength { get; set; }
    public required string DefaultName { get; set; }
    public required LeaderboardRowRules Row { get; set; }
}

/// <summary>Flat coins, FIFA Points and an optional pack for an eligible completed match, independent of result and difficulty.</summary>
public sealed class MatchCompletionRewardRules
{
    public required long Coins { get; set; }

    /// <summary>FIFA Points per completed match (owner, 2026-10-04); 0 pays none.</summary>
    public long FifaPoints { get; set; }

    /// <summary>Season XP per completed match (migration 145; it was objectives xp.match plus xp.win for a win), whatever the result.</summary>
    public long Xp { get; set; }
    public required long? PackId { get; set; }
    public required bool Untradeable { get; set; }
}

/// <summary>Online pairing by Windows maths group (seasons.json _mathGroupsComment; Python server 43d3bac, c3992c5).</summary>
public sealed class MathGroupRules
{
    public required bool Enabled { get; set; }
    public required long NewBuild { get; set; }
    public required Dictionary<long, string> BridgeGroups { get; set; }

    /// <summary>Whether a bridged CPU without AVX2 is its own group (seasons.json mathGroups.splitNoAvx2; on unless set false).</summary>
    public bool SplitNoAvx2 { get; set; } = true;

    public string GroupOfWindows(long build) => build >= NewBuild ? "24h2" : "pre24h2";

    /// <summary>
    /// A bridged ucrtbase's group: a build measured bit-identical to a Windows group joins it; a CPU without FMA3 is apart, and so
    /// is one without AVX2 (FIFA's own code outside ucrtbase can take AVX2 paths the bridge cannot pin).
    /// </summary>
    public string GroupOfBridge(long ucrtBuild, bool fma3, bool avx2 = true) =>
        (BridgeGroups.TryGetValue(ucrtBuild, out var group) ? group : "ucrt" + ucrtBuild.ToString(System.Globalization.CultureInfo.InvariantCulture))
        + (fma3 ? string.Empty : "-nofma3") + (avx2 || !SplitNoAvx2 ? string.Empty : "-noavx2");
}

/// <summary>Keeping a client that desyncs with everyone apart (seasons.json _desyncQuarantineComment, Sessions.DesyncQuarantine).</summary>
public sealed class DesyncQuarantineRules
{
    public bool Enabled { get; set; } = true;
    /// <summary>Only a desync this many real seconds into the match or sooner counts.</summary>
    public long EarlySeconds { get; set; } = 120;
    /// <summary>How many different opponents a player must have desynced with inside the window.</summary>
    public int DistinctOpponents { get; set; } = 3;
    public long WindowSeconds { get; set; } = 3600;
    /// <summary>How long a quarantine lasts.</summary>
    public long QuarantineSeconds { get; set; } = 21600;
}

/// <summary>
/// The online pre-match squad fetches (seasons.json _onlineMatchComment; Python server RIVALS_OPPONENT_HANDOFF, whose
/// ACTIVE_SWAP and WAIT_S production runs derived from the umbrella).
/// </summary>
public sealed class OnlineMatchRules : IJsonOnDeserialized
{
    /// <summary>The longest wait: below FIFA's roughly ten-second FUT service budget (Python server _RIVALS_OPPONENT_WAIT_CEILING_S).</summary>
    private const double MaxOpponentWaitSeconds = 9.0;

    /// <summary>Screen names FIFA stamps into X-PERFORMANCE-GROUP on its online pre-match fetches (CardsDLL's screen enum).</summary>
    public required List<string> PrematchPerformanceGroups { get; set; }

    /// <summary>How long a queued, unpaired caller's pre-match GET squad/active waits for its pairing.</summary>
    public required double OpponentWaitSeconds { get; set; }

    /// <summary>How often that wait looks for the pairing.</summary>
    public required int OpponentWaitPollMilliseconds { get; set; }

    public TimeSpan OpponentWait => TimeSpan.FromSeconds(OpponentWaitSeconds);

    public TimeSpan OpponentWaitPoll => TimeSpan.FromMilliseconds(OpponentWaitPollMilliseconds);

    /// <summary>Whether an X-PERFORMANCE-GROUP value names an online pre-match screen (any case, anywhere in the value).</summary>
    public bool IsPrematch(string? performanceGroup) =>
        performanceGroup is { Length: > 0 } value && PrematchPerformanceGroups.Any(group => value.Contains(group, StringComparison.OrdinalIgnoreCase));

    public void OnDeserialized()
    {
        if (PrematchPerformanceGroups.Count == 0 || PrematchPerformanceGroups.Any(string.IsNullOrWhiteSpace)
            || OpponentWaitSeconds is < 0 or > MaxOpponentWaitSeconds || OpponentWaitPollMilliseconds is < 1 or > 1000)
        {
            throw new InvalidDataException("rules/seasons.json onlineMatch needs pre-match groups, a wait of 0 to 9 seconds and a poll of 1 to 1000 ms.");
        }
    }
}

/// <summary>rules/seasons.json: Division Rivals, Weekend League, FUT Champions and Leaderboards settings.</summary>
public sealed class SeasonRules : IJsonOnDeserialized
{
    public void OnDeserialized()
    {
        if (!RewardModes.All.SetEquals(MatchCompletionRewards.Keys)
            || MatchCompletionRewards.Values.Any(reward => reward.Coins < 0 || reward.FifaPoints < 0 || reward.Xp < 0 || reward.PackId is <= 0))
            throw new InvalidDataException("seasons matchCompletionRewards needs every paying mode, non-negative coins, FIFA Points and XP, and a positive packId or null.");
    }

    public const long WeekSeconds = 7 * 24 * 60 * 60;

    public required OnlineMatchRules OnlineMatch { get; set; }
    public required RivalsRules Rivals { get; set; }
    public required LeaderboardRules Leaderboards { get; set; }
    public required Dictionary<string, MatchCompletionRewardRules> MatchCompletionRewards { get; set; }
    public required RewardEligibilityRules RewardEligibility { get; set; }
    public required WeekendLeagueRules WeekendLeague { get; set; }
    public required ChampionsRules Champions { get; set; }
    public required MathGroupRules MathGroups { get; set; }

    /// <summary>The desync quarantine (seasons.json desyncQuarantine); its defaults apply when the key is absent.</summary>
    public DesyncQuarantineRules DesyncQuarantine { get; set; } = new();
}
