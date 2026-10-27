using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>
/// The grant sources a reward pack or item can come from (rules/tradeability.json sources). Each grant names one; the
/// switch decides whether it is untradeable, and an item remembers it as its reward source (items.reward_source).
/// </summary>
public static class RewardSources
{
    public const string Store = "store";
    public const string Sbc = "sbc";
    public const string Objective = "objective";
    public const string Season = "season";
    public const string RivalsOptionA = "rivals_option_a";
    public const string RivalsOptionB = "rivals_option_b";
    public const string RivalsOptionC = "rivals_option_c";

    /// <summary>A Rivals rank with one fixed reward (Ranks V and VI).</summary>
    public const string RivalsFixed = "rivals_fixed";
    public const string RivalsPromotion = "rivals_promotion";
    public const string WeekendLeague = "weekend_league";
    public const string SquadBattles = "squad_battles";
    public const string DraftOnline = "draft_online";
    public const string DraftSingle = "draft_single";
    public const string Gift = "gift";
    public const string MatchPack = "match_pack";
    public const string MatchCompletion = "match_completion";

    /// <summary>The player chosen from a 1-of-N pick.</summary>
    public const string Pick = "pick";
    public const string Pity = "pity";
    public const string GuaranteedInform = "guaranteed_inform";

    /// <summary>A pack or card an operator grants from the panel.</summary>
    public const string Operator = "operator";

    /// <summary>Every source; rules/tradeability.json must set each one.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Store, Sbc, Objective, Season, RivalsOptionA, RivalsOptionB, RivalsOptionC, RivalsFixed, RivalsPromotion, WeekendLeague,
        SquadBattles, DraftOnline, DraftSingle, Gift, MatchPack, MatchCompletion, Pick, Pity, GuaranteedInform, Operator,
    ];

    /// <summary>
    /// The item document key a grant sets for the save to store as the item's reward source. The save consumes it: a
    /// rendered item never carries it, so the client never sees it.
    /// </summary>
    public const string ItemKey = "rewardSource";

    /// <summary>The Rivals source of a rank's reward option: A, B, C by position when the rank offers a choice, else fixed.</summary>
    public static string RivalsOption(int optionIndex, int optionCount) =>
        optionCount <= 1 ? RivalsFixed
        : optionIndex switch { 0 => RivalsOptionA, 1 => RivalsOptionB, _ => RivalsOptionC };
}

/// <summary>
/// rules/tradeability.json (owner decision D14): whether each grant source hands out untradeable packs and items, a lever
/// changed without code. A pack's own override (packs) beats its source's value, and a source's per-pack override
/// (sourcePacks) beats both.
/// </summary>
public sealed class TradeabilityRules : IJsonOnDeserialized
{
    /// <summary>Each grant source: true when what it grants is untradeable.</summary>
    public required Dictionary<string, bool> Sources { get; set; }

    /// <summary>Packs untradeable (true) or tradeable (false) from any source.</summary>
    public required Dictionary<long, bool> Packs { get; set; }

    /// <summary>Per source, packs that differ from both the source and the pack override.</summary>
    public required Dictionary<string, Dictionary<long, bool>> SourcePacks { get; set; }

    /// <summary>Whether a grant from <paramref name="source"/> (of pack <paramref name="packId"/>, or 0 for an item) is untradeable.</summary>
    /// <exception cref="InvalidOperationException">The source is not one of <see cref="RewardSources.All"/>.</exception>
    public bool Untradeable(string source, long packId = 0)
    {
        if (!Sources.TryGetValue(source, out var bySource))
        {
            throw new InvalidOperationException($"unknown grant source {source}: rules/tradeability.json sources lists every source");
        }
        if (SourcePacks.TryGetValue(source, out var packs) && packs.TryGetValue(packId, out var bySourcePack))
        {
            return bySourcePack;
        }
        return Packs.TryGetValue(packId, out var byPack) ? byPack : bySource;
    }

    public void OnDeserialized()
    {
        var unknown = Sources.Keys.Concat(SourcePacks.Keys).Where(source => !RewardSources.All.Contains(source)).ToList();
        var missing = RewardSources.All.Where(source => !Sources.ContainsKey(source)).ToList();
        if (unknown.Count > 0 || missing.Count > 0)
        {
            throw new InvalidDataException("rules/tradeability.json sources must set exactly the grant sources "
                + $"{string.Join(", ", RewardSources.All)} (unknown: {string.Join(", ", unknown)}; missing: {string.Join(", ", missing)}).");
        }
    }
}
