using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Club;

/// <summary>
/// My Stadium vanity items, as retail FIFA 21 had them. A club owns the rules/club.json vanity starterItems plus what it
/// is granted (club_vanity_items, migration 122: the Stadium Development objectives' item packs), and puts owned items in
/// FIFA's own slots (club_vanity_slots; slot ids are eVanitySlotSubType, which the match engine reads). Slots marked
/// locked open once the club claims an objective whose reward lists them in unlockSlots.
/// </summary>
public sealed class VanityService
{
    private readonly FutServices _fut;

    public VanityService(FutServices fut) => _fut = fut;

    private ClubRules Rules => _fut.Game.Rules.Club;

    /// <summary>The club's vanity choices, by slot id, as virtual item ids (choices the catalogue no longer holds are left out).</summary>
    public async ValueTask<Dictionary<long, long>> EquippedAsync(IUnitOfWork uow)
    {
        var rows = await uow.QueryAsync(
            "SELECT slot_id, native_subtype, native_asset_id FROM ultimateteam.club_vanity_slots WHERE club_id=@club",
            r => (Slot: (long)r.GetInt32(0), Subtype: (long)r.GetInt32(1), Asset: r.GetInt64(2)));
        return rows.Select(row => (row.Slot, ItemId: Rules.Vanity.ItemIdOf(row.Subtype, row.Asset)))
            .Where(row => row.ItemId is not null).ToDictionary(row => row.Slot, row => row.ItemId!.Value);
    }

    /// <summary>
    /// The virtual ids of the items the club owns: the starter items and every granted one the catalogue still holds. A
    /// granted item belongs to the club from the draw, wherever it is: one still waiting in the New Items pile (not yet
    /// stored) counts as owned too, so no grant can pay it twice — pass <paramref name="storedOnly"/> for the kept items
    /// alone (equipping and the pickers).
    /// </summary>
    public async ValueTask<HashSet<long>> OwnedAsync(IUnitOfWork uow, bool storedOnly = false)
    {
        var rows = await RowsAsync(uow, storedOnly);
        return rows.Concat(Rules.Vanity.StarterItems.Select(item => (item.Subtype, item.Asset, Granted: default(long?))))
            .Select(item => Rules.Vanity.ItemIdOf(item.Subtype, item.Asset))
            .Where(id => id is not null).Select(id => id!.Value).ToHashSet();
    }

    /// <summary>The club's granted items, by card subtype and asset id, with when each was granted.</summary>
    private async ValueTask<List<(long Subtype, long Asset, long? Granted)>> RowsAsync(IUnitOfWork uow, bool storedOnly) =>
        await uow.QueryAsync(
            "SELECT native_subtype, native_asset_id, extract(epoch from granted_at)::bigint FROM ultimateteam.club_vanity_items WHERE club_id=@club"
            + (storedOnly ? " AND stored" : ""),
            r => ((long)r.GetInt32(0), r.GetInt64(1), (long?)r.GetInt64(2)));

    /// <summary>
    /// The slot ids open to the club: every slot the rules leave unlocked, plus those named by the unlockSlots of the
    /// objectives (and groups) it has claimed.
    /// </summary>
    public async ValueTask<HashSet<long>> UnlockedSlotsAsync(IUnitOfWork uow)
    {
        var open = Rules.MyStadiumSlots.Where(slot => !slot.Locked).Select(Rules.SlotIdOf).ToHashSet();
        foreach (var reward in await ClaimedRewardsAsync(uow))
        {
            open.UnionWith(reward.UnlockSlots);
        }
        return open;
    }

    /// <summary>The claimed rewards of the club's lifetime objectives and groups (the ones that unlock stadium slots and tiers).</summary>
    private async ValueTask<List<ObjectiveReward>> ClaimedRewardsAsync(IUnitOfWork uow)
    {
        var state = await uow.ObjectivesAsync(_fut.Game.Rules.Objectives);
        var rewards = new List<ObjectiveReward>();
        foreach (var group in _fut.Game.Rules.Objectives.GrindGroups)
        {
            rewards.AddRange(group.Objectives.Where(objective => state.ClaimedObjectives.Contains(objective.Id)).Select(objective => objective.Award));
            if (state.ClaimedGroups.Contains(group.Id))
            {
                rewards.Add(group.Award);
            }
        }
        return rewards;
    }

    /// <summary>
    /// Whether the club may choose this venue: any venue but the upper FUT Stadium tiers, which open with the Stadium
    /// Development rewards (a higher tier opens the ones below it).
    /// </summary>
    public async ValueTask<bool> VenueOpenAsync(IUnitOfWork uow, long venue)
    {
        var tiers = Rules.Venues.FutStadiums;
        var index = tiers.IndexOf(venue);
        if (index <= 0)
        {
            return true;
        }
        var earned = (await ClaimedRewardsAsync(uow)).Select(reward => reward.StadiumTier).OfType<long>().Select(tier => tiers.IndexOf(tier));
        return earned.Any(tier => tier >= index);
    }

    /// <summary>One vanity item, in the state of the slot it is equipped in (an item fills at most one slot).</summary>
    public JsonObject Item(long itemId, IReadOnlyDictionary<long, long> equipped)
    {
        var (subtype, asset) = Rules.Vanity.Find(itemId) ?? throw new ArgumentOutOfRangeException(nameof(itemId));
        var slot = equipped.Where(entry => entry.Value == itemId).Select(entry => Rules.SlotById(entry.Key)).FirstOrDefault(found => found is not null);
        var (rating, rareflag) = Rules.Vanity.RarityOf(subtype, asset);
        var item = TemplateLibrary.Core.Object("items/vanity", new TemplateArgs
        {
            ["id"] = itemId,
            ["resourceId"] = Rules.Vanity.ResourceId(subtype, asset),
            ["asset"] = asset,
            ["subtype"] = subtype,
            ["state"] = slot is not null ? slot.ItemState : "free",
            ["rating"] = rating,
            ["rareflag"] = rareflag,
            ["timestamp"] = _fut.Game.Established,
        });
        // The card's name is a localisation key FIFA looks up, as for kits and stadiums: a nameless audio or colour card
        // showed only its type (FIFA's own lookup by asset id has no string for most of them).
        if (Rules.Vanity.NameKey(subtype, asset) is { Length: > 0 } key)
        {
            item["name"] = key;
        }
        if (Rules.Vanity.ItemColours.TryGetValue(subtype, out var coloured) && coloured.TryGetValue(asset, out var colours))
        {
            item["attributeArray"] = new JsonArray(colours.Select(colour => (JsonNode?)colour).ToArray());
        }
        return item;
    }

    /// <summary>A catalogue card of this subtype for a reward tile (the first one), or null when the catalogue has none.</summary>
    public JsonObject? SampleItem(long subtype, long? asset = null) =>
        Rules.Vanity.Catalogue.TryGetValue(subtype, out var assets) && assets.Count > 0 && Rules.Vanity.ItemIdOf(subtype, asset ?? assets[0]) is { } id
            ? Item(id, new Dictionary<long, long>())
            : null;

    /// <summary>Every vanity item the club has kept, for the My Stadium pickers. A granted item joins them when the player keeps it in the New Items pile.</summary>
    public async ValueTask<List<JsonObject>> ItemsAsync(IUnitOfWork uow)
    {
        var equipped = await EquippedAsync(uow);
        var stored = (await RowsAsync(uow, storedOnly: true)).Select(item => Rules.Vanity.ItemIdOf(item.Subtype, item.Asset)).OfType<long>();
        return stored.Concat(Rules.Vanity.StarterItems
                .Select(item => Rules.Vanity.ItemIdOf(item.Subtype, item.Asset)).OfType<long>())
            .Order().Select(id => Item(id, equipped)).ToList();
    }

    /// <summary>
    /// The granted items waiting in the New Items pile (GET /purchased/items), newest first: the player has kept none of
    /// them yet. A grant owns its item on the spot, so these are already the club's — keeping one moves it to My Stadium
    /// (<see cref="StoreAsync"/>), quick-selling one throws it away (<see cref="DiscardAsync"/>).
    /// </summary>
    public async ValueTask<List<JsonObject>> UnassignedAsync(IUnitOfWork uow)
    {
        var equipped = await EquippedAsync(uow);
        var waiting = await uow.QueryAsync(
            "SELECT native_subtype, native_asset_id, extract(epoch from granted_at)::bigint FROM ultimateteam.club_vanity_items WHERE club_id=@club AND NOT stored",
            r => ((long)r.GetInt32(0), r.GetInt64(1), r.GetInt64(2)));
        return waiting.OrderByDescending(row => row.Item3).ThenBy(row => row.Item1).ThenBy(row => row.Item2)
            .Select(row => Rules.Vanity.ItemIdOf(row.Item1, row.Item2)).OfType<long>()
            .Select(id => PileItem(id, equipped)).ToList();
    }

    /// <summary>How many granted items wait in the New Items pile.</summary>
    public async ValueTask<long> UnassignedCountAsync(IUnitOfWork uow) =>
        await uow.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.club_vanity_items WHERE club_id=@club AND NOT stored");

    /// <summary>Keeps a granted item from the New Items pile: it joins the club's My Stadium items. Starter items have nothing to keep.</summary>
    public async ValueTask StoreAsync(IUnitOfWork uow, long itemId)
    {
        if (Rules.Vanity.Find(itemId) is not { } item)
        {
            return;
        }
        await uow.ExecuteAsync("UPDATE ultimateteam.club_vanity_items SET stored=true WHERE club_id=@club AND native_subtype=@subtype AND native_asset_id=@asset",
            ("subtype", (int)item.Subtype), ("asset", item.Asset));
    }

    /// <summary>Throws a waiting item away (a pile quick sell: My Stadium items are untradeable and worth nothing). Only an unstored one goes.</summary>
    public async ValueTask<bool> DiscardAsync(IUnitOfWork uow, long itemId)
    {
        if (Rules.Vanity.Find(itemId) is not { } item)
        {
            return false;
        }
        return await uow.ExecuteAsync("DELETE FROM ultimateteam.club_vanity_items WHERE club_id=@club AND native_subtype=@subtype AND native_asset_id=@asset AND NOT stored",
            ("subtype", (int)item.Subtype), ("asset", item.Asset)) > 0;
    }

    /// <summary>A granted item as the New Items pile serves it: the club card, waiting (not equipped) in the pile.</summary>
    private JsonObject PileItem(long itemId, IReadOnlyDictionary<long, long> equipped)
    {
        var item = Item(itemId, equipped);
        item["pile"] = "unassigned";
        return item;
    }

    /// <summary>The equipped vanity items by slot id, for the My Stadium document.</summary>
    public async ValueTask<Dictionary<long, JsonObject>> SlotItemsAsync(IUnitOfWork uow)
    {
        var equipped = await EquippedAsync(uow);
        return equipped.ToDictionary(entry => entry.Key, entry => Item(entry.Value, equipped));
    }

    /// <summary>
    /// A reward's items: <paramref name="count"/> items of these subtypes the club does not own yet, drawn at random from
    /// the subtype's rewardOnly list when it has one (so rewards and the store pack never overlap); fewer when they run out.
    /// The slot state never gates a grant: an item whose slot is still locked is the club's from the draw and waits in the
    /// New Items pile with the rest (SetAsync refuses equipping it until Stadium Development opens the slot).
    /// </summary>
    public async ValueTask<List<JsonObject>> GrantAsync(IUnitOfWork uow, IReadOnlyCollection<long> subtypes, long count, IReadOnlyCollection<long>? assets = null)
    {
        var owned = await OwnedAsync(uow);
        var pool = Rules.Vanity.Items().Where(item => subtypes.Contains(item.Subtype) && !owned.Contains(item.ItemId)
            && (assets is { Count: > 0 } ? assets.Contains(item.Asset)
                : !Rules.Vanity.RewardOnly.ContainsKey(item.Subtype) || Rules.Vanity.IsRewardOnly(item.Subtype, item.Asset))).ToList();
        return await OwnAsync(uow, GameRandom.Shared.Sample(pool, (int)Math.Min(count, pool.Count)));
    }

    /// <summary>
    /// A store My Stadium pack (store_packs.vanity_pack, migration 123), drawn by <see cref="VanityRules.DrawPack"/> against
    /// what the club owns; the items are the club's from the draw and wait in the New Items pile until the player keeps them.
    /// </summary>
    public async ValueTask<List<JsonObject>> DrawPackAsync(IUnitOfWork uow, JsonObject spec) =>
        await OwnAsync(uow, Rules.Vanity.DrawPack(spec, await OwnedAsync(uow), GameRandom.Shared));

    private async ValueTask<List<JsonObject>> OwnAsync(IUnitOfWork uow, IReadOnlyList<(long Subtype, long Asset, long ItemId)> items)
    {
        foreach (var item in items)
        {
            await uow.ExecuteAsync("""
                INSERT INTO ultimateteam.club_vanity_items(club_id, native_subtype, native_asset_id) VALUES (@club, @subtype, @asset)
                ON CONFLICT DO NOTHING
                """, ("subtype", (int)item.Subtype), ("asset", item.Asset));
        }
        var equipped = await EquippedAsync(uow);
        return items.Select(item => PileItem(item.ItemId, equipped)).ToList();
    }

    /// <summary>Whether a slot id is a vanity slot.</summary>
    public bool IsVanitySlot(long slotId) =>
        Rules.MyStadiumSlots.Any(slot => slot.ItemType == "vanity" && Rules.SlotIdOf(slot) == slotId);

    /// <summary>
    /// Puts a vanity item in a slot, or empties the slot (item id 0). The slot must be open to the club and take the
    /// item's subtype, and the club must own the item; one card fills one slot, so it leaves any other slot it was in.
    /// </summary>
    public async ValueTask SetAsync(IUnitOfWork uow, long slotId, long itemId)
    {
        if (!IsVanitySlot(slotId))
        {
            throw new ClubRuleException("INVALID_CUSTOMISATION_BINDING");
        }
        await uow.LockClubAsync();
        if (itemId == 0)
        {
            await uow.ExecuteAsync("DELETE FROM ultimateteam.club_vanity_slots WHERE club_id=@club AND slot_id=@slot", ("slot", (int)slotId));
            return;
        }
        if (Rules.Vanity.Find(itemId) is not { } item || Rules.SlotById(slotId) is not { } slot || !slot.VanityTypes.Contains(item.Subtype))
        {
            throw new ClubRuleException("CUSTOMISATION_ITEM_DOES_NOT_MATCH_SLOT");
        }
        if (!(await UnlockedSlotsAsync(uow)).Contains(slotId))
        {
            throw new ClubRuleException("CUSTOMISATION_SLOT_LOCKED");
        }
        if (!(await OwnedAsync(uow, storedOnly: true)).Contains(itemId))
        {
            throw new ClubRuleException("CUSTOMISATION_ITEM_NOT_OWNED");
        }
        await uow.ExecuteAsync("DELETE FROM ultimateteam.club_vanity_slots WHERE club_id=@club AND native_subtype=@subtype AND native_asset_id=@asset AND slot_id<>@slot",
            ("slot", (int)slotId), ("subtype", (int)item.Subtype), ("asset", item.Asset));
        await uow.ExecuteAsync("""
            INSERT INTO ultimateteam.club_vanity_slots(club_id, slot_id, native_subtype, native_asset_id) VALUES (@club, @slot, @subtype, @asset)
            ON CONFLICT (club_id, slot_id) DO UPDATE SET native_subtype=EXCLUDED.native_subtype, native_asset_id=EXCLUDED.native_asset_id
            """, ("slot", (int)slotId), ("subtype", (int)item.Subtype), ("asset", item.Asset));
    }

    /// <summary>
    /// PUT /item for a vanity id: equips it in the slot whose state FIFA asks for (several slots take tifos and props), else
    /// in the slot already holding it or the first open, empty slot that takes it; a free request empties the slot holding it.
    /// </summary>
    public async ValueTask<JsonObject> EquipResultAsync(IUnitOfWork uow, long itemId, string requestedState)
    {
        var (subtype, _) = Rules.Vanity.Find(itemId)!.Value;
        var unlocked = await UnlockedSlotsAsync(uow);
        var slots = Rules.VanitySlots(subtype).Where(slot => unlocked.Contains(Rules.SlotIdOf(slot))).ToList();
        if (slots.Count == 0)
        {
            return new JsonObject { ["id"] = itemId, ["pile"] = "club", ["success"] = false, ["reason"] = "no open slot for this vanity category" };
        }
        var equipped = await EquippedAsync(uow);
        var holding = equipped.Where(entry => entry.Value == itemId).Select(entry => (long?)entry.Key).FirstOrDefault();
        if (requestedState is "" || requestedState.Equals("free", StringComparison.OrdinalIgnoreCase))
        {
            if (holding is { } held)
            {
                await SetAsync(uow, held, 0);
            }
            return new JsonObject { ["id"] = itemId, ["pile"] = "club", ["success"] = true, ["itemState"] = "free" };
        }
        var slot = slots.FirstOrDefault(candidate => candidate.ItemState.Equals(requestedState, StringComparison.OrdinalIgnoreCase))
            ?? (holding is { } current ? slots.FirstOrDefault(candidate => Rules.SlotIdOf(candidate) == current) : null)
            ?? slots.FirstOrDefault(candidate => !equipped.ContainsKey(Rules.SlotIdOf(candidate)))
            ?? slots[0];
        try
        {
            await SetAsync(uow, Rules.SlotIdOf(slot), itemId);
        }
        catch (ClubRuleException error)
        {
            return new JsonObject { ["id"] = itemId, ["pile"] = "club", ["success"] = false, ["reason"] = error.Message };
        }
        return new JsonObject { ["id"] = itemId, ["pile"] = "club", ["success"] = true, ["itemState"] = slot.ItemState };
    }
}
