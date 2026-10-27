using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>Service-facing per-club progress operations backed by relational tables.</summary>
public static partial class ProgressRepository
{
    /// <summary>Adds Season XP in the season in force.</summary>
    public static ValueTask AddObjectivesXpAsync(this IUnitOfWork work, ObjectiveRules rules, long xp) =>
        work.AddObjectivesXpAsync(rules.SeasonAt(work.NowSeconds).Id, xp);

    public static async ValueTask AddObjectivesXpAsync(this IUnitOfWork work, long season, long xp)
    {
        await work.LockClubAsync();
        var current = await work.ObjectivesAsync(season);
        await work.SaveObjectivesAsync(current with { UserXp = current.UserXp + xp });
    }

    /// <summary>Advances one objective counter and the given season's Season XP in one step.</summary>
    public static async ValueTask BumpObjectiveStatAsync(this IUnitOfWork work, long season, string stat, long amount, long xp)
    {
        await work.LockClubAsync();
        var current = await work.ObjectivesAsync(season);
        var stats = new Dictionary<string, long>(current.Stats, StringComparer.Ordinal);
        stats[stat] = stats.GetValueOrDefault(stat) + Math.Max(0, amount);
        await work.SaveObjectivesAsync(current with { Stats = stats, UserXp = current.UserXp + xp });
    }
}
