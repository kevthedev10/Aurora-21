# Aurora Tokens artwork

`prepare.py` adapts the maintainer's supplied transparent PNGs to the reviewed
FIFA 21 textures. This is an offline development utility, using Python/Pillow
for resizing and NumPy for native DXT3/DXT5 encoding. Python is not required by the launcher.
It does not generate new artwork or edit the installed game.

The original PNG inputs are preserved in `ultimateteam/assets/currency/source/`.
The reviewed SVG redraws now supply every texture: `aurora-points-single.svg`
for single-diamond artwork and `aurora-points-wide.svg` for three-diamond
product/tile/popup graphics. A blurred rendered variant supplies the popup glow.
Both the double and triple points-product tiles use the wide graphic.

For the four compact icons (32px header/Store, 44px preview and 64px Store),
`source/aurora-points-compact.svg` redraws the supplied central diamond and letter
with simpler paths. It removes side diamonds, fine texture and nested outlines
that alias at these sizes. This is a compact adaptation, not a lossless PNG trace.
The compact design has no outer dark stroke; its gradient rim meets transparency.
`render_compact.py` rasterizes the SVG paths using Python/Pillow; `prepare.py`
accepts the SVGs directly, including `--compact` for small icons. Inspect
the decoded DDS previews: vector input does not eliminate DDS compression.
The larger redraws use the same clean centre mark and diamond borders, with
three overlapping diamonds in the wide design. These are vector adaptations,
not lossless traces of the raster texture. Original PNGs remain available.

`bcn.py` fits each compressed colour palette to visible pixels, using nearest
palette entries and alpha-weighted endpoint refinement. It preserves native
BC2/BC3 formats while avoiding the pink edge wedges observed with the earlier
encoder. `python tools/fifa21-currency-art/test_bcn.py` checks alpha, block padding
and transparent colour isolation through Pillow's independent DDS decoder.

The 2026-10-04 inventory contains 12 reviewed textures in 11 native assets:

| Native asset under `data/ui/external/ion_fut/` | Member | Purpose inferred from path/UI references |
| --- | --- | --- |
| `imgAssets/store/icon/icon_fifaPoints_lg.dds` | — | Large Store/purchase icon |
| `imgAssets/store/icon/icon_fifaPoints_sm.dds` | — | Small Store icon |
| `components/PlayerIdentity.big` | `80` | Header currency icon |
| `screens/futPackPreviewScreen.big` | `33` | Pack-preview currency icon |
| `components/FUTControls.big` | `114`, `116` | Large purchase symbol and glow |
| `imgAssets/GameHub/GameHub_DraftPoints.dds` | — | Draft points tile |
| `imgAssets/liveMessage/liveMessage_5.dds` | — | Promotional points graphic |
| `imgAssets/pack/background/background_99000.dds` | — | Single points-product artwork |
| `imgAssets/pack/background/background_99001.dds` | — | Double points-product artwork |
| `imgAssets/pack/background/background_99002.dds` | — | Triple points-product artwork |
| `artAssets/ChalkTutorials/tutorial16.big` | `13` | Tutorial atlas points symbol |

The audit used the 80,876-entry legacy index, the 12,446 extracted FUT/shared UI
assets and 13,253 decoded PNGs. Fourteen FUT containers contain FIFA Points
strings/bindings. Name searches, image contact sheets and duplicate-pixel hashes
identified the graphics above; other references include shared imports and
currency data/text. This establishes the reviewed targets, not proof that every
screen/variant in the running game is covered. General player, kit and crest
collections are outside the extraction's scope. Native screen usage and final
visual fit need an in-game check.

Preparation retains native dimensions, DDS headers, formats and file lengths,
including the small Store texture's trailing bytes. The tutorial replacement is
limited to the block-aligned rectangle `(600, 0, 904, 180)`; every decoded pixel
outside it must match the source, preserving the neighbouring coins and stripe.
Runtime composition pins both complete source assets and individual DDS hashes.
BIG member offsets and sizes remain unchanged; all other container bytes survive.

From repository-root Windows PowerShell, using the existing verified extraction:

```powershell
python tools/fifa21-currency-art/prepare.py --native outputs/verification/fifa21-ui-art-20261004/native --single ultimateteam/assets/currency/source/aurora-points-single.svg --wide ultimateteam/assets/currency/source/aurora-points-wide.svg --compact ultimateteam/assets/currency/source/aurora-points-compact.svg --output ultimateteam/assets/currency --report outputs/verification/currency-art-review
powershell.exe -NoProfile -File ultimateteam/Build-LeaderboardClientTool.ps1
& ./ultimateteam/client-mod-tools/Aurora21-LeaderboardUiTool.exe --merge-currency-art ./ultimateteam/leaderboard-client-profile.json ./ultimateteam/assets/currency/artwork.json
```

The tool embeds the reviewed DDS payloads. The bundled profile includes their
hashes and original identities; its revision covers artwork as well as captions.
Normal launcher Play therefore selects a new profile instead of reusing an older
text-only cache. Compiled verification requires exactly the 21 localisation
databases plus these 11 artwork assets, comparing all compiled bytes with the
generated replacements. Earlier cached profiles remain intact.

Native verification on 2026-10-06 passed for all 21 language databases and all
11 artwork assets. The compiled SDK cache omitted type metadata for four
collectors; verification restores their original types only after matching their
names and GUIDs against the original game index, then rebuilds the legacy view.
The resulting index contains 80,876 assets. `collector-validation.json` records
the comparison. This reader correction does not change the compiled game files.

Test the global currency header, Store prices/purchase popup, pack preview, Draft
tile/cost, tutorials and any enabled points-product/promotional screens. Inspect
small-icon legibility and animation/glow fit. Coins, balances, prices and the
native `points` identifiers are unchanged. No VPS update changes these local
textures; other players need the updated client profile/tool package.
