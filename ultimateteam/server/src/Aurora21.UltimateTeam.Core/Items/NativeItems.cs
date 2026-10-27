using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Core.Items;

/// <summary>Projections of stored items onto the exact item shapes each native decoder accepts.</summary>
public sealed class NativeItems
{
    private static readonly string[] ServerOnlyKeys = { "pile", "special", "tier", "wefutCard", "expiredLoan", Rules.RewardSources.ItemKey };

    /// <summary>The concept search page size when the request names none, and its largest page.</summary>
    private const long ConceptSearchDefaultCount = 21;
    private const long ConceptSearchMaxCount = 150;

    /// <summary>The rating a consumable stack shows when neither the rules nor the stored card name one.</summary>
    private const long ConsumableStackDefaultRating = 75;

    private readonly GameContext _game;
    private readonly ItemFactory _items;

    public NativeItems(GameContext game, ItemFactory items)
    {
        _game = game;
        _items = items;
    }

    /// <summary>
    /// Card with a rarity FIFA 21's bundled roster cannot name (anything but plain
    /// gold/silver/bronze rareflag 0/1).
    /// </summary>
    public static bool IsSpecialCard(JsonObject item)
    {
        var node = item.Get("rareflag");
        return JsonValues.HasValue(node) && JsonValues.TryInt(node, out var rareflag) && rareflag is not (0 or 1);
    }

    /// <summary>Stored item types that are consumable stacks.</summary>
    public static bool IsConsumable(JsonObject item) =>
        item.Text("itemType").ToLowerInvariant() is "consumable" or "training" or "development";

    /// <summary>Pack/unassigned item shape: the stored item minus server-only keys; consumables use their native shape.</summary>
    public JsonObject PackItem(JsonObject item)
    {
        if (IsConsumable(item) && ConsumableItem(item) is { } consumable)
        {
            return consumable;
        }
        var output = item.Copy();
        if (item.Text("itemType") is "player" or "manager")
        {
            var contracts = item.Text("itemType") == "player" ? _items.PlayableContracts(item) : _game.Rules.Cards.PermanentContracts;
            output["contract"] = contracts;
            if (output.ContainsKey("contracts")) output["contracts"] = contracts;
            if (ItemFactory.IsExpiredLoan(item)) output["loans"] = 1;
        }
        foreach (var key in ServerOnlyKeys)
        {
            output.Remove(key);
        }
        // A loan's matches left travel as its contract count (ItemFactory.ShownContracts); the stored contract stays 0.
        if (output.Text("itemType", "player") == "player" && output.Int("loans") > 0 && !ItemFactory.IsExpiredLoan(item))
        {
            var shown = ItemFactory.ShownContracts(JsonValues.Int(output.GetOr("contract", output.GetOr("contracts", 7))), output.Int("loans"));
            output["contract"] = shown;
            output["contracts"] = shown;
        }
        if (output.Text("itemType", "player") == "player" && IsSpecialCard(output))
        {
            // Icons/TOTY/TOTS have no bundled roster entry, so the reveal needs the name here.
            output.Update(NameFields(output));
        }
        // A Pro card shares its base player's asset, so FIFA would show that player's stars and traits from its own table
        // (4-star Bruno Fernandes on his 5-star Pro card): send the card's own. Other cards keep the client's values here.
        if (output.Text("itemType", "player") == "player" && _game.Cards.IsAquaPro(output.Int("resourceId"))
            && _game.Cards.NativeCardFields(output.Int("resourceId"), output.Int("assetId")) is { } native)
        {
            output.Update(native);
        }
        return output;
    }

    /// <summary>
    /// A stored item as the club search pages serve it. A loan's matches left travel as its contract count
    /// (<see cref="ItemFactory.ShownContracts"/>), so a page handing a stored loan's render to FIFA must swap the
    /// games in or the card shows 0 matches; an expired loan keeps its native loan flag (loans 1) and drops the
    /// server-only expiry marker. Every other field stays exactly as rendered, and items that are not loans
    /// keep the instance they were given.
    /// </summary>
    public JsonObject SearchItem(JsonObject item)
    {
        if (item.Text("itemType") != "player" || (item.Int("loans") <= 0 && !ItemFactory.IsExpiredLoan(item)))
        {
            return item;
        }
        var output = item.Copy();
        var contracts = _items.PlayableContracts(item);
        output["contract"] = contracts;
        if (output.ContainsKey("contracts")) output["contracts"] = contracts;
        output.Remove("expiredLoan");
        if (ItemFactory.IsExpiredLoan(item)) output["loans"] = 1;
        return output;
    }

    public JsonObject PlayerItem(JsonObject item)
    {
        JsonArray ListOrEmpty(string key) => item.GetOr(key, new JsonArray()) is JsonArray list ? (JsonArray)list.DeepClone() : new JsonArray();

        var output = new JsonObject
        {
            ["assetId"] = JsonValues.Int(item.GetOr("assetId", item.Get("resourceId"))),
            ["attributeList"] = ListOrEmpty("attributeList"),
            ["cardsubtypeid"] = item.Int("cardsubtypeid"),
            // A loan's matches left travel as its contract count (ItemFactory.ShownContracts).
            ["contract"] = _items.PlayableContracts(item),
            // Recalculate from current card rules; an expired loan must still sell for nothing.
            ["discardValue"] = ItemFactory.IsExpiredLoan(item) ? 0 : _game.Cards.QuickSellValue(JsonValues.Int(item.GetOr("resourceId", item.Get("assetId"))), item.Int("loans"), item.Bool("untradeable"),
                item.Text(Rules.RewardSources.ItemKey) is { Length: > 0 } source ? source : null),
            ["fitness"] = item.IntOr("fitness", 99),
            ["injuryGames"] = item.Int("injuryGames"),
            ["injuryType"] = 0,
            ["itemState"] = item.Str("itemState", "free"),
            ["itemType"] = "player",
            ["lastSalePrice"] = item.Int("lastSalePrice"),
            ["leagueId"] = item.Int("leagueId"),
            ["nation"] = item.Int("nation"),
            ["loans"] = ItemFactory.IsExpiredLoan(item) ? 1 : item.Int("loans"),
            ["morale"] = item.IntOr("morale", 50),
            ["owners"] = item.Int("owners", 1),
            ["preferredPosition"] = item.Str("preferredPosition"),
            ["preferredFoot"] = item.Str("preferredFoot"),
            ["weakFoot"] = item.Int("weakFoot"),
            ["skillMoves"] = item.Int("skillMoves"),
            ["workRateAttack"] = item.Str("workRateAttack"),
            ["workRateDefense"] = item.Str("workRateDefense"),
            ["rareflag"] = JsonValues.Int(item.GetOr("rareflag", _game.Config.RareFlag)),
            ["rating"] = item.Int("rating"),
            ["resourceId"] = JsonValues.Int(item.GetOr("resourceId", item.Get("assetId"))),
            ["suspension"] = item.Int("suspension"),
            ["teamid"] = item.Int("teamid"),
            ["timestamp"] = JsonValues.Int(item.GetOr("timestamp", _game.Established)),
            ["training"] = item.Int("training"),
            ["untradeable"] = item.Bool("untradeable"),
            ["playStyle"] = _game.Cards.EffectivePlayStyle(item),
            ["posMods"] = ListOrEmpty("posMods"),
            ["id"] = item.Int("id"),
            ["statsList"] = _game.Cards.StatRowsOrCopy(item.Get("statsList")),
            ["lifetimeStats"] = _game.Cards.StatRowsOrCopy(item.Get("lifetimeStats")),
            ["assists"] = item.Int("assists"),
            ["lifetimeAssists"] = item.Int("lifetimeAssists"),
        };
        // The stored loyalty bonus makes a card loyal whatever its owner count, in FIFA's chemistry and in ours
        // (ChemistryModel.Loyal); without it a bought card with the bonus scored one chemistry short of the client.
        if (item.Get("loyaltyBonus") is not null)
        {
            output["loyaltyBonus"] = item.Int("loyaltyBonus");
        }
        if (_game.Cards.RarityGroups(output.Int("rareflag")) is { Count: > 0 } groups)
        {
            output["groups"] = groups;
        }
        // FIFA reads stars, work rates and traits only from its own lower-case fields (rules/cards.json).
        if (_game.Cards.NativeCardFields(output.Int("resourceId"), output.Int("assetId")) is { } native)
        {
            output.Update(native);
        }
        // Only special cards get catalogue names: doing it for every player broke consumable
        // application and Draft swaps. Skill moves/weak foot keep the client's own values.
        if (IsSpecialCard(item))
        {
            output.Update(NameFields(item));
        }
        return output;
    }

    /// <summary>Every catalogue card whose definition or asset id is <paramref name="defId"/> as a concept card, highest rated first (GET /defid).</summary>
    public List<JsonObject> DefinitionVariants(long defId) => _game.Players.Entries
        .Where(entry => defId != 0 && (entry.Value.IntOr("assetId", entry.Key, entry.Key) == defId || entry.Key == defId) && !_game.Cards.IsGrantOnly(entry.Key))
        .Select(entry => ConceptPlayerItem(entry.Key))
        .OrderByDescending(item => item.Int("rating"))
        .ToList();

    /// <summary>
    /// A full native player card flagged as a concept (FIFA draws <c>dream</c> cards with the grey
    /// shell). Its item id sits in the concept range, so no ownership path finds a stored item.
    /// </summary>
    public JsonObject ConceptPlayerItem(long resourceId)
    {
        var item = PlayerItem(_items.BuildPlayer(resourceId, _game.Cards.ConceptItemId(resourceId), "club"));
        item.Update(new JsonObject
        {
            ["dream"] = true,
            ["concept"] = true,
            ["untradeable"] = true,
            ["discardValue"] = 0,
            ["lastSalePrice"] = 0,
            ["marketDataMinPrice"] = 0,
            ["marketDataMaxPrice"] = 0,
            ["loyaltyBonus"] = 0,
            // Two owners: never packed, so never loyal. With one owner FIFA's squad screen called the card loyal while its
            // chemistry service did not, and every swap previewed a chemistry drop (Python server, commit e7fc5e1).
            ["owners"] = 2,
        });
        return item;
    }

    /// <summary>A Draft card: a native player that is never loyal (two owners, no loyalty bonus), as a concept card.</summary>
    public JsonObject DraftPlayerItem(JsonObject player)
    {
        var item = PlayerItem(player);
        item["owners"] = 2;
        item["loyaltyBonus"] = 0;
        return item;
    }

    /// <summary>
    /// The squad screen's concept search: every player definition matching the club-search filters
    /// (position group, league, nation, team, level, rarity, position), excluding cards already in
    /// the squad, highest rated first.
    /// </summary>
    public List<JsonObject> ConceptSearch(QueryValues query)
    {
        string Value(string name) => query.Last(name) ?? string.Empty;
        long Number(string name, long fallback) => long.TryParse(Value(name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        var clubRules = _game.Rules.Club;
        var zone = clubRules.PositionGroupTypes.GetValueOrDefault(Value("type").ToLowerInvariant());
        var excluded = Value("excldef").Split(',').Select(part => long.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1).ToHashSet();
        var (league, nation, team) = (Number("league", -1), Number("nation", -1), Number("team", -1));
        var level = Value("level").ToLowerInvariant();
        var rare = Value("rare").ToUpperInvariant();
        var position = (query.Last("pos") is { Length: > 0 } pos ? pos : Value("position")).ToUpperInvariant();
        var positions = position.Split('-');
        var start = Math.Max(0, Number("start", 0));
        var count = Math.Clamp(Number("count", Number("num", ConceptSearchDefaultCount)), 1, ConceptSearchMaxCount);
        var tiers = _game.Packs.Rules.Tiers;

        return _game.Players.Entries
            .Where(entry => entry.Value.Rating > 0 && !excluded.Contains(entry.Key) && !_game.Cards.IsGrantOnly(entry.Key))
            .Where(entry =>
            {
                var meta = entry.Value;
                var preferred = meta.Position;
                return (zone is null || clubRules.PositionInZone(preferred, zone))
                       && (position.Length == 0 || positions.Contains(preferred.ToUpperInvariant()))
                       && (league <= 0 || meta.Int("leagueId") == league)
                       && (nation <= 0 || meta.Int("nation") == nation)
                       && (team <= 0 || meta.Int("teamid") == team)
                       && level switch
                       {
                           "gold" => meta.Rating >= tiers.GoldMinRating,
                           "silver" => meta.Rating >= tiers.SilverMinRating && meta.Rating < tiers.GoldMinRating,
                           "bronze" => meta.Rating < tiers.SilverMinRating,
                           _ => true,
                       }
                       && (rare is not ("SP" or "SPECIAL") || meta.IntOr("rareflag", 1) > 1);
            })
            .OrderByDescending(entry => entry.Value.Rating)
            .ThenByDescending(entry => entry.Value.IntOr("rareflag", 1))
            .ThenBy(entry => entry.Key)
            .Skip((int)Math.Min(start, int.MaxValue))
            .Take((int)count)
            .Select(entry => ConceptPlayerItem(entry.Key))
            .ToList();
    }

    /// <summary>
    /// A stored position modifier or chemistry style in the member set FIFA 21's development-item
    /// factory accepts, or null for a consumable that is not a native definition.
    /// </summary>
    public JsonObject? ConsumableItem(JsonObject item)
    {
        var cards = _game.Cards;
        var resourceId = JsonValues.Int(item.GetOr("resourceId", item.GetOr("assetId", 0)));
        var style = cards.ChemistryStyle(resourceId);
        var modifier = cards.PositionModifier(resourceId);
        var stackCount = Math.Max(1, JsonValues.Int(item.GetOr("stackCount", item.GetOr("amount", 1)), 1));
        if (cards.ManagerLeagueCard(resourceId) is { } leagueCard)
        {
            var untradeable = item.Bool("untradeable");
            var card = _items.ManagerLeagueCardItem(leagueCard, item.Int("id"), item.Int("timestamp", _game.NowSeconds), item.Str("itemState", "free"),
                item.Str("pile", "club"), stackCount, untradeable, JsonValues.Int(item.GetOr("untradeableCount", untradeable ? stackCount : 0)),
                item.Int("owners", 1), item.Int("lastSalePrice"));
            // The quick-sell value the item carries follows its reward rules (a Match Pack or untradeable card sells for
            // nothing), so a stored zero is kept rather than read as missing.
            card["discardValue"] = item.IntKeep("discardValue", card.Int("discardValue"));
            return card;
        }
        if (style is null && modifier is null)
        {
            return null;
        }
        var args = new TemplateArgs
        {
            ["id"] = item.Int("id"),
            ["timestamp"] = item.Int("timestamp", _game.NowSeconds),
            ["resourceId"] = resourceId,
            ["itemState"] = item.Str("itemState", "free"),
            ["rareflag"] = item.Int("rareflag", 1),
            ["rareFlag"] = JsonValues.Int(item.GetOr("rareFlag", item.GetOr("rareflag", 1)), 1),
            ["pile"] = item.Str("pile", "club"),
            ["discardValue"] = item.Int("discardValue"),
            ["lastSalePrice"] = item.Int("lastSalePrice"),
            ["owners"] = item.Int("owners", 1),
            ["untradeable"] = item.Bool("untradeable", true),
            ["stackCount"] = stackCount,
            ["untradeableCount"] = Math.Clamp(JsonValues.Int(item.GetOr("untradeableCount", stackCount)), 0, stackCount),
            ["weightrare"] = item.Int("weightrare", 1),
        };
        if (style is not null)
        {
            args["cardAssetId"] = cards.Rules.ChemistryStyles.CardAssetId;
            args["rating"] = item.Int("rating", cards.Rules.ChemistryStyles.Rating);
            args["subType"] = style.PlayStyle;
            // FIFA files the style by its amount (attackers, midfielders, defenders, all, goalkeepers); the stack size is stackCount.
            args["positionGroup"] = style.PositionGroup;
            return TemplateLibrary.Core.Object("items/native-chemistry-style", args);
        }
        args["cardAssetId"] = item.Int("cardassetid", cards.Rules.PositionModifierCard.CardAssetId);
        args["rating"] = item.Int("rating", modifier!.Rating);
        args["subType"] = modifier.SubType;
        return TemplateLibrary.Core.Object("items/native-position-modifier", args);
    }

    /// <summary>
    /// One Club &gt; Consumables catalogue stack: every owned copy of a definition summed. The
    /// nested item has no id, state or ownership: FutConsumablesSearchServerResponse carries
    /// catalogue stacks, not owned items.
    /// </summary>
    public JsonObject ConsumableStack(IReadOnlyList<JsonObject> copies)
    {
        var first = copies[0];
        var resourceId = JsonValues.Int(first.GetOr("resourceId", first.GetOr("assetId", 0)));
        long Stack(JsonObject copy) => Math.Max(1, copy.Int("stackCount", 1));
        var count = copies.Sum(Stack);
        var untradeable = copies.Where(copy => copy.Bool("untradeable", true)).Sum(Stack);
        var modifier = _game.Cards.PositionModifier(first.Int("resourceId"));
        var style = _game.Cards.ChemistryStyle(first.Int("resourceId"));
        return TemplateLibrary.Core.Object("club/consumable-stack", new TemplateArgs
        {
            // A chemistry style's amount is its position group (FIFA lists it under that group); other consumables keep theirs.
            ["amount"] = style?.PositionGroup ?? first.Int("amount"),
            ["cardAssetId"] = first.Int("cardassetid", _game.Cards.Rules.PositionModifierCard.CardAssetId),
            ["subType"] = style?.PlayStyle ?? first.Int("cardsubtypeid"),
            ["rareflag"] = first.Int("rareflag", 1),
            ["rating"] = modifier?.Rating ?? first.Int("rating", ConsumableStackDefaultRating),
            ["resourceId"] = resourceId,
            ["weightRare"] = modifier?.WeightRare ?? first.Int("weightrare"),
            ["bronze"] = first.Int("bronze"),
            ["silver"] = first.Int("silver"),
            ["gold"] = first.Int("gold"),
            ["discardValue"] = first.Int("discardValue"),
            ["count"] = count,
            ["untradeableCount"] = Math.Min(count, untradeable),
        });
    }

    /// <summary>A manager's quick-sell value from the rules, for a manager item that carries none.</summary>
    private long ManagerValue(long rating, long rareflag) =>
        _game.Rules.Cards.QuickSell.ManagerValue(_game.Rules.Packs.TierFor(rating), rating, rareflag != 0);

    /// <summary>Native manager subtype 4. Squad assignment is expressed by the squad, not by item state.</summary>
    public JsonObject ManagerItem(JsonObject item)
    {
        var meta = _game.Managers.Find(JsonValues.Int(item.GetOr("resourceId", item.Get("assetId")))) ?? new JsonObject();
        return new JsonObject
        {
            ["id"] = item.Int("id"),
            ["assetId"] = JsonValues.Int(item.GetOr("assetId", item.Get("resourceId"))),
            ["resourceId"] = JsonValues.Int(item.GetOr("resourceId", item.Get("assetId"))),
            ["carddbid"] = JsonValues.Int(item.GetOr("carddbid", item.Get("resourceId"))),
            ["stackCount"] = Math.Max(1, item.Int("stackCount", 1)),
            ["untradeableCount"] = Math.Max(0, item.Int("untradeableCount")),
            ["resourceGameYear"] = 2021,
            ["itemType"] = "manager",
            ["itemState"] = item.Str("itemState", "free"),
            ["cardsubtypeid"] = 4,
            ["category"] = item.Int("category"),
            ["contract"] = _game.Rules.Cards.PermanentContracts,
            ["negotiation"] = item.Int("negotiation"),
            ["discardValue"] = item.ContainsKey("discardValue") ? item.Int("discardValue") : ManagerValue(item.ContainsKey("rating") ? item.Int("rating") : meta.Int("rating"), item.Int("rareflag")),
            ["lastSalePrice"] = item.Int("lastSalePrice"),
            ["leagueId"] = item.Int("leagueId"),
            ["nation"] = item.Int("nation"),
            ["teamid"] = item.Int("teamid"),
            ["owners"] = item.Int("owners", 1),
            ["rareflag"] = item.Int("rareflag"),
            ["rating"] = item.Int("rating"),
            ["timestamp"] = JsonValues.Int(item.GetOr("timestamp", _game.Established)),
            ["untradeable"] = item.Bool("untradeable"),
            ["pile"] = item.Str("pile", "club"),
            ["firstName"] = JsonValues.Str(item.GetOr("firstName", meta.GetOr("firstName", string.Empty))),
            ["lastName"] = JsonValues.Str(item.GetOr("lastName", meta.GetOr("lastName", string.Empty))),
            ["commonname"] = JsonValues.Str(item.GetOr("commonname", meta.GetOr("commonname", string.Empty))),
        };
    }

    /// <summary>Resolves a stored player item back to its catalogue entry via any id it carries.</summary>
    public PlayerMeta? CatalogueMeta(JsonObject item)
    {
        foreach (var key in new[] { "resourceId", "dynamicImageResourceId", "assetId", "carddbid" })
        {
            var id = item.Int(key);
            if (id != 0 && _game.Players.Find(id) is { } meta)
            {
                return meta;
            }
        }
        return _game.Players.FindByAsset(item.Int("assetId"));
    }

    public JsonObject NameFields(JsonObject item)
    {
        var meta = CatalogueMeta(item);

        string Pick(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (JsonValues.HasValue(item.Get(key)))
                {
                    return JsonValues.Text(item.Get(key));
                }
            }
            if (meta is not null)
            {
                foreach (var key in keys)
                {
                    if (JsonValues.HasValue(meta.Get(key)))
                    {
                        return JsonValues.Text(meta.Get(key));
                    }
                }
            }
            return string.Empty;
        }

        var first = Pick("firstName", "first_name");
        var last = Pick("lastName", "last_name", "surname");
        var common = Pick("commonName", "commonname", "knownAs", "commonName");
        if (common.Length == 0)
        {
            common = Pick("name");
        }
        if (common.Length == 0)
        {
            var joined = (first + " " + last).Trim();
            common = joined.Length > 0 ? joined : last.Length > 0 ? last : first;
        }
        if (last.Length == 0)
        {
            last = common;
        }
        var knownAs = Pick("knownAs");
        return new JsonObject
        {
            ["firstName"] = first,
            ["lastName"] = last,
            ["commonName"] = common,
            ["commonname"] = common,
            ["knownAs"] = knownAs.Length > 0 ? knownAs : common,
            ["name"] = common,
            ["playerName"] = common,
        };
    }
}
