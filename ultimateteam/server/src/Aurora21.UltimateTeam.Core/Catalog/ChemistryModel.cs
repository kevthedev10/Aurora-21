using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>A linked-teammate threshold: an average link score of at least <see cref="Min"/> gives <see cref="Factor"/>.</summary>
public sealed class ChemistryLinkThreshold
{
    public required double Min { get; set; }
    public required double Factor { get; set; }
}

/// <summary>
/// FIFA 21's own squad chemistry (runtime-source/ultimateteam21/chemistry.json): the numbers the squad screen and every swap
/// preview show. Ported from the Python server, which checked it against 267 saved squads.
/// </summary>
public sealed class ChemistryModel
{
    public required List<string> Positions { get; set; }
    public required Dictionary<string, int> PositionGroups { get; set; }
    public required List<double> FitFactors { get; set; }
    public required double ExactSlotFactor { get; set; }
    public required double UnknownFitFactor { get; set; }
    public required long LinkScore { get; set; }
    public required long IconLeagueId { get; set; }
    public required List<ChemistryLinkThreshold> LinkThresholds { get; set; }
    public required double NoLinkFactor { get; set; }
    public required int PlayerCap { get; set; }
    public required int SquadCap { get; set; }
    public required Dictionary<string, int> FormationIds { get; set; }
    public required Dictionary<string, List<int>> Formations { get; set; }
    public required Dictionary<string, Dictionary<string, List<int>>> Links { get; set; }
    public required Dictionary<string, Dictionary<string, int>> Fit { get; set; }

    /// <summary>The starting slots a formation has (eleven in every FIFA 21 formation).</summary>
    public int Starters => Formations.Values.Max(slots => slots.Count);

    public static ChemistryModel Load(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "chemistry.json");
        return RulesJson.Deserialize<ChemistryModel>(File.ReadAllText(path), path);
    }

    private readonly record struct Starter(int Slot, int Preferred, long Club, long League, long Nation, bool Loyal);

    /// <summary>
    /// Squad chemistry for eleven starters in formation order (a null starter is an empty slot) and the squad's manager,
    /// or null for a formation FIFA has no slots for. Starters are native player items.
    /// </summary>
    public int? SquadChemistry(string formation, IReadOnlyList<JsonObject?> starters, JsonObject? manager)
    {
        if (!Formations.TryGetValue(formation, out var slots) || !FormationIds.TryGetValue(formation, out var formationId))
        {
            return null;
        }
        var links = Links.GetValueOrDefault(formationId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? new();
        // Python's reads: a present value counts even when it is 0; only a missing one takes the default.
        static long Read(JsonObject? source, string key, long missing) => source is not null && JsonValues.TryInt(source.Get(key), out var value) ? value : missing;
        var managerLeague = Read(manager, "leagueId", -1);
        var managerNation = Read(manager, "nation", -1);
        var players = new List<Starter>();
        for (var index = 0; index < slots.Count; index++)
        {
            if (index >= starters.Count || starters[index] is not { } item)
            {
                continue;
            }
            players.Add(new Starter(slots[index], Positions.IndexOf(item.Str("preferredPosition").ToUpperInvariant()),
                item.Int("teamid"), item.Int("leagueId"), item.Int("nation"), Loyal(item)));
        }
        var total = 0;
        foreach (var player in players)
        {
            var scores = new List<long>();
            foreach (var other in players)
            {
                if (other.Equals(player) || !(links.GetValueOrDefault(other.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Contains(player.Slot) ?? false))
                {
                    continue;
                }
                var score = player.Club == other.Club ? LinkScore : 0;
                if (player.League == IconLeagueId || other.League == IconLeagueId || player.League == other.League) score += LinkScore;
                if (player.Nation == other.Nation) score += LinkScore;
                scores.Add(score);
            }
            var link = scores.Count > 0 ? LinkFactor(scores.Average()) : NoLinkFactor;
            var chemistry = (int)Math.Floor(link * PositionFactor(player.Preferred, player.Slot) + 0.5);
            if (player.League == managerLeague || player.Nation == managerNation) chemistry++;
            if (player.Loyal) chemistry++;
            total += Math.Min(PlayerCap, chemistry);
        }
        return Math.Min(SquadCap, total);
    }

    private double LinkFactor(double average) =>
        LinkThresholds.FirstOrDefault(threshold => average >= threshold.Min)?.Factor ?? NoLinkFactor;

    private double PositionFactor(int preferred, int slot)
    {
        if (preferred == slot)
        {
            return ExactSlotFactor;
        }
        string Group(int position) => PositionGroups.GetValueOrDefault(position.ToString(System.Globalization.CultureInfo.InvariantCulture), position)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Fit.TryGetValue(Group(preferred), out var row)
            ? FitFactors[row.GetValueOrDefault(Group(slot))]
            : UnknownFitFactor;
    }

    /// <summary>
    /// The chemistry service's loyalty test with enableLoyaltyBonusForConceptPlayers off: a free or in-game card that has
    /// played ten games for the club, has one owner, or carries a loyalty bonus. Concept and Draft cards have two owners
    /// and no bonus, so they are never loyal.
    /// </summary>
    private static bool Loyal(JsonObject item)
    {
        if (item.Str("itemState").ToLowerInvariant() is not ("free" or "ingame"))
        {
            return false;
        }
        if ((item.Get("statsList") as JsonArray)?.OfType<JsonObject>().FirstOrDefault(row => JsonValues.TryInt(row.Get("index"), out var index) && index == 0) is { } games
            && games.Int("value") >= 10)
        {
            return true;
        }
        var owners = JsonValues.TryInt(item.Get("owners"), out var count) ? count : 1;
        return owners <= 1 || item.Int("loyaltyBonus") >= 1;
    }
}
