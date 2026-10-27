using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Core.Services.Club;

/// <summary>Owned items and squads: piles, duplicates, equipping, quick-sell and squad persistence.</summary>
public sealed class ClubInventoryService
{
    public const string ListedItemReason = "item is listed on the transfer market";

    /// <summary>The longest name a new squad keeps.</summary>
    private const int MaxSquadNameLength = 30;

    private static readonly string[] DuplicateUiKeys = { "IS_DUPLICATE", "isItemAlreadyInClub", "DUPLICATE_CARD_REF", "duplicateCardRef" };

    private readonly FutServices _fut;
    private readonly ILogger _log;

    public ClubInventoryService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Club");
    }

    internal GameContext Game => _fut.Game;


    public static bool IsPlayer(JsonObject item) => item.Text("itemType", "player") == "player";

    public static long ResourceOrAsset(JsonObject item) => JsonValues.Int(item.GetOr("resourceId", item.GetOr("assetId", 0)));

    /// <summary>
    /// Cards a club keeps one copy of: players, managers and club items (kits, badges, balls, stadium cards). A second copy
    /// is a duplicate and stays in Unassigned. Consumables stack instead.
    /// </summary>
    public static bool IsUniqueCard(JsonObject item) => CardKind(item) is "player" or "manager" or "kit" or "custom" or "ball" or "stadium";

    /// <summary>A unique card's identity: its kind and resource, so cards of different kinds never match.</summary>
    public static (string Kind, long Resource) CardKey(JsonObject item) => (CardKind(item), ResourceOrAsset(item));

    // A badge is stored as the "custom" customisation kind, the item type its card carries.
    private static string CardKind(JsonObject item) => item.Text("itemType", "player") is var kind && kind == "badge" ? "custom" : kind;

    /// <summary>
    /// Every owned copy (club or squad, club pile first) of the unique cards among <paramref name="items"/>. A loan copy is
    /// not one: retail keeps a bought or packed card in the club beside its loan.
    /// </summary>
    public static async ValueTask<List<((string Kind, long Resource) Key, long ItemId)>> OwnedCardsAsync(IUnitOfWork uow, IEnumerable<JsonObject> items)
    {
        var keys = items.Where(IsUniqueCard).Select(CardKey).Where(key => key.Resource != 0).Distinct().ToList();
        var players = keys.Where(key => key.Kind == "player").Select(key => key.Resource).ToArray();
        var others = keys.Where(key => key.Kind != "player").Select(key => key.Resource).Distinct().ToArray();
        var owned = new List<((string, long), long)>();
        // An empty player list would read the whole club, so each query only runs when the items hold its cards.
        if (players.Length > 0) owned.AddRange((await uow.OwnedPlayerIdentitiesAsync(players, permanentOnly: true)).Select(row => (("player", row.ResourceId), row.ItemId)));
        if (others.Length > 0) owned.AddRange((await uow.OwnedNonPlayerIdentitiesAsync(others)).Select(row => ((row.Kind, row.ResourceId), row.ItemId)));
        return owned;
    }

    /// <summary>The first owned copy of each unique card among <paramref name="items"/>, keyed by <see cref="CardKey"/>.</summary>
    public static async ValueTask<Dictionary<(string Kind, long Resource), long>> OwnedCardIndexAsync(IUnitOfWork uow, IEnumerable<JsonObject> items)
    {
        var index = new Dictionary<(string, long), long>();
        foreach (var (key, itemId) in await OwnedCardsAsync(uow, items)) index.TryAdd(key, itemId);
        return index;
    }

    /// <summary>
    /// The unique cards (players, managers, club items) among <paramref name="items"/> whose card the club already owns (club
    /// or squad), each with the owned copy it duplicates: the same check PUT /item makes before keeping a card in Unassigned.
    /// FIFA reads duplicates only from this duplicateItemIdList; it has no parser for an item's own duplicate flags.
    /// </summary>
    public static async ValueTask<JsonArray> DuplicateListAsync(IUnitOfWork uow, IReadOnlyCollection<JsonObject> items)
    {
        // Only these cards can be duplicates, so the index covers them, not the whole club.
        var owned = await OwnedCardIndexAsync(uow, items);
        var duplicates = new JsonArray();
        foreach (var item in items.Where(IsUniqueCard))
        {
            var itemId = item.Int("id");
            var ownedId = owned.GetValueOrDefault(CardKey(item));
            if (itemId != 0 && ownedId != 0 && ownedId != itemId)
            {
                duplicates.Add(new JsonObject { ["itemId"] = itemId, ["duplicateItemId"] = ownedId });
            }
        }
        return duplicates;
    }

    /// <summary>Player definitions owned in the club or a squad (unassigned pack items excluded).</summary>
    public async ValueTask<HashSet<long>> OwnedPlayerResourceIdsAsync(IUnitOfWork uow) =>
        (await uow.OwnedPlayerIdentitiesAsync()).Select(item => item.ResourceId).ToHashSet();

    public async ValueTask<JsonObject?> OwnedPlayerItemAsync(IUnitOfWork uow, long resourceId) =>
        resourceId == 0 ? null : (await OwnedPlayersAsync(uow)).FirstOrDefault(item => ResourceOrAsset(item) == resourceId);

    /// <summary>
    /// Resource id to the item id of its first owned player card, for checking many cards at once.
    /// <paramref name="resourceIds"/> limits the index to the resources the caller will look up; null builds
    /// the whole club. TryAdd over the club-before-squad order picks the first owned card either way.
    /// </summary>
    public async ValueTask<Dictionary<long, long>> OwnedPlayerIndexAsync(IUnitOfWork uow, long[]? resourceIds = null)
    {
        var index = new Dictionary<long, long>();
        foreach (var item in await uow.OwnedPlayerIdentitiesAsync(resourceIds, permanentOnly: true))
        {
            index.TryAdd(item.ResourceId, item.ItemId);
        }
        return index;
    }

    private static async ValueTask<List<JsonObject>> OwnedPlayersAsync(IUnitOfWork uow)
    {
        var club = await uow.ListItemsAsync("club");
        var squad = await uow.ListItemsAsync("squad");
        return club.Concat(squad).Where(IsPlayer).ToList();
    }

    public async ValueTask<bool> WouldDuplicateOwnedPlayerAsync(IUnitOfWork uow, long itemId)
    {
        var item = await uow.GetItemAsync(itemId);
        if (item is null || !IsPlayer(item))
        {
            return false;
        }
        var resourceId = ResourceOrAsset(item);
        return resourceId != 0 && (await uow.OwnedPlayerIdentitiesAsync(new[] { resourceId }, permanentOnly: true)).Count > 0;
    }

    /// <summary>Persists only the conservative duplicate marker and keeps duplicates in Unassigned.</summary>
    public ValueTask<bool> MarkDuplicateAsync(IUnitOfWork uow, long itemId, bool duplicate = true) =>
        UpdateItemAsync(uow, itemId, data => SetDuplicateMarker(data, duplicate));

    internal static void SetDuplicateMarker(JsonObject data, bool duplicate = true)
    {
        foreach (var key in DuplicateUiKeys)
        {
            data.Remove(key);
        }
        data["duplicate"] = duplicate;
        data["isDuplicate"] = duplicate;
        if (duplicate || !data.ContainsKey("pile"))
        {
            data["pile"] = "unassigned";
        }
    }

    public ValueTask<bool> UpdatePileAsync(IUnitOfWork uow, long itemId, string pile) =>
        UpdateItemAsync(uow, itemId, data => data["pile"] = pile);

    private static async ValueTask<bool> UpdateItemAsync(IUnitOfWork uow, long itemId, Action<JsonObject> change)
    {
        if (await uow.GetItemAsync(itemId) is not { } data)
        {
            return false;
        }
        change(data);
        await uow.SaveItemAsync(data);
        return true;
    }

    public static async ValueTask<List<JsonObject>> PendingPlayerPickTokensAsync(IUnitOfWork uow) =>
        (await uow.ListItemsAsync()).Where(ItemFactory.IsPlayerPickToken).ToList();

    /// <summary>
    /// Applies one position modifier or chemistry style card to a player, or one manager league card to a manager, and takes
    /// one card off an owned stack (club pile first, then Unassigned, lowest id first), all in one transaction.
    /// </summary>
    public async ValueTask<JsonObject> ApplyConsumableAsync(long resourceId, long targetItemId,
        CancellationToken cancellationToken = default)
    {
        var cards = _fut.Game.Cards;
        var style = cards.ChemistryStyle(resourceId);
        var modifier = cards.PositionModifier(resourceId);
        var leagueCard = cards.ManagerLeagueCard(resourceId);
        var kind = style is not null ? "chemistry style" : leagueCard is not null ? "manager league card" : "position modifier";
        if (style is null && modifier is null && leagueCard is null)
        {
            throw new ClubRuleException($"unknown {kind} definition {resourceId}");
        }
        return await _fut.Game.RunAsync("apply-consumable", async uow =>
        {
            // The club lock is the market's order too, so a card cannot be listed between this check and the write.
            await uow.LockClubAsync();
            var (stackId, stack) = (await uow.ListItemsAsync())
                .Where(item => ResourceOrAsset(item) == resourceId && item.Text("pile") is "club" or "unassigned" && StackCount(item) > 0)
                .OrderBy(item => item.Text("pile") == "club" ? 0 : 1)
                .ThenBy(item => item.IntKeep("id"))
                .Select(item => (item.IntKeep("id"), item))
                .FirstOrDefault();
            if (stack is null)
            {
                throw new ClubRuleException($"{kind} is not owned");
            }
            var target = await uow.GetItemAsync(targetItemId) ?? throw new ClubRuleException($"{kind} target was not found");
            if (leagueCard is null && !IsPlayer(target))
            {
                throw new ClubRuleException($"{kind} target is not a player");
            }
            if (leagueCard is not null && target.Text("itemType") != "manager")
            {
                throw new ClubRuleException($"{kind} target is not a manager");
            }
            if (await uow.LiveMarketListingForItemAsync(targetItemId) is not null)
            {
                throw new ClubRuleException(ListedItemReason);
            }
            if (leagueCard is not null)
            {
                // FIFA's own apply screen refuses the same value too (FUT_CANNOT_APPLY_SAME_*).
                if (target.Int("leagueId") == leagueCard.LeagueId)
                {
                    throw new ClubRuleException($"{kind} cannot apply the manager's own league {leagueCard.LeagueId}");
                }
                target["leagueId"] = leagueCard.LeagueId;
            }
            else if (style is not null)
            {
                if (style.Goalkeeper != Catalog.Cards.IsGoalkeeper(target))
                {
                    throw new ClubRuleException(style.Goalkeeper ? "goalkeeper styles apply only to goalkeepers" : "outfield styles cannot be applied to goalkeepers");
                }
                target["playStyle"] = style.PlayStyle;
            }
            else
            {
                var current = target.Str("preferredPosition").ToUpperInvariant();
                if (current != modifier!.Source)
                {
                    throw new ClubRuleException($"position modifier requires {modifier.Source}, target is {current}");
                }
                target["preferredPosition"] = modifier.Target;
            }
            await uow.SaveItemAsync(target);
            var remaining = StackCount(stack) - 1;
            if (remaining <= 0)
            {
                await uow.DeleteItemAsync(stackId);
                return target;
            }
            stack["stackCount"] = remaining;
            if (leagueCard is null)
            {
                stack["amount"] = remaining; // a manager league card's amount is its league
            }
            if (stack.ContainsKey("untradeableCount"))
            {
                stack["untradeableCount"] = Math.Clamp(stack.Int("untradeableCount") - 1, 0, remaining);
            }
            await uow.SaveItemAsync(stackId, stack);
            return target;
        }, cancellationToken);
    }

    /// <summary>How many cards a stored consumable stack holds.</summary>
    public static long StackCount(JsonObject item) => JsonValues.Int(item.GetOr("stackCount", item.GetOr("amount", 1)));

    /// <summary>
    /// Equips a club item into a verified My Stadium slot and frees whatever held it. The item must
    /// match the slot's native item type and card subtype; locked slots cannot be edited.
    /// </summary>
    public async ValueTask<JsonObject> EquipAsync(long itemId, string itemState, CancellationToken cancellationToken = default) =>
        await _fut.Game.RunAsync("equip-club-item", uow => EquipAsync(uow, itemId, itemState), cancellationToken);

    private ValueTask<JsonObject> EquipAsync(IUnitOfWork uow, long itemId, string itemState) =>
        uow.RelationalItems.Customisation.EquipAsync(uow, itemId, itemState);

    /// <summary>
    /// Deletes owned items and credits their server-side value. Client-supplied values are
    /// never trusted; a card in a squad leaves its squads, while equipped slot items and market listings are refused.
    /// </summary>
    public async ValueTask<(long Credits, List<long> Deleted)> DiscardItemsAsync(IEnumerable<JsonNode?> rawIds,
        CancellationToken cancellationToken = default)
    {
        var ids = rawIds.Select(raw => JsonValues.TryInt(raw, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return (await _fut.Game.ReadAsync(uow => uow.CreditsAsync(), cancellationToken), new List<long>());
        }

        return await _fut.Game.RunAsync("discard-items", async uow =>
        {
            await uow.LockClubAsync();
            var items = (await uow.GetItemsAsync(ids)).ToDictionary(item => item.IntKeep("id"));
            var guards = (await uow.RelationalDiscardGuardsAsync(ids.ToArray()))
                .ToDictionary(guard => guard.Id);
            var eligible = new Dictionary<long, long>();
            var inSquads = new List<long>();
            var discardedVanity = new List<long>();
            foreach (var id in ids)
            {
                if (!items.TryGetValue(id, out var data))
                {
                    // A My Stadium item waiting in the New Items pile (virtual): quick selling one throws it away. My
                    // Stadium items are untradeable and worth nothing, so it pays 0.
                    if (await _fut.Vanity.DiscardAsync(uow, id))
                    {
                        eligible.Add(id, 0);
                        discardedVanity.Add(id);
                    }
                    continue;
                }
                var guard = guards[id];
                if (data.Text("pile") == "squad" || guard.InSlot)
                {
                    _log.LogWarning("Refused discard of squad-bound or equipped item {Id}", id);
                    continue;
                }
                if (guard.Listed)
                {
                    _log.LogWarning("Refused discard of market-listed item {Id}", id);
                    continue;
                }
                // A player pays its card's value after the item's own rules (loan, untradeable, reward source); anything else
                // pays its rendered value after the same reward rules, which the render no longer bakes in (consumables are
                // tradeable as built, so an untradeable grant lives in the stored item alone).
                var value = IsPlayer(data)
                    ? data.Bool("expiredLoan") ? 0 : _fut.Game.Cards.QuickSellValue(data.Int("resourceId"), data.Int("loans"), data.Bool("untradeable"), guard.RewardSource)
                    : _fut.Game.Rules.Cards.QuickSell.ForItem(data.Int("discardValue"), data.Bool("untradeable"), guard.RewardSource);
                eligible.Add(id, Math.Max(0, value));
                if (guard.InSquad) inSquads.Add(id);
            }
            // The club screen quick sells a card of a squad and saves that squad without it only afterwards, so the
            // squads let go of it here; refusing left the card in place until a later squad save.
            if (inSquads.Count > 0)
            {
                await uow.RelationalItems.Squads.ReleaseItemsAsync(uow, inSquads.ToArray());
                _log.LogInformation("QUICKSELL released squad cards [{Items}]", string.Join(", ", inSquads));
            }
            var removed = (await uow.DeleteRelationalItemsAsync(eligible.Keys.ToArray())).ToHashSet();
            var deletedIds = ids.Where(removed.Contains).Concat(discardedVanity).ToList();
            var total = deletedIds.Sum(id => eligible[id]);
            var balance = total != 0 ? await uow.AddCreditsToAsync(uow.ClubId, total) : await uow.CreditsAsync();
            _log.LogWarning("QUICKSELL items=[{Items}] payout={Payout} newCredits={Credits}", string.Join(", ", deletedIds), total, balance);
            return (balance, deletedIds);
        }, cancellationToken);
    }

    /// <summary>
    /// The operator panel's removal: takes these player cards out of the club's club, New items or transfer pile and pays
    /// nothing. A card in a squad leaves its squads first, as a quick sell does. Any other item (consumables, managers, club
    /// customisation, player picks), a card listed on the market or an unknown id refuses the whole removal, and nothing
    /// changes.
    /// </summary>
    /// <exception cref="ClubRuleException">An item cannot be removed.</exception>
    public async ValueTask<int> RemoveItemsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
    {
        var natives = ids.Distinct().ToArray();
        return await _fut.Game.RunAsync("panel-remove-items", async uow =>
        {
            await uow.LockClubAsync();
            var guards = (await uow.RelationalDiscardGuardsAsync(natives)).ToDictionary(guard => guard.Id);
            var rows = (await uow.QueryAsync("""
                SELECT native_item_id, pile, family_id FROM ultimateteam.items WHERE club_id=@club AND native_item_id=ANY(@native)
                """, row => (Id: row.GetInt64(0), Pile: row.GetString(1), Family: row.GetInt16(2)), ("native", natives))).ToDictionary(row => row.Id);
            foreach (var id in natives)
            {
                if (!rows.TryGetValue(id, out var row) || !guards.TryGetValue(id, out var guard))
                    throw new ClubRuleException($"The club has no item {id}.");
                if (row.Family != 1)
                    throw new ClubRuleException($"Item {id} is not a player card; only player cards can be removed.");
                if (row.Pile is not ("club" or "unassigned" or "trade"))
                    throw new ClubRuleException($"Item {id} is not in the club, New items or transfer pile.");
                if (guard.Listed)
                    throw new ClubRuleException($"Item {id} is listed on the transfer market; it can be removed once the listing ends.");
            }
            var inSquads = natives.Where(id => guards[id].InSquad).ToArray();
            if (inSquads.Length > 0)
            {
                await uow.RelationalItems.Squads.ReleaseItemsAsync(uow, inSquads);
            }
            var removed = await uow.DeleteRelationalItemsAsync(natives);
            _log.LogWarning("PANEL REMOVE items=[{Items}] released from squads=[{Squads}]", string.Join(", ", removed), string.Join(", ", inSquads));
            return removed.Count;
        }, cancellationToken);
    }

    /// <summary>
    /// The operator panel takes one owned item away and pays nothing for it. A card in a squad leaves its squads first,
    /// as a quick sell does; an equipped My Stadium item or a card on the transfer market is refused and nothing changes.
    /// </summary>
    public async ValueTask TakeAwayItemAsync(long itemId, CancellationToken cancellationToken = default)
    {
        await _fut.Game.RunAsync("panel-take-item", async uow =>
        {
            await uow.LockClubAsync();
            var guards = await uow.RelationalDiscardGuardsAsync([itemId]);
            if (guards.Count == 0) throw new ClubRuleException($"The club holds no item {itemId}.");
            var guard = guards[0];
            if (guard.InSlot) throw new ClubRuleException($"Item {itemId} is equipped in My Stadium or a club slot; it cannot be taken away while equipped.");
            if (guard.Listed) throw new ClubRuleException($"Item {itemId} is listed on the transfer market; it can be taken away once that listing ends.");
            if (guard.InSquad) await uow.RelationalItems.Squads.ReleaseItemsAsync(uow, [itemId]);
            if ((await uow.DeleteRelationalItemsAsync([itemId])).Count != 1) throw new ClubRuleException($"The club holds no item {itemId}.");
            _log.LogWarning("PANEL took item {Item} away (squad released: {InSquad})", itemId, guard.InSquad);
        }, cancellationToken);
    }

    public async ValueTask<int> RemoveNonSquadPlayerItemsAsync(long resourceId, CancellationToken cancellationToken = default)
    {
        return await _fut.Game.RunAsync("remove-player-items", async uow =>
        {
            await uow.LockClubAsync();
            var rows = await uow.ItemsWithResourceAsync(resourceId);
            if (rows.Any(row => row.Pile == "squad"))
            {
                throw new ClubRuleException("refusing to remove a card currently assigned to a squad");
            }
            foreach (var row in rows)
                if (await uow.RelationalItems.Squads.IsItemBoundAsync(uow, row.Id))
                    throw new ClubRuleException("refusing to remove a card currently assigned to a squad");
            await uow.DeleteItemsWithResourceAsync(resourceId);
            return rows.Count;
        }, cancellationToken);
    }

    /// <summary>
    /// PUT/POST /item: pile moves, kit/badge/ball equipping and duplicate handling. Duplicates
    /// always stay in Unassigned but are acknowledged as successful: a failed move makes this
    /// build tear down FUT auth.
    /// </summary>
    public async ValueTask<JsonObject> ApplyItemUpdatesAsync(JsonNode payload, CancellationToken cancellationToken = default)
    {
        var changes = (payload as JsonObject)?.Get("itemData") switch
        {
            JsonArray list => list.OfType<JsonObject>().ToList(),
            JsonObject single => new List<JsonObject> { single },
            _ => new List<JsonObject>(),
        };
        var batch = ((payload as JsonObject)?.Get("itemData") as JsonArray)?.Count > 1;
        return await _fut.Game.RunAsync("update-items", async uow =>
        {
            // One club-locked transaction for the batch: the listed-card check and each move cannot interleave with a listing.
            await uow.LockClubAsync();
            var ids = changes.Select(change => JsonValues.TryInt(JsonValues.HasValue(change.Get("id")) ? change.Get("id") : change.Get("itemId"), out var id) ? id : 0)
                .Where(id => id != 0).Distinct().ToArray();
            // The batch's items, their live listings and the owned cards of their players, read once. Moves then apply
            // in order in memory, so each one sees the ones before it, and every changed item is saved once at the end.
            var items = (await uow.GetItemsAsync(ids)).ToDictionary(item => item.IntKeep("id"));
            var listed = ids.Length == 0 ? new HashSet<long>() : (await uow.LiveMarketListedItemsAsync(ids)).ToHashSet();
            var ownedBy = new Dictionary<(string, long), HashSet<long>>();
            foreach (var (key, ownedId) in await OwnedCardsAsync(uow, items.Values))
            {
                (ownedBy.TryGetValue(key, out var owners) ? owners : ownedBy[key] = new HashSet<long>()).Add(ownedId);
            }
            var changed = new Dictionary<long, JsonObject>();
            // Keeps the owned index current as a unique card enters or leaves the club and squad piles.
            void Move(JsonObject item, Action<JsonObject> change)
            {
                var id = item.IntKeep("id");
                var wasOwned = IsUniqueCard(item) && item.Text("pile") is "club" or "squad";
                change(item);
                if (IsUniqueCard(item) && CardKey(item) is { Resource: not 0 } key)
                {
                    var owners = ownedBy.TryGetValue(key, out var existing) ? existing : ownedBy[key] = new HashSet<long>();
                    if (item.Text("pile") is "club" or "squad") owners.Add(id);
                    else if (wasOwned) owners.Remove(id);
                }
                changed[id] = item;
            }

            var results = new JsonArray();
            var duplicates = new JsonArray();
            foreach (var change in changes)
            {
                if (!JsonValues.TryInt(JsonValues.HasValue(change.Get("id")) ? change.Get("id") : change.Get("itemId"), out var itemId))
                {
                    continue;
                }
                var pile = JsonValues.Text(change.GetOr("pile", "club"), "None");
                var moving = items.GetValueOrDefault(itemId);
                var requestedState = change.Str("itemState");

                if (moving is not null && pile is "club" or "squad" && listed.Contains(itemId))
                {
                    results.Add(new JsonObject
                    {
                        ["id"] = itemId,
                        ["pile"] = moving.Str("pile", "trade"),
                        ["success"] = false,
                        ["reason"] = ListedItemReason,
                    });
                    continue;
                }

                // Swap Duplicate (Python server 328a5b1): {"id": new, "pile": "club", "swap": old}. The client has already
                // moved the club copy to Unassigned and acts on it from there next (quick sell, transfer list), so the new
                // copy takes the old one's place, squad slots included. Ignoring "swap" left the new copy in Unassigned,
                // and the player quick-sold the club copy it showed as the duplicate.
                if (moving is not null && pile is "club" or "squad" && JsonValues.TryInt(change.Get("swap"), out var swapWith)
                    && swapWith != 0 && swapWith != itemId && await SwapDuplicateAsync(uow, moving, swapWith, Move, changed))
                {
                    items = (await uow.GetItemsAsync(ids)).ToDictionary(item => item.IntKeep("id"));
                    results.Add(new JsonObject { ["id"] = itemId, ["pile"] = "club", ["success"] = true });
                    continue;
                }

                // A vanity item (virtual). A pile move keeps it: the New Items screen's Keep/Send-to-Club confirms as
                // {id, pile, swap, tradeId}, and the item is the club's from its grant whatever its slot state — routing
                // that through the equip path refused the locked-category items, and a failed move tears this build's flow
                // down (live 10-03). An itemState equips it in an open slot that takes it, or empties its slot.
                if (moving is null && _fut.Game.Rules.Club.Vanity.Find(itemId) is not null)
                {
                    if (change.ContainsKey("itemState"))
                    {
                        await uow.SaveItemsAsync(changed.Values);
                        changed.Clear();
                        results.Add(await _fut.Vanity.EquipResultAsync(uow, itemId, requestedState));
                        continue;
                    }
                    await _fut.Vanity.StoreAsync(uow, itemId);
                    results.Add(new JsonObject { ["id"] = itemId, ["pile"] = "club", ["success"] = true });
                    continue;
                }

                // The club's own default badge, ball or stadium (a virtual item), picked to replace an owned one.
                if (moving is null && _fut.Game.Rules.Club.RenderedDefaultActiveItems(uow.ClubId)
                        .FirstOrDefault(item => item.Int("id") == itemId && item.Text("itemType") != "kit") is { } clubDefault)
                {
                    await uow.SaveItemsAsync(changed.Values);
                    changed.Clear();
                    results.Add(await EquipResultAsync(uow, itemId, clubDefault.Text("itemState"), clubDefault));
                    items = (await uow.GetItemsAsync(ids)).ToDictionary(item => item.IntKeep("id"));
                    continue;
                }

                if (moving is not null && _fut.Game.Rules.Club.StadiumSlot(requestedState) is { } slot)
                {
                    // Equipping reads and writes other items itself: save the moves so far first, then reload the batch.
                    await uow.SaveItemsAsync(changed.Values);
                    changed.Clear();
                    results.Add(slot.Locked
                        ? new JsonObject { ["id"] = itemId, ["pile"] = moving.Str("pile", "club"), ["success"] = false, ["reason"] = "requested Stadium slot is not editable" }
                        : await EquipResultAsync(uow, itemId, requestedState, moving));
                    items = (await uow.GetItemsAsync(ids)).ToDictionary(item => item.IntKeep("id"));
                    continue;
                }

                var temporaryLoan = moving is not null && (moving.Int("loans") > 0 || moving.Bool("expiredLoan"));
                if (pile is "club" or "squad" && !temporaryLoan && moving is not null && IsUniqueCard(moving)
                    && CardKey(moving) is { Resource: not 0 } cardKey && ownedBy.TryGetValue(cardKey, out var owners)
                    // A card already in the club is not its own duplicate: a repeated Send to Club moved it to Unassigned.
                    && owners.Any(owned => owned != itemId))
                {
                    Move(moving, item => SetDuplicateMarker(item));
                    duplicates.Add(new JsonObject { ["itemId"] = itemId });
                    // Store All: acknowledge with the resulting Unassigned pile so the client does not
                    // animate the card into the club; single moves use the native duplicate marker.
                    var result = new JsonObject { ["id"] = itemId, ["pile"] = "unassigned", ["success"] = true };
                    if (!batch)
                    {
                        result["IS_DUPLICATE"] = true;
                    }
                    results.Add(result);
                    continue;
                }

                if (moving is not null)
                {
                    // FIFA names Unassigned "purchased" on the wire (Transfer Targets' won items are sent there); the
                    // server keeps it as "unassigned", the pile /purchased/items lists. Storing "purchased" hid the cards
                    // from every screen (live 10-04). The reply echoes the pile the client asked for.
                    var stored = pile == "purchased" ? "unassigned" : pile;
                    Move(moving, item =>
                    {
                        // A former duplicate whose club copy is gone joins the club as the club's own card.
                        if (stored is "club" or "squad" && item.Bool("duplicate")) SetDuplicateMarker(item, duplicate: false);
                        item["pile"] = stored;
                    });
                }
                results.Add(new JsonObject { ["id"] = itemId, ["pile"] = pile, ["success"] = moving is not null });
            }
            await uow.SaveItemsAsync(changed.Values);

            var response = new JsonObject { ["itemData"] = results };
            if (duplicates.Count > 0)
            {
                response["duplicateItemIdList"] = duplicates;
            }
            return response;
        }, cancellationToken);
    }

    /// <summary>
    /// Swaps an Unassigned duplicate with the club copy of the same card: the new copy joins the club and takes the old
    /// one's squad slots (captain and kick takers are roles of those slots) or manager slots, the old copy goes to Unassigned
    /// as the duplicate. False, with nothing changed, unless both are copies of one player or manager card, the new one in
    /// Unassigned.
    /// Each copy keeps its own tradeability and reward source, as in FIFA 21: swapping an untradeable duplicate in sends the
    /// tradeable club copy to Unassigned, where it can be listed or quick sold at its full value.
    /// </summary>
    private static async ValueTask<bool> SwapDuplicateAsync(IUnitOfWork uow, JsonObject newCopy, long oldId,
        Action<JsonObject, Action<JsonObject>> move, Dictionary<long, JsonObject> changed)
    {
        if (await uow.GetItemAsync(oldId) is not { } oldCopy || CardKind(newCopy) is not ("player" or "manager")
            || newCopy.Text("pile") != "unassigned" || oldCopy.Text("pile") is not ("club" or "squad")
            || CardKey(newCopy) != CardKey(oldCopy))
        {
            return false;
        }
        var newId = newCopy.IntKeep("id");
        await uow.ExecuteAsync("""
            UPDATE ultimateteam.squad_players sp SET item_id = n.id
            FROM ultimateteam.items n, ultimateteam.items o
            WHERE n.club_id = @club AND n.native_item_id = @new AND o.club_id = @club AND o.native_item_id = @old AND sp.item_id = o.id;
            UPDATE ultimateteam.squad_managers sm SET item_id = n.id
            FROM ultimateteam.items n, ultimateteam.items o
            WHERE n.club_id = @club AND n.native_item_id = @new AND o.club_id = @club AND o.native_item_id = @old AND sm.item_id = o.id
            """, ("new", newId), ("old", oldId));
        move(newCopy, item =>
        {
            SetDuplicateMarker(item, duplicate: false);
            item["pile"] = "club";
        });
        move(oldCopy, item => SetDuplicateMarker(item));
        await uow.SaveItemsAsync(changed.Values);
        changed.Clear();
        return true;
    }

    private async ValueTask<JsonObject> EquipResultAsync(IUnitOfWork uow, long itemId, string itemState, JsonObject moving)
    {
        try
        {
            var equipped = await EquipAsync(uow, itemId, itemState);
            // A club default is virtual: there is no stored item to read back.
            _log.LogInformation("Equipped club item={Item} resource={Resource} state={State}", itemId, equipped?.Get("resourceId") ?? moving.Get("resourceId"), itemState);
            return new JsonObject { ["id"] = itemId, ["pile"] = "club", ["itemState"] = itemState, ["success"] = true };
        }
        catch (ClubRuleException error)
        {
            return new JsonObject { ["id"] = itemId, ["pile"] = moving.Str("pile", "club"), ["success"] = false, ["reason"] = error.Message };
        }
    }

    // ---- squads -------------------------------------------------------------

    /// <summary>
    /// Saves the game's native squad JSON without over-normalizing it, repairing a manager
    /// slot submitted as id 0 (a NullItem the pre-match screen rejects).
    /// </summary>
    public async ValueTask<JsonObject> SaveSquadAsync(JsonNode? payload, CancellationToken cancellationToken = default)
    {
        var source = payload as JsonObject ?? new JsonObject();
        if (source.Obj("squad") is { } nested)
        {
            source = nested;
        }
        return await _fut.Game.RunAsync("save-squad", uow => SaveSquadAsync(uow, source), cancellationToken);
    }

    /// <summary>Squad fields a save never inherits from the stored squad: it may name the squad by either key, and the active column says which squad is active.</summary>
    private static readonly HashSet<string> SquadFieldsNotKept = new(StringComparer.Ordinal) { "active", "name", "squadName" };

    private async ValueTask<JsonObject> SaveSquadAsync(IUnitOfWork uow, JsonObject source)
    {
        var squadId = SquadDocuments.Id(source);

        // The listed-card check and the save share one club-locked transaction (the market's lock order).
        await uow.LockClubAsync();
        // A save is not always the whole squad: renaming sends only the id and the new squadName. Keep every stored field
        // the save leaves out, or a rename wipes the players, formation, captain and kick takers (Python server c5d4098).
        var stored = await uow.GetSquadAsync(squadId);
        foreach (var (key, value) in stored ?? new JsonObject())
        {
            if (!source.ContainsKey(key) && !SquadFieldsNotKept.Contains(key))
            {
                source[key] = value?.DeepClone();
            }
        }
        var storedName = stored is null ? null : SquadDocuments.FirstText(stored, "", "squadName", "name");
        var name = SquadDocuments.FirstText(source, string.IsNullOrEmpty(storedName) ? $"Squad {squadId}" : storedName, "name", "squadName");
        source["id"] = squadId;
        source["squadId"] = squadId;
        source["name"] = name;
        source["squadName"] = name;
        // One round trip for every referenced card: the reason carries no item identity, so which of
        // them is listed cannot change the response.
        if ((await uow.LiveMarketListedItemsAsync(SquadDocuments.ItemIds(source).ToArray())).Count > 0)
        {
            throw new ClubRuleException(ListedItemReason);
        }
        var managerData = source.Arr("manager") is { Count: > 0 } managers && managers[0] is JsonObject first ? first.Obj("itemData") ?? first : null;
        var managerId = managerData?.Int("id") ?? 0;
        if (managerId == 0 || (await uow.GetItemAsync(managerId))?.Text("itemType") != "manager")
        {
            var owned = (await uow.ListItemsAsync())
                .Where(item => item.Text("itemType") == "manager" && item.Int("id") > 0)
                .OrderBy(item => -item.Int("rating"))
                .ThenBy(item => item.Int("id"))
                .FirstOrDefault();
            if (owned is not null)
            {
                source["manager"] = new JsonArray(new JsonObject { ["id"] = owned.Int("id"), ["dream"] = false });
            }
        }
        // Keep a squad's stored active flag unless the save names one: defaulting squad 1 to active on
        // every save pulled the club back to the first squad after switching.
        var active = source.TryGetPropertyValue("active", out var activeNode)
            ? JsonValues.HasValue(activeNode)
            : await uow.SquadActiveAsync(squadId) ?? (squadId == 1 && !await uow.AnyActiveSquadAsync());
        await uow.SaveSquadAsync(squadId, name, active, source);
        return source;
    }

    /// <summary>Creates a new inactive squad from a client squad document (POST /squad, or saving squad 0).</summary>
    public async ValueTask<JsonObject> CreateSquadAsync(JsonNode? payload, CancellationToken cancellationToken = default)
    {
        var source = (payload as JsonObject)?.Copy() ?? new JsonObject();
        if (source.Obj("squad") is { } nested)
        {
            source = nested.Copy();
        }
        return await _fut.Game.RunAsync("create-squad", async uow =>
        {
            // The allocation's club lock is held until the new squad row exists.
            var squadId = await uow.AllocateSquadIdAsync();
            var name = SquadDocuments.FirstText(source, $"Squad {squadId}", "squadName", "name");
            name = name[..Math.Min(name.Length, MaxSquadNameLength)];
            source["id"] = squadId;
            source["squadId"] = squadId;
            source["name"] = name;
            source["squadName"] = name;
            source["formation"] = JsonValues.Str(source.Get("formation"), _fut.Game.Rules.Club.Squad.DefaultFormation);
            source["active"] = false;
            source["players"] = source.Arr("players")?.DeepClone() ?? new JsonArray();
            source["kicktakers"] = source.Arr("kicktakers")?.DeepClone() ?? new JsonArray();
            return await SaveSquadAsync(uow, source);
        }, cancellationToken);
    }

    /// <summary>
    /// Copies a squad into a new inactive squad (CardsDLL FutSquadCopyServerCall appends "/clone" to the squad route; the
    /// route was unknown and every copy failed, Python server d1cc974). The squad as the client sent it is copied when the
    /// body carries players, otherwise the stored one. Null when there is neither.
    /// </summary>
    public async ValueTask<JsonObject?> CloneSquadAsync(long sourceId, JsonNode? payload, string? name = null,
        CancellationToken cancellationToken = default)
    {
        var source = payload is JsonObject sent && sent.Arr("players") is { Count: > 0 } ? sent
            : await _fut.Game.ReadAsync(uow => uow.GetSquadAsync(sourceId), cancellationToken);
        if (source is null)
        {
            return null;
        }
        var copy = source.Copy();
        var rules = _fut.Game.Rules.Club.Squad;
        // FIFA names the copy itself (dstSquadName); without one, the source's name with the copy suffix.
        name = string.IsNullOrWhiteSpace(name)
            ? SquadDocuments.FirstText(copy, $"Squad {sourceId}", "squadName", "name") + rules.CopyNameSuffix
            : name.Trim();
        name = name[..Math.Min(name.Length, MaxSquadNameLength)];
        copy["squadName"] = name;
        copy["name"] = name;
        return await CreateSquadAsync(copy, cancellationToken);
    }

    /// <summary>
    /// Deletes a squad; the club's last squad cannot be deleted. FIFA deletes the active squad before it saves the
    /// squad it switches to (live 10-05: GET squad/1, delete squad/4, PUT squad/1), so the active squad can go and the
    /// first remaining squad, the one FIFA's list shows next, takes the flag.
    /// </summary>
    public async ValueTask<bool> DeleteSquadAsync(long squadId, CancellationToken cancellationToken = default)
    {
        return await _fut.Game.RunAsync("delete-squad", async uow =>
        {
            await uow.LockClubAsync();
            if (await uow.SquadActiveAsync(squadId) is not { } wasActive)
            {
                return false;
            }
            if (await uow.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.squads WHERE club_id=@club") <= 1)
            {
                throw new ClubRuleException("LAST_SQUAD_CANNOT_BE_DELETED");
            }
            var deleted = await uow.DeleteSquadAsync(squadId);
            if (wasActive || !await uow.AnyActiveSquadAsync())
            {
                var next = await uow.ScalarAsync<long>("SELECT min(native_squad_id) FROM ultimateteam.squads WHERE club_id=@club");
                await uow.SetActiveSquadAsync(next);
            }
            return deleted;
        }, cancellationToken);
    }

    /// <summary>
    /// Merges the custom tactics FIFA posts (one entry per mentality) into each named squad's
    /// stored tactics. Returns how many entries were saved.
    /// </summary>
    public async ValueTask<int> SaveTacticsAsync(QueryValues query, JsonNode? payload, CancellationToken cancellationToken = default)
    {
        // The Draft squad is not a club squad: its tactics live on the Draft run (DraftService.SaveTacticsAsync).
        var entries = TacticEntries(query, payload).Where(entry => entry.SquadId != _fut.Draft.SquadId).ToList();
        if (entries.Count == 0)
        {
            return 0;
        }
        return await _fut.Game.RunAsync("save-tactics", async uow =>
        {
            foreach (var (squadId, mentality, entry) in entries)
            {
                await uow.SaveSquadTacticAsync(squadId, mentality, entry);
            }
            return entries.Count;
        }, cancellationToken);
    }

    /// <summary>
    /// The custom tactic entries of a tactics POST with a known mentality and a squad id, the entry's own or the query's.
    /// </summary>
    public IEnumerable<(long SquadId, string Mentality, JsonObject Entry)> TacticEntries(QueryValues query, JsonNode? payload)
    {
        if ((payload as JsonObject)?.Arr("tactics") is not { } entries)
        {
            yield break;
        }
        var mentalities = _fut.Game.Rules.Club.Squad.TacticMentalities;
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var mentality = entry.Str("tactic").ToUpperInvariant();
            if (!mentalities.Contains(mentality))
            {
                continue;
            }
            var squadNode = JsonValues.HasValue(entry.Get("squadId")) ? entry.Get("squadId") : JsonValue.Create(query.Last("squadId") ?? "0");
            if (!JsonValues.TryInt(squadNode, out var squadId) || squadId <= 0)
            {
                continue;
            }
            yield return (squadId, mentality, entry.Copy());
        }
    }

    private IEnumerable<JsonObject> LineupCandidates(IEnumerable<JsonObject> items) =>
        items.Where(IsPlayer).DistinctBy(item => _fut.Game.Players.Find(item.IntKeep("resourceId"))?.IntKeep("assetId") ?? item.IntKeep("resourceId"));

    public async ValueTask SeedSquadAsync(IUnitOfWork uow)
    {
        var rules = _fut.Game.Rules.Club.Squad;
        var squad = new JsonObject
        {
            ["id"] = 1,
            ["squadId"] = 1,
            ["name"] = rules.DefaultName,
            ["squadName"] = rules.DefaultName,
            ["formation"] = rules.DefaultFormation,
            ["rating"] = 0,
            ["chemistry"] = 0,
            ["active"] = true,
            ["players"] = SquadDocuments.Lineup(LineupCandidates(await uow.ListItemsAsync("club")).Take(rules.Starters)),
            ["kicktakers"] = new JsonArray(),
        };
        await uow.SaveSquadAsync(1, rules.DefaultName, true, squad);
    }

    /// <summary>Populates a wholly empty saved squad while preserving any non-empty lineup.</summary>
    public async ValueTask RepairEmptyActiveSquadAsync(IUnitOfWork uow)
    {
        var squads = await uow.ListSquadsAsync();
        var active = squads.FirstOrDefault(s => s.Bool("active")) ?? squads.FirstOrDefault();
        (long Id, JsonObject Data)? selected = active is null ? null : (active.IntKeep("id"), active);
        if (selected is not { } row) return;
        var squad = row.Data;
        if (SquadDocuments.ItemIds(squad).Any(id => id > 0)) return;
        var clubItems = await uow.ListItemsAsync("club");
        var ownedIds = clubItems.Select(item => item.Int("id")).ToHashSet();
        var players = LineupCandidates(clubItems).Take(_fut.Game.Rules.Club.Squad.Starters).ToList();
        if (SquadDocuments.ItemIds(squad).Any(ownedIds.Contains) || players.Count == 0)
        {
            return;
        }
        squad["players"] = SquadDocuments.Lineup(players);
        squad["active"] = true;
        await uow.UpdateSquadDataAsync(row.Id, true, squad);
        _log.LogInformation("Repaired empty FUT squad {Id} with {Count} owned players", row.Id, players.Count);
    }
}
