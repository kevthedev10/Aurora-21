using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>The FUT Store: purchase groups, My Packs, pack purchases and store localization.</summary>
public sealed class StoreService
{
    private static readonly JsonObject TokensSection = EmbeddedResources.ReadJson(typeof(StoreService).Assembly,
        "Templates/store/tokens-section.json").AsObject();
    private static readonly string[] PurchasePackIdKeys = { "packId", "id", "packType", "serverId", "assetId" };
    private static readonly string[] NestedPurchaseKeys = { "purchase", "pack", "item" };

    /// <summary>The status CardsDLL maps to PACK_EXISTS_IN_PURCHASED_PILE (items still waiting in Unassigned).</summary>
    private const int UnassignedItemsStatus = 471;

    private readonly FutServices _fut;
    private readonly ILogger _log;

    public StoreService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Store");
    }

    private GameContext Game => _fut.Game;

    public async ValueTask<JsonObject> PurchaseGroupsAsync(IUnitOfWork uow)
    {
        var credits = await uow.CreditsAsync();
        var points = await uow.FifaPointsAsync();
        var tiles = new JsonArray();
        var purchases = (await uow.QueryAsync("SELECT pack_id,purchases FROM ultimateteam.club_pack_purchases WHERE club_id=@club",
            row => (PackId: row.GetInt64(0), Count: row.GetInt64(1)))).ToDictionary(row => row.PackId, row => row.Count);
        var communityPurchases = (await uow.QueryAsync("SELECT pack_id,purchases FROM ultimateteam.community_pack_purchases",
            row => (PackId: row.GetInt64(0), Count: row.GetInt64(1)))).ToDictionary(row => row.PackId, row => row.Count);
        // An admin-only pack is never listed: only an explicit grant hands one out; nor is one no allowed currency can buy.
        foreach (var (packId, config) in EnabledStorePacks().Where(entry => !entry.Config.Bool("admin_only")
            && !RequestsChampionsRed(entry.Config) && Currencies(entry.Config).Count > 0))
        {
            var counterId = config.IntKeep("base_pack_id", packId);
            var limit = config.Int("purchase_limit");
            if (limit > 0 && purchases.GetValueOrDefault(counterId) >= limit) continue;
            var communityLimit = config.Int("community_limit");
            if (communityLimit > 0 && communityPurchases.GetValueOrDefault(counterId) >= communityLimit) continue;
            tiles.Add(StorePackTile(packId, config, credits, points, purchases.GetValueOrDefault(counterId), communityPurchases.GetValueOrDefault(counterId)));
        }
        // Native Draft looks up one of these fixed store ids after /draft/state reports no run.
        var entry = Game.Rules.Draft.Entry;
        foreach (var entryId in entry.StoreEntryIds)
        {
            tiles.Add(TemplateLibrary.Core.Object("store/draft-entry-tile", new TemplateArgs
            {
                ["entryId"] = entryId,
                ["name"] = entry.StoreName,
                ["description"] = entry.StoreDescription,
                ["coins"] = entry.Coins,
                ["points"] = entry.Points,
                ["purchasable"] = credits >= entry.Coins,
            }));
        }
        // Some screens discover My Packs from this catch-all response.
        foreach (var packId in (await uow.UnopenedPackIdsAsync()).Order())
        {
            tiles.Add(OwnedPackTile(packId));
        }
        return new JsonObject { ["purchase"] = tiles, ["timestamp"] = Game.NowSeconds };
    }

    private IEnumerable<(long PackId, JsonObject Config)> EnabledStorePacks() =>
        StorePackConfigs().Where(entry => entry.Config.Bool("enabled", true));

    /// <summary>Every pack defined in the store settings, enabled or not, by pack id.</summary>
    private IEnumerable<(long PackId, JsonObject Config)> StorePackConfigs() =>
        (Game.Packs.Load().Obj("packs") ?? new JsonObject())
            .Concat(Game.Packs.TokenOffers())
            .Select(entry => (PackId: long.Parse(entry.Key, CultureInfo.InvariantCulture), Config: entry.Value as JsonObject))
            .Where(entry => entry.Config is not null)
            .OrderBy(entry => entry.PackId)
            .Select(entry => (entry.PackId, entry.Config!));

    private static bool RequestsChampionsRed(JsonObject config) =>
        config.Arr("allowed_rareflags")?.Any(flag => JsonValues.TryInt(flag, out var rarity) && rarity == 18) == true
        || config.Arr("player_picks")?.OfType<JsonObject>().Any(pick =>
            pick.Arr("rareflags")?.Any(flag => JsonValues.TryInt(flag, out var rarity) && rarity == 18) == true) == true;

    private JsonObject StorePackTile(long packId, JsonObject config, long credits, long points, long purchases, long communityPurchases)
    {
        var packRules = Game.Packs.Rules;
        var definitionId = config.IntKeep("base_pack_id", packId);
        var name = config.Text("name", $"Custom Pack {definitionId}");
        var price = Math.Max(0, config.IntKeep("price", 0));
        var pointsPrice = config.Int("base_pack_id") > 0 ? config.Int("points_price") : 0;
        var currencies = Currencies(config);
        var quantity = Math.Clamp(config.IntKeep("size", 12), 1, packRules.MaxPackSize);
        var communityLimit = config.Int("community_limit");
        var userLimit = config.Int("purchase_limit");
        var communityRemaining = communityLimit > 0 ? Math.Max(0, communityLimit - communityPurchases) : -1;
        var mix = PackTierMix.For(config, quantity);
        var requested = config.Str("store_group", mix.Dominant).ToLowerInvariant();
        var group = packRules.StoreGroupAliases.GetValueOrDefault(requested, requested);
        if (!packRules.StoreGroups.ContainsKey(group))
        {
            group = mix.Dominant;
        }
        var tokens = config.Int("base_pack_id") > 0;
        return TemplateLibrary.Core.Object("store/pack-tile", new TemplateArgs
        {
            ["packId"] = packId,
            ["artId"] = packRules.StoreArtId(definitionId, config),
            ["name"] = name,
            ["bio"] = config.Str("bio"),
            ["price"] = price,
            ["pointsPrice"] = pointsPrice,
            ["purchaseLimit"] = config.Int("purchase_limit"),
            ["purchaseCount"] = purchases,
            // CardsDLL uses these classifications for the promo/quantity-limited store presentation.
            ["dealType"] = userLimit > 0 || communityLimit > 0 ? "promo" : "",
            ["saleType"] = userLimit > 0 || communityLimit > 0 ? "QUANTITY" : "NONE",
            ["remaining"] = userLimit > 0 ? Math.Max(0, userLimit - purchases) : communityLimit > 0 ? communityRemaining : 0,
            // Finite global stock activates FIFA's Community Limit display; per-club packs must not send it.
            ["availableQuantity"] = communityRemaining,
            // Only the currencies the pack is sold for (Python server: allowed_currencies).
            ["currencies"] = new JsonArray(currencies.Select(currency => (JsonNode?)TemplateLibrary.Core.Object("store/pack-currency", new TemplateArgs
            {
                ["name"] = currency,
                ["funds"] = currency == "points" ? pointsPrice : price,
            })).ToArray()),
            ["categoryId"] = tokens ? TokensSection.Int("categoryId") : packRules.StoreGroups[group],
            ["group"] = tokens ? TokensSection.Str("group") : group,
            ["packType"] = mix.Dominant.ToUpperInvariant(),
            ["quantity"] = quantity,
            ["gold"] = mix.Gold,
            ["silver"] = mix.Silver,
            ["bronze"] = mix.Bronze,
            ["rare"] = Math.Clamp(config.IntKeep("rare_slots", 0), 0, quantity),
            ["purchasable"] = (currencies.Contains("coins") && credits >= price) || (currencies.Contains("points") && points >= pointsPrice),
        });
    }

    /// <summary>
    /// The currencies a store pack is sold for: its allowed currencies, without FIFA Points while rules/club.json
    /// fifaPointsPurchases is off.
    /// </summary>
    private IReadOnlyList<string> Currencies(JsonObject config) =>
        (config.Int("base_pack_id") > 0 ? (config.Int("points_price") > 0 ? new[] { "points" } : Array.Empty<string>())
            : (config.Int("price") > 0 ? new[] { "coins" } : Array.Empty<string>()))
            .Where(currency => currency != "points" || Game.Rules.Club.FifaPointsPurchases).ToList();

    public async ValueTask<JsonObject> MyPacksAsync(IUnitOfWork uow)
    {
        var packIds = (await uow.UnopenedPackIdsAsync()).Order().ToList();
        return TemplateLibrary.Core.Object("store/my-packs", new TemplateArgs
        {
            ["packs"] = new JsonArray(packIds.Select(packId => (JsonNode?)OwnedPackTile(packId)).ToArray()),
            ["total"] = packIds.Count,
            ["timestamp"] = Game.NowSeconds,
        });
    }

    public JsonObject OwnedPackTile(long packId)
    {
        var config = Game.Packs.GetPackConfig(packId);
        var size = Math.Clamp(config.Int("size", 12), 1, Game.Packs.Rules.MaxPackSize);
        var mix = PackTierMix.For(config, size);
        return TemplateLibrary.Core.Object("store/owned-pack-tile", new TemplateArgs
        {
            ["packId"] = packId,
            ["artId"] = Game.Packs.Rules.StoreArtId(packId, config),
            ["quantity"] = 1,
            ["name"] = JsonValues.Text(config.GetOr("name", Game.Packs.Rules.PackName(packId))),
            ["size"] = size,
            ["rare"] = Math.Clamp(config.Int("rare_slots"), 0, size),
            ["gold"] = mix.Gold,
            ["silver"] = mix.Silver,
            ["bronze"] = mix.Bronze,
        });
    }

    /// <summary>Opens a purchased or owned reward pack (POST/PUT /purchased/items).</summary>
    /// <summary>A pack purchase: a refusal already shaped as a result, or the completed pack's template values.</summary>
    public readonly record struct PackOpenBody(FutResult? Refused, TemplateArgs? Complete);

    public const string PurchaseCompleteTemplate = "store/purchase-complete";

    public async Task<FutResult> OpenPackAsync(JsonNode? payload, string contentType, byte[] body, QueryValues query, CancellationToken cancellationToken)
    {
        var outcome = await OpenPackBodyAsync(payload, contentType, body, query, cancellationToken);
        return outcome.Refused ?? FutResult.Ok(TemplateLibrary.Core.Object(PurchaseCompleteTemplate, outcome.Complete!));
    }

    /// <summary>The same purchase for the route, which writes a completed pack straight from its template.</summary>
    public async Task<PackOpenBody> OpenPackBodyAsync(JsonNode? payload, string contentType, byte[] body, QueryValues query,
        CancellationToken cancellationToken)
    {
        var request = PurchaseRequest(payload, contentType, body, query);
        long packId;
        PackSettings.PackView packView;
        try
        {
            packId = PurchasePackId(request);
            packView = Game.Packs.Capture(packId);
            if (packView.Config.Count == 0)
            {
                throw new ClubRuleException($"unknown pack id {packId}");
            }
        }
        catch (ClubRuleException error)
        {
            _log.LogWarning("PACK_OPEN_REJECTED reason={Reason} request={Request}", error.Message, FutJson.Serialize(request));
            return new PackOpenBody(FutResult.Coded(HttpStatusCode.BadRequest, error.Message), null);
        }

        try
        {
            return await Game.RunAsync("pack-open", uow => BuyAndOpenPackAsync(uow, packId, packView, request), cancellationToken);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        {
            // The transaction has rolled back: no payment, consumed pack, item or objective change remains.
            _log.LogError(error, "PACK_OPEN_FAILED packId={Pack} rolledBack=true", packId);
            return new PackOpenBody(FutResult.Coded(HttpStatusCode.InternalServerError, $"local pack generation failed: {error.Message}"), null);
        }
    }

    /// <summary>Payment, pack consumption, generated items, objective progress and the transaction id commit together.</summary>
    private async ValueTask<PackOpenBody> BuyAndOpenPackAsync(IUnitOfWork uow, long packId, PackSettings.PackView view, JsonObject request)
    {
        var config = view.Config;
        var offerId = packId;
        var tokenOffer = config.Int("base_pack_id") > 0;
        if (tokenOffer) packId = config.Int("base_pack_id");
        await uow.LockClubAsync();
        // FUT refuses a pack while Unassigned holds anything: the reveal would list the whole pile. CardsDLL's create-pack
        // handler (0x1800515c0) turns 471 into PACK_EXISTS_IN_PURCHASED_PILE, which sends the player to deal with those
        // items first. Nothing is charged (Python server 328a5b1). My Stadium items waiting in the pile count too.
        var waiting = await uow.CountPileAsync("unassigned") + await _fut.Vanity.UnassignedCountAsync(uow);
        if (waiting > 0)
        {
            _log.LogWarning("PACK_OPEN_BLOCKED packId={Pack} reason=unassigned-items count={Count}", packId, waiting);
            return new PackOpenBody(FutResult.Coded((HttpStatusCode)UnassignedItemsStatus, "unassigned items must be handled before opening a pack"), null);
        }
        var ownedReward = !tokenOffer && (await uow.UnopenedPackIdsAsync()).Contains(packId);
        if (RequestsChampionsRed(config) && !(ownedReward && packId is >= 550 and <= 553))
        {
            _log.LogWarning("PACK_OPEN_REJECTED packId={Pack} reason=champions-red-reward-only", packId);
            return new PackOpenBody(FutResult.Coded(HttpStatusCode.BadRequest, "FUT Champions red cards are reward-only"), null);
        }
        // A pack the club does not own must be one the store sells; reward packs only open from My Packs.
        if (!ownedReward && !view.ForSale)
        {
            _log.LogWarning("PACK_OPEN_REJECTED packId={Pack} reason=not-for-sale", packId);
            return new PackOpenBody(FutResult.Coded(HttpStatusCode.BadRequest, $"pack {packId} is not for sale"), null);
        }
        if (!ownedReward && config.Int("purchase_limit") is var purchaseLimit && purchaseLimit > 0
            && await uow.ScalarAsync<long>("SELECT coalesce((SELECT purchases FROM ultimateteam.club_pack_purchases WHERE club_id=@club AND pack_id=@pack),0)",
                ("pack", packId)) >= purchaseLimit)
            return new PackOpenBody(FutResult.Coded(HttpStatusCode.BadRequest, "This club has reached the pack purchase limit."), null);
        if (!ownedReward && config.Int("community_limit") is var communityLimit && communityLimit > 0)
        {
            await uow.ExecuteAsync("INSERT INTO ultimateteam.community_pack_purchases(pack_id,purchases) VALUES(@pack,0) ON CONFLICT DO NOTHING", ("pack", packId));
            // Serialize contenders for the last community copy across clubs and server processes.
            var communityPurchases = await uow.ScalarAsync<long>("SELECT purchases FROM ultimateteam.community_pack_purchases WHERE pack_id=@pack FOR UPDATE", ("pack", packId));
            if (communityPurchases >= communityLimit)
                return new PackOpenBody(FutResult.Coded(HttpStatusCode.BadRequest, "This pack's community limit has been reached."), null);
        }
        var price = ownedReward ? 0 : Math.Max(0, config.IntKeep("price", 0));
        var pointsPrice = ownedReward || !tokenOffer ? 0 : config.Int("points_price");
        var currency = FirstNonEmptyText(request, "currency", "currencyId", "COINS").ToUpperInvariant();
        var pointsRequested = currency is "POINTS" or "FIFAPOINTS" or "FIFA_POINTS" or "1" || IsTruthy(request.GetOr("usePoints", 0));
        if (pointsRequested)
        {
            currency = "POINTS";
        }
        // An unowned pack is always paid for, in points when asked and otherwise in coins. The request's
        // useCredits flag cannot decline payment; FIFA's My Packs request sends useCredits=0 for an owned pack.
        var usePoints = !ownedReward && pointsRequested;
        var useCredits = !ownedReward && !usePoints;
        if (!ownedReward && currency is not ("COINS" or "CREDITS" or "POINTS"))
        {
            _log.LogWarning("PACK_OPEN_REJECTED packId={Pack} reason=unsupported-currency currency={Currency}", packId, currency);
            return new PackOpenBody(FutResult.Coded(HttpStatusCode.BadRequest, $"unsupported local pack currency {currency}"), null);
        }
        if (!ownedReward && !Currencies(config).Contains(usePoints ? "points" : "coins"))
        {
            _log.LogWarning("PACK_OPEN_REJECTED packId={Pack} reason=currency-not-allowed currency={Currency}", packId, usePoints ? "points" : "coins");
            return new PackOpenBody(FutResult.Coded(HttpStatusCode.BadRequest, $"{(usePoints ? "points" : "coins")} are not accepted for this pack"), null);
        }
        if (usePoints)
        {
            if (await uow.FifaPointsAsync() < pointsPrice)
            {
                _log.LogWarning("PACK_OPEN_REJECTED packId={Pack} reason=insufficient-points price={Price} points={Points}", packId, pointsPrice,
                    await uow.FifaPointsAsync());
                var insufficient = TemplateLibrary.Core.Object("store/insufficient-funds", new TemplateArgs { ["packId"] = offerId });
                return new PackOpenBody(FutResult.Ok(insufficient.Update(await ClubPresentationService.CreditsPayloadAsync(uow))), null);
            }
            await uow.AddFifaPointsAsync(-pointsPrice);
        }
        else if (useCredits)
        {
            // The club row is already locked above, so the conditional spend is the same check-then-debit
            // in one statement; only a refusal needs the balance, for the log.
            if (!await uow.TrySpendRelationalCreditsAsync(price))
            {
                _log.LogWarning("PACK_OPEN_REJECTED packId={Pack} reason=insufficient-coins price={Price} credits={Credits}", packId, price,
                    await uow.CreditsAsync());
                var insufficient = TemplateLibrary.Core.Object("store/insufficient-funds", new TemplateArgs { ["packId"] = offerId });
                return new PackOpenBody(FutResult.Ok(insufficient.Update(await ClubPresentationService.CreditsPayloadAsync(uow))), null);
            }
        }

        // Keep the priced configuration for the roll even if an operator publishes a new catalog mid-request. An owned copy
        // rolled when it was granted (a Match Pack) opens exactly as stored.
        var owned = ownedReward ? await uow.TakeOwnedPackContentsAsync(packId) ?? throw new InvalidOperationException("owned reward pack is no longer available") : default;
        var pack = owned.Contents is { } rolled
            ? await _fut.LegacyMatchPacks.OpenRolledAsync(uow, packId, rolled, owned.Untradeable)
            : await _fut.Packs.CreatePackAsync(uow, packId, config);
        var storeUntradeable = config.Bool("store_untradeable", tokenOffer);
        if (ownedReward ? !owned.Untradeable : !storeUntradeable)
        {
            // Club items from a tradeable pack trade like its players, as in retail. Their card templates start untradeable for
            // the grants outside packs (onboarding, objectives, the club's defaults).
            var cosmetics = (pack.Arr("itemData") ?? new JsonArray()).OfType<JsonObject>()
                .Where(item => ItemFactory.IsClubItem(item) && item.Bool("untradeable")).ToList();
            foreach (var item in cosmetics) ItemFactory.MakeTradeable(item);
            if (cosmetics.Count > 0) await uow.SaveItemsAsync(cosmetics);
        }
        if (ownedReward)
        {
            // A reward pack's items keep its grant source, and a pack granted untradeable (rules/tradeability.json) opens
            // untradeable items (Python server 21bf125); their quick-sell values follow (rules/cards.json quickSell). A Match
            // Pack opens match_pack items: untradeable, quick-selling for nothing.
            var opened = (pack.Arr("itemData") ?? new JsonArray()).OfType<JsonObject>().ToList();
            foreach (var item in opened)
            {
                _fut.Items.MarkReward(item, owned.Source, owned.Untradeable);
            }
            await uow.SaveItemsAsync(opened);
        }
        else if (storeUntradeable)
        {
            // Each listing sets purchase tradeability; missing choices default to tradeable for coins and untradeable for tokens.
            var opened = (pack.Arr("itemData") ?? new JsonArray()).OfType<JsonObject>().ToList();
            foreach (var item in opened)
            {
                _fut.Items.MarkReward(item, RewardSources.Store, untradeable: true);
            }
            await uow.SaveItemsAsync(opened);
        }
        if (!ownedReward)
        {
            await uow.ExecuteAsync("""
                INSERT INTO ultimateteam.club_pack_purchases(club_id,pack_id,purchases) VALUES(@club,@pack,1)
                ON CONFLICT(club_id,pack_id) DO UPDATE SET purchases=ultimateteam.club_pack_purchases.purchases+1
                """, ("pack", packId));
            await uow.ExecuteAsync("""
                INSERT INTO ultimateteam.community_pack_purchases(pack_id,purchases) VALUES(@pack,1)
                ON CONFLICT(pack_id) DO UPDATE SET purchases=ultimateteam.community_pack_purchases.purchases+1
                """, ("pack", packId));
        }
        // An SBC reward pack counts for neither the packs stat nor Season XP: the Upgrades repeat without limit and each
        // repeat pays a pack, so counting it would farm XP the way counting the repeats did.
        if (!(ownedReward && owned.Source == RewardSources.Sbc))
        {
            await _fut.Objectives.RecordPackOpenedAsync(uow);
        }

        var converted = (pack.Arr("itemData") ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(item => _fut.Native.PackItem(item))
            .ToList();
        // Every pack type's reveal lists its duplicates exactly as GET /purchased/items will: the generators' own lists
        // missed some (mixed store packs and pick-pack bonus players sent none), so FIFA called every card new, Store All
        // sent a duplicate to the club and the server kept it in Unassigned, blocking the next pack.
        var duplicates = await ClubInventoryService.DuplicateListAsync(uow, converted);
        var duplicateIds = duplicates.OfType<JsonObject>().Select(entry => entry.Int("itemId")).ToHashSet();
        foreach (var item in converted.Where(item => duplicateIds.Contains(item.Int("id"))))
        {
            // The markers a generator's own duplicate check stores; the reveal never carries the pile.
            item["duplicate"] = true;
            item["isDuplicate"] = true;
        }
        var items = new JsonArray(converted.Select(item => (JsonNode?)item).ToArray());
        var args = new TemplateArgs
        {
            AdoptNodes = true,
            ["packId"] = tokenOffer ? offerId : JsonValues.Int(pack.GetOr("purchasedPackId", pack.GetOr("packId", 0))),
            ["transactionId"] = await NextTransactionIdAsync(uow),
            ["numberItems"] = items.Count,
            ["duplicates"] = duplicates,
            ["items"] = items,
        };
        // The balances the template ends with, read after the purchase exactly as the credits payload was.
        var summary = await uow.FinancialSummaryAsync();
        args["credits"] = summary.Credits;
        args["points"] = summary.FifaPoints;
        args["unopenedPacks"] = summary.UnopenedPacks;
        // The body may be written after this transaction commits, so a template mismatch must fail here
        // and roll the purchase back, as rendering inside the transaction did.
        TemplateLibrary.Core[PurchaseCompleteTemplate].Validate(args);
        return new PackOpenBody(null, args);
    }

    /// <summary>GET /purchased/items: the Unassigned pile in reveal order with duplicate markers, My Stadium items included.</summary>
    public async ValueTask<JsonObject> UnassignedItemsAsync(IUnitOfWork uow)
    {
        // Granted My Stadium items wait here too (VanityService): FIFA opens this pile on a pack reveal and an objective
        // claim, and the pile is where the player keeps or discards them.
        var unassigned = ItemOrdering.RevealOrder(await uow.ListItemsAsync("unassigned"))
            .Concat(await _fut.Vanity.UnassignedAsync(uow)).ToList();
        return TemplateLibrary.Core.Object("store/unassigned-items", new TemplateArgs
        {
            ["numberItems"] = unassigned.Count,
            ["duplicates"] = await ClubInventoryService.DuplicateListAsync(uow, unassigned),
            ["items"] = new JsonArray(unassigned.Select(item => (JsonNode?)_fut.Native.PackItem(item)).ToArray()),
        });
    }

    /// <summary>
    /// The XLIFF translation file the client uses for FUT_STORE_PACK_{id}_NAME/_DESC labels,
    /// including names for reward packs used by Objectives and FUT Champions.
    /// </summary>
    public byte[] PackDescriptionsXliff()
    {
        var units = new StringBuilder();
        AddUnit(units, "FUT_STORE_CAT_KITS_NAME", TokensSection.Str("name"));
        AddUnit(units, "FUT_STORE_CAT_KITS_DESC", TokensSection.Str("description"));
        void AddPack(long packId, string name, string description)
        {
            AddUnit(units, $"FUT_STORE_PACK_{packId}_NAME", name);
            AddUnit(units, $"FUT_STORE_PACK_{packId}_DESC", description);
            AddUnit(units, $"FUT_STORE_PACK_{packId}_NAME_MOBILE", name);
        }

        var exported = new HashSet<long>();
        // Packs outside the store lineup still need their configured names and descriptions in My Packs.
        foreach (var (packId, config) in StorePackConfigs())
        {
            AddPack(packId, config.Str("name", $"Custom Pack {config.IntKeep("base_pack_id", packId)}"), config.Str("bio"));
            exported.Add(packId);
        }
        // Every other pack and the display-only reward names (pack 500).
        foreach (var (packId, name) in Game.Packs.Rules.PackNames.Where(entry => !exported.Contains(entry.Key)))
        {
            AddPack(packId, name, name);
            exported.Add(packId);
        }
        var document = EmbeddedResources.ReadText(typeof(StoreService).Assembly, "Templates/store/pack-descriptions.xliff")
            .TrimEnd('\n')
            .Replace("{{units}}\n", units.ToString(), StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(document);
    }

    /// <summary>One XLIFF translation unit; the source text is XML-escaped (quotes are left as written).</summary>
    private static void AddUnit(StringBuilder units, string key, string text) =>
        units.Append("      <trans-unit id=\"").Append(key).Append("\" resname=\"").Append(key).Append("\"><source>")
            .Append(text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal))
            .Append("</source></trans-unit>\n");

    /// <summary>Accepts both JSON and form-encoded purchase bodies plus query fallbacks.</summary>
    public static JsonObject PurchaseRequest(JsonNode? payload, string contentType, byte[] body, QueryValues query)
    {
        var request = payload is JsonObject document ? document.Copy() : new JsonObject();
        if (request.Count == 0 && body.Length > 0 && contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (key, values) in QueryString.Parse(Encoding.UTF8.GetString(body), keepBlankValues: true).Entries)
            {
                request[key] = values.Count == 1 ? values[^1] : new JsonArray(values.Select(v => (JsonNode?)v).ToArray());
            }
        }
        foreach (var (key, values) in query.Entries)
        {
            if (!request.ContainsKey(key) && values.Count > 0)
            {
                request[key] = values[^1];
            }
        }
        return request;
    }

    public static long PurchasePackId(JsonObject request)
    {
        var sources = new[] { request }.Concat(NestedPurchaseKeys.Select(key => request.Obj(key)).OfType<JsonObject>());
        foreach (var source in sources)
        {
            foreach (var key in PurchasePackIdKeys)
            {
                var value = source.Get(key) is JsonArray list ? (list.Count > 0 ? list[^1] : null) : source.Get(key);
                if (value is not null && JsonValues.Text(value).Trim().Length > 0 && JsonValues.TryInt(value, out var id))
                {
                    return id;
                }
            }
        }
        throw new ClubRuleException("purchase request did not include a numeric packId/id/packType/serverId/assetId");
    }

    public static async ValueTask<long> NextTransactionIdAsync(IUnitOfWork uow)
    {
        var id = Math.Max(await uow.LastPurchaseTransactionIdAsync() + 1, uow.NowMilliseconds);
        await uow.SetLastPurchaseTransactionIdAsync(id);
        return id;
    }

    /// <summary>A request flag: anything but an empty, zero, false, no or off value.</summary>
    private static bool IsTruthy(JsonNode? value) =>
        JsonValues.Text(value, "None").Trim().ToLowerInvariant() is not ("" or "0" or "false" or "no" or "off");

    private static string FirstNonEmptyText(JsonObject source, string first, string second, string fallback) =>
        JsonValues.HasValue(source.Get(first)) ? JsonValues.Text(source.Get(first))
        : JsonValues.HasValue(source.Get(second)) ? JsonValues.Text(source.Get(second))
        : fallback;
}
