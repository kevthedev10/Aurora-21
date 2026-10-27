using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>Local maintenance endpoints under /ultimateteam.</summary>
public sealed class AdminRoutes : FutRouteGroup
{
    public const string AdminTokenVariable = "ULTIMATETEAM21_ADMIN_TOKEN";
    public const string AdminTokenHeader = "X-Aurora21-Admin-Token";

    public AdminRoutes(FutServices fut) : base(fut)
    {
    }

    public FutResponse? TestStatus(FutRequest r)
    {
        if (r.Lower != "/ultimateteam/test/status")
        {
            return null;
        }
        if (!Context.TestMode || r.Method != "GET")
        {
            return FutResponse.Reason(404, "Not a test runtime");
        }
        return FutResponse.Json(Template("bootstrap/test-status", new TemplateArgs
        {
            ["runtimeRoot"] = Context.Paths.RuntimeRoot,
            ["database"] = Context.Database.Describe(),
            ["clubId"] = Context.ClubId,
        }));
    }

    public async ValueTask<FutResponse?> GrantAllCustomisationAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (r.Lower != "/ultimateteam/customisation/grant-all")
        {
            return null;
        }
        if (r.Method != "POST")
        {
            return FutResponse.Reason(405, "POST required", ("Allow", "POST"));
        }
        if (!Authorized(r))
        {
            return Forbidden();
        }
        var result = await Fut.Customisation.GrantAllAsync(cancellationToken);
        Log.LogInformation("Granted local customisation assets: {Result}", FutJson.Serialize(result));
        return FutResponse.NoStore(result);
    }

    /// <summary>
    /// Local single-player maintenance stays convenient. A shared server fails closed unless its
    /// operator sets a secret and sends it in the maintenance request header.
    /// </summary>
    private bool Authorized(FutRequest request)
    {
        if (Fut.Players?.IsShared != true)
        {
            return true;
        }
        var expected = Environment.GetEnvironmentVariable(AdminTokenVariable);
        return !string.IsNullOrWhiteSpace(expected)
               && request.Headers.TryGetValue(AdminTokenHeader, out var supplied)
               && FixedTimeEquals(expected, supplied);
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private static FutResponse Forbidden() =>
        FutResponse.Reason(403, $"shared-server maintenance requires {AdminTokenHeader}", ("Cache-Control", "no-store"));
}
