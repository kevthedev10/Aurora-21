using System.Collections.Concurrent;
using Aurora21.UltimateTeam.Core.Config;

namespace Aurora21.UltimateTeam.Server.Access;

/// <summary>
/// Each player's launcher friends (Discord accounts), as the launcher sent them with the player's last verified launch
/// (<c>POST /client/launch</c> <c>friends</c>). FIFA's own friends list and invites come from the EA app, so online
/// Friendlies pairing uses these instead: two players are friends only when each one's launcher lists the other. Kept in
/// memory; every launch replaces the player's list.
/// </summary>
public sealed class LauncherFriends
{
    /// <summary>The most friends one launch may name.</summary>
    public const int MaxFriends = 200;

    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _friends = new(StringComparer.Ordinal);

    /// <summary>Records a player's launcher friends: Discord ids, invalid ones and the player itself skipped, at most <see cref="MaxFriends"/>.</summary>
    public void Record(string discordId, IEnumerable<string> friendDiscordIds)
    {
        var player = PlayerIdentity.Of(discordId).Id;
        var friends = friendDiscordIds.Where(PlayerAccess.IsDiscordId).Select(id => PlayerIdentity.Of(id).Id)
            .Where(id => id != player).Distinct(StringComparer.Ordinal).Take(MaxFriends).ToHashSet(StringComparer.Ordinal);
        _friends[player] = friends;
    }

    /// <summary>A player's launcher friends (player ids), mutual or not.</summary>
    public IReadOnlyCollection<string> Of(string playerId) =>
        _friends.TryGetValue(playerId, out var friends) ? friends : Array.Empty<string>();

    /// <summary>Whether each player's launcher lists the other.</summary>
    public bool Mutual(string first, string second) =>
        _friends.TryGetValue(first, out var mine) && mine.Contains(second)
        && _friends.TryGetValue(second, out var theirs) && theirs.Contains(first);
}
