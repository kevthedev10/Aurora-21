using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>Mixed packs specify ranges, one tier, and an exact rare count across all item families.</summary>
public sealed record MixedPackContents(string Tier, int Size, int Rare, IReadOnlyList<MixedPackContents.Range> Ranges)
{
    public static readonly string[] Categories = ["players", "consumables", "stadium", "staff"];
    public sealed record Range(int Min, int Max);
    public sealed record Capacity(int Common, int Rare);
    public sealed record SlotCount(int Total, int Rare);

    public static MixedPackContents? Read(JsonObject config)
    {
        if (config["mixed_contents"] is null) return null;
        if (config["mixed_contents"] is JsonObject { Count: 0 }) return null;
        if (config["mixed_contents"] is not JsonObject contents ||
            contents.Any(pair => pair.Key != "tier" && !Categories.Contains(pair.Key)))
            throw new InvalidOperationException("Mixed contents need a tier and four item ranges.");
        var tier = contents.Text("tier");
        if (tier is not ("bronze" or "silver" or "gold"))
            throw new InvalidOperationException("Mixed pack tier must be bronze, silver or gold.");
        int Number(JsonObject obj, string key, int max)
        {
            if (!JsonValues.IsStrictInt(obj[key], out var number) || number < 0 || number > max)
                throw new InvalidOperationException($"Mixed pack {key} must be a whole number from 0 to {max}.");
            return (int)number;
        }
        var size = Number(config, "size", 100);
        var rare = Number(config, "rare_slots", size);
        if (size == 0) throw new InvalidOperationException("Mixed packs must contain at least one item.");
        var ranges = Categories.Select(category =>
        {
            if (contents[category] is not JsonObject range || range.Count != 2 || !range.ContainsKey("min") || !range.ContainsKey("max"))
                throw new InvalidOperationException($"Mixed pack {category} needs minimum and maximum counts.");
            var min = Number(range, "min", size);
            var max = Number(range, "max", size);
            if (min > max) throw new InvalidOperationException($"Mixed pack {category} minimum exceeds its maximum.");
            return new Range(min, max);
        }).ToArray();
        if (ranges.Sum(range => range.Min) > size || ranges.Sum(range => range.Max) < size)
            throw new InvalidOperationException("Mixed item ranges cannot add up to the pack size.");
        if (ranges[0].Max < rare)
            throw new InvalidOperationException($"Mixed pack rare slots are rare players, so it needs room for {rare} players.");
        if (config.Arr("player_picks") is { Count: > 0 } || config.Int("player_pick_count") > 0 ||
            config.Int("bonus_player_count") > 0 || config.Bool("promo_only") || config.Int("guaranteed_specials") > 0 ||
            config.Int("random_consumable_count") > 0 || config.Int("random_manager_count") > 0 ||
            config.Obj("identity_pack") is not null || config.Bool("all_consumables") || config.Bool("all_kits") ||
            config.Bool("kit_stadium_pack") || config.Int("set_stadium_id") > 0 || config.Int("swap_token_rareflag") > 0 ||
            PackOpeningService.VanityPack(config) is not null || config.Float("manager_chance") > 0 || config.Float("chem_style_chance") > 0)
            throw new InvalidOperationException("Mixed contents cannot be combined with pick, replacement-slot or other item generators.");
        return new(tier, size, rare, ranges);
    }

    /// <summary>
    /// Uniform feasible compositions. A pack's rare count is its rare players: "1 rare" in a Gold Pack is the rare player
    /// fans open it for. Other items keep their own rarity, which only sets how often they drop (<see cref="Catalog.NonPlayerDropTable"/>),
    /// so only the players' capacity has a rare part. Never pads a missing pool.
    /// </summary>
    public IReadOnlyList<SlotCount> Draw(IReadOnlyList<Capacity> pools, GameRandom random)
    {
        if (pools.Count != Categories.Length) throw new ArgumentException("Four mixed item pools are required.", nameof(pools));
        bool Fits(int index, int count) => index == 0
            ? count >= Rare && Rare <= pools[0].Rare && count - Rare <= pools[0].Common
            : count <= pools[index].Common;
        var counts = new int[4];
        int[]? selected = null;
        long seen = 0;
        void Composition(int index, int left)
        {
            if (index == 4)
            {
                if (left == 0 && random.NextInt64(0, ++seen) == 0) selected = (int[])counts.Clone();
                return;
            }
            var range = Ranges[index];
            var remainingMin = Ranges.Skip(index + 1).Sum(r => r.Min);
            var remainingMax = Ranges.Skip(index + 1).Sum(r => r.Max);
            for (var count = Math.Max(range.Min, left - remainingMax); count <= Math.Min(range.Max, left - remainingMin); count++)
            {
                if (!Fits(index, count)) continue;
                counts[index] = count;
                Composition(index + 1, left - count);
            }
        }
        Composition(0, Size);
        if (selected is null)
        {
            var missing = Categories.Where((_, i) => Ranges[i].Min > 0 && pools[i].Common + pools[i].Rare == 0);
            var names = string.Join(", ", missing);
            throw new InvalidOperationException(names.Length > 0
                ? $"No verified {Tier} items are available for: {names}."
                : $"The verified {Tier} item pools cannot fill these ranges with {Rare} rare players.");
        }
        return selected.Select((count, i) => new SlotCount(count, i == 0 ? Rare : 0)).ToArray();
    }
}
