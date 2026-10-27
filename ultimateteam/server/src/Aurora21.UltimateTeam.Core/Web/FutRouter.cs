using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Web.Routes;

namespace Aurora21.UltimateTeam.Core.Web;

/// <summary>
/// The FUT/POW/local-auth HTTP API. Routes are evaluated in a fixed order because several
/// broad matches (e.g. any path containing "squadbattle" or "playerpick") must only apply after
/// the specific routes before them. A route may decline a request (return null) to fall through.
/// </summary>
public sealed class FutRouter
{

    private readonly GameContext _game;
    private readonly ILogger _log;
    private readonly ScoutLog _scout;
    private readonly SyncFutRoute _localAuth;
    private readonly SyncFutRoute _anonymousLocalAuth;
    private readonly IReadOnlyList<FutRoute> _routes;
    private readonly IReadOnlyList<FutRoute> _anonymousRoutes;
    private readonly Dictionary<FutRoute, string> _routeNames = new();

    public FutRouter(FutServices fut)
    {
        _game = fut.Game;
        _log = fut.Game.Logger("UltimateTeam.Http");
        _scout = new ScoutLog(_log);

        var admin = new AdminRoutes(fut);
        var bootstrap = new BootstrapRoutes(fut);
        var content = new ContentRoutes(fut);
        var draft = new DraftRoutes(fut);
        var sbc = new SbcRoutes(fut);
        var club = new ClubRoutes(fut);
        var matches = new MatchRoutes(fut);
        var squadBattles = new SquadBattlesRoutes(fut);
        var seasons = new SeasonRoutes(fut);
        var onboarding = new OnboardingRoutes(fut);
        var store = new StoreRoutes(fut);
        var picks = new PlayerPickRoutes(fut);
        var market = new MarketRoutes(fut);
        var items = new ItemRoutes(fut);

        _localAuth = request => bootstrap.LocalAuth(request, noteMathGroup: true);
        _anonymousLocalAuth = request => bootstrap.LocalAuth(request, noteMathGroup: false);
        // What a request without a verified player may reach (the session identity mode): shared CDN content only, never
        // a club. Every other path is refused by the caller.
        _anonymousRoutes = new FutRoute[]
        {
            Sync(content.SbcImage),
            Sync(squadBattles.Banner),
            content.StaticContentAsync,
            content.PlayerHeadAsync,
            content.ItemBackgroundAsync,
            Sync(content.HouseRuleBackground),
            Sync(content.StaticLocalization),
            Sync(seasons.SeasonalConfig),
        };
        _routes = new FutRoute[]
        {
            Sync(admin.TestStatus),
            admin.GrantAllCustomisationAsync,
            bootstrap.PowAsync,
            bootstrap.UtAuthAsync,
            // Draft owns the broad "/grant/award" and ".../stats" matches.
            draft.HandleAsync,
            Sync(bootstrap.Shards),
            Sync(bootstrap.ResourceMapping),
            bootstrap.AccountInfoAsync,
            bootstrap.ClubAccountInfoAsync,
            Sync(bootstrap.Friends),
            Sync(content.SbcImage),
            // Before the cached CDN content and the broad Squad Battles match.
            Sync(squadBattles.Banner),
            content.StaticContentAsync,
            content.PlayerHeadAsync,
            content.ItemBackgroundAsync,
            Sync(content.HouseRuleBackground),
            Sync(content.StaticLocalization),
            // Answers every remaining "/sbs/" path.
            sbc.HandleAsync,
            Sync(bootstrap.Settings),
            club.StadiumAsync,
            items.AttributeMetadataAsync,
            Sync(bootstrap.TrustedDevice),
            matches.EndAsync,
            Sync(matches.KeepAlive),
            Sync(matches.Ready),
            Sync(matches.Reset),
            matches.CreateAsync,
            matches.OfflineSquadAsync,
            squadBattles.HubAsync,
            squadBattles.LeaderboardAsync,
            squadBattles.PrizeAsync,
            seasons.HandleAsync,
            squadBattles.OpponentAsync,
            squadBattles.RefreshAsync,
            squadBattles.FeaturedSquadAsync,
            // Any remaining path naming Squad Battles.
            squadBattles.LegacyFeedAsync,
            // FIFA's club creation (GetHistorical, the country action, CreateUser) before the user read.
            onboarding.CreationAsync,
            club.UserAsync,
            club.TacticsAsync,
            onboarding.ClientDataAsync,
            onboarding.ChoicesAsync,
            // Before the generic squad save: Draft rearrangements must never replace a real squad.
            draft.SquadAsync,
            club.SquadsAsync,
            club.OverviewAsync,
            club.LeaderboardsAsync,
            club.StatsAsync,
            club.ConsumablesAsync,
            club.SearchAsync,
            club.CreditsAsync,
            store.PurchaseGroupsAsync,
            store.PurchasedItemsAsync,
            // Any remaining path naming a player pick, and item actions on pick tokens.
            picks.HandleAsync,
            // Shared-market paths must run before the generic item actions below.
            market.HandleAsync,
            items.ApplyConsumableAsync,
            items.LookupsAsync,
            items.DiscardsAsync,
            items.UpdatesAsync,
            club.MassInfoAsync,
            Sync(items.Definitions),
        };
        foreach (var route in _routes) _routeNames.TryAdd(route, route.Method.DeclaringType!.Name + "." + route.Method.Name);
    }

    /// <summary>Adapts a route group whose services are still synchronous to the async route list (until Phase 4 Step 7).</summary>
    private FutRoute Sync(SyncFutRoute route)
    {
        FutRoute adapted = (request, _) => ValueTask.FromResult(route(request));
        _routeNames[adapted] = route.Method.DeclaringType!.Name + "." + route.Method.Name;
        return adapted;
    }

    /// <summary>
    /// Answers a request that carries no verified player (the session identity mode): the local auth endpoints (without
    /// noting a maths group, which needs a player), shared CDN content, and for any other <c>GET /fut/</c> asset the empty
    /// unknown-endpoint answer. Null for everything else: the caller refuses it, and no club route ever runs.
    /// </summary>
    public async ValueTask<FutResponse?> HandleAnonymousAsync(FutRequest request, CancellationToken cancellationToken = default)
    {
        using var content = _game.Live.Pin();
        if (_anonymousLocalAuth(request) is { } auth)
        {
            return auth;
        }
        if (request.Method != "GET" || !request.Lower.StartsWith("/fut/", StringComparison.Ordinal))
        {
            return null;
        }
        foreach (var route in _anonymousRoutes)
        {
            if (await route(request, cancellationToken) is { } response)
            {
                return response;
            }
        }
        // Any other /fut/ asset gets the same empty unknown-endpoint answer it gets in the other modes, never a club route.
        // (Card-background DDS files, observed 2026-09-27, are ContentRoutes.ItemBackgroundAsync above.)
        return Unknown(request);
    }

    /// <summary>Answers one request; the first route that returns a response wins.</summary>
    public async ValueTask<FutResponse> HandleAsync(FutRequest request, CancellationToken cancellationToken = default)
    {
        // Every read of this request comes from one content revision, even if a reload lands meanwhile.
        using var content = _game.Live.Pin();
        using var timing = new DatabaseTimings.EndpointScope(request.Method);
        timing.Route("BootstrapRoutes.LocalAuth");
        if (_localAuth(request) is { } auth)
        {
            timing.Status = auth.Status;
            return auth;
        }
        if (_game.Config.CaptureDiagnostics)
        {
            _scout.Record(request);
        }
        foreach (var route in _routes)
        {
            var routeName = _routeNames[route];
            if (timing.Enabled && routeName == "MarketRoutes.HandleAsync")
            {
                // This group handles several endpoints; IDs and query strings never become metric labels.
                var operation = request.Lower.Contains("transfermarket", StringComparison.Ordinal) ? "search"
                    : request.Lower.Contains("marketdata/pricelimits", StringComparison.Ordinal) ? "price-limits"
                    : request.Lower.Contains("watch", StringComparison.Ordinal) ? "watchlist"
                    : (request.Lower.EndsWith("/offer", StringComparison.Ordinal) || request.Lower.EndsWith("/bid", StringComparison.Ordinal)) ? "bid"
                    : request.Lower.Contains("buy", StringComparison.Ordinal) ? "buy-now"
                    : request.Lower.Contains("status", StringComparison.Ordinal) ? "status"
                    : request.Lower.Contains("sold", StringComparison.Ordinal) ? "clear-sold"
                    : request.Lower.Contains("auctionhouse", StringComparison.Ordinal) ? "auctionhouse"
                    : "tradepile";
                routeName += "." + operation;
            }
            timing.Route(routeName);
            if (await route(request, cancellationToken) is { } response)
            {
                timing.Status = response.Status;
                return response;
            }
        }
        timing.Route("unknown");
        timing.Status = 200;
        return Unknown(request);
    }

    /// <summary>An endpoint no route answers: an empty 200, recorded in the unknown-request file.</summary>
    private FutResponse Unknown(FutRequest request)
    {
        // Every occurrence goes to the dedicated unknown-request file; the main log names each endpoint once.
        if (_game.UnknownRequests is { } unknown && unknown.Record(request))
        {
            _log.LogWarning("New unknown FUT/POW endpoint: {Method} {Endpoint}; occurrences are recorded in {File}",
                request.Method, UnknownRequestLog.PathPattern(request.Path), unknown.FilePath);
        }
        return FutResponse.Json(new JsonObject(), 200, ("X-UltimateTeam-Unknown", "1"));
    }
}
