# Launch-day operation record — 2026-09-29

These scripts describe an explicitly authorized one-off production operation,
not normal setup or upgrade steps. They do not define the current panel store
lineup. Use [the deployment guide](../README.md) and reviewed panel changes for
current operation; a club wipe requires explicit authorization.

The owner asked for two one-off production changes, made during the first deploy of FIFA's own club creation
([onboarding.md](../../onboarding.md)):

- **Every club starts over** ([wipe-clubs.sql](wipe-clubs.sql)), so each player creates theirs through FIFA's first-run
  screens.
- **The store sells only the standard packs** ([launch-store.sql](launch-store.sql)): Bronze, Silver and Gold plus their
  Premium versions, at the other Aurora servers' prices, and a Cosmetics Pack with the insides of Horizzon's Identity
  Crisis. Pack odds stay as they are until the owner sends new ones.

Neither is a migration. The store is operator data (the panel edits it live), and a local or test install keeps the
bundled store and its clubs. `LaunchStoreScriptTests` runs both store scripts against the current schema.

| Pack | Coins | FIFA Points | Items | Rares | Tab |
| --- | --- | --- | --- | --- | --- |
| 1 Bronze Pack | 400 | 8 | 12 | 0 | Bronze |
| 4 Premium Bronze Pack | 750 | 15 | 12 | 1 | Bronze |
| 2 Silver Pack | 2,500 | 50 | 12 | 0 | Silver |
| 5 Premium Silver Pack | 3,750 | 75 | 12 | 1 | Silver |
| 3 Gold Pack | 5,000 | 100 | 12 | 1 | Gold |
| 6 Premium Gold Pack | 7,500 | 150 | 12 | 3 | Gold |
| 7 Cosmetics Pack | 5,000 | 100 | 2 balls, 3 badges, 4 kits, 3 stadiums | 0 | Promo |

Prices, points and rares come from Aurora 16 (`src/fut/pack-catalog.js` DEFAULTS), which match FIFA 15 Local FUT's
`pack_settings.json`. Every other store pack is removed. `launch-store.sql` kept packs 42-47 off sale (`admin_only`)
because objective awards granted them; the owner then had those go too ([remove-promo-packs.sql](remove-promo-packs.sql)),
and the objective groups and Season Progress levels grant FIFA 21 reward packs in their place:

| Old award | Now |
| --- | --- |
| 47 84+ x50 Pack | 402 Jumbo Rare Players Pack |
| 45 Icon Vault | 403 Mega Pack |
| 44 Galacticos Pack, 46 Deep Freeze Mega Pack | 404 Rare Mega Pack |
| 42 JayFreezy Pack, 43 Ballon d'Or Pack | 405 Ultimate Pack |

Removing the stadium-selector packs (11-20) also removes the only way to change the home stadium (the in-game editor
crashes); put one back from the panel if that is wanted.

## Order on the VPS

Run from `/opt/aurora21`, with maintenance on in the panel so nobody connects. `psql` runs inside the `db` service:
`docker compose exec -T db psql -U ultimateteam -d ultimateteam`.

1. Take a dump: `docker compose run --rm backup once predeploy`.
2. Save the keepsake copy of the old store:
   `... psql -At < export-old-store.sql > backups/old-store-packs-20260929.json`, then copy that file off the VPS.
3. Stop the game: `docker compose stop server redirector`.
4. Run the club wipe: `... psql < wipe-clubs.sql`.
5. Run the store script: `... psql < launch-store.sql`. It stops without changing anything if packs 1-3 or 66 are
   missing.
6. Load the new image and start it: `docker compose up -d`. The server applies migration 065 at start, recreates its
   own club, and logs `CLUB CREATION native`.
7. Check the store in the panel, then turn maintenance off.
8. Later, once an image with the new objective rewards is live: `... psql < remove-promo-packs.sql`. It stops without
   changing anything while a club holds an unopened pack 42-47.

Restoring the dump from step 1 undoes all of it (see [the deploy README](../README.md#backups)).

## Phase 1 (pack odds, 2026-09-29)

[phase1-store.sql](phase1-store.sql) turns the launch store into the phase-1 store (`pack-odds-2026-09/PLAN.md` section 3.3). It
runs **after** an image with migration 071 is live, because it sets the `banded_odds` (068), `consumable_mix` and
`manager_league_card_share` (071) columns those migrations add, and it stops without changing anything unless it finds the
launch store.

| Pack | Coins | Items | Rares | In-form chance per rare slot | Notes |
| --- | --- | --- | --- | --- | --- |
| 1 Bronze Pack | 400 | 12 bronze | 0 | – | Managers and chemistry styles as before; half the manager hits are a manager league card (1, 2, 4, 5) |
| 4 Premium Bronze Pack | 750 | 12 bronze | 1 | 0.5% (bronze in-form) | |
| 2 Silver Pack | 2,500 | 12 silver | 0 | – | |
| 5 Premium Silver Pack | 3,750 | 12 silver | 1 | 0.5% (silver in-form) | |
| 3 Gold Pack | 5,000 | 12 gold | 1 | 0.4% | Players only; a volume pack with its own thinner rare table (goldRareBandTables volume) |
| 6 Premium Gold Pack | 7,500 | 12 gold | 3 | 1.1% | Players only |
| **8 Rare Gold Pack** (new) | **25,000** | 12 gold | 12 | 1.1% | Players only |
| 7 Cosmetics Pack | 5,000 | unchanged | | | |
| **9 Consumables Pack** (new) | **3,000** | 12 distinct: 4 manager league cards, 4 position modifiers, 4 chemistry styles (never Basic) | – | – | `consumable_mix`; the league cards are generated definitions (migration 071) |

- Every pack draws the **phase pool** (`rules/pack_odds.json`): base cards without UCL, Libertadores or Sudamericana
  cards, plus TOTW 1-3. Gold rare slots draw by rating band. FIFA Points buy nothing (coins only).
- **No odds in the bios** (owner decision D15). The odds are published outside the game, from the calculator in
  `pack-odds-2026-09/sim`.

Order: after step 6 of a deploy (the image with migration 071 is up), run `... psql < phase1-store.sql`, check the store
in the panel, then turn maintenance off.
