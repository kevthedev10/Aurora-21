# Aurora Champions client logos

`prepare.py` converts the supplied transparent PNG into DDS replacements for the
reviewed FIFA 21 Champions logo assets. The source is preserved at
`ultimateteam/assets/champions/source/aurora_champions.png`. The native texture
headers and dimensions are fixed, so wrapping the raster in an SVG would not
increase detail. The converter resizes the cropped artwork to each texture's
actual bounds and spreads the edge colours into the transparent texels before
BC2/BC3 compression. The game draws these straight-alpha textures magnified
(a 256px logo covers 512px at 1440p), so sharpening the alpha channel or leaving
dark transparent neighbours shows up as jagged, fringed edges. That
magnification is also the limit on how sharp the 256px logos can look.

The 25 image targets are the four primary Champions logos (`logo_champions`, both
highlight states and `logo_weekend`), the sell screen logo and watermark, the
Rivals Champions colour/flat logos and two Weekend League logos, and the hub
ribbon. The Division Rivals introduction has a separate crest in
`FutRivalsThresholdOverlay.big`; member `116` is replaced in place. Hub artwork
also includes the Play mode icon, Leaderboard prize image, the full Champions
Channel medals, and four Division Rivals sell screen variants. The channel
medals now cover the original EA SPORTS / CHANNEL rings with a black disc. For
these composites, only the compressed blocks touching the badge are rewritten.

Three targets copy what the native art does, measured on the 2026-10-04
extraction, instead of fitting the whole logo into the old bounds:

- The hub ribbon (the tile watermark) is the full-colour logo at its 256px
  size and alpha 34, moved up so the tile cuts it off at the bottom and right.
- The Play mode icon is cut out of the flat grey `(126, 125, 131)` that the
  other five mode icons use: the gold parts stay and the red shield body is open.
- The transparent texels around that icon keep the same grey.
The preparation utility keeps each native DDS header, size, compression
format and length. The bundled client mod verifies the original and
replacement SHA-256 hashes before applying any texture.

`prepare_text.py` reads the verified, extracted native language tables. It
generates exact source/target edits for “FUT Champions”, its common short forms
and reviewed localised forms. It replaces only the `FUT` brand token with
`Aurora`, preserving each language's own word for Champions and the rest of
each sentence. The 2026-10-10 inventory has 2,141 replacements across
107 string IDs and 21 languages. The builder checks each original row,
placeholder and line break before writing a database, then checks every
unrelated field after round trip. The edits also cover the native
`StadiumName_445` display label where the old brand appears; the stadium ID and
gameplay setting remain unchanged.

Rank crests and Champions Points reward icons have distinct UI meanings and
remain as native art. Generic FUT 21 reward packs, coins and match still imagery
in the composite hub panels also remain. Final visual fit needs an in-game check.

From the repository root:

```bash
python3 tools/fifa21-champions-art/prepare.py \
  --native outputs/verification/fifa21-ui-art-20261004/native \
  --source ultimateteam/assets/champions/source/aurora_champions.png \
  --output ultimateteam/assets/champions \
  --report outputs/verification/champions-art-review

python3 tools/fifa21-champions-art/prepare_text.py \
  --tables outputs/verification/champions-top25-20261003/localisation-all/tables \
  --profile ultimateteam/leaderboard-client-profile.json \
  --output ultimateteam/assets/champions/text.json \
  --report outputs/verification/champions-art-review/text-preparation.json
```

Then run the supported `ultimateteam/Build-LeaderboardClientTool.ps1` builder and
use the packaged tool's `--merge-champions-art` and
`--merge-champions-text` commands to append the reviewed image and text edits
to `ultimateteam/leaderboard-client-profile.json`. This retains the existing
localisation and Aurora Tokens artwork in the same `.fbmod`.
