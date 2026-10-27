# Gold and Base Icon balance — migration 120

Reviewed on 2026-10-02. This is a custom Aurora 21 balance profile, with the
maintainer's chosen default of 5× harder for desirable cards. It is not a
reconstruction of EA's player-specific pack weights.

## Research and provenance

EA's [pack probability explanation](https://www.ea.com/able/news/fifa-pack-probabilities)
describes pack-level probabilities measured through simulated openings and
categories such as minimum overall rating. It does not supply FIFA 21 individual
player weights or a desirable-player classification.

Contemporaneous [December 2020 player discussion](https://www.reddit.com/r/EASportsFC/comments/knnddy)
identifies Mendy, Gomez and Sissoko as examples of desirable low-rated cards that
players perceived as hard to pack. The [January 2021 Base Icon guide](https://www.reddit.com/r/EASportsFC/comments/ktcx07)
provides period context for Icon desirability. These are community opinions,
not measured odds. The larger curated list below is an Aurora balance decision;
the sources do not establish a 5× multiplier for every listed player.

Identities come from this repository's native catalogue and curated promo data:
[players.json](../ultimateteam/runtime-source/ultimateteam21/players.json),
[promo CSV](../tools/fifa21_promo_group_cards.csv), and the existing promo seed
migrations. Gold uses native asset IDs inside Gold Rare memberships, covering
ordinary Gold versions already in that pool. Icons use exact Base resource IDs,
so Mid and Prime versions are unaffected. The named IDs are recorded in
[migration 120](../ultimateteam/server/database-setup/migrations/120-gold-and-base-icon-bucket-balance.sql).

## Buckets and per-card reductions

Gold Rare keeps its existing seven rating-range buckets and their weights,
including operator changes. The stock weights are 58, 29, 9.6, 2.7, 0.5, 0.15
and 0.05 for 75–79, 80–81, 82–83, 84–85, 86–87, 88–89 and 90–99 respectively.

Base Icons keeps its seven rating buckets and weights: 85:35, 86:28, 87:20,
88:10, 89:5, 90:1.5, 91:0.5. Gullit and Vieira return from the old artificial
89 placement to their actual 86 bucket; any different operator placement is
preserved. Their desirable marks handle the reduction independently.

The runtime chooses the bucket first. Then each eligible member draws with
weight `1 / draw_reduction`. Unmarked cards have reduction 1; a card marked 5
has weight 0.2. With two ordinary cards and one marked card in a bucket:

| Card | Weight | Conditional chance within this bucket |
| --- | ---: | ---: |
| Ordinary A | 1 | 45.45% |
| Ordinary B | 1 | 45.45% |
| Desirable | 0.2 | 9.09% |

The desirable card is five times harder than either ordinary card in the same
bucket. This is not a promise of one fifth its previous absolute probability:
the weights are renormalised. If every eligible card has the same reduction,
they still draw equally. A singleton eligible card still draws with certainty
after its bucket has been selected. Pack rating limits, grant-only exclusions
and cards already drawn all affect eligibility before normalisation. Pack and
promo hit chances are unchanged.

Marks belong to a card's membership in a promo. They follow it when moved
between that promo's buckets. They do not apply globally to the player or to
their special versions in other promos.

## Gold Common

Gold Common uses only actual-rating buckets, with no desirable-card marks.
The bucket weight is the number of members multiplied by this per-card factor:

| Rating | Per-card factor |
| --- | ---: |
| 75 | 1 |
| 76 | 0.8 |
| 77 | 0.6 |
| 78 | 0.4 |
| 79 | 0.25 |
| 80 | 0.15 |
| 81 | 0.08 |
| 82 | 0.04 |

Each subsequent rating halves the factor if such members exist at migration
time. This is an assumed decreasing curve, not published EA odds. The current
seed has 1,374 Gold Common members rated 75–82. Within each rating, selection
is uniform. The old bucket remains disabled for history. These weights are
seeded once; later membership edits do not automatically recalculate them.

## Initial desirable marks

Migration 120 seeds 5× for these 32 Gold Rare players:

Sissoko, Saint-Maximin, Ousmane Dembélé, Joe Gomez, Ferland Mendy, Walker,
Varane, Davies, Renato Sanches, Vinícius Jr, Valverde, Militão, Rashford,
Martial, Werner, Lucas Moura, Gabriel Jesus, Semedo, Wan-Bissaka, Pogba,
Kanté, Son, Aubameyang, Hazard, Dybala, Mané, Salah, Van Dijk, Mbappé,
Neymar, Cristiano Ronaldo and Messi.

It seeds 5× for these 28 Base Icons:

Vieira, Gullit, Eusébio, Cruyff, Ronaldo, Pelé, Maradona, Zidane, Garrincha,
Ronaldinho, Best, Maldini, Carlos Alberto, Puskás, Cantona, Eto'o, Dalglish,
Butragueño, Ferdinand, Essien, Blanc, Desailly, Seedorf, Roberto Carlos,
Zambrotta, Lahm, Matthäus and Kaká.

An operator-removed member stays removed. Other pools, card definitions,
pack odds and club state are preserved. The migration runs once; subsequent
startup does not reset panel edits.

## Panel and API

Open a promo's bucket editor, search for the card, tick **Desirable**, and enter
the desired **× harder** factor. Preview, stage and publish through Changes.
Untick to restore ordinary weight. Gold Common has no desirability controls.
Factors greater than 1 and up to 999999 are supported, with six decimal places.

Promo definitions expose `card_reductions`, keyed by native resource ID. Reads
include only factors greater than 1. An explicit map replaces the marks;
`{}` clears them. Omitting the field preserves them for older clients and
historical drafts. History stores marks so publication can be reverted.
See the [API contract](../ultimateteam/panel-api-contract.md#promos-buckets-events-and-icon-rotations).

## Verification

The panel's 218 Vitest tests, both TypeScript checks, Vite build and source
stamp check pass. Server verification covers weighted draws and exact
distributions, filtered eligibility, real migration-119 upgrade preservation,
and panel preview/publish/move/clear/revert. All 48 distinct focused checks pass,
including a corrected synthetic card-ID fixture and a pre-118 draft. The 303
repository hygiene checks also pass. The panel was subsequently rebuilt with
the club reset button; the panel test inventory is now 221 (220 passed in the
initial run, with all 16 player-page tests passing after correcting the modal
animation wait in the remaining test).
The full solution suite, browser review and native FIFA
pack opening are not acceptance results for this change yet.
