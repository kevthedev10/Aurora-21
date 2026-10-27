using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// SBC reward packs are named by id (2026-10-04): the bundled sbc.json names ids, and a database whose stored rules still
/// hold the old pack-name mapping is converted once at start-up (SbcRewardPackConversion), paying what it paid before.
/// </summary>
public sealed class SbcRewardPackConversionTests
{
    /// <summary>The shipped rules/sbc.json pack-name mapping before the conversion, as stored rule rows.</summary>
    public static readonly IReadOnlyDictionary<string, long> ShippedRewardPacks = new Dictionary<string, long>
    {
        ["bronze"] = 100, ["silver"] = 200, ["premium_silver_players"] = 201, ["gold"] = 304, ["premium_gold"] = 305, ["gold_players"] = 304,
        ["premium_gold_players"] = 305, ["electrum_players"] = 306, ["prime_electrum_players"] = 307, ["prime_gold_players"] = 308,
        ["prime_mixed_players"] = 309, ["rare_players"] = 401, ["rare_mixed_players"] = 406, ["rare_gold"] = 400, ["jumbo_rare_players"] = 402,
        ["mega"] = 403, ["rare_mega"] = 404, ["ultimate"] = 405, ["player_pick"] = 513,
        ["two_rare_bronze_players"] = 80, ["two_rare_silver_players"] = 81, ["two_rare_gold_players"] = 82,
    };

    /// <summary>SQL that puts the old mapping back into the stored sbc rules, with some names pointing elsewhere.</summary>
    public static string OldMappingSql(IReadOnlyDictionary<string, long>? changed = null)
    {
        var rows = ShippedRewardPacks.Select(pair => (Path: $"[\"rewardPacks\", \"{pair.Key}\"]",
                Value: (changed is not null && changed.TryGetValue(pair.Key, out var value) ? value : pair.Value).ToString(CultureInfo.InvariantCulture)))
            .Concat(Enumerable.Range(1, 14).Select(key => (Path: $"[\"legacyRewardPacks\", \"{key}\"]", Value: "304")))
            .Append((Path: "[\"defaultRewardPack\"]", Value: "304"))
            .Append((Path: "[\"_rewardPacksComment\"]", Value: "\"old\""));
        return "INSERT INTO ultimateteam.content_rules(file, path, value, ordinal) VALUES " +
            string.Join(", ", rows.Select((row, index) => $"('sbc', '{row.Path}', '{row.Value}', {9000 + index})")) +
            " ON CONFLICT (file, path) DO UPDATE SET value = excluded.value;";
    }

    private static IEnumerable<long> PackIds(JsonObject set) =>
        (set.Arr("challenges") ?? new JsonArray()).OfType<JsonObject>().Prepend(set)
            .SelectMany(source => (source.Obj("rewards")?.Arr("entries") ?? new JsonArray()).OfType<JsonObject>())
            .Where(entry => entry.Text("type") == "PACK").Select(entry => entry.Obj("attributes")!.Int("packId"));

    [Fact]
    public void EveryBundledPackRewardNamesAnExistingPackById()
    {
        using var rt = TestRuntime.Create();
        var sets = rt.Game.Sbc.Sets;
        // 118 challenge packs and the six Foundations group packs (#115).
        Assert.Equal(124, sets.Sum(set => PackIds(set.Source).Count()));
        Assert.All(sets.SelectMany(set => PackIds(set.Source)), packId => Assert.NotEmpty(rt.Game.Packs.GetPackConfig(packId)));
        // A player pick upgrade pays its own rating's pick; the Upgrades pay their two-rare packs.
        foreach (var (key, pick) in new[] { ("UP_82", 513L), ("UP_84", 514L), ("UP_86", 515L), ("UP_88", 516L), ("UPG_BRONZE", 80L), ("UPG_SILVER", 81L), ("UPG_GOLD", 82L) })
            Assert.Equal([pick], PackIds(rt.Game.Sbc.FindSet(key)!.Source));
        var rules = JsonNode.Parse(rt.Game.InUnit(async work => (await RuleRows.DocumentsAsync(work))["sbc"]))!.AsObject();
        Assert.DoesNotContain(rules, rule => rule.Key is "rewardPacks" or "legacyRewardPacks" or "defaultRewardPack" or "_rewardPacksComment");
    }

    [Fact]
    public void AnUpgradeConvertsPanelEditsAndKeepsWhatAnEditedMappingPaid()
    {
        var rt = TestRuntime.Create();
        // An operator's panel edit still naming packs (one by name, one by a legacy number, one player pick), and a stored
        // mapping where gold pays 305.
        var edit = (JsonObject)rt.Game.Sbc.BundledSet("UP_84")!.DeepClone();
        var entries = edit["challenges"]![0]!["rewards"]!["entries"]!.AsArray();
        entries[0]!["attributes"] = new JsonObject { ["pack"] = "player_pick", ["count"] = 1, ["name"] = "Player Pick" };
        entries.Add(new JsonObject { ["type"] = "PACK", ["attributes"] = new JsonObject { ["pack"] = "rare_players", ["count"] = 1 } });
        entries.Add(new JsonObject { ["type"] = "PACK", ["attributes"] = new JsonObject { ["pack"] = 6, ["count"] = 1 } });
        rt.Game.InUnit(work => work.ExecuteAsync(OldMappingSql(new Dictionary<string, long> { ["gold"] = 305 }) + """
            INSERT INTO ultimateteam.sbc_sets(set_key, definition) VALUES ('UP_84', @edit::jsonb);
            """, ("edit", edit.ToJsonString())));
        using var upgraded = TestRuntime.Create(connectionString: rt.Detach());

        Assert.Equal([514L, 401L, 304L], PackIds(upgraded.Game.Sbc.FindSet("UP_84")!.Source));
        Assert.DoesNotContain(upgraded.Game.Sbc.FindSet("UP_84")!.Source.ToJsonString(), "\"pack\":", StringComparison.Ordinal);
        // FND_LETS_MOVE_FORWARD's challenges named only gold: under the stored mapping they paid 305, so they keep 305
        // through an edit row. The set's own group pack (82) was always named by id.
        var moveForward = upgraded.Game.Sbc.FindSet("FND_LETS_MOVE_FORWARD")!.Source;
        Assert.All(PackIds(moveForward).Skip(1), packId => Assert.Equal(305L, packId));
        Assert.Equal(82L, PackIds(moveForward).First());
        Assert.Equal(1L, upgraded.Game.InUnit(work => work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.sbc_sets WHERE set_key = 'FND_LETS_MOVE_FORWARD'")));
        // A set whose names all paid as shipped is left unedited.
        Assert.Equal(0L, upgraded.Game.InUnit(work => work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.sbc_sets WHERE set_key = 'UPG_BRONZE'")));
        Assert.Equal(0L, upgraded.Game.InUnit(work => work.ScalarAsync<long>(
            "SELECT count(*) FROM ultimateteam.content_rules WHERE file = 'sbc' AND path->>0 IN ('rewardPacks', 'legacyRewardPacks', 'defaultRewardPack', '_rewardPacksComment')")));
    }

    [Fact]
    public async Task APackRewardWithoutAPackIdIsRefused()
    {
        using var rt = TestRuntime.Create();
        var set = (JsonObject)rt.Game.Sbc.BundledSet("UPG_BRONZE")!.DeepClone();
        set["challenges"]![0]!["rewards"]!["entries"]![0]!["attributes"] = new JsonObject { ["pack"] = "gold", ["count"] = 1 };
        var changes = new ContentChangesets(rt.Game, [new SbcSetArea(rt.Game)]);
        var error = await Assert.ThrowsAsync<ContentChangeException>(() => changes.SetChangeAsync(SbcSetArea.AreaName,
            new JsonObject { ["setKey"] = "UPG_BRONZE" }, ContentOperation.Update, new JsonObject { ["definition"] = set }, null));
        Assert.Equal(400, error.Status);
        Assert.Contains("packId", error.Message, StringComparison.Ordinal);
    }
}
