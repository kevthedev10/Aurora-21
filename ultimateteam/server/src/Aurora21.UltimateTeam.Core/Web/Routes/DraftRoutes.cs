using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Modes;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>Single-player FUT Draft.</summary>
public sealed partial class DraftRoutes : FutRouteGroup
{
    private const int DraftSquadCaptureBytes = 128 * 1024;

    public DraftRoutes(FutServices fut) : base(fut)
    {
    }

    private DraftService Draft => Fut.Draft;

    public async ValueTask<FutResponse?> HandleAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (Is(r, $"{Game}/squad/mode/draft/state", "GET"))
        {
            var slot = await Draft.SlotForAsync(r, cancellationToken);
            var state = await Context.ReadAsync(async uow =>
                await Draft.StatePayloadAsync(uow, await Draft.CurrentRunAsync(uow, slot)), cancellationToken);
            return FutResponse.NoStore(state);
        }
        if (DraftPurchasePattern().Match(r.Lower) is { Success: true } purchase && r.Method == "POST")
        {
            var slot = await Draft.SlotForAsync(r, cancellationToken);
            return FutResponse.Action(await Draft.PurchaseEntryAsync(slot, singlePlayer: purchase.Groups[1].Value != "0", r.Payload, cancellationToken));
        }
        if (Is(r, $"{Game}/squad/mode/draft/choose/difficulty", "PUT", "POST"))
        {
            return FutResponse.Action(await Draft.ChooseDifficultyAsync(await Draft.SlotForAsync(r, cancellationToken), r.Payload, cancellationToken));
        }
        if (DraftChoicesPattern().Match(r.Lower) is { Success: true } choices && r.IsMethod("GET", "PUT", "POST"))
        {
            return FutResponse.Action(await Draft.ChoicesRequestAsync(await Draft.SlotForAsync(r, cancellationToken), choices.Groups[1].Value,
                r.PayloadObject, cancellationToken));
        }
        if (r.Lower.EndsWith("/draft/choose", StringComparison.Ordinal) && r.IsMethod("PUT", "POST"))
        {
            return FutResponse.Action(await Draft.ChooseRequestAsync(await Draft.SlotForAsync(r, cancellationToken), r.PayloadObject, cancellationToken));
        }
        if (r.Lower.EndsWith("/draft/autocomplete", StringComparison.Ordinal) && r.IsMethod("POST", "PUT"))
        {
            return FutResponse.Action(await Draft.AutocompleteRequestAsync(await Draft.SlotForAsync(r, cancellationToken), cancellationToken));
        }
        if (IsStatsPath(r.Lower) && r.Method == "GET")
        {
            var slot = await Draft.SlotForAsync(r, cancellationToken);
            var stats = await Context.ReadAsync(async uow => Draft.Stats(await Draft.CurrentRunAsync(uow, slot)), cancellationToken);
            return FutResponse.NoStore(stats);
        }
        if (r.Lower.Contains("/grant/award", StringComparison.Ordinal) && r.IsMethod("GET", "POST", "PUT"))
        {
            return FutResponse.NoStore(await Draft.GrantAwardAsync(await Draft.SlotForAsync(r, cancellationToken), cancellationToken));
        }
        return null;
    }

    /// <summary>The Draft squad id: rearrangements (PUT/POST) never touch the user's real squads.</summary>
    public async ValueTask<FutResponse?> SquadAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (r.Lower != $"{Game}/squad/{Draft.SquadId}" || !r.IsMethod("GET", "PUT", "POST"))
        {
            return null;
        }
        if (Context.Config.CaptureDiagnostics && r.Method != "GET")
        {
            // Opt-in evidence for the untraced rearrangement body; a full Draft squad exceeds the generic preview.
            Log.LogInformation("DRAFT-SQUAD-SCOUT REQUEST method={Method} path={Path} bytes={Bytes} body={Body}",
                r.Method, r.RawTarget, r.Body.Length, r.BodyPreview(DraftSquadCaptureBytes));
        }
        return FutResponse.Action(await Draft.SquadRequestAsync(await Draft.SlotForAsync(r, cancellationToken),
            r.Method == "GET" ? null : r.Payload, cancellationToken));
    }

    private static bool IsStatsPath(string path) =>
        DraftStatsPattern().IsMatch(path)
        || (path.Contains("/squad/mode/", StringComparison.Ordinal) && path.EndsWith("/stats", StringComparison.Ordinal));

    [GeneratedRegex(@"/draft/mode/\d+/stats$")]
    private static partial Regex DraftStatsPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/purchase/mode/(\d+)/draft$")]
    private static partial Regex DraftPurchasePattern();

    [GeneratedRegex(@"/draft/choices/(difficulty|formation|captain|player|manager)$")]
    private static partial Regex DraftChoicesPattern();
}
