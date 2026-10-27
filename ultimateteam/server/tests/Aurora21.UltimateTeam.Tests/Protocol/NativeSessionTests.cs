using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Http;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// The session identity mode (ultimateteam/native-hosting-investigation.md gate 3): FUT requests belong to the player whose
/// issued auth code passed <c>/ut/auth</c>, then to its random <c>X-UT-SID</c>, never to an address or a persona id in
/// the body. Everything else is refused before a club is opened. Synthetic codes and throwaway clubs only.
/// </summary>
public sealed class NativeSessionTests
{
    private const long Start = 1_790_000_000;
    private const string G = "/ut/game/fifa21";

    /// <summary>Every player in these tests connects from this one address, as behind a shared NAT.</summary>
    private static readonly IPAddress SharedAddress = IPAddress.Parse("203.0.113.7");

    private static readonly NativeAuthSettings Settings = new(CodeLifetimeSeconds: 900, SessionIdleSeconds: 7200);

    private static readonly PlayerIdentity First = PlayerIdentity.Of("111111");
    private static readonly PlayerIdentity Second = PlayerIdentity.Of("222222");

    [Fact]
    public void CodesAndSessionsExpireEndAndAreReplaced()
    {
        var clock = new TestRuntime.MovableTime(Start);
        var sessions = new NativeSessions(Settings, clock);
        var code = sessions.IssueCode(First);
        Assert.Matches("^[0-9a-f]{40}$", code);
        Assert.Equal(First, sessions.PlayerForCode(code));
        // A code serves several requests (accountinfo, then /ut/auth); once used it outlives its lifetime while the player
        // is active (NativeSessionsTests) and stops after the session idle time.
        Assert.Equal(First, sessions.PlayerForCode(code));
        Assert.Null(sessions.PlayerForCode(null));
        Assert.Null(sessions.PlayerForCode(code.ToUpperInvariant()));
        clock.Now += Settings.CodeLifetimeSeconds;
        Assert.Equal(First, sessions.PlayerForCode(code));
        clock.Now += Settings.SessionIdleSeconds + 1;
        Assert.Null(sessions.PlayerForCode(code));
        var unused = sessions.IssueCode(First);
        clock.Now += Settings.CodeLifetimeSeconds;
        Assert.Null(sessions.PlayerForCode(unused));

        var sid = sessions.StartSession(First);
        Assert.Matches("^ut\\.[0-9a-f]{32}$", sid);
        Assert.Equal(First, sessions.PlayerForSession(sid));
        // Each use keeps it alive; an idle one expires.
        clock.Now += Settings.SessionIdleSeconds - 1;
        Assert.Equal(First, sessions.PlayerForSession(sid));
        clock.Now += Settings.SessionIdleSeconds + 1;
        Assert.Null(sessions.PlayerForSession(sid));

        // A new session replaces the player's old one; logout and revocation end it.
        var older = sessions.StartSession(First);
        var newer = sessions.StartSession(First);
        Assert.Null(sessions.PlayerForSession(older));
        Assert.Equal(First, sessions.PlayerForSession(newer));
        sessions.EndSession(newer);
        Assert.Null(sessions.PlayerForSession(newer));
        var revokedCode = sessions.IssueCode(Second);
        var revokedSid = sessions.StartSession(Second);
        sessions.Revoke(Second.Id);
        Assert.Null(sessions.PlayerForCode(revokedCode));
        Assert.Null(sessions.PlayerForSession(revokedSid));
    }

    private sealed record Reply(int Status, string? UtSid, JsonNode? Body, string Text);

    private static async Task<Reply> SendAsync(FutEndpoint endpoint, string method, string target, JsonNode? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Method = method;
        var parts = target.Split('?', 2);
        request.Path = parts[0];
        request.QueryString = parts.Length > 1 ? new QueryString("?" + parts[1]) : QueryString.Empty;
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers[name] = value;
        }
        var bytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body.ToJsonString());
        request.Body = new MemoryStream(bytes);
        request.ContentLength = bytes.Length;
        request.ContentType = "application/json";
        context.Connection.RemoteIpAddress = SharedAddress;
        context.Response.Body = new MemoryStream();
        await endpoint.HandleAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return new Reply(context.Response.StatusCode, context.Response.Headers["X-UT-SID"].FirstOrDefault(),
            text.Length == 0 ? null : FutJson.SafeParse(Encoding.UTF8.GetBytes(text)), text);
    }

    private static JsonObject UtAuth(string? code, long? persona = null)
    {
        var body = new JsonObject { ["method"] = "authcode", ["isReadOnly"] = false, ["sku"] = "FUT21PCC" };
        if (code is not null)
        {
            body["identification"] = new JsonObject { ["authCode"] = code };
        }
        if (persona is not null)
        {
            body["nucleusPersonaId"] = persona;
        }
        return body;
    }

    private static Dictionary<string, string> Sid(string sid) => new() { ["X-UT-SID"] = sid };

    [Fact]
    public async Task TwoPlayersBehindOneAddressKeepTheirOwnClubs()
    {
        var clock = new TestRuntime.MovableTime(Start);
        using var rt = TestRuntime.Create(time: clock);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true)
        {
            NativeSessions = new NativeSessions(Settings, clock),
        };
        var sessions = players.NativeSessions!;
        var endpoint = new FutEndpoint(players);
        var firstCode = sessions.IssueCode(First);
        var secondCode = sessions.IssueCode(Second);

        // Before /ut/auth, the front end's accountinfo carries the code as nucleus-access-code.
        var info = await SendAsync(endpoint, "GET", $"{G}/v2/user/accountinfo", headers: new Dictionary<string, string> { ["nucleus-access-code"] = secondCode });
        Assert.Equal(200, info.Status);
        Assert.Equal(Second.PersonaId, info.Body!["userAccountInfo"]!["personas"]![0]!.AsObject().Int("personaId"));

        // /ut/auth: the verified code chooses the club, even when the body names the other player's persona.
        var first = await SendAsync(endpoint, "POST", "/ut/auth", UtAuth(firstCode, persona: Second.PersonaId));
        var second = await SendAsync(endpoint, "POST", "/ut/auth", UtAuth(secondCode));
        Assert.Equal((200, 200), (first.Status, second.Status));
        Assert.NotNull(first.UtSid);
        Assert.NotEqual(first.UtSid, second.UtSid);
        // Random, not the club-derived value anyone could compute, and the body carries the same one.
        var firstClub = players.Acquire(First);
        try
        {
            Assert.NotEqual(ClubPresentationService.UtSid(firstClub.Session.Game.Sid), first.UtSid);
        }
        finally
        {
            firstClub.Dispose();
        }
        Assert.Equal(first.UtSid, first.Body!.AsObject().Text("sid"));

        // Later requests belong to their session's player, and the reply echoes that session.
        var firstUser = await SendAsync(endpoint, "GET", $"{G}/user", headers: Sid(first.UtSid!));
        var secondUser = await SendAsync(endpoint, "GET", $"{G}/user", headers: Sid(second.UtSid!));
        Assert.Equal((First.PersonaId, Second.PersonaId), (firstUser.Body!.AsObject().Int("personaId"), secondUser.Body!.AsObject().Int("personaId")));
        Assert.Equal((first.UtSid, second.UtSid), (firstUser.UtSid, secondUser.UtSid));

        // A new /ut/auth replaces the old session; logout ends the current one.
        var again = await SendAsync(endpoint, "POST", "/ut/auth", UtAuth(firstCode));
        Assert.Equal(401, (await SendAsync(endpoint, "GET", $"{G}/user", headers: Sid(first.UtSid!))).Status);
        Assert.Equal(200, (await SendAsync(endpoint, "POST", "/ut/delete/auth", new JsonObject(), headers: Sid(again.UtSid!))).Status);
        Assert.Equal(401, (await SendAsync(endpoint, "GET", $"{G}/user", headers: Sid(again.UtSid!))).Status);

        // An idle session expires.
        clock.Now += Settings.SessionIdleSeconds + 1;
        Assert.Equal(401, (await SendAsync(endpoint, "GET", $"{G}/user", headers: Sid(second.UtSid!))).Status);
    }

    [Fact]
    public async Task EverythingUnverifiedIsRefusedBeforeAClubOpens()
    {
        var clock = new TestRuntime.MovableTime(Start);
        using var rt = TestRuntime.Create(time: clock);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true)
        {
            NativeSessions = new NativeSessions(Settings, clock),
        };
        var endpoint = new FutEndpoint(players);
        var code = players.NativeSessions!.IssueCode(First);
        var opened = players.OpenCount;

        async Task Refused(string method, string target, JsonNode? body = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            var reply = await SendAsync(endpoint, method, target, body, headers);
            Assert.True(reply.Status == 401, $"{method} {target} -> {reply.Status}");
            Assert.Equal("401", reply.Body!.AsObject().Text("code"));
            Assert.Null(reply.UtSid);
        }

        await Refused("POST", "/ut/auth", UtAuth(null));
        await Refused("POST", "/ut/auth", UtAuth("0000000000000000000000000000000000000000"));
        await Refused("GET", $"{G}/user");
        await Refused("GET", $"{G}/user", headers: Sid("ut.00000000000000000000000000000000"));
        await Refused("GET", $"{G}/v2/user/accountinfo", headers: new Dictionary<string, string> { ["nucleus-access-code"] = "forged" });
        // Only GET reaches shared /fut/ content without a session.
        await Refused("POST", "/fut/seasonal/seasonalconfig.json", new JsonObject());
        // An expired code no longer opens a session.
        clock.Now += Settings.CodeLifetimeSeconds;
        await Refused("POST", "/ut/auth", UtAuth(code));
        Assert.Equal(opened, players.OpenCount);

        // Shared content and the local auth bootstrap need no player.
        Assert.Equal(200, (await SendAsync(endpoint, "GET", "/fut/sbc/abc/challenges/images/sbc_challenge_image_1.png")).Status);
        Assert.Equal(200, (await SendAsync(endpoint, "GET", "/ultimateteam/auth/bootstrap")).Status);
        // The seasonal document (clock and rules only) is shared content too (observed refused 2026-09-27 20:20).
        var seasonal = await SendAsync(endpoint, "GET", "/fut/seasonal/seasonalconfig.json");
        Assert.Equal(200, seasonal.Status);
        Assert.NotEmpty(seasonal.Body!.AsObject());
        // A card background (observed 2026-09-24/27) is shared content too: the DDS, not the empty unknown answer. The
        // cache is seeded so the test never reaches the CDN.
        var background = Path.Combine(rt.Game.Paths.ContentCacheDirectory, "fut", "items", "images", "backgrounds", "itemBGs",
            "ab719e69-0d3e-430c-8e67-80a106de93c1", "cards_bg_s_1_52_0.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(background)!);
        await File.WriteAllBytesAsync(background, "DDS "u8.ToArray().Concat(new byte[124]).ToArray());
        var art = await SendAsync(endpoint, "GET", "/fut/items/images/backgrounds/itemBGs/ab719e69-0d3e-430c-8e67-80a106de93c1/cards_bg_s_1_52_0.dds");
        Assert.Equal(200, art.Status);
        Assert.StartsWith("DDS ", art.Text, StringComparison.Ordinal);
        // Any other /fut/ asset gets the empty unknown answer it gets in the other modes, including a path that reaches
        // the Squad Battles club feed there: no club route runs.
        foreach (var asset in new[] { "/fut/items/images/backgrounds/itemBGs/unknown.png", "/fut/squadbattle/opponents/feed" })
        {
            var reply = await SendAsync(endpoint, "GET", asset);
            Assert.Equal(200, reply.Status);
            Assert.Empty(reply.Body!.AsObject());
        }
        Assert.Equal(opened, players.OpenCount);

        // Session mode has no peer-IP fallback; Blaze takes its separate verified AUTH path.
        Assert.Throws<InvalidOperationException>(() => FutEndpoint.PlayerFor(SharedAddress, players));
    }
}
