using System.Net;
using System.Text.Json;
using Aurora21.UltimateTeam.Server.Access;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// A synthetic Discord server for access tests: members and their roles, the server's roles, and a switch that makes
/// Discord unreachable. Never talks to discord.com.
/// </summary>
internal sealed class FakeDiscordApi : IDiscordApi
{
    public const string GuildId = "555555555555555555";
    public const string PlayRole = "111111111111111111";
    public const string BypassRole = "222222222222222222";

    public Dictionary<string, string[]> Members { get; } = new(StringComparer.Ordinal);
    public bool Down { get; set; }
    public int Calls { get; private set; }

    /// <summary>A server where each listed account holds the play role.</summary>
    public static FakeDiscordApi WithPlayers(params string[] players)
    {
        var api = new FakeDiscordApi();
        foreach (var id in players) api.Members[id] = [PlayRole];
        return api;
    }

    /// <summary>Access settings that require the play role in this server.</summary>
    public static AccessSettings RequirePlayRole(bool maintenance = false) =>
        AccessSettings.Default with { GuildId = GuildId, PlayRoles = [PlayRole], BypassRoles = [BypassRole], Maintenance = maintenance };

    public Task<(HttpStatusCode Status, JsonDocument? Body)> GetAsync(string path, CancellationToken cancellationToken)
    {
        Calls++;
        if (Down) throw new HttpRequestException("synthetic outage");
        if (path == $"guilds/{GuildId}/roles")
            return Task.FromResult<(HttpStatusCode, JsonDocument?)>((HttpStatusCode.OK, JsonDocument.Parse(JsonSerializer.Serialize(new[]
            {
                new { id = PlayRole, name = "Aurora 21 player", position = 2 },
                new { id = BypassRole, name = "Tester", position = 3 },
            }))));
        var prefix = $"guilds/{GuildId}/members/";
        if (path.StartsWith(prefix, StringComparison.Ordinal) && Members.TryGetValue(path[prefix.Length..], out var roles))
            return Task.FromResult<(HttpStatusCode, JsonDocument?)>((HttpStatusCode.OK, JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                roles, user = new { username = "member" + path[^4..], global_name = (string?)null },
            }))));
        return Task.FromResult<(HttpStatusCode, JsonDocument?)>((HttpStatusCode.NotFound, null));
    }
}
