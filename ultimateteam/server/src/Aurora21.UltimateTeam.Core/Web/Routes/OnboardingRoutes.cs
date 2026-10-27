using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>First-run onboarding: client-data progress documents, starter loans, kits and badge.</summary>
public sealed partial class OnboardingRoutes : FutRouteGroup
{
    public OnboardingRoutes(FutServices fut) : base(fut)
    {
    }

    private OnboardingService Onboarding => Fut.Onboarding;

    /// <summary>
    /// FIFA's own club creation (ultimateteam/onboarding.md): GetHistorical, the country screen's action, CreateUser and
    /// the club-name screen that follows onboarding.
    /// CreateUser answers clubs FIFA creates, including deliberately reset local clubs; every other
    /// <c>/user</c> request is the user read (ClubRoutes.UserAsync, after this route).
    /// </summary>
    public async ValueTask<FutResponse?> CreationAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (Is(r, $"{Game}/user/historical", "GET"))
        {
            return FutResponse.NoStore(Onboarding.Historical());
        }
        if (CountryPattern().Match(r.Lower) is { Success: true } country && r.IsMethod("POST", "PUT"))
        {
            return FutResponse.NoStore(await Onboarding.SelectCountryAsync(ParseLong(country), cancellationToken));
        }
        if (r.Lower == $"{Game}/user" && r.IsMethod("POST", "PUT") && await Onboarding.UsesNativeCreationAsync(cancellationToken))
        {
            return FutResponse.NoStore(await Onboarding.CreateClubAsync(r.Payload, cancellationToken));
        }
        // ChangeClubName: the club-name screen's Apply (CardsDLL builds user + /club?profaneNameChange=%s).
        if (r.Lower == $"{Game}/user/club" && r.IsMethod("POST", "PUT"))
        {
            return FutResponse.NoStore(await Onboarding.RenameClubAsync(r.Payload, r.Query.First("profaneNameChange"), cancellationToken));
        }
        return null;
    }

    public async ValueTask<FutResponse?> ClientDataAsync(FutRequest r, CancellationToken cancellationToken) => r.Lower switch
    {
        // Intro "seen" markers are stored but only read back at login; replies stay what FIFA accepted
        // (echoing stored rivals markers in the reply once crashed squad edits).
        $"{Game}/clientdata/tutorialpopups" => await SaveSilentlyAsync(r, "tutorialpopups", "client-data/tutorial-popups", cancellationToken),
        $"{Game}/clientdata/rivals" => await SaveSilentlyAsync(r, "rivals", null, cancellationToken),
        $"{Game}/clientdata/squadbuildingsets" => await SaveSilentlyAsync(r, "squadbuildingsets", null, cancellationToken),
        $"{Game}/clientdata/worldevents" => await SaveSilentlyAsync(r, "worldevents", null, cancellationToken),
        $"{Game}/clientdata/onboarding" => await SaveThenReadAsync(r, "onboarding", Onboarding.CompletedOnboardingClientDataAsync, cancellationToken),
        $"{Game}/clientdata/squad" => await SaveThenReadAsync(r, "squad", Onboarding.SquadClientDataAsync, cancellationToken),
        $"{Game}/clientdata/pilesize" => FutResponse.Template("client-data/pile-size"),
        $"{Game}/clientdata/userhubdata" => await SaveSilentlyAsync(r, "userhubdata", "client-data/user-hub-data", cancellationToken),
        $"{Game}/clientdata/managerquest" => FutResponse.Template("client-data/manager-quest"),
        $"{Game}/clientdata/loanplayerreward" => await SaveThenReadAsync(r, "loanplayerreward", Onboarding.CompletedLoanClientDataAsync, cancellationToken),
        _ => null,
    };

    public async ValueTask<FutResponse?> ChoicesAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (Is(r, $"{Game}/onboarding/kits"))
        {
            return r.Method switch
            {
                "GET" => FutResponse.Json(await Context.ReadAsync(Onboarding.KitChoicesAsync, cancellationToken)),
                "POST" => FutResponse.Json(await Onboarding.SelectKitsAsync(r.Payload, cancellationToken)),
                _ => FutResponse.Reason(405, "unsupported kit-selection method"),
            };
        }
        if (Is(r, $"{Game}/onboarding/badges", "GET"))
        {
            return FutResponse.Json(await Context.ReadAsync(Onboarding.BadgeChoicesAsync, cancellationToken));
        }
        if (BadgePattern().Match(r.Lower) is { Success: true } badge && r.Method == "POST")
        {
            return FutResponse.Json(await Onboarding.SelectBadgeAsync(ParseLong(badge), cancellationToken));
        }
        if (Is(r, $"{Game}/loan/players"))
        {
            return FutResponse.Json(await Context.ReadAsync(Onboarding.LoanOffersAsync, cancellationToken));
        }
        if (LoanPattern().Match(r.Lower) is { Success: true } loan && r.IsMethod("PUT", "POST"))
        {
            return FutResponse.Json(await Onboarding.SignLoanAsync(ParseLong(loan), cancellationToken));
        }
        return null;
    }

    /// <summary>A PUT stores the client's entries; the reply is the fixed template (or {}) whatever was stored.</summary>
    private async ValueTask<FutResponse> SaveSilentlyAsync(FutRequest r, string kind, string? template, CancellationToken cancellationToken)
    {
        if (r.Method == "PUT")
        {
            await Onboarding.SaveClientDataAsync(kind, r.Payload, cancellationToken);
        }
        return template is null ? FutResponse.Json(new JsonObject()) : FutResponse.Template(template);
    }

    /// <summary>A PUT stores the client's entries first; every method answers with the current document.</summary>
    private async ValueTask<FutResponse> SaveThenReadAsync(FutRequest r, string kind,
        Func<Data.UnitOfWork, ValueTask<JsonObject>> current, CancellationToken cancellationToken)
    {
        if (r.Method == "PUT" && await Onboarding.SaveClientDataAsync(kind, r.Payload, cancellationToken) is { Status: not 200 } rejected)
        {
            return FutResponse.Json(rejected);
        }
        return FutResponse.Json(await Context.ReadAsync(current, cancellationToken));
    }

    [GeneratedRegex(@"^/ut/game/fifa21/user/action/onboarding_country_(\d+)_selected$")]
    private static partial Regex CountryPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/onboarding/badge/(\d+)$")]
    private static partial Regex BadgePattern();

    [GeneratedRegex(@"^/ut/game/fifa21/loan/player/(\d+)$")]
    private static partial Regex LoanPattern();
}
