using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>Published leaderboard limits and exact client localisation edits; revision changes only with this manifest.</summary>
public static class LeaderboardUiManifest
{
    public const string Path = "/client/ui/leaderboards.json";
    private static readonly JsonObject Definition = Read();

    public static JsonObject Build(GameRules rules)
    {
        var result = (JsonObject)Definition.DeepClone();
        var captions = result["captions"]!.AsArray();
        captions[0]!["top"] = rules.Seasons.WeekendLeague.TopX;
        captions[1]!["top"] = rules.SquadBattles.Competition.BoardSize;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(result.ToJsonString()));
        result["revision"] = Convert.ToHexString(hash).ToLowerInvariant();
        return result;
    }

    private static JsonObject Read()
    {
        using var stream = typeof(LeaderboardUiManifest).Assembly.GetManifestResourceStream("client/leaderboard-ui.json")
            ?? throw new InvalidDataException("Missing leaderboard client manifest resource.");
        using var reader = new StreamReader(stream);
        var definition = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        using var currencyStream = typeof(LeaderboardUiManifest).Assembly.GetManifestResourceStream("client/aurora-tokens.json")
            ?? throw new InvalidDataException("Missing Aurora Tokens client manifest resource.");
        using var currencyReader = new StreamReader(currencyStream);
        definition["textEdits"] = JsonNode.Parse(currencyReader.ReadToEnd())!.AsArray();
        using var storeStream = typeof(LeaderboardUiManifest).Assembly.GetManifestResourceStream("client/tokens-store.json")
            ?? throw new InvalidDataException("Missing Tokens Store client manifest resource.");
        using var storeReader = new StreamReader(storeStream);
        definition["textAdditions"] = JsonNode.Parse(storeReader.ReadToEnd())!.AsArray();
        return definition;
    }
}
