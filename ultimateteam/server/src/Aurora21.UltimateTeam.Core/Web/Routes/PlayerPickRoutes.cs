using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Store;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>1-of-N player picks: every console/web reveal and confirmation path.</summary>
public sealed partial class PlayerPickRoutes : FutRouteGroup
{
    /// <summary>FIFA 21 names the unlockable (and sometimes the choice) in the query string of /item/nontargeted.</summary>
    private static readonly string[] NonTargetedQueryKeys =
    {
        "itemId", "playerPickItemId", "playerPickId", "tokenId", "unlockableItemId", "selectedItemId", "selectedPlayerId", "choiceId", "selectedResourceId", "choiceIndex",
    };

    public PlayerPickRoutes(FutServices fut) : base(fut)
    {
    }

    private PlayerPickService Picks => Fut.Picks;

    public async ValueTask<FutResponse?> HandleAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (Is(r, $"{Game}/item/nontargeted", "POST", "PUT"))
        {
            var action = r.Payload is JsonObject body ? body.Copy() : new JsonObject();
            foreach (var key in NonTargetedQueryKeys.Where(key => !action.ContainsKey(key)))
            {
                if (r.Query.First(key) is { } value)
                {
                    action[key] = value;
                }
            }
            var tokenId = await TokenIdAsync(r.Lower, action, cancellationToken);
            if (!await IsPickTokenAsync(tokenId, cancellationToken))
            {
                return FutResponse.NoStore(FutResult.Coded(HttpStatusCode.NotFound, "Player-pick item not found"));
            }
            return await PickActionAsync(tokenId, action, alwaysSelect: true, cancellationToken);
        }

        // The native confirmation is POST /playerpicks/item/{chosenId}/select with an empty body.
        if (PlayerPickSelectPattern().Match(r.Lower) is { Success: true } select && r.IsMethod("POST", "PUT"))
        {
            var chosenId = ParseLong(select);
            var tokenId = await TokenIdAsync(r.Lower, r.Payload, cancellationToken);
            return tokenId == 0
                ? FutResponse.NoStore(PlayerPickService.EmptyPicksResponse())
                : await RejectableAsync(tokenId, () => Picks.SelectResponseAsync(tokenId, JsonValue.Create(chosenId), cancellationToken), $" chosen={chosenId}");
        }

        if (r.Lower.Contains("playerpick", StringComparison.Ordinal))
        {
            var tokenId = await TokenIdAsync(r.Lower, r.Payload, cancellationToken);
            return tokenId == 0
                ? FutResponse.NoStore(PlayerPickService.EmptyPicksResponse())
                : await PickActionAsync(tokenId, r.Payload, alwaysSelect: r.IsMethod("POST", "PUT"), cancellationToken);
        }

        if (ItemIdPattern().Match(r.Lower) is { Success: true } item && r.IsMethod("POST", "PUT") && await IsPickTokenAsync(ParseLong(item), cancellationToken))
        {
            return await PickActionAsync(ParseLong(item), r.Payload, alwaysSelect: true, cancellationToken);
        }
        return null;
    }

    private Task<long> TokenIdAsync(string path, JsonNode? payload, CancellationToken cancellationToken) =>
        Context.ReadAsync(uow => PlayerPickService.TokenIdAsync(uow, path, payload), cancellationToken);

    private async ValueTask<bool> IsPickTokenAsync(long itemId, CancellationToken cancellationToken) =>
        itemId != 0 && ItemFactory.IsPlayerPickToken(await Context.ReadAsync(uow => uow.GetItemAsync(itemId), cancellationToken));

    /// <summary>Selects when the body names a choice (and selection is allowed), otherwise reveals the choices.</summary>
    private Task<FutResponse> PickActionAsync(long tokenId, JsonNode? payload, bool alwaysSelect, CancellationToken cancellationToken) =>
        RejectableAsync(tokenId, () =>
        {
            var (hasSelection, selection) = PlayerPickService.ReadSelection(payload);
            return alwaysSelect && hasSelection
                ? Picks.SelectResponseAsync(tokenId, selection, cancellationToken)
                : Picks.RedeemResponseAsync(tokenId, cancellationToken);
        });

    /// <summary>Runs a pick action, answering a rejected pick with 400 and its reason.</summary>
    private async Task<FutResponse> RejectableAsync(long tokenId, Func<Task<JsonObject>> action, string detail = "")
    {
        try
        {
            return FutResponse.NoStore(await action());
        }
        catch (Exception error) when (error is ClubRuleException or InvalidOperationException)
        {
            Log.LogWarning("PLAYER_PICK_REJECTED token={Token}{Detail} reason={Reason}", tokenId, detail, error.Message);
            return FutResponse.Json(FutResult.Coded(HttpStatusCode.BadRequest, error.Message));
        }
    }

    [GeneratedRegex(@"^/ut/game/fifa21/playerpicks/item/(\d+)/select$")]
    private static partial Regex PlayerPickSelectPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/item/(\d+)$")]
    private static partial Regex ItemIdPattern();
}
