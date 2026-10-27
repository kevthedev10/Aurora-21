using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Aurora21.UltimateTeam.Server.Access;

/// <summary>What Discord said about one account in the Aurora Discord server.</summary>
/// <param name="Status"><c>ok</c> (a member, with <paramref name="Roles"/>), <c>not_member</c>, <c>unavailable</c>
/// (Discord could not be asked and nothing recent is remembered) or <c>not_configured</c> (no bot token or server id).</param>
public sealed record MemberRoles(string Status, IReadOnlySet<string>? Roles, string? Username, string? Problem = null, DateTimeOffset? CheckedAt = null)
{
    /// <summary>The roles to decide with: a member's roles, none for a non-member, null when unknown.</summary>
    public IReadOnlySet<string>? Known => Status switch
    {
        "ok" => Roles,
        "not_member" => new HashSet<string>(StringComparer.Ordinal),
        _ => null,
    };
}

/// <summary>The one Discord call shape this needs: an authenticated GET returning a status and JSON.</summary>
public interface IDiscordApi
{
    Task<(HttpStatusCode Status, JsonDocument? Body)> GetAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// The Discord bot's view of the Aurora Discord server (Aurora 16's role bypass, used here for who may play too): one
/// member's roles, and the server's role list for the panel's picker. Answers are remembered for
/// <see cref="FreshFor"/>, so a launch costs at most one Discord call and a role given or taken away counts within
/// minutes. When Discord cannot be asked (down, rate limited, a timeout), an answer from the last
/// <see cref="StaleFor"/> is used instead; with none, the account's roles are unknown and a launch that needs them is
/// refused, never admitted unchecked. The bot token is never logged or shown.
/// </summary>
public sealed class DiscordRoles
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StaleFor = TimeSpan.FromDays(7);
    private readonly IDiscordApi? _api;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, MemberRoles> _members = new(StringComparer.Ordinal);

    public DiscordRoles(IDiscordApi? api, TimeProvider? time = null)
    {
        _api = api;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A bot client from a token (null or empty: roles are not configured).</summary>
    public static DiscordRoles FromToken(string? token, TimeProvider? time = null) =>
        new(string.IsNullOrWhiteSpace(token) ? null : new DiscordHttpApi(token.Trim()), time);

    public bool TokenSet => _api is not null;

    public int Cached => _members.Count;

    /// <summary>One account's roles in the server, remembered for <see cref="FreshFor"/> unless <paramref name="fresh"/>.</summary>
    public async Task<MemberRoles> MemberAsync(string guildId, string userId, bool fresh, CancellationToken cancellationToken)
    {
        if (_api is null || !PlayerAccess.IsDiscordId(guildId)) return new MemberRoles("not_configured", null, null);
        if (!PlayerAccess.IsDiscordId(userId)) return new MemberRoles("not_member", null, null);
        var key = guildId + ":" + userId;
        var now = _time.GetUtcNow();
        var cached = _members.GetValueOrDefault(key);
        if (!fresh && cached is { CheckedAt: { } at } && now - at < FreshFor) return cached;
        MemberRoles answer;
        try
        {
            var (status, body) = await _api.GetAsync($"guilds/{guildId}/members/{userId}", cancellationToken);
            using (body)
            {
                answer = status switch
                {
                    HttpStatusCode.OK when body is not null => Member(body.RootElement, now),
                    HttpStatusCode.NotFound => new MemberRoles("not_member", null, null, null, now),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        new MemberRoles("unavailable", null, null, "the bot token was refused, or the bot is not in this Discord server"),
                    _ => new MemberRoles("unavailable", null, null, $"Discord answered {(int)status}"),
                };
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            answer = new MemberRoles("unavailable", null, null, "Discord could not be reached");
        }
        if (answer.Status != "unavailable")
        {
            _members[key] = answer;
            return answer;
        }
        return cached is { CheckedAt: { } then } && now - then < StaleFor ? cached with { Problem = answer.Problem } : answer;
    }

    /// <summary>The server's roles for the panel's picker, highest first; empty when Discord cannot be asked.</summary>
    public async Task<IReadOnlyList<(string Id, string Name)>> GuildRolesAsync(string guildId, CancellationToken cancellationToken)
    {
        if (_api is null || !PlayerAccess.IsDiscordId(guildId)) return [];
        var (status, body) = await _api.GetAsync($"guilds/{guildId}/roles", cancellationToken);
        using (body)
        {
            if (status != HttpStatusCode.OK || body?.RootElement.ValueKind != JsonValueKind.Array) return [];
            return body.RootElement.EnumerateArray()
                .Where(role => role.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .OrderByDescending(role => role.TryGetProperty("position", out var position) && position.TryGetInt32(out var p) ? p : 0)
                .Select(role => (role.GetProperty("id").GetString()!, role.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""))
                .ToArray();
        }
    }

    private static MemberRoles Member(JsonElement member, DateTimeOffset now)
    {
        var roles = member.TryGetProperty("roles", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(role => role.ValueKind == JsonValueKind.String).Select(role => role.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        string? username = null;
        if (member.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            username = user.TryGetProperty("global_name", out var global) && global.ValueKind == JsonValueKind.String ? global.GetString()
                : user.TryGetProperty("username", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        return new MemberRoles("ok", roles, username, null, now);
    }

    /// <summary>Discord's REST API with the bot token, over HTTPS, with a short timeout.</summary>
    private sealed class DiscordHttpApi : IDiscordApi
    {
        private readonly HttpClient _http;

        public DiscordHttpApi(string token)
        {
            _http = new HttpClient { BaseAddress = new Uri("https://discord.com/api/v10/"), Timeout = TimeSpan.FromSeconds(5) };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
            // Discord asks bots to identify as DiscordBot (URL, version); this names the server rather than a site.
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DiscordBot (aurora21, 1)");
        }

        public async Task<(HttpStatusCode Status, JsonDocument? Body)> GetAsync(string path, CancellationToken cancellationToken)
        {
            using var response = await _http.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return (response.StatusCode, await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken));
        }
    }
}
