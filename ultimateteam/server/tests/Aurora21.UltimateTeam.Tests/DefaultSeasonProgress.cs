using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;

namespace Aurora21.UltimateTeam.Tests;

/// <summary>
/// Objective progress in the default season (the bundled campaign id), for tests that schedule no custom season: Season XP
/// and Season Pass claims are kept per season (migration 166).
/// </summary>
public static class DefaultSeasonProgress
{
    public const long DefaultSeason = 1;

    public static ValueTask<ObjectivesState> ObjectivesAsync(this IUnitOfWork work) => work.ObjectivesAsync(DefaultSeason);

    public static ValueTask AddObjectivesXpAsync(this IUnitOfWork work, long xp) => work.AddObjectivesXpAsync(DefaultSeason, xp);

    public static ValueTask BumpObjectiveStatAsync(this IUnitOfWork work, string stat, long amount, long xp) =>
        work.BumpObjectiveStatAsync(DefaultSeason, stat, amount, xp);
}
