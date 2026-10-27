using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Market;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>The shared transfer market, transfer list and watchlist.</summary>
public sealed partial class MarketRoutes : FutRouteGroup
{
    private const long DefaultListingDurationSeconds = 3600;

    public MarketRoutes(FutServices fut) : base(fut)
    {
    }

    public async ValueTask<FutResponse?> HandleAsync(FutRequest r, CancellationToken cancellationToken)
    {
        if (r.Method == "GET" && r.Lower.Contains("marketdata/pricelimits", StringComparison.Ordinal))
        {
            return FutResponse.NoStore(Fut.Market.PriceLimits(r.Query));
        }
        if (Is(r, $"{Game}/transfermarket", "GET"))
        {
            var (template, args) = await Fut.Market.SearchBodyAsync(r.Query, cancellationToken);
            return FutResponse.Template(template, args, 200,
                ("Cache-Control", "no-cache, no-store, must-revalidate"),
                ("Pragma", "no-cache"),
                ("Expires", "0"));
        }
        if (Is(r, $"{Game}/auctionhouse/relist", "PUT", "POST"))
        {
            return FutResponse.NoStore(await Fut.Market.RelistAllAsync(cancellationToken));
        }
        if (Is(r, $"{Game}/auctionhouse", "GET"))
        {
            return await StreamedAsync(Fut.Market.TradePileBodyAsync(r.Query, cancellationToken));
        }
        if (Is(r, $"{Game}/auctionhouse", "POST", "PUT"))
        {
            return await ListAsync(r, cancellationToken);
        }
        if (Is(r, $"{Game}/watchlist") || Is(r, $"{Game}/watch"))
        {
            return await WatchlistAsync(r, unwatchOnly: false, cancellationToken);
        }
        if (Is(r, "/ut/delete/game/fifa21/watchlist"))
        {
            return await WatchlistAsync(r, unwatchOnly: true, cancellationToken);
        }
        if (OfferPattern().Match(r.Lower) is { Success: true } offer && r.IsMethod("POST", "PUT"))
        {
            return await OfferAsync(r, ParseLong(offer), cancellationToken);
        }
        if (BuyPattern().Match(r.Lower) is { Success: true } buy && r.Method == "POST")
        {
            return await BuyAsync(r, buy, cancellationToken);
        }
        if (r.Method == "GET" && r.Lower.Contains("trade/status/lite", StringComparison.Ordinal))
        {
            return await StreamedAsync(Fut.Market.StatusBodyAsync(QueryIds(r.Query, "tradeIds", "tradeids"), cancellationToken));
        }
        if (r.Lower is "/ut/delete/game/fifa21/trade/sold" or "/ut/delete/game/fifa21/tradepile/sold")
        {
            return await MarketActionAsync(() => Fut.Market.ClearSoldAsync(cancellationToken), cancellationToken);
        }
        if (ClearTradePattern().Match(r.Lower) is { Success: true } clear && r.IsMethod("GET", "POST", "DELETE"))
        {
            return await MarketActionAsync(() => Fut.Market.ClearTradeAsync(ParseLong(clear), cancellationToken), cancellationToken);
        }
        if (r.Lower is "/ut/game/fifa21/tradepile" or "/ut/delete/game/fifa21/trade" or "/ut/delete/game/fifa21/tradepile")
        {
            return await StreamedAsync(Fut.Market.TradePileBodyAsync(r.Query, cancellationToken));
        }
        return null;
    }

    private async ValueTask<FutResponse> ListAsync(FutRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var source = request.Payload as JsonObject ?? new JsonObject();
            var itemData = source.Get("itemData") switch
            {
                JsonObject one => one,
                JsonArray { Count: > 0 } list when list[0] is JsonObject first => first,
                _ => new JsonObject(),
            };
            var itemId = Integer(FirstTruthy(itemData.Get("id"), source.Get("itemId"), source.Get("id")), 0, "item id");
            var startingBid = Integer(source.GetOr("startingBid", source.Get("startPrice")), 0, "starting bid");
            var buyNow = Integer(source.GetOr("buyNowPrice", source.Get("buyNow")), 0, "Buy Now price");
            var duration = Integer(source.GetOr("duration", source.GetOr("expires", JsonValue.Create(DefaultListingDurationSeconds))),
                DefaultListingDurationSeconds, "duration");
            return FutResponse.NoStore(await Fut.Market.ListAsync(itemId, startingBid, buyNow, duration, cancellationToken));
        }
        catch (MarketException error)
        {
            return await RejectedAsync(error, cancellationToken);
        }
    }

    private async ValueTask<FutResponse> WatchlistAsync(FutRequest request, bool unwatchOnly, CancellationToken cancellationToken)
    {
        if (request.Method == "GET" && !unwatchOnly)
        {
            return await StreamedAsync(Fut.Market.WatchlistBodyAsync(request.Query, cancellationToken));
        }
        var tradeIds = CollectTradeIds(request);
        foreach (var tradeId in tradeIds)
        {
            if (!unwatchOnly && request.IsMethod("POST", "PUT"))
            {
                await Fut.Market.WatchAsync(tradeId, cancellationToken);
            }
            else
            {
                await Fut.Market.UnwatchAsync(tradeId, cancellationToken);
            }
        }
        return await StreamedAsync(Fut.Market.WatchlistBodyAsync(request.Query, cancellationToken));
    }

    private async ValueTask<FutResponse> OfferAsync(FutRequest request, long tradeId, CancellationToken cancellationToken)
    {
        try
        {
            var amount = Integer((request.Payload as JsonObject)?.Get("bid"), 0, "bid");
            await Fut.Market.OfferAsync(tradeId, amount, cancellationToken);
            return await StreamedAsync(Fut.Market.StatusBodyAsync(new[] { tradeId }, cancellationToken));
        }
        catch (MarketException error)
        {
            return await OfferRejectedAsync(error, cancellationToken);
        }
    }

    private async ValueTask<FutResponse> BuyAsync(FutRequest request, Match match, CancellationToken cancellationToken)
    {
        try
        {
            var source = request.Payload as JsonObject ?? new JsonObject();
            var pathId = match.Groups[1].Success ? ParseLong(match) : 0;
            var tradeId = Integer(FirstTruthy(source.Get("tradeId"), JsonValue.Create(pathId)), 0, "trade id");
            var offered = Integer(source.GetOr("bid", source.Get("buyNowPrice")), 0, "Buy Now offer");
            await Fut.Market.BuyNowAsync(tradeId, offered, cancellationToken);
            return await StreamedAsync(Fut.Market.StatusBodyAsync(new[] { tradeId }, cancellationToken));
        }
        catch (MarketException error)
        {
            return await OfferRejectedAsync(error, cancellationToken);
        }
    }

    private async ValueTask<FutResponse> MarketActionAsync(Func<ValueTask<JsonObject>> action, CancellationToken cancellationToken)
    {
        try
        {
            return FutResponse.NoStore(await action());
        }
        catch (MarketException error)
        {
            return await RejectedAsync(error, cancellationToken);
        }
    }

    /// <summary>A no-store response written straight from a market template.</summary>
    private static async ValueTask<FutResponse> StreamedAsync(ValueTask<(string Template, TemplateArgs Args)> body)
    {
        var (template, args) = await body;
        return FutResponse.TemplateNoStore(template, args);
    }

    private async ValueTask<FutResponse> RejectedAsync(MarketException error, CancellationToken cancellationToken) =>
        FutResponse.NoStore(await Fut.Market.RejectedAsync(error.Message, cancellationToken));

    /// <summary>
    /// A refused bid or Buy Now. FIFA treats any 200 offer reply as won, so a refusal with a status sends
    /// FIFA's error (retail body shape: code, reason, string) and FIFA stays on the search list.
    /// </summary>
    private async ValueTask<FutResponse> OfferRejectedAsync(MarketException error, CancellationToken cancellationToken)
    {
        if (error.Status == 200)
        {
            return await RejectedAsync(error, cancellationToken);
        }
        Fut.Market.LogRejected(error.Message);
        return FutResponse.NoStore(new JsonObject
        {
            ["code"] = error.Status.ToString(CultureInfo.InvariantCulture),
            ["reason"] = error.WireReason,
            ["string"] = error.WireReason,
            ["debug"] = error.Message,
        }, error.Status);
    }

    /// <summary>Trade ids from query, the root payload and each auctionInfo row, once and in request order.</summary>
    private static List<long> CollectTradeIds(FutRequest request)
    {
        var ids = QueryIds(request.Query, "tradeId", "tradeid");
        if (request.Payload is JsonObject payload)
        {
            AddInteger(ids, payload.GetOr("tradeId", payload.Get("id")));
            IEnumerable<JsonNode?> info = payload.Get("auctionInfo") switch
            {
                JsonObject one => new JsonNode?[] { one },
                JsonArray list => list,
                _ => Array.Empty<JsonNode?>(),
            };
            foreach (var row in info.OfType<JsonObject>())
            {
                AddInteger(ids, row.GetOr("tradeId", row.Get("id")));
            }
        }
        return ids.Distinct().ToList();
    }

    private static List<long> QueryIds(QueryValues query, params string[] keys) =>
        // Spell out the lambda: QueryValues.Ids has an optional limit argument, so the method
        // group otherwise binds to SelectMany's indexed overload and passes 0 as the first key's
        // limit. FIFA uses that first alias (tradeIds), which made hover refreshes return no row.
        keys.SelectMany(key => query.Ids(key)).Distinct().ToList();

    private static void AddInteger(List<long> ids, JsonNode? value)
    {
        if (JsonValues.TryInt(value, out var id) && id > 0)
        {
            ids.Add(id);
        }
    }

    private static JsonNode? FirstTruthy(params JsonNode?[] values) => values.FirstOrDefault(JsonValues.HasValue);

    /// <summary>Python's <c>int(value or fallback)</c>: empty values use the fallback; malformed values reject the action.</summary>
    private static long Integer(JsonNode? value, long fallback, string name)
    {
        if (!JsonValues.HasValue(value))
        {
            return fallback;
        }
        if (JsonValues.TryInt(value, out var parsed))
        {
            return parsed;
        }
        throw new MarketException($"invalid {name}");
    }

    [GeneratedRegex(@"^/ut/game/fifa21/trade/(\d+)/(?:offer|bid)$")]
    private static partial Regex OfferPattern();

    [GeneratedRegex(@"^/ut/game/fifa21/trade(?:/(\d+))?/buy$")]
    private static partial Regex BuyPattern();

    [GeneratedRegex(@"^/ut/delete/game/fifa21/trade(?:pile)?/(\d+)$")]
    private static partial Regex ClearTradePattern();
}
