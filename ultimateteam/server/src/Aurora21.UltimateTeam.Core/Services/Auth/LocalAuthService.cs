using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Core.Services.Auth;

/// <summary>
/// Strict loopback emulation of the Nucleus OAuth contract the auth bridge and Blaze SDK use.
/// Emulator-only constants: EA credentials are never requested, validated, logged or forwarded.
/// </summary>
public sealed class LocalAuthService
{
    public const string ServerClientId = "ultimateteam21-blaze-server";
    public const string SdkClientId = "ultimateteam21-blaze-sdk";
    public const string SsfClientId = "ultimateteam21-ssf";
    public const string AuthCode = "LOCAL-FUT21-AUTH-CODE";
    public const string AccessToken = "LOCAL-FUT21-ACCESS";
    public const string RefreshToken = "LOCAL-FUT21-REFRESH";

    private const int TokenLifetimeSeconds = 3600;
    private const int MaxTokenRequestBytes = 4096;
    private const int MaxTokenRequestFields = 8;
    private const int MaxTokenInfoQueryChars = 4096;
    private const int MaxTokenInfoFields = 4;

    private static readonly string[] TokenRequestFields = { "grant_type", "code", "client_id", "client_secret", "redirect_uri" };
    private static readonly string[] TokenInfoFields = { "access_token", "client_id" };

    public static readonly IReadOnlyList<string> ClientIds = new[] { ServerClientId, SdkClientId, SsfClientId };

    public static readonly IReadOnlySet<string> Paths = new HashSet<string>(StringComparer.Ordinal)
    {
        "/ultimateteam/auth/bootstrap", "/connect/token", "/connect/tokeninfo", "/ultimateteam/callback",
    };

    private static readonly Lazy<string> BuildHash = new(ComputeBuildHash);

    private readonly FutServices _fut;

    public LocalAuthService(FutServices fut) => _fut = fut;

    /// <summary>The OAuth settings Blaze PreAuth and the auth bootstrap advertise.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Config(int futPort, string? nucleusConnectOverride = null) => new KeyValuePair<string, string>[]
    {
        new("blazeServerClientId", ServerClientId),
        new("blazeSdkClientId", SdkClientId),
        new("blazeSdkClientSecret", string.Empty),
        new("nucleusConnect", nucleusConnectOverride ?? BaseUrl(futPort)),
        new("identityRedirectUri", RedirectUri(futPort)),
    };

    private static string BaseUrl(int futPort) => $"http://127.0.0.1:{futPort}/";

    private static string RedirectUri(int futPort) => BaseUrl(futPort) + "ultimateteam/callback";

    /// <summary>SHA-256 of the running server executable (reported by the auth bootstrap).</summary>
    public static string ServerBuildSha256 => BuildHash.Value;

    public AuthResponse Handle(string method, string path, string rawQuery, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        AuthResponse Reject(string error = "invalid_request") => Json(400, new JsonObject { ["error"] = error });

        if (headers.TryGetValue("authorization", out var authorization) && authorization.Length > 0)
        {
            return Reject("invalid_client");
        }

        var futPort = Core.Config.Ports.Fut;
        switch (path)
        {
            case "/ultimateteam/auth/bootstrap":
                if (method != "GET" || rawQuery.Length > 0 || body.Length > 0)
                {
                    return Reject();
                }
                return Json(200, new JsonObject
                {
                    ["version"] = 1,
                    ["serverImplementation"] = "csharp",
                    ["serverBuildSha256"] = ServerBuildSha256,
                    ["authCode"] = AuthCode,
                    ["clientIds"] = new JsonArray(ClientIds.Select(id => (JsonNode?)id).ToArray()),
                    ["config"] = new JsonObject(Config(futPort).Select(entry => KeyValuePair.Create(entry.Key, (JsonNode?)entry.Value))),
                });

            case "/ultimateteam/callback":
                return method != "GET" || body.Length > 0 ? Reject() : new AuthResponse(200, Array.Empty<byte>());

            case "/connect/token":
                {
                    var contentType = headers.TryGetValue("content-type", out var type) ? type.Split(';', 2)[0].Trim().ToLowerInvariant() : string.Empty;
                    if (method != "POST" || rawQuery.Length > 0 || body.Length > MaxTokenRequestBytes || contentType != "application/x-www-form-urlencoded")
                    {
                        return Reject();
                    }
                    string text;
                    try
                    {
                        text = new UTF8Encoding(false, true).GetString(body);
                    }
                    catch (DecoderFallbackException)
                    {
                        return Reject();
                    }
                    if (!QueryString.TryParseStrict(text, MaxTokenRequestFields, out var form))
                    {
                        return Reject();
                    }
                    if (form.Keys.Any(key => !TokenRequestFields.Contains(key)) || form.Entries.Any(entry => entry.Value.Count != 1))
                    {
                        return Reject();
                    }
                    if (form.First("grant_type") != "authorization_code")
                    {
                        return Reject("unsupported_grant_type");
                    }
                    if (!ClientIds.Contains(form.First("client_id") ?? string.Empty) || (form.First("client_secret") ?? string.Empty).Length > 0)
                    {
                        return Reject("invalid_client");
                    }
                    if (form.First("code") != AuthCode)
                    {
                        return Reject("invalid_grant");
                    }
                    if (form.ContainsKey("redirect_uri") && form.First("redirect_uri") != RedirectUri(futPort))
                    {
                        return Reject();
                    }
                    return Json(200, new JsonObject
                    {
                        ["access_token"] = AccessToken,
                        ["token_type"] = "Bearer",
                        ["expires_in"] = TokenLifetimeSeconds,
                        ["refresh_token"] = RefreshToken,
                    });
                }

            case "/connect/tokeninfo":
                {
                    if (method != "GET" || body.Length > 0 || rawQuery.Length > MaxTokenInfoQueryChars)
                    {
                        return Reject();
                    }
                    if (!QueryString.TryParseStrict(rawQuery, MaxTokenInfoFields, out var query))
                    {
                        return Reject();
                    }
                    if (query.Keys.Any(key => !TokenInfoFields.Contains(key)) || query.Entries.Any(entry => entry.Value.Count != 1))
                    {
                        return Reject();
                    }
                    if (query.First("access_token") != AccessToken)
                    {
                        return Reject("invalid_token");
                    }
                    var clientId = query.First("client_id") ?? SdkClientId;
                    if (!ClientIds.Contains(clientId))
                    {
                        return Reject("invalid_client");
                    }
                    var game = _fut.Game.Config;
                    return Json(200, new JsonObject
                    {
                        ["user_id"] = game.NucleusId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["persona_id"] = game.PersonaId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["client_id"] = clientId,
                        ["expires_in"] = TokenLifetimeSeconds,
                    });
                }
        }
        return Reject();
    }

    private static AuthResponse Json(int status, JsonNode body) => new(status, FutJson.SerializeToUtf8(body));

    private static string ComputeBuildHash()
    {
        var path = Environment.ProcessPath;
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
        }
        catch (IOException)
        {
        }
        return string.Empty;
    }
}
