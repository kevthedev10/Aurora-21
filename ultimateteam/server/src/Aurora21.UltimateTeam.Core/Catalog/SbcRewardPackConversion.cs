using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>
/// Start-up conversion, once (2026-10-04): SBC PACK rewards used to name their pack ("gold", "player_pick", a legacy number)
/// and rules/sbc.json mapped the name to a pack id (rewardPacks, legacyRewardPacks, defaultRewardPack). Now every reward
/// names its pack by id. It runs while the stored rules still hold rewardPacks, and resolves each name exactly as the
/// server did, through that database's stored mapping:
/// <list type="bullet">
/// <item>Each panel edit (sbc_sets row) gets the packId its names paid.</item>
/// <item>A bundled set without an edit already names ids in sbc.json (the shipped mapping). Where the stored mapping paid a
/// different pack, the set gets an edit row with the pack it paid, from its old names (Resources/Upgrades/sbc-reward-pack-names.json).</item>
/// <item>The mapping rules are deleted; the bundled file no longer has them, so they are not filled back.</item>
/// </list>
/// </summary>
public static partial class SbcRewardPackConversion
{
    private static readonly string[] RetiredRules = ["rewardPacks", "legacyRewardPacks", "defaultRewardPack", "_rewardPacksComment"];

    public static async ValueTask<string?> FillAsync(IUnitOfWork work, string dataRoot, ILogger log)
    {
        var documents = await RuleRows.DocumentsAsync(work);
        if (!documents.TryGetValue("sbc", out var text) || JsonNode.Parse(text) is not JsonObject rules || rules["rewardPacks"] is not JsonObject)
        {
            return null;
        }
        var mapping = new Mapping(rules, (await StorePackCatalog.LoadAsync(work)).Rules);
        var rows = await SbcSetRows.ReadAsync(work);
        var edited = new List<string>();
        foreach (var row in rows)
        {
            var definition = (JsonObject)row.Definition.DeepClone();
            if (ConvertNames(definition, row.Key, mapping))
            {
                await SbcSetRows.WriteAsync(work, row.Key, definition);
                edited.Add(row.Key);
            }
        }

        var bundled = (JsonNode.Parse(File.ReadAllText(Path.Combine(dataRoot, "sbc.json"))) as JsonObject)?.Arr("sets") ?? new JsonArray();
        var withRow = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        var repointed = new List<string>();
        foreach (var group in OldNames().Where(name => !withRow.Contains(name.Set)).GroupBy(name => name.Set))
        {
            if (bundled.OfType<JsonObject>().FirstOrDefault(set => JsonValues.Text(set.Get("id")) == group.Key) is not { } set)
            {
                continue;
            }
            var definition = (JsonObject)set.DeepClone();
            var changed = false;
            foreach (var name in group)
            {
                var source = name.Challenge is null ? definition
                    : (definition.Arr("challenges") ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(challenge => JsonValues.Text(challenge.Get("id")) == name.Challenge);
                if (source?.Obj("rewards")?.Arr("entries") is not { } entries || name.Entry >= entries.Count || entries[name.Entry]?.AsObject().Obj("attributes") is not { } attributes)
                {
                    continue;
                }
                var paid = mapping.Resolve(JsonValue.Create(name.Pack), group.Key);
                if (attributes.Int("packId") != paid)
                {
                    attributes["packId"] = paid;
                    changed = true;
                }
            }
            if (changed)
            {
                await SbcSetRows.WriteAsync(work, group.Key, definition);
                repointed.Add(group.Key);
            }
        }

        await work.ExecuteAsync("DELETE FROM ultimateteam.content_rules WHERE file = 'sbc' AND path->>0 = ANY(@keys)", ("keys", RetiredRules));
        var done = $"named every SBC reward pack by id: {edited.Count} panel edit(s) converted" +
            (edited.Count > 0 ? $" ({string.Join(", ", edited)})" : "") +
            (repointed.Count > 0 ? $"; {repointed.Count} bundled set(s) kept the pack the stored mapping paid ({string.Join(", ", repointed)})" : "");
        log.LogInformation("SBC {Done}", done);
        return done;
    }

    /// <summary>Gives every PACK reward without a packId the pack its name paid; true when anything changed.</summary>
    private static bool ConvertNames(JsonObject set, string setKey, Mapping mapping)
    {
        var changed = false;
        var sources = (set.Arr("challenges") ?? new JsonArray()).OfType<JsonObject>().Prepend(set);
        foreach (var entry in sources.SelectMany(source => (source.Obj("rewards")?.Arr("entries") ?? new JsonArray()).OfType<JsonObject>()))
        {
            if (!JsonValues.Text(entry.Get("type")).Equals("PACK", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var attributes = entry.Obj("attributes") ?? (JsonObject)(entry["attributes"] = new JsonObject());
            if (attributes.Int("packId") > 0)
            {
                continue;
            }
            // As the server granted it: the pack name, else the attributes' type, else "gold".
            attributes["packId"] = mapping.Resolve(attributes.GetOr("pack", attributes.GetOr("type", JsonValue.Create("gold"))), setKey);
            attributes.Remove("pack");
            changed = true;
        }
        return changed;
    }

    private sealed record OldName(string Set, string? Challenge, int Entry, string Pack);

    private static IEnumerable<OldName> OldNames()
    {
        using var stream = typeof(SbcRewardPackConversion).Assembly.GetManifestResourceStream("Upgrades/sbc-reward-pack-names.json")
            ?? throw new InvalidOperationException("Missing resource Upgrades/sbc-reward-pack-names.json.");
        var document = JsonNode.Parse(stream) as JsonObject ?? throw new InvalidDataException("sbc-reward-pack-names.json must contain an object.");
        return (document.Arr("rewards") ?? new JsonArray()).OfType<JsonObject>()
            .Select(row => new OldName(row.Text("set"), row.Get("challenge") is { } challenge ? JsonValues.Text(challenge) : null, (int)row.Int("entry"), row.Text("pack")))
            .ToList();
    }

    /// <summary>The retired name-to-pack resolution, from a database's stored rules/sbc.json.</summary>
    private sealed class Mapping(JsonObject rules, PackRules packs)
    {
        private readonly JsonObject _named = rules.Obj("rewardPacks") ?? new JsonObject();
        private readonly JsonObject _legacy = rules.Obj("legacyRewardPacks") ?? new JsonObject();
        private readonly long _default = rules.Int("defaultRewardPack");

        public long Resolve(JsonNode? name, string setKey)
        {
            var text = JsonValues.Text(name, "None");
            // A player pick on a set whose key names a rating (UP_84, ..._86+) paid that rating's pick.
            if (text.Equals("player_pick", StringComparison.OrdinalIgnoreCase)
                && UpgradeRatingPattern().Matches(setKey)
                    .Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                    .FirstOrDefault(rating => packs.PlayerPickPacks.ContainsKey(rating)) is var rating and > 0)
            {
                return packs.PlayerPickPack(rating);
            }
            if (JsonValues.IsString(name) && _named[text] is { } named && JsonValues.TryInt(named, out var mapped))
            {
                return mapped;
            }
            var digits = text.Length > 0 && text.All(char.IsAsciiDigit) ? text : null;
            return digits is not null && _legacy[digits] is { } legacy && JsonValues.TryInt(legacy, out var legacyMapped) ? legacyMapped : _default;
        }
    }

    [GeneratedRegex(@"(?:^|_)(\d{2,3})(?=\+|_|$)")]
    private static partial Regex UpgradeRatingPattern();
}
