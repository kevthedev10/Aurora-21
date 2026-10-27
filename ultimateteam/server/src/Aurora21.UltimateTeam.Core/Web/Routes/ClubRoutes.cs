using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>The user, club overview, squads, stadium and credits.</summary>
public sealed partial class ClubRoutes : FutRouteGroup
{
    public ClubRoutes(FutServices fut) : base(fut)
    {
    }

    public async ValueTask<FutResponse?> UserAsync(FutRequest r, CancellationToken cancellationToken) => r.Lower switch
    {
        $"{Game}/user" => FutResponse.Json(await Context.ReadAsync(uow => Fut.Club.NativeUserAsync(uow), cancellationToken)),
        $"{Game}/userdata" => FutResponse.Template("bootstrap/userdata"),
        // FUT Friendlies opens its hub with subType=DYNAMIC alone. EA's own hub sample (ea8ae7a) answers BASIC and
        // DYNAMIC rules in one list, and CardsDLL (0x180356855) keeps a DYNAMIC rule only in the Live Friendlies flow, so
        // every subType gets both: the nine game types, plus this week's Live Friendlies events, without which that
        // screen opened blank (10-08).
        $"{Game}/houserules/user/hub" when r.Method == "GET" => FutResponse.NoStore(HouseRulesHub(DateTimeOffset.UtcNow)),
        "/ut/v2/game/fifa21/store/transaction" => FutResponse.Template("bootstrap/store-transaction"),
        _ => null,
    };

    public async ValueTask<FutResponse?> StadiumAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/stadium"))
        {
            return null;
        }
        if (r.IsMethod("PUT", "POST") && r.Payload is JsonObject body)
        {
            // FIFA reads an error status as a server failure; a refused save is logged and the stadium as stored is returned.
            try { await Fut.Customisation.SaveStadiumAsync(body,cancellationToken); }
            catch(Models.ClubRuleException error) { Log.LogWarning("My Stadium save refused: {Reason}", error.Message); }
        }
        return FutResponse.NoStore(await Context.ReadAsync(uow => Fut.Club.StadiumResponseAsync(uow), cancellationToken));
    }

    /// <summary>The header FIFA stamps with the screen a FUT request was issued from.</summary>
    private const string PerformanceGroupHeader = "X-PERFORMANCE-GROUP";

    public async ValueTask<FutResponse?> SquadsAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (Is(r, $"{Game}/squad/active"))
        {
            // The online pre-match screen's zero-persona opponent fetch gets the live opponent; every other caller its own squad.
            if (r.Method == "GET" && Context.Rules.Seasons.OnlineMatch.IsPrematch(r.Headers.GetValueOrDefault(PerformanceGroupHeader))
                && await Fut.Matches.PrematchActiveSquadAsync(cancellationToken) is { } opponent)
            {
                return FutResponse.NoStore(opponent);
            }
            return FutResponse.Json(await Context.ReadAsync(uow => Fut.Club.NativeSquadAsync(uow), cancellationToken));
        }
        if (SquadActiveUserPattern().Match(r.Lower) is { Success: true } activeUser && r.Method == "GET")
        {
            // The Rivals pre-match opponent fetch; an unresolved persona falls through to the unknown answer, as before.
            return await Fut.Matches.ActiveSquadForPersonaAsync(ParseLong(activeUser), IncludeStadium(r), cancellationToken) is { } squad
                ? FutResponse.NoStore(squad)
                : null;
        }
        if (Is(r, $"{Game}/user/list", "GET"))
        {
            var personas = (r.Query.First("personaIdList") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0);
            return FutResponse.NoStore(await Fut.Matches.UserListAsync(personas, cancellationToken));
        }
        if (Is(r, $"{Game}/squad/list", "GET"))
        {
            return FutResponse.NoStore(await Context.ReadAsync(Fut.Club.SquadListAsync, cancellationToken));
        }
        if (Is(r, $"{Game}/squad", "POST"))
        {
            return await SaveSquadAsync(() => Fut.Inventory.CreateSquadAsync(r.Payload, cancellationToken), cleanLoans: false, noStore: true);
        }
        if (SquadUserPattern().Match(r.Lower) is { Success: true } user && r.Method == "GET")
        {
            var squadId = ParseLong(user);
            var persona = long.Parse(user.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (squadId == Fut.Draft.SquadId)
            {
                // The Draft squad by persona: the caller's own run, or in an Online Draft pairing the opponent's.
                var draft = await Fut.Draft.SquadForPersonaAsync(await Fut.Draft.SlotForAsync(r, cancellationToken), persona, cancellationToken);
                return draft is null ? FutResponse.NoStore(new JsonObject { ["reason"] = "DRAFT_NOT_ACTIVE" }, 404) : FutResponse.NoStore(draft);
            }
            // The paired opponent's persona (the Weekend League pre-match) gets that club's squad; any other persona keeps
            // the caller's own squad, as before.
            if (persona != Context.Config.PersonaId
                && await Fut.Matches.OpponentSquadForPersonaAsync(persona, cancellationToken) is { } opponent)
            {
                return FutResponse.NoStore(opponent);
            }
            var squad = await Context.ReadAsync(async uow => await uow.GetSquadAsync(squadId) is null
                ? null : await Fut.Club.NativeSquadAsync(uow, squadId: squadId), cancellationToken);
            return squad is null ? SquadNotFound() : FutResponse.NoStore(squad);
        }
        if (CloneSquadPattern().Match(r.Lower) is { Success: true } clone && r.Method is "POST" or "PUT" or "GET")
        {
            return await CloneSquadAsync(ParseLong(clone), r.Payload, null, cancellationToken);
        }
        // FIFA 21's "Copy squad" posts {"srcId":1,"dstSquadName":"COPY ..."} to /squad/clone (live 10-04: it was unknown, so
        // the copy never appeared). The client reads nothing from the reply and fetches squad/list again.
        if (Is(r, $"{Game}/squad/clone", "POST", "PUT"))
        {
            var body = r.Payload as JsonObject;
            if (body?.Get("srcId") is not { } source || !JsonValues.TryInt(source, out var sourceId))
            {
                return FutResponse.NoStore(new JsonObject { ["reason"] = "SQUAD_NOT_FOUND" }, 404);
            }
            return await CloneSquadAsync(sourceId, null, body.Str("dstSquadName"), cancellationToken);
        }
        // FIFA's club delete (CardsDLL ut/delete/game/fifa21/user). Clubs are not deleted from the game: the unknown endpoint's
        // empty 200 would have told the client the club was gone.
        if (r.Lower == "/ut/delete/game/fifa21/user")
        {
            Log.LogWarning("Club delete refused: clubs are not deleted from the game");
            return FutResponse.NoStore(new JsonObject { ["reason"] = "CLUB_DELETE_NOT_ALLOWED" }, 403);
        }
        // FIFA 21 PC deletes a squad with GET /ut/delete/game/fifa21/squad/{id}.
        if (DeleteSquadPattern().Match(r.Lower) is { Success: true } deleted)
        {
            return await DeleteSquadAsync(ParseLong(deleted), cancellationToken);
        }
        if (SquadIdPattern().Match(r.Lower) is not { Success: true } match)
        {
            return null;
        }
        var id = ParseLong(match);
        switch (r.Method)
        {
            case "GET":
                var squad = await Context.ReadAsync(async uow => id == 0 || await uow.GetSquadAsync(id) is not null
                    ? await Fut.Club.NativeSquadAsync(uow, squadId: id) : null, cancellationToken);
                return squad is not null ? FutResponse.NoStore(squad) : SquadNotFound();
            case "PUT" or "POST" when id == 0:
                return await SaveSquadAsync(() => Fut.Inventory.CreateSquadAsync(r.Payload, cancellationToken), cleanLoans: false, noStore: true);
            case "PUT" or "POST":
                var source = r.Payload as JsonObject ?? new JsonObject();
                source["id"] = id;
                source["squadId"] = id;
                // The first-run loan-explore save carries onboarding=true (CardsDLL 0x1097A0): it records the loan marker.
                var onboarding = string.Equals(r.Query.First("onboarding"), "true", StringComparison.OrdinalIgnoreCase);
                return await SaveSquadAsync(() => Fut.Inventory.SaveSquadAsync(source, cancellationToken), cleanLoans: true, noStore: false,
                    onboarding: onboarding);
            case "DELETE":
                return await DeleteSquadAsync(id, cancellationToken);
            default:
                return null;
        }
    }

    private async ValueTask<FutResponse> SaveSquadAsync(Func<ValueTask<JsonObject>> save, bool cleanLoans, bool noStore, bool onboarding = false)
    {
        try
        {
            var saved = await save();
            if (onboarding)
            {
                await Fut.Onboarding.RecordLoanSquadAsync(saved);
            }
            if (cleanLoans)
            {
                await Fut.Onboarding.CleanupStaleLoansAsync(saved);
            }
            var body = new JsonObject { ["id"] = SquadDocuments.Id(saved) };
            return noStore ? FutResponse.NoStore(body) : FutResponse.Json(body);
        }
        catch (Models.ClubRuleException error)
        {
            Log.LogWarning("Squad save rejected: {Reason}", error.Message);
            return FutResponse.NoStore(new JsonObject { ["reason"] = error.Message }, 400);
        }
    }

    /// <summary>The copy as the squad route serves it, under the new squad's id.</summary>
    private async ValueTask<FutResponse> CloneSquadAsync(long sourceId, JsonNode? payload, string? name, CancellationToken cancellationToken)
    {
        try
        {
            var created = await Fut.Inventory.CloneSquadAsync(sourceId, payload, name, cancellationToken);
            if (created is null)
            {
                return SquadNotFound();
            }
            var createdId = SquadDocuments.Id(created);
            Log.LogInformation("Squad cloned from={Source} to={Squad}", sourceId, createdId);
            return FutResponse.NoStore(await Context.ReadAsync(uow => Fut.Club.NativeSquadAsync(uow, squadId: createdId), cancellationToken));
        }
        catch (Models.ClubRuleException error)
        {
            Log.LogWarning("Squad clone rejected: {Reason}", error.Message);
            return FutResponse.NoStore(new JsonObject { ["reason"] = error.Message }, 400);
        }
    }

    private async ValueTask<FutResponse> DeleteSquadAsync(long squadId, CancellationToken cancellationToken)
    {
        try
        {
            var found = await Fut.Inventory.DeleteSquadAsync(squadId, cancellationToken);
            Log.LogInformation("Squad deleted id={Squad} found={Found}", squadId, found);
            return FutResponse.NoStore(await Context.ReadAsync(Fut.Club.SquadListAsync, cancellationToken));
        }
        catch (Models.ClubRuleException error)
        {
            Log.LogWarning("Squad delete rejected id={Squad}: {Reason}", squadId, error.Message);
            return FutResponse.NoStore(new JsonObject { ["reason"] = error.Message }, 400);
        }
    }

    /// <summary>
    /// The Friendlies hub list: the nine game types, then the Live Friendlies events, which run Monday to Monday (UTC) so
    /// one is always live. FIFA reads startTime/endTime as Unix seconds.
    /// </summary>
    internal static JsonObject HouseRulesHub(DateTimeOffset now)
    {
        var monday = now.UtcDateTime.Date.AddDays(-(((int)now.UtcDateTime.DayOfWeek + 6) % 7));
        var start = new DateTimeOffset(monday, TimeSpan.Zero);
        var hub = TemplateLibrary.Core.Object("friendlies/house-rules-basic");
        var rules = hub["houseRules"]!.AsArray();
        var live = TemplateLibrary.Core.Array("friendlies/house-rules-live", new TemplateArgs
        {
            ["startTime"] = start.ToUnixTimeSeconds(),
            ["endTime"] = start.AddDays(7).ToUnixTimeSeconds(),
        });
        foreach (var rule in live.ToArray())
        {
            live.Remove(rule);
            rules.Add(rule);
        }
        return hub;
    }

    private static FutResponse SquadNotFound() => FutResponse.NoStore(new JsonObject { ["reason"] = "SQUAD_NOT_FOUND" }, 404);

    /// <summary>Whether a squad fetch asks for the squad's venue too (includeMyStadium; 0, false, no or empty mean no).</summary>
    private static bool IncludeStadium(FutRequest r) =>
        (r.Query.First("includeMyStadium") ?? "0").Trim().ToLowerInvariant() is not ("0" or "" or "false" or "no");

    /// <summary>Custom tactics (tactics?squadId=N), read back from the squad document; the reply stays a bare {}.</summary>
    public async ValueTask<FutResponse?> TacticsAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/tactics", "POST", "PUT"))
        {
            return null;
        }
        var saved = await Fut.Inventory.SaveTacticsAsync(r.Query, r.Payload, cancellationToken);
        if (Fut.Inventory.TacticEntries(r.Query, r.Payload).Any(entry => entry.SquadId == Fut.Draft.SquadId))
        {
            saved += await Fut.Draft.SaveTacticsAsync(await Fut.Draft.SlotForAsync(r, cancellationToken), r.Query, r.Payload, cancellationToken);
        }
        Log.LogInformation("Custom tactics saved={Saved}", saved);
        return FutResponse.Json(new JsonObject());
    }

    public async ValueTask<FutResponse?> OverviewAsync(FutRequest r, CancellationToken cancellationToken) => r.Lower switch
    {
        // The FUT News screen's message (rules/club.json news).
        $"{Game}/livemessage" => FutResponse.NoStore(Template("club/live-messages", new TemplateArgs
        {
            ["messageId"] = Context.Rules.Club.News.MessageId,
            ["title"] = Context.Rules.Club.News.Title,
            ["message"] = Context.Rules.Club.News.Message,
        })),
        // GET livemessage/template?screen=futlivemsggen4&pagesize=5 (parser 0x18030c740; Python server 764ef6e): no
        // messages keeps FUT going without a news screen.
        $"{Game}/livemessage/template" => FutResponse.NoStore(Template("club/live-message-template")),
        $"{Game}/eventfeed" => FutResponse.Json(Template("club/event-feed", new TemplateArgs { ["now"] = Context.NowSeconds })),
        // The hub body is sent unchanged, so it is written straight from its template.
        $"{Game}/hub" => await Context.ReadAsync(uow => Fut.Club.HubBodyAsync(uow), cancellationToken) is var hub
            ? FutResponse.Template(hub.Template, hub.Args)
            : throw new InvalidOperationException("unreachable"),
        $"{Game}/clubuser" => FutResponse.Json(Template("club/club-user", new TemplateArgs
        {
            ["persona"] = Context.Config.DisplayName,
            ["personaId"] = Context.Config.PersonaId,
        })),
        _ => null,
    };

    /// <summary>
    /// Leaderboards: the options document, and every board (FIFA appends each permutation url to
    /// /leaderboards) as ranked entries. The Squad Battles board is the weekly competition's (SquadBattlesRoutes).
    /// </summary>
    public async ValueTask<FutResponse?> LeaderboardsAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (r.Lower == $"{Game}/leaderboards/options")
        {
            return FutResponse.NoStore(Fut.Leaderboards.Options());
        }
        var period = LeaderboardSegment("period").Match(r.Lower) is { Success: true } periodMatch ? periodMatch.Groups[1].Value : null;
        if (LeaderboardDetailsPattern().Match(r.Lower) is { Success: true } details
            && long.TryParse(details.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            return FutResponse.NoStore(await Context.ReadAsync(uow => Fut.Leaderboards.DetailsAsync(uow, id, period), cancellationToken));
        }
        if (r.Lower.StartsWith($"{Game}/leaderboards", StringComparison.Ordinal))
        {
            // FIFA appends the permutation url (/period/{period}/category/{category}/view/{view}); ids match in any case.
            var category = LeaderboardSegment("category").Match(r.Lower) is { Success: true } board ? board.Groups[1].Value : null;
            var view = LeaderboardSegment("view").Match(r.Lower) is { Success: true } compare ? compare.Groups[1].Value : null;
            return FutResponse.NoStore(await Context.ReadAsync(uow => Fut.Leaderboards.EntriesAsync(uow, category, view, period), cancellationToken));
        }
        return null;
    }

    /// <summary>GET leaderboards/period/{period}/user/{id}, optionally ?group=SEASONS (CardsDLL 0x180148b71).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"/leaderboards/period/[a-z0-9_]+/user/(\d+)$")]
    private static partial System.Text.RegularExpressions.Regex LeaderboardDetailsPattern();

    private static System.Text.RegularExpressions.Regex LeaderboardSegment(string name) => name switch
    {
        "period" => LeaderboardPeriodPattern(),
        "category" => LeaderboardCategoryPattern(),
        _ => LeaderboardViewPattern(),
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"/leaderboards/(?:.*/)?period/([a-z0-9_]+)(?:/|$)")]
    private static partial System.Text.RegularExpressions.Regex LeaderboardPeriodPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"/leaderboards/(?:.*/)?category/([a-z0-9_]+)(?:/|$)")]
    private static partial System.Text.RegularExpressions.Regex LeaderboardCategoryPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"/leaderboards/(?:.*/)?view/([a-z0-9_]+)(?:/|$)")]
    private static partial System.Text.RegularExpressions.Regex LeaderboardViewPattern();

    public async ValueTask<FutResponse?> StatsAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!r.Lower.StartsWith($"{Game}/club/stats/", StringComparison.Ordinal))
        {
            return null;
        }
        return r.Lower[$"{Game}/club/stats/".Length..] switch
        {
            "staff" => FutResponse.NoStore(Template("club/staff-stats")),
            "consumables" => FutResponse.NoStore(await Context.ReadAsync(Fut.Club.ConsumableStatsAsync, cancellationToken)),
            _ => FutResponse.Json(await Context.ReadAsync(Fut.Club.ClubStatsAsync, cancellationToken)),
        };
    }

    /// <summary>Club &gt; Consumables and the apply screen, by category.</summary>
    public async ValueTask<FutResponse?> ConsumablesAsync(FutRequest r, CancellationToken cancellationToken) =>
        ConsumablesPattern().Match(r.Lower) is { Success: true } match && match.Groups[1].Value != "stats" && r.Method == "GET"
            ? FutResponse.NoStore(await Context.ReadAsync(uow => Fut.Club.ConsumableSearchAsync(uow, match.Groups[1].Value, r.Query), cancellationToken))
            : null;

    public async ValueTask<FutResponse?> SearchAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/club"))
        {
            return null;
        }
        var body = await Context.ReadAsync(uow => Fut.Search.SearchBodyAsync(uow, r.Query, r.Payload), cancellationToken);
        return body.Document is { } document ? FutResponse.Json(document) : FutResponse.Template(body.Template!, body.Args);
    }

    public async ValueTask<FutResponse?> CreditsAsync(FutRequest r, CancellationToken cancellationToken) =>
        Is(r, $"{Game}/user/credits")
            ? FutResponse.Json(await Context.ReadAsync(Services.Club.ClubPresentationService.CreditsPayloadAsync, cancellationToken))
            : null;

    /// <summary>
    /// CardsDLL's bulk start-up document (/usermassinfo in any UT namespace). A club FIFA has yet to create answers 465,
    /// which FIFA reads as "no club" (native result 1, CardsDLL 0x37E0A0) and answers with its country and club screens;
    /// a 404 would be an unexpected error (ultimateteam/onboarding.md).
    /// </summary>
    public async ValueTask<FutResponse?> MassInfoAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (GameSuffix(r) is not ("/usermassinfo" or "/user/massinfo"))
        {
            return null;
        }
        if (await Fut.Onboarding.IsAwaitingCreationAsync(cancellationToken))
        {
            return FutResponse.NoStore(Template("onboarding/no-club"), NoClubStatus);
        }
        return FutResponse.Json(await Context.ReadAsync(Fut.Club.UserMassInfoAsync, cancellationToken));
    }

    /// <summary>The status FIFA reads as "this persona has no FUT club yet" (native result 1).</summary>
    private const int NoClubStatus = 465;

    [GeneratedRegex(@"^/ut/game/fifa21/squad/(\d+)/user/(\d+)$")]
    private static partial Regex SquadUserPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/squad/active/user/(\d+)$")]
    private static partial Regex SquadActiveUserPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/squad/(\d+)/clone$")]
    private static partial Regex CloneSquadPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/squad/(\d+)$")]
    private static partial Regex SquadIdPattern();

    [GeneratedRegex(@"^/ut/delete/game/fifa21/squad/(\d+)$")]
    private static partial Regex DeleteSquadPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/club/consumables/([a-z]+)$")]
    private static partial Regex ConsumablesPattern();
}
