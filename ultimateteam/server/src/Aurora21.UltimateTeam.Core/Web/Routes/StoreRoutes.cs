using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Store;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>The pack store, owned packs and pack opening.</summary>
public sealed class StoreRoutes : FutRouteGroup
{
    private static readonly string[] OwnedPackTokens = { "/cardpack", "/mypacks", "/unopened", "/recovered", "/purchased" };

    public StoreRoutes(FutServices fut) : base(fut)
    {
    }

    public async ValueTask<FutResponse?> PurchaseGroupsAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (r.Lower.Contains($"{Game}/store/purchasegroup/", StringComparison.Ordinal)
            && OwnedPackTokens.Any(token => r.Lower.Contains(token, StringComparison.Ordinal)))
        {
            return FutResponse.NoStore(await Context.ReadAsync(Fut.Store.MyPacksAsync, cancellationToken));
        }
        return Is(r, $"{Game}/store/purchasegroup/all")
            ? FutResponse.NoStore(await Context.ReadAsync(Fut.Store.PurchaseGroupsAsync, cancellationToken))
            : null;
    }

    public async ValueTask<FutResponse?> PurchasedItemsAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (!Is(r, $"{Game}/purchased/items"))
        {
            return null;
        }
        if (r.IsMethod("POST", "PUT"))
        {
            var outcome = await Fut.Store.OpenPackBodyAsync(r.Payload, r.ContentType, r.Body, r.Query, cancellationToken);
            if (outcome.Refused is { } refused)
            {
                Fut.Objectives.DiscardProgressUpdates();
                return FutResponse.Json(refused);
            }
            // The pack-open parser (0x2b7820) reads dynamicObjectivesUpdates, so the packs stat moves in FUT at once.
            if (Fut.Objectives.TakeProgressUpdates() is not { } updates)
            {
                return FutResponse.Template(StoreService.PurchaseCompleteTemplate, outcome.Complete);
            }
            var complete = TemplateLibrary.Core.Object(StoreService.PurchaseCompleteTemplate, outcome.Complete!);
            complete["dynamicObjectivesUpdates"] = updates;
            return FutResponse.Json(complete);
        }
        // FIFA refreshes this after the pack-open POST and uses its order for New Items.
        return FutResponse.Json(await Context.ReadAsync(Fut.Store.UnassignedItemsAsync, cancellationToken));
    }
}
