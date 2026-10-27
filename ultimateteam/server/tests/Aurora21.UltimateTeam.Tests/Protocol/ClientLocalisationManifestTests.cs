using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Server.Http;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

public sealed class ClientLocalisationManifestTests
{
    [Fact]
    public void CombinedManifestPreservesPlaceholdersAndInvalidatesLeaderboardOnlyCache()
    {
        var manifest = LeaderboardUiManifest.Build(GameRules.Load(TestRuntime.DataRoot));
        var languages = manifest["languages"]!.AsArray().Select(n => n!.GetValue<string>()).Order().ToArray();
        var edits = manifest["textEdits"]!.AsArray().OfType<JsonObject>().ToDictionary(e => e["stringId"]!.GetValue<string>());
        var addition = Assert.Single(manifest["textAdditions"]!.AsArray());
        Assert.Equal("FUT_STORE_CAT_KITS_NAME", addition!["stringId"]!.GetValue<string>());
        Assert.Equal("TOKENS", addition["target"]!.GetValue<string>());
        Assert.NotEmpty(edits);
        foreach (var edit in edits.Values)
        {
            var translations = edit["languages"]!.AsObject();
            Assert.Equal(languages, translations.Select(e => e.Key).Order());
            foreach (var (_, translation) in translations)
            {
                var source = translation!["source"]!.GetValue<string>();
                var target = translation["target"]!.GetValue<string>();
                Assert.NotEqual(source, target);
                Assert.Contains("aurora tokens", target.Replace("\r", "").Replace("\n", " ").ToLowerInvariant());
                Assert.Equal(Regex.Matches(source, @"%[0-9]*[A-Za-z]").Select(m => m.Value), Regex.Matches(target, @"%[0-9]*[A-Za-z]").Select(m => m.Value));
                Assert.Equal(Regex.Matches(source, @"\r\n|\r|\n").Select(m => m.Value), Regex.Matches(target, @"\r\n|\r|\n").Select(m => m.Value));
            }
        }
        Assert.Equal("Aurora\nTokens", edits["FUT_GH_FIFAPOINTS_0"]["languages"]!["eng_us"]!["target"]!.GetValue<string>());
        Assert.Equal("%1d Aurora Tokens", edits["FUT_DRAFT_POINTS_COST"]["languages"]!["eng_us"]!["target"]!.GetValue<string>());
        Assert.Equal("%1s (AURORA TOKENS)", edits["FUT_STORE_PURCHASE_POPUP_POINT_COST"]["languages"]!["eng_us"]!["target"]!.GetValue<string>());
        Assert.DoesNotContain("FUT_Champions_TopX_upper", edits.Keys);
        Assert.DoesNotContain("FUT_SQUADBATTLE_TOP100_TAB", edits.Keys);
        var revision = manifest["revision"]!.GetValue<string>();
        manifest.Remove("revision");
        static string Hash(JsonObject value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString()))).ToLowerInvariant();
        Assert.Equal(revision, Hash(manifest));
        manifest.Remove("textEdits");
        Assert.NotEqual(revision, Hash(manifest));
    }
}
