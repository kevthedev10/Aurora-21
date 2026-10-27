using Aurora21.UltimateTeam.Core.Sessions;

namespace Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

/// <summary>Answers "who is my opponent" from the paired games.</summary>
public sealed class MatchmakingRoster : IMatchRoster
{
    private readonly MatchmakingRegistry _registry;

    public MatchmakingRoster(MatchmakingRegistry registry) => _registry = registry;

    public bool IsSearching(string playerId) => _registry.IsSearching(playerId);

    public TimeSpan? OpponentQuietFirstBy(string playerId, long gameId) => _registry.RelayQuietFirstBy(gameId, playerId);

    public bool OpponentLeftGameFirst(string playerId, long gameId) => _registry.OpponentLeftGameFirst(gameId, playerId);

    public OnlineOpponent? OpponentOf(string playerId)
    {
        if (_registry.GameIdFor(playerId) is not { } gameId)
        {
            return null;
        }
        return _registry.Read(gameId, match =>
        {
            if (match is null || match.Players.Count < 2 || match.Entry(playerId) is null)
            {
                return null;
            }
            var opponent = match.Players.First(player => player.PlayerId != playerId);
            return new OnlineOpponent(match.Host.PlayerId == playerId, opponent.PlayerId, opponent.Client!.PersonaId, match.GameId,
                HouseRules.IsHouseRulesMode(match.Mode));
        });
    }
}
