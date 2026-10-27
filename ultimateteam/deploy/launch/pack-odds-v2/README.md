# Pack odds v2 (2026-10-02)

Testers reported that packs feel poor. The live odds were modelled before anything was changed: an exact model of
the server's draw code, run against the live panel snapshot of 2026-10-02. It found three causes:

- **The rare often isn't a player.** A mixed pack spread its rare count evenly over players, consumables, club items
  and staff. A Gold Pack's single rare was a player only a third of the time (0.33 rare players per pack).
- **No store pack could drop a TOTW in-form.** Every store pack had `special_chance` 0 and no `category_chances`.
  TOTW 1-3 were live, but only through rewards and Match Packs.
- **The top Gold Rare groups are thin.** 86-87 had weight 0.35, 88-89 0.11 and 90+ 0.04: 0.5% for an 86+ per rare
  slot. Base Icons were 0.05% per rare slot.

The quick-sell audit of the bronze and silver packs found two more problems:

- **Managers quick-sold a flat 150/300 in every tier.** A bronze manager was worth 8-17x a bronze player, so the
  400-coin Bronze Pack returned 97% of its price, and 53% of Bronze Packs returned more than they cost.
- **Silver rares were capped at 90**, below silver commons (98-111). Premium Silver therefore returned less than Silver.

## What changes

| Lever | Where | Before | After |
|---|---|---|---|
| A pack's rares go to its players first | code, `MixedPackContents.Draw` | spread evenly over 4 categories | players first; only what they can't take goes elsewhere |
| Manager quick sell | code, `QuickSellRules.ManagerValue` | 150 / 300 in every tier | lower of 150/300 and a base player of its tier: bronze 17/45, silver 98/148, gold unchanged |
| Silver rare quick sell cap | `rules/cards.json` (rules row) | 90 | 148, above commons and below the 150 market floor |
| AI Market Maker | migration 111 (on main, Atlas) | 500 listings | 0, unsold AI stock removed: a players-only market (owner) |
| Gold Rare group weights, 75-79 / 80-81 / 82-83 / 84-85 / 86-87 / 88-89 / 90+ | panel, promo `gold_rare` | 60.4 / 28 / 8.7 / 2.4 / 0.35 / 0.11 / 0.04 | 58 / 29 / 9.6 / 2.7 / 0.5 / 0.15 / 0.05 |
| TOTW in-form chance per rare player slot | panel, pack `category_chances.totw` | 0 | Gold 0.6%, Premium Gold and Jumbo Premium Gold 0.8% |
| Base Icon chance per rare player slot | panel, pack `category_chances.icon` | 0.05% (global) | Gold 0.05%, Premium Gold and Jumbo Premium Gold 0.08% |

Everything ships with the deploy. **Migration 113** sets the weights, the pack chances and the silver cap rules row
when the new image starts. It only changes a value that still has its seeded value, so an operator's panel edit is
never overwritten. `apply.mjs` is the manual route: it stages the same values as one panel changeset, which can be
validated, previewed with the panel's pack odds sampler (`pending`), published, and reverted from content history.

## Results (model: 200,000 packs per row; live = 2026-10-02)

"Walkout" means 86+ base, any in-form, or an Icon. Retail figures are FIFA 21's disclosed odds: Gold 82+ 6.4-7.7%,
Premium Gold 82+ 17-20% and 84+ 4.1-4.6%.

| Pack | Price | Rare players | 82+ or special | 84+ | Walkout | In-form | Icon | Quick-sell return |
|---|---|---|---|---|---|---|---|---|
| Gold, live | 5,000 | 0.33 | 4.6% | 1.0% | 0.19% | 0 | 0.02% | 30% |
| **Gold, v2** | | 1.00 | **14.1%** | 3.6% | **1.4%** | 0.6% | 0.05% | 35% |
| Premium Gold, live | 7,500 | 1.12 | 12.8% | 3.3% | 0.63% | 0 | 0.06% | 23% |
| **Premium Gold, v2** | | 3.00 | **35.9%** | 10.9% | **4.6%** (1 in 22) | 2.4% | 0.22% | 33% |
| Jumbo Premium Gold, live | 15,000 | 2.84 | 28.4% | 8.4% | 1.6% | 0 | 0.15% | 25% |
| **Jumbo Premium Gold, v2** | | 6.83 | **63.8%** | 25.2% | **10.3%** | 5.5% | 0.55% | 37% |

Bronze and silver, quick-sell return after the manager and silver-rare fixes (they draw no specials):

| Pack | Price | Live | v2 |
|---|---|---|---|
| Bronze | 400 | 97% (53% of packs pay back) | 44% (0% pay back) |
| Premium Bronze | 750 | 53% | 29% |
| Jumbo Premium Bronze | 1,500 | 79% (23% pay back) | 34% |
| Silver | 2,500 | 48% | about 43% |
| Premium Silver | 3,750 | 29% | about 34% |
| Jumbo Premium Silver | 7,500 | 31% | about 35% |

The v2 silver figures are the auditor's EVs, adjusted for the 148 rare cap instead of 2x rating.

## Why these numbers

- **A fat middle and a thin top, but a top players can see.** The reward-prediction-error signal is largest for
  rewards that are rare but do arrive (Schultz, Dayan & Montague 1997, *Science*). Rarer loot-box items are rated
  more rewarding (Larche et al. 2021). A walkout every ~22 Premium Gold packs (about one every 4-5 weeks per engaged
  player from packs alone, more with rewards) is rare enough to feel special and frequent enough to be believed. Live,
  it was once every ~32 weeks.
- **About 2x retail, not more.** The audience is FIFA players frustrated with EA's odds. Premium Gold at about 2x
  retail for 82+ and 84+ answers that, without making 84+ cards worthless.
- **Packs stay a coin sink.** Quick sell is now the only guaranteed exit, because the market is players-only and has
  no AI buyers. Every pack's quick-sell return is 29-44%, and fewer than 3% of packs (gold only) return more than
  their price, so no pack prints coins. Value beyond quick sell has to come from other players at real market prices.
- **The market can absorb the supply.** At ~40k pack coins per engaged player a week (4 Premium Gold + 1 Gold +
  0.25 Jumbo per player), each 1,000 players mint per week: about 101 base 86+ cards (1.6 per 86+ card), 116 TOTW
  in-forms (1.7 per in-form) and 11 Icons (0.1 per Icon). Live, the figures were 28, 0 and 3. Icons stay scarce: one
  per player roughly every 90 weeks from packs. The 5% seller tax and SBCs remain the sinks.
- **Odds disclosure.** Evidence favours publishing odds (Xiao et al. 2022). The owner decided earlier (D15) to keep
  odds out of pack bios and post them outside the game; the tables above are that post's source.
- **What's left out on purpose:** silver TOTW (only about 3 silver in-forms are in TOTW 1-3, so they would repeat),
  pity timers on store packs (Match Packs already have one), and price changes.

## Apply

The deploy applies it: migration 113 runs when the new image starts, together with the code. Nothing else is needed.

To re-apply the values by hand later (say after a panel experiment), open the panel tunnel
(`ssh -i ~/.ssh/aurora21_csharp -N -L 18448:127.0.0.1:18448 root@<vps>`), then:

- `node apply.mjs` does a dry run: it stages and validates, then discards.
- `node apply.mjs --keep` stages and leaves the changeset open to preview in the panel.
- `node apply.mjs --publish` publishes.

The model and its inputs are in `F:/Aurora 21 Claude Code/pack-odds-2026-10/`: `sim.mjs`, `final.mjs`,
`final-results.json`, the live snapshot in `data/`, and the bronze/silver audit in `bronze-silver/`.
`apply.mjs` has not been run against a live panel yet; migration 113 is covered by `PackOddsV2MigrationTests`.
