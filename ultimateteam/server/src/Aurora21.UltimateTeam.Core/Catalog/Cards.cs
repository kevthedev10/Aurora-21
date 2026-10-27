using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>
/// Card-level data derived from the player catalog and rules/cards.json: quick-sell values,
/// pack selection weights, native attribute payloads, position modifiers and the custom
/// Aqua Pro shells.
/// </summary>
public sealed class Cards
{
    /// <summary>The highest card rating and attribute value.</summary>
    public const long MaxRating = 99;

    private readonly PlayerCatalog _players;
    private readonly CardRules _rules;
    private readonly Func<PackRules> _packRules;
    private readonly MarketRules _marketRules;
    private readonly Dictionary<long, PositionModifier> _modifiersByResource;
    private readonly Dictionary<long, ChemistryStyle> _chemistryStylesByResource;
    private readonly IReadOnlyList<ManagerLeagueCard> _managerLeagueCards;
    private readonly Dictionary<long, ManagerLeagueCard> _managerLeagueCardsByResource;
    private readonly Dictionary<long, ManagerLeagueCard> _managerLeagueCardsByLeague;
    // A Pro card's own id to its base card's, and back. Only the Pro card itself is a Pro card: a base card is an ordinary
    // card, which carries the Pro record only for a club that owns the Pro card (OwnedProMetadata).
    private readonly Dictionary<long, long> _proBaseByCustom = new();
    private readonly Dictionary<long, long> _proCustomByBase = new();
    // A resource's face attributes never change, but every render used to look the player up and
    // reparse them. The values are cached; the nodes are still built per call, because a JsonNode has
    // one parent and callers embed what they receive.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long[]> _faceValues = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, JsonObject> _slotAttributes = new();
    private readonly string[] _inGameOrder;
    private readonly IReadOnlyDictionary<long, IReadOnlyList<long>> _rarityGroups;

    public Cards(PlayerCatalog players, CardRules rules, PackRules packRules, MarketRules marketRules)
        : this(players, rules, () => packRules, marketRules) { }

    /// <param name="managerLeagueCards">The catalogue's manager league cards (migration 071); none when omitted.</param>
    public Cards(PlayerCatalog players, CardRules rules, Func<PackRules> packRules, MarketRules marketRules,
        IEnumerable<ManagerLeagueCard>? managerLeagueCards = null)
    {
        _managerLeagueCards = (managerLeagueCards ?? Array.Empty<ManagerLeagueCard>()).OrderBy(card => card.LeagueId).ToList();
        _managerLeagueCardsByResource = _managerLeagueCards.ToDictionary(card => card.ResourceId);
        _managerLeagueCardsByLeague = _managerLeagueCards.ToDictionary(card => card.LeagueId);
        _players = players;
        _rules = rules;
        _rarityGroups = players.NativeRarityGroups ?? rules.RarityGroupsByRareflag.ToDictionary(
            pair => pair.Key, pair => (IReadOnlyList<long>)Array.AsReadOnly(pair.Value.ToArray()));
        _packRules = packRules;
        _marketRules = marketRules;
        _modifiersByResource = rules.PositionModifiers.ToDictionary(modifier => modifier.ResourceId);
        _chemistryStylesByResource = rules.ChemistryStyles.Styles.ToDictionary(style => style.ResourceId);
        foreach (var card in rules.AquaProCards)
        {
            _proBaseByCustom[card.CustomId] = card.BaseId;
            _proCustomByBase[card.BaseId] = card.CustomId;
        }
        _inGameOrder = rules.InGameAttributeOrder.ToArray();
    }

    public IReadOnlyList<PositionModifier> PositionModifiers => _rules.PositionModifiers;

    public PositionModifier? PositionModifier(long resourceId) => _modifiersByResource.GetValueOrDefault(resourceId);

    public CardRules Rules => _rules;

    public IReadOnlyList<ChemistryStyle> ChemistryStyles => _rules.ChemistryStyles.Styles;

    public ChemistryStyle? ChemistryStyle(long resourceId) => _chemistryStylesByResource.GetValueOrDefault(resourceId);

    /// <summary>Every manager league card, in league id order.</summary>
    public IReadOnlyList<ManagerLeagueCard> ManagerLeagueCards => _managerLeagueCards;

    public ManagerLeagueCard? ManagerLeagueCard(long resourceId) => _managerLeagueCardsByResource.GetValueOrDefault(resourceId);

    /// <summary>The manager league card of a native league, or null when that league has none.</summary>
    public ManagerLeagueCard? ManagerLeagueCardForLeague(long leagueId) => _managerLeagueCardsByLeague.GetValueOrDefault(leagueId);

    /// <summary>Whether the definition is a position modifier, chemistry style or manager league card.</summary>
    public bool IsConsumableDefinition(long resourceId) =>
        _modifiersByResource.ContainsKey(resourceId) || _chemistryStylesByResource.ContainsKey(resourceId)
        || _managerLeagueCardsByResource.ContainsKey(resourceId);

    public static bool IsGoalkeeper(JsonObject item) => item.Str("preferredPosition").ToUpperInvariant() == "GK";

    /// <summary>A player's chemistry style; cards never given a valid one play as Basic or GK Basic.</summary>
    public long EffectivePlayStyle(JsonObject item)
    {
        var stored = item.Get("playStyle") is { } node && JsonValues.TryInt(node, out var value) ? value : 0;
        return EffectivePlayStyle(stored, IsGoalkeeper(item));
    }

    /// <summary>The same rule from typed values, for callers that have no item document.</summary>
    public long EffectivePlayStyle(long stored, bool goalkeeper)
    {
        var styles = _rules.ChemistryStyles;
        return styles.Names.ContainsKey(stored) ? stored
            : goalkeeper ? styles.GoalkeeperDefault
            : styles.OutfieldDefault;
    }

    /// <summary>Explicit catalog membership; unknown native rarity codes have no groups.</summary>
    public JsonArray RarityGroups(long rareflag) =>
        _rarityGroups.TryGetValue(rareflag, out var groups)
            ? new JsonArray(groups.Select(group => (JsonNode)JsonValue.Create(group)!).ToArray())
            : new JsonArray();

    /// <summary>
    /// Fixed-size <c>{index, value}</c> stat rows, keeping any recorded values. Rows may be objects
    /// or bare numbers (positional); out-of-range indexes are dropped and negative values clamped.
    /// </summary>
    public JsonArray StatRows(JsonNode? existing)
    {
        var values = new long[_rules.PlayerStatSlots];
        var position = 0;
        foreach (var row in existing as JsonArray ?? new JsonArray())
        {
            long index;
            long value;
            if (row is JsonObject rowObject)
            {
                var valueNode = rowObject.Get("value");
                if (!JsonValues.TryInt(rowObject.GetOr("index", position), out index)
                    || !(JsonValues.HasValue(valueNode) ? JsonValues.TryInt(valueNode, out value) : (value = 0) == 0))
                {
                    position++;
                    continue;
                }
            }
            else if (row is null || !JsonValues.TryInt(row, out value))
            {
                position++;
                continue;
            }
            else
            {
                index = position;
            }
            if (index >= 0 && index < values.Length)
            {
                values[index] = Math.Max(0, value);
            }
            position++;
        }
        return IndexedRows(values);
    }

    /// <summary>
    /// The same fixed-size rows from values that are already typed, without building the
    /// <c>{index, value}</c> objects only to read them straight back out. Out-of-range indexes are
    /// dropped and negative values clamped exactly as the <see cref="JsonNode"/> overload does.
    /// </summary>
    public JsonArray StatRowsFrom(IEnumerable<(int Index, long Value)> recorded)
    {
        var values = new long[_rules.PlayerStatSlots];
        foreach (var (index, value) in recorded)
        {
            if (index >= 0 && index < values.Length)
            {
                values[index] = Math.Max(0, value);
            }
        }
        return IndexedRows(values);
    }

    /// <summary>
    /// Stat rows for a document that may already carry canonical ones. A document this server rendered
    /// already has exactly the right shape, so it is copied rather than parsed apart and rebuilt;
    /// anything else still goes through the full normalisation.
    /// </summary>
    public JsonArray StatRowsOrCopy(JsonNode? existing) =>
        IsCanonicalStatRows(existing) ? (JsonArray)existing!.DeepClone() : StatRows(existing);

    private bool IsCanonicalStatRows(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count != _rules.PlayerStatSlots)
        {
            return false;
        }
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject row || row.Count != 2
                || !JsonValues.TryInt(row.Get("index"), out var rowIndex) || rowIndex != index
                || !JsonValues.TryInt(row.Get("value"), out var value) || value < 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The wire's <c>{index, value}</c> rows. Each object is built from an exactly sized pair array so
    /// its dictionary is sized once instead of growing, and the nodes are fresh every call because a
    /// JsonNode has a single parent and callers embed these directly.
    /// </summary>
    internal static JsonArray IndexedRows(long[] values)
    {
        var rows = new JsonNode?[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            rows[index] = new JsonObject(new[]
            {
                KeyValuePair.Create("index", (JsonNode?)JsonValue.Create((long)index)),
                KeyValuePair.Create("value", (JsonNode?)JsonValue.Create(values[index])),
            });
        }
        return new JsonArray(rows);
    }

    public bool IsConceptItemId(long itemId) =>
        itemId > _rules.ConceptItemIdBase && itemId < _rules.ConceptItemIdBase + _rules.ConceptItemIdSpan;

    public long ConceptItemId(long resourceId) => _rules.ConceptItemIdBase + resourceId;

    public long ConceptResourceId(long itemId) => itemId - _rules.ConceptItemIdBase;

    /// <summary>The Pro card's own id when this is a Pro card (rules/cards.json aquaProCards), or null. A base card is not one.</summary>
    public long? AquaProResourceId(long resourceId) => _proBaseByCustom.ContainsKey(resourceId) ? resourceId : null;

    public bool IsAquaPro(long resourceId) => _proBaseByCustom.ContainsKey(resourceId);

    /// <summary>Developer cards are always untradeable, including new native Pro card definitions.</summary>
    public bool IsDeveloperCard(long resourceId) => IsAquaPro(resourceId) || _players.Find(resourceId)?.Rareflag == 10;

    /// <summary>Every Pro card's own id.</summary>
    public IReadOnlyCollection<long> ProCardIds => _proBaseByCustom.Keys;

    /// <summary>The Pro card built on this base card (FIFA's details screen also asks for a Pro card by its base asset id), or null.</summary>
    public long? ProCardOfBase(long baseId) => _proCustomByBase.TryGetValue(baseId, out var id) ? id : null;

    /// <summary>
    /// Whether only an explicit grant hands out this card (Python server 8d674f7): no pack, pick, Draft, loan, SBC match or
    /// concept search ever offers it. Every Pro card is one (the developers' exclusive cards).
    /// </summary>
    public bool IsGrantOnly(long resourceId) => _rules.GrantOnlyResourceIds.Contains(resourceId) || _proBaseByCustom.ContainsKey(resourceId);

    /// <summary>
    /// Whether a pack or pick limited to <paramref name="named"/> rarities (none: any) may draw this rarity: a reward-only
    /// rarity only when named (Python server _pick_rareflag_allowed).
    /// </summary>
    public bool RarityAllowed(long rareflag, IReadOnlyCollection<long>? named) =>
        named is { Count: > 0 } ? named.Contains(rareflag) : !_rules.RewardOnlyRareflags.Contains(rareflag);

    /// <summary>
    /// A card definition's quick-sell value before any item rule (rules/cards.json quickSell, retail FIFA 21): the rating times a
    /// multiplier of its kind, base cards by tier and rarity, every special by the in-form bands.
    /// </summary>
    public long DiscardValue(long resourceId)
    {
        var quickSell = _rules.QuickSell;
        var meta = _players.Find(resourceId);
        var rareflag = meta?.Rareflag ?? 0;
        var rating = meta?.Rating ?? 0;
        if (quickSell.BaseCompetitionRareflags.Contains(rareflag))
        {
            return quickSell.BaseValue(_packRules().TierFor(rating, meta?.Tier ?? ""), rating, rare: true);
        }
        if (meta?.IsSpecial != true)
        {
            return quickSell.BaseValue(_packRules().TierFor(rating, meta?.Tier ?? ""), rating, rare: rareflag == 1);
        }
        return quickSell.SpecialValue(rareflag, rating);
    }

    /// <summary>
    /// What quick-selling one owned copy pays: nothing on loan, otherwise the card's value after the item's reward rules
    /// (nothing for a Match Pack item, at most the reward-untradeable cap for an untradeable one).
    /// </summary>
    public long QuickSellValue(long resourceId, long loanGames, bool untradeable, string? rewardSource) =>
        loanGames > 0 ? 0 : _rules.QuickSell.ForItem(DiscardValue(resourceId), untradeable, rewardSource);

    /// <summary>The transfer market listing price range of a definition: the market's bounds, with the minimum raised above the card's quick-sell value.</summary>
    public (long Minimum, long Maximum) MarketPriceBounds(long resourceId)
    {
        var meta = _players.Find(resourceId);
        var (minimum, maximum) = _marketRules.BoundsFor(meta?.Rating ?? 0, meta?.IsSpecial ?? false);
        // Never listable below what quick sell pays (owner, 2026-10-01): otherwise a card listed cheap is bought and quick-sold for
        // more. The floor sits one grid step above the value, so a buyer always pays more than the card quick-sells for.
        var floor = _marketRules.SnapUp(DiscardValue(resourceId) + 1);
        return floor > minimum ? (floor, Math.Max(maximum, floor)) : (minimum, maximum);
    }

    /// <summary>Whether every card of this definition is untradeable whatever grants it (the swap tokens).</summary>
    public bool IsAlwaysUntradeable(long resourceId) => _rules.UntradeableRareflags.Contains(_players.Find(resourceId)?.Rareflag ?? 0);

    /// <summary>Selection weight in packs and player picks (elite ratings are boosted).</summary>
    public double PackWeight(long resourceId) => _packRules().EliteWeightFor(_players.Find(resourceId)?.Rating ?? 0);

    /// <summary>The six face stats as native <c>[{index, value}]</c> item attributes.</summary>
    public JsonArray AttributeList(long resourceId)
    {
        var values = _faceValues.GetOrAdd(resourceId, id =>
            (_players.Find(id)?.Get("face") as JsonArray ?? new JsonArray())
                .Select(node => JsonValues.TryInt(node, out var value) ? value : 0).ToArray());
        return IndexedRows(values);
    }

    /// <summary>
    /// The attribute record a squad slot carries for its card (<c>playerAttributes</c>), or null for anything but a known
    /// player card. CardsDLL's squad-slot parser applies it like a metadata record, so both clients in an online match
    /// hold identical values for all 22 players: a client fetches /attributes/metadata only for its own cards and would
    /// otherwise estimate its opponent's, and each side's pre-match squad check then fails (Python server 951b763).
    /// The record is shared and must not be changed; copy it before putting it into a document.
    /// </summary>
    public JsonObject? SquadSlotAttributes(JsonObject card)
    {
        if (card.Text("itemType").ToLowerInvariant() != "player" || card.Int("resourceId") is not (> 0 and var resourceId)
            || _players.Find(AquaProResourceId(resourceId) ?? resourceId) is not { } meta)
        {
            return null;
        }
        var assetId = card.Int("assetId");
        var metaAsset = meta.Int("assetId");
        if (assetId != 0 && metaAsset != 0 && assetId != metaAsset)
        {
            return null;
        }
        return _slotAttributes.GetOrAdd(resourceId, Metadata);
    }

    /// <summary>
    /// The published Pro record for a base card id, for a club that owns the Pro card built on it: FIFA's details screen alternates
    /// between a Pro card's own id and its base asset id and indexes the response by the id it asked for. Null when no Pro
    /// card is built on this id. Any other club gets <see cref="Metadata"/>: the base card as it is.
    /// </summary>
    public JsonObject? OwnedProMetadata(long baseId) => ProCardOfBase(baseId) is { } customId ? MetadataForDefinition(baseId, customId) : null;

    /// <summary>
    /// One record for CardsDLL's FutGetPlayerAttributesServerResponse decoder, which expects
    /// integer arrays (not web-app style <c>{index, value}</c> objects).
    /// </summary>
    public JsonObject Metadata(long resourceId) => MetadataForDefinition(resourceId, resourceId);

    private JsonObject MetadataForDefinition(long requestedId, long definitionId)
    {
        var meta = _players.Find(definitionId);
        long MetaInt(string key) => meta?.Int(key) ?? 0;
        var faceCount = _rules.OutfieldAttributeGroups.Count;

        var face = (meta?.Get("face") as JsonArray ?? new JsonArray())
            .Select(node => Clamp(JsonValues.TryInt(node, out var value) ? value : 0))
            .Concat(Enumerable.Repeat(0L, faceCount))
            .Take(Math.Max(faceCount, (meta?.Get("face") as JsonArray)?.Count ?? 0))
            .ToList();

        var supplied = new Dictionary<string, long?>(StringComparer.Ordinal);
        if (meta?.Get("attributes") is JsonObject attributes)
        {
            foreach (var (name, value) in attributes)
            {
                supplied[name.ToLowerInvariant().Replace("_", string.Empty, StringComparison.Ordinal)] = JsonValues.TryInt(value, out var parsed) ? parsed : null;
            }
        }

        // An attribute of 0 is unknown. Unknown attributes take their face stat; a goalkeeper's single group spans every GK attribute.
        long? Known(string name) => supplied.GetValueOrDefault(name) is { } value && value > 0 ? value : null;
        var goalkeeper = (meta?.Text("preferredPosition") ?? string.Empty).ToUpperInvariant() == "GK";
        var groups = goalkeeper ? new List<List<string>> { _rules.GoalkeeperAttributes } : _rules.OutfieldAttributeGroups;
        var resolved = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var fallback = groupIndex < face.Count ? face[groupIndex] : 0;
            foreach (var name in groups[groupIndex])
            {
                resolved[name] = Clamp(Known(name) ?? fallback);
            }
        }
        var inGame = _inGameOrder.Select(name => Clamp(Known(name) ?? resolved.GetValueOrDefault(name))).ToList();
        // FIFA uses a card's own attributes only when its record carries a version; otherwise it estimates them from its
        // player table. The version is sent only when every value is the card's own and they reproduce its face.
        var complete = _inGameOrder.All(name => Known(name) is >= 1 and <= MaxRating) && FaceMatches(inGame, face, goalkeeper);

        // Pro shells need an authoritative record even when their inherited GK
        // attributes are unknown. Its version must track the published values,
        // rather than forcing a maxed record on every detail/squad request.
        return MetadataRecord(requestedId, inGame, face.Take(faceCount), Clamp(MetaInt("rating")),
            MetaInt("trait1"), MetaInt("trait2"), versioned: complete || IsAquaPro(definitionId));
    }

    /// <summary>The version of the record the server serves for a card now (0: no complete record), cached per card.</summary>
    public long AttributeRecordVersion(long resourceId) => _slotAttributes.GetOrAdd(resourceId, Metadata).Int("version");

    private static JsonObject MetadataRecord(long defId, IEnumerable<long> inGame, IEnumerable<long> face, long rating, long trait1, long trait2,
        bool versioned)
    {
        var inGameValues = inGame.ToList();
        var faceValues = face.ToList();
        return new JsonObject
        {
            ["defId"] = defId,
            ["ingameattribs"] = IntArray(inGameValues),
            ["attributeArray"] = IntArray(faceValues),
            ["rating"] = rating,
            ["trait1"] = trait1,
            ["trait2"] = trait2,
            ["version"] = versioned ? RecordVersion(inGameValues, faceValues, rating, trait1, trait2) : 0,
        };
    }

    /// <summary>
    /// A record's version, derived from its content (Python server 4736f1a): any change to a card's served stats is a new
    /// version, so CreateMatch can list a client's stale copies. The same hash as the Python server (SHA-1 of the compact
    /// JSON [ingame, face, rating, trait1, trait2]; 4 plus the first four bytes modulo 1,000,000), so both agree.
    /// </summary>
    internal static long RecordVersion(IReadOnlyList<long> inGame, IReadOnlyList<long> face, long rating, long trait1, long trait2)
    {
        var text = $"[[{string.Join(",", inGame)}],[{string.Join(",", face)}],{rating},{trait1},{trait2}]";
        var digest = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return VersionBase + System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(digest) % VersionModulo;
    }

    /// <summary>Content versions start above the constant versions served before (up to 3).</summary>
    private const long VersionBase = 4;
    private const long VersionModulo = 1_000_000;

    /// <summary>
    /// FIFA's native star, work-rate and trait fields for one outgoing player item (see rules/cards.json nativeWorkRates),
    /// or null when the item's asset is not the catalogue card's (an alias, which FIFA resolves itself).
    /// </summary>
    public JsonObject? NativeCardFields(long resourceId, long assetId)
    {
        if (_players.Find(resourceId) is not { } meta)
        {
            return null;
        }
        var cardAsset = meta.Int("assetId");
        if (assetId != 0 && cardAsset != 0 && assetId != cardAsset)
        {
            return null;
        }
        var fields = new JsonObject();
        if (meta.Int("skillMoves") is var skill and >= 1 and <= 5) fields["skillmoves"] = skill - 1;
        if (meta.Int("weakFoot") is var weak and >= 1 and <= 5) fields["weakfootabilitytypecode"] = weak;
        if (_rules.NativeWorkRates.TryGetValue(meta.Str("workRateAttack").ToLowerInvariant(), out var attack)
            && _rules.NativeWorkRates.TryGetValue(meta.Str("workRateDefense").ToLowerInvariant(), out var defense))
        {
            fields["attackingworkrate"] = attack;
            fields["defensiveworkrate"] = defense;
        }
        fields["trait1"] = meta.Int("trait1");
        fields["trait2"] = meta.Int("trait2");
        return fields;
    }

    /// <summary>Whether attributes in <see cref="CardRules.InGameAttributeOrder"/> reproduce the six face ratings (FIFA's formulas).</summary>
    private bool FaceMatches(IReadOnlyList<long> inGame, IReadOnlyList<long> face, bool goalkeeper)
    {
        var formulas = goalkeeper ? _rules.FaceAttributeWeights.Goalkeeper : _rules.FaceAttributeWeights.Outfield;
        if (face.Count < formulas.Count)
        {
            return false;
        }
        var named = _inGameOrder.Select((name, index) => (name, value: inGame[index])).ToDictionary(pair => pair.name, pair => pair.value, StringComparer.Ordinal);
        return formulas.Select((weights, index) => (weights, index)).All(formula =>
            Math.Abs((long)Math.Floor(formula.weights.Sum(weight => named.GetValueOrDefault(weight.Key) * weight.Value) + 0.5) - face[formula.index])
                <= _rules.FaceAttributeTolerance);
    }

    private static long Clamp(long value) => Math.Clamp(value, 0, MaxRating);

    private static JsonArray IntArray(IEnumerable<long> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
