# Contract candidates

**2026-09-30 policy update:** the maintainer chose to remove permanent-card
contracts. Contract generation/application is no longer planned; these files
remain reference data only. Loans retain their finite remaining-match counts.
Bronze/silver store packs now contain no consumables. Gold healing candidates
remain unverified and are not imported into runtime content.

`fifa17-contract-candidates.csv` transcribes the twelve ordinary player and
manager contract rows supplied by the maintainer on 2026-09-30. The maintainer
confirmed the source is FIFA 17. The original file/link is not known.

These are reference candidates, not verified FIFA 21 definitions. No runtime
imports this file. The 99-contract special item is intentionally excluded.

The maintainer subsequently supplied the same twelve IDs as FIFA 21 candidates,
plus healing IDs 5002029 (gold common, all injuries, 2 games) and 5002030 (gold
rare, all injuries, 5 games), citing https://github.com/mynameisvasco/futwebapiv2.
Inspection of all 18 TypeScript sources at revision
`00256c4f84fd0c02500d5f19b0f32a20a2108f7d` found none of those IDs and no
consumable effect table. Its README does describe a FUT 21 web-app API, but that
does not independently attest these rows. Preserve the maintainer's candidate
list pending an exact source file or native test; do not describe the linked
repository as containing definitions we could not find.

Comparison with Aurora 21: all twenty position modifiers agree on resource ID,
subtype, rating, rarity and art. Chemistry style IDs/subtypes agree, but the
working Aurora 21 implementation differs in art and ratings; keep those and
effects unchanged. The maintainer specified rare chemistry styles Hawk, Engine,
Anchor, Hunter, Catalyst and Shadow; all other styles are common gold. That
classification now lives in `rules/cards.json` under `chemistryStyles.rareStyles`.
Manager league IDs in Aurora 21 are generated and
cannot confirm the old export. Contracts have no existing definitions to compare.

Before promoting these rows into the catalogue, test in an isolated FIFA 21
profile: appearance/name, tier and rarity, counters and stacking, player versus
manager applicability, contract increments for each target tier, stack decrement,
save/reload and quick sell. Record observed results and preserve the source year.
Fitness and attribute-training rows from the older export are not candidates.
Healing needs its own FIFA 21 evidence; the older body-part definitions do not
establish FIFA 21's healing catalogue.
