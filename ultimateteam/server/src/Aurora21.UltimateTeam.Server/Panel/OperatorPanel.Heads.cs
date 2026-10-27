using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Player heads for the card editor (live-content-editing-plan.md, step 5): every head a player's card can show (the art of
/// each of the player's cards, and heads uploaded for that player) and uploading a new one. An upload is kept as an option
/// for that player (ultimateteam.custom_player_heads, migration 074); a card only shows it once an edit that picks it is saved.
/// </summary>
public sealed partial class OperatorPanel
{
    /// <summary>The largest upload body: the preview PNG and the 180 × 180 RGBA pixels, both base64 in JSON.</summary>
    private const int MaxHeadUploadBytes = 2 * 1024 * 1024;
    private const int MaxHeadPngBytes = 1024 * 1024;

    [GeneratedRegex(@"^/panel/heads/([1-9][0-9]{0,17})\.png$")]
    private static partial Regex PanelHead();

    private async Task<(int, object)?> HeadsAsync(ApiRequest request)
    {
        if (request.Parts is ["heads", "preview"])
        {
            if (request.Method != "POST") throw new PanelError(405, "method not allowed");
            var pixels = Base64(request.Body, "rgba");
            if (pixels is null || pixels.Length != HeadDds.RgbaBytes) throw new PanelError(400, "Expected 180 × 180 RGBA pixels.");
            return (200, new { png = Convert.ToBase64String(HeadDds.PreviewPng(HeadDds.FromRgba(pixels))) });
        }
        if (request.Parts is not ["heads"]) return null;
        if (request.Method != "POST") throw new PanelError(405, "method not allowed");
        var body = request.Body;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("assetId", out var assetNode) || !assetNode.TryGetInt64(out var asset) || asset < 1)
            throw new PanelError(400, "assetId must be the player's asset id.");
        if (!game.Players.Entries.Any(entry => entry.Value.Int("assetId") == asset))
            throw new PanelError(404, "No card has that player.");
        var label = body.TryGetProperty("label", out var labelNode) && labelNode.ValueKind == JsonValueKind.String ? labelNode.GetString()!.Trim() : "";
        if (label.Length is < 1 or > 80) throw new PanelError(400, "Give the image a label of 1 to 80 characters.");
        var png = Base64(body, "png") ?? throw new PanelError(400, "png must be the image as base64.");
        if (png.Length > MaxHeadPngBytes || png.Length < 8 || !png.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            throw new PanelError(400, "png must be a PNG image of at most 1 MB.");
        var rgba = Base64(body, "rgba") ?? throw new PanelError(400, "rgba must be the pixels as base64.");
        if (rgba.Length != HeadDds.RgbaBytes)
            throw new PanelError(400, $"rgba must be {HeadDds.Size} × {HeadDds.Size} RGBA pixels ({HeadDds.RgbaBytes} bytes).");
        var by = body.TryGetProperty("by", out var byNode) && byNode.ValueKind == JsonValueKind.String ? byNode.GetString()?.Trim() : null;
        var dds = HeadDds.FromRgba(rgba);
        png = HeadDds.PreviewPng(dds);
        var stored = await game.UnitOfWork.RunAsync(0, "panel-head-upload", async work => (await work.QueryAsync("""
            INSERT INTO ultimateteam.custom_player_heads(native_asset_id,label,png,dds,created_by) VALUES(@asset,@label,@png,@dds,@by)
            RETURNING head_id, created_at
            """, row => (Id: row.GetInt64(0), At: row.GetDateTime(1)),
            ("asset", asset), ("label", label), ("png", png), ("dds", dds), ("by", string.IsNullOrEmpty(by) ? DBNull.Value : by[..Math.Min(64, by.Length)]))).Single(),
            request.Aborted);
        _log.LogInformation("PANEL {Remote} uploaded head {Head} for player {Asset}", request.Remote, stored.Id, asset);
        return (200, new { headId = stored.Id, assetId = asset, label, createdAt = Iso(stored.At), url = $"/panel/heads/{stored.Id}.png?texture=2" });
    }

    private static byte[]? Base64(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String) return null;
        try { return Convert.FromBase64String(node.GetString()!); }
        catch (FormatException) { return null; }
    }

    /// <summary>GET cards/images/{assetId}: the heads a card of this player can show.</summary>
    private async Task<object> PlayerImagesAsync(long asset, CancellationToken token)
    {
        var cards = game.Players.Entries.Where(entry => entry.Value.Int("assetId") == asset).ToList();
        if (cards.Count == 0) throw new PanelError(404, "No card has that player.");
        var root = options.AssetsDirectory;
        bool Drawn(long id) => CustomPortraitPath(id) is not null || (root is not null && File.Exists(Path.Combine(root, "dynamic", $"p{id}.png")));
        // Offer actual downloaded dynamic portraits, not every card version or reward copy.
        // Borrowed portraits are represented by their source card rather than another duplicate option.
        var options_ = new List<object>();
        var seen = new HashSet<long>();
        foreach (var (resource, card) in cards.OrderByDescending(entry => entry.Value.Rating))
        {
            if (game.Cards.Rules.FutChampionsRedRareflags.Contains(card.Rareflag)) continue;
            foreach (var image in new[] { resource, card.Int("dynamicImageResourceId") }.Where(id => id > 0))
            {
                var source = game.Players.Find(image);
                if (source is null || source.Int("assetId") != asset
                    || game.Cards.Rules.FutChampionsRedRareflags.Contains(source.Rareflag)
                    || !Drawn(image) || !seen.Add(image)) continue;
                options_.Add(new
                {
                    kind = "card", resourceId = image, name = source.Text("name"), rating = source.Rating, rarity = source.Text("rarityName"),
                    url = $"/panel/assets/dynamic/p{image}.png",
                });
            }
        }
        var uploads = await game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync("""
            SELECT head_id, label, created_at, created_by FROM ultimateteam.custom_player_heads WHERE native_asset_id=@asset ORDER BY head_id DESC
            """, row => (object)new
            {
                kind = "upload", headId = row.GetInt64(0), label = row.GetString(1), createdAt = Iso(row.GetDateTime(2)),
                createdBy = row.IsDBNull(3) ? null : row.GetString(3), url = $"/panel/heads/{row.GetInt64(0)}.png?texture=2",
            }, ("asset", asset)), token);
        return new
        {
            assetId = asset,
            basePhoto = root is not null && File.Exists(Path.Combine(root, "players", $"{asset}.png")) ? $"/panel/assets/players/{asset}.png" : null,
            cards = options_,
            uploads,
        };
    }

    /// <summary>Lossless PNG decoded from the uploaded DDS stored in PostgreSQL.</summary>
    private async Task HeadImageAsync(HttpContext context, long headId)
    {
        var rows = await game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync(
            "SELECT dds FROM ultimateteam.custom_player_heads WHERE head_id=@id", row => (byte[])row.GetValue(0), ("id", headId)), context.RequestAborted);
        if (rows.Count == 0)
        {
            await Json(context, 404, new { error = "not found" });
            return;
        }
        var png = HeadDds.PreviewPng(rows[0]);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        context.Response.ContentLength = png.Length;
        // An upload never changes.
        context.Response.Headers.CacheControl = "private, max-age=604800";
        Security(context.Response);
        await context.Response.Body.WriteAsync(png, context.RequestAborted);
    }
}
