using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Helpers;

/// <summary>Pieces of FIFA 21's native squad document shared by the club, Draft, Squad Battles and SBC squads.</summary>
public static class SquadDocuments
{
    /// <summary>A saved squad's id: <c>id</c> when set, otherwise <c>squadId</c>, otherwise 1.</summary>
    public static long Id(JsonObject squad) => JsonValues.HasValue(squad.Get("id")) ? squad.Int("id", 1) : squad.Int("squadId", 1);

    /// <summary>The first set text value among <paramref name="keys"/>, or <paramref name="fallback"/>.</summary>
    public static string FirstText(JsonObject source, string fallback, params string[] keys) =>
        keys.Select(key => source.Get(key)).FirstOrDefault(JsonValues.HasValue) is { } value ? JsonValues.Text(value) : fallback;

    /// <summary>A stored chemistry value, or <paramref name="fallback"/> when missing or not numeric.</summary>
    public static long Chemistry(JsonObject squad, long fallback) =>
        squad.Get("chemistry") is { } chemistry && JsonValues.TryInt(chemistry, out var value) ? value : fallback;

    /// <summary>
    /// FUT's squad overall from the starters' ratings (Python server _sqi_squad_overall): the total plus the amount by which
    /// above-average starters exceed the average, divided by the starter count and rounded down, so eleven 84s are an 84.
    /// 0 when fewer than <paramref name="starters"/> rated starters are given, so the caller keeps what it had.
    /// </summary>
    public static long Overall(IEnumerable<long> ratings, int starters)
    {
        var values = ratings.Where(rating => rating > 0).Take(starters).ToList();
        if (starters <= 0 || values.Count < starters)
        {
            return 0;
        }
        var total = values.Sum();
        var average = total / (double)starters;
        var excess = values.Sum(value => Math.Max(0.0, value - average));
        return Math.Clamp((long)Math.Floor((total + excess) / starters), 1, 99);
    }

    public static JsonObject EmptySlot(long index) => new() { ["index"] = index, ["kitNumber"] = 0 };

    public static JsonObject Slot(long index, long kitNumber, JsonNode itemData) =>
        new() { ["index"] = index, ["kitNumber"] = kitNumber, ["itemData"] = itemData };

    /// <summary>A squad slot with its card's attribute record (<see cref="Catalog.Cards.SquadSlotAttributes"/>), as every squad document sends it.</summary>
    public static JsonObject Slot(long index, long kitNumber, JsonObject itemData, Catalog.Cards cards)
    {
        var slot = Slot(index, kitNumber, itemData);
        if (cards.SquadSlotAttributes(itemData) is { } attributes)
        {
            slot["playerAttributes"] = attributes.Copy();
        }
        return slot;
    }

    /// <summary>The native <c>kicktakers</c> list for the given player item ids; <paramref name="isConcept"/> marks concept cards.</summary>
    public static JsonArray KickTakers(IEnumerable<long> itemIds, Func<long, bool>? isConcept = null) =>
        new(itemIds.Select((id, index) => (JsonNode?)new JsonObject { ["index"] = index, ["id"] = id, ["dream"] = isConcept?.Invoke(id) ?? false }).ToArray());

    /// <summary>The first <paramref name="count"/> ids, repeating the last one when fewer are available.</summary>
    public static List<long> PaddedKickTakerIds(IEnumerable<long> itemIds, int count)
    {
        var ids = itemIds.Take(count).ToList();
        while (ids.Count > 0 && ids.Count < count)
        {
            ids.Add(ids[^1]);
        }
        return ids;
    }

    /// <summary>A saved-squad lineup referencing owned items by id.</summary>
    public static JsonArray Lineup(IEnumerable<JsonObject> items) =>
        new(items.Select((item, index) => (JsonNode?)new JsonObject
        {
            ["index"] = index,
            ["itemId"] = item.Get("id")?.DeepClone(),
            ["loyaltyBonus"] = 1,
        }).ToArray());

    /// <summary>Item ids referenced by a squad's player slots (by <c>itemId</c> or the embedded item).</summary>
    /// <summary>
    /// The ids chemistry reads from a saved squad: the cards in the first <paramref name="starters"/> slots and its saved
    /// managers. A slot without an index counts by its position in the list.
    /// </summary>
    public static IEnumerable<long> ChemistryItemIds(JsonObject squad, int starters)
    {
        var slots = (squad.Arr("players") ?? new JsonArray()).OfType<JsonObject>().ToList();
        for (var ordinal = 0; ordinal < slots.Count; ordinal++)
        {
            var slot = slots[ordinal];
            if (JsonValues.TryInt(slot.GetOr("index", ordinal), out var index) && index >= 0 && index < starters
                && JsonValues.TryInt(slot.Get("itemId") ?? slot.Obj("itemData")?.Get("id"), out var id))
            {
                yield return id;
            }
        }
        foreach (var manager in (squad.Arr("manager") ?? new JsonArray()).OfType<JsonObject>())
        {
            yield return (manager.Obj("itemData") ?? manager).Int("id");
        }
    }

    public static List<long> ItemIds(JsonObject squad) =>
        (squad.Arr("players") ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(position => position.Get("itemId") ?? position.Obj("itemData")?.Get("id"))
            .Select(raw => JsonValues.TryInt(raw, out var id) ? (long?)id : null)
            .OfType<long>()
            .ToList();
}
