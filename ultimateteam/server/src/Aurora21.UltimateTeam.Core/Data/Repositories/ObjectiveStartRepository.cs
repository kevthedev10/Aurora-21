using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>
/// When objective groups first go live (migration 143): a group's objectives count from then, so each club's current
/// counters for the group's stats are stored as its starting point (<see cref="ObjectivesState.Progress"/>). A group keeps
/// its starting point for good: switching it off and on again, or editing it, continues its progress.
/// </summary>
public static class ObjectiveStartRepository
{
    /// <summary>Starts every live group that has never been live; returns their ids. Runs on publish and at start-up.</summary>
    public static async ValueTask<IReadOnlyList<long>> StartLiveAsync(IUnitOfWork work, ObjectiveRules rules)
    {
        var started = (await work.QueryAsync("SELECT group_id FROM ultimateteam.objective_group_starts", row => row.GetInt64(0))).ToHashSet();
        var fresh = rules.LiveGroupIds.Where(id => !started.Contains(id)).ToList();
        foreach (var group in fresh)
        {
            await work.ExecuteAsync("INSERT INTO ultimateteam.objective_group_starts(group_id) VALUES(@group) ON CONFLICT DO NOTHING", ("group", group));
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.objective_baselines(club_id, group_id, stat_code, value)
                SELECT club_id, @group, stat_code, value FROM ultimateteam.objective_counters WHERE stat_code = ANY(@stats) AND value > 0
                ON CONFLICT DO NOTHING
                """, ("group", group), ("stats", StatsOf(rules, group)));
        }
        return fresh;
    }

    /// <summary>The stats a group's objectives track.</summary>
    private static string[] StatsOf(ObjectiveRules rules, long group) =>
        (group == rules.WelcomeGroup.Id ? [rules.WelcomeGroup.Stat]
            : rules.GrindGroups.FirstOrDefault(entry => entry.Id == group) is { } grind ? grind.Objectives.Select(objective => (string?)objective.Counter)
            : rules.Gifts.Where(gift => gift.Id == group).Select(gift => gift.Stat))
        .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
}
