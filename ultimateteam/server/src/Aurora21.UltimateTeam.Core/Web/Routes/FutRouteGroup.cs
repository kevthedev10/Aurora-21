using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>A route answers a request, or returns null to let the routes after it try.</summary>
public delegate ValueTask<FutResponse?> FutRoute(FutRequest request, CancellationToken cancellationToken);

/// <summary>A route whose service work is still synchronous (Phase 4 Step 7 migrates each group to <see cref="FutRoute"/>).</summary>
public delegate FutResponse? SyncFutRoute(FutRequest request);

/// <summary>Shared plumbing for one feature's FUT routes.</summary>
public abstract partial class FutRouteGroup
{
    /// <summary>The FUT game API namespace every in-game path lives under.</summary>
    protected const string Game = "/ut/game/fifa21";

    protected FutRouteGroup(FutServices fut)
    {
        Fut = fut;
        Log = fut.Game.Logger("UltimateTeam.Http");
    }

    protected FutServices Fut { get; }

    protected GameContext Context => Fut.Game;

    protected ILogger Log { get; }

    /// <summary>True when the request is for exactly <paramref name="path"/> with one of <paramref name="methods"/> (any method when none are given).</summary>
    protected static bool Is(FutRequest request, string path, params string[] methods) =>
        request.Lower == path && (methods.Length == 0 || request.IsMethod(methods));

    /// <summary>The path below the UT namespace (<c>/ut/game/fifa21/x</c> and <c>/ut/fifa21/x</c> give <c>/x</c>); other paths unchanged.</summary>
    protected static string GameSuffix(FutRequest request) =>
        GameNamespacePattern().Match(request.Lower) is { Success: true } match ? "/" + match.Groups[1].Value : request.Lower;

    protected static JsonObject Template(string name, TemplateArgs? args = null) => TemplateLibrary.Core.Object(name, args);

    protected static int ParseId(Match match) =>
        int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    protected static long ParseLong(Match match) =>
        long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    [GeneratedRegex(@"^/ut/(?:game/)?[^/]+/(.*)$")]
    private static partial Regex GameNamespacePattern();
}
