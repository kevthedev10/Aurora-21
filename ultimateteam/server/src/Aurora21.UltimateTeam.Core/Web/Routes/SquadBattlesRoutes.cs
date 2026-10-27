using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Services;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>Squad Battles: the hub, opponents and their refresh, the weekly board and prize, the Team of the Week and the legacy feed.</summary>
public sealed partial class SquadBattlesRoutes : FutRouteGroup
{
    private static readonly string[] LegacyFeedTokens = { "squadbattle", "squad/battle", "squad_battle" };

    public SquadBattlesRoutes(FutServices fut) : base(fut)
    {
    }

    /// <summary>The hub, with this club's week. It writes: the week's first opponents are drawn when it is first read.</summary>
    public async ValueTask<FutResponse?> HubAsync(FutRequest r, CancellationToken cancellationToken) =>
        Is(r, $"{Game}/sqbt/user/hub", "GET") ? FutResponse.NoStore(await Context.RunAsync("sqbt-hub", Fut.SquadBattles.HubAsync, cancellationToken)) : null;

    /// <summary>The weekly "Top 200" (period=curr) and last week's board (period=prev).</summary>
    public async ValueTask<FutResponse?> LeaderboardAsync(FutRequest r, CancellationToken cancellationToken) =>
        Is(r, $"{Game}/sqbt/leaderboard")
            ? FutResponse.NoStore(await Context.ReadAsync(uow => Fut.SquadBattles.LeaderboardAsync(uow,
                (r.Query.First("period") ?? "curr").Contains("prev", StringComparison.OrdinalIgnoreCase)), cancellationToken))
            : null;

    /// <summary>Every settled weekly prize (GetSquadBattlePrize), each paid once.</summary>
    public async ValueTask<FutResponse?> PrizeAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/sqbt/user/prize"))
        {
            return null;
        }
        var prizes = await Context.RunAsync("sqbt-prize", Fut.SquadBattles.ClaimPrizeAsync, cancellationToken);
        Context.UserSessionRefresh.Set();
        return FutResponse.NoStore(prizes);
    }

    /// <summary>The frozen squad behind a hub tile.</summary>
    public async ValueTask<FutResponse?> OpponentAsync(FutRequest r, CancellationToken cancellationToken) =>
        OpponentSquadPattern().Match(r.Lower) is { Success: true } match && r.Method == "GET"
            ? FutResponse.NoStore(await Context.RunAsync("sqbt-opponent", uow => Fut.SquadBattles.SetDocumentAsync(uow, ParseLong(match)), cancellationToken))
            : null;

    /// <summary>New opponents on request (RefreshSquadBattle: a PUT with an empty body).</summary>
    public async ValueTask<FutResponse?> RefreshAsync(FutRequest r, CancellationToken cancellationToken) =>
        Is(r, $"{Game}/sqbt/v2/user/opponents/refresh", "PUT", "POST")
            ? FutResponse.NoStore(await Context.RunAsync("sqbt-refresh", Fut.SquadBattles.RefreshAsync, cancellationToken))
            : null;

    /// <summary>
    /// The Featured Squad and Team of the Week (featureConsumerId sqbt or sqbttotw): user/stats is the tile, /history the
    /// TOTW archive (a bare array, newest first) and /{id} one squad.
    /// </summary>
    public async ValueTask<FutResponse?> FeaturedSquadAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!r.Lower.StartsWith($"{Game}/featuredsquad/", StringComparison.Ordinal) || r.Method != "GET")
        {
            return null;
        }
        var consumer = (r.Query.First("featureConsumerId") ?? string.Empty).Trim().ToLowerInvariant();
        if (r.Lower == $"{Game}/featuredsquad/user/stats")
        {
            return FutResponse.NoStore(await Context.ReadAsync(uow => Fut.SquadBattles.FeaturedStatsAsync(uow, consumer), cancellationToken));
        }
        if (r.Lower == $"{Game}/featuredsquad/history")
        {
            return FutResponse.NoStore(Fut.SquadBattles.FeaturedHistory(consumer));
        }
        if (FeaturedSquadPattern().Match(r.Lower) is { Success: true } squad)
        {
            return FutResponse.NoStore(await Context.ReadAsync(uow => Fut.SquadBattles.FeaturedSquadAsync(uow, ParseLong(squad), consumer), cancellationToken));
        }
        return null;
    }

    /// <summary>A TOTW banner: CardsDLL 0x1800f92d0 fetches &lt;FUT content base&gt;squadBattle/cmn/&lt;bannerImageName&gt;.</summary>
    public FutResponse? Banner(FutRequest r)
    {
        if (BannerPattern().Match(r.Lower) is not { Success: true } match || r.Method != "GET")
        {
            return null;
        }
        return Fut.SquadBattles.Banner(match.Groups[1].Value) is { } image
            ? FutResponse.Bytes(200, image, ("Content-Type", "image/png"), ("Cache-Control", $"public, max-age={Context.Rules.SquadBattles.Totw.CacheSeconds}"))
            : FutResponse.Bytes(404, "not found"u8.ToArray(), ("Content-Type", "text/plain"));
    }

    /// <summary>A broad match: any remaining path naming Squad Battles gets the generic opponent feed.</summary>
    public async ValueTask<FutResponse?> LegacyFeedAsync(FutRequest r, CancellationToken cancellationToken) =>
        LegacyFeedTokens.Any(token => r.Lower.Contains(token, StringComparison.Ordinal)) && r.IsMethod("GET", "POST", "PUT")
            ? FutResponse.NoStore(await Context.ReadAsync(Fut.SquadBattles.LegacyOpponentFeedAsync, cancellationToken))
            : null;

    [GeneratedRegex(@"^/ut/game/fifa21/sqbt/user/opponentsquad/(\d+)$")]
    private static partial Regex OpponentSquadPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/featuredsquad/(\d+)$")]
    private static partial Regex FeaturedSquadPattern();

    [GeneratedRegex(@"^/fut/squadbattle/cmn/([a-z0-9_]+\.png)$")]
    private static partial Regex BannerPattern();
}
