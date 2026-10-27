using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>Compatibility metadata for existing player consumers; typed columns remain authoritative.</summary>
internal static class RelationalPlayerProjection
{
    internal static JsonObject Project(ItemDefinitionRecord definition, PlayerCardDefinitionRecord player,
        CardTypeRecord type, FootballerRecord footballer, DefinitionExtensionRecord? extension)
    {
        var payload = extension is null ? null : JsonNode.Parse(extension.Payload.Text) as JsonObject;
        var result = payload?["compatibilityMetadata"]?.DeepClone() as JsonObject ?? new JsonObject
        {
            ["firstName"] = footballer.FirstName,
            ["lastName"] = footballer.LastName,
            ["commonName"] = footballer.CommonName,
        };
        result["name"] = definition.DisplayName;
        result["resourceId"] = definition.NativeResourceId?.Value;
        result["assetId"] = definition.NativeAssetId;
        result["rating"] = player.Rating;
        result["preferredPosition"] = player.Position;
        result["preferredFoot"] = player.PreferredFoot;
        result["weakFoot"] = player.WeakFoot;
        result["skillMoves"] = player.SkillMoves;
        result["workRateAttack"] = player.WorkRateAttack;
        result["workRateDefense"] = player.WorkRateDefense;
        result["leagueId"] = player.NativeLeagueId;
        result["teamid"] = player.NativeTeamId;
        result["nation"] = player.NativeNationId;
        result["cardsubtypeid"] = definition.NativeSubtype;
        result["rareflag"] = type.NativeRareflag;
        result["rarityName"] = type.Name;
        result["tier"] = type.CardLevel;
        result["special"] = type.IsSpecial;
        if (player.NativeTrait1.HasValue) result["trait1"] = player.NativeTrait1.Value;
        if (player.NativeTrait2.HasValue) result["trait2"] = player.NativeTrait2.Value;
        if (player.NativeVersion.HasValue) result["version"] = player.NativeVersion.Value;
        // A head the operator picked in the card editor (migration 074); a card without one keeps its own.
        if (player.HeadResourceId.HasValue)
        {
            result["headResourceId"] = player.HeadResourceId.Value;
            result["dynamicImageResourceId"] = player.HeadResourceId.Value;
        }
        if (player.CustomHeadId.HasValue) result["customHeadId"] = player.CustomHeadId.Value;
        // Every player response exposes both stat groups; typed columns supply their values.
        result["face"] = new JsonArray(new long?[] { player.FaceSlot0, player.FaceSlot1, player.FaceSlot2,
            player.FaceSlot3, player.FaceSlot4, player.FaceSlot5 }.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        var values = new (string Name, long? Value)[]
        {
            ("acceleration", player.Acceleration), ("sprintSpeed", player.SprintSpeed),
            ("positioning", player.Positioning), ("finishing", player.Finishing), ("shotPower", player.ShotPower),
            ("longShots", player.LongShots), ("volleys", player.Volleys), ("penalties", player.Penalties),
            ("vision", player.Vision), ("crossing", player.Crossing), ("freeKickAccuracy", player.FreeKickAccuracy),
            ("shortPassing", player.ShortPassing), ("longPassing", player.LongPassing), ("curve", player.Curve),
            ("agility", player.Agility), ("balance", player.Balance), ("reactions", player.Reactions),
            ("ballControl", player.BallControl), ("dribbling", player.Dribbling), ("composure", player.Composure),
            ("interceptions", player.Interceptions), ("headingAccuracy", player.HeadingAccuracy), ("marking", player.Marking),
            ("standingTackle", player.StandingTackle), ("slidingTackle", player.SlidingTackle),
            ("jumping", player.Jumping), ("stamina", player.Stamina), ("strength", player.Strength), ("aggression", player.Aggression),
            ("gkDiving", player.GkDiving), ("gkHandling", player.GkHandling), ("gkKicking", player.GkKicking),
            ("gkReflexes", player.GkReflexes), ("gkSpeed", player.GkSpeed), ("gkPositioning", player.GkPositioning),
        };
        result["attributes"] = new JsonObject(values.Where(v => v.Value.HasValue)
            .Select(v => new KeyValuePair<string, JsonNode?>(v.Name, JsonValue.Create(v.Value!.Value))));
        return result;
    }
}
