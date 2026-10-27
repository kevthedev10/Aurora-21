using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>
/// One line of a <see cref="Reward"/> (todo.md section 2): a pack or pick (<see cref="PackId"/>) or a player card
/// (<see cref="CardResourceId"/>), how many, and whether the club receives it untradeable. The line's own choice is what
/// pays; rules/tradeability.json does not change it.
/// </summary>
public sealed class RewardLine
{
    /// <summary>The most copies of one line; a reward is a prize, not a bulk grant.</summary>
    public const long MaxCount = 100;

    public long? PackId { get; set; }
    public long? CardResourceId { get; set; }
    public long Count { get; set; } = 1;
    public required bool Untradeable { get; set; }

    /// <summary>The grant source the line is recorded under (<see cref="RewardSources"/>); null is the mode's own.</summary>
    public string? Source { get; set; }

    public JsonObject ToJson()
    {
        var line = new JsonObject();
        if (PackId is { } pack)
        {
            line["packId"] = pack;
        }
        if (CardResourceId is { } card)
        {
            line["cardResourceId"] = card;
        }
        line["count"] = Count;
        line["untradeable"] = Untradeable;
        if (Source is { } source)
        {
            line["source"] = source;
        }
        return line;
    }
}

/// <summary>
/// The one reward format every mode shares (todo.md section 2, owner design 2026-10-03): coins, FUT Champions Points, XP and
/// lines. Stored in the rewards and reward_lines tables (migration 126); a mode's own tier table says who earns which.
/// </summary>
public sealed class Reward
{
    public long Coins { get; set; }
    public long FcPoints { get; set; }
    public long Xp { get; set; }
    public List<RewardLine> Lines { get; set; } = new();

    /// <summary>The packs and picks of <see cref="Lines"/>, as the competition payouts and their stored results carry them.</summary>
    [JsonIgnore]
    public IEnumerable<RewardPack> Packs => Lines.Where(line => line.PackId is not null).Select(line => new RewardPack
    {
        PackId = line.PackId!.Value,
        Count = line.Count,
        Untradeable = line.Untradeable,
        Source = line.Source,
    });

    /// <summary>
    /// Why the reward is invalid, or null: no negative amounts, every line exactly one pack or card with a count of 1 to
    /// <see cref="RewardLine.MaxCount"/> and a known grant source. <paramref name="cards"/> false refuses card lines (a mode
    /// whose prize screens are not yet checked with a player tile).
    /// </summary>
    public string? Problem(bool cards)
    {
        if (Coins < 0 || FcPoints < 0 || Xp < 0)
        {
            return "coins, FC Points and XP must not be negative";
        }
        foreach (var (line, index) in Lines.Select((line, index) => (line, index + 1)))
        {
            if ((line.PackId is null) == (line.CardResourceId is null))
            {
                return $"line {index} needs exactly one of a pack or a player card";
            }
            if (line.PackId is < 1 || line.CardResourceId is < 1)
            {
                return $"line {index} names no valid pack or card";
            }
            if (line.CardResourceId is not null && !cards)
            {
                return $"line {index}: player cards are not available in this mode's rewards yet";
            }
            if (line.Count is < 1 or > RewardLine.MaxCount)
            {
                return $"line {index} needs a count from 1 to {RewardLine.MaxCount}";
            }
            if (line.Source is { } source && !RewardSources.All.Contains(source))
            {
                return $"line {index} names unknown grant source {source}";
            }
        }
        return null;
    }

    public JsonObject ToJson() => new()
    {
        ["coins"] = Coins,
        ["fcPoints"] = FcPoints,
        ["xp"] = Xp,
        ["lines"] = new JsonArray(Lines.Select(line => (JsonNode?)line.ToJson()).ToArray()),
    };

    /// <summary>A reward from its JSON form (the bundled seeds and panel edits), read strictly.</summary>
    /// <exception cref="InvalidDataException">The JSON is not a reward.</exception>
    public static Reward FromJson(JsonNode? node, string source) =>
        RulesJson.Deserialize<Reward>(node is JsonObject reward ? reward.ToJsonString() : throw new InvalidDataException($"{source}: a reward is an object"), source);
}

/// <summary>
/// The Division Rivals reward tables (migration 131): for each division 1-10 its six ranks (Rank I first), each with 1-4 reward
/// options, and each division's promotion reward. Also the shape of the bundled seed rewards/rivals.json.
/// </summary>
public sealed class RivalsRewardTables
{
    public required Dictionary<int, List<List<Reward>>> Divisions { get; set; }
    public required Dictionary<int, Reward> Promotion { get; set; }
}

/// <summary>Every mode's reward tables, as the rules load them (the database, or the bundled seeds for file-only loads).</summary>
public sealed record ModeRewardTables(IReadOnlyList<ChampionsRank> Champions, RivalsRewardTables Rivals,
    IReadOnlyList<SquadBattlesRewardTier> SquadBattles, IReadOnlyList<DraftRewardLevel> Draft);
