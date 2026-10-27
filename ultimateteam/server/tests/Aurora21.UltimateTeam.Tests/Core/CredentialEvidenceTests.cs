using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Services.Auth;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// The redacted credential evidence (native-hosting-investigation.md): values this server issued are named, anything else (a real EA
/// credential or account id in EA App mode) appears only as a length and hash prefix, never as its text.
/// </summary>
public sealed class CredentialEvidenceTests
{
    private const string ForeignToken = "QVQwOjIuMDozLjA6ODY0MDA6synthetic-foreign-token-1234567890";
    private const long ForeignAccountId = 1_000_123_456_789;

    [Fact]
    public void OwnValuesAreNamedAndForeignOnesNeverAppear()
    {
        using var rt = TestRuntime.Create();
        var game = rt.Game;
        Assert.Equal("local-auth-code", CredentialEvidence.Describe(LocalAuthService.AuthCode, game));
        Assert.Equal("local-access-token", CredentialEvidence.Describe(LocalAuthService.AccessToken, game));
        Assert.Equal("club-session-token", CredentialEvidence.Describe(game.Token, game));
        Assert.Equal($"contains-local-access-token(len={("Bearer " + LocalAuthService.AccessToken).Length})", CredentialEvidence.Describe("Bearer " + LocalAuthService.AccessToken, game));
        Assert.Equal(("absent", "empty", "nucleus:rest"), (CredentialEvidence.Describe(null, game), CredentialEvidence.Describe("", game), CredentialEvidence.Describe("nucleus:rest", game)));
        Assert.StartsWith("other(len=", CredentialEvidence.Describe(ForeignToken, game));
        Assert.Equal(("local-persona-id", "4", "number(digits=13)"),
            (CredentialEvidence.Describe(game.Config.PersonaId, game), CredentialEvidence.Describe(4, game), CredentialEvidence.Describe(ForeignAccountId, game)));

        // A Blaze login, a UT auth body, a token form and headers: the foreign values never show.
        var login = new TdfWriter().Str("AUTH", ForeignToken).Int("PLAT", 4).Struct("SESS", s => s.Str("KEY", game.Token).Int("UID", ForeignAccountId)).ToArray();
        var blaze = CredentialEvidence.DescribeTdf(login, game);
        Assert.Contains("PLAT=4", blaze);
        Assert.Contains("SESS.KEY=club-session-token", blaze);
        var json = CredentialEvidence.DescribeJson(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["identification"] = new JsonObject { ["authCode"] = LocalAuthService.AuthCode, ["redirectUrl"] = "nucleus:rest" },
            ["nucPersId"] = ForeignAccountId,
            ["token"] = ForeignToken,
        }.ToJsonString()), game);
        Assert.Contains("$.identification.authCode=local-auth-code", json);
        var form = CredentialEvidence.DescribeForm($"grant_type=authorization_code&code={Uri.EscapeDataString(LocalAuthService.AuthCode)}&secret={ForeignToken}", game);
        Assert.Contains("code=local-auth-code", form);
        var headers = CredentialEvidence.DescribeHeaders(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + ForeignToken,
            ["Accept"] = "x",
            ["X-UT-SID"] = game.UtSid,
            ["Cookie"] = "session=" + ForeignToken,
        }, game);
        Assert.DoesNotContain("accept", headers);
        Assert.Contains("x-ut-sid=club-ut-sid", headers);
        Assert.Contains("cookie=other(len=", headers);
        foreach (var text in new[] { blaze, json, form, headers })
        {
            Assert.DoesNotContain(ForeignToken, text);
            Assert.DoesNotContain("1000123456789", text);
        }
    }
}
