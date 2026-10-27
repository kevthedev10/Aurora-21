using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>One requirement as the client gets it: an eligibility slot (or none, for a description-only rule) and its label.</summary>
public sealed record SbcRequirement(SbcEligibilityKey? Key, long Value, SbcScope Scope, long PlayerCount, string Label);

/// <summary>
/// The SBC text and requirement translation from rules/sbc.json and one authored set or challenge: what FIFA shows as a
/// challenge's requirements and a set's description. SbcService serves it to the game; the panel's editor previews with it.
/// </summary>
public static class SbcText
{
    private static JsonObject AttributesOf(JsonObject entry) => entry.Obj("attributes") ?? new JsonObject();

    private static IEnumerable<JsonObject> RequirementRules(JsonObject source) =>
        ((source.Obj("requirements") ?? new JsonObject()).Arr("rules") ?? new JsonArray()).OfType<JsonObject>();

    /// <summary>The authored requirement rules this server understands, in authored order; ignored rule types are neither shown nor checked.</summary>
    public static IEnumerable<SbcRequirement> Requirements(SbcRules rules, JsonObject challenge)
    {
        foreach (var rule in RequirementRules(challenge))
        {
            var type = JsonValues.Text(rule.Get("type")).ToUpperInvariant();
            if (rules.IgnoredRequirements.Contains(type) || !rules.Requirements.TryGetValue(type, out var definition))
            {
                continue;
            }
            var attributes = AttributesOf(rule);
            if (definition.Bound == SbcBound.Value)
            {
                var text = JsonValues.Text(attributes.GetOr("value", string.Empty));
                if (text.Length > 0)
                {
                    var quality = definition.Qualities.GetValueOrDefault(text.ToLowerInvariant());
                    // A quality rule covers every player the challenge takes (all eleven, or a brick wall's free slots).
                yield return new SbcRequirement(quality != 0 ? definition.EligibilityKey : null, quality, definition.Scope,
                        definition.PlayerCount > 0 ? RequiredPlayers(rules, challenge) : 0, RuleText.Format(definition.Label, ("value", TitleCase(text))));
                }
                continue;
            }
            var fixedValue = definition.EligibilityValue;
            if (definition.Selector is { } selector)
                fixedValue = selector == "quality" ? definition.Qualities.GetValueOrDefault(attributes.Text(selector)) : attributes.Int(selector);
            foreach (var (boundName, scope, comparison) in new[]
                { ("min", SbcScope.AtLeast, "Min"), ("max", SbcScope.AtMost, "Max"), ("exact", SbcScope.Exact, "Exactly") })
            {
                if (attributes.Get(boundName) is not { } number || !JsonValues.TryInt(number, out var bound) || bound < 0) continue;
                if (boundName == "max" && fixedValue is null && Ceiling(rules, challenge, definition.EligibilityKey) is { } ceiling && bound >= ceiling) continue;
                // Zero is meaningful (for example, at most zero TOTW players).
                var label = definition.Label;
                if (boundName == "exact" && definition.Exact is { } exact) label = exact.Label;
                else label = label.Replace("Min ", comparison + " ").Replace("Max ", comparison + " ");
                var selection = attributes.Text("selectionName", attributes.Text(definition.Selector ?? ""));
                label = RuleText.Format(label, ("value", bound), ("comparison", comparison), ("selection", definition.Selector == "quality" ? TitleCase(selection) : selection));
                yield return new SbcRequirement(definition.EligibilityKey, fixedValue ?? bound, scope,
                    fixedValue is not null ? bound : definition.PlayerCount, label);
            }
        }
    }

    /// <summary>
    /// The highest value a squad-wide requirement can reach, or null when it has none. A max bound at or above it can never fail
    /// ("Team Overall Rating: Max. 99", "Squad Chemistry: Max. 100"), so it is neither sent nor checked: FIFA 21 lists only the
    /// first six requirement lines and neither shows nor checks the rest, so a no-op line pushed Chemistry 100 off the list on
    /// Advanced (CH_20) and the server refused squads the game showed all green (10-08).
    /// </summary>
    private static long? Ceiling(SbcRules rules, JsonObject challenge, SbcEligibilityKey? key) => key switch
    {
        SbcEligibilityKey.TeamRating => 99,
        SbcEligibilityKey.Chemistry => 100,
        SbcEligibilityKey.SameNation or SbcEligibilityKey.SameLeague or SbcEligibilityKey.SameClub
            or SbcEligibilityKey.NationCount or SbcEligibilityKey.LeagueCount or SbcEligibilityKey.ClubCount => RequiredPlayers(rules, challenge),
        _ => null,
    };

    /// <summary>Human-readable requirement summary used as the challenge bio.</summary>
    public static string Describe(SbcRules rules, JsonObject challenge)
    {
        var labels = Requirements(rules, challenge).Select(requirement => requirement.Label).ToList();
        return labels.Count > 0 ? string.Join(rules.Text.RequirementSeparator, labels) : RuleText.Format(rules.Text.NoRequirements, ("count", RequiredPlayers(rules, challenge)));
    }

    /// <summary>Set descriptions are frequently null in sbc.json; never show the literal "None".</summary>
    public static string SetDescription(SbcRules rules, JsonObject set)
    {
        if (set.Get("description") is JsonValue description && JsonValues.IsString(description) && JsonValues.Text(description).Trim().Length > 0)
        {
            return JsonValues.Text(description);
        }
        var name = JsonValues.Text(set.GetOr("name", string.Empty), "None").Trim();
        var count = set.Arr("challenges")?.Count ?? 0;
        var challenges = count == 1 ? rules.Text.Challenge : rules.Text.Challenges;
        return name.Length > 0
            ? RuleText.Format(rules.Text.SetDescription, ("name", name), ("count", count), ("challenges", challenges))
            : RuleText.Format(rules.Text.UnnamedSetDescription, ("count", count), ("challenges", challenges));
    }

    /// <summary>The exact squad size: the PLAYERS rule's max (its min is only a tutorial lower bound).</summary>
    public static long RequiredPlayers(SbcRules rules, JsonObject challenge)
    {
        var squadSize = rules.SquadSize;
        if (RequirementRules(challenge).FirstOrDefault(rule => JsonValues.Text(rule.Get("type")).ToUpperInvariant() == "PLAYERS") is not { } players)
        {
            return squadSize;
        }
        var attributes = AttributesOf(players);
        var value = JsonValues.Int(attributes.GetOr("max", attributes.GetOr("exact", squadSize)), squadSize);
        return value >= 1 && value <= squadSize ? value : squadSize;
    }

    /// <summary>Upper-cases the first letter of each word and lower-cases the rest ("gold" becomes "Gold").</summary>
    private static string TitleCase(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousCased = false;
        foreach (var c in text)
        {
            builder.Append(previousCased ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c));
            previousCased = char.IsLetter(c);
        }
        return builder.ToString();
    }
}
