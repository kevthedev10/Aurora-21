using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>Opens and deletes clubs in the relational schema using the shared connection pool.</summary>
public static class ClubOpen
{
    private const long OpenGateKey = 0;

    public static Task<ClubAccount> EnsureClubAsync(ClubUnitOfWorkRunner runner, ClubIdentity identity, long initialCredits,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(OpenGateKey, "open-club", uow => uow.EnsureClubAsync(identity, initialCredits), cancellationToken);

    /// <summary>
    /// Reads another club's data in this unit's transaction (every <c>@club</c> statement binds <paramref name="clubId"/>
    /// meanwhile), then switches back. A nested unit is refused: it would wait on its own club gate.
    /// </summary>
    public static async ValueTask<T> ReadOtherClubAsync<T>(this IUnitOfWork work, long clubId, Func<IUnitOfWork, ValueTask<T>> read)
    {
        var unit = work as UnitOfWork ?? throw new InvalidOperationException("Reading another club needs a relational unit of work.");
        var own = unit.ClubId;
        unit.UseClub(clubId);
        try
        {
            return await read(unit);
        }
        finally
        {
            unit.UseClub(own);
        }
    }

    public static Task<bool> DeleteClubAsync(ClubUnitOfWorkRunner runner, long personaId, CancellationToken cancellationToken = default) =>
        runner.RunRetryableAsync(OpenGateKey, "delete-club", async uow =>
        {
            if (await uow.ClubIdOfPersonaAsync(personaId) is not { } clubId)
            {
                return false;
            }
            uow.UseClub(clubId);
            return await uow.DeleteClubAsync();
        }, cancellationToken);
}
