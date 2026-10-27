using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>A settled Squad Battles match: its outcome (won or lost), whether it counted, DestroyMatch's squadBattlesScore and the difficulty played.</summary>
internal sealed record SquadBattlesMatch(MatchService.MatchOutcome Outcome, bool Counted, JsonObject Score, int GamesCounted, int Difficulty);

/// <summary>
/// The weekly Squad Battles competition as in FIFA 21 retail (Python server f354894, a4fdc85; squad_battles.json
/// <c>competition</c>, rules/sqbt_rewards.json): CreateMatch records the opponent or TOTW and the difficulty, the match end
/// scores it with the retail Battle Points, and each closed week settles once for every club by rank and percentile, each
/// prize claimed once.
/// </summary>
public sealed partial class SquadBattlesService
{
    private SquadBattlesCompetitionRules Competition => Rules.Competition;
    private SquadBattlesRewardRules Rewards => Game.Rules.SquadBattlesRewards;

    private long CurrentWeek => Competition.WeekId(Game.NowSeconds);

    /// <summary>The current weekly competition (Monday 00:00 UTC turnover).</summary>
    internal long CurrentWeekId => CurrentWeek;

    /// <summary>
    /// Remembers the Squad Battles match a CreateMatch names (CardsDLL's writer 0x1802b4ad0 sends sqbtEventId and
    /// sqbtMatchDifficulty with either sqbtOpponentSquadId, a hub slot, or featuredSquadId, the TOTW); any other clears it.
    /// </summary>
    public async ValueTask RecordCreateAsync(IUnitOfWork uow, JsonObject body)
    {
        if (!body.ContainsKey("sqbtEventId"))
        {
            await uow.SetSquadBattlesPendingAsync(null);
            return;
        }
        var difficulty = Competition.ClampDifficulty(JsonValues.Int(body.Get("sqbtMatchDifficulty")));
        var now = Game.NowSeconds;
        if (JsonValues.TryInt(body.Get("featuredSquadId"), out var featured))
        {
            await uow.SetSquadBattlesPendingAsync(new SquadBattlesPending("totw", 0, difficulty, CurrentWeek, now, featured, null, null));
            return;
        }
        var set = await uow.SquadBattlesSetAsync();
        await uow.SetSquadBattlesPendingAsync(new SquadBattlesPending("set", (int)Math.Clamp(JsonValues.Int(body.Get("sqbtOpponentSquadId")), 0, Sets.SquadsPerSet - 1),
            difficulty, CurrentWeek, now, null, set?.Week, set?.Index));
    }

    /// <summary>
    /// Scores the Squad Battles match this club's last CreateMatch named, or null when it was not one. A match is won or lost:
    /// a level score goes to penalties, and a quit is a loss worth no points (EA) that marks the squad played. A community
    /// squad scores once, while fewer than the week's matches have counted; only the current TOTW scores, and a replay only
    /// by what it improves on the week's result. The reply's squadBattlesScore must be this object: DestroyMatch
    /// (0x1802b1e40) reads exactly these six members, and a bare number hung the client at full time.
    /// </summary>
    internal async ValueTask<SquadBattlesMatch?> SettleAsync(IUnitOfWork uow, JsonObject body, bool counted)
    {
        if (await uow.TakeSquadBattlesPendingAsync() is not { } pending || Game.NowSeconds - pending.CreatedAt > Competition.PendingMaxAgeSeconds)
        {
            return null;
        }
        var reason = body.Str("endReason").ToUpperInvariant();
        var (mine, theirs) = ObjectivesService.Goals(body);
        var myPens = body.Obj("myMatchStats")?.Int("penaltyGoals") ?? 0;
        var theirPens = body.Obj("opponentMatchStats")?.Int("penaltyGoals") ?? 0;
        var abandoned = MatchEndFacts.Abandoned(reason);
        var win = !abandoned && (reason is "WIN" or "DNF_WIN" || reason is not ("LOSS" or "DNF_LOSS") && (mine != theirs ? mine > theirs : myPens > theirPens));
        // A won or lost match level on goals went to a shootout; the tile shows its score.
        var shootout = !abandoned && mine == theirs && (myPens != 0 || theirPens != 0);
        var week = await uow.SquadBattlesWeekAsync(CurrentWeek);
        var difficulty = Competition.ClampDifficulty(pending.Difficulty);
        var sameWeek = pending.Week == week.Week;
        var parts = Competition.Zero(difficulty);
        var scores = false;
        string opponent;
        long rating, chemistry;
        JsonObject? previousTotw = null;
        SquadBattlesSet? set = null;
        JsonObject? squad = null;
        string? unmatched = null;
        if (pending.Kind == "totw")
        {
            var totwWeek = Totw.WeekOf(pending.FeaturedSquadId ?? 0);
            var document = totwWeek is { } number ? await TotwDocumentAsync(uow, number) : null;
            if (week.Totw is { } stored && stored.Int("id") == pending.FeaturedSquadId)
            {
                previousTotw = stored;
            }
            scores = counted && sameWeek && document is not null && totwWeek == Game.Rules.Totw.Current;
            opponent = document?.Str("squadName") ?? "TOTW";
            (rating, chemistry) = document is null ? (0, 0) : (DocumentRating(document), document.Int("chemistry"));
        }
        else
        {
            set = await uow.SquadBattlesSetAsync();
            squad = set is not null && set.Week == pending.SetWeek && set.Index == pending.SetIndex
                ? set.Squads.OfType<JsonObject>().FirstOrDefault(entry => Present(entry, "id", -1) == pending.Slot)
                : null;
            bool first;
            if (squad?.Obj("info") is { } info)
            {
                first = squad.Obj("result") is null;
                (opponent, rating, chemistry) = (info.Str("clubName"), info.Int("rating"), info.Int("chemistry"));
            }
            else
            {
                // No frozen set behind this match (it was replaced since): it scores once per slot against the generated squad.
                unmatched = $"{pending.SetWeek}:{pending.SetIndex}:{pending.Slot}";
                first = !week.Played.Contains(unmatched);
                var preset = Rules.Opponents[Math.Min(pending.Slot, Rules.Opponents.Count - 1)];
                (opponent, rating, chemistry) = (preset.ClubName, preset.Rating, preset.Chemistry);
            }
            scores = counted && sameWeek && first && week.Counted(Competition.MaxRegularGames) < Competition.MaxRegularGames;
        }
        long total = 0;
        JsonObject? result = null;
        if (scores)
        {
            if (!abandoned)
            {
                parts = Competition.MatchPoints(win, difficulty, rating, chemistry, body.Obj("myMatchStats") ?? new JsonObject(), mine, theirs);
            }
            total = parts.Final;
            result = new JsonObject
            {
                ["pointsWon"] = total,
                ["score"] = mine,
                ["oppScore"] = theirs,
                ["penaltyScore"] = shootout ? myPens : -1,
                ["oppPenaltyScore"] = shootout ? theirPens : -1,
                ["difficulty"] = difficulty,
            };
            if (previousTotw is not null && total <= previousTotw.Int("pointsWon"))
            {
                // A replay that does not beat this week's result changes nothing.
                scores = false;
                parts = parts with { Final = 0 };
            }
        }
        if (scores)
        {
            if (pending.Kind == "totw")
            {
                result!["id"] = pending.FeaturedSquadId;
                week = week with { Totw = result, SpecialGames = week.SpecialGames + (previousTotw is null ? 1 : 0) };
                if (previousTotw is not null)
                {
                    // Only the improvement is added; the end screen shows it as the bonus.
                    total -= previousTotw.Int("pointsWon");
                    parts = parts with { Final = total };
                }
            }
            else
            {
                week = week with { Games = week.Games + 1, Played = unmatched is null ? week.Played : [.. week.Played.TakeLast(40), unmatched] };
                if (squad is not null)
                {
                    squad["result"] = result;
                    await uow.SaveSquadBattlesSetAsync(set!);
                }
            }
            week = week with { Points = week.Points + total };
            if (previousTotw is null)
            {
                week = win ? week with { Wins = week.Wins + 1 } : week with { Losses = week.Losses + 1 };
            }
            await uow.SaveSquadBattlesWeekAsync(week);
        }
        _log.LogInformation("SQBT MATCH_END kind={Kind} reason={Reason} score={Mine}-{Theirs} win={Win} opponent={Opponent} difficulty={Difficulty} scored={Scored} points={Points} weekPoints={WeekPoints} counted={Counted}/{Max}",
            pending.Kind, reason.Length > 0 ? reason : "UNSET", mine, theirs, win, opponent, difficulty, scores, parts.Final, week.Points,
            week.Counted(Competition.MaxRegularGames), Competition.MaxRegularGames);
        var score = TemplateLibrary.Core.Object("squad-battles/score", new TemplateArgs
        {
            ["final"] = parts.Final,
            ["result"] = parts.MatchResult,
            ["teamRating"] = parts.TeamRating,
            ["skillRating"] = parts.Skill,
            ["goals"] = parts.Goals,
            ["modifier"] = parts.Modifier,
        });
        return new SquadBattlesMatch(win ? MatchService.MatchOutcome.Win : MatchService.MatchOutcome.Loss, counted, score, week.Counted(Competition.MaxRegularGames), difficulty);
    }

    // ---- standings and prizes ------------------------------------------------------

    /// <summary>
    /// Everyone's live position and tier in the week, and this club's own: other clubs' scores from the shared cache (reused
    /// for standingsCacheSeconds), this club's from its own current week, so its own rank never lags.
    /// </summary>
    private async ValueTask<(List<SquadBattlesStanding> Table, SquadBattlesStanding? Mine, List<SquadBattlesRankedBand> Bands)> StandingAsync(
        IUnitOfWork uow, SquadBattlesWeek week)
    {
        var rows = await Game.Competitions.RowsAsync("squad-battles", week.Week, Competition.StandingsCacheSeconds, () => uow.SquadBattlesWeekRowsAsync(week.Week));
        var cachedOwn = rows.FirstOrDefault(row => row.ClubId == uow.ClubId);
        var own = new SquadBattlesWeekRow(uow.ClubId, cachedOwn?.Name ?? Game.Config.DisplayName, week.Points, week.Wins, week.Games);
        var bands = await BandsAsync(uow, week.Week);
        var table = Rewards.Standings(rows.Where(row => row.ClubId != uow.ClubId).Append(own), bands);
        return (table, table.FirstOrDefault(standing => standing.Row.ClubId == uow.ClubId), bands);
    }

    /// <summary>
    /// A week's Top slots: their places as the Top size (competition.boardSize) set them, as currently published. They do not
    /// change with the number of players (owner, 2026-10-03: band sizing removed, migration 133).
    /// </summary>
    private ValueTask<List<SquadBattlesRankedBand>> BandsAsync(IUnitOfWork uow, long week) => ValueTask.FromResult(Rewards.RankedBands());

    /// <summary>The claim window: the closed weeks whose prizes can still be claimed.</summary>
    internal (long From, long To) PrizeWindow() => (CurrentWeek - Competition.PrizeWeeksBack, CurrentWeek - 1);

    /// <summary>
    /// Settles the two most recent closed weeks settleDelaySeconds after they close (retail paid a few minutes after Monday
    /// 00:00 UTC); only they may still be settled here. No query once they are known settled (<see cref="CompetitionCache"/>).
    /// </summary>
    internal ValueTask EnsureSettledAsync(IUnitOfWork uow)
    {
        var current = CurrentWeek;
        var due = new[] { current - 2, current - 1 }
            .Where(week => week >= Competition.FirstRetailWeek && Game.NowSeconds >= Competition.WeekEnd(week) + Rewards.SettleDelaySeconds);
        return Game.Competitions.EnsureSettledAsync("squad-battles", due, uow.SquadBattlesSettledWeeksAsync, week => SettleWeekAsync(uow, week));
    }

    /// <summary>This club's settled, unclaimed prizes within the claim window, oldest first, the due weeks settled first.</summary>
    private async ValueTask<List<SquadBattlesResult>> UnclaimedAsync(IUnitOfWork uow)
    {
        await EnsureSettledAsync(uow);
        var (from, to) = PrizeWindow();
        return await uow.UnclaimedSquadBattlesResultsAsync(from, to);
    }

    /// <summary>
    /// Settles a closed week for every club: each result with a prize keeps the awards resolved now. The Top-N bands are the ones
    /// frozen at its opening; staff clubs are left out while rules/staff.json says so, their places passing to the clubs below.
    /// </summary>
    private async ValueTask SettleWeekAsync(IUnitOfWork uow, long week)
    {
        var bands = await BandsAsync(uow, week);
        var staff = Game.Rules.Staff.Excludes(StaffRules.SquadBattles, Competition.WeekEnd(week)) ? await uow.StaffClubIdsAsync(Game.Rules.Staff) : null;
        var table = Rewards.Standings(await uow.SquadBattlesWeekRowsAsync(week), bands, staff);
        var ranked = Rewards.RankedActive(table.Count);
        if (!await uow.TryBeginSquadBattlesSettlementAsync(week, table.Count, ranked))
        {
            return;
        }
        var prizes = table.Where(standing => standing.Tier is { Awards.Count: > 0 }).ToList();
        foreach (var standing in prizes)
        {
            await uow.AddSquadBattlesResultAsync(week, standing);
        }
        _log.LogWarning("SQBT SETTLED week={Week} participants={Participants} prizes={Prizes} mode={Mode} rankedTiers={Ranked} bands={Bands} staffClubs={Staff}",
            week, table.Count, prizes.Count, Rewards.Mode, ranked, string.Join(",", bands.Select(band => $"{band.Start}-{band.End}")), staff?.Count ?? 0);
    }

    /// <summary>One configured prize as FIFA's award object (parser 0x18032b1f0).</summary>
    private JsonObject Award(SquadBattlesAward award)
    {
        if (award.Type == "coin")
        {
            return TemplateLibrary.Core.Object("awards/competition-coins", new TemplateArgs { ["coins"] = award.Amount });
        }
        // The pack tile shows the award's own tradeability (migration 133), as the payout grants it.
        var pack = _fut.Awards.Pack(award.PackId, award.Count);
        pack["untradeable"] = award.Untradeable;
        pack["isUntradeable"] = award.Untradeable;
        return pack;
    }

    private JsonArray Awards(IEnumerable<SquadBattlesAward> awards) => new(awards.Select(award => (JsonNode?)Award(award)).ToArray());

    /// <summary>
    /// The hub's prizeTiers, best first (hub parser 0x1802f8590): the week's Top-N rank tiers (LB_RANKING_TYPE, the positions
    /// frozen at its opening) while enough clubs have points, then the points tiers with their percent band and this week's point
    /// range.
    /// </summary>
    private JsonArray PrizeTiers(IReadOnlyList<SquadBattlesStanding> table, IReadOnlyList<SquadBattlesRankedBand> bands)
    {
        var thresholds = Rewards.Thresholds(table);
        long? previous = null;
        var tiers = new JsonArray();
        if (Rewards.RankedActive(table.Count))
        {
            foreach (var band in bands)
            {
                tiers.Add(TemplateLibrary.Core.Object("squad-battles/prize-tier", new TemplateArgs
                {
                    ["tierType"] = "LB_RANKING_TYPE",
                    ["tierLevel"] = 0,
                    ["tierStart"] = band.Start,
                    ["tierEnd"] = band.End,
                    ["startValue"] = 0,
                    ["endValue"] = 0,
                    ["awards"] = Awards(band.Tier.Awards),
                }));
            }
        }
        foreach (var tier in Rewards.PointsTiers)
        {
            var start = thresholds.GetValueOrDefault(tier.Id, 1);
            tiers.Add(TemplateLibrary.Core.Object("squad-battles/prize-tier", new TemplateArgs
            {
                ["tierType"] = "PERCENTILE",
                ["tierLevel"] = tier.TierLevel,
                ["tierStart"] = tier.PercentStart,
                ["tierEnd"] = tier.PercentEnd,
                ["startValue"] = start,
                ["endValue"] = Math.Max(0, previous is { } above ? above - 1 : 0),
                ["awards"] = Awards(tier.Awards),
            }));
            previous = start;
        }
        return tiers;
    }

    /// <summary>
    /// GET /sqbt/user/prize (GetSquadBattlePrize): collects every settled weekly prize. FIFA adds the listed coins and packs
    /// to its own counters (prize screen 0x1800dbfd0), so exactly what is listed is credited; the weeks are marked claimed
    /// first, so a failure can lose a prize but never pay it twice.
    /// </summary>
    public async ValueTask<JsonObject> ClaimPrizeAsync(IUnitOfWork uow)
    {
        await uow.LockClubAsync();
        var unclaimed = await UnclaimedAsync(uow);
        var claimed = unclaimed.Count == 0 ? new HashSet<long>() : await uow.ClaimSquadBattlesResultsAsync(unclaimed.Select(result => result.Week));
        var awarded = new JsonArray();
        foreach (var result in unclaimed.Where(result => claimed.Contains(result.Week)))
        {
            foreach (var award in result.Awards)
            {
                if (award.Type == "coin")
                {
                    await uow.AddCreditsToAsync(uow.ClubId, award.Amount);
                }
                else
                {
                    for (var copy = 0; copy < award.Count; copy++)
                    {
                        // The award's own untradeable choice pays (migration 133); results settled before stored none and paid
                        // their packs tradeable (rules/tradeability.json's squad_battles switch).
                        await uow.AddUnopenedPackAsync(award.PackId, RewardSources.SquadBattles, untradeable: award.Untradeable);
                    }
                }
            }
            awarded.Add(TemplateLibrary.Core.Object("squad-battles/awarded-prize", new TemplateArgs
            {
                ["week"] = result.Week,
                ["rank"] = result.Rank,
                ["level"] = result.TierLevel,
                ["awards"] = Awards(result.Awards),
            }));
            _log.LogWarning("SQBT PRIZE club={Club} week={Week} position={Position} rank={Rank} tier={Tier} tierLevel={Level} points={Points}",
                uow.ClubId, result.Week, result.Position, result.Rank, result.TierId, result.TierLevel, result.Points);
        }
        return TemplateLibrary.Core.Object("squad-battles/prize", new TemplateArgs { ["awarded"] = awarded });
    }

    // ---- documents ------------------------------------------------------------------

    /// <summary>GET /sqbt/user/hub: the week, the four tiles, the rank and the prize tiers.</summary>
    public async ValueTask<JsonObject> HubAsync(IUnitOfWork uow)
    {
        var now = Game.NowSeconds;
        var week = await uow.SquadBattlesWeekAsync(CurrentWeek);
        var set = await CurrentSetAsync(uow, week.Week);
        var (table, mine, bands) = await StandingAsync(uow, week);
        return TemplateLibrary.Core.Object("squad-battles/hub", new TemplateArgs
        {
            ["week"] = week.Week,
            ["current"] = now,
            ["start"] = Competition.WeekStart(week.Week),
            ["end"] = Competition.WeekEnd(week.Week),
            ["counted"] = week.Counted(Competition.MaxRegularGames),
            ["maxGames"] = Competition.MaxRegularGames,
            // Read only by the Rivals placement tile.
            ["games"] = week.Games,
            ["points"] = week.Points,
            // Any rank above 0 makes FIFA show "ranked #N" instead of the tier name, so it is set only inside a Top-N tier.
            ["rank"] = mine?.Rank ?? 0,
            ["tierLevel"] = mine?.TierLevel ?? 0,
            ["resultWin"] = Competition.MatchResultPoints["win"],
            ["resultLoss"] = Competition.MatchResultPoints["loss"],
            ["winModifiers"] = new JsonArray(Competition.WinModifiers.Select(value => (JsonNode?)value).ToArray()),
            ["lossModifiers"] = new JsonArray(Competition.LossModifiers.Select(value => (JsonNode?)value).ToArray()),
            ["leaderboardRefresh"] = now + Competition.LeaderboardRefreshSeconds,
            ["prizeAvailable"] = (await UnclaimedAsync(uow)).Count > 0,
            ["prizeTiers"] = PrizeTiers(table, bands),
            ["refreshAt"] = RefreshAt(set),
            ["opponents"] = Summaries(set),
        });
    }

    /// <summary>The sqbtEvent block of the FUT home hub (sub-parser 0x18032d260): it never draws a set.</summary>
    internal async ValueTask<JsonObject> HomeEventAsync(IUnitOfWork uow, HubCompetitionState state)
    {
        var now = Game.NowSeconds;
        var week = state.SquadBattles;
        var (_, mine, _) = await StandingAsync(uow, week);
        return TemplateLibrary.Core.Object("squad-battles/home-event", new TemplateArgs
        {
            ["week"] = week.Week,
            ["points"] = week.Points,
            ["rank"] = mine?.Rank ?? 0,
            ["tierLevel"] = mine?.TierLevel ?? 0,
            ["remaining"] = Math.Max(0, Competition.MaxRegularGames - week.Counted(Competition.MaxRegularGames)),
            ["current"] = now,
            ["refreshAt"] = state.SquadBattlesRefreshAt ?? now,
            ["start"] = Competition.WeekStart(week.Week),
            ["end"] = Competition.WeekEnd(week.Week),
            ["prizeAvailable"] = state.SquadBattlesPrize,
        });
    }

    /// <summary>The Squad Battles "Top 200" for this week, or last week's (<paramref name="previous"/>): points, then wins.</summary>
    public async ValueTask<JsonObject> LeaderboardAsync(IUnitOfWork uow, bool previous)
    {
        var row = Game.Rules.Seasons.Leaderboards.Row;
        var board = SquadBattlesRewardRules.Order(await uow.SquadBattlesWeekRowsAsync(CurrentWeek - (previous ? 1 : 0))).Take(Competition.BoardSize);
        return new JsonObject
        {
            ["entries"] = new JsonArray(board.Select((entry, index) => (JsonNode?)TemplateLibrary.Core.Object("seasons/leaderboard-row", new TemplateArgs
            {
                ["rank"] = index + 1,
                // Plain ASCII only, as on the Rivals leaderboards (Python server 81c770e).
                ["name"] = PlayerNames.Readable(entry.Name) ?? Game.Rules.Seasons.Leaderboards.DefaultName,
                ["badge"] = row.Badge,
                ["est"] = row.Est,
                ["sku"] = Competition.BoardSku,
                ["scoreIcon"] = "points",
                ["score"] = entry.Points,
                ["tiebreakIcon"] = "wins",
                ["wins"] = entry.Wins,
            })).ToArray()),
        };
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
