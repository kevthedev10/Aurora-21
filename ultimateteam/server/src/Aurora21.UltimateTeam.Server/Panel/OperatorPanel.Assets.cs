using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Catalog;
using Microsoft.AspNetCore.Http;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Panel images: card frames, player and special-card art, club badges, league logos and nation flags, downloaded once
/// with ultimateteam/download-card-images.py into the assets folder (the runtime's panel-assets folder, or
/// --panel-assets-dir; on the VPS a read-only mount) and served as-is under /panel/assets/. Like the rest of the panel they
/// are only served on its private loopback port, to operators.
/// </summary>
public sealed partial class OperatorPanel
{
    /// <summary>The image folders the download script writes, one per category.</summary>
    private static readonly string[] AssetCategories = ["players", "dynamic", "clubs", "leagues", "nations", "cards/hd"];

    // A category folder and a plain PNG file name; nothing else reaches the file system.
    [GeneratedRegex(@"^/panel/assets/(players|dynamic|clubs|leagues|nations|cards/hd)/([A-Za-z0-9][A-Za-z0-9_-]{0,63}\.png)$")]
    private static partial Regex PanelImage();
    private static readonly string[] BundledFrames = ["10_pro_player.png", "18_champions_gold.png"];

    // PNG sidecars are decoded from the exact DDS that FIFA receives, not downloaded substitute portraits.
    private string? CustomPortraitPath(long resource)
    {
        var folder = game.Paths.CustomPlayerHeadsDirectory;
        var preview = Path.Combine(folder, $"p{resource}.png");
        return File.Exists(Path.Combine(folder, $"p{resource}.dds")) && File.Exists(preview) ? preview : null;
    }

    private async Task ImageAsync(HttpContext context, string category, string file)
    {
        var root = options.AssetsDirectory;
        var path = root is null ? null : Path.GetFullPath(Path.Combine(root, category, file));
        if (category == "dynamic" && file.StartsWith('p') && long.TryParse(file.AsSpan(1, file.Length - 5), out var resource)
            && game.Players.Find(resource) is not null && CustomPortraitPath(resource) is { } custom)
        {
            var preview = await File.ReadAllBytesAsync(custom, context.RequestAborted);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "image/png";
            context.Response.ContentLength = preview.Length;
            context.Response.Headers.CacheControl = "private, max-age=604800";
            Security(context.Response);
            await context.Response.Body.WriteAsync(preview, context.RequestAborted);
            return;
        }
        if (path is null || !path.StartsWith(Path.GetFullPath(root!) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            if (category == "cards/hd" && BundledFrames.Contains(file, StringComparer.Ordinal))
            {
                await using var image = typeof(OperatorPanel).Assembly.GetManifestResourceStream($"panel/assets/cards/hd/{file}");
                if (image is not null)
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "image/png";
                    context.Response.ContentLength = image.Length;
                    Security(context.Response);
                    await image.CopyToAsync(context.Response.Body, context.RequestAborted);
                    return;
                }
            }
            await Json(context, 404, new { error = "not found" });
            return;
        }
        var bytes = await File.ReadAllBytesAsync(path, context.RequestAborted);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        context.Response.ContentLength = bytes.Length;
        // The art never changes once downloaded; the panel is private, so browsers may keep it.
        context.Response.Headers.CacheControl = "private, max-age=604800";
        Security(context.Response);
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    /// <summary>
    /// Whether images are installed, how many of each category, and the card-frame file names, so pages can pick a frame
    /// per card and fall back to text without images.
    /// </summary>
    private object AssetsReport()
    {
        var root = options.AssetsDirectory;
        string[] Files(string category)
        {
            var folder = root is null ? null : Path.Combine(root, category);
            return folder is not null && Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.png").Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToArray()
                : [];
        }
        var counts = AssetCategories.ToDictionary(category => category, category => category == "cards/hd" ? 0 : Files(category).Length);
        var frames = Files("cards/hd").Union(BundledFrames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        counts["cards/hd"] = frames.Length;
        return new
        {
            available = counts.Values.Any(count => count > 0),
            baseUrl = "/panel/assets/",
            categories = counts,
            frames,
        };
    }

    /// <summary>
    /// The ids a card's art is named by, for a player card of the catalogue (null for anything else): its player image
    /// (asset id), its special-card image (resource id, or a portrait override), its rarity for the frame, and the native
    /// club, nation and league ids of its badges.
    /// </summary>
    private object? CardArt(long? resourceId)
    {
        if (resourceId is not { } id || game.Players.Find(id) is not { } card) return null;
        var portrait = card.Int("dynamicImageResourceId");
        return new
        {
            resourceId = id,
            assetId = card.Int("assetId"),
            portraitId = portrait > 0 ? portrait : (long?)null,
            headResourceId = card.Int("headResourceId") > 0 ? card.Int("headResourceId") : (long?)null,
            defaultPortraitUrl = card.Int("headResourceId") == 0 && card.Int("customHeadId") == 0 && CustomPortraitPath(id) is not null
                ? $"/panel/assets/dynamic/p{id}.png" : null,
            customHeadId = card.Int("customHeadId") > 0 ? card.Int("customHeadId") : (long?)null,
            rareflag = card.Rareflag,
            rating = card.Rating,
            position = card.Position,
            skillMoves = card.Int("skillMoves"),
            weakFoot = card.Int("weakFoot"),
            workRateAttack = card.Text("workRateAttack"),
            workRateDefense = card.Text("workRateDefense"),
            name = card.Text("name"),
            cardName = PanelCardName(card),
            teamId = card.Int("teamid"),
            nationId = card.Int("nation"),
            leagueId = card.Int("leagueId"),
        };
    }

    private static string PanelCardName(PlayerMeta card)
    {
        var common = Regex.Replace(card.Text("commonName").Trim(), @"\s+Jr\.?$", "", RegexOptions.IgnoreCase);
        if (common.Length > 0) return common.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
        var surname = card.Text("lastName").Trim();
        return surname.Length > 0 ? surname : card.Text("name").Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
    }

    /// <summary>Pack preview items with their card art attached.</summary>
    private System.Text.Json.Nodes.JsonArray WithArt(System.Text.Json.Nodes.JsonArray items)
    {
        foreach (var item in items.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            if (item["itemType"]?.GetValue<string>() is { } kind && kind != "player") continue;
            var resource = item["resourceId"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out long id) ? id : (long?)null;
            if (CardArt(resource) is { } art) item["art"] = System.Text.Json.JsonSerializer.SerializeToNode(art);
        }
        return items;
    }
}
