using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>Conditional counters from an already eligible, settled match; never selects a mode from the client's body.</summary>
internal sealed class ObjectiveMatchProgress(GameContext game)
{
    public async ValueTask<List<(string Stat, long Amount)>> CountsAsync(IUnitOfWork work, JsonObject body,
        string? mode, bool won, ObjectiveRules rules)
    {
        var objectives = rules.GrindGroups.SelectMany(group => group.Objectives).Where(objective => objective.Requirements is not null).ToArray();
        var ids = (body.Arr("items") ?? []).OfType<JsonObject>().Select(item => item.Int("id")).Where(id => id > 0).Distinct().ToArray();
        var owned = new Dictionary<long, JsonObject>();
        foreach (var item in await work.GetItemsAsync(ids))
        {
            if (item.Text("itemType") != "player" || game.Players.Find(item.Int("resourceId")) is not { } meta) continue;
            owned[item.Int("id")] = Facts(meta);
        }
        // A Draft squad is not the club's: FIFA reports its cards by resource id (live SCORING EVIDENCE, 2026-10-08), so
        // every row was unowned and no Draft match could count a league, nation or club objective.
        if (mode is RewardModes.DraftOnline or RewardModes.DraftSinglePlayer)
        {
            foreach (var id in ids.Where(id => !owned.ContainsKey(id)))
                if (game.Players.Find(id) is { } meta) owned[id] = Facts(meta);
        }
        // Every count here is bound by the match's goals, and those by rules.MaxGoalsPerMatch (a crafted end names any number).
        var counts = objectives.Select(objective => (objective.Counter, Math.Min(Count(objective, body, owned, mode, won, rules.ScoringEvents), rules.MaxGoalsPerMatch)))
            .Where(entry => entry.Item2 > 0).ToList();
        // The new assists stat also works for ordinary objectives without league/squad requirements.
        var maximum = Math.Min(Math.Max(0, body.Obj("myMatchStats")?.Int("goals") ?? 0), rules.MaxGoalsPerMatch);
        long assists = 0;
        foreach (var row in (body.Arr("items") ?? []).OfType<JsonObject>().GroupBy(row => row.Int("id")).Select(group => group.First()))
            if (owned.ContainsKey(row.Int("id"))) assists += Math.Min(Math.Max(0, row.Int("assists")), maximum - assists);
        if (assists > 0) counts.Add(("assists", assists));
        return counts;
    }

    private static JsonObject Facts(PlayerMeta meta) => new()
    {
        ["assetId"] = meta.Int("assetId"), ["leagueId"] = meta.Int("leagueId"), ["nation"] = meta.Int("nation"), ["clubId"] = meta.Int("teamid"),
    };

    internal static long Count(GrindObjective objective, JsonObject body, IReadOnlyDictionary<long, JsonObject> owned,
        string? mode, bool won, ObjectiveScoringEvents scoring)
    {
        var req = objective.Requirements;
        if (req is null || mode is null || !req.Modes.Contains(mode, StringComparer.Ordinal)) return 0;
        var rows = (body.Arr("items") ?? []).OfType<JsonObject>().ToArray();
        var starters = rows.Take(11).Select(row => row.Int("id")).ToArray();
        // Missing, duplicate, unknown or unowned starting cards cannot satisfy an XI restriction.
        if (req.StartingSquad is { } squad)
        {
            if (starters.Length != 11 || starters.Distinct().Count() != 11 || starters.Any(id => !owned.ContainsKey(id))) return 0;
            var count = starters.Count(id => Matches(owned[id], squad));
            if (squad.ExactCount is { } exact ? count != exact : count < squad.MinimumCount) return 0;
        }
        if (objective.Stat == "matches") return 1;
        if (objective.Stat == "wins") return won ? 1 : 0;
        if (objective.Stat == "cleanSheets") return (body.Obj("opponentMatchStats")?.Int("goals") ?? 0) == 0 ? 1 : 0;
        var goals = Math.Max(0, body.Obj("myMatchStats")?.Int("goals") ?? 0);
        if (objective.Stat == "scoringMatches")
        {
            // With a player restriction, a match counts when one of the matching players scored.
            return goals > 0 && (req.Player is null || rows.Any(row => row.Int("goals") > 0
                && owned.TryGetValue(row.Int("id"), out var scorer) && Matches(scorer, req.Player))) ? 1 : 0;
        }
        if (objective.Stat == "goals" && req.Player is null && req.GoalType is null) return Math.Min(goals, objective.Target);
        var type = req.GoalType ?? req.AssistType;
        // Special scoring requires an explicit mapping. An ordinary goal/assist never fills this gap.
        if (type is not null && (scoring.Flag(type) is null || scoring.PlayerIdKind is null)) return 0;
        var events = type is null ? null : Events(body, objective.Stat == "goals" ? "goal" : "assist", scoring.Flag(type)!.Value);
        long total = 0;
        foreach (var row in rows.GroupBy(row => row.Int("id")).Select(group => group.First()))
        {
            if (!owned.TryGetValue(row.Int("id"), out var player) || !Matches(player, req.Player)) continue;
            var reported = Math.Max(0, row.Int(objective.Stat));
            var amount = events is null ? reported : Math.Min(reported,
                events.GetValueOrDefault(scoring.PlayerIdKind == "itemId" ? row.Int("id") : player.Int("assetId")));
            total += Math.Min(amount, objective.Target - total);
        }
        return Math.Min(total, Math.Max(0, body.Obj("myMatchStats")?.Int("goals") ?? 0));
    }

    private static bool Matches(JsonObject player, ObjectivePlayerRestriction? restriction) => restriction is null
        || (restriction.LeagueId is not { } league || player.Int("leagueId") == league)
        && (restriction.Nation is not { } nation || player.Int("nation") == nation)
        && (restriction.ClubId is not { } club || player.Int("clubId") == club);

    /// <summary>
    /// CardsDLL 0x2B1261-0x2B1B72 serializes events as {attributes:[{key:"name",value:"goal"|"assist"},
    /// {key:"player_id",value:"..."},{key:"flag",value:integer},...]}. Repeated flags still describe one event.
    /// </summary>
    private static Dictionary<long, long> Events(JsonObject body, string name, long flag)
    {
        var counts = new Dictionary<long, long>();
        foreach (var entry in (body.Arr("events") ?? []).OfType<JsonObject>())
        {
            var attrs = (entry.Arr("attributes") ?? []).OfType<JsonObject>().ToArray();
            var names = attrs.Where(attr => attr.Text("key") == "name").ToArray();
            var players = attrs.Where(attr => attr.Text("key") == "player_id").ToArray();
            if (names.Length != 1 || names[0].Text("value") != name || players.Length != 1
                || !JsonValues.TryInt(players[0].Get("value"), out var player) || player <= 0
                || !attrs.Any(attr => attr.Text("key") == "flag" && JsonValues.TryInt(attr.Get("value"), out var value) && value == flag)) continue;
            counts[player] = counts.GetValueOrDefault(player) + 1;
        }
        return counts;
    }
}
