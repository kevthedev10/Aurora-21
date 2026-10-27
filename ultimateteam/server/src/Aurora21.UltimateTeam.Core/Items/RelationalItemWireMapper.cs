using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Items;

/// <summary>
/// The scalars <see cref="RelationalItemWireMapper.Metadata"/> would otherwise be built to supply.
/// No itemType string is carried: the presentation view's only question about type is whether a row
/// is the club's manager, which the family answers. <paramref name="Rating"/> is the player rating,
/// and zero for a consumable, customisation or pick row, whose rating only the rendered document
/// carries — the view orders managers by it and nothing else reads it, and managers are summarised
/// the long way for exactly that reason.
/// </summary>
internal readonly record struct MetadataScalars(long Id, string Pile, string State, long Rating);

/// <summary>Definitions/rules + typed owned state produce the existing wire document.</summary>
public sealed class RelationalItemWireMapper
{
    private static readonly HashSet<string> TypedFields = new(new[] { "id", "itemId", "timestamp", "pile", "itemState", "untradeable", "owners", "lastSalePrice",
        "contract", "contracts", "fitness", "injuryType", "injuryGames", "suspension", "loans", "morale", "training", "preferredPosition",
        "playStyle", "assists", "lifetimeAssists", "loyaltyBonus", "stackCount", "amount", "untradeableCount", "leagueId", "islocked",
        "minRating", "choiceCount", "sourcePackId", "duplicate", "isDuplicate", "statsList", "lifetimeStats" });
    internal GameContext Game => _game;
    private readonly GameContext _game;
    private readonly ItemFactory _factory;
    // The live catalogue revision (the one the unit of work pinned), so a content reload reaches this bound mapper.
    private RelationalCatalogSnapshot CatalogRevision => _game.Catalog;
    private FrozenDictionary<DefinitionId, CustomisationDefinitionRecord> CustomisationDefinitions => CatalogRevision.CustomisationsById;
    private FrozenDictionary<DefinitionId, PlayerCardDefinitionRecord> PlayerDefinitions => CatalogRevision.PlayersById;
    private FrozenDictionary<DefinitionId, ConsumableDefinitionRecord> ConsumableDefinitions => CatalogRevision.ConsumablesById;
    public RelationalItemWireMapper(GameContext game)
    {
        _game = game; _factory = new(game);
    }

    // Scalar fields used by paging/counts. Never build player stat/attribute arrays here.
    /// <summary>
    /// The scalars a presentation row needs, read straight from the header and catalog instead of out
    /// of a rendered summary. Returns null when they cannot be known without building the document,
    /// and the caller must fall back to <see cref="Metadata"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Metadata"/> renders a whole wire document for a manager, consumable or customisation
    /// item and then reads five scalars back out of it. On the hub path that document has no other
    /// reader, and the pass was 36% of a hub response's allocation, so the rows that can answer
    /// without it do.
    /// </remarks>
    internal MetadataScalars? SummaryScalars(RelationalItemHeader h, string? payload, string? representation)
    {
        // A stored extension can add or overwrite any field, including these five, so those rows are
        // still summarised the long way and the scalars read back from the result.
        if (payload is not null || representation is not null) return null;
        // Only the rendered manager document carries the rating ManagerRow orders by, and a club has
        // very few managers, so this stays on the document path rather than duplicating the lookup.
        if (h.FamilyId == ManagerFamily) return null;
        // Every other family: Metadata sets id/pile/itemState straight from the header, and only a
        // player's summary carries a rating that anything reads.
        var rating = 0L;
        if (h.FamilyId == PlayerFamily)
        {
            var meta = _game.Players.Find(CatalogRevision.DefinitionsById[h.DefinitionId].NativeResourceId?.Value ?? 0);
            rating = meta?.IntKeep("rating", 0) ?? 0;
        }
        return new MetadataScalars(h.NativeItemId.Value, h.Pile ?? string.Empty, h.ItemState ?? string.Empty, rating);
    }

    private const short PlayerFamily = 1;
    private const short ManagerFamily = 2;

    internal JsonObject Metadata(RelationalItemHeader h, string? position, long? style, long loans,
        long? managerLeague, long pickRating, long pickPreview, string? payload, string? representation)
    {
        var d = CatalogRevision.DefinitionsById[h.DefinitionId];
        var resource = d.NativeResourceId?.Value ?? 0;
        JsonObject result;
        if (h.FamilyId == 1)
        {
            var meta = _game.Players.Find(resource);
            result = new JsonObject { ["itemType"] = "player", ["resourceId"] = resource };
            foreach (var key in new[] { "assetId", "rating", "rareflag", "leagueId", "teamid", "nation", "preferredPosition", "cardsubtypeid" })
                result[key] = meta?.Get(key)?.DeepClone();
            result["assetId"] = meta?.IntKeep("assetId", resource) ?? resource;
            result["rating"] = meta?.IntKeep("rating", 0) ?? 0;
            result["rareflag"] = meta?.IntKeep("rareflag", _game.Config.RareFlag) ?? _game.Config.RareFlag;
            result["preferredPosition"] = position ?? meta?.Text("preferredPosition") ?? "";
            result["playStyle"] = style ?? 0;
        }
        else if (h.FamilyId == 2) result = Render(new(h, Manager: new(h.Id, 7, managerLeague)));
        else if (h.FamilyId == 3) result = Render(new(h, Consumable: new(h.Id, 1, 0)));
        else if (h.FamilyId == 4) result = Render(new(h, Customisation: new(h.Id, null)));
        else result = new JsonObject { ["itemType"] = "misc", ["cardsubtypeid"] = ItemFactory.PlayerPickSubType,
            ["rating"] = pickRating, ["rareflag"] = 1, ["resourceId"] = pickPreview, ["assetId"] = pickPreview, ["discardValue"] = 0 };
        result["id"] = h.NativeItemId.Value; result["timestamp"] = h.Timestamp;
        result["pile"] = h.Pile; result["itemState"] = h.ItemState; result["untradeable"] = h.Untradeable;
        ApplyExtension(result, payload, representation);
        ApplyQuickSell(result, h, resource, loans);
        return result;
    }

    private static void ApplyExtension(JsonObject result, string? payload, string? wire)
    {
        foreach (var (key, value) in payload is null ? new JsonObject() : JsonNode.Parse(payload) as JsonObject ?? new())
            if (!result.ContainsKey(key)) result[key] = value?.DeepClone();
        var representation = wire is null ? null : JsonNode.Parse(wire) as JsonObject;
        foreach (var (key, value) in representation?.Obj("overrides") ?? new()) result[key] = value?.DeepClone();
        foreach (var key in representation?.Arr("stringNumbers")?.Select(v => v!.GetValue<string>()) ?? Array.Empty<string>())
            if (JsonValues.TryInt(result.Get(key), out var value)) result[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var key in representation?.Arr("nulls")?.Select(v => v!.GetValue<string>()) ?? Array.Empty<string>()) result[key] = null;
        foreach (var key in representation?.Arr("missing")?.Select(v => v!.GetValue<string>()) ?? Array.Empty<string>()) result.Remove(key);
    }

    public JsonObject Render(RelationalItemAggregate item)
    {
        var d = CatalogRevision.DefinitionsById[item.Header.DefinitionId];
        if (d.GameYear != 2021) throw new RuntimeSetupException("This item wire profile is FIFA 21 only.");
        var h = item.Header;
        var resource = d.NativeResourceId?.Value ?? 0;
        JsonObject result;
        if (item.Player is { } p)
        {
            // The stored stats go straight into the card rather than replacing freshly built empty rows.
            result = _factory.BuildPlayer(resource, h.NativeItemId.Value, h.Pile ?? "club", p.LoanGames,
                Stats(item, "current"), Stats(item, "lifetime"));
            result["contract"] = p.Contracts; result["contracts"] = p.Contracts;
            result["fitness"] = p.Fitness; result["injuryType"] = p.InjuryType; result["injuryGames"] = p.InjuryGames;
            result["suspension"] = p.Suspension; result["morale"] = p.Morale; result["training"] = p.Training;
            result["assists"] = p.Assists; result["lifetimeAssists"] = p.LifetimeAssists;
            if (p.PositionOverride is not null) result["preferredPosition"] = p.PositionOverride;
            result["playStyle"] = p.NativePlayStyle ?? 0;
            if (p.StyleDefinitionId is { } style) result["playStyle"] = ConsumableDefinitions[style].NativeEffectId;
            result["loyaltyBonus"] = p.LoyaltyBonus;
            if (p.DuplicateAcknowledged) { result["duplicate"] = true; result["isDuplicate"] = true; }
        }
        else if (item.Manager is { } m)
        {
            result = _factory.BuildManager(resource, h.NativeItemId.Value, h.Pile ?? "club");
            result["contract"] = m.Contracts; result["contracts"] = m.Contracts;
            result["leagueId"] = m.LeagueId is { } league
                ? CatalogRevision.Leagues.Single(l => l.LeagueId == league).NativeLeagueId : 0;
        }
        else if (item.Consumable is { } stack)
        {
            result = _factory.BuildConsumable(resource, h.NativeItemId.Value, h.Pile ?? "club", stack.Quantity);
            // A manager league card's amount is its league (FIFA reads the league from it), not the stack size.
            if (_game.Cards.ManagerLeagueCard(resource) is null) result["amount"] = stack.Quantity;
            result["stackCount"] = stack.Quantity;
            result["untradeableCount"] = stack.UntradeableQuantity;
        }
        else if (item.Customisation is { } custom)
        {
            var c = CustomisationDefinitions[d.DefinitionId];
            if (c.CustomisationKind == "kit")
                result = ItemFactory.KitItem(h.NativeItemId.Value, h.Timestamp, resource, h.ItemState ?? "free", h.Pile ?? "club",
                    c.NativeTeamId ?? 0, c.NativeKitType ?? -1, 2 + (c.NativeKitType ?? -1), c.Name);
            else
                result = _game.Rules.Club.DefaultActiveItemsTemplate.RenderArray(_game.Rules.Club.VirtualItemIdArgs(new TemplateArgs {
                    ["timestamp"] = h.Timestamp, ["stadiumId"] = _game.Rules.Club.Venues.MatchVenue(d.NativeAssetId), // an owned default card (asset 261) plays in the FUT Stadium
                    ["capacity"] = _game.Rules.Club.DefaultStadiumCapacity }, h.ClubId))
                    .OfType<JsonObject>().SingleOrDefault(i => i.IntKeep("resourceId") == resource)?.Copy()
                    // Owned stadium cards, badges and balls (migration 053) have their own card shape.
                    ?? ItemFactory.ClubCosmeticItem(c.CustomisationKind, h.NativeItemId.Value, h.Timestamp, resource, d.NativeAssetId ?? 0,
                        h.ItemState ?? "free", h.Pile ?? "club", c.Name, c.Rating ?? 0, c.NativeRareflag ?? 0,
                        _game.Stadiums.Find(d.NativeAssetId ?? 0)?.Capacity ?? _game.Rules.Club.DefaultStadiumCapacity)
                    ?? throw new RuntimeSetupException("No verified customisation wire template for this definition.");
            result["id"] = h.NativeItemId.Value;
            if (custom.IsLockedOverride is { } locked) result["islocked"] = locked ? 1 : 0;
        }
        else if (item.Pick is { } pick)
        {
            var preview = pick.PreviewDefinitionId is { } previewId ? CatalogRevision.DefinitionsById[previewId].NativeResourceId?.Value ?? 0 : 0;
            var rules = _game.Rules.Cards.PlayerPickToken;
            result = TemplateLibrary.Core.Object("items/player-pick-token", new TemplateArgs {
                ["id"] = h.NativeItemId.Value, ["timestamp"] = h.Timestamp, ["previewResourceId"] = preview,
                ["subType"] = ItemFactory.PlayerPickSubType, ["pile"] = h.Pile ?? "unassigned",
                ["minRating"] = pick.MinRating, ["choiceCount"] = pick.ChoiceCount, ["sourcePackId"] = pick.SourcePackId,
                ["name"] = pick.Label ?? rules.NameFor(pick.ChoiceCount, pick.MinRating), ["description"] = rules.DescriptionFor(pick.ChoiceCount, pick.MinRating) });
        }
        else throw new RuntimeSetupException("Item has no family state.");
        // Kits already use id in their response template; no per-item omission marker is needed.
        if (item.Customisation is not null && CustomisationDefinitions[d.DefinitionId].CustomisationKind != "kit")
            result["itemId"] = h.NativeItemId.Value;
        result["timestamp"] = h.Timestamp; result["pile"] = h.Pile; result["itemState"] = h.ItemState;
        result["untradeable"] = h.Untradeable; result["owners"] = h.Owners; result["lastSalePrice"] = h.LastSalePrice;
        // A club item card is one item: its untradeable count follows its flag (the templates' 1 is the untradeable default).
        if (item.Customisation is not null && result.ContainsKey("untradeableCount")) result["untradeableCount"] = h.Untradeable ? 1 : 0;
        if (item.Extension is { } extension)
        {
            var unknown = JsonNode.Parse(extension.Payload.Text) as JsonObject ?? new();
            foreach (var (key, value) in unknown) {
                if (result.ContainsKey(key)) throw new RuntimeSetupException($"Extension shadows known item field {key}.");
                result[key] = value?.DeepClone();
            }
            var representation = extension.WireRepresentation is { } wire ? JsonNode.Parse(wire.Text) as JsonObject : null;
            foreach (var (key, value) in representation?.Obj("overrides") ?? new()) {
                if (TypedFields.Contains(key)) throw new RuntimeSetupException($"Representation duplicates typed item field {key}.");
                result[key] = value?.DeepClone();
            }
            foreach (var key in representation?.Arr("stringNumbers")?.Select(v => v!.GetValue<string>()) ?? Array.Empty<string>())
                if (JsonValues.TryInt(result.Get(key), out var value)) result[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var key in representation?.Arr("nulls")?.Select(v => v!.GetValue<string>()) ?? Array.Empty<string>()) result[key] = null;
            foreach (var key in representation?.Arr("missing")?.Select(v => v!.GetValue<string>()) ?? Array.Empty<string>()) result.Remove(key);
        }
        // A Pro card shares its base player's asset, so FIFA would take that player's stars and traits from its own table:
        // every stored Pro card carries its own (NativeItems.PackItem does the same for freshly built ones).
        if (h.FamilyId == PlayerFamily && _game.Cards.IsAquaPro(resource)
            && _game.Cards.NativeCardFields(resource, result.Int("assetId")) is { } native)
        {
            result.Update(native);
        }
        ApplyQuickSell(result, h, resource, item.Player?.LoanGames ?? 0);
        return result;
    }

    /// <summary>
    /// The quick-sell value the item shows, the one quick sell pays: a player card's from its definition, anything else's
    /// from its card, then the item's reward rules (rules/cards.json quickSell). Set after any stored extension, so an old
    /// stored value never shows a different number.
    /// </summary>
    private void ApplyQuickSell(JsonObject result, RelationalItemHeader h, long resource, long loans)
    {
        if (h.FamilyId == PlayerFamily)
        {
            result["discardValue"] = ItemFactory.IsExpiredLoan(result) ? 0 : _game.Cards.QuickSellValue(resource, loans, h.Untradeable, h.RewardSource);
        }
        else if (result.ContainsKey("discardValue") && JsonValues.TryInt(result.Get("discardValue"), out var value))
        {
            result["discardValue"] = _game.Rules.Cards.QuickSell.ForItem(value, h.Untradeable, h.RewardSource);
        }
    }

    internal JsonObject RenderChoice(PlayerPickChoiceRecord choice, long token)
    {
        var resource = CatalogRevision.DefinitionsById[choice.DefinitionId].NativeResourceId?.Value
            ?? throw new RuntimeSetupException("Choice definition has no native resource.");
        var wire = _factory.BuildPlayer(resource, choice.ReservedWireItemId.Value, "unassigned");
        wire["timestamp"] = choice.Timestamp;
        wire.Update(TemplateLibrary.Core.Object("player-picks/choice-fields", new TemplateArgs {
            ["duplicate"] = choice.IsDuplicate, ["tokenId"] = token, ["index"] = choice.Ordinal }));
        return wire;
    }

    /// <param name="existingRewardSource">The stored item's reward source, kept unless the document sets <see cref="RewardSources.ItemKey"/>.</param>
    public RelationalItemAggregate Capture(long club, JsonObject wire, ItemId internalId = default,
        PlayerPickItemState? existingPick = null, string? existingRewardSource = null)
    {
        var pickToken = ItemFactory.IsPlayerPickToken(wire);
        short family = (short)(pickToken ? 5 : wire.Text("itemType") switch {
            "player" => 1, "manager" => 2, "training" or "development" or "consumable" => 3,
            "kit" or "stadium" or "badge" or "ball" or "custom" => 4,
            _ => throw new RuntimeSetupException("Unknown owned item family.") });
        var year = checked((int)wire.IntKeep("resourceGameYear", 2021));
        if (year != 2021) throw new RuntimeSetupException("This item wire profile is FIFA 21 only.");
        var resource = new NativeResourceId(wire.IntKeep("resourceId"));
        var definition = pickToken
            ? CatalogRevision.Definitions.SingleOrDefault(d => d.FamilyId == 5 && d.GameYear == year && d.NativeSubtype == ItemFactory.PlayerPickSubType)
            : CatalogRevision.ResolveDefinition(year, family, resource);
        if (definition is null) throw new RuntimeSetupException("No catalog definition for owned item; token preview is not a token product ID.");
        long I(string key, long fallback = 0) => wire.IntKeep(key, fallback);
        string? Text(string key) => wire.Get(key) is null ? null : wire.Text(key);
        long Contract()
        {
            var contract = I("contract", I("contracts", 7));
            if (wire.ContainsKey("contract") && wire.ContainsKey("contracts") && contract != I("contracts"))
                throw new RuntimeSetupException("Conflicting contract aliases require an explicit audit.");
            return contract;
        }
        // Enforce the restriction even if a caller saves a Developer card with a false wire flag.
        if (family == 1 && _game.Cards.IsDeveloperCard(resource.Value)) wire["untradeable"] = true;
        var h = new RelationalItemHeader(internalId, club, new NativeItemId(I("id")), definition.DefinitionId, family,
            I("timestamp", _game.NowSeconds), Text("pile"), Text("itemState"), wire.Bool("untradeable"), I("owners", 1), I("lastSalePrice"),
            wire.ContainsKey(RewardSources.ItemKey) ? (wire.Text(RewardSources.ItemKey) is { Length: > 0 } source ? source : null) : existingRewardSource);
        if(family==4 && wire.Get("itemId") is not null && I("itemId")!=h.NativeItemId.Value)throw new RuntimeSetupException("Conflicting customisation native item aliases require an audit.");
        var aggregate = new RelationalItemAggregate(h);
        if (family == 1)
        {
            var card = PlayerDefinitions[definition.DefinitionId];
            var position = wire.Str("preferredPosition");
            var styleCode = I("playStyle");
            var style = CatalogRevision.Consumables.FirstOrDefault(c => c.ConsumableKind == "chemistry_style" && c.NativeEffectId == styleCode);
            aggregate = aggregate with { Player = new(internalId, Contract(), I("fitness", 99), Text("injuryType"), I("injuryGames"),
                I("suspension"), I("loans"), I("morale", 50), I("training"), position == card.Position ? null : position,
                style?.DefinitionId, styleCode, I("assists"), I("lifetimeAssists"), wire.Get("loyaltyBonus") is null ? null : I("loyaltyBonus"),
                wire.Bool("duplicate") || wire.Bool("isDuplicate")),
                Stats = ReadStats(wire.Get("statsList"), "current").Concat(ReadStats(wire.Get("lifetimeStats"), "lifetime")).ToArray() };
        }
        else if (family == 2)
        {
            var defaultLeague = CatalogRevision.Managers.Single(m => m.DefinitionId == definition.DefinitionId).NativeLeagueId ?? 0;
            var nativeLeague = I("leagueId", defaultLeague);
            var league = nativeLeague == 0 ? null : CatalogRevision.Leagues.SingleOrDefault(l => l.NativeLeagueId == nativeLeague);
            if (nativeLeague != 0 && league is null) throw new RuntimeSetupException($"Unknown manager league code {nativeLeague}.");
            aggregate = aggregate with { Manager = new(internalId, Contract(), league?.LeagueId) };
        }
        else if (family == 3)
        {
            var quantity = I("stackCount", I("amount", 1));
            if (wire.ContainsKey("amount") && quantity != I("amount") && _game.Cards.ManagerLeagueCard(resource.Value) is null) throw new RuntimeSetupException("Conflicting quantity aliases require an explicit audit.");
            aggregate = aggregate with { Consumable = new(internalId, quantity, I("untradeableCount")) };
        }
        else if (family == 4) aggregate = aggregate with { Customisation = new(internalId, wire.ContainsKey("islocked") ? I("islocked") != 0 : null) };
        else
        {
            // A token named by its pick group keeps that name; the rules name is the default.
            var (minRating, choiceCount) = (I("minRating", I("rating")), I("choiceCount"));
            var label = wire.Text("name") is { Length: > 0 } name && name != _game.Rules.Cards.PlayerPickToken.NameFor(choiceCount, minRating) ? name : null;
            aggregate = aggregate with { Pick = existingPick ?? new(internalId,
                CatalogRevision.ResolveDefinition(year, 1, resource)?.DefinitionId, minRating, choiceCount, I("sourcePackId"), "unrevealed", null, Label: label) };
        }
        var canonical = Render(aggregate);
        // Baseline constants/derived overrides are compatibility data, not duplicate mutable state.
        var unknown = new JsonObject(); var overrides = new JsonObject(); var missing = new JsonArray(); var nulls = new JsonArray(); var strings = new JsonArray();
        foreach (var key in canonical.Select(p => p.Key).Where(k => !wire.ContainsKey(k) && !(k == "discardValue" && family == 1))) missing.Add(key);
        foreach (var (key, value) in wire)
        {
            // The validated kit itemId alias duplicates the typed native identity.
            if (key == "itemId" && family == 4 && CustomisationDefinitions[definition.DefinitionId].CustomisationKind == "kit") continue;
            // The reward source is typed state, and a player's quick-sell value is derived (ApplyQuickSell), never stored.
            if (key == RewardSources.ItemKey || (key == "discardValue" && family == 1)) continue;
            // A player is not a stack and its duplicate markers default to false (rendered only when set), so the
            // untradeableCount and false markers a player-pick choice carries are not stored as an extension.
            if (family == 1 && !canonical.ContainsKey(key) && (key == "untradeableCount"
                || (key is "duplicate" or "isDuplicate" && value is JsonValue flag && flag.TryGetValue<bool>(out var marked) && !marked))) continue;
            if (!canonical.ContainsKey(key)) { unknown[key] = value?.DeepClone(); continue; }
            if (JsonNode.DeepEquals(value, canonical[key])) continue;
            if (TypedFields.Contains(key)) {
                if (value is null) nulls.Add(key);
                else if (value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String && JsonValues.TryInt(value, out _)) strings.Add(key);
                else if (!JsonNode.DeepEquals(value, canonical[key])) throw new RuntimeSetupException($"Unsupported representation of typed field {key}.");
            } else overrides[key] = value?.DeepClone();
        }
        var rep = new JsonObject { ["overrides"] = overrides, ["missing"] = missing, ["nulls"] = nulls, ["stringNumbers"] = strings };
        if (unknown.Count != 0 || overrides.Count != 0 || missing.Count != 0 || nulls.Count != 0 || strings.Count != 0)
            aggregate = aggregate with { Extension = new(internalId, 1, new CatalogJson(unknown.ToJsonString()), new CatalogJson(rep.ToJsonString())) };
        return aggregate;
    }

    /// <summary>
    /// Whether <see cref="RenderNativePlayer"/> can serve this item. Extensions rewrite the rendered
    /// document, and special cards take their names from it, so both keep the render-then-project path.
    /// </summary>
    public bool CanRenderNativePlayer(RelationalItemAggregate item)
    {
        if (item.Player is null || item.Extension is not null) return false;
        var definition = CatalogRevision.DefinitionsById[item.Header.DefinitionId];
        if (definition.GameYear != 2021) return false;
        var meta = _game.Players.Find(definition.NativeResourceId?.Value ?? 0);
        var rareflag = meta?.IntKeep("rareflag", _game.Config.RareFlag) ?? _game.Config.RareFlag;
        return rareflag is 0 or 1;
    }

    /// <summary>
    /// The native squad card built straight from typed state, instead of rendering the full wire
    /// document and projecting it down. Every value below reproduces what
    /// <c>NativeItems.PlayerItem(Render(item))</c> reads out of that document, including the lenient
    /// fallbacks it relies on; the differential test holds the two byte-identical.
    /// </summary>
    public JsonObject RenderNativePlayer(RelationalItemAggregate item)
    {
        var h = item.Header;
        var p = item.Player ?? throw new InvalidOperationException("Native player render needs player state.");
        var definition = CatalogRevision.DefinitionsById[h.DefinitionId];
        var resource = definition.NativeResourceId?.Value ?? 0;
        var meta = _game.Players.Find(resource);
        long Keep(string key, long fallback) => meta?.IntKeep(key, fallback) ?? fallback;
        long MetaInt(string key) => meta?.Int(key) ?? 0;
        string MetaStr(string key) => meta?.Str(key) ?? string.Empty;

        var loans = Math.Max(0, p.LoanGames);
        // GetOr falls back only for a missing key, and the rendered document always sets these, so a
        // stored zero reaches the client as zero. Only the Int/IntOr readers below treat zero as empty.
        var assetId = Keep("assetId", resource);
        var rareflag = Keep("rareflag", _game.Config.RareFlag);
        var position = p.PositionOverride ?? meta?.Text("preferredPosition") ?? string.Empty;
        // A style with no native effect renders as JSON null, which the play-style rule reads as zero.
        var playStyle = p.StyleDefinitionId is { } style ? ConsumableDefinitions[style].NativeEffectId ?? 0 : p.NativePlayStyle ?? 0;

        var output = new JsonObject
        {
            ["assetId"] = assetId,
            ["attributeList"] = _game.Cards.AttributeList(resource),
            ["cardsubtypeid"] = MetaInt("cardsubtypeid"),
            ["contract"] = loans > 0 ? loans : _game.Rules.Cards.PermanentContracts,
            ["discardValue"] = _game.Cards.QuickSellValue(resource, loans, h.Untradeable, h.RewardSource),
            ["fitness"] = p.Fitness,
            ["injuryGames"] = p.InjuryGames,
            ["injuryType"] = 0,
            ["itemState"] = string.IsNullOrEmpty(h.ItemState) ? "free" : h.ItemState,
            ["itemType"] = "player",
            ["lastSalePrice"] = h.LastSalePrice,
            ["leagueId"] = Keep("leagueId", 0),
            ["nation"] = Keep("nation", 0),
            ["loans"] = loans,
            ["morale"] = p.Morale,
            ["owners"] = h.Owners != 0 ? h.Owners : 1,
            ["preferredPosition"] = position,
            ["preferredFoot"] = MetaStr("preferredFoot"),
            ["weakFoot"] = MetaInt("weakFoot"),
            ["skillMoves"] = MetaInt("skillMoves"),
            ["workRateAttack"] = MetaStr("workRateAttack"),
            ["workRateDefense"] = MetaStr("workRateDefense"),
            ["rareflag"] = rareflag,
            ["rating"] = Keep("rating", 0),
            ["resourceId"] = resource,
            ["suspension"] = p.Suspension,
            ["teamid"] = Keep("teamid", 0),
            ["timestamp"] = h.Timestamp,
            ["training"] = p.Training,
            ["untradeable"] = h.Untradeable,
            ["playStyle"] = _game.Cards.EffectivePlayStyle(playStyle, position.ToUpperInvariant() == "GK"),
            ["posMods"] = new JsonArray(),
            ["id"] = h.NativeItemId.Value,
            ["statsList"] = Stats(item, "current"),
            ["lifetimeStats"] = Stats(item, "lifetime"),
            ["assists"] = p.Assists,
            ["lifetimeAssists"] = p.LifetimeAssists,
        };
        if (p.LoyaltyBonus is { } loyaltyBonus)
        {
            output["loyaltyBonus"] = loyaltyBonus;
        }
        if (_game.Cards.RarityGroups(rareflag) is { Count: > 0 } groups)
        {
            output["groups"] = groups;
        }
        // FIFA reads stars, work rates and traits only from its own lower-case fields (rules/cards.json).
        if (_game.Cards.NativeCardFields(resource, assetId) is { } native)
        {
            output.Update(native);
        }
        return output;
    }

    private JsonArray Stats(RelationalItemAggregate item, string scope) => _game.Cards.StatRowsFrom(
        (item.Stats ?? Array.Empty<PlayerStatValue>()).Where(s => s.Scope == scope).Select(s => (s.StatIndex, s.Value)));
    private IEnumerable<PlayerStatValue> ReadStats(JsonNode? rows, string scope) => _game.Cards.StatRows(rows).OfType<JsonObject>()
        .Select(row => new PlayerStatValue(0, default, scope, checked((int)row.Int("index")), row.Int("value")));
}
