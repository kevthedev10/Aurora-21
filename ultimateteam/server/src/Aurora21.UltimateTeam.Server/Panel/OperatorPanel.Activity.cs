using System.Text;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Logs and Matchmaking. Logs are the server's own files (Aurora 21 writes them to its
/// runtime logs folder, not captures/). FIFA 21 Modes is implemented in OperatorPanel.Modes.cs;
/// Aurora 16's Tournaments tab (FIFA 16 cups) is left out.
/// </summary>
public sealed partial class OperatorPanel
{
    private const int MaximumLogLines = 2000;
    private const long MaximumLogTailBytes = 4 * 1024 * 1024;

    private async Task<(int, object)?> LogsAsync(ApiRequest request)
    {
        if (request.Method != "GET") throw new PanelError(405, "method not allowed");
        var files = options.LogsDirectory is null || !Directory.Exists(options.LogsDirectory) ? [] : new DirectoryInfo(options.LogsDirectory)
            .EnumerateFiles("*.log*").Where(file => SafeName(file.Name)).OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        if (request.Parts.Length == 1)
        {
            return (200, new
            {
                pinned = files.Where(file => file.Name == "ultimateteam.log").Select(file => file.Name).ToArray(),
                files = files.Select(file => new { name = file.Name, size = file.Length, mtime = Iso(file.LastWriteTimeUtc) }),
            });
        }
        if (request.Parts.Length != 2) return null;
        var name = Uri.UnescapeDataString(request.Parts[1]);
        var file = files.FirstOrDefault(candidate => candidate.Name == name) ?? throw new PanelError(404, "No such log.");
        var lines = QueryInt(request, "lines", 200, 1, MaximumLogLines);
        var query = request.Query("q");
        if (query.Length > 200) throw new PanelError(400, "The filter is too long.");
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - MaximumLogTailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(request.Aborted);
        var all = text.Split('\n');
        var matching = (start > 0 ? all.Skip(1) : all).Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && (query.Length == 0 || line.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        return (200, new
        {
            name = file.Name,
            size = file.Length,
            mtime = Iso(file.LastWriteTimeUtc),
            truncated = start > 0,
            lines = matching.Skip(Math.Max(0, matching.Count - lines)).ToArray(),
        });
    }

    private static bool SafeName(string name) => name.Length <= 128 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_');

    private object Matchmaking()
    {
        var online = issuer.Presence?.Online() ?? [];
        var lobby = matchmaking?.PanelSnapshot();
        var entries = lobby?.Players.ToDictionary(player => player.PlayerId, StringComparer.Ordinal) ?? [];
        var searching = entries.Values.Where(player => player.State == "searching").ToArray();
        var matches = entries.Values.Where(player => player.State == "match").ToArray();
        var modes = searching.Select(player => player.Mode).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var presence = online.ToDictionary(player => player.PlayerId, StringComparer.Ordinal);
        var ids = online.OrderBy(player => player.Since).Select(player => player.PlayerId).Concat(entries.Keys).Distinct(StringComparer.Ordinal).ToArray();
        return new
        {
            snapshot = new { current = lobby is not null && issuer.Presence is not null, updatedAt = Iso(DateTimeOffset.UtcNow), ageMs = 0,
                available = lobby is not null, accepting = lobby?.Accepting,
                reason = lobby is null ? "The matchmaking registry is not available." : issuer.Presence is null ? "Player presence is not available." : null },
            counts = new { searching = searching.Length, match = matches.Length, online = online.Count, total = ids.Length,
                preMatch = matches.Count(player => player.GameState is GameState.Initializing or GameState.ConnectionVerification or GameState.PreGame),
                inGame = matches.Count(player => player.GameState == GameState.InGame), postMatch = matches.Count(player => player.GameState == GameState.PostGame),
                games = matches.Select(player => player.GameId).Distinct().Count() },
            splitQueue = modes.Length > 1,
            modes,
            players = ids.Select(id =>
            {
                var entry = entries.GetValueOrDefault(id);
                return new
                {
                    name = issuer.Access.Find(id)?.DisplayName ?? $"persona {PlayerIdentity.Of(id).PersonaId}",
                    state = entry?.State ?? "online", mode = entry?.Mode, discordId = id, blazeId = PlayerIdentity.Of(id).PersonaId,
                    connected = presence.ContainsKey(id), connectedSince = presence.TryGetValue(id, out var connection) ? Iso(connection.Since) : null,
                    sessionId = entry?.SessionId, gameId = entry?.GameId, gameState = entry?.GameState, queuedForMs = entry?.QueuedForMs,
                    phase = entry?.GameState switch { GameState.Initializing or GameState.ConnectionVerification or GameState.PreGame => "pre_match",
                        GameState.InGame => "in_match", GameState.PostGame => "post_match", _ => (string?)null },
                };
            }),
        };
    }
}
