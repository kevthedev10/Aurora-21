using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// The panel's Objectives page: rules/objectives.json as published (the catalogue of objective groups, the live list, the
/// Season Pass levels and the XP settings), edited as rules changes of that file and published under Changes, with what the
/// editor needs besides: the reward packs, the stats an objective can track, the next free group and objective ids (never one
/// a club has claimed, migration 143's history included), since when each group counts, and for the seasons (migration 166)
/// the one in force, the next free season id (never one a club has progress under) and how many clubs played each.
/// </summary>
public sealed partial class OperatorPanel
{
    private async Task<(int, object)?> ObjectivesAsync(ApiRequest request)
    {
        if (request.Parts.Length != 1 || request.Method != "GET") return null;
        var (document, claimed, starts, seasons) = await game.UnitOfWork.RunReadAsync(0, async work =>
        {
            var documents = await RuleRows.DocumentsAsync(work);
            var claims = await work.QueryAsync("""
                SELECT scope, max(claim_id) FROM ultimateteam.objective_claims WHERE scope IN ('group', 'objective') GROUP BY scope
                """, row => (Scope: row.GetString(0), Max: row.GetInt64(1)));
            var started = await work.QueryAsync("SELECT group_id, started_at FROM ultimateteam.objective_group_starts ORDER BY group_id",
                row => (Group: row.GetInt64(0), At: new DateTimeOffset(row.GetDateTime(1), TimeSpan.Zero)));
            // Clubs with progress per season: the season each club's progress is stored under, and its season history.
            var played = await work.QueryAsync("""
                SELECT season_id, count(*), COALESCE(sum(user_xp), 0)::bigint FROM (
                    SELECT season_id, user_xp FROM ultimateteam.objectives
                    UNION ALL SELECT season_id, user_xp FROM ultimateteam.objective_season_history) AS progress
                GROUP BY season_id ORDER BY season_id
                """, row => (Season: row.GetInt64(0), Clubs: row.GetInt64(1), Xp: row.GetInt64(2)));
            return (JsonNode.Parse(documents["objectives"])!.AsObject(), claims, started, played);
        }, request.Aborted);
        var rules = game.Rules.Objectives;
        long Max(string scope) => claimed.Where(row => row.Scope == scope).Select(row => row.Max).DefaultIfEmpty(0).Max();
        var objectiveIds = rules.GrindGroups.SelectMany(group => group.Objectives.Select(objective => objective.Id))
            .Concat(rules.Gifts.Select(gift => gift.Objective.Id)).Append(rules.WelcomeGroup.Objective.Id);
        return (200, new
        {
            settingsFile = "objectives",
            rules = document,
            liveGroupIds = rules.LiveGroupIds,
            stats = rules.StatLabels.Keys.Order(StringComparer.Ordinal),
            nextGroupId = Math.Max(rules.GroupIds.Max(), Max("group")) + 1,
            nextObjectiveId = Math.Max(objectiveIds.Max(), Max("objective")) + 1,
            started = starts.Select(start => new { group = start.Group, at = start.At }),
            packs = RewardPackOptions(),
            // What a match condition can name: the modes it counts in, the stats it can track, and those a player restriction
            // applies to (rules/objectives.json requirements).
            conditions = new
            {
                modes = new[] { RewardModes.Rivals, RewardModes.WeekendLeague, RewardModes.SquadBattles, RewardModes.DraftOnline, RewardModes.DraftSinglePlayer },
                stats = ObjectiveMatchRequirements.Stats.Order(StringComparer.Ordinal),
                playerStats = ObjectiveMatchRequirements.PlayerStats.Order(StringComparer.Ordinal),
            },
            seasons = new
            {
                now = DateTimeOffset.FromUnixTimeSeconds(game.NowSeconds),
                current = rules.SeasonAt(game.NowSeconds).Id,
                defaultId = rules.Campaign.Id,
                nextId = rules.Seasons.Select(season => season.Id).Concat(seasons.Select(row => row.Season)).Append(rules.Campaign.Id).Max() + 1,
                played = seasons.Select(row => new { season = row.Season, clubs = row.Clubs, xp = row.Xp }),
            },
        });
    }
}
