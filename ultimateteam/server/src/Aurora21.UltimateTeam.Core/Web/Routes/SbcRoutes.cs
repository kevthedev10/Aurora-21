using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Services;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>Squad Building Challenges (/sbs/...).</summary>
public sealed partial class SbcRoutes : FutRouteGroup
{
    public SbcRoutes(FutServices fut) : base(fut)
    {
    }

    public async ValueTask<FutResponse?> HandleAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!r.Lower.Contains("/sbs/", StringComparison.Ordinal) && !r.Lower.EndsWith("/sbs", StringComparison.Ordinal))
        {
            return null;
        }
        if (Is(r, $"{Game}/sbs/sets", "GET"))
        {
            return FutResponse.NoStore(await Fut.Sbc.SetsResponseAsync(cancellationToken));
        }
        if (Is(r, $"{Game}/sbs/sets/tag"))
        {
            return FutResponse.NoStore(await Fut.Sbc.TagSetsAsync(r.Payload, r.Method is "PUT" or "POST", cancellationToken));
        }
        if (SbcChallengesPattern().Match(r.Lower) is { Success: true } challenges && r.Method == "GET")
        {
            return FutResponse.NoStore(await Fut.Sbc.ChallengesResponseAsync(ParseId(challenges), cancellationToken));
        }
        if (SbcSquadPattern().Match(r.Lower) is { Success: true } squad)
        {
            var challengeId = ParseId(squad);
            if (r.Method != "PUT")
            {
                return FutResponse.NoStore(await Fut.Sbc.SquadAsync(challengeId, cancellationToken));
            }
            // FIFA follows the squad save with an empty PUT /sbs/challenge/{id} to submit.
            try
            {
                await Fut.Sbc.SaveSquadAsync(challengeId, r.Payload, cancellationToken);
                return FutResponse.Json(new JsonObject());
            }
            catch (ClubRuleException error)
            {
                // The refusal's reason was only in the reply; log it with the squad's shape so a failed save can be traced.
                var slots = r.PayloadObject?["players"] as JsonArray;
                Log.LogWarning("SBC SQUAD SAVE REFUSED challengeId={Challenge} reason={Reason} slots={Slots} filled={Filled}",
                    challengeId, error.Message, slots?.Count ?? -1,
                    slots?.OfType<JsonObject>().Count(slot => JsonValues.Int((slot["itemData"] as JsonObject ?? slot)["id"]) != 0) ?? 0);
                // FIFA saves the squad when the player backs out of a challenge, and reads any error status as a server
                // failure: a 400 here sent the player back through login (live 10-02). The refused squad is forgotten
                // instead, so the submit answers with the retail INVALID message and the player stays in FUT.
                await Fut.Sbc.ClearSquadAsync(challengeId, cancellationToken);
                return FutResponse.Json(new JsonObject());
            }
        }
        if (SbcChallengePattern().Match(r.Lower) is { Success: true } challenge)
        {
            var challengeId = ParseId(challenge);
            // PUT submits; POST (or GET) enters the challenge and returns its saved squad (blank when none).
            if (r.Method != "PUT")
            {
                return FutResponse.NoStore(await Fut.Sbc.SquadAsync(challengeId, cancellationToken));
            }
            var submitted = await Fut.Sbc.SubmitAsync(challengeId, cancellationToken);
            // The submit parser (0x2fb140) reads dynamicObjectivesUpdates; a refused submit rolled its progress back.
            if (submitted.Status != 200)
            {
                Fut.Objectives.DiscardProgressUpdates();
                return FutResponse.NoStore(submitted);
            }
            return FutResponse.NoStore(Fut.Objectives.WithProgressUpdates(submitted.Body), submitted.Status);
        }
        return FutResponse.Json(new JsonObject());
    }

    [GeneratedRegex(@"^/ut/game/fifa21/sbs/setid/(\d+)/challenges$")]
    private static partial Regex SbcChallengesPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/sbs/challenge/(\d+)/squad$")]
    private static partial Regex SbcSquadPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/sbs/challenge/(\d+)$")]
    private static partial Regex SbcChallengePattern();
}
