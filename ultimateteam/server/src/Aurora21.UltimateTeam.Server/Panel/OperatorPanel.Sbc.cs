using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Microsoft.AspNetCore.Http;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// The panel's SBC tab: the catalogue (content area <c>sbc_set</c>), every set bundled or added here, and the live list
/// (<c>sbc_live</c>, migration 142) of the sets the game shows, with what the editor offers — the game's SBC tabs, the badge
/// images, the requirement types and the packs a reward may name. Edits go through the content changeset like every other
/// content area.
/// </summary>
public sealed partial class OperatorPanel
{
    private async Task<(int, object)?> SbcAsync(ApiRequest request)
    {
        if (request.Parts is [_, "preview"] && request.Method == "POST") return (200, SbcPreview(request.Body));
        if (request.Parts.Length != 1 || request.Method != "GET") return null;
        var (rows, listing) = await game.UnitOfWork.RunReadAsync(0, async work => (await SbcSetRows.ReadAsync(work), await SbcLiveRows.ReadAsync(work)), request.Aborted);
        var edits = rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var live = listing.ToDictionary(entry => entry.Key, entry => entry.Enabled, StringComparer.Ordinal);
        var ids = game.Sbc.Sets.ToDictionary(set => set.Key, set => set.Id, StringComparer.Ordinal);
        var rules = game.Rules.Sbc;
        var sets = new List<object>();
        object Set(string key, JsonObject definition, string origin, bool edited) => new
        {
            key,
            setId = ids.TryGetValue(key, out var id) ? id : (int?)null,
            origin,
            edited,
            listed = live.ContainsKey(key),
            enabled = live.GetValueOrDefault(key),
            definition,
        };
        foreach (var bundled in (game.Sbc.Bundled.Arr("sets") ?? new JsonArray()).OfType<JsonObject>())
        {
            var key = JsonValues.Text(bundled.Get("id"));
            var edit = edits.GetValueOrDefault(key);
            sets.Add(Set(key, edit?.Definition ?? bundled, "bundled", edit is not null));
        }
        foreach (var row in rows.Where(row => game.Sbc.BundledSet(row.Key) is null))
            sets.Add(Set(row.Key, row.Definition, "panel", true));

        var area = new SbcSetArea(game);
        var requirements = new List<object> { new { type = "PLAYERS", bound = "count", label = "Players in squad: {value}", exact = false, qualities = Array.Empty<string>() } };
        requirements.AddRange(rules.Requirements
            .Where(entry => !rules.IgnoredRequirements.Contains(entry.Key))
            .Select(entry => (object)new
            {
                type = entry.Key,
                bound = entry.Value.Bound.ToString().ToLowerInvariant(),
                label = entry.Value.Label,
                exact = entry.Value.EligibilityKey is not null && entry.Value.Bound != SbcBound.Value,
                selector = entry.Value.Selector,
                qualities = entry.Value.Qualities.Keys.ToArray(),
            }));
        var packs = RewardPackOptions();
        return (200, new
        {
            tabs = (game.Sbc.Root.Arr("tabs") ?? new JsonArray()).OfType<JsonObject>().Select(tab => new { id = tab.Int("id"), name = tab.Text("name") }),
            badges = area.Badges(),
            requirements,
            packs,
            sets,
            live = SbcLiveArea.Listing(listing),
            // SBC → Settings: rules/sbc.json, edited as rules changes of this file. Every requirement type, with what the
            // client is sent (fixed) and the label the panel may change; the formations a default may name.
            settingsFile = "sbc",
            settings = new
            {
                formation = rules.Formation,
                repeatableRewardsUntradeable = rules.RepeatableRewardsUntradeable,
                playerPickRewardName = rules.PlayerPickRewardName,
                ignoredRequirements = rules.IgnoredRequirements.Order(StringComparer.Ordinal),
                text = new
                {
                    requirementSeparator = rules.Text.RequirementSeparator, noRequirements = rules.Text.NoRequirements,
                    setDescription = rules.Text.SetDescription, unnamedSetDescription = rules.Text.UnnamedSetDescription,
                    challenge = rules.Text.Challenge, challenges = rules.Text.Challenges, unknownChallenge = rules.Text.UnknownChallenge,
                    shortSquad = rules.Text.ShortSquad,
                },
                images = new
                {
                    imageIdBase = rules.Images.ImageIdBase, artFolder = rules.Images.ArtFolder,
                    fallbackTabId = rules.Images.FallbackTabId, cacheSeconds = rules.Images.CacheSeconds,
                },
                squadSize = rules.SquadSize,
                brickItemIdBase = rules.BrickItemIdBase,
            },
            requirementRules = rules.Requirements.Select(entry => new
            {
                type = entry.Key,
                bound = entry.Value.Bound.ToString().ToLowerInvariant(),
                eligibilityKey = entry.Value.EligibilityKey?.ToString(),
                eligibilityValue = entry.Value.EligibilityValue,
                scope = entry.Value.EligibilityKey is null ? null : entry.Value.Scope.ToString(),
                playerCount = entry.Value.PlayerCount,
                label = entry.Value.Label,
                exact = entry.Value.Exact is { } exact ? new { scope = exact.Scope.ToString(), label = exact.Label } : null,
                qualities = entry.Value.Qualities,
            }),
            // Each formation's slots in squad order (slot 0 is the goalkeeper) as position ids into positions: the
            // editor's pitch, where locked slots (bricks) are placed.
            formations = game.Chemistry.Formations.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new { id = entry.Key, slots = entry.Value }),
            positions = game.Chemistry.Positions,
            squadSize = rules.SquadSize,
            managerLeagues = game.Cards.ManagerLeagueCards.Select(card => new { league = card.LeagueId, name = card.Name, resourceId = card.ResourceId }),
        });
    }

    /// <summary>
    /// The editor's live preview of an unsaved set ({setKey, definition}): why staging it would be refused (or null), and the
    /// text FIFA would show, from the same code the game uses (SbcText): the set description and each challenge's
    /// requirement line and squad size.
    /// </summary>
    private object SbcPreview(JsonElement body)
    {
        var request = body.ValueKind == JsonValueKind.Object ? JsonNode.Parse(body.GetRawText()) as JsonObject : null;
        if (request?["definition"] is not JsonObject definition) throw new PanelError(400, "A preview is {setKey, definition}.");
        var setKey = request["setKey"] is JsonValue key && key.TryGetValue(out string? text) && SbcSetArea.IsSetKey(text) ? text : "NEW_SBC";
        var rules = game.Rules.Sbc;
        return new
        {
            problem = new SbcSetArea(game).Problem(setKey, definition),
            description = SbcText.SetDescription(rules, definition),
            challenges = (definition["challenges"] as JsonArray ?? []).OfType<JsonObject>().Select(challenge => new
            {
                description = SbcText.Describe(rules, challenge),
                players = SbcText.RequiredPlayers(rules, challenge),
            }),
        };
    }

    /// <summary>
    /// Every pack a reward may name (SBCs, objectives, the Season Pass), by id: its name, size and whether the store sells it,
    /// and whether it is a player pick, random (drawn by its filters) or fixed (its groups list the cards it offers).
    /// </summary>
    private List<object> RewardPackOptions() => (game.Packs.Load().Obj("packs") ?? new JsonObject())
        .Select(entry => (Id: long.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0, Config: entry.Value as JsonObject))
        .Where(pack => pack.Id > 0 && pack.Config is not null)
        .OrderBy(pack => pack.Id)
        .Select(pack => (pack.Id, Config: pack.Config!))
        .Select(pack => (object)new
        {
            id = pack.Id, name = pack.Config.Text("name"), size = pack.Config.Int("size"), enabled = pack.Config.Bool("enabled", true),
            pick = pack.Config.Arr("player_picks") is { Count: > 0 } || pack.Config.Int("player_pick_count") > 0,
            @fixed = (pack.Config.Arr("player_picks") ?? []).OfType<JsonObject>().Any(group => group.Arr("choices") is { Count: > 0 }),
        })
        .ToList();

    /// <summary>A badge image from the SBC art folder, for the editor's picker and previews.</summary>
    private async Task SbcBadgeAsync(HttpContext context, string name)
    {
        var folder = Path.GetFullPath(Path.Combine(game.Paths.DataRoot, game.Rules.Sbc.Images.ArtFolder));
        var path = Path.GetFullPath(Path.Combine(folder, name + ".png"));
        if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            await Json(context, 404, new { error = "not found" });
            return;
        }
        var bytes = await File.ReadAllBytesAsync(path, context.RequestAborted);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.CacheControl = "private, max-age=3600";
        Security(context.Response);
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    [GeneratedRegex(@"^/panel/sbc-badges/([A-Za-z0-9_-]{1,80})\.png$")]
    private static partial Regex PanelSbcBadge();
}
