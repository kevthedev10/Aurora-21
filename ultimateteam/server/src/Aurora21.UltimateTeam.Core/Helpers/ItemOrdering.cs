using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Helpers;

/// <summary>The order FIFA reveals pack items in: specials, then players by rating and rarity, managers last.</summary>
public static class ItemOrdering
{
    public static List<JsonObject> RevealOrder(IEnumerable<JsonObject> items) => items
        .OrderBy(item => item.Text("itemType", "player") == "manager" ? 2 : item.Bool("special") || item.Int("rareflag") > 1 ? 0 : 1)
        .ThenBy(item => -item.Int("rating"))
        .ThenBy(item => -item.Int("rareflag"))
        .ThenBy(item => -ClubInventoryService.ResourceOrAsset(item))
        .ToList();
}
