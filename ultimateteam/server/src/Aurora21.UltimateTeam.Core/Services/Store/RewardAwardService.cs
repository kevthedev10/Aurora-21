using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>The reward tiles shared by SBCs, Objectives and FUT Champions.</summary>
public sealed class RewardAwardService
{
    private readonly PackSettings _packs;

    public RewardAwardService(PackSettings packs) => _packs = packs;

    /// <summary>
    /// A pack reward tile. Deliberately narrow: item-shaped keys such as defId collide with real
    /// cards and crash squad edits, and any itemData (even empty) makes the reward decoders draw a
    /// blank player card. The art is the pack's art band: objectives read it from assetId, Season
    /// Progress levels from halId.
    /// </summary>
    public JsonObject Pack(long packId, long count = 1)
    {
        var config = _packs.GetPackConfig(packId);
        var art = _packs.Rules.StoreArtId(packId, config);
        var name = JsonValues.HasValue(config.Get("name")) ? config.Text("name") : _packs.Rules.PackName(packId);
        return TemplateLibrary.Core.Object("awards/pack", new TemplateArgs
        {
            ["packId"] = packId,
            ["artId"] = art,
            ["artIdText"] = art.ToString(CultureInfo.InvariantCulture),
            ["count"] = count,
            ["name"] = name,
        });
    }

    /// <summary>The game's AWARD_LABEL_0 (ITEM(S)) and AWARD_LABEL_31 (STADIUM(S)).</summary>
    public const long AwardLabelItems = 0, AwardLabelStadiums = 31;

    /// <summary>An item reward tile (the SBC item shape): a My Stadium card, an unlocked slot's type or a stadium tier.</summary>
    /// <remarks>
    /// <paramref name="label"/> is the award's halId, which FIFA shows as AWARD_LABEL_&lt;halId&gt; under the tile (0 ITEM(S),
    /// 31 STADIUM(S), 39 KIT(S)...); the item's resource id there rendered as *AWARD_LABEL_6200444.
    /// </remarks>
    public JsonObject Item(JsonObject item, string name, long label = AwardLabelItems) =>
        TemplateLibrary.Core.Object("awards/sbc-item", new TemplateArgs
        {
            ["resourceId"] = JsonValues.Int(item.GetOr("resourceId", item.Get("assetId"))),
            ["halId"] = label,
            ["untradeable"] = true,
            ["name"] = name,
            ["item"] = item,
            ["rating"] = item.Int("rating"),
        });

    /// <summary>A coin reward tile (FUT Champions prize tiers).</summary>
    public JsonObject Coins(long coins) =>
        TemplateLibrary.Core.Object("awards/coins", new TemplateArgs { ["coins"] = coins });

    /// <summary>A coin reward tile carrying the value/count aliases the Objectives screens read.</summary>
    public JsonObject ObjectiveCoins(long coins) =>
        TemplateLibrary.Core.Object("awards/objective-coins", new TemplateArgs { ["coins"] = coins });
}
