using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Services;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>The seasonal CDN document, FUT Objectives (SCMP), Division Rivals and FUT Champions.</summary>
public sealed partial class SeasonRoutes : FutRouteGroup
{
    public SeasonRoutes(FutServices fut) : base(fut)
    {
    }

    public async ValueTask<FutResponse?> HandleAsync(FutRequest r, CancellationToken cancellationToken) =>
        await ObjectivesAsync(r, cancellationToken) ?? await CompetitionsAsync(r, cancellationToken);

    /// <summary>The seasonal CDN document: the week schedule from the clock and rules only, no club.</summary>
    public FutResponse? SeasonalConfig(FutRequest r) =>
        Is(r, "/fut/seasonal/seasonalconfig.json", "GET") ? FutResponse.NoStore(Fut.Seasons.SeasonalConfig()) : null;

    private async ValueTask<FutResponse?> ObjectivesAsync(FutRequest r, CancellationToken cancellationToken)
    {
        var objectives = Fut.Objectives;
        if (Is(r, "/fut/seasonal/seasonalconfig.json", "GET"))
        {
            return FutResponse.NoStore(Fut.Seasons.SeasonalConfig());
        }
        if (r.Lower is $"{Game}/scmp/campaign/active" or $"{Game}/scmp/campaign" && r.Method == "GET")
        {
            return FutResponse.NoStore(await objectives.CampaignAsync(cancellationToken));
        }
        if (ScmpDetailsPattern().Match(r.Lower) is { Success: true } details && r.IsMethod("PUT", "POST", "GET"))
        {
            return FutResponse.NoStore(await objectives.CategoryDetailsAsync(ParseLong(details), cancellationToken));
        }
        if (Is(r, $"{Game}/scmp/world/campaign/active", "GET"))
        {
            return FutResponse.NoStore(new JsonObject());
        }
        // The World (community) campaign has no rewards here; its auto-claim (PUT, parser 0x18030b260; Python server 764ef6e)
        // gets empty award lists.
        if (Is(r, $"{Game}/scmp/world/campaign/rewards/all", "PUT"))
        {
            return FutResponse.NoStore(Template("objectives/world-autoclaim"));
        }
        if (r.Lower is $"{Game}/scmp/campaign/rewards/all" or $"{Game}/scmp/learning/campaign/rewards/all" or $"{Game}/scmp/world/campaign/rewards/all"
            && r.Method == "GET")
        {
            return FutResponse.NoStore(Template("objectives/rewards-all"));
        }
        if (ScmpLevelPattern().Match(r.Lower) is { Success: true } level && r.IsMethod("POST", "PUT"))
        {
            return FutResponse.NoStore(await objectives.ClaimLevelAsync(ParseLong(level), long.Parse(level.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), cancellationToken));
        }
        if (ScmpClaimPattern().Match(r.Lower) is { Success: true } claim && r.IsMethod("POST", "PUT"))
        {
            long? Group(int index) => claim.Groups[index].Success ? long.Parse(claim.Groups[index].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
            return FutResponse.NoStore(await objectives.ClaimAsync(Group(1), Group(2), cancellationToken));
        }
        return null;
    }

    private async ValueTask<FutResponse?> CompetitionsAsync(FutRequest r, CancellationToken cancellationToken)
    {
        var rivals = Fut.Rivals;
        var weekendLeague = Fut.WeekendLeague;
        // The Rivals and Weekend League documents write: they roll the club's record over to the current week, and the first
        // read after a week's or Weekend League's rewards time settles it for every club.
        if (Is(r, $"{Game}/rivals/user/hub", "GET"))
        {
            return FutResponse.NoStore(await Context.RunAsync("rivals-hub", rivals.HubAsync, cancellationToken));
        }
        // The weekly reward choice (RivalsSelectReward): an empty body; the path carries the chosen option's categoryId.
        if (RivalsRewardPattern().Match(r.Lower) is { Success: true } reward && r.IsMethod("PUT", "POST", "GET"))
        {
            var categoryId = ParseId(reward);
            var claimed = await Context.RunAsync("rivals-reward", uow => rivals.ClaimRewardAsync(uow, categoryId), cancellationToken);
            Context.UserSessionRefresh.Set();
            return FutResponse.NoStore(claimed);
        }
        if (Is(r, $"{Game}/rivals/user/leaderboard/previous", "GET"))
        {
            return FutResponse.NoStore(await Context.RunAsync("rivals-previous", rivals.PreviousWeekAsync, cancellationToken));
        }
        // Weekend League entry: POST with an empty body (request 0x1803774b0); the reply is only checked for success.
        if (Is(r, $"{Game}/rivals/user/weekendleague/access", "POST", "PUT"))
        {
            return FutResponse.Json(await Context.RunAsync("weekend-league-access", weekendLeague.RedeemAsync, cancellationToken));
        }
        if (Is(r, $"{Game}/user/pidinfo", "GET"))
        {
            return FutResponse.NoStore(await Context.ReadAsync(weekendLeague.PidInfoAsync, cancellationToken));
        }
        if (Is(r, $"{Game}/champion/user/registration", "POST", "PUT"))
        {
            return FutResponse.Json(await Context.RunAsync("champion-registration", uow => weekendLeague.RegisterAsync(uow, r.Payload), cancellationToken));
        }
        if (Is(r, $"{Game}/champion/user/hub", "GET"))
        {
            return FutResponse.NoStore(await Context.RunAsync("champion-hub", weekendLeague.HubAsync, cancellationToken));
        }
        if (Is(r, $"{Game}/champion/events/upcoming", "GET"))
        {
            return FutResponse.NoStore(Template("seasons/upcoming-events"));
        }
        // FUT Champions rewards: FutGrantPrizeChampionUser (reply parser 0x180301ef0).
        if (Is(r, $"{Game}/champion/user/prize", "GET", "POST", "PUT"))
        {
            var prizes = await Context.RunAsync("champion-prize", weekendLeague.ClaimPrizesAsync, cancellationToken);
            Context.UserSessionRefresh.Set();
            return FutResponse.NoStore(prizes);
        }
        if (Is(r, $"{Game}/champion/user/stats", "GET"))
        {
            return FutResponse.NoStore(await Context.ReadAsync(uow => weekendLeague.StatsAsync(uow, r.Query.First("period"), r.Query.First("type")), cancellationToken));
        }
        if (Is(r, $"{Game}/champion/leaderboard", "GET"))
        {
            // The Top 200 (Python server 764ef6e): period and type, or championEventId, and an optional region.
            long? eventId = long.TryParse(r.Query.First("championEventId"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
            return FutResponse.NoStore(await Context.ReadAsync(uow => weekendLeague.LeaderboardAsync(uow, r.Query.First("period"), r.Query.First("type"),
                r.Query.First("region"), eventId), cancellationToken));
        }
        if (ChampionFriendsPattern().Match(r.Lower) is { Success: true } friends && r.Method == "GET")
        {
            return FutResponse.NoStore(await Context.ReadAsync(uow => weekendLeague.FriendsAsync(uow, friends.Groups[1].Value, friends.Groups[2].Value), cancellationToken));
        }
        return null;
    }

    [GeneratedRegex(@"^/ut/game/fifa21/scmp/(?:active|learning|world)/category/(\d+)/details$")]
    private static partial Regex ScmpDetailsPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/scmp/(?:campaign|world/campaign)/level/(\d+)/reward/(\d+)$")]
    private static partial Regex ScmpLevelPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/scmp/campaign/(?:group/(\d+)/(?:objective/(\d+)/)?rewards|rewards/all)$")]
    private static partial Regex ScmpClaimPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/rivals/reward/(\d+)$")]
    private static partial Regex RivalsRewardPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/champion/type/([^/]+)/period/([^/]+)/friends/stats$")]
    private static partial Regex ChampionFriendsPattern();
}
