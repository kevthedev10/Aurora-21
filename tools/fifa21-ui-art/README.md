# FIFA 21 FUT artwork export

Read-only extraction of the installed game's FUT and supporting shared UI bitmaps into editable PNGs,
screen/area folders, an offline searchable `index.html`, and CSV/JSON provenance indexes. The tool does not
launch FIFA, touch services, patch the game, build a mod, or download replacement artwork.

The native exporter uses the **existing** asset cache, SDK and FIFA21 plugin from the separately reviewed
FMT revision `67b35957725fc961e4262afb8be08e9cd1df239e`. It refuses missing caches and game builds whose
executable/CardsDLL hashes differ from the supported local build, and rechecks those hashes after extraction.
The existing legacy inventory selects candidates; each candidate is resolved and read from the current native
asset manager, rather than treating the old inventory as proof of its content. Raw inputs remain under
`outputs/verification/`; only the extraction tool sources should be committed.

## Run

Requires the Windows .NET 10 SDK, the already built/cached FMT installation, and Python with Pillow
(`python -m pip install Pillow`). The C# project references existing FMT binaries without rebuilding upstream.
Its raw-asset path works without starting FMT's desktop UI. FMT's transitive CSIL reference can emit a
WindowsBase version warning at build time; image conversion uses Pillow separately.

From the repository root, supply your actual paths:

```powershell
.\tools\fifa21-ui-art\Export-Fifa21UiArt.ps1 `
  -GameRoot 'C:\Games\FIFA 21' `
  -FmtRoot '<path to the reviewed FMT checkout>' `
  -InventoryPath '<path to existing legacy-index.json>'
```

The output defaults to `outputs/FIFA21-FUT-UI-Art-YYYY-MM-DD/`. `-OutputRoot` selects another destination;
`-Python` selects a Python interpreter; `-Limit` bounds a sample run. Run the Python conversion separately
from WSL if Pillow is installed there rather than on Windows:

```bash
python3 tools/fifa21-ui-art/build_gallery.py --native '<native export folder>' --output '<gallery folder>'
python3 tools/fifa21-ui-art/test_gallery.py
```

If a conversion failure has been diagnosed and fixed, add `--repair-failures` to retry only the native sources
listed as failures in the existing `index.json`; successful images and their provenance remain in the index.
The native manifest's build profile must match. DDS and PNG representations sharing a native basename are
kept separately (the PNG representation gets `__source_png`).

## Coverage and verification

- Select every DDS/PNG/BIG asset under the native `data/ui/external/ion_fut/` tree, plus reviewed supporting
  shared UI directories and FUT/main-menu/pause archives. General player portraits, kits and crest collections
  are excluded. `select_assets` contains the complete selection rule; `selection.json` records exact names.
- Parse BIGF/BIG4 indexes with bounds checks. Retain native archive member identifiers and decode EA 0x10FB
  compressed members when present. Unsupported signatures and formats become explicit failures.
- Export the original top mip without resizing, cropping or recolouring. Each PNG is decoded again and its
  RGBA pixels compared with the source decode. Preserve transparency and record native format, dimensions,
  native asset/bitmap hashes, PNG hash and decoded-pixel hash.
- Keep archives with no bitmap members in a separate report. APT vector shapes, layouts, runtime content,
  downloaded server artwork and the final assembled screen are not represented as extracted PNGs.
- Folder names are inferred from native paths. This is not a verified screen-to-texture or clipping map.
  Some extracted textures are atlases or unused/fallback art shipped by EA.
- Reruns refuse to overwrite a PNG whose pixels differ from the source, preserving artist edits. Keep the
  output alongside its indexes; edited PNGs need a separately implemented and verified importer before use in FIFA.

The gallery runs directly from disk without a server. It displays 120 images per page, searches native paths
and filenames, filters areas and opens each original-size PNG. It never embeds the PNGs into the HTML.
