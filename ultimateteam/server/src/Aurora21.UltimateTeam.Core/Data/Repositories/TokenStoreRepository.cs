using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

public static class TokenStoreRepository
{
    public sealed record Listing(long PackId, long OfferId, long Price, bool Enabled, bool? Untradeable);

    public static ValueTask<List<Listing>> TokenListingsAsync(this IUnitOfWork work) => work.QueryAsync("""
        SELECT pack_id,offer_id,price,enabled,untradeable FROM ultimateteam.token_store_listings WHERE listed ORDER BY pack_id
        """, row => new Listing(row.GetInt64(0),row.GetInt32(1),row.GetInt64(2),row.GetBoolean(3),row.IsDBNull(4) ? null : row.GetBoolean(4)));

    public static async ValueTask<JsonObject> TokenOffersAsync(this IUnitOfWork work, JsonObject packs)
    {
        var offers = new JsonObject();
        foreach (var listing in await work.TokenListingsAsync())
        {
            if (packs.Obj(listing.PackId.ToString(CultureInfo.InvariantCulture)) is not { } source) continue;
            var offer = source.Copy();
            offer["base_pack_id"] = listing.PackId;
            offer["enabled"] = listing.Enabled;
            offer["store_section"] = "tokens";
            offer["price"] = 0;
            offer["points_price"] = listing.Price;
            offer.Remove("store_untradeable");
            offer["store_untradeable"] = listing.Untradeable ?? true;
            offers[listing.OfferId.ToString(CultureInfo.InvariantCulture)] = offer;
        }
        return offers;
    }
}
