using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Server.Http;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

public sealed class LaunchCodeEndpointTests
{
    private const string Issuer = "https://accounts.example.test";
    private const string DiscordId = "123456789012345678";

    [Fact]
    public void VerifierRefusesAConfiguredPrivateKey()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => new LaunchAssertionVerifier(signer.ExportPkcs8PrivateKeyPem(), Issuer));
    }

    [Fact]
    public async Task SignedAssertionIssuesAPlayerCodeOnce()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new LaunchAssertionVerifier(signer.ExportSubjectPublicKeyInfoPem(), Issuer);
        var sessions = new NativeSessions(new NativeAuthSettings(900, 7200));
        var endpoint = Endpoint(verifier, sessions, [DiscordId]);
        var assertion = Sign(signer);

        var first = await Post(endpoint, assertion);
        Assert.Equal(200, first.Status);
        using var result = JsonDocument.Parse(first.Body);
        var code = result.RootElement.GetProperty("code").GetString();
        Assert.NotNull(code);
        Assert.Matches("^[0-9a-f]{40}$", code);
        Assert.Equal(900, result.RootElement.GetProperty("expiresInSeconds").GetInt32());
        Assert.Equal(PlayerIdentity.Of(DiscordId), sessions.PlayerForCode(code));
        Assert.Equal("no-store", first.CacheControl);

        var replay = await Post(endpoint, assertion);
        Assert.Equal(401, replay.Status);
        Assert.DoesNotContain(code, replay.Body);
    }

    [Fact]
    public async Task WrongIssuerScopeAudienceAndSignatureNeverIssueCodes()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new LaunchAssertionVerifier(signer.ExportSubjectPublicKeyInfoPem(), Issuer);
        var sessions = new NativeSessions(new NativeAuthSettings(900, 7200));
        var endpoint = Endpoint(verifier, sessions, [DiscordId]);

        foreach (var assertion in new[]
                 {
                     Sign(signer, issuer: "https://wrong.example.test"),
                     Sign(signer, audience: "another-game"),
                     Sign(signer, scope: "aurora16.launch"),
                     Sign(signer, subject: "not-a-discord-id"),
                     Sign(other),
                     Sign(signer, expires: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()),
                 })
        {
            var result = await Post(endpoint, assertion);
            Assert.Equal(401, result.Status);
            Assert.DoesNotContain("\"code\"", result.Body);
        }
    }

    [Fact]
    public async Task TwoPlayersAtOneAddressReceiveDifferentVerifiedCodes()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new LaunchAssertionVerifier(signer.ExportSubjectPublicKeyInfoPem(), Issuer);
        const string secondId = "987654321098765432";
        var sessions = new NativeSessions(new NativeAuthSettings(900, 7200));
        var endpoint = Endpoint(verifier, sessions, [DiscordId, secondId]);

        var first = await Post(endpoint, Sign(signer));
        var second = await Post(endpoint, Sign(signer, subject: secondId));
        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        using var firstJson = JsonDocument.Parse(first.Body);
        using var secondJson = JsonDocument.Parse(second.Body);
        var firstCode = firstJson.RootElement.GetProperty("code").GetString();
        var secondCode = secondJson.RootElement.GetProperty("code").GetString();
        Assert.NotEqual(firstCode, secondCode);
        Assert.Equal(PlayerIdentity.Of(DiscordId), sessions.PlayerForCode(firstCode));
        Assert.Equal(PlayerIdentity.Of(secondId), sessions.PlayerForCode(secondCode));
    }

    [Fact]
    public async Task ControlRouteRejectsNonLoopbackAndWrongRequests()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new LaunchAssertionVerifier(signer.ExportSubjectPublicKeyInfoPem(), Issuer);
        var endpoint = Endpoint(verifier, new NativeSessions(new NativeAuthSettings(900, 7200)), [DiscordId]);
        var assertion = Sign(signer);
        Assert.Equal(400, (await Post(endpoint, assertion, IPAddress.Parse("203.0.113.1"))).Status);
        Assert.Equal(404, (await Post(endpoint, assertion, IPAddress.Loopback, "/ut/auth")).Status);
        Assert.Equal(200, (await Post(endpoint, assertion)).Status);
    }

    [Fact]
    public async Task SignedIdWithoutThePlayRoleIsDenied()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new LaunchAssertionVerifier(signer.ExportSubjectPublicKeyInfoPem(), Issuer);
        var sessions = new NativeSessions(new NativeAuthSettings(900, 7200));
        var endpoint = Endpoint(verifier, sessions, ["987654321098765432"]);

        var result = await Post(endpoint, Sign(signer));
        Assert.Equal(403, result.Status);
        Assert.DoesNotContain("\"code\"", result.Body);
    }

    /// <summary>
    /// The hosted launch is the only request a hosted server sees from the player's own launcher, so it carries the Windows
    /// maths group that keeps 24H2 and older players apart (the local health check never reaches a hosted server).
    /// </summary>
    [Fact]
    public async Task AnIssuedLaunchRecordsTheWindowsMathsGroup()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = new LaunchAssertionVerifier(signer.ExportSubjectPublicKeyInfoPem(), Issuer);
        var sessions = new NativeSessions(new NativeAuthSettings(900, 7200));
        var path = Path.Combine(Path.GetTempPath(), "ultimateteam21-launch-math-" + Guid.NewGuid().ToString("N"), "windows-builds.json");
        var groups = new MathGroups(path, new Aurora21.UltimateTeam.Core.Rules.MathGroupRules
        {
            Enabled = true,
            NewBuild = 26100,
            BridgeGroups = new Dictionary<long, string> { [26100] = "24h2" },
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        try
        {
            const string secondId = "987654321098765432";
            var endpoint = Endpoint(verifier, sessions, [DiscordId, secondId], groups, () => 1_790_000_000);

            Assert.Equal(200, (await Post(endpoint, Sign(signer), headers: [(LaunchCodeEndpoint.WindowsHeader, "26100.4652")])).Status);
            Assert.Equal("24h2", groups.GroupOf(PlayerIdentity.Of(DiscordId).Id));
            Assert.Equal(200, (await Post(endpoint, Sign(signer, subject: secondId), headers: [(LaunchCodeEndpoint.WindowsHeader, "19045.5011")])).Status);
            Assert.False(groups.Compatible(PlayerIdentity.Of(DiscordId).Id, PlayerIdentity.Of(secondId).Id));

            // A refused launch records nothing.
            const string strangerId = "555555555555555555";
            Assert.Equal(403, (await Post(endpoint, Sign(signer, subject: strangerId), headers: [(LaunchCodeEndpoint.WindowsHeader, "26100.1")])).Status);
            Assert.Null(groups.GroupOf(PlayerIdentity.Of(strangerId).Id));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>A launch route over a synthetic Discord server where the listed accounts hold the play role.</summary>
    private static LaunchCodeEndpoint Endpoint(LaunchAssertionVerifier verifier, NativeSessions sessions, string[] players,
        MathGroups? groups = null, Func<long>? now = null) =>
        new(new LaunchIssuer(verifier, sessions, PlayerAccess.InMemory(FakeDiscordApi.RequirePlayRole()),
            new DiscordRoles(FakeDiscordApi.WithPlayers(players)), null, groups, now));

    private static string Sign(ECDsa signer, string issuer = Issuer, string audience = LaunchAssertionVerifier.Audience,
        string scope = LaunchAssertionVerifier.RequiredScope, string subject = DiscordId, long? expires = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = issuer, aud = audience, sub = subject, scope,
            iat = now, nbf = now, exp = expires ?? now + 60,
            jti = Encode(RandomNumberGenerator.GetBytes(24)),
        }));
        var signingInput = header + "." + payload;
        var signature = signer.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Encode(signature);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<(int Status, string Body, string CacheControl)> Post(LaunchCodeEndpoint endpoint,
        string assertion, IPAddress? peer = null, string path = "/api/v1/launch/code", (string Name, string Value)[]? headers = null)
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers ?? [])
        {
            context.Request.Headers[name] = value;
        }
        context.Connection.RemoteIpAddress = peer ?? IPAddress.Loopback;
        context.Request.Path = path;
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        var body = JsonSerializer.SerializeToUtf8Bytes(new { launchAssertion = assertion });
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        await endpoint.HandleAsync(context);
        context.Response.Body.Position = 0;
        var reply = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, reply, context.Response.Headers.CacheControl.ToString());
    }
}
