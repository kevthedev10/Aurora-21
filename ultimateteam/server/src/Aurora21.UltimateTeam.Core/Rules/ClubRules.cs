using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Rules;

public sealed class SquadRules
{
    public required string DefaultName { get; set; }

    /// <summary>Appended to a squad's name when FIFA copies it (POST /squad/{id}/clone).</summary>
    public required string CopyNameSuffix { get; set; }
    public required string DefaultFormation { get; set; }
    public required int Slots { get; set; }
    public required int Starters { get; set; }
    public required int KickTakers { get; set; }
    public required string CustomTactics { get; set; }
    public required long DefaultChemistry { get; set; }
    public required long DefaultStarRating { get; set; }

    /// <summary>The <c>squadType</c> of a club squad that stores none (the client's own save sends REGULAR_SQUAD).</summary>
    public required string DefaultSquadType { get; set; }

    /// <summary>Custom tactics are stored and returned one per mentality, in this order.</summary>
    public required List<string> TacticMentalities { get; set; }
}

/// <summary>rules/club.json clubNames: how a shared-server player's own club name and abbreviation are made (Config.ClubNames).</summary>
public sealed class ClubNameRules : IJsonOnDeserialized
{
    public required string NameSuffix { get; set; }
    public required int NameMaxLength { get; set; }
    public required int AbbreviationLength { get; set; }

    public void OnDeserialized()
    {
        if (NameMaxLength < 3 || AbbreviationLength < 1)
        {
            throw new InvalidDataException("rules/club.json clubNames needs a nameMaxLength of at least 3 and an abbreviationLength of at least 1.");
        }
    }
}

public sealed class InitialKit
{
    public required long ResourceId { get; set; }
    public required string ItemState { get; set; }
    public required long TeamId { get; set; }
    public required long KitType { get; set; }
    public required string Name { get; set; }
}

/// <summary>One verified My Stadium slot: the item it holds, where it shows and whether it can be edited or emptied.</summary>
public sealed class MyStadiumSlot
{
    public required string ItemState { get; set; }
    public required string ItemType { get; set; }
    public required long CardSubType { get; set; }
    public required string SlotName { get; set; }
    public required long StadiumArea { get; set; }

    /// <summary>A fixed native slot id; slots without one are numbered by list position.</summary>
    public long? SlotId { get; set; }

    /// <summary>
    /// Whether the slot starts locked: a locked slot opens for a club once it claims an objective whose reward lists it in
    /// unlockSlots (retail's Stadium Development milestones).
    /// </summary>
    public required bool Locked { get; set; }
    public required bool Emptiable { get; set; }
    /// <summary>The card subtypes the slot accepts; CardsDLL reads them as integers.</summary>
    public required List<long> VanityTypes { get; set; }
}

/// <summary>One starter My Stadium item: a catalogue card by subtype and asset id.</summary>
public sealed class VanityStarterItem
{
    public required long Subtype { get; set; }
    public required long Asset { get; set; }
}

/// <summary>
/// One rarity band of rules/club.json vanity.rarity: the card rating and rare flag of these subtypes' items, optionally only
/// for assets from <see cref="Min"/> to <see cref="Max"/>. FIFA colours a card by rating (bronze, silver, gold) and shines it
/// when the rare flag is set.
/// </summary>
public sealed class VanityRarity
{
    public required List<long> Subtypes { get; set; }
    public long? Min { get; set; }
    public long? Max { get; set; }
    public required long Rating { get; set; }
    public required long Rareflag { get; set; }
}

/// <summary>rules/club.json vanity: the My Stadium vanity catalogue, the starter items and how ids are formed.</summary>
public sealed class VanityRules
{
    public required long ItemIdBase { get; set; }
    public required long SubtypeStride { get; set; }
    public required Dictionary<long, long> ResourceOffsets { get; set; }

    /// <summary>
    /// The localisation key format of each subtype's item name ({0} = the resource id), sent as the card's name: FIFA looks a
    /// card's name up, as it does for kits (TeamName_Abbr15_*) and stadiums (StadiumName_*).
    /// </summary>
    public Dictionary<long, string> NameKeys { get; set; } = new();

    /// <summary>
    /// Per-item name keys, by subtype then asset id: the paints, nets, line paint and pyro, whose names are the game's colour
    /// keys (FUT_VANITYCOLOUR_BLUE_RED...) rather than a numbered key.
    /// </summary>
    public Dictionary<long, Dictionary<long, string>> ItemNames { get; set; } = new();

    /// <summary>
    /// The colour values of an item, by subtype then asset id, sent as its attributeArray: CardsDLL 0x1803c4a80 hands the
    /// match engine a My Stadium slot's colour component n from the card's attribute array (base paint 0-1, seat paint 0,
    /// goal net 0-2, pyro 0); without it the stadium preview turned the stairs dark.
    /// </summary>
    public Dictionary<long, Dictionary<long, List<long>>> ItemColours { get; set; } = new();

    /// <summary>The card name (a localisation key) of a catalogue item, or an empty string.</summary>
    public string NameKey(long subtype, long asset) =>
        ItemNames.TryGetValue(subtype, out var named) && named.TryGetValue(asset, out var key) ? key
        : NameKeys.TryGetValue(subtype, out var format) ? string.Format(System.Globalization.CultureInfo.InvariantCulture, format, ResourceId(subtype, asset)) : string.Empty;

    /// <summary>The rating of an item no <see cref="Rarity"/> band covers (a common card).</summary>
    public required long Rating { get; set; }

    /// <summary>Rarity bands, first match wins (see <see cref="RarityOf"/>).</summary>
    public List<VanityRarity> Rarity { get; set; } = new();

    /// <summary>An item's card rating and rare flag: the first <see cref="Rarity"/> band that covers it, else <see cref="Rating"/>, common.</summary>
    public (long Rating, long Rareflag) RarityOf(long subtype, long asset) =>
        Rarity.FirstOrDefault(band => band.Subtypes.Contains(subtype) && asset >= (band.Min ?? long.MinValue) && asset <= (band.Max ?? long.MaxValue))
            is { } found ? (found.Rating, found.Rareflag) : (Rating, 0);

    public required Dictionary<long, List<long>> Catalogue { get; set; }

    /// <summary>The items every club owns from the start (retail's basic setup); everything else is granted.</summary>
    public List<VanityStarterItem> StarterItems { get; set; } = new();

    /// <summary>Catalogue assets only rewards give, by subtype: rewards of a listed subtype draw from these, store packs never.</summary>
    public Dictionary<long, List<long>> RewardOnly { get; set; } = new();

    /// <summary>Tifo card art found in the game files and not catalogued yet (animated, big, collection), kept for assigning later.</summary>
    public Dictionary<string, List<long>> UnassignedTifos { get; set; } = new();

    /// <summary>Whether only rewards may give this item.</summary>
    public bool IsRewardOnly(long subtype, long asset) => RewardOnly.TryGetValue(subtype, out var assets) && assets.Contains(asset);

    /// <summary>What each card subtype is called (FIFA's eVanityCardSubType), for the panel and pack previews.</summary>
    public Dictionary<long, string> SubtypeNames { get; set; } = new();

    public string SubtypeName(long subtype) => SubtypeNames.GetValueOrDefault(subtype) ?? $"Type {subtype}";

    /// <summary>Most slots a My Stadium pack may have.</summary>
    public const int MaxPackSlots = 12;

    /// <summary>
    /// Why a store My Stadium pack spec (store_packs.vanity_pack) cannot be sold, or null. It is <c>{"slots": [...]}</c>;
    /// a slot names catalogued <c>subtypes</c> and optional weighted <c>pools</c> of asset ranges (<c>weight</c>,
    /// <c>min</c>, <c>max</c>), and every slot and pool must hold an item a store pack may give.
    /// </summary>
    public string? PackIssue(JsonObject spec)
    {
        if (spec.Any(entry => entry.Key != "slots") || spec.Arr("slots") is not { Count: > 0 and <= MaxPackSlots } slots)
            return $"A My Stadium pack needs 1 to {MaxPackSlots} slots and nothing else.";
        for (var index = 0; index < slots.Count; index++)
        {
            var name = $"Slot {index + 1}";
            if (slots[index] is not JsonObject slot || slot.Any(entry => entry.Key is not ("subtypes" or "pools")))
                return $"{name}: a slot has only subtypes and pools.";
            if (slot.Arr("subtypes") is not { Count: > 0 } subtypes || subtypes.Any(node => !JsonValues.IsStrictInt(node, out var subtype) || !Catalogue.ContainsKey(subtype)))
                return $"{name}: choose at least one catalogued item type.";
            var open = SellableItems(subtypes.Select(node => JsonValues.Int(node)).ToHashSet()).ToList();
            if (open.Count == 0) return $"{name}: those item types have nothing a store pack may give.";
            if (!slot.ContainsKey("pools")) continue;
            if (slot.Arr("pools") is not { Count: > 0 and <= 10 } pools) return $"{name}: give 1 to 10 pools, or none.";
            foreach (var node in pools)
            {
                if (node is not JsonObject pool || pool.Any(entry => entry.Key is not ("weight" or "min" or "max")))
                    return $"{name}: a pool has only weight, min and max.";
                if (pool.Get("weight") is not JsonValue weightValue || !weightValue.TryGetValue(out double weight) || weight <= 0 || weight > 1000000)
                    return $"{name}: a pool's weight is a number above 0.";
                if (pool.ContainsKey("min") && !JsonValues.IsStrictInt(pool.Get("min"), out _) || pool.ContainsKey("max") && !JsonValues.IsStrictInt(pool.Get("max"), out _))
                    return $"{name}: a pool's min and max are whole asset ids.";
                var (min, max) = (pool.Int("min", long.MinValue), pool.Int("max", long.MaxValue));
                if (min > max) return $"{name}: a pool's min exceeds its max.";
                if (!open.Any(item => item.Asset >= min && item.Asset <= max)) return $"{name}: a pool has no item a store pack may give.";
            }
        }
        return null;
    }

    /// <summary>The catalogue items of these subtypes a store pack may give (not reward-only).</summary>
    public IEnumerable<(long Subtype, long Asset, long ItemId)> SellableItems(IReadOnlySet<long> subtypes) =>
        Items().Where(item => subtypes.Contains(item.Subtype) && !IsRewardOnly(item.Subtype, item.Asset));

    /// <summary>
    /// One draw of a store My Stadium pack: one item per slot from the slot's subtypes, never a reward-only item, one in
    /// <paramref name="owned"/>, or one drawn earlier in the pack. A slot's pools weigh asset ranges (the basic tifo block
    /// against the club and player tifos); a slot with nothing left is skipped.
    /// </summary>
    public List<(long Subtype, long Asset, long ItemId)> DrawPack(JsonObject spec, IReadOnlySet<long> owned, GameRandom random)
    {
        var drawn = new List<(long Subtype, long Asset, long ItemId)>();
        foreach (var slot in (spec.Arr("slots") ?? new JsonArray()).OfType<JsonObject>())
        {
            var subtypes = (slot.Arr("subtypes") ?? new JsonArray()).Select(node => JsonValues.Int(node)).ToHashSet();
            var open = SellableItems(subtypes).Where(item => !owned.Contains(item.ItemId) && !drawn.Contains(item)).ToList();
            var pools = (slot.Arr("pools") ?? new JsonArray { new JsonObject { ["weight"] = 1 } }).OfType<JsonObject>()
                .Select(pool => (Weight: JsonValues.Float(pool.Get("weight"), 1), Items: open.Where(item =>
                    item.Asset >= pool.Int("min", long.MinValue) && item.Asset <= pool.Int("max", long.MaxValue)).ToList()))
                .Where(pool => pool.Weight > 0 && pool.Items.Count > 0).ToList();
            if (pools.Count == 0) continue;
            var chosen = random.WeightedChoice(pools, pools.Select(pool => pool.Weight).ToList());
            drawn.Add(random.Choice(chosen.Items));
        }
        return drawn;
    }

    /// <summary>Every vanity item: its card subtype, asset id and virtual item id.</summary>
    public IEnumerable<(long Subtype, long Asset, long ItemId)> Items() =>
        Catalogue.SelectMany(entry => entry.Value.Select((asset, index) => (entry.Key, asset, ItemIdBase + entry.Key * SubtypeStride + index)));

    public long ResourceId(long subtype, long asset) => ResourceOffsets.GetValueOrDefault(subtype) + asset;

    /// <summary>The vanity item a virtual id names, or null.</summary>
    public (long Subtype, long Asset)? Find(long itemId)
    {
        var offset = itemId - ItemIdBase;
        if (offset < 0)
        {
            return null;
        }
        var (subtype, index) = Math.DivRem(offset, SubtypeStride);
        return Catalogue.TryGetValue(subtype, out var assets) && index < assets.Count ? (subtype, assets[(int)index]) : null;
    }

    /// <summary>The virtual id of a stored (subtype, asset) choice, or null when the catalogue no longer holds it.</summary>
    public long? ItemIdOf(long subtype, long asset) =>
        Catalogue.TryGetValue(subtype, out var assets) && assets.IndexOf(asset) is var index and >= 0 ? ItemIdBase + subtype * SubtypeStride + index : null;
}

/// <summary>rules/club.json news: the FUT News screen (futlivemsggen4) message (Python server ab6b0eb).</summary>
public sealed class NewsRules
{
    /// <summary>Whether the hub and mass info announce FUT News; the Python server's ULTIMATETEAM21_LIVE_MESSAGES switch.</summary>
    public required bool Enabled { get; set; }
    public required long MessageId { get; set; }
    public required string Title { get; set; }
    public required List<string> Lines { get; set; }

    /// <summary>The body: the heading repeated, since builds that ignore the title field still show it, then the lines.</summary>
    public string Message => Title + "\n\n" + string.Join("\n", Lines);
}

/// <summary>A stadium card's rating and rare flag (rules/club.json venues.futStadiumCards).</summary>
public sealed class VenueCardRarity
{
    public required long Rating { get; set; }
    public required long Rareflag { get; set; }
}

/// <summary>rules/club.json venues: which venue ids FIFA 21 can load for a match.</summary>
public sealed class VenueRules
{
    public required HashSet<long> Installed { get; set; }
    public required HashSet<long> Bare { get; set; }
    public required List<long> FutStadiums { get; set; }
    public required string FutStadiumName { get; set; }

    /// <summary>
    /// The card a Stadium Development reward shows for each FUT Stadium tier (rating and rare flag, by venue): the tier's own
    /// stadium card, 6200000 + venue, named StadiumName_&lt;venue&gt; (FUT Stadium, FUT Challenger Stadium, FUT Champions Stadium).
    /// </summary>
    public Dictionary<long, VenueCardRarity> FutStadiumCards { get; set; } = new();

    /// <summary>The tier 1 FUT Stadium: the venue of a club that has chosen none.</summary>
    public long DefaultVenueId => FutStadiums[0];

    /// <summary>Whether a match can be played at this venue: an installed venue with stands, or a FUT Stadium.</summary>
    public bool IsMatchVenue(long venue) => FutStadiums.Contains(venue) || (Installed.Contains(venue) && !Bare.Contains(venue));

    /// <summary>The venue itself when a match can be played there, otherwise the tier 1 FUT Stadium.</summary>
    public long MatchVenue(long? venue) => venue is { } id && IsMatchVenue(id) ? id : DefaultVenueId;
}

/// <summary>rules/club.json: squad defaults, starter kits, default active items and My Stadium slots.</summary>
public sealed class ClubRules : IJsonOnDeserialized
{
    public required SquadRules Squad { get; set; }
    public required ClubNameRules ClubNames { get; set; }
    public required List<InitialKit> InitialKits { get; set; }
    public required JsonArray DefaultActiveItems { get; set; }
    public required long DefaultStadiumCapacity { get; set; }
    /// <summary>Catalogue balls FIFA 21 has no fcc_balls row for (a blank *BallName_ card): never drawn into a pack.</summary>
    public required HashSet<long> UnknownBalls { get; set; }
    public required VenueRules Venues { get; set; }
    public required VanityRules Vanity { get; set; }
    public required NewsRules News { get; set; }
    public required List<MyStadiumSlot> MyStadiumSlots { get; set; }
    public required HashSet<string> NonEquippableItemTypes { get; set; }
    public required HashSet<string> MatchDayItemTypes { get; set; }

    /// <summary>Whether store packs and FUT Draft entries can be bought with FIFA Points (off in phase 1: no FP economy).</summary>
    public required bool FifaPointsPurchases { get; set; }
    public required Dictionary<string, HashSet<string>> PositionZones { get; set; }
    public required Dictionary<string, string> PositionZoneAliases { get; set; }
    public required Dictionary<string, string> PositionGroupTypes { get; set; }
    public required List<string> UnsupportedVanityCategories { get; set; }
    public required Dictionary<string, int> VirtualItemIdSlots { get; set; }

    /// <summary>One virtual club cosmetic's item id in a club's own band (see <see cref="VirtualItemIdSlots"/>).</summary>
    public long VirtualItemId(long clubId, string itemState) => ClubRepository.VirtualItemIdOf(clubId, VirtualItemIdSlots[itemState]);

    /// <summary>Fills the default active items' <c>{{<itemState>Id}}</c> placeholders for one club.</summary>
    public TemplateArgs VirtualItemIdArgs(TemplateArgs args, long clubId)
    {
        foreach (var itemState in _defaultItemStates)
        {
            args[itemState + "Id"] = VirtualItemId(clubId, itemState);
        }
        return args;
    }

    /// <summary>The default active items as one club sees them (its own virtual ids), for matching requested ids.</summary>
    public List<JsonObject> RenderedDefaultActiveItems(long clubId) =>
        DefaultActiveItemsTemplate.RenderArray(VirtualItemIdArgs(new TemplateArgs { ["timestamp"] = 0, ["stadiumId"] = 0, ["capacity"] = 0 }, clubId))
            .OfType<JsonObject>().ToList();

    private List<string> _defaultItemStates = new();

    /// <summary>Whether a position belongs to a zone named directly or by one of its aliases.</summary>
    public bool PositionInZone(string? position, string? zone) =>
        PositionZoneAliases.TryGetValue((zone ?? string.Empty).Trim().ToLowerInvariant(), out var name)
        && PositionZones.TryGetValue(name, out var members)
        && members.Contains((position ?? string.Empty).ToUpperInvariant());

    public MyStadiumSlot? StadiumSlot(string itemState) => MyStadiumSlots.FirstOrDefault(slot => slot.ItemState == itemState);

    /// <summary>A slot's native id: its fixed slotId, else its list position.</summary>
    public long SlotIdOf(MyStadiumSlot slot) => slot.SlotId ?? MyStadiumSlots.IndexOf(slot) + 1;

    /// <summary>The vanity slots that take a vanity card subtype, in list order (a tifo fits every tifo position).</summary>
    public IEnumerable<MyStadiumSlot> VanitySlots(long subtype) =>
        MyStadiumSlots.Where(slot => slot.ItemType == "vanity" && slot.VanityTypes.Contains(subtype));

    /// <summary>The slot with this native id, or null.</summary>
    public MyStadiumSlot? SlotById(long slotId) => MyStadiumSlots.FirstOrDefault(slot => SlotIdOf(slot) == slotId);

    /// <summary>The default active items with <c>{{timestamp}}</c>, <c>{{stadiumId}}</c>, <c>{{capacity}}</c> and per-club id placeholders.</summary>
    [JsonIgnore]
    public JsonTemplate DefaultActiveItemsTemplate { get; private set; } = null!;

    public void OnDeserialized()
    {
        _defaultItemStates = DefaultActiveItems.OfType<JsonObject>().Select(item => item.Text("itemState")).ToList();
        var missing = _defaultItemStates.Where(state => !VirtualItemIdSlots.ContainsKey(state)).ToList();
        if (missing.Count > 0 || VirtualItemIdSlots.Values.Any(slot => slot < 1 || slot > ClubRepository.VirtualItemIdReserve)
            || VirtualItemIdSlots.Values.Distinct().Count() != VirtualItemIdSlots.Count)
        {
            throw new InvalidDataException("rules/club.json virtualItemIdSlots needs a distinct slot (1 to 16) for every default active item state.");
        }
        DefaultActiveItemsTemplate = JsonTemplate.Parse("rules/club.json defaultActiveItems", DefaultActiveItems.ToJsonString());
        if (Venues.FutStadiums.Count == 0)
        {
            throw new InvalidDataException("rules/club.json venues.futStadiums needs the tier 1 FUT Stadium.");
        }
    }
}
