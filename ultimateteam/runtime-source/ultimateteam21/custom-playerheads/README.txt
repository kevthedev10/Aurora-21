UltimateTeam custom per-card player portraits
=============================================

A file here overrides the portrait of exactly one card: FIFA 21 asks for
/fut/playerheads/g4/single/p<resourceId>.dds and the server serves
p<resourceId>.dds from this folder before any cache or CDN copy. The card's base
player (its asset id) is not changed.

The developers' Pro cards (rules/cards.json aquaProCards, migration 066):

  Ghoul     99 Allan Saint-Maximin   p167988548.dds
  Mustafa   99 Diogo Jota            p167996618.dds
  Shadow    99 Anthony Martial       p167983460.dds
  Miz       99 Michael Olise         p168019987.dds
  Horizzon  99 Bruno Fernandes       p167984358.dds  (his FIFA 21 TOTY art, 201538790)
  Atlaz     99 Bas Dost              p167961228.dds

Format: DDS, DXT5 (BC3) with transparency, one mip, 220x256 like EA's own
dynamic heads. Frame them by the face, like Bruno's TOTY art (p167984358):
crown about 4-9 px below the top, eye line near y 44-50, face centre near
x 140-148 (right of centre, so the rating/position column on the left stays
clear), head about 65-75 px wide, body faded out towards the bottom (fully
transparent by about y 250); arms may run off the sides. Do not centre the
subject's bounding box: a centred figure puts the face under the rating.

Dost framing repair (2026-10-06): the original portrait faded out at row 160
instead of filling the 256px canvas. p167961228 now uses more of the original
photo's torso, a larger head placed right of centre, and a smooth fade ending
before row 250. The original DDS header, dimensions and DXT5 format are retained.
After reviewing the first repair in FIFA, Dost was shifted left to reduce clipping
at the right card edge, then another 8px left at the user's request (26px total,
face centre about x114); size and fade are unchanged.
tools/repair-dost-portrait.py reproduces the crop from the linked Parool photo
and the pre-repair DDS (not the already repaired DDS). Its output includes an
independently decoded PNG, comparison and source/baseline/candidate SHA-256s.
Python dependencies: Pillow and NumPy; shared offline BC3 encoder in
tools/fifa21-currency-art/bcn.py. These tools are not server dependencies.

After replacing a portrait:
  - regenerate the panel PNG sidecars with python tools/export-pro-head-previews.py
    (Pillow required; this is an offline asset tool, not a runtime dependency).
    PNGs are exact RGBA decodes of the DDS and ship alongside them. The private
    panel serves them at /panel/assets/dynamic/p<resourceId>.png, including on
    hosts without the downloaded panel image folder. The DDS remains authoritative
    for FIFA; the PNG is its browser preview.
  - redeploy (the folder ships in the server image), then fully restart FIFA 21:
    the head is cached by the client for a year.
  - check the UltimateTeam log for:
      Serving custom player head resource=<id> file=p<id>.dds
