using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>A verified launch assertion: the Discord id it proves and the account name the signer included, if any.</summary>
public sealed record VerifiedLaunch(string DiscordId, string? Name);

/// <summary>
/// Verifies a short-lived launch assertion (a compact JWT) signed by the Aurora account service. The launcher carries it
/// but never holds the signing key or chooses the player id. The configured key fixes the algorithm: ES256 with the
/// account service's P-256 public key (preferred: Aurora21 holds nothing that can sign), or HS256 with a secret shared
/// only between the account service and this server. A token signed with the other algorithm is refused.
/// Required claims: iss, aud = aurora21-launch, sub = the Discord id, iat, exp (at most 120 seconds after iat) and a
/// fresh jti. Optional: nbf, scope (aurora21.launch when present), name (the account's display name).
/// </summary>
public sealed class LaunchAssertionVerifier : IDisposable
{
    public const string Audience = "aurora21-launch";
    public const string RequiredScope = "aurora21.launch";
    public const int MinimumSecretBytes = 32;
    private const int MaximumAssertionLength = 4096;
    private readonly ECDsa? _key;
    private readonly byte[]? _secret;
    private readonly string _issuer;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, long> _usedIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>ES256: <paramref name="publicKeyPem"/> is the account service's P-256 public key in SPKI PEM.</summary>
    public LaunchAssertionVerifier(string publicKeyPem, string issuer, TimeProvider? time = null)
    {
        _issuer = RequireIssuer(issuer);
        if (!publicKeyPem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) ||
            publicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new CryptographicException("A public signing key in SPKI PEM format is required.");
        _time = time ?? TimeProvider.System;
        _key = ECDsa.Create();
        try
        {
            _key.ImportFromPem(publicKeyPem);
            if (_key.KeySize != 256) throw new CryptographicException("The launch assertion public key must use P-256.");
        }
        catch
        {
            _key.Dispose();
            throw;
        }
    }

    /// <summary>HS256: <paramref name="secret"/> is shared only with the account service (never another game's secret).</summary>
    public LaunchAssertionVerifier(byte[] secret, string issuer, TimeProvider? time = null)
    {
        _issuer = RequireIssuer(issuer);
        if (secret.Length < MinimumSecretBytes)
            throw new CryptographicException($"The launch assertion secret must be at least {MinimumSecretBytes} bytes.");
        _secret = secret.ToArray();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The algorithm this verifier accepts: ES256 or HS256.</summary>
    public string Algorithm => _key is not null ? "ES256" : "HS256";

    /// <summary>Returns the verified Discord id once per assertion; null for every invalid or replayed assertion.</summary>
    public string? Consume(string? assertion) => Verify(assertion)?.DiscordId;

    /// <summary>Verifies and consumes an assertion once; null for every invalid or replayed assertion.</summary>
    public VerifiedLaunch? Verify(string? assertion)
    {
        if (string.IsNullOrEmpty(assertion) || assertion.Length > MaximumAssertionLength) return null;
        var parts = assertion.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var header = JsonDocument.Parse(Decode(parts[0]));
            var type = ReadString(header.RootElement, "typ");
            if (ReadString(header.RootElement, "alg") != Algorithm || (Has(header.RootElement, "typ") && type != "JWT")) return null;
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var claims = payload.RootElement;
            var subject = ReadString(claims, "sub");
            var jti = ReadString(claims, "jti");
            if (ReadString(claims, "iss") != _issuer || ReadString(claims, "aud") != Audience ||
                (Has(claims, "scope") && ReadString(claims, "scope") != RequiredScope) ||
                subject is null || subject.Length is < 5 or > 32 || !subject.All(char.IsAsciiDigit) ||
                jti is null || jti.Length is < 22 or > 128 || !jti.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ||
                !ReadInt64(claims, "iat", out var issued) || !ReadInt64(claims, "exp", out var expires)) return null;
            var notBefore = issued;
            if (Has(claims, "nbf") && !ReadInt64(claims, "nbf", out notBefore)) return null;
            var name = ReadString(claims, "name");
            if (Has(claims, "name") && (name is null || name.Length > 128)) return null;
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            if (issued < now - 120 || issued > now + 30 || notBefore > now || notBefore < issued - 30 ||
                expires <= now || expires > issued + 120) return null;
            var signature = Decode(parts[2]);
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            lock (_gate)
            {
                if (!SignatureValid(signed, signature)) return null;
                foreach (var stale in _usedIds.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
                    _usedIds.Remove(stale);
                if (_usedIds.ContainsKey(jti)) return null;
                _usedIds.Add(jti, expires);
            }
            return new VerifiedLaunch(subject, name);
        }
        catch (Exception error) when (error is FormatException or JsonException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private bool SignatureValid(byte[] signed, byte[] signature)
    {
        if (_key is not null)
        {
            return signature.Length == 64 && _key.VerifyData(signed, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        var expected = HMACSHA256.HashData(_secret!, signed);
        return signature.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    private static string RequireIssuer(string issuer) =>
        string.IsNullOrWhiteSpace(issuer) || issuer.Length > 256
            ? throw new ArgumentException("A launch assertion issuer is required.", nameof(issuer))
            : issuer;

    private static byte[] Decode(string text)
    {
        if (text.Length == 0 || text.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new FormatException("Invalid base64url data.");
        return Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
    }

    private static bool Has(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out _);

    private static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool ReadInt64(JsonElement obj, string name, out long value)
    {
        value = 0;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    public void Dispose()
    {
        _key?.Dispose();
        if (_secret is not null) CryptographicOperations.ZeroMemory(_secret);
    }
}
