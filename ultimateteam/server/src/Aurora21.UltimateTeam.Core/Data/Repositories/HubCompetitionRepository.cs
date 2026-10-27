using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>The weeks and events the FUT home hub's competition tiles read this club's rows for.</summary>
public sealed record HubCompetitionWindows(long RivalsFrom, long RivalsTo, long WeekendLeagueEvent, long WeekendLeagueFrom, long WeekendLeagueTo,
    long SquadBattlesWeek, long SquadBattlesFrom, long SquadBattlesTo, long SquadBattlesRefreshSeconds);

/// <summary>
/// Everything this club's Division Rivals, Weekend League and Squad Battles home tiles read, in one query: the Rivals record
/// and oldest unclaimed weekly reward, the Weekend League entries, record and unclaimed results, and the Squad Battles week,
/// refresh time and whether a prize waits.
/// </summary>
public sealed record HubCompetitionState(RivalsRecord? Rivals, (RivalsWeekResult Result, long AwardTime, JsonObject Divisions)? RivalsUnclaimed,
    List<long> WeekendLeagueEntries, WeekendLeagueRecord WeekendLeague, List<long> WeekendLeagueUnclaimed,
    SquadBattlesWeek SquadBattles, long? SquadBattlesRefreshAt, bool SquadBattlesPrize);

public static class HubCompetitionRepository
{
    public static async ValueTask<HubCompetitionState> HubCompetitionStateAsync(this IUnitOfWork work, HubCompetitionWindows windows) =>
        (await work.QueryAsync($"""
            -- query: hub.competitions
            SELECT p.*,
                   (SELECT coalesce(array_agg(event_id ORDER BY event_id), '{"{}"}') FROM ultimateteam.weekend_league_entries WHERE club_id = @club),
                   wl.*, sw.*,
                   (SELECT least(next_refresh_at, created_at + @sqbtRefresh) FROM ultimateteam.squad_battles_sets WHERE club_id = @club AND week_id = @sqbtWeek),
                   ru.*,
                   (SELECT coalesce(array_agg(event_id ORDER BY event_id), '{"{}"}') FROM ultimateteam.champion_results
                    WHERE club_id = @club AND claimed_at IS NULL AND event_id BETWEEN @wlFrom AND @wlTo),
                   EXISTS(SELECT 1 FROM ultimateteam.squad_battles_results
                          WHERE club_id = @club AND claimed_at IS NULL AND week_id BETWEEN @sqbtFrom AND @sqbtTo)
            FROM (SELECT 1) one
            LEFT JOIN LATERAL (SELECT true AS present, {RivalsRepository.Columns} FROM ultimateteam.rivals_progress WHERE club_id = @club) p ON true
            LEFT JOIN LATERAL (SELECT games, wins, draws, losses, form FROM ultimateteam.weekend_league_records
                               WHERE club_id = @club AND event_id = @wlEvent) wl ON true
            LEFT JOIN LATERAL (SELECT points, games, skipped, special_games, wins, draws, losses, played, totw::text FROM ultimateteam.squad_battles_weeks
                               WHERE club_id = @club AND week_id = @sqbtWeek) sw ON true
            LEFT JOIN LATERAL (SELECT r.week_id, r.division, r.rank, r.score, r.options::text, s.award_time, s.divisions::text
                               FROM ultimateteam.rivals_week_results r JOIN ultimateteam.rivals_week_settlements s ON s.week_id = r.week_id
                               WHERE r.club_id = @club AND r.claimed_at IS NULL AND r.week_id BETWEEN @rivalsFrom AND @rivalsTo
                               ORDER BY r.week_id LIMIT 1) ru ON true
            """,
            reader =>
            {
                const int Entries = 26, WeekendLeague = 27, SquadBattles = 32, Refresh = 41, Unclaimed = 42, ChampionUnclaimed = 49, SquadBattlesPrize = 50;
                var rivals = reader.IsDBNull(0) ? null : RivalsRepository.ReadRecord(reader, 1);
                var weekendLeague = reader.IsDBNull(WeekendLeague) ? new WeekendLeagueRecord(windows.WeekendLeagueEvent, 0, 0, 0, 0, 0)
                    : new WeekendLeagueRecord(windows.WeekendLeagueEvent, reader.GetInt64(WeekendLeague), reader.GetInt64(WeekendLeague + 1),
                        reader.GetInt64(WeekendLeague + 2), reader.GetInt64(WeekendLeague + 3), reader.GetInt64(WeekendLeague + 4));
                var squadBattles = reader.IsDBNull(SquadBattles) ? SquadBattlesWeek.Blank(windows.SquadBattlesWeek)
                    : new SquadBattlesWeek(windows.SquadBattlesWeek, reader.GetInt64(SquadBattles), reader.GetInt32(SquadBattles + 1), reader.GetInt32(SquadBattles + 2),
                        reader.GetInt32(SquadBattles + 3), reader.GetInt32(SquadBattles + 4), reader.GetInt32(SquadBattles + 5), reader.GetInt32(SquadBattles + 6),
                        reader.GetFieldValue<string[]>(SquadBattles + 7), reader.IsDBNull(SquadBattles + 8) ? null : FutJson.ParseObject(reader.GetString(SquadBattles + 8)));
                (RivalsWeekResult, long, JsonObject)? unclaimed = reader.IsDBNull(Unclaimed) ? null
                    : (new RivalsWeekResult(reader.GetInt64(Unclaimed), reader.GetInt32(Unclaimed + 1), reader.GetInt32(Unclaimed + 2), reader.GetInt64(Unclaimed + 3),
                        RivalsRepository.Options(reader.GetString(Unclaimed + 4))), reader.GetInt64(Unclaimed + 5),
                        FutJson.ParseObject(reader.GetString(Unclaimed + 6)) ?? new JsonObject());
                return new HubCompetitionState(rivals, unclaimed, reader.GetFieldValue<long[]>(Entries).ToList(), weekendLeague,
                    reader.GetFieldValue<long[]>(ChampionUnclaimed).ToList(), squadBattles, NullableLong(reader, Refresh), reader.GetBoolean(SquadBattlesPrize));
            },
            ("rivalsFrom", windows.RivalsFrom), ("rivalsTo", windows.RivalsTo), ("wlEvent", windows.WeekendLeagueEvent), ("wlFrom", windows.WeekendLeagueFrom),
            ("wlTo", windows.WeekendLeagueTo), ("sqbtWeek", windows.SquadBattlesWeek), ("sqbtFrom", windows.SquadBattlesFrom), ("sqbtTo", windows.SquadBattlesTo),
            ("sqbtRefresh", windows.SquadBattlesRefreshSeconds)))
        .Single();
}
