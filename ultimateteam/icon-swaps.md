# Icon Swaps

Status (reviewed 2026-10-01): **foundations only.** Tokens no longer come out of packs, and the server can hand one out and
take them in an SBC, but no objective pays a token and no SBC swaps them yet. Nothing new is visible to a player
beyond tokens disappearing from packs. This page is the plan for switching the rest on.

## What FIFA 21 had

Four rounds, each with 18 token player items: bronze, untradeable, lower-league players rated 60. Players earned the
tokens (mostly from objectives) and handed them in through SBCs for Icons or packs.

Our catalogue has all 72 tokens (seed `013-player-definitions.sql`):

| Round | Rareflag | Card type | Cards |
|---|---|---|---|
| Icon Swaps 1 | 89 | 77 (named by migration 009) | 18 |
| Icon Swaps 2 | 92 | 83 | 18 |
| Icon Swaps 3 | 93 | 84 | 18 |
| Icon Swaps 4 | 94 | 85 | 18 |

## In place

| Piece | Where | Behaviour |
|---|---|---|
| Rewards only | `rules/cards.json` `rewardOnlyRareflags`; migration 064 and `rules/packs.json` drop their default special weights | No store pack or player pick draws a token. Only a pack or pick that names the rarity (`allowed_rareflags`, a pick's `rareflags`) can. Like the red picks (18), the rarities have no default pack weight. The FGS Swaps 1-2 tokens (76 and 77, 10 each) are kept out of packs the same way; nothing hands them out yet; they are untradeable and quick-sell for zero. Before this, a bronze pack's special slot drew an Icon Swaps or FGS Swaps token about one time in eight. |
| Quick sell 0 | `rules/cards.json` `quickSell.zeroValueRareflags` | A token sells for nothing, as in FIFA 21, so it is only worth swapping. It was 12,000 (the special default). |
| Red FUT Champions pool | `rules/cards.json` `futChampionsRedRareflags` | The fixed TOTW 1–3 Champions reward pool uses its own rarity list (18), so a token of a Team of the Week player never joins it. |
| Token packs 560-563 | migration `064-icon-swaps-token-packs.sql`, `store_packs.swap_token_rareflag`, mirrored in `rules/packs.json` | Reward packs, never on sale. Each opens one **untradeable** token of its round that the club does not hold in any pile (Unassigned included), or any of them once it holds all 18. An SBC squad takes each footballer once, so a second copy of a held token could never be swapped. The operator panel's contents preview refuses them, as it refuses the other special pack kinds. |
| Token requirement rules | `rules/sbc.json` `ICON_SWAPS_1` ... `ICON_SWAPS_4` | Eligibility key `Rarity` (18) with the round's rareflag, `AtLeast`. The server counts cards of exactly that rareflag, so a token of another round does not count. Label: "Icon Swaps 1 tokens: Min N". |
| Set rewards on the tile | `SbcService.SetJson` | A set with its own `rewards` shows them on its tile (sets without one still show their last challenge's). A two-part swap needs this. No set in `sbc.json` has set rewards today, so nothing on screen changed. |
| Tests | `IconSwapsFoundationTests` | Rewards only and quick sell; 150 bronze special rolls with no token; the red pool; the token packs (18 distinct, then any; untradeable; not on sale); a token rule counts only its round; a two-part swap pays its Icon only after both parts. |

## Switching on the objectives

Token objectives are ordinary grind groups in `rules/objectives.json` whose award is the round's token pack. Claiming
one puts a token pack in My Packs.

```json
{
  "id": 8, "title": "Icon Swaps 1", "subTitle": "Earn tokens and swap them for Icons in SBCs", "award": { "pack": 401 },
  "objectives": [
    { "id": 801, "name": "Token 1", "stat": "matches", "target": 2, "award": { "pack": 560 }, "xp": 500 }
  ]
}
```

- One group per round (ids 8-11), 18 objectives each (ids 801-818, 901-918, 1001-1018, 1101-1118), each paying one
  pack of its round (560, 561, 562, 563).
- Objective ids must be unique across every group; the rules loader rejects a stat without a `statLabels` entry
  (matches, wins, goals, cleanSheets, draftWins, sbcs, packs).
- Progress is the club's **lifetime** stat, not progress since the group appeared. A club already past a target can
  claim at once. After the C# switch every club starts fresh, so a ladder that climbs round by round works.

A proposed ladder (18 per round; XP per objective in brackets; group award in the last column):

| Round | matches | wins | goals | cleanSheets | sbcs | packs | draftWins | Group award |
|---|---|---|---|---|---|---|---|---|
| 1 (500) | 2, 5, 10, 15, 20 | 1, 3, 6, 10 | 5, 12, 20, 30 | 1, 3 | 1 | 3 | 1 | 401 Rare Players |
| 2 (1000) | 30, 40, 50, 60 | 15, 20, 25, 30 | 45, 60, 75, 90 | 6, 9 | 4, 8 | 15 | 3 | 403 Mega |
| 3 (1500) | 75, 90, 105, 120 | 40, 50, 60, 70 | 120, 150, 180, 210 | 14, 18 | 12 | 30 | 6, 10 | 404 Rare Mega |
| 4 (2000) | 140, 160, 180, 200 | 85, 100, 115, 130 | 250, 290, 330, 370 | 24, 30 | 18 | 50 | 15, 20 | 405 Ultimate |

## Switching on the swap SBCs

FIFA 21's Icon offers, each checked against our catalogue (card, rarity and rating) on 2026-09-28:

| Round | Icon | Version | Resource id | Card | Tokens | Parts |
|---|---|---|---|---|---|---|
| 1 | Thierry Henry | Mid | 237069 | 90 LW | 17 | 11 + 6 |
| 1 | Frank Rijkaard | Prime | 214098 | 90 CDM | 14 | 11 + 3 |
| 1 | Ronald Koeman | Prime | 167680 | 91 CB | 13 | 11 + 2 |
| 1 | Davor Šuker | Mid | 257417 | 87 ST | 11 | 1 |
| 1 | Gheorghe Hagi | Mid | 239057 | 89 CAM | 9 | 1 |
| 1 | Nemanja Vidić | Base | 17032973 | 85 CB | 7 | 1 |
| 2 | Roberto Baggio | Moments | 246493 | 94 CF | 17 | 11 + 6 |
| 2 | Emilio Butragueño | Base | 17016819 | 87 ST | 15 | 11 + 4 |
| 2 | Laurent Blanc | Mid | 238444 | 89 CB | 13 | 11 + 2 |
| 2 | Marc Overmars | Moments | 246536 | 91 LM | 11 | 1 |
| 2 | Ashley Cole | Prime | 34079 | 89 LB | 9 | 1 |
| 2 | Deco | Moments | 246508 | 91 CM | 7 | 1 |
| 3 | Patrick Vieira | Prime | 238427 | 91 CM | 17 | 11 + 6 |
| 3 | Rio Ferdinand | Moments | 246495 | 91 CB | 15 | 11 + 4 |
| 3 | Ferenc Puskás | Prime | 254642 | 94 CF | 13 | 11 + 2 |
| 3 | George Best | Prime | 226764 | 93 RW | 11 | 1 |
| 3 | Hristo Stoichkov | Moments | 246509 | 93 ST | 8 | 1 |
| 3 | Bastian Schweinsteiger | Moments | 256340 | 92 CDM | 6 | 1 |
| 4 | Zinedine Zidane | Moments | 248451 | 97 CAM | 17 | 11 + 6 |
| 4 | Paolo Maldini | Moments | 246535 | 95 CB | 15 | 11 + 4 |
| 4 | Pelé | Mid | 237068 | 95 CF | 10 | 1 |
| 4 | Diego Maradona | Prime | 190042 | 97 CAM | 9 | 1 |
| 4 | Eusébio | Base | 16967265 | 89 CF | 6 | 1 |
| 4 | Kaká | Moments | 247299 | 92 CAM | 4 | 1 |

Moments are rarity 84, the others 12. Two lookalikes to avoid: Sócrates Vieira de Oliveira shares "Vieira", and Raúl
González Blanco shares "Blanc".

An SBC squad holds at most 11 cards (`rules/sbc.json` `squadSize`, and the client's squad has 11 slots). So a swap of
11 tokens or fewer is one challenge with the Icon as its reward, and a dearer one is a **two-part set**: 11 tokens,
then the rest, with the Icon as the **set** reward. A set reward pays only once every challenge is in, so no single
part can claim it. FIFA 21's own screen for 12-17 tokens is not recorded; its costs are kept exactly this way.

A single swap (tab 5, Icons):

```json
{
  "id": "ISW1_SUKER", "tabId": 5, "name": "Icon Swaps 1: Davor Šuker",
  "description": "Swap 11 Icon Swaps 1 tokens for Davor Šuker (Mid Icon, 87 ST).",
  "repeatable": false, "image": "sbc_set_image_icon_swaps", "state": "available",
  "challenges": [
    {
      "id": "CH_ISW1_SUKER", "order": 1, "name": "Swap 11 tokens", "difficulty": 1,
      "requirements": { "rules": [
        { "type": "PLAYERS", "attributes": { "min": 11, "max": 11 } },
        { "type": "ICON_SWAPS_1", "attributes": { "min": 11 } } ] },
      "rewards": { "entries": [
        { "type": "PLAYER", "attributes": { "resourceId": 257417, "name": "<catalogue name>", "rating": 87, "position": "ST" } } ] }
    }
  ],
  "challengeCount": 1, "challengeIds": [ "CH_ISW1_SUKER" ]
}
```

A two-part swap has two challenges with `"rewards": { "entries": [] }` (11 tokens, then the rest), and the `PLAYER`
entry moves to a `"rewards"` block on the set itself.

Rules for editing `sbc.json`:

- The player reward resolves by name, rating and position to the closest catalogue card, then overlays those onto
  it. Author the catalogue's own `name`, `rating` and `position` so the card is unchanged, and keep the resource id.
  A test should assert each swap resolves to its resource id.
- Challenge completions are stored under the challenge's string `id`, so every challenge id must be unique across the
  whole file.
- Numeric set ids come from the set's position (900000 + index × 100), and a challenge's id is its set id plus its
  `number` (else its 1-based place). Append new sets at the end, or every later set's ids (and saved SBC squads) move.
- Update `counts` (sets, challenges).
- A set that is not on the live SBC list (panel SBCs → Live SBCs, migration 142; it replaced `rules/sbc.json` `hiddenSets`)
  or is switched off there is served nowhere and refuses saves and submissions. Phase 1 lists only the Foundations
  (`FND_*` except `FND_ADVANCED`) and the Bronze, Silver and Gold Upgrades (`UPG_*`).
- `rules/sbc.json` `ignoredRequirements` drops a rule type from every challenge (neither shown nor checked). Phase 1
  drops `CHEMISTRY`: the SBC squad save body is untraced, so the server cannot compute squad chemistry.
- `sbc.json` is stored with mixed line endings and Git keeps them as they are. Edit it byte-exactly with CRLF lines,
  and check that `git diff` shows only additions.

FIFA 21's swap menus also offered packs (for example Base Icon Pack for 8 tokens, 83+ x25 for 5, player picks of
Icon Moments). Most need Icon-only packs by version, which do not exist yet, so they are left out.

## Unverified until a live test

- How the client words a `Rarity` requirement for 89 and 92-94 (the challenge text uses the server's label).
- How the client shows a set-level player reward, on the tile and in the completion popup.
- The token pack's name and art in My Packs (reward ids from 400 use the special art band, 4).
- An objective group of 18 objectives.

## Sources

- FIFA UTeam, [FIFA 21 ICON Swaps list](https://fifauteam.com/fifa-21-icon-swaps-list-objectives-rewards/): every round's offers and token costs.
- GamesRadar, [Icon Swaps 3 guide](https://www.gamesradar.com/fifa-21-icon-swaps-player-tokens/), and Gfinity, [Icon Swaps 1 objectives](https://www.gfinityesports.com/article/fifa-21-icon-swaps-1-all-token-objectives-guide-fast-ps4-xbox-one-series-x-pc-ps5): agree with it for rounds 1 and 3.
