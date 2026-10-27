using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Auth;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>Session start-up: local auth, POW/EASFC, UT auth, shards, account and feature settings.</summary>
public sealed class BootstrapRoutes : FutRouteGroup
{
    public BootstrapRoutes(FutServices fut) : base(fut)
    {
    }

    /// <param name="noteMathGroup">Whether a bootstrap request records the caller's maths group; false when no player is verified.</param>
    public FutResponse? LocalAuth(FutRequest r, bool noteMathGroup)
    {
        if (!LocalAuthService.Paths.Contains(r.Lower))
        {
            return null;
        }
        var lowerHeaders = r.Headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => header.Value);
        if (r.Lower == "/ultimateteam/auth/bootstrap" && noteMathGroup)
        {
            // The launcher's health check and the maths bridge report this player's Windows maths (Python server 43d3bac, c3992c5).
            Context.MathGroups.Note(Context.Player.Id, lowerHeaders.GetValueOrDefault("user-agent"), lowerHeaders.GetValueOrDefault("x-aurora21-math"), Context.NowSeconds);
        }
        var auth = Fut.Auth.Handle(r.Method, r.Lower, r.RawQuery, lowerHeaders, r.Body);
        if (r.Lower is "/connect/token" or "/connect/tokeninfo")
        {
            // Which code and token FIFA exchanges, redacted (ultimateteam/native-hosting-investigation.md).
            Log.LogInformation("AUTH EVIDENCE http {Method} {Path} status={Status} query: {Query} form: {Form} headers: {Headers}", r.Method, r.Lower, auth.Status,
                CredentialEvidence.DescribeForm(r.RawQuery, Context), CredentialEvidence.DescribeForm(System.Text.Encoding.UTF8.GetString(r.Body), Context),
                CredentialEvidence.DescribeHeaders(r.Headers, Context));
        }
        return FutResponse.Bytes(auth.Status, auth.Body, ("Cache-Control", "no-store"), ("Pragma", "no-cache"));
    }

    public async ValueTask<FutResponse?> PowAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (r.Lower == "/pow/auth")
        {
            var sid = await Context.ReadAsync(Services.Club.ClubPresentationService.PowSidAsync, cancellationToken);
            return FutResponse.Json(Template("bootstrap/pow-auth", new TemplateArgs { ["sid"] = sid, ["now"] = Context.NowSeconds }), 200, ("X-POW-SID", sid));
        }
        if (!r.Lower.StartsWith("/pow/", StringComparison.Ordinal))
        {
            return null;
        }
        if (r.Lower == "/pow/bank/user/account")
        {
            return FutResponse.Template("bootstrap/pow-bank-account");
        }
        if (r.Lower == "/pow/store/game/fifa21/catalog/list")
        {
            return FutResponse.Template("bootstrap/pow-catalogs");
        }
        if (r.Lower.Contains("/catalog/0/item/list", StringComparison.Ordinal))
        {
            return FutResponse.Template("bootstrap/pow-catalog-items");
        }
        return FutResponse.Json(new JsonObject());
    }

    public async ValueTask<FutResponse?> UtAuthAsync(FutRequest r, CancellationToken cancellationToken)
    {
        // FIFA's logout when the player leaves FUT (CardsDLL DELETE_AUTH; its reply is not read). The endpoint ends the UT
        // session; answered here so it is no longer filed as an unknown endpoint (222 times since 09-28).
        if (r.Lower == "/ut/delete/auth")
        {
            return FutResponse.Json(new JsonObject());
        }
        if (r.Lower != "/ut/auth")
        {
            return null;
        }
        if (r.Method == "DELETE")
        {
            return FutResponse.Json(new JsonObject());
        }
        // Which credential UT auth carries, redacted (ultimateteam/native-hosting-investigation.md).
        Log.LogInformation("AUTH EVIDENCE http {Method} /ut/auth body: {Body} headers: {Headers}", r.Method,
            CredentialEvidence.DescribeJson(r.Body, Context), CredentialEvidence.DescribeHeaders(r.Headers, Context));
        var sid = Context.UtAuthSid ?? await Context.ReadAsync(Services.Club.ClubPresentationService.UtSidAsync, cancellationToken);
        return FutResponse.Json(Template("bootstrap/ut-auth", new TemplateArgs { ["sid"] = sid }), 200, ("X-UT-SID", sid));
    }

    public FutResponse? Shards(FutRequest r)
    {
        if (r.Lower is not ("/ut/shards" or "/ut/shards/v2"))
        {
            return null;
        }
        var port = Ports.Fut.ToString(CultureInfo.InvariantCulture);
        return FutResponse.Json(Template("bootstrap/shards", new TemplateArgs { ["port"] = port, ["address"] = $"127.0.0.1:{port}" }));
    }

    /// <summary>CardsDLL parses this as an array of {format, key, resourceType, value}; none are needed locally.</summary>
    public FutResponse? ResourceMapping(FutRequest r) =>
        Is(r, $"{Game}/resourcemapping") ? FutResponse.Json(new JsonArray()) : null;

    /// <summary>
    /// GET v2/user/accountinfo, asked by FIFA21.exe itself (parser 0x144a23720; Python server 764ef6e): the front-end FUT
    /// tile's club. A club counts only with year "2021" and platform "pc", and its SKU list names FFA21PCC.
    /// </summary>
    public async ValueTask<FutResponse?> ClubAccountInfoAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/v2/user/accountinfo", "GET"))
        {
            return null;
        }
        var config = Context.Config;
        if (await Fut.Onboarding.IsAwaitingCreationAsync(cancellationToken))
        {
            // No FUT club yet: FIFA creates it through its first-run screens (ultimateteam/onboarding.md).
            return FutResponse.NoStore(Template("bootstrap/club-account-info-no-club", new TemplateArgs
            {
                ["personaId"] = config.PersonaId,
                ["personaName"] = config.DisplayName,
            }));
        }
        var established = await Context.ReadAsync(uow => uow.EstablishedAsync(), cancellationToken);
        return FutResponse.NoStore(Template("bootstrap/club-account-info", new TemplateArgs
        {
            ["personaId"] = config.PersonaId,
            ["personaName"] = config.DisplayName,
            ["clubName"] = config.ClubName[..Math.Min(29, config.ClubName.Length)],
            ["established"] = established,
            ["badge"] = Context.Rules.Seasons.Leaderboards.Row.Badge,
        }));
    }

    /// <summary>POST user/friends {"personaList": [...]}: the platform friends list, fire-and-forget (parser 0x1803535b0 reads nothing).</summary>
    public FutResponse? Friends(FutRequest r) =>
        Is(r, $"{Game}/user/friends", "POST") ? FutResponse.NoStore(new JsonObject()) : null;

    public async ValueTask<FutResponse?> AccountInfoAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/user/accountinfo"))
        {
            return null;
        }
        var config = Context.Config;
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (await Fut.Onboarding.IsAwaitingCreationAsync(cancellationToken))
        {
            return FutResponse.Json(Template("bootstrap/account-info-no-club", new TemplateArgs
            {
                ["dateOfBirth"] = config.DateOfBirth,
                ["age"] = config.AccountAge(today),
                ["isUnderAge"] = config.IsUnderAge(today),
                ["personaId"] = config.PersonaId,
                ["personaName"] = config.DisplayName,
            }));
        }
        return FutResponse.Json(Template("bootstrap/account-info", new TemplateArgs
        {
            ["dateOfBirth"] = config.DateOfBirth,
            ["age"] = config.AccountAge(today),
            ["isUnderAge"] = config.IsUnderAge(today),
            ["personaId"] = config.PersonaId,
            ["personaName"] = config.DisplayName,
            ["clubName"] = config.ClubName,
            ["now"] = Context.NowSeconds,
            ["clubId"] = config.ClubId,
            ["clubAbbr"] = config.ClubAbbr,
        }));
    }

    /// <summary>
    /// Feature flags: every local mode is switched on. Division Rivals (Python server 56145ed, parser 0x180338f80): the
    /// weekly matches that count are the soft cap, whose warning shows from softCapWarning matches left and neither blocks
    /// matchmaking; reaching the skill cap (from which matchmaking treats everyone alike) shows its popup once. Values are
    /// strings on the wire. transferListSize is the Transfer List's capacity (same parser; it sets the size CardsDLL
    /// checks before "Transfer List full"): FIFA never asks /clientdata/pilesize, so without it the client keeps its
    /// built-in default whatever maxAuctionsAllowed says.
    /// </summary>
    public FutResponse? Settings(FutRequest r)
    {
        if (!Is(r, $"{Game}/settings"))
        {
            return null;
        }
        var rivals = Context.Rules.Seasons.Rivals;
        static string Text(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return FutResponse.Template("bootstrap/settings", new TemplateArgs
        {
            ["weeklyMatches"] = Text(rivals.WeeklyMatches),
            ["softCapWarning"] = Text(rivals.SoftCapWarning),
            ["skillCap"] = Text(rivals.TopSkillThreshold),
            ["transferListSize"] = Text(Context.Rules.Market.Listings.MaxAuctionsAllowed),
        });
    }

    public FutResponse? TrustedDevice(FutRequest r) =>
        r.Lower.StartsWith($"{Game}/phishing/trusteddevice", StringComparison.Ordinal) ? FutResponse.Template("bootstrap/trusted-device") : null;
}
