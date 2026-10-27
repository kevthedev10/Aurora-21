using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>Per-club FIFA 21 competition state and deliberate progress resets.</summary>
public sealed partial class OperatorPanel
{
    private async Task<(int, object)?> ModesAsync(ApiRequest request)
    {
        var parts = request.Parts;
        if (parts.Length == 1 && request.Method == "GET")
        {
            var clubs = await ClubsAsync(null, request.Aborted);
            return (200, new { players = clubs.Select(club => new
            {
                id = club.PersonaId, name = PlayerName(club.PersonaId, club.DisplayName),
                clubName = club.DisplayName,
            }).ToArray() });
        }
        if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var persona) || persona <= 0)
            throw new PanelError(400, "Choose a club by persona id.");
        var club = (await ClubsAsync(persona, request.Aborted)).SingleOrDefault() ?? throw new PanelError(404, "That club does not exist.");
        if (parts.Length == 2 && request.Method == "GET") return (200, await ModeStateAsync(club, request.Aborted));
        if (parts.Length == 4 && parts[3] == "reset" && request.Method == "POST")
        {
            if (request.Body.ValueKind != JsonValueKind.Object || !request.Body.TryGetProperty("confirm", out var confirmation) ||
                confirmation.ValueKind != JsonValueKind.True)
                throw new PanelError(400, "Confirm the mode reset explicitly.");
            var mode = parts[2];
            if (mode is not ("rivals" or "squad-battles" or "champions" or "draft"))
                throw new PanelError(404, "Unknown FIFA 21 mode.");
            var discord = issuer.Access.DiscordByPersona().GetValueOrDefault(persona);
            if (discord is not null && (issuer.Presence?.IsOnline(discord) == true || matchmaking?.IsInMatch(discord) == true ||
                                        matchmaking?.IsSearching(discord) == true))
                throw new PanelError(409, "The player must leave FIFA 21 before resetting competition progress.");
            var squadWeek = game.Rules.SquadBattles.Competition.WeekId(game.NowSeconds);
            var championEvent = game.Rules.Seasons.WeekendLeague.Slot(game.NowSeconds).Id;
            await game.UnitOfWork.RunAsync(club.ClubId, "panel-mode-reset", async work =>
            {
                switch (mode)
                {
                    case "rivals":
                        await work.ExecuteAsync("DELETE FROM ultimateteam.rivals_pending WHERE club_id=@club");
                        await work.ExecuteAsync("DELETE FROM ultimateteam.rivals_progress WHERE club_id=@club");
                        break;
                    case "squad-battles":
                        await work.ExecuteAsync("DELETE FROM ultimateteam.squad_battles_pending WHERE club_id=@club");
                        await work.ExecuteAsync("DELETE FROM ultimateteam.squad_battles_sets WHERE club_id=@club AND week_id=@week", ("week", squadWeek));
                        await work.ExecuteAsync("DELETE FROM ultimateteam.squad_battles_weeks WHERE club_id=@club AND week_id=@week", ("week", squadWeek));
                        break;
                    case "champions":
                        await work.ExecuteAsync("DELETE FROM ultimateteam.champion_connection_claims WHERE club_id=@club");
                        await work.ExecuteAsync("DELETE FROM ultimateteam.champion_pending WHERE club_id=@club");
                        await work.ExecuteAsync("DELETE FROM ultimateteam.weekend_league_records WHERE club_id=@club AND event_id=@event", ("event", championEvent));
                        await work.ExecuteAsync("DELETE FROM ultimateteam.champions_progress WHERE club_id=@club");
                        // Entry redemptions, registration and settled prizes remain: a reset must not refund points or award twice.
                        break;
                    case "draft":
                        // All four child tables cascade from draft_runs. This also abandons an unfinished paid run.
                        await work.ExecuteAsync("DELETE FROM ultimateteam.draft_runs WHERE club_id=@club");
                        break;
                }
                await work.ExecuteAsync("""
                    INSERT INTO ultimateteam.panel_edits(persona_id,patch,requested_by,result,applied_at)
                    VALUES(@persona,@patch::jsonb,@by,'ok',now())
                    """, ("persona", persona), ("patch", new JsonObject { ["op"] = "resetMode", ["mode"] = mode }.ToJsonString()),
                    ("by", "panel " + request.Remote));
            }, request.Aborted);
            if (mode is "rivals" or "squad-battles" or "champions") game.Competitions.InvalidateRows();
            _log.LogWarning("PANEL reset {Mode} progress for persona {Persona}", mode, persona);
            return (200, await ModeStateAsync(club, request.Aborted));
        }
        throw new PanelError(405, "Unknown mode operation or method.");
    }

    private async Task<object> ModeStateAsync(ClubSummary club, CancellationToken token) => await game.UnitOfWork.RunReadAsync(club.ClubId, async work =>
    {
        var squadWeek = game.Rules.SquadBattles.Competition.WeekId(game.NowSeconds);
        var championEvent = game.Rules.Seasons.WeekendLeague.Slot(game.NowSeconds).Id;
        var rivals = (await work.QueryAsync("""
            SELECT placed,coalesce(skill_rating,0),coalesce(division,10),wins,draws,losses,weekly_score,
                   weekly_games,fc_points,form,season_id
            FROM ultimateteam.rivals_progress WHERE club_id=@club
            """, row => (object)new
            {
                placed = row.GetBoolean(0), skillRating = row.GetInt64(1), division = row.GetInt64(2),
                wins = row.GetInt64(3), draws = row.GetInt64(4), losses = row.GetInt64(5),
                weeklyScore = row.GetInt64(6), weeklyGames = row.GetInt64(7), fcPoints = row.GetInt64(8),
                form = row.GetString(9), week = row.IsDBNull(10) ? (long?)null : row.GetInt64(10),
            })).FirstOrDefault();
        var squadBattles = (await work.QueryAsync("""
            SELECT week_id,points,games,skipped,special_games,wins,draws,losses
            FROM ultimateteam.squad_battles_weeks WHERE club_id=@club AND week_id=@week
            """, row => (object)new
            {
                week = row.GetInt64(0), points = row.GetInt64(1), games = row.GetInt32(2), skipped = row.GetInt32(3),
                specialGames = row.GetInt32(4), wins = row.GetInt32(5), draws = row.GetInt32(6), losses = row.GetInt32(7),
            }, ("week", squadWeek))).FirstOrDefault();
        var champions = (await work.QueryAsync("""
            SELECT event_id,games,wins,draws,losses,form FROM ultimateteam.weekend_league_records
            WHERE club_id=@club AND event_id=@event
            """, row => (object)new
            {
                eventId = row.GetInt64(0), games = row.GetInt64(1), wins = row.GetInt64(2),
                draws = row.GetInt64(3), losses = row.GetInt64(4), form = row.GetInt64(5),
            }, ("event", championEvent))).FirstOrDefault();
        var entries = await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.weekend_league_entries WHERE club_id=@club");
        var draft = await work.QueryAsync("""
            SELECT slot,mode,state,formation,wins,losses,drafts_completed,pending_coin_reward
            FROM ultimateteam.draft_runs WHERE club_id=@club ORDER BY slot
            """, row => new
            {
                slot = row.GetInt16(0), mode = row.GetString(1), state = row.GetString(2),
                formation = row.IsDBNull(3) ? null : row.GetString(3), wins = row.GetInt64(4),
                losses = row.GetInt64(5), completed = row.GetInt64(6), pendingCoinReward = row.GetInt64(7),
            });
        var pending = await work.QueryAsync("""
            SELECT (SELECT count(*) FROM ultimateteam.rivals_pending WHERE club_id=@club),
                   (SELECT count(*) FROM ultimateteam.squad_battles_pending WHERE club_id=@club),
                   (SELECT count(*) FROM ultimateteam.champion_pending WHERE club_id=@club)
            """, row => new { rivals = row.GetInt64(0) > 0, squadBattles = row.GetInt64(1) > 0, champions = row.GetInt64(2) > 0 });
        return (object)new
        {
            id = club.PersonaId, name = PlayerName(club.PersonaId, club.DisplayName),
            rivals, squadBattles, champions, championEntries = entries, draft,
            pending = pending.Single(),
            resetPolicy = "Current progress is cleared; settled reward history and spent Weekend League entries remain.",
        };
    }, token);
}
