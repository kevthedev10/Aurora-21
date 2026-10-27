using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Server.Lsx;

public sealed class LsxChallengeSettings
{
    public required string Version { get; init; }
    public required string Build { get; init; }
}

/// <summary>One LSX element: its name and attributes in document order (values may hold placeholders).</summary>
public sealed class LsxElement
{
    public required string Element { get; init; }
    public JsonObject Attributes { get; init; } = new();
}

/// <summary>Resources/lsx.json: the local Origin LSX answers.</summary>
public sealed class LsxSettings
{
    public required LsxChallengeSettings Challenge { get; init; }
    public required LsxElement LoginEvent { get; init; }
    public required List<string[]> Services { get; init; }
    public required Dictionary<string, string> Settings { get; init; }
    public required Dictionary<string, string> GameInfo { get; init; }
    public required Dictionary<string, LsxElement> Responses { get; init; }
    public required LsxElement UnmodelledResponse { get; init; }

    public static LsxSettings Load() =>
        RulesJson.Deserialize<LsxSettings>(EmbeddedResources.ReadText(typeof(LsxSettings).Assembly, "lsx.json"), "lsx.json");
}
