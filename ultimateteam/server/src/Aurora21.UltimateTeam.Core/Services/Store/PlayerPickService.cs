using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>Native 1-of-N player picks: reveal a stable set of choices, then grant exactly one.</summary>
public sealed partial class PlayerPickService
{
    private const long DefaultChoiceCount = 4;

    /// <summary>The native picker reads these indexes before confirming; missing ones crash it.</summary>
    private static readonly string[] ChoiceIndexKeys = { "globalChoiceIndex", "localChoiceIndex", "choiceIndex" };

    /// <summary>Picker bookkeeping removed from the granted item.</summary>
    private static readonly string[] PickerKeys = ChoiceIndexKeys.Prepend("playerPickItemId").ToArray();

    private static readonly string[] TokenIdKeys = { "playerPickItemId", "playerPickId", "tokenId", "unlockableItemId", "itemId", "id" };

    // Every FIFA 21 console/web confirmation shape, in the order they are tried.
    private static readonly string[] SelectionKeys = { "selectedItemId", "selectedPlayerId", "choiceId", "selectedResourceId", "choiceIndex", "globalChoiceIndex", "selectedItemPos" };
    private static readonly string[] NestedSelectionKeys = { "selectedItem", "selection", "selectedPick", "choice" };
    private static readonly string[] NestedIdKeys = { "id", "itemId", "resourceId", "assetId", "index" };
    private static readonly string[] ItemDataIdKeys = NestedIdKeys.Prepend("selectedItemId").ToArray();

    private readonly FutServices _fut;

    public PlayerPickService(FutServices fut) => _fut = fut;

    private GameContext Game => _fut.Game;

    /// <summary>Reveals (or returns the already revealed) choices for a pick token.</summary>
    public Task<JsonArray> RedeemAsync(long tokenId, CancellationToken cancellationToken = default) =>
        // Reserved choice ids and the stored choices commit together; the club lock makes the reveal happen once.
        Game.RunAsync("player-pick-reveal", uow => RevealAsync(uow, tokenId), cancellationToken);

    private async ValueTask<JsonArray> RevealAsync(IUnitOfWork uow, long tokenId)
    {
        await uow.LockClubAsync();
        if (await uow.GetItemAsync(tokenId) is not { } token || !ItemFactory.IsPlayerPickToken(token))
        {
            throw new ClubRuleException("player pick item not found");
        }
        var stored = await uow.GetRelationalItemAsync(new NativeItemId(tokenId));
        if (await uow.GetPlayerPickAsync(tokenId) is { Choices.Count: > 0 } state)
        {
            // Repair picks opened by an older runtime that lacked the choice indexes.
            if (RepairChoiceIndexes(state.Choices))
            {
                await uow.SavePlayerPickAsync(tokenId, state);
            }
            return Granted(state.Choices, stored);
        }

        var minRating = Math.Clamp(JsonValues.Int(token.GetOr("minRating", token.GetOr("rating", 0))), 0, Cards.MaxRating);
        var tokenRules = Game.Rules.Cards.PlayerPickToken;
        var choiceCount = (int)Math.Clamp(token.Int("choiceCount", DefaultChoiceCount), tokenRules.MinChoices, tokenRules.MaxChoices);
        var owned = await _fut.Inventory.OwnedPlayerResourceIdsAsync(uow);
        // A token from a store pack's pick group keeps that group's filters.
        var filters = stored?.Pick;
        var choices = new JsonArray();
        var config = filters?.DrawConfig is { } saved ? JsonNode.Parse(saved)!.AsObject()
            : PlayerPickDraw.Configuration(Game.Packs.GetPackConfig(filters?.SourcePackId ?? 0), new JsonObject
            {
                ["min_rating"] = minRating, ["max_rating"] = filters?.MaxRating ?? Cards.MaxRating,
                ["promo_only"] = filters?.PromoOnly ?? false,
                ["rareflags"] = new JsonArray((filters?.Rareflags ?? Array.Empty<long>()).Select(flag => (JsonNode?)flag).ToArray()),
                ["resource_pool"] = filters?.ResourcePool,
            });
        var drawn = PlayerPickDraw.Draw(_fut, config, choiceCount, filters?.SourcePackId ?? 0);
        foreach (var (resourceId, index) in drawn.Select((id, index) => (id, index)))
        {
            var choice = _fut.Items.BuildPlayer(resourceId, await uow.NextItemIdAsync(), "unassigned");
            // Same duplicate marker a pack reveal gets before display.
            choice.Update(TemplateLibrary.Core.Object("player-picks/choice-fields", new TemplateArgs
            {
                ["duplicate"] = owned.Contains(resourceId),
                ["tokenId"] = tokenId,
                ["index"] = index,
            }));
            choices.Add(choice);
        }
        await uow.SavePlayerPickAsync(tokenId, new PlayerPickState(minRating, choiceCount, choices));
        return Granted(choices, stored);
    }

    /// <summary>
    /// The choices as the pick grants them, for display: each carries the token's reward source (else pick) and its
    /// restriction (<see cref="Grant"/>). The stored choices stay as revealed; <see cref="SelectAsync"/> applies the same
    /// rule to the granted player.
    /// </summary>
    private JsonArray Granted(JsonArray choices, RelationalItemAggregate? token)
    {
        var granted = (JsonArray)choices.DeepClone();
        var (source, untradeable) = Grant(token);
        foreach (var choice in granted.OfType<JsonObject>())
        {
            // The grant decides both ways: the choice template (player-picks/choice-fields) always shows untradeable.
            choice["untradeable"] = untradeable;
            _fut.Items.MarkReward(choice, source, untradeable);
        }
        return granted;
    }

    /// <summary>
    /// What a pick grants from this token: its reward source (the token's, else pick), and whether the chosen player is
    /// untradeable. A token is itself always untradeable (items/player-pick-token), and every revealed choice was shown
    /// untradeable (player-picks/choice-fields), so the player inherits the restriction of the grant the token came with:
    /// untradeable when rules/tradeability.json makes that source and pack untradeable (every pick pack is) or makes every
    /// pick untradeable (pick). With the first setting every pick is untradeable, as before.
    /// </summary>
    private (string Source, bool Untradeable) Grant(RelationalItemAggregate? token)
    {
        var tradeability = Game.Rules.Tradeability;
        var source = token?.Header.RewardSource;
        var granted = source is not null && RewardSources.All.Contains(source) && tradeability.Untradeable(source, token?.Pick?.SourcePackId ?? 0);
        return (source ?? RewardSources.Pick, granted || tradeability.Untradeable(RewardSources.Pick));
    }

    private static bool RepairChoiceIndexes(JsonArray choices)
    {
        var repaired = false;
        for (var index = 0; index < choices.Count; index++)
        {
            if (choices[index] is not JsonObject choice)
            {
                continue;
            }
            foreach (var key in ChoiceIndexKeys.Where(key => !(JsonValues.IsStrictInt(choice.Get(key), out var current) && current == index)))
            {
                choice[key] = index;
                repaired = true;
            }
        }
        return repaired;
    }

    /// <summary>Consumes the token and grants the selected choice, keeping the revealed item id.</summary>
    public async Task<JsonObject> SelectAsync(long tokenId, JsonNode? selection, CancellationToken cancellationToken = default)
    {
        // The reveal commits on its own, so a rejected selection never re-rolls the choices.
        var choices = (await RedeemAsync(tokenId, cancellationToken)).OfType<JsonObject>().ToList();
        var numeric = JsonValues.TryInt(selection, out var parsed) ? parsed : -1;
        var selected = (numeric >= 0 && numeric < choices.Count ? choices[(int)numeric] : null)
            ?? choices.FirstOrDefault(choice =>
                numeric == choice.Int("id") || numeric == choice.Int("resourceId") || numeric == choice.Int("assetId"))
            ?? throw new ClubRuleException("selected player is not part of this pick");

        selected = selected.Copy();
        foreach (var key in PickerKeys)
        {
            selected.Remove(key);
        }
        selected["pile"] = "unassigned";
        // The native picker already holds this exact item instance; reusing the token id
        // would describe an object it has never seen.
        var selectedId = selected.IntKeep("id");
        // Consuming the token, granting the choice and its duplicate marker commit together.
        return await Game.RunAsync("player-pick-select", async uow =>
        {
            await uow.LockClubAsync();
            if (await uow.GetRelationalItemAsync(new NativeItemId(tokenId)) is not { } token)
            {
                throw new ClubRuleException("player pick item has already been redeemed");
            }
            // The chosen player inherits the token's restriction (an untradeable pick never grants a tradeable card).
            var (source, untradeable) = Grant(token);
            selected["untradeable"] = untradeable;
            _fut.Items.MarkReward(selected, source, untradeable);
            await uow.DeleteItemAsync(tokenId);
            await uow.SaveItemAsync(selectedId, selected);
            await uow.DeletePlayerPickAsync(tokenId);
            if (await _fut.Inventory.WouldDuplicateOwnedPlayerAsync(uow, selectedId))
            {
                await _fut.Inventory.MarkDuplicateAsync(uow, selectedId);
                return await uow.GetItemAsync(selectedId) ?? selected;
            }
            return selected;
        }, cancellationToken);
    }

    /// <summary>Finds the pending token referenced by the URL, the body, or the only pending token.</summary>
    public static async ValueTask<long> TokenIdAsync(IUnitOfWork uow, string path, JsonNode? payload)
    {
        var pendingIds = (await ClubInventoryService.PendingPlayerPickTokensAsync(uow)).Select(item => item.Int("id")).ToHashSet();
        foreach (var match in PathIdPattern().Matches(path).Reverse())
        {
            if (long.TryParse(match.Groups[1].Value, out var id) && pendingIds.Contains(id))
            {
                return id;
            }
        }
        if (payload is JsonObject body && TokenIdKeys.Select(key => body.Int(key)).FirstOrDefault(pendingIds.Contains) is var fromBody and not 0)
        {
            return fromBody;
        }
        return pendingIds.Count == 1 ? pendingIds.First() : 0;
    }

    /// <summary>Reads every FIFA 21 console/web player-pick confirmation shape.</summary>
    public static (bool HasSelection, JsonNode? Selection) ReadSelection(JsonNode? payload)
    {
        if (payload is not JsonObject body)
        {
            return (false, null);
        }
        if (FirstPresent(body, SelectionKeys) is { HasSelection: true } direct)
        {
            return direct;
        }
        foreach (var nested in NestedSelectionKeys.Select(body.Get).Where(nested => nested is not null))
        {
            if (nested is not JsonObject nestedObject)
            {
                return (true, nested);
            }
            if (FirstPresent(nestedObject, NestedIdKeys) is { HasSelection: true } nestedId)
            {
                return nestedId;
            }
        }
        var data = body.Get("itemData");
        if (data is JsonArray { Count: > 0 } dataList)
        {
            data = dataList[0];
        }
        if (data is JsonObject dataObject && FirstPresent(dataObject, ItemDataIdKeys) is { HasSelection: true } fromItemData)
        {
            return fromItemData;
        }
        if (body.Get("selectedPicks") is JsonArray { Count: > 0 } picks)
        {
            return picks[0] is JsonObject first
                ? FirstPresent(first, ItemDataIdKeys) is { HasSelection: true } fromPick ? fromPick : ReadSelection(first)
                : (true, picks[0]);
        }
        return (false, null);
    }

    private static (bool HasSelection, JsonNode? Selection) FirstPresent(JsonObject source, IEnumerable<string> keys) =>
        keys.FirstOrDefault(source.ContainsKey) is { } key ? (true, source.Get(key)) : (false, null);

    /// <summary>
    /// The revealed choices. CardsDLL parses this reply (FutApplyCardNontargetedServerResponse, parser 0x337020) as itemData
    /// with a duplicateItemIdList, the only place FIFA learns a card is a duplicate; the select reply is the bare item. So
    /// the choices the club already owns are listed here, and the picked one keeps its id, or FIFA offered Send to Club for
    /// a duplicate: PUT /item kept it in Unassigned and the next pack was refused (471).
    /// </summary>
    public async Task<JsonObject> RedeemResponseAsync(long tokenId, CancellationToken cancellationToken)
    {
        var choices = (await RedeemAsync(tokenId, cancellationToken)).OfType<JsonObject>().ToList();
        return TemplateLibrary.Core.Object("player-picks/redeemed", new TemplateArgs
        {
            ["tokenId"] = tokenId,
            ["choices"] = new JsonArray(choices.Select(item => (JsonNode)_fut.Native.PackItem(item)).ToArray()),
            ["duplicates"] = await Game.ReadAsync(uow => ClubInventoryService.DuplicateListAsync(uow, choices), cancellationToken),
        });
    }

    /// <summary>
    /// CardsDLL's select-pick parser deserializes the response root as the item itself, so
    /// neither an array nor an itemData envelope is valid here.
    /// </summary>
    public async Task<JsonObject> SelectResponseAsync(long tokenId, JsonNode? selection, CancellationToken cancellationToken) =>
        _fut.Native.PackItem(await SelectAsync(tokenId, selection, cancellationToken));

    public static JsonObject EmptyPicksResponse() => TemplateLibrary.Core.Object("player-picks/none-pending");

    // Matches do not overlap: the trailing slash is consumed, so "/1/2" yields only 1.
    [GeneratedRegex(@"/(\d+)(?:/|$)")]
    private static partial Regex PathIdPattern();
}
