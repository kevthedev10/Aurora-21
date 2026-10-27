using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>One rated Rivals match: the record after it and this match's changes.</summary>
/// <summary>A Rivals pairing a match end takes: the opponent's Skill Rating at kickoff, when it was made and the opponent persona.</summary>
internal sealed record RivalsPairing(long OpponentRating, long CreatedAt, long OpponentPersona);

public sealed record RivalsMatchResult(RivalsRecord Record, long OpponentRating, long ScoreGain, long PromotionCoins, long FcGain, bool PlacementDone);

/// <summary>The hub's reward fields: a settled week waiting to be claimed, otherwise the live week.</summary>
public sealed record RivalsPrizeView(bool IsPrizeAvailable, long PrizesRivalPoints, long PrizesDivision, long PrizeAwardTime, JsonObject Prizes,
    int TierLevel, bool PreviousAvailable);

/// <summary>
/// Division Rivals as in FIFA 21 retail (Python server 21bf125, 56145ed): EA's Skill Rating with K-Factors by division,
/// five placement matches, a weekly competition with a division fixed for the week, the Weekly Score, FUT Champions Points
/// and promotion rewards. Each week is settled once for every club after it closes (ranks by percentile within each
/// division, or on one pooled server-wide table for a division under poolBelowClubs clubs; rules/rivals_rewards.json) and
/// claimed per club. The opponent's Skill Rating is snapshotted when the match is
/// created (migration 055 <c>rivals_pending</c>), and the match end rates the result against it.
/// </summary>
public sealed class RivalsService
{
    private readonly FutServices _fut;
    private readonly ILogger _log;

    public RivalsService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Rivals");
    }

    private GameContext Game => _fut.Game;
    private RivalsRules Rules => Game.Rules.Seasons.Rivals;
    private RivalsRewardRules Rewards => Game.Rules.RivalsRewards;

    /// <summary>The weekly competition id for now (Thursday 07:00 UTC turnover), the hub's rivalsEventId.</summary>
    public long WeekIndex(long now) => Rules.WeekIndex(now);

    /// <summary>
    /// This club's record, rolled over to the current weekly competition. A record from before the current rating version
    /// is cleared (the club places again; its club record stays). At the turnover the finished week is kept as the last
    /// week (for its reward) and the new week's division takes that week's promotion or relegation. A club without a
    /// record gets a fresh one in memory only, so reading the hub does not create a row.
    /// </summary>
    public async ValueTask<RivalsRecord> ProgressAsync(IUnitOfWork uow) => await ProgressAsync(uow, await uow.RivalsRecordAsync());

    /// <summary><see cref="ProgressAsync(IUnitOfWork)"/> from the stored record already read (the hub reads it with its other tiles).</summary>
    internal async ValueTask<RivalsRecord> ProgressAsync(IUnitOfWork uow, RivalsRecord? stored)
    {
        var week = WeekIndex(Game.NowSeconds);
        if (stored is not null && (stored.RatingVersion < Rules.RatingVersion || stored.WeekId != week))
        {
            // A roll-over writes: take the club lock (a hub read is not serialized with a match end) and read again.
            await uow.LockClubAsync();
            stored = await uow.RivalsRecordAsync();
        }
        var changed = false;
        RivalsRecord record;
        if (stored is null)
        {
            record = RivalsRecord.Fresh(Rules, Rules.RatingVersion);
        }
        else if (stored.RatingVersion < Rules.RatingVersion)
        {
            _log.LogWarning("RIVALS RESET club={Club} old rating={Rating} division={Division} matches={Matches}",
                uow.ClubId, stored.SkillRating, stored.Division, stored.MatchesPlayed);
            record = RivalsRecord.Fresh(Rules, Rules.RatingVersion);
            changed = true;
        }
        else
        {
            record = stored;
            if (!record.Placed)
            {
                record.WeekDivision = Rules.DivisionCount;
                record.BestDivision = Rules.DivisionCount + 1;
            }
        }
        if (record.WeekId != week)
        {
            if (record.WeekId is { } finished && record.WeeklyScore > 0)
            {
                record.LastWeekId = finished;
                record.LastWeekDivision = record.WeekDivision;
                record.LastWeekScore = record.WeeklyScore;
                record.LastWeekGames = record.WeeklyGames;
            }
            if (record.Placed)
            {
                record.WeekDivision = Rules.NextWeekDivision(record.Division, record.SkillRating);
            }
            record.WeeklyWins = record.WeeklyDraws = record.WeeklyGames = record.WeeklyScore = record.WeeklyCounted = 0;
            record.WeekId = week;
            changed = true;
        }
        // The division the club stands in: FIFA shows promotion and relegation from its change after each match. A division
        // reached mid-week keeps its relegation buffer like the week's own (owner 10-08: Div 6 reached at 750 was lost at 700).
        // During placement it stays at the entry division (the tile shows "Div ?").
        record.Division = record.Placed ? Rules.NextWeekDivision(record.Division, record.SkillRating) : Rules.DivisionCount;
        if (changed && stored is not null)
        {
            await uow.SaveRivalsRecordAsync(record);
        }
        return record;
    }

    /// <summary>A club's Skill Rating, for the opponent's snapshot at kickoff.</summary>
    public async ValueTask<long> SkillRatingAsync(IUnitOfWork uow) => (await ProgressAsync(uow)).SkillRating;

    /// <summary>
    /// Remembers the Rivals opponent of the match this club is creating, with its rating at kickoff; the match clock keeps
    /// the opponent for the reward gate's pair caps.
    /// </summary>
    public async ValueTask<int> NotePairingAsync(IUnitOfWork uow, OnlineOpponent online, long opponentRating)
    {
        await uow.NoteMatchOpponentAsync(online.OpponentPersonaId, online.OpponentId);
        return await uow.ExecuteAsync("""
            INSERT INTO ultimateteam.rivals_pending(club_id, game_id, opponent_persona, opponent_rating, created_at)
            VALUES (@club, @game, @persona, @rating, @now)
            ON CONFLICT (club_id) DO UPDATE SET game_id=EXCLUDED.game_id, opponent_persona=EXCLUDED.opponent_persona,
                opponent_rating=EXCLUDED.opponent_rating, created_at=EXCLUDED.created_at
            """, ("game", online.GameId), ("persona", online.OpponentPersonaId), ("rating", opponentRating), ("now", Game.NowSeconds));
    }

    /// <summary>Takes this club's pending Rivals pairing (any match end consumes it); null without one made within the pairing TTL.</summary>
    internal async ValueTask<RivalsPairing?> TakePairingAsync(IUnitOfWork uow)
    {
        var pending = await uow.QueryAsync(
            "DELETE FROM ultimateteam.rivals_pending WHERE club_id=@club RETURNING opponent_rating, created_at, opponent_persona",
            r => new RivalsPairing(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
        return pending.Count == 0 || Game.NowSeconds - pending[0].CreatedAt > Rules.PairingTtlSeconds ? null : pending[0];
    }

    /// <summary>
    /// Rates the result against the taken pairing when it counts: not for a no-contest, not for a Weekend League match, and
    /// not without a recent pairing.
    /// </summary>
    internal async ValueTask<RivalsMatchResult?> SettleAsync(IUnitOfWork uow, RivalsPairing? pairing, MatchService.MatchOutcome outcome, long goalsFor, bool rate)
    {
        if (!rate || outcome == MatchService.MatchOutcome.None || pairing is null)
        {
            return null;
        }
        return await RecordResultAsync(uow, OutcomeKey(outcome), pairing.OpponentRating, goalsFor);
    }

    internal static string OutcomeKey(MatchService.MatchOutcome outcome) => outcome switch
    {
        MatchService.MatchOutcome.Win => "win",
        MatchService.MatchOutcome.Draw => "draw",
        MatchService.MatchOutcome.Loss => "loss",
        _ => "none",
    };

    /// <summary>
    /// One finished Rivals match: Skill Rating (K by the week's division; placement calibrates faster), then the Weekly
    /// Score and FUT Champions Points once placed (placement matches earn neither), and the promotion reward (coins and packs)
    /// for every division entered for the first time (placement pays the placed division and every one below it).
    /// </summary>
    public async ValueTask<RivalsMatchResult> RecordResultAsync(IUnitOfWork uow, string outcome, long? opponentRating, long goalsFor)
    {
        var record = await ProgressAsync(uow);
        var own = record.SkillRating;
        var opponent = opponentRating ?? own;
        var placement = !record.Placed;
        var rating = Rules.Rated(own, opponent, outcome, placement ? Rules.PlacementK : Rules.KFactor(record.WeekDivision));
        long gain = 0, fcGain = 0;
        var placementDone = false;
        if (placement)
        {
            record.PlacementPlayed++;
            if (record.PlacementPlayed >= Rules.PlacementMatches)
            {
                placementDone = true;
                record.Placed = true;
                // The first weekly competition starts in the placed division.
                record.WeekDivision = Rules.DivisionFor(rating);
            }
        }
        else
        {
            gain = Rewards.ScoreGain(outcome, goalsFor, record.WeeklyCounted);
            fcGain = AddFcPoints(record, Rules.FcPoints(record.WeekDivision, outcome));
        }
        var division = record.Placed ? Rules.NextWeekDivision(placementDone ? record.WeekDivision : record.Division, rating) : Rules.DivisionCount;
        var promotion = new RewardOption();
        List<int> entered = [];
        if (record.Placed && division < record.BestDivision)
        {
            (promotion, entered) = Rewards.PromotionFor(record.BestDivision, division);
            if (promotion.Coins > 0)
            {
                await uow.AddCreditsToAsync(uow.ClubId, promotion.Coins);
            }
            // FIFA's match end shows only the coins; the packs wait in the club's unopened packs.
            foreach (var pack in promotion.Packs)
            {
                for (var copy = 0; copy < pack.Count; copy++)
                {
                    // The line's own choice pays (migration 131); the conversion kept what the flag or the switch paid before.
                    await uow.AddUnopenedPackAsync(pack.PackId, RewardSources.RivalsPromotion, untradeable: pack.Untradeable);
                }
            }
            fcGain += AddFcPoints(record, promotion.FcPoints);
            record.BestDivision = division;
        }
        var coins = promotion.Coins;
        var weekly = placement ? 0 : 1;
        record.SkillRating = rating;
        record.Division = division;
        record.Wins += outcome == "win" ? 1 : 0;
        record.Draws += outcome == "draw" ? 1 : 0;
        record.Losses += outcome == "loss" ? 1 : 0;
        record.MatchesPlayed++;
        record.WeeklyWins += outcome == "win" ? weekly : 0;
        record.WeeklyDraws += outcome == "draw" ? weekly : 0;
        record.WeeklyGames += weekly;
        record.WeeklyScore += gain;
        record.WeeklyCounted = Math.Min(Rewards.WeeklyScore.MatchCap, record.WeeklyCounted + weekly);
        record.LastDelta = rating - own;
        var form = record.Form + outcome switch { "win" => "W", "draw" => "D", _ => "L" };
        record.Form = form[Math.Max(0, form.Length - 5)..];
        await uow.SaveRivalsRecordAsync(record);
        var packs = string.Join(",", promotion.Packs.Select(pack => $"{pack.PackId}x{pack.Count}{(pack.Untradeable ? "u" : "")}"));
        if (placementDone)
        {
            _log.LogWarning("RIVALS PLACEMENT club={Club} rating={Rating} division={Division} coins={Coins} packs={Packs}", uow.ClubId, rating, division, coins, packs);
        }
        else if (entered.Count > 0)
        {
            _log.LogWarning("RIVALS PROMOTION club={Club} division={Division} entered={Entered} coins={Coins} packs={Packs}", uow.ClubId, division,
                string.Join(",", entered), coins, packs);
        }
        return new RivalsMatchResult(record, opponent, gain, coins, fcGain, placementDone);
    }

    /// <summary>Adds FUT Champions Points up to the cap a club can hold (retail: points past it are lost); returns what was added.</summary>
    public long AddFcPoints(RivalsRecord record, long amount)
    {
        var gained = Math.Max(0, Math.Min(amount, Game.Rules.ChampionsRewards.FcPointsCap - record.FcPoints));
        record.FcPoints += gained;
        return gained;
    }

    /// <summary>
    /// EA: a completed Weekend League match adds its Weekly Score and uses one of the week's counting matches, while any
    /// remain (placed clubs only, and only while rivals_rewards.json weeklyScore.weekendLeagueCounts is on).
    /// </summary>
    public async ValueTask<long> CountWeekendLeagueMatchAsync(IUnitOfWork uow)
    {
        var rules = Rewards.WeeklyScore;
        if (!rules.WeekendLeagueCounts)
        {
            return 0;
        }
        var record = await ProgressAsync(uow);
        if (!record.Placed || record.WeeklyCounted >= rules.MatchCap)
        {
            return 0;
        }
        record.WeeklyScore += rules.WeekendLeagueMatch;
        record.WeeklyCounted++;
        record.WeeklyGames++;
        await uow.SaveRivalsRecordAsync(record);
        return rules.WeekendLeagueMatch;
    }

    // ---- weekly competition ---------------------------------------------------

    /// <summary>
    /// Each division's ranks and thresholds for a closed week (small divisions pooled server-wide), without staff clubs while
    /// rules/staff.json leaves them out: they get no reward, and every percentile is taken without them.
    /// </summary>
    private async ValueTask<Dictionary<int, RivalsDivisionTable>> WeekTablesAsync(IUnitOfWork uow, long week, IReadOnlyCollection<RivalsWeekRow> rows)
    {
        if (Game.Rules.Staff.Excludes(StaffRules.Rivals, Rules.WeekStart(week + 1)))
        {
            var staff = await uow.StaffClubIdsAsync(Game.Rules.Staff);
            rows = rows.Where(row => !staff.Contains(row.ClubId)).ToList();
        }
        return Rewards.WeekTables(rows);
    }

    /// <summary>
    /// A closed week's thresholds and award time, settled once for every club: the first request after the week's end plus
    /// the settle delay writes each ranked club's result with the reward options resolved then, so a later rules edit never
    /// changes a settled week. Null while the week is open, before the first rewarded week, or when not computing.
    /// </summary>
    private async ValueTask<(long AwardTime, JsonObject Divisions)?> SettledAsync(IUnitOfWork uow, long week, bool compute)
    {
        if (await uow.RivalsSettlementAsync(week) is { } existing)
        {
            return existing;
        }
        var awardTime = Rules.WeekStart(week + 1) + Rewards.SettleDelaySeconds;
        if (!compute || week < Rules.FirstRewardWeek || Game.NowSeconds < awardTime)
        {
            return null;
        }
        var rows = await uow.RivalsWeekRowsAsync(week, Rules.RatingVersion);
        var tables = await WeekTablesAsync(uow, week, rows);
        var divisions = new JsonObject();
        foreach (var (division, table) in tables)
        {
            divisions[division.ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["participants"] = table.Participants,
                ["thresholds"] = new JsonArray(table.Thresholds.Select(value => (JsonNode?)value).ToArray()),
                ["pooled"] = table.Pooled,
            };
        }
        if (!await uow.TryBeginRivalsSettlementAsync(week, awardTime, divisions))
        {
            // Another club settled it first; its transaction has committed by the time the insert returns.
            return await uow.RivalsSettlementAsync(week);
        }
        var scores = rows.ToDictionary(row => row.ClubId, row => row.Score);
        foreach (var (division, table) in tables)
        {
            foreach (var (clubId, rank) in table.Ranks)
            {
                var options = new JsonArray(Rewards.Divisions[division][rank - 1].Select(option => (JsonNode?)option.ToJson()).ToArray());
                await uow.AddRivalsWeekResultAsync(week, clubId, division, rank, scores[clubId], options);
            }
        }
        _log.LogWarning("RIVALS SETTLED week={Week} clubs={Clubs} by division={Divisions} pooled={Pooled} staffLeftOut={Staff}", week,
            tables.Sum(table => table.Value.Participants),
            string.Join(",", tables.Where(table => table.Value.Participants > 0).Select(table => $"{table.Key}:{table.Value.Participants}")),
            string.Join(",", tables.Where(table => table.Value.Pooled && table.Value.Participants > 0).Select(table => table.Key)),
            rows.Count(row => row.Score > 0) - tables.Sum(table => table.Value.Participants));
        return (awardTime, divisions);
    }

    private static List<long> Thresholds(JsonObject divisions, int division) =>
        JsonValues.Longs(divisions.Obj(division.ToString(CultureInfo.InvariantCulture))?.Get("thresholds"));

    private long AwardTime(long week) => Rules.WeekStart(week + 1) + Rewards.SettleDelaySeconds;

    /// <summary>The claim window: the closed weeks whose rewards can still be claimed.</summary>
    internal (long From, long To) PrizeWindow() => WeekIndex(Game.NowSeconds) is var current ? (current - Rules.PrizeWeeksBack, current - 1) : default;

    /// <summary>
    /// Settles the two most recent closed weeks once their award time has passed (only they may still be settled here). No
    /// query once they are known settled (<see cref="CompetitionCache"/>).
    /// </summary>
    internal ValueTask EnsureSettledAsync(IUnitOfWork uow)
    {
        var current = WeekIndex(Game.NowSeconds);
        var due = new[] { current - 2, current - 1 }.Where(week => week >= Rules.FirstRewardWeek && Game.NowSeconds >= AwardTime(week));
        return Game.Competitions.EnsureSettledAsync("rivals", due, uow.RivalsSettledWeeksAsync, async week => await SettledAsync(uow, week, compute: true));
    }

    /// <summary>Whether last week's leaderboard can be shown: a closed week is settled from its award time (the first read after it settles it).</summary>
    private bool PreviousAvailable(long current) => current - 1 >= Rules.FirstRewardWeek && Game.NowSeconds >= AwardTime(current - 1);

    /// <summary>This club's oldest settled, unclaimed weekly reward, the due weeks settled first.</summary>
    private async ValueTask<(RivalsWeekResult Result, long AwardTime, JsonObject Divisions)?> PendingPrizeAsync(IUnitOfWork uow)
    {
        await EnsureSettledAsync(uow);
        var (from, to) = PrizeWindow();
        return await uow.OldestUnclaimedRivalsResultAsync(from, to);
    }

    /// <summary>
    /// A week's live rows: other clubs' from the shared cache (reused for standingsCacheSeconds), this club's from its own
    /// current record, so its own score never lags.
    /// </summary>
    private async ValueTask<List<RivalsWeekRow>> LiveRowsAsync(IUnitOfWork uow, long week, RivalsRecord record) =>
        (await Game.Competitions.RowsAsync("rivals", week, Rules.StandingsCacheSeconds, () => uow.RivalsWeekRowsAsync(week, Rules.RatingVersion)))
            .Where(row => row.ClubId != uow.ClubId)
            .Append(new RivalsWeekRow(uow.ClubId, record.WeekDivision, record.WeeklyScore, record.WeeklyGames))
            .ToList();

    /// <summary>
    /// One reward option as FIFA's award objects (parser 0x18032b1f0): coins, then packs, then FUT Champions Points. Each
    /// pack shows whether it comes untradeable from <paramref name="source"/> (rules/tradeability.json), as it is granted.
    /// </summary>
    /// <param name="ownTradeability">
    /// True for a reward from the mode reward tables (migration 126): each pack shows its own untradeable choice, which is what
    /// pays. False: rules/tradeability.json decides, as the payout of a reward still in the rules does.
    /// </param>
    public JsonArray RenderOption(RewardOption option, string source, bool ownTradeability = false)
    {
        var awards = new JsonArray();
        if (option.Coins > 0)
        {
            awards.Add(TemplateLibrary.Core.Object("awards/competition-coins", new TemplateArgs { ["coins"] = option.Coins }));
        }
        foreach (var pack in option.Packs)
        {
            var award = _fut.Awards.Pack(pack.PackId, pack.Count);
            var untradeable = ownTradeability ? pack.Untradeable : Game.Rules.Tradeability.Untradeable(source, pack.PackId);
            award["untradeable"] = untradeable;
            award["isUntradeable"] = untradeable;
            awards.Add(award);
        }
        if (option.FcPoints > 0)
        {
            awards.Add(TemplateLibrary.Core.Object("awards/fc-points", new TemplateArgs { ["points"] = option.FcPoints }));
        }
        return awards;
    }

    /// <summary>The hub's prizes.prizeTiers for one division, best first (tierLevel 1 is Rank I).</summary>
    private JsonArray PrizeTiers(int division, IReadOnlyList<long> thresholds, Func<int, IReadOnlyList<RewardOption>> optionsFor)
    {
        var bands = Rewards.RankBands();
        return new JsonArray(RivalsRewardRules.TierRanges(thresholds).Select(range =>
        {
            var options = optionsFor(range.TierLevel);
            var categories = new JsonArray(options.Select((option, index) => (JsonNode?)new JsonObject
            {
                ["categoryId"] = index + 1,
                ["awards"] = RenderOption(option, RewardSources.RivalsOption(index, options.Count), ownTradeability: true),
            }).ToArray());
            return (JsonNode?)TemplateLibrary.Core.Object("seasons/rivals-prize-tier", new TemplateArgs
            {
                ["tierLevel"] = range.TierLevel,
                ["percentFrom"] = bands[range.TierLevel - 1].Start,
                ["percentTo"] = bands[range.TierLevel - 1].End,
                ["pointsFrom"] = range.StartValue,
                ["pointsTo"] = range.EndValue,
                ["categories"] = categories,
            });
        }).ToArray());
    }

    private IReadOnlyList<RewardOption> ConfiguredOptions(int division, int rank) =>
        Rewards.Divisions[Math.Clamp(division, 1, RivalsRewardRules.DivisionCount)][rank - 1];

    private static JsonObject Prizes(long week, JsonArray tiers) => new() { ["competitionId"] = week, ["prizeTiers"] = tiers };

    /// <summary>
    /// The hub's reward fields. While a settled week is unclaimed, prizes is that week's list: FIFA claims from the tier
    /// whose score range holds prizesRivalPoints (0x180279da0). Otherwise it is the live week's.
    /// </summary>
    public async ValueTask<RivalsPrizeView> PrizeViewAsync(IUnitOfWork uow, RivalsRecord record) =>
        await PrizeViewAsync(uow, record, await PendingPrizeAsync(uow));

    private async ValueTask<RivalsPrizeView> PrizeViewAsync(IUnitOfWork uow, RivalsRecord record,
        (RivalsWeekResult Result, long AwardTime, JsonObject Divisions)? found)
    {
        var week = WeekIndex(Game.NowSeconds);
        var rows = await LiveRowsAsync(uow, week, record);
        var live = Rewards.WeekTable(rows, record.WeekDivision);
        var liveRank = RivalsRewardRules.RankForScore(live.Thresholds, record.WeeklyScore);
        if (found is { } pending)
        {
            var result = pending.Result;
            var tiers = PrizeTiers(result.Division, Thresholds(pending.Divisions, result.Division),
                rank => rank == result.Rank ? result.Options : ConfiguredOptions(result.Division, rank));
            return new RivalsPrizeView(true, result.Score, result.Division, pending.AwardTime, Prizes(result.Week, tiers), liveRank, true);
        }
        var liveTiers = PrizeTiers(record.WeekDivision, live.Thresholds, rank => ConfiguredOptions(record.WeekDivision, rank));
        return new RivalsPrizeView(false, record.WeeklyScore, record.WeekDivision, AwardTime(week), Prizes(week, liveTiers), liveRank, PreviousAvailable(week));
    }

    /// <summary>
    /// PUT rivals/reward/{categoryId}: credits the chosen option of the pending week. FIFA adds the listed coins and packs
    /// to its own counters (0x18017b820), so exactly what is listed is credited. The week is marked claimed first, so a
    /// failure can lose a reward but never pay it twice.
    /// </summary>
    public async ValueTask<JsonObject> ClaimRewardAsync(IUnitOfWork uow, int categoryId)
    {
        await uow.LockClubAsync();
        var record = await ProgressAsync(uow);
        if (await PendingPrizeAsync(uow) is not { } pending || !await uow.ClaimRivalsResultAsync(pending.Result.Week))
        {
            return TemplateLibrary.Core.Object("seasons/rivals-claim", new TemplateArgs { ["awards"] = new JsonArray() });
        }
        var options = pending.Result.Options;
        var optionIndex = categoryId >= 1 && categoryId <= options.Count ? categoryId - 1 : 0;
        var option = options.ElementAtOrDefault(optionIndex) ?? new RewardOption();
        // Option A, B or C (or a rank's one fixed reward) is the grant source; each line's own untradeable choice pays (migration
        // 131). A result settled before stored the rivals_rewards.json flag, which matched rules/tradeability.json for every pack.
        var source = RewardSources.RivalsOption(optionIndex, options.Count);
        AddFcPoints(record, option.FcPoints);
        await uow.SaveRivalsRecordAsync(record);
        if (option.Coins > 0)
        {
            await uow.AddCreditsToAsync(uow.ClubId, option.Coins);
        }
        foreach (var pack in option.Packs)
        {
            for (var copy = 0; copy < pack.Count; copy++)
            {
                await uow.AddUnopenedPackAsync(pack.PackId, source, untradeable: pack.Untradeable);
            }
        }
        _log.LogWarning("RIVALS REWARD club={Club} week={Week} division={Division} rank={Rank} score={Score} option={Option} coins={Coins} packs={Packs} fc={Fc}",
            uow.ClubId, pending.Result.Week, pending.Result.Division, pending.Result.Rank, pending.Result.Score, categoryId, option.Coins,
            string.Join(",", option.Packs.Select(pack => $"{pack.PackId}x{pack.Count}{(pack.Untradeable ? "u" : "")}")), option.FcPoints);
        return TemplateLibrary.Core.Object("seasons/rivals-claim", new TemplateArgs { ["awards"] = RenderOption(option, source, ownTradeability: true) });
    }

    /// <summary>GET rivals/user/leaderboard/previous: last week's rank, score and rewards.</summary>
    public async ValueTask<JsonObject> PreviousWeekAsync(IUnitOfWork uow)
    {
        var week = WeekIndex(Game.NowSeconds) - 1;
        if (await SettledAsync(uow, week, compute: true) is not { } settled)
        {
            return TemplateLibrary.Core.Object("seasons/rivals-previous", new TemplateArgs
            {
                ["tierLevel"] = 0,
                ["rivalPoints"] = 0,
                ["prizes"] = new JsonObject { ["prizeTiers"] = new JsonArray() },
            });
        }
        var record = await ProgressAsync(uow);
        var result = await uow.RivalsResultAsync(week);
        var division = result?.Division ?? record.WeekDivision;
        return TemplateLibrary.Core.Object("seasons/rivals-previous", new TemplateArgs
        {
            ["tierLevel"] = result?.Rank ?? 0,
            ["rivalPoints"] = result?.Score ?? 0,
            ["prizes"] = Prizes(week, PrizeTiers(division, Thresholds(settled.Divisions, division), rank => ConfiguredOptions(division, rank))),
        });
    }

    /// <summary>(gamesPlayed, maxNumberOfMatches): the placement matches during placement, then the week's matches against the cap.</summary>
    private (long Played, long Max) Counter(RivalsRecord record) =>
        record.Placed ? (record.WeeklyGames, Rules.WeeklyMatches) : (record.PlacementPlayed, Rules.PlacementMatches);

    /// <summary>
    /// Every division, bottom first: FIFA reads division d as divisionsList[count - d] (CardsDLL 0x18027a020), by position.
    /// The badge splits localizedName on "_" (plain "Division 10" drew nothing); Division 1 has no upper line, so its
    /// ceiling is the Skill Rating from which matchmaking treats everyone alike.
    /// </summary>
    private JsonArray DivisionsList() => new(Enumerable.Range(1, Rules.DivisionCount).Reverse().Select(division =>
    {
        var floor = Rules.Floor(division);
        return (JsonNode?)TemplateLibrary.Core.Object("seasons/rivals-division", new TemplateArgs
        {
            ["divisionId"] = division,
            ["localizedName"] = "FUT_" + division.ToString(CultureInfo.InvariantCulture),
            ["floor"] = floor,
            ["ceiling"] = Rules.Ceiling(division) ?? Rules.TopSkillThreshold,
            ["relegationBuffer"] = Rules.RelegationLine(division) is { } line ? floor - line : 0,
            ["pointsForWin"] = Rules.FcPoints(division, "win"),
        });
    }).ToArray());

    /// <summary>GET rivals/user/hub.</summary>
    public async ValueTask<JsonObject> HubAsync(IUnitOfWork uow)
    {
        var now = Game.NowSeconds;
        var week = WeekIndex(now);
        var start = Rules.WeekStart(week);
        var end = Rules.WeekStart(week + 1);
        var record = await ProgressAsync(uow);
        var prize = await PrizeViewAsync(uow, record);
        var (played, max) = Counter(record);
        var weeklyLosses = Math.Max(0, record.WeeklyGames - record.WeeklyWins - record.WeeklyDraws);
        return TemplateLibrary.Core.Object("seasons/rivals-hub", new TemplateArgs
        {
            ["weekIndex"] = week,
            ["seasonId"] = record.WeekId ?? week,
            ["weekDivision"] = record.WeekDivision,
            ["division"] = record.Division,
            ["skillRating"] = record.SkillRating,
            ["current"] = now,
            ["start"] = start,
            ["end"] = end,
            ["untilEnd"] = end - now,
            ["gamesPlayed"] = played,
            ["weeklyGames"] = record.WeeklyGames,
            ["weeklyWins"] = record.WeeklyWins,
            ["weeklyDraws"] = record.WeeklyDraws,
            ["weeklyLosses"] = weeklyLosses,
            ["gamesLeft"] = Math.Max(0, Rules.WeeklyMatches - record.WeeklyGames),
            ["weeklyMatches"] = Rules.WeeklyMatches,
            ["maxNumberOfMatches"] = max,
            ["wins"] = record.Wins,
            ["draws"] = record.Draws,
            ["losses"] = record.Losses,
            ["titleWins"] = record.TitleWins,
            ["inPlacement"] = !record.Placed,
            ["placed"] = record.Placed,
            ["placementMatches"] = Rules.PlacementMatches,
            ["placementPlayed"] = record.PlacementPlayed,
            ["fcPoints"] = record.FcPoints,
            ["pointsForWin"] = Rules.FcPoints(record.WeekDivision, "win"),
            ["qualificationPoints"] = Game.Rules.Seasons.WeekendLeague.QualificationPoints,
            // An entry for a Weekend League that has not finished; FIFA sets the same locally on a redeem (0x1801a9b89).
            ["qualified"] = await _fut.WeekendLeague.HoldsEntryAsync(uow),
            ["prizes"] = prize.Prizes,
            ["tierLevel"] = prize.TierLevel,
            ["weeklyScore"] = record.WeeklyScore,
            ["prizesDivision"] = prize.PrizesDivision,
            ["isPrizeAvailable"] = prize.IsPrizeAvailable,
            ["prizeAwardTime"] = prize.PrizeAwardTime,
            ["previousAvailable"] = prize.PreviousAvailable,
            // Divisions tab (element parser 0x1802f4c90); a missing list crashed the tab (tester #27).
            ["divisions"] = DivisionsList(),
            ["prizesRivalPoints"] = prize.PrizesRivalPoints,
            // Read as a string (0x1802f4794): the last results, e.g. "WWDLW".
            ["winForm"] = record.Form,
        });
    }

    /// <summary>
    /// The FUT home hub's Rivals tile (parser 0x18032d480): it announces an unclaimed reward and leads to the choice. Built from
    /// the hub's one read of this club's competition rows (the due weeks settled before it).
    /// </summary>
    internal async ValueTask<JsonObject> HomeTileAsync(IUnitOfWork uow, HubCompetitionState state)
    {
        var now = Game.NowSeconds;
        var week = WeekIndex(now);
        var record = await ProgressAsync(uow, state.Rivals);
        var prize = await PrizeViewAsync(uow, record, state.RivalsUnclaimed);
        var (played, max) = Counter(record);
        var tier = prize.Prizes.Arr("prizeTiers")?.OfType<JsonObject>().FirstOrDefault(entry => entry.Int("tierLevel") == prize.TierLevel);
        return TemplateLibrary.Core.Object("seasons/rivals-tile", new TemplateArgs
        {
            ["gamesPlayed"] = played,
            ["maxNumberOfMatches"] = max,
            ["tierLevel"] = prize.TierLevel,
            ["nextTierLevel"] = prize.TierLevel > 0 ? Math.Max(1, prize.TierLevel - 1) : RivalsRewardRules.Ranks,
            ["tierStartValue"] = tier?.Int("tierStartValue") ?? 0,
            ["tierEndValue"] = tier?.Int("tierEndValue") ?? 0,
            ["weeklyScore"] = record.WeeklyScore,
            ["skillRating"] = record.SkillRating,
            ["division"] = record.Division,
            ["current"] = now,
            ["start"] = Rules.WeekStart(week),
            ["end"] = Rules.WeekStart(week + 1),
            ["prizeAwardTime"] = prize.PrizeAwardTime,
            ["isPrizeAvailable"] = prize.IsPrizeAvailable,
            ["inPlacement"] = !record.Placed,
            ["fcPoints"] = record.FcPoints,
            ["qualificationPoints"] = Game.Rules.Seasons.WeekendLeague.QualificationPoints,
            ["winForm"] = record.Form,
        });
    }

    /// <summary>The DestroyMatch fields of a rated match: rivalsScore carries this match's changes, which FIFA adds to its cached hub.</summary>
    public static void AddMatchEndFields(JsonObject ended, RivalsMatchResult rated)
    {
        // DestroyMatch reads rivalsScore as an object {rivalPoints (+0xe0), skillPoints (+0xd8), weekendLeaguePoints (+0xdc)};
        // the coin screen shows rivalPoints as "N Rivals points earned".
        ended["rivalsScore"] = TemplateLibrary.Core.Object("seasons/rivals-score", new TemplateArgs
        {
            ["rivalPoints"] = rated.ScoreGain,
            ["skillPoints"] = rated.Record.LastDelta,
            ["weekendLeaguePoints"] = rated.FcGain,
        });
        // Promotion or placement coins, already credited: FIFA only displays them (RANKUP_COINS_REWARD / PLACEMENT_COINS_REWARD).
        ended["rivalsDivisionCoinsReward"] = rated.PromotionCoins;
        ended["rivalsOpponentELORating"] = rated.OpponentRating;
    }
}
