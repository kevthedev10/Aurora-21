using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>The completion extras of each eligible completed match: an optional catalogue pack (tradeability fixed at grant) and FIFA Points.</summary>
public sealed class CompletionPackService(FutServices fut)
{
    public async ValueTask<long?> GrantAsync(IUnitOfWork uow, string mode)
    {
        var reward = fut.Game.Rules.Seasons.MatchCompletionRewards[mode];
        if (reward.PackId is not { } packId) return null;
        await uow.AddRelationalUnopenedPackAsync(packId, RewardSources.MatchCompletion, untradeable: reward.Untradeable);
        fut.Game.Logger("UltimateTeam.CompletionPack").LogInformation(
            "COMPLETION PACK granted club={Club} mode={Mode} pack={Pack} untradeable={Untradeable}",
            uow.ClubId, mode, packId, reward.Untradeable);
        return packId;
    }

    /// <summary>The mode's FIFA Points for an eligible completed match, or 0 when it pays none.</summary>
    public async ValueTask<long> GrantPointsAsync(IUnitOfWork uow, string mode)
    {
        var points = fut.Game.Rules.Seasons.MatchCompletionRewards[mode].FifaPoints;
        if (points <= 0) return 0;
        await uow.AddFifaPointsAsync(points);
        fut.Game.Logger("UltimateTeam.CompletionPack").LogInformation("COMPLETION POINTS granted club={Club} mode={Mode} points={Points}", uow.ClubId, mode, points);
        return points;
    }
}
