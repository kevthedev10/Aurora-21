using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Server.Http;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>The Aurora launcher's <c>/client/*</c> API: health, verified launches, presence, trust anchors and the install pack.</summary>
public sealed class ClientApiEndpointTests : IDisposable
{
    private const string DiscordId = LaunchAssertionVerifierTests.DiscordId;
    private const string OtherId = "987654321098765432";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurora21-client-api-" + Guid.NewGuid().ToString("N"));

    private static readonly LauncherApiSettings Settings = new("Aurora 21", 3, 600, "Down for maintenance.", "Full.", "Blocked.",
        "No play role.", "Roles unavailable.", "Not configured.");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A launcher API over a synthetic Discord server where <see cref="DiscordId"/> holds the play role.</summary>
    private (ClientApiEndpoint Endpoint, NativeSessions Sessions, PlayerAccess Access, PlayerPresence Presence) Create(
        bool identity = true, string? install = null, string? certificates = null, FakeDiscordApi? discord = null, Func<JsonObject>? leaderboardUi = null,
        MathGroups? mathGroups = null, TimeProvider? time = null, LauncherFriends? friends = null)
    {
        var sessions = new NativeSessions(new NativeAuthSettings(900, 7200), time);
        var access = PlayerAccess.InMemory(FakeDiscordApi.RequirePlayRole());
        var presence = new PlayerPresence();
        var verifier = identity ? new LaunchAssertionVerifier(LaunchAssertionVerifierTests.Secret, LaunchAssertionVerifierTests.Issuer) : null;
        var issuer = new LaunchIssuer(verifier, sessions, access, new DiscordRoles(discord ?? FakeDiscordApi.WithPlayers(DiscordId)), presence,
            mathGroups: mathGroups, nowSeconds: time is null ? null : () => time.GetUtcNow().ToUnixTimeSeconds(),
            playerNamesPath: Path.Combine(_root, "state", "player-names.json"));
        var endpoint = new ClientApiEndpoint(issuer, null, new ClientApiOptions(Settings, "203.0.113.7", certificates, install, null), time: time,
            leaderboardUi: leaderboardUi, mathGroups: mathGroups, friends: friends);
        return (endpoint, sessions, access, presence);
    }

    /// <summary>The bridge-report rules this server runs with (newBuild 26100; the SDK redist's ucrt maps to 24h2).</summary>
    private MathGroups MathGroups() => new(Path.Combine(_root, "state", "windows-builds.json"),
        new MathGroupRules { Enabled = true, NewBuild = 26100, BridgeGroups = new Dictionary<long, string> { [19041] = "pre24h2", [26100] = "24h2" } },
        NullLogger.Instance);

    [Fact]
    public async Task ARuntimeMathReportNamesThePlayerThroughItsGameCode()
    {
        var (endpoint, sessions, _, _) = Create(mathGroups: MathGroups());
        var assertion = LaunchAssertionVerifierTests.SignHs256(LaunchAssertionVerifierTests.Secret, name: "Synthetic Player");
        var launch = await Send(endpoint, "POST", "/client/launch", new { discordId = DiscordId, discordName = "ignored", launcher = "aurora", assertion });
        Assert.Equal(200, launch.Status);
        var code = launch.Json!["code"]!.GetValue<string>();
        var player = PlayerIdentity.Of(DiscordId);

        var header = ("X-Aurora21-Math", "v=1; ucrt=10.0.26100.7705; sha256=0123456789abcdef; fma3=1");
        var report = await Send(endpoint, "POST", "/client/launch/math", new { code }, new[] { header });
        Assert.Equal(200, report.Status);
        Assert.True(report.Json!["ok"]!.GetValue<bool>());
        Assert.Equal("24h2", report.Json["group"]!.GetValue<string>());
        Assert.Equal("24h2", MathGroups().GroupOf(player.Id));
        // The code names the player but is not consumed: FIFA still authenticates with it.
        Assert.Equal(player, sessions.PlayerForCode(code));
        Assert.Null(MathGroups().GroupOf(OtherId));
    }

    /// <summary>
    /// The client pack's maths bridge reports about 20 s after FIFA's maths imports settle, so normally after FIFA has logged in
    /// (Blaze and /ut/auth) with the same launch code: the code names the player until it expires, it is never used up. A new
    /// launch's Windows header clears the last bridge report, so every launch reports again.
    /// </summary>
    [Fact]
    public async Task TheMathReportLandsAfterFifaLoggedInWithTheCodeUntilTheCodeExpires()
    {
        var clock = new Aurora21.UltimateTeam.Tests.Api.TestRuntime.MovableTime(1_790_000_000);
        var groups = MathGroups();
        var (endpoint, sessions, _, _) = Create(mathGroups: groups, time: clock);
        var player = PlayerIdentity.Of(DiscordId);
        var windows = ("X-Aurora21-Windows", "19045.6456");
        var bridge = ("X-Aurora21-Math", "v=1; ucrt=10.0.26100.1742; sha256=2E5FB14B7BF85402; fma3=1");

        async Task<string> LaunchAsync()
        {
            var assertion = LaunchAssertionVerifierTests.SignHs256(LaunchAssertionVerifierTests.Secret, name: "Synthetic Player");
            var launch = await Send(endpoint, "POST", "/client/launch", new { discordId = DiscordId, discordName = "ignored", launcher = "aurora", assertion },
                new[] { windows });
            Assert.Equal(200, launch.Status);
            return launch.Json!["code"]!.GetValue<string>();
        }

        var code = await LaunchAsync();
        Assert.Equal("pre24h2", groups.GroupOf(player.Id));
        // FIFA logs in with the code (Blaze login and /ut/auth both look it up; /ut/auth starts the UT session).
        clock.Now += 25;
        Assert.Equal(player, sessions.PlayerForCode(code));
        sessions.StartSession(player);
        clock.Now += 30;
        var report = await Send(endpoint, "POST", "/client/launch/math", new { code }, new[] { bridge });
        Assert.Equal(200, report.Status);
        Assert.Equal("24h2", report.Json!["group"]!.GetValue<string>());

        // The next launch starts from the Windows build again until its own report lands.
        var next = await LaunchAsync();
        Assert.Equal("pre24h2", groups.GroupOf(player.Id));
        // A report after the code expired (code_lifetime_seconds, 900 here) names nobody.
        clock.Now += 901;
        var late = await Send(endpoint, "POST", "/client/launch/math", new { code = next }, new[] { bridge });
        Assert.Equal(401, late.Status);
        Assert.Equal("unknown_code", late.Json!["error"]!.GetValue<string>());
        Assert.Equal("pre24h2", groups.GroupOf(player.Id));
    }

    [Fact]
    public async Task AMathReportNeedsItsCodeHeaderAndJson()
    {
        var (endpoint, sessions, _, _) = Create(mathGroups: MathGroups());
        var code = sessions.IssueCode(PlayerIdentity.Of(DiscordId));
        var header = ("X-Aurora21-Math", "v=1; ucrt=10.0.26100.7705; sha256=0123456789abcdef; fma3=1");
        Assert.Equal(400, (await Send(endpoint, "POST", "/client/launch/math", new { code })).Status);
        // Windows PowerShell's VersionInfo.FileVersion carries a "(WinBuild...)" suffix the contract does not allow: such a
        // report is refused, not answered ok and then dropped by MathGroups.
        var suffixed = ("X-Aurora21-Math", "v=1; ucrt=10.0.26100.7705 (WinBuild.160101.0800); sha256=0123456789abcdef; fma3=1");
        var refused = await Send(endpoint, "POST", "/client/launch/math", new { code }, new[] { suffixed });
        Assert.Equal(400, refused.Status);
        Assert.Equal("invalid_math_header", refused.Json!["error"]!.GetValue<string>());
        Assert.Equal(401, (await Send(endpoint, "POST", "/client/launch/math", new { code = "0".PadLeft(40, '0') }, new[] { header })).Status);
        Assert.Equal(401, (await Send(endpoint, "POST", "/client/launch/math", new { code = "not-a-code" }, new[] { header })).Status);
        var crossSite = await Send(endpoint, "POST", "/client/launch/math", new { code }, new[] { header, ("Sec-Fetch-Site", "cross-site") });
        Assert.Equal(403, crossSite.Status);
        Assert.Null(MathGroups().GroupOf(PlayerIdentity.Of(DiscordId).Id));
    }

    [Fact]
    public async Task HealthSaysWhetherThisPlayerMayPlay()
    {
        var (unconfigured, _, _, _) = Create(identity: false);
        var closed = await Send(unconfigured, "GET", "/client/health");
        Assert.Equal(200, closed.Status);
        Assert.True(closed.Json!["maintenance"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("not_configured", closed.Json["maintenance"]!["reason"]!.GetValue<string>());

        var (endpoint, _, access, _) = Create();
        var open = await Send(endpoint, "GET", $"/client/health?discord={DiscordId}");
        Assert.False(open.Json!["maintenance"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("203.0.113.7", open.Json["gameAddress"]!.GetValue<string>());
        Assert.Equal("Aurora 21", open.Json["server"]!.GetValue<string>());
        var stranger = await Send(endpoint, "GET", $"/client/health?discord={OtherId}");
        Assert.Equal("no_role", stranger.Json!["maintenance"]!["reason"]!.GetValue<string>());
        Assert.Equal("No play role.", stranger.Json["maintenance"]!["message"]!.GetValue<string>());

        await access.UpdateSettingsAsync(maintenance: true, message: "Back at eight.");
        var maintenance = await Send(endpoint, "GET", $"/client/health?discord={DiscordId}");
        Assert.Equal("maintenance", maintenance.Json!["maintenance"]!["reason"]!.GetValue<string>());
        Assert.Equal("Back at eight.", maintenance.Json["maintenance"]!["message"]!.GetValue<string>());
        await access.UpdatePlayerAsync(DiscordId, null, true, null, CancellationToken.None);
        var bypass = await Send(endpoint, "GET", $"/client/health?discord={DiscordId}");
        Assert.True(bypass.Json!["maintenance"]!["bypass"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AVerifiedApprovedLaunchGetsAGameCodeAndNamesTheClub()
    {
        var (endpoint, sessions, access, _) = Create();
        var assertion = LaunchAssertionVerifierTests.SignHs256(LaunchAssertionVerifierTests.Secret, name: "Synthetic Player");
        var reply = await Send(endpoint, "POST", "/client/launch", new { discordId = DiscordId, discordName = "ignored", launcher = "aurora", assertion });
        Assert.Equal(200, reply.Status);
        var code = reply.Json!["code"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{40}$", code);
        Assert.Equal(PlayerIdentity.Of(DiscordId), sessions.PlayerForCode(code));
        Assert.Equal(PlayerIdentity.Of(DiscordId).PersonaId, reply.Json["personaId"]!.GetValue<long>());
        Assert.Equal("Synthetic Player", access.Find(DiscordId)!.DisplayName);
        Assert.Equal("Synthetic Player", PlayerNames.Recorded(Path.Combine(_root, "state", "player-names.json"), DiscordId));
        Assert.Equal("no-store", reply.CacheControl);
    }

    [Fact]
    public async Task AVerifiedLaunchRecordsTheLaunchersFriendsAndARefusedOneDoesNot()
    {
        var friends = new LauncherFriends();
        var (endpoint, _, _, _) = Create(friends: friends);
        var secret = LaunchAssertionVerifierTests.Secret;
        var reply = await Send(endpoint, "POST", "/client/launch", new
        {
            discordId = DiscordId, assertion = LaunchAssertionVerifierTests.SignHs256(secret),
            friends = new object[] { OtherId, DiscordId, "not-an-id", 42, OtherId },
        });
        Assert.Equal(200, reply.Status);
        // Valid Discord ids only, never the player itself, once each.
        Assert.Equal(new[] { PlayerIdentity.Of(OtherId).Id }, friends.Of(PlayerIdentity.Of(DiscordId).Id));
        Assert.False(friends.Mutual(PlayerIdentity.Of(DiscordId).Id, PlayerIdentity.Of(OtherId).Id));

        // A refused launch (no play role) records nothing, so it cannot make the pair mutual.
        var refused = await Send(endpoint, "POST", "/client/launch", new
        {
            discordId = OtherId, assertion = LaunchAssertionVerifierTests.SignHs256(secret, subject: OtherId), friends = new[] { DiscordId },
        });
        Assert.Equal(403, refused.Status);
        Assert.Empty(friends.Of(PlayerIdentity.Of(OtherId).Id));

        // A launch without the field keeps the last list.
        Assert.Equal(200, (await Send(endpoint, "POST", "/client/launch",
            new { discordId = DiscordId, assertion = LaunchAssertionVerifierTests.SignHs256(secret) })).Status);
        Assert.Single(friends.Of(PlayerIdentity.Of(DiscordId).Id));
    }

    [Fact]
    public async Task LaunchRefusalsIssueNoCode()
    {
        var (endpoint, _, access, _) = Create();
        var secret = LaunchAssertionVerifierTests.Secret;
        var mismatch = await Send(endpoint, "POST", "/client/launch",
            new { discordId = OtherId, assertion = LaunchAssertionVerifierTests.SignHs256(secret) });
        Assert.Equal(401, mismatch.Status);
        Assert.Equal("discord_id_mismatch", mismatch.Json!["error"]!.GetValue<string>());

        var stranger = await Send(endpoint, "POST", "/client/launch",
            new { discordId = OtherId, discordName = "Stranger", assertion = LaunchAssertionVerifierTests.SignHs256(secret, subject: OtherId) });
        Assert.Equal(403, stranger.Status);
        Assert.Equal("no_role", stranger.Json!["error"]!.GetValue<string>());
        Assert.Equal("No play role.", stranger.Json["message"]!.GetValue<string>());
        Assert.Null(stranger.Json["code"]);
        Assert.Equal("Stranger", access.Find(OtherId)!.DisplayName);

        var crossSite = await Send(endpoint, "POST", "/client/launch", new { discordId = DiscordId, assertion = "x" },
            headers: [("Sec-Fetch-Site", "cross-site")]);
        Assert.Equal(403, crossSite.Status);
        // A refused cross-site request is not a launch; the third launch from this address still counts (and fails).
        Assert.Equal(401, (await Send(endpoint, "POST", "/client/launch", new { discordId = DiscordId, assertion = "x" })).Status);
        var limited = await Send(endpoint, "POST", "/client/launch", new { discordId = DiscordId, assertion = "x" });
        Assert.Equal(429, limited.Status);
    }

    [Fact]
    public async Task PresenceListsOnlyPlayersOnlineNow()
    {
        var (endpoint, _, _, presence) = Create();
        using var online = presence.Enter(DiscordId, () => { });
        var reply = await Send(endpoint, "POST", "/client/presence", new { discord = new[] { DiscordId, OtherId } });
        Assert.Equal(200, reply.Status);
        var listed = reply.Json!["presence"]!.AsObject();
        Assert.True(listed[DiscordId]!["online"]!.GetValue<bool>());
        Assert.False(listed.ContainsKey(OtherId));
        var bad = await Send(endpoint, "GET", "/client/presence?discord=not-an-id");
        Assert.Equal(400, bad.Status);
    }

    [Fact]
    public async Task InstallServesOnlyTheManifestAndTheFilesItNamesAndResumes()
    {
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        var bytes = Encoding.ASCII.GetBytes("synthetic client pack contents");
        await File.WriteAllBytesAsync(Path.Combine(install, "aurora21-client-1.zip"), bytes);
        await File.WriteAllTextAsync(Path.Combine(install, "secret.txt"), "never served");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(install, "manifest.json"), JsonSerializer.Serialize(new
        {
            schema = 1, version = "2026.09.28.1", files = new[] { new { name = "aurora21-client-1.zip", size = bytes.Length, sha256 = sha, kind = "runtime" } },
        }));
        var (endpoint, _, _, _) = Create(install: install);
        var manifest = await Send(endpoint, "GET", "/client/install/manifest.json");
        Assert.Equal(200, manifest.Status);
        Assert.Equal("2026.09.28.1", manifest.Json!["version"]!.GetValue<string>());
        var health = await Send(endpoint, "GET", "/client/health");
        Assert.Equal(1, health.Json!["install"]!["files"]!.GetValue<int>());
        var file = await Send(endpoint, "GET", "/client/install/aurora21-client-1.zip");
        Assert.Equal(200, file.Status);
        Assert.Equal(sha, file.Headers["X-Content-SHA256"]);
        var resumed = await Send(endpoint, "GET", "/client/install/aurora21-client-1.zip", headers: [("Range", "bytes=10-")]);
        Assert.Equal(206, resumed.Status);
        Assert.Equal(Encoding.ASCII.GetString(bytes, 10, bytes.Length - 10), resumed.Body);
        Assert.Equal(404, (await Send(endpoint, "GET", "/client/install/secret.txt")).Status);
        Assert.Equal(404, (await Send(endpoint, "GET", "/client/install/../manifest.json")).Status);
    }

    [Fact]
    public async Task TrustAnchorsArePublicFilesOnly()
    {
        var certificates = Path.Combine(_root, "certs-root");
        Directory.CreateDirectory(Path.Combine(certificates, "certs"));
        foreach (var name in new[] { "local-gos2015-modulus.bin", "local-gs2019-modulus.bin" })
            await File.WriteAllBytesAsync(Path.Combine(certificates, name), RandomNumberGenerator.GetBytes(256));
        foreach (var name in new[] { "local-gos2015-ca-cert.pem", "local-gs2019-ca-cert.pem", "local-gos2015-ca-key.pem" })
            await File.WriteAllTextAsync(Path.Combine(certificates, "certs", name), "synthetic " + name);
        var (endpoint, _, _, _) = Create(certificates: certificates);
        var manifest = await Send(endpoint, "GET", "/client/trust-anchors/manifest.json");
        Assert.Equal(200, manifest.Status);
        var names = manifest.Json!["files"]!.AsArray().Select(file => file!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(4, names.Length);
        Assert.DoesNotContain(names, name => name.Contains("key", StringComparison.Ordinal));
        Assert.Equal(200, (await Send(endpoint, "GET", "/client/trust-anchors/local-gs2019-ca-cert.pem")).Status);
        Assert.Equal(404, (await Send(endpoint, "GET", "/client/trust-anchors/local-gos2015-ca-key.pem")).Status);
    }

    [Fact]
    public async Task LeaderboardUiManifestIsFreshPublicAndSupportsHead()
    {
        var top = 25;
        var (endpoint, _, _, _) = Create(leaderboardUi: () => new JsonObject
        {
            ["schema"] = 1, ["revision"] = $"synthetic-{top}",
            ["captions"] = new JsonArray(new JsonObject { ["mode"] = "champions", ["top"] = top }),
        });
        var first = await Send(endpoint, "GET", LeaderboardUiManifest.Path);
        Assert.Equal(200, first.Status);
        Assert.Equal(25, first.Json!["captions"]![0]!["top"]!.GetValue<int>());
        Assert.Equal("no-store", first.CacheControl);
        Assert.Equal("\"synthetic-25\"", first.Headers.ETag.ToString());
        top = 50;
        var next = await Send(endpoint, "GET", LeaderboardUiManifest.Path);
        Assert.Equal(50, next.Json!["captions"]![0]!["top"]!.GetValue<int>());
        Assert.Equal("\"synthetic-50\"", next.Headers.ETag.ToString());
        var head = await Send(endpoint, "HEAD", LeaderboardUiManifest.Path);
        Assert.Equal(200, head.Status);
        Assert.Empty(head.Body);
        var (unwired, _, _, _) = Create();
        Assert.Equal(503, (await Send(unwired, "GET", LeaderboardUiManifest.Path)).Status);
    }

    private static async Task<(int Status, string Body, JsonNode? Json, string CacheControl, IHeaderDictionary Headers)> Send(
        ClientApiEndpoint endpoint, string method, string pathAndQuery, object? body = null, (string Name, string Value)[]? headers = null)
    {
        var context = new DefaultHttpContext();
        var query = pathAndQuery.IndexOf('?');
        context.Request.Method = method;
        context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery[..query];
        if (query >= 0) context.Request.QueryString = new QueryString(pathAndQuery[query..]);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        foreach (var (name, value) in headers ?? []) context.Request.Headers[name] = value;
        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
        }
        var response = new MemoryStream();
        context.Response.Body = response;
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(
            new StreamResponseBodyFeature(response));
        await endpoint.HandleAsync(context);
        var text = Encoding.UTF8.GetString(response.ToArray());
        JsonNode? json = null;
        try { json = text.Length > 0 && context.Response.ContentType?.StartsWith("application/json", StringComparison.Ordinal) == true ? JsonNode.Parse(text) : null; }
        catch (JsonException) { }
        return (context.Response.StatusCode, text, json, context.Response.Headers.CacheControl.ToString(), context.Response.Headers);
    }
}
