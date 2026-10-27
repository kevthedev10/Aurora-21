using System.Globalization;
using Aurora21.UltimateTeam.Protocol.Tdf;

namespace Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

/// <summary>
/// Online FUT Friendlies ("Play Online" in the Friendlies hub): the FUT_HouseRules scenario. FIFA's request builder
/// (FIFA21.exe 0x140be5d30) puts the player's chosen house-rule game types in its criteria as FUT_HOUSE_RULE_PREF1..3
/// ("abstain" when unset), and CardsDLL reads the game's FUTFriendlyType back from GAME.ATTR when it joins (0x1800b7a6f,
/// through the OSDK attribute snapshot 0x1410c5510 and getInt 0x1410b4cd0): a type outside 0..9 fails the join
/// (FailedToJoin). Both players must therefore be given one game type they both chose.
/// </summary>
public static class HouseRules
{
    /// <summary>The GAME.ATTR key FIFA reads the match's house-rule game type from (OSDK rule FUTFriendlyType).</summary>
    public const string GameTypeAttribute = "FUTFriendlyType";

    private const int MaxGameType = 9;
    private static readonly string[] PreferenceKeys =
        { "FUT_HOUSE_RULE_PREF1", "FUT_HOUSE_RULE_PREF2", "FUT_HOUSE_RULE_PREF3", GameTypeAttribute, "FUT_FRIENDY_HOUSE_RULES_TYPE" };

    /// <summary>Whether a scenario name is online Friendlies (FUT_HouseRules, or its limited-time variant FUT_HouseRules_LTM).</summary>
    public static bool IsHouseRulesMode(string mode) => MatchmakingRegistry.ModeKey(mode) is "fut_houserules" or "fut_houserules_ltm";

    /// <summary>The game types a search asks for, in its own order of preference; empty when it set none.</summary>
    public static IReadOnlyList<int> Preferences(byte[] payload)
    {
        var criteria = TdfReader.FindGenericMap(payload, "SCNA").ToDictionary(StringComparer.OrdinalIgnoreCase);
        var types = new List<int>();
        foreach (var key in PreferenceKeys)
        {
            if (criteria.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var type)
                && type is >= 0 and <= MaxGameType && !types.Contains(type))
            {
                types.Add(type);
            }
        }
        return types;
    }

    /// <summary>
    /// The game type two searches play: the waiting player's first preference the newcomer also chose; with one side
    /// asking for none, the other side's first. Null when they share none (they are not paired), or when neither asked for
    /// one (paired, and no type is published, so each client keeps the one it searched with).
    /// </summary>
    public static (bool Compatible, int? GameType) Agree(IReadOnlyList<int> waiting, IReadOnlyList<int> newest)
    {
        if (waiting.Count == 0 || newest.Count == 0)
        {
            var only = waiting.Count > 0 ? waiting : newest;
            return (true, only.Count > 0 ? only[0] : null);
        }
        foreach (var type in waiting)
        {
            if (newest.Contains(type))
            {
                return (true, type);
            }
        }
        return (false, null);
    }
}
