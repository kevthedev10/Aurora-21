using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Items;

/// <summary>Creates FIFA 21's native owned-item documents from catalog definitions.</summary>
public sealed class ItemFactory
{
    /// <summary>The native card subtype of the 1-of-N player pick unlockable.</summary>
    public const long PlayerPickSubType = 237;

    private readonly GameContext _game;

    internal GameContext Game => _game;

    public ItemFactory(GameContext game) => _game = game;


    public JsonObject BuildPlayer(long resourceId, long itemId, string pile, long loanGames = 0) =>
        BuildPlayer(resourceId, itemId, pile, loanGames, null, null);

    /// <param name="statsList">Current-season stat rows the caller already has; null renders empty rows.</param>
    /// <param name="lifetimeStats">Lifetime stat rows the caller already has; null renders empty rows.</param>
    internal JsonObject BuildPlayer(long resourceId, long itemId, string pile, long loanGames, JsonArray? statsList, JsonArray? lifetimeStats)
    {
        var meta = _game.Players.Find(resourceId);
        loanGames = Math.Max(0, loanGames);
        var onLoan = loanGames != 0;
        long Keep(string key, long fallback) => meta?.IntKeep(key, fallback) ?? fallback;
        long Int(string key) => meta?.Int(key) ?? 0;
        string Str(string key) => meta?.Str(key) ?? string.Empty;
        var (marketMin, marketMax) = _game.Cards.MarketPriceBounds(resourceId);
        var untradeable = onLoan || _game.Cards.IsDeveloperCard(resourceId) || (meta?.Bool("untradeable") ?? false) || _game.Cards.IsAlwaysUntradeable(resourceId);

        // Every argument below is built for this card alone, so the render may keep the nodes.
        var item = TemplateLibrary.Core.Object("items/player", new TemplateArgs
        {
            AdoptNodes = true,
            ["id"] = itemId,
            ["timestamp"] = _game.NowSeconds,
            ["resourceId"] = resourceId,
            ["assetId"] = Keep("assetId", resourceId),
            ["rating"] = Keep("rating", 0),
            ["rareflag"] = Keep("rareflag", _game.Config.RareFlag),
            ["leagueId"] = Keep("leagueId", 0),
            ["contract"] = onLoan ? 0 : _game.Rules.Cards.PermanentContracts,
            ["preferredPosition"] = meta?.Text("preferredPosition") ?? string.Empty,
            ["preferredFoot"] = Str("preferredFoot"),
            ["weakFoot"] = Int("weakFoot"),
            ["skillMoves"] = Int("skillMoves"),
            ["workRateAttack"] = Str("workRateAttack"),
            ["workRateDefense"] = Str("workRateDefense"),
            ["discardValue"] = _game.Cards.QuickSellValue(resourceId, loanGames, untradeable, rewardSource: null),
            ["cardSubType"] = Int("cardsubtypeid"),
            ["untradeable"] = untradeable,
            ["statsList"] = statsList ?? _game.Cards.StatRows(null),
            ["lifetimeStats"] = lifetimeStats ?? _game.Cards.StatRows(null),
            ["attributeList"] = _game.Cards.AttributeList(resourceId),
            ["teamId"] = Keep("teamid", 0),
            ["loans"] = loanGames,
            ["pile"] = pile,
            ["nation"] = Keep("nation", 0),
            ["marketMinPrice"] = marketMin,
            ["marketMaxPrice"] = marketMax,
            ["firstName"] = Str("firstName"),
            ["lastName"] = Str("lastName"),
            ["commonName"] = meta?.Str("commonName", Str("commonname")) ?? string.Empty,
        });
        // The SBC rare requirement checks this group, using explicit card-type membership from creation.
        if (_game.Cards.RarityGroups(Keep("rareflag", _game.Config.RareFlag)) is { Count: > 0 } groups)
        {
            item["groups"] = groups;
        }
        return item;
    }

    /// <summary>
    /// Makes an item untradeable, with the quick-sell value that follows (rules/cards.json quickSell: an untradeable card sells
    /// for at most rewardUntradeableCap), so the document and the value quick sell pays agree. The caller still saves it.
    /// </summary>
    public JsonObject Restrict(JsonObject item)
    {
        item["untradeable"] = true;
        RestrictItemCount(item);
        var source = item.Text(RewardSources.ItemKey) is { Length: > 0 } named ? named : null;
        if (item.Text("itemType") == "player")
        {
            item["discardValue"] = _game.Cards.QuickSellValue(item.IntKeep("resourceId"), item.Int("loans"), untradeable: true, source);
        }
        else if (JsonValues.TryInt(item.Get("discardValue"), out var value))
        {
            item["discardValue"] = _game.Rules.Cards.QuickSell.ForItem(value, untradeable: true, source);
        }
        return item;
    }

    /// <summary>
    /// Marks a granted item with its grant source (rules/tradeability.json; stored as its reward source) and makes it
    /// untradeable when the grant is. Its quick-sell value follows both. Never lifts a restriction the item already has; the
    /// caller still saves the item.
    /// </summary>
    public JsonObject MarkReward(JsonObject item, string source, bool untradeable)
    {
        item[RewardSources.ItemKey] = source;
        if (untradeable || item.Text("itemType") == "player" && _game.Cards.IsDeveloperCard(item.IntKeep("resourceId")))
        {
            item["untradeable"] = true;
            RestrictItemCount(item);
        }
        var restricted = item.Bool("untradeable");
        if (item.Text("itemType") == "player")
        {
            item["discardValue"] = _game.Cards.QuickSellValue(item.IntKeep("resourceId"), item.Int("loans"), restricted, source);
        }
        else if (JsonValues.TryInt(item.Get("discardValue"), out var value))
        {
            item["discardValue"] = _game.Rules.Cards.QuickSell.ForItem(value, restricted, source);
        }
        return item;
    }

    /// <summary>
    /// A consumable stack marked untradeable carries the untradeable count FIFA reads per card, so the whole stored stack
    /// (quantity travels as stackCount) shows and quick-sells as untradeable, not just its header flag. Club item cards
    /// carry a count of one when their template includes it.
    /// </summary>
    private static void RestrictItemCount(JsonObject item)
    {
        if (item.Text("itemType") is "training" or "development" or "consumable")
        {
            item["untradeableCount"] = JsonValues.Int(item.GetOr("stackCount", item.GetOr("amount", 1)));
        }
        else if (IsClubItem(item) && item.ContainsKey("untradeableCount")) item["untradeableCount"] = 1;
    }

    /// <summary>
    /// The contract count FIFA is sent for a player card. FIFA gives a card only a loan flag and its contract count (CardsDLL
    /// 0xDE7C0: FUT_LOAN_PLAYER is loans &gt; 0, FUT_CONTRACTS the contract), so a loan's matches left must be its contract
    /// count; a loan sent with its stored 0 contracts showed 0 matches. Stored items keep their own contracts.
    /// </summary>
    public static long ShownContracts(long contracts, long loanGames) => loanGames > 0 ? loanGames : contracts;

    // Persisted extension marks a loan whose remaining-games counter has reached zero.
    // It must never become a permanent card merely because that counter is now empty.
    public static bool IsExpiredLoan(JsonObject item) => item.Bool("expiredLoan");

    public long PlayableContracts(JsonObject item) => IsExpiredLoan(item) ? 0
        : item.Int("loans") > 0 ? item.Int("loans") : _game.Rules.Cards.PermanentContracts;

    public async ValueTask<JsonObject> MakePlayerAsync(IUnitOfWork uow, long resourceId, string pile = "club", long loanGames = 0) =>
        await SaveAsync(uow, BuildPlayer(resourceId, await uow.NextItemIdAsync(), pile, loanGames));

    /// <summary>
    /// A native position-change (development) or chemistry style (training) consumable stack, or a manager league card
    /// (migration 071). Unknown definitions are refused: synthetic ids render as broken cards.
    /// </summary>
    public JsonObject BuildConsumable(long resourceId, long itemId, string pile = "club", long stackCount = 1)
    {
        var cards = _game.Cards;
        var quantity = Math.Max(1, stackCount);
        if (cards.ManagerLeagueCard(resourceId) is { } leagueCard)
        {
            // Tradeable as built; a grant marks it untradeable when its source is (rules/tradeability.json).
            return ManagerLeagueCardItem(leagueCard, itemId, _game.NowSeconds, "free", pile, quantity, untradeable: false, untradeableCount: 0,
                owners: 1, lastSalePrice: 0);
        }
        // Chemistry styles and position modifiers are tradeable as built (retail pack consumables); a grant marks them
        // untradeable when its source is (rules/tradeability.json), the same rule the manager league card follows.
        if (cards.ChemistryStyle(resourceId) is { } style)
        {
            var styles = cards.Rules.ChemistryStyles;
            var item = TemplateLibrary.Core.Object("items/chemistry-style", new TemplateArgs
            {
                ["id"] = itemId,
                ["timestamp"] = _game.NowSeconds,
                ["resourceId"] = resourceId,
                ["cardAssetId"] = styles.CardAssetId,
                ["rating"] = styles.Rating,
                ["playStyle"] = style.PlayStyle,
                ["pile"] = pile,
                ["discardValue"] = _game.Rules.Cards.QuickSell.ForItem(styles.DiscardValue, untradeable: false, rewardSource: null),
                ["stackCount"] = quantity,
                ["consumableType"] = styles.ConsumableType,
            });
            var rareflag = styles.RareStyles.Contains(style.PlayStyle) ? 1 : 0;
            item["rareflag"] = rareflag;
            item["rareFlag"] = rareflag;
            item["weightrare"] = rareflag;
            return item;
        }
        var modifier = cards.PositionModifier(resourceId) ?? throw new InvalidOperationException($"unknown FIFA 21 position modifier definition {resourceId}");
        var card = cards.Rules.PositionModifierCard;
        return TemplateLibrary.Core.Object("items/position-modifier", new TemplateArgs
        {
            ["id"] = itemId,
            ["timestamp"] = _game.NowSeconds,
            ["resourceId"] = resourceId,
            ["cardAssetId"] = card.CardAssetId,
            ["rating"] = modifier.Rating,
            ["subType"] = modifier.SubType,
            ["pile"] = pile,
            ["discardValue"] = _game.Rules.Cards.QuickSell.ForItem(card.DiscardValue, untradeable: false, rewardSource: null),
            ["stackCount"] = quantity,
            ["consumableType"] = card.ConsumableType,
            ["source"] = modifier.Source,
            ["target"] = modifier.Target,
        });
    }

    /// <summary>
    /// A manager league card stack (rules/cards.json managerLeagueCards): card asset 32, consumable type 5, the manager league
    /// subtype FIFA files it under (300 + the league's fcc_leagues row) and the league itself as leagueId and as amount. FIFA
    /// reads a manager league card's league from amount, a consumable's effect value (CardsDLL 0x321910 passes it to 0x31FCD0,
    /// which keeps it for category 10); the stack size is stackCount only.
    /// </summary>
    internal JsonObject ManagerLeagueCardItem(ManagerLeagueCard card, long itemId, long timestamp, string itemState, string pile, long stackCount,
        bool untradeable, long untradeableCount, long owners, long lastSalePrice)
    {
        var rules = _game.Rules.Cards.ManagerLeagueCards;
        return TemplateLibrary.Core.Object("items/manager-league-card", new TemplateArgs
        {
            ["id"] = itemId,
            ["timestamp"] = timestamp,
            ["resourceId"] = card.ResourceId,
            ["cardAssetId"] = rules.CardAssetId,
            ["rating"] = rules.Rating,
            ["itemState"] = itemState,
            ["league"] = card.LeagueId,
            ["subtype"] = rules.Subtype(card.LeagueId) ?? card.LeagueId,
            ["pile"] = pile,
            ["discardValue"] = rules.DiscardValue,
            ["lastSalePrice"] = lastSalePrice,
            ["owners"] = owners,
            ["untradeable"] = untradeable,
            ["stackCount"] = stackCount,
            ["untradeableCount"] = Math.Clamp(untradeableCount, 0, stackCount),
            ["consumableType"] = rules.ConsumableType,
        });
    }

    public async ValueTask<JsonObject> MakeConsumableAsync(IUnitOfWork uow, long resourceId, string pile = "club", long stackCount = 1) =>
        await SaveAsync(uow, BuildConsumable(resourceId, await uow.NextItemIdAsync(), pile, stackCount));

    /// <summary>Native manager item from a real cards_ng_db definition.</summary>
    public JsonObject BuildManager(long resourceId, long itemId, string pile)
    {
        var meta = _game.Managers.Find(resourceId) ?? throw new InvalidOperationException($"unknown FIFA 21 manager definition {resourceId}");
        var rules = _game.Rules.Cards;
        var rating = meta.Int("rating", rules.ManagerDefaultRating);
        var rareflag = meta.IntOr("rareflag", rating >= rules.ManagerRareFromRating ? 1 : 0);
        var item = TemplateLibrary.Core.Object("items/manager", new TemplateArgs
        {
            ["id"] = itemId,
            ["timestamp"] = _game.NowSeconds,
            ["resourceId"] = resourceId,
            ["cardDbId"] = meta.IntOr("carddbid", resourceId, resourceId),
            ["rating"] = rating,
            ["rareflag"] = rareflag,
            ["leagueId"] = meta.Int("leagueId"),
            ["nation"] = meta.Int("nation"),
            ["teamId"] = meta.Int("teamid"),
            ["negotiation"] = meta.Int("negotiation"),
            ["discardValue"] = rules.QuickSell.ManagerValue(_game.Rules.Packs.TierFor(rating), rating, rareflag != 0),
            ["pile"] = pile,
            ["firstName"] = meta.Str("firstName"),
            ["lastName"] = meta.Str("lastName"),
            ["commonName"] = meta.Str("commonname"),
        });
        item["contract"] = rules.PermanentContracts;
        item["contracts"] = rules.PermanentContracts;
        return item;
    }

    public async ValueTask<JsonObject> MakeManagerAsync(IUnitOfWork uow, long resourceId, string pile = "club") =>
        await SaveAsync(uow, BuildManager(resourceId, await uow.NextItemIdAsync(), pile));

    /// <summary>
    /// One item of the non-player drop table (a consumable, manager, kit, badge, ball or stadium card), built as mixed packs
    /// build it: with the drop's own rating and rarity, since the kit template's rating is a fixed gold one.
    /// </summary>
    public JsonObject BuildDrop(NonPlayerDrop drop, long itemId, string pile)
    {
        var item = drop.Kind switch
        {
            "manager" => BuildManager(drop.Resource, itemId, pile),
            "consumable" => BuildConsumable(drop.Resource, itemId, pile),
            "kit" => KitItem(itemId, _game.NowSeconds, drop.Resource, "free", pile, drop.Team, drop.KitType, 2 + drop.KitType, drop.Name),
            _ => ClubCosmeticItem(drop.Kind, itemId, _game.NowSeconds, drop.Resource, drop.Asset, "free", pile, drop.Name, drop.Rating, drop.Rareflag,
                    _game.Stadiums.Find(drop.Asset)?.Capacity ?? _game.Rules.Club.DefaultStadiumCapacity)
                ?? throw new InvalidOperationException($"unknown non-player item kind {drop.Kind}"),
        };
        item["rating"] = drop.Rating;
        item["rareflag"] = drop.Rareflag;
        if (item.ContainsKey("rareFlag")) item["rareFlag"] = drop.Rareflag;
        return item;
    }

    /// <summary>
    /// FIFA 21's unlockable 1-of-N player-pick misc item. The preview resource id must be a
    /// real qualifying card: the client resolves the token's art by resource id.
    /// </summary>
    public async ValueTask<JsonObject> MakePlayerPickAsync(IUnitOfWork uow, long minRating, long choiceCount = 4, string pile = "unassigned", long sourcePackId = 0,
        long previewResourceId = 0, bool promoOnly = false, IReadOnlyCollection<long>? rareflags = null, string? label = null,
        long maxRating = Cards.MaxRating, string? resourcePool = null, JsonObject? drawConfig = null)
    {
        var rules = _game.Rules.Cards.PlayerPickToken;
        minRating = Math.Clamp(minRating, 0, Cards.MaxRating);
        choiceCount = Math.Clamp(choiceCount, rules.MinChoices, rules.MaxChoices);
        var itemId = await uow.NextItemIdAsync();
        var item = TemplateLibrary.Core.Object("items/player-pick-token", new TemplateArgs
        {
            ["id"] = itemId,
            ["timestamp"] = _game.NowSeconds,
            ["previewResourceId"] = previewResourceId,
            ["subType"] = PlayerPickSubType,
            ["pile"] = pile,
            ["minRating"] = minRating,
            ["name"] = string.IsNullOrEmpty(label) ? rules.NameFor(choiceCount, minRating) : label,
            ["description"] = rules.DescriptionFor(choiceCount, minRating),
            ["choiceCount"] = choiceCount,
            ["sourcePackId"] = sourcePackId,
        });
        await uow.SaveItemAsync(item);
        await uow.SavePlayerPickAsync(itemId, new PlayerPickState(minRating, choiceCount, new JsonArray()));
        if (drawConfig is not null || promoOnly || rareflags is { Count: > 0 } || maxRating < Cards.MaxRating || !string.IsNullOrEmpty(resourcePool))
        {
            await uow.SetPlayerPickFiltersAsync(itemId, promoOnly, rareflags ?? Array.Empty<long>(), maxRating, resourcePool, drawConfig?.ToJsonString());
        }
        return item;
    }

    /// <summary>The item types of club items: kits, badges (custom), balls and stadiums.</summary>
    public static readonly string[] ClubItemTypes = ["kit", "custom", "ball", "stadium"];

    /// <summary>Whether the item is a kit, badge, ball or stadium card.</summary>
    public static bool IsClubItem(JsonObject? item) => item is not null && ClubItemTypes.Contains(item.Text("itemType").ToLowerInvariant());

    /// <summary>Lifts a club item card's template restriction (its untradeable flag and untradeable count of 1).</summary>
    public static JsonObject MakeTradeable(JsonObject item)
    {
        item["untradeable"] = false;
        if (item.ContainsKey("untradeableCount")) item["untradeableCount"] = 0;
        return item;
    }

    public static bool IsPlayerPickToken(JsonObject? item) =>
        item is not null
        && item.Text("itemType").ToLowerInvariant() == "misc"
        && JsonValues.Int(item.GetOr("cardsubtypeid", item.Get("subtype"))) == PlayerPickSubType;

    /// <summary>
    /// An owned stadium card, club badge or match ball (fcc_stadium, fcc_badgecards, fcc_balls; Python server ca228bc,
    /// ceb35f0, e2e098f), or null for another customisation kind.
    /// </summary>
    public static JsonObject? ClubCosmeticItem(string kind, long itemId, long timestamp, long resourceId, long assetId, string itemState,
        string pile, string name, long rating, long rareflag, long capacity)
    {
        var template = kind switch { "stadium" => "items/club-stadium", "custom" => "items/club-badge", "ball" => "items/club-ball", _ => null };
        if (template is null)
        {
            return null;
        }
        var args = new TemplateArgs
        {
            ["id"] = itemId, ["timestamp"] = timestamp, ["resourceId"] = resourceId, ["asset"] = assetId, ["state"] = itemState,
            ["pile"] = pile, ["name"] = name, ["rating"] = rating, ["rareflag"] = rareflag,
        };
        if (kind == "stadium")
        {
            args["capacity"] = capacity;
        }
        return TemplateLibrary.Core.Object(template, args);
    }

    /// <summary>An owned kit card (fcc_kitcards).</summary>
    /// <remarks>
    /// FIFA shows a kit's name through its localisation lookup, so a plain name rendered as "*RSC Anderlecht Home Kit";
    /// a team's kit carries the game's own key, TeamName_Abbr15_&lt;team&gt; (CardsDLL 0x1802971d9; Python server ca228bc).
    /// </remarks>
    public static JsonObject KitItem(long itemId, long timestamp, long resourceId, string itemState, string pile, long teamId, long kitType, long category, string name) =>
        TemplateLibrary.Core.Object("items/kit", new TemplateArgs
        {
            ["id"] = itemId,
            ["timestamp"] = timestamp,
            ["resourceId"] = resourceId,
            ["itemState"] = itemState,
            ["pile"] = pile,
            ["category"] = category,
            ["teamId"] = teamId,
            ["kitType"] = kitType,
            ["name"] = teamId > 0 ? $"TeamName_Abbr15_{teamId}" : name,
        });

    /// <summary>The kit definition's kitType, or -1 when it is missing or not a number.</summary>
    public static long KitType(JsonObject kit) => kit.Get("kitType") is { } node && JsonValues.TryInt(node, out var value) ? value : -1;

    private static async ValueTask<JsonObject> SaveAsync(IUnitOfWork uow, JsonObject item)
    {
        await uow.SaveItemAsync(item);
        return item;
    }
}
