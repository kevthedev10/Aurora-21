using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Services;

namespace Aurora21.UltimateTeam.Core.Web.Routes;

/// <summary>Static CDN content: bundled or cached files, player heads and store localization.</summary>
public sealed partial class ContentRoutes : FutRouteGroup
{
    /// <summary>A 1x1 transparent PNG.</summary>
    private static readonly byte[] TransparentPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public ContentRoutes(FutServices fut) : base(fut)
    {
    }

    public async ValueTask<FutResponse?> StaticContentAsync(FutRequest r, CancellationToken cancellationToken) =>
        await Context.Content.GetFutContentAsync(r.Lower, cancellationToken) is { } content
            ? FutResponse.Bytes(200, content.Body, ("Content-Type", content.ContentType), ("Cache-Control", "public, max-age=86400"))
            : null;

    /// <summary>
    /// SBC tile art. Set tiles get their pre-rendered badge. The challenge screen draws its image
    /// oversized over the requirements, so challenges get a transparent image.
    /// </summary>
    public FutResponse? SbcImage(FutRequest r)
    {
        if (SbcImagePattern().Match(r.Lower) is not { Success: true } match || r.Method != "GET")
        {
            return null;
        }
        if (match.Groups[1].Value == "challenges")
        {
            return FutResponse.Bytes(200, TransparentPng, ("Content-Type", "image/png"), ("Cache-Control", "no-store"));
        }
        if (Fut.Sbc.SetImage(long.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)) is not { } image)
        {
            return FutResponse.Bytes(404, "not found"u8.ToArray(), ("Content-Type", "text/plain"));
        }
        return FutResponse.Bytes(200, image, ("Content-Type", "image/png"), ("Cache-Control", $"public, max-age={Context.Rules.Sbc.Images.CacheSeconds}"));
    }

    public async ValueTask<FutResponse?> PlayerHeadAsync(FutRequest r, CancellationToken cancellationToken)
    {
        var match = PlayerHeadPattern().Match(r.Lower);
        if (!match.Success || r.Method != "GET")
        {
            return null;
        }
        if (await Context.Content.GetPlayerHeadAsync(ParseLong(match), cancellationToken) is not { } body)
        {
            return FutResponse.Bytes(404, "dynamic player head unavailable\n"u8.ToArray(), ("Content-Type", "text/plain; charset=utf-8"), ("Cache-Control", "no-store"));
        }
        // Publishing an image selection changes the bytes behind the same card URL.
        return FutResponse.Bytes(200, body, ("Content-Type", "application/octet-stream"), ("Cache-Control", "no-store"));
    }

    /// <summary>
    /// A card background FIFA downloads for a rarity whose art the game does not carry (observed 2026-09-24 for
    /// Sudamericana, Libertadores, Player Moments, Season Reward and Icon Swaps 4). A 404 when it is unavailable, as
    /// the Python server answered.
    /// </summary>
    public async ValueTask<FutResponse?> ItemBackgroundAsync(FutRequest r, CancellationToken cancellationToken)
    {
        var match = ItemBackgroundPattern().Match(r.Lower);
        if (!match.Success || r.Method != "GET")
        {
            return null;
        }
        if (await Context.Content.GetItemBackgroundAsync(match.Groups[1].Value, match.Groups[2].Value, cancellationToken) is not { } body)
        {
            return FutResponse.Bytes(404, "card background unavailable\n"u8.ToArray(), ("Content-Type", "text/plain; charset=utf-8"));
        }
        return FutResponse.Bytes(200, body, ("Content-Type", "application/octet-stream"), ("Cache-Control", "public, max-age=31536000, immutable"));
    }

    /// <summary>
    /// Live Friendlies tile art (house rule imageId background_&lt;n&gt;). EA's CDN no longer has these, and the catch-all
    /// answered them 200 "{}", which is no texture; every event gets the same plain dark DXT1 instead.
    /// </summary>
    public FutResponse? HouseRuleBackground(FutRequest r) =>
        r.Method == "GET" && HouseRuleBackgroundPattern().IsMatch(r.Lower)
            ? FutResponse.Bytes(200, PlainBackgroundDds, ("Content-Type", "application/octet-stream"), ("Cache-Control", "public, max-age=86400"))
            : null;

    private static readonly byte[] PlainBackgroundDds = SolidDxt1(512, 256, 0x08A5);

    /// <summary>A one-colour DXT1 DDS (no mipmaps): every 4x4 block is <paramref name="rgb565"/> at index 0.</summary>
    internal static byte[] SolidDxt1(int width, int height, ushort rgb565)
    {
        var blocks = (width / 4) * (height / 4);
        var dds = new byte[128 + blocks * 8];
        var span = dds.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span, 0x20534444); // "DDS "
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x81007); // caps, height, width, pixel format, linear size
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)(blocks * 8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[80..], 0x4); // FOURCC
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[84..], 0x31545844); // "DXT1"
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[108..], 0x1000); // texture
        for (var block = 0; block < blocks; block++)
        {
            // color0 > color1 keeps the opaque four-colour mode; index 0 everywhere picks color0.
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[(128 + block * 8)..], rgb565);
        }
        return dds;
    }

    public FutResponse? StaticLocalization(FutRequest r)
    {
        if (r.Lower is "/fut/loc/pc/leaderboards.eng_us.xml" or "/fut/tutorials/cmn")
        {
            return FutResponse.Bytes(404, "not found"u8.ToArray(), ("Content-Type", "text/plain"));
        }
        if (!StorePackDescriptionsPattern().IsMatch(r.Lower))
        {
            return null;
        }
        // The native store resolves titles through FUT_STORE_PACK_<id>_NAME/_DESC keys. Every client language asks for
        // its own file (es_mx, fr_fr, ar_sa, ru_ru, zh_cn and es_es were seen live on the Python server); CardsDLL reads
        // only trans-unit, resname and source, so one document serves them all.
        return FutResponse.Bytes(200, Fut.Store.PackDescriptionsXliff(),
            ("Content-Type", "application/xml; charset=utf-8"),
            ("Cache-Control", "no-store, no-cache, must-revalidate"),
            ("Pragma", "no-cache"),
            ("Expires", "0"));
    }

    [GeneratedRegex(@"^/fut/packs/loc/storepackdescriptions\.[a-z]{2,3}_[a-z]{2,4}\.xml$")]
    private static partial Regex StorePackDescriptionsPattern();

    [GeneratedRegex(@"^/fut/sbc/[a-z0-9]+/(sets|challenges)/images/sbc_(?:set|challenge)_image_(\d+)\.png$")]
    private static partial Regex SbcImagePattern();

    [GeneratedRegex(@"^/fut/houserules/background/background_\d+\.dds$")]
    private static partial Regex HouseRuleBackgroundPattern();

    [GeneratedRegex(@"^/fut/playerheads/g4/single/p(\d+)\.dds$")]
    private static partial Regex PlayerHeadPattern();

    /// <summary>itemBGs/&lt;rarity guid&gt;/cards_bg_&lt;size&gt;_1_&lt;rarity&gt;_&lt;n&gt;.dds, matched on the lower-cased path.</summary>
    [GeneratedRegex(@"^/fut/items/images/backgrounds/itembgs/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/(cards_bg_[a-z0-9_]+\.dds)$")]
    private static partial Regex ItemBackgroundPattern();
}
