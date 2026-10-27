using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Services;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>Offline match hand-offs (club, Draft and Squad Battles fixtures).</summary>
public sealed partial class MatchRoutes : FutRouteGroup
{
    private const int ReadyBodyPreviewBytes = 512;

    public MatchRoutes(FutServices fut) : base(fut)
    {
    }

    /// <summary>A Draft match settles the Draft run; every other match settles through the club.</summary>
    public async ValueTask<FutResponse?> EndAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/match/end", "POST", "PUT"))
        {
            return null;
        }
        if (r.Payload is JsonObject body) await Fut.Matches.LogScoringEvidenceAsync(body, cancellationToken);
        // A match end names no Draft slot, so the last named one applies, read with the Draft flag.
        var (slot, lastMatchWasDraft) = await Fut.Draft.MatchEndStateAsync(cancellationToken);
        var response = (lastMatchWasDraft ? await Fut.Draft.EndMatchAsync(slot, r.Payload, cancellationToken) : null)
                       ?? await Fut.Matches.EndMatchAsync(r.Payload, cancellationToken);
        // The match-end parser (0x2b1e40) reads dynamicObjectivesUpdates: the match's objective progress shows in FUT at once.
        return FutResponse.NoStore(Fut.Objectives.WithProgressUpdates(response));
    }

    public FutResponse? Reset(FutRequest r) =>
        Is(r, $"{Game}/match/reset") ? FutResponse.Template("match/reset") : null;

    /// <summary>KeepAlive held the match session open on retail.</summary>
    public FutResponse? KeepAlive(FutRequest r) =>
        Is(r, $"{Game}/match/keepalive") ? FutResponse.NoStore(new JsonObject()) : null;

    /// <summary>FIFA's pre-match readiness handoff, composed from the CreateMatch matchId; both spellings are accepted and logged.</summary>
    public FutResponse? Ready(FutRequest r)
    {
        var match = MatchReadyPattern().Match(r.Lower);
        if (!match.Success && r.Lower != $"{Game}/match/ready")
        {
            return null;
        }
        Log.LogWarning("FUT MATCH READY method={Method} path={Path} matchId={MatchId} knownMatchId={Known} body={Body}",
            r.Method, r.RawTarget, match.Success ? match.Groups[1].Value : null, Fut.Matches.CurrentMatchId, r.BodyPreview(ReadyBodyPreviewBytes));
        return FutResponse.NoStore(new JsonObject());
    }

    /// <summary>Match creation must return the complete active squad or both kits appear unowned.</summary>
    public async ValueTask<FutResponse?> CreateAsync(FutRequest r, CancellationToken cancellationToken) =>
        Is(r, $"{Game}/match", "POST", "PUT")
            ? FutResponse.Action(await Fut.Matches.CreateAsync(r.Method, r.Payload,
                await Fut.Draft.SlotForAsync(r, cancellationToken), cancellationToken))
            : null;

    public async ValueTask<FutResponse?> OfflineSquadAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (OfflineSquadPattern().Match(r.Lower) is not { Success: true } match || r.Method != "GET")
        {
            return null;
        }
        await Fut.Draft.SlotForAsync(r, cancellationToken);
        return FutResponse.NoStore(await Fut.Matches.OfflineSquadAsync(ParseLong(match), cancellationToken));
    }

    [GeneratedRegex(@"^/ut/game/fifa21/match/([^/]+)/ready$")]
    private static partial Regex MatchReadyPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/offlinesquad/(\d+)$")]
    private static partial Regex OfflineSquadPattern();
}
