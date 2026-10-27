using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Models;

/// <summary>How many gold, silver and bronze cards a pack of a given size shows, from its tier weights.</summary>
public readonly record struct PackTierMix(long Gold, long Silver, long Bronze, string Dominant)
{
    public static PackTierMix For(JsonObject config, long quantity)
    {
        var gold = Math.Max(0.0, config.Float("gold_weight"));
        var silver = Math.Max(0.0, config.Float("silver_weight"));
        var bronze = Math.Max(0.0, config.Float("bronze_weight"));
        var total = gold + silver + bronze;
        if (total <= 0)
        {
            (gold, silver, bronze, total) = (100.0, 0.0, 0.0, 100.0);
        }
        var goldCount = (long)Math.Round(quantity * gold / total);
        var silverCount = (long)Math.Round(quantity * silver / total);
        var dominant = silver > gold ? (bronze > silver ? "bronze" : "silver") : bronze > gold ? "bronze" : "gold";
        return new PackTierMix(goldCount, silverCount, Math.Max(0, quantity - goldCount - silverCount), dominant);
    }
}

