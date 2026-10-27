using Aurora21.UltimateTeam.Core.Data;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Access;

/// <summary>
/// Records verified Blaze sessions in <c>player_sessions</c> (migration 063) for the operator panel: logins, recent
/// activity and each player's session list. Writing never delays or fails a game connection: both ends are written in
/// the background, and a failure is logged and dropped. Rows still open when the server stops are closed at the next start.
/// </summary>
public sealed class PlayerSessionLog(ClubUnitOfWorkRunner database, ILogger log)
{
    /// <summary>Closes sessions a previous run left open (it stopped without recording their end).</summary>
    public async Task CloseStaleAsync(CancellationToken cancellationToken)
    {
        var closed = await database.RunAsync(0, "player-sessions-close-stale", work => work.ExecuteAsync(
            "UPDATE ultimateteam.player_sessions SET disconnected_at=now() WHERE disconnected_at IS NULL"), cancellationToken);
        if (closed > 0) log.LogInformation("Closed {Count} player session(s) left open by the previous run", closed);
    }

    /// <summary>Records an opened session in the background; the task yields its row id, or null when it failed.</summary>
    public Task<long?> OpenAsync(string discordId, string? remote) => Task.Run(async () =>
    {
        try
        {
            return (long?)await database.RunAsync(0, "player-session-open", work => work.ScalarAsync<long>("""
                INSERT INTO ultimateteam.player_sessions(discord_id,remote) VALUES(@id,@remote::text) RETURNING id
                """, ("id", discordId), ("remote", remote)));
        }
        catch (Exception error)
        {
            log.LogWarning("Could not record a player session: {Error}", error.Message);
            return null;
        }
    });

    /// <summary>Records the end of the session <paramref name="opened"/> started, once its row exists.</summary>
    public void Close(Task<long?> opened) => _ = Task.Run(async () =>
    {
        try
        {
            if (await opened is not { } id) return;
            await database.RunAsync(0, "player-session-close", work => work.ExecuteAsync(
                "UPDATE ultimateteam.player_sessions SET disconnected_at=now() WHERE id=@id AND disconnected_at IS NULL", ("id", id)));
        }
        catch (Exception error)
        {
            log.LogWarning("Could not record a player session's end: {Error}", error.Message);
        }
    });
}
