using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aurora21.UltimateTeam.Server.Http;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>The launch assertion's two key types, the optional claims, and that a key accepts only its own algorithm.</summary>
public sealed class LaunchAssertionVerifierTests
{
    internal const string Issuer = "https://accounts.example.test";
    internal const string DiscordId = "123456789012345678";
    internal static readonly byte[] Secret = Encoding.UTF8.GetBytes("synthetic-shared-secret-for-tests-0123456789");

    [Fact]
    public void Hs256AssertionIsVerifiedOnceAndCarriesItsName()
    {
        using var verifier = new LaunchAssertionVerifier(Secret, Issuer);
        var assertion = SignHs256(Secret, name: "Synthetic Player");
        var verified = verifier.Verify(assertion);
        Assert.NotNull(verified);
        Assert.Equal(DiscordId, verified.DiscordId);
        Assert.Equal("Synthetic Player", verified.Name);
        Assert.Equal("HS256", verifier.Algorithm);
        Assert.Null(verifier.Verify(assertion));
    }

    [Fact]
    public void MinimalClaimsAreEnoughButPresentOptionalClaimsMustBeRight()
    {
        using var verifier = new LaunchAssertionVerifier(Secret, Issuer);
        Assert.NotNull(verifier.Verify(SignHs256(Secret, scope: null, notBefore: false)));
        Assert.Null(verifier.Verify(SignHs256(Secret, scope: "aurora16.launch")));
        Assert.Null(verifier.Verify(SignHs256(Secret, audience: "aurora13-launch")));
        Assert.Null(verifier.Verify(SignHs256(Encoding.UTF8.GetBytes("another-secret-that-is-long-enough-000000"))));
        Assert.Null(verifier.Verify(SignHs256(Secret, lifetime: 600)));
    }

    [Fact]
    public void EachKeyAcceptsOnlyItsOwnAlgorithm()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var es256 = new LaunchAssertionVerifier(signer.ExportSubjectPublicKeyInfoPem(), Issuer);
        using var hs256 = new LaunchAssertionVerifier(Secret, Issuer);
        Assert.Null(es256.Verify(SignHs256(Secret)));
        Assert.Null(hs256.Verify(SignEs256(signer)));
        Assert.NotNull(es256.Verify(SignEs256(signer)));
    }

    [Fact]
    public void AShortSecretIsRefused() =>
        Assert.Throws<CryptographicException>(() => new LaunchAssertionVerifier(Encoding.UTF8.GetBytes("too-short"), Issuer));

    internal static string SignHs256(byte[] secret, string subject = DiscordId, string? scope = LaunchAssertionVerifier.RequiredScope,
        string audience = LaunchAssertionVerifier.Audience, bool notBefore = true, long lifetime = 60, string? name = null) =>
        Sign("HS256", subject, scope, audience, notBefore, lifetime, name, input => HMACSHA256.HashData(secret, input));

    internal static string SignEs256(ECDsa signer, string subject = DiscordId) =>
        Sign("ES256", subject, LaunchAssertionVerifier.RequiredScope, LaunchAssertionVerifier.Audience, true, 60, null,
            input => signer.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static string Sign(string algorithm, string subject, string? scope, string audience, bool notBefore, long lifetime,
        string? name, Func<byte[], byte[]> sign)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object> { ["iss"] = Issuer, ["aud"] = audience, ["sub"] = subject, ["iat"] = now,
            ["exp"] = now + lifetime, ["jti"] = Encode(RandomNumberGenerator.GetBytes(24)) };
        if (scope is not null) claims["scope"] = scope;
        if (notBefore) claims["nbf"] = now;
        if (name is not null) claims["name"] = name;
        var input = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = algorithm, typ = "JWT" })) + "." +
                    Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        return input + "." + Encode(sign(Encoding.ASCII.GetBytes(input)));
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
