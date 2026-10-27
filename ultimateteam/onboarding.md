# FUT first-run onboarding

How a new player's club comes to exist and completes FIFA 21's first-run flow. Evidence labels follow
the [archived onboarding analysis](../archive/documentation-2026-10/docs/fut-onboarding-analysis.md):

- **Native**: recovered from CardsDLL. The fuller notes are in that archived analysis; the earlier revision is
  `git show 54fd936:docs/fut-onboarding-analysis.md`.
- **Observed**: seen in traffic. The only capture is access-log lines from the Python server on 2026-09-24.
- **Policy**: our own choice, not EA's.

**Status (2026-09-29):** two new players went through the flow on the VPS (sha-a01b3cf, 23:34 and 00:18 UTC):
465 → `GET user/historical` → `POST user/action/ONBOARDING_COUNTRY_<n>_SELECTED` → `POST user` one second later → loan
picker → `PUT loan/player/<defId>`, `PUT item`, `PUT squad/1?onboarding=true` → `GET`/`POST onboarding/kits` →
`GET onboarding/badges`, `POST onboarding/badge/<rid>` → hub. The owner's screenshots showed three faults, fixed since:
loan cards showing 0 matches, blank kit tiles, and no chance to name the club (see below).

## Who gets it

- **Hosted server:** `serve --hosted` sets `PlayerRegistryOptions.NativeClubCreation`, so a player's new club waits
  for FIFA to create it (`GameContext.NativeClubCreation`).
- **Clubs that already exist:** they have the `relational_club_initialized_v1` flag and keep their club.
- **Local and seeded servers:** they still seed a club when it opens, except after a deliberate panel club reset.
- **Panel reset:** Players → Overview → Reset club clears club contents and progress after confirmation,
  only while the player is offline. The persistent `native_club_creation_required_v1` flag makes an empty
  club await FIFA's country/CreateUser flow in both local and hosted profiles. Maintenance does not seed it.
  CreateUser clears that flag after completing native creation; the normal initialized/native flags then
  prevent repeated onboarding. This path has isolated route tests; native FIFA acceptance is pending.
- **Club maintenance:**
  - It leaves a club awaiting creation empty.
  - It never tops up a club FIFA created (`native_club_created_v1`) with the configured initial players, manager or kits.

The 2026-10-02 reset checks cover clearing all current club-owned tables, preserving
another club and player access, exact confirmation, active-session refusal,
transaction rollback on an audit failure, and the 465 → country → CreateUser → 200
route sequence for local and shared clubs. The focused onboarding/reset checks,
12 admission/shutdown/build checks and existing market refund test pass. The
isolated startup harness also passes across fresh, custom and restarted profiles
(518 loopback requests). These are server fixtures, not native FIFA acceptance.

## The flow

Paths are under `/ut/game/fifa21`.

| Step | Request | Server | Evidence |
| --- | --- | --- | --- |
| No club | `GET usermassinfo` | **465** while the club awaits creation (`ClubRoutes.MassInfoAsync`). FIFA reads 465 as "no club" (native result 1); a 404 would be an error. | Native |
| | `GET v2/user/accountinfo`, `GET user/accountinfo` | An empty `userClubList` and `returningUser: 0` | Native, whether it gates is unconfirmed |
| Historical | `GET user/historical` | `{clubName, clubAbbr, isReturningUser:false, returningUserRewards:[]}`, where the name is the one derived from the player's recorded name | Native |
| Country | `POST`/`PUT user/action/ONBOARDING_COUNTRY_<n>_SELECTED` | Stores nation `countryNations[n-1]`. Answers `{}`, or 404 for an unknown n. | Native list; the verb is unconfirmed |
| CreateUser | `POST`/`PUT user` `{useFut1Data, clubName, clubAbbr, purchased}` | Creates the club in one transaction: the starter squad and manager (untradeable), squad 1, the starter consumables, and the typed name and abbreviation (cut to 30 and 5 characters). Answers `{bonusPacks:false, login, squad, starterPack[], userData}`. A repeat answers the same club. | Native shape; the verb is unconfirmed |
| Loan | `GET loan/players` (FIFA used the `userMassInfo` copy), `PUT loan/player/<defId>`, `PUT item` | Base-card offers ranked 1-5 per position; the signed loan goes to unassigned and its reply is a bare item. A loan card's `contract` is its matches left: FIFA gives a card only a loan flag and its contract count (CardsDLL 0xDE7C0), so `contract: 0` showed "0". | Native + Observed |
| Loan explore | `PUT squad/<id>?onboarding=true` | Saves the squad, then records loan marker `loanplayerreward` key 0 = rid & 0xFFFFFF when the loan is in the starting eleven | Native + Observed; the marker is policy |
| Kits | `GET`/`POST onboarding/kits` | A random choice seeded by the club, nine a side: the screen is a 3×3 grid that shows the home list, then the away list, and draws a blank white shirt in every empty slot. The chosen pair is granted untradeable and equipped, the reply gives the owned ids, and `onboarding` keys 1 and 2 are recorded. | Native shape + Observed; the pool is policy |
| Badge | `GET onboarding/badges`, `POST onboarding/badge/<rid>` | A random choice seeded by the club. The pick is granted and equipped, the reply is `{badgeId}`, and onboarding is complete. | Native shape; the pool is policy |
| Done | `userMassInfo` | `onboardingClientData` {0:3, 1:home, 2:away, 3:badge}, the loan marker, and no loan offers | Native reading |
| Club name | `POST`/`PUT user/club?profaneNameChange=<true\|false>` `{clubName, clubAbbr}` | Offered through the user document's `clubNameChangeAllowed: 1` while a club FIFA created is unnamed. Keeps the typed name and abbreviation (cut to 30 and 5 characters) once, sets `native_club_named_v1` and answers `{}`; a second rename gets 403. | Native (below); not yet seen in game |

FIFA resumes from the markers. With no loan marker it goes to loan select; with the loan marker, to kits; with keys 1
and 2, to the badge; with key 3, to the hub.

The server sends 3, not the "just finished" 1, after the badge. That is what the Python server sent to every player who
reached the hub on 2026-09-24. The 1 → 3 acknowledgement has never been seen in game.

## Policy (rules/onboarding.json)

- **Club name:** the player's typed club name and abbreviation are kept (owner, 2026-09-28). The persona, the name shown
  on the player, stays their recorded Discord name.
  - FIFA does not ask for a name before CreateUser. It sends CreateUser straight after the country screen with a
    name it makes up itself (2026-09-29 13:27 UTC: "Bell Ville FC" / "Bel", not GetHistorical's). The naming screen is
    ChangeClubName, and FIFA offers it **at the next FUT login, not at the end of onboarding** (observed 2026-09-29:
    the flag was 1, onboarding ran to the summary, and FIFA went straight to the hub).
  - CardsDLL evidence (retail `CardsDLL_Win64_retail.dll`, RVAs):
    - the user parser (0x329190) stores `clubNameChangeAllowed` at user +0x64; usermassinfo's handler (0x3679D0)
      hands the parsed user to the club manager, whose copy sits at +0x12B28;
    - the FUT entry router (0x40D10) reads the onboarding step (+0x12D24): 0 opens the loan screen, 1 the kits,
      2 and 3 the badge, and 4 (done) opens `GoToChangeClubName` when the copy's byte +0x64 is 1, else the hub;
    - the onboarding summary's Continue (0x1A3930) marks onboarding complete and sends `advance` (or
      `GoToReturningUser`) straight to the hub; it never passes through the router, so the flag is not read there;
    - the hub's other way into the screen, `ShowClubReportPopup` (0x15A520), is EA's "your club name was reported"
      popup, gated by abuse-report state; its wording rules it out as a welcome prompt;
    - Apply builds `user` + `/club?profaneNameChange=%s` (0x31E5A0) with body `{clubName, clubAbbr}` (0x31E660);
    - the reply parser (0x31E9F0) reads only an optional `dynamicObjectivesUpdates`.
  - A natively created club gets `clubNameChangeAllowed: 1` until it has been named once; every other club gets 0.
  - The Python server kept this at 0 because "the rename flow crashes after Apply". It never answered the Blaze
    FilterForProfanity check FIFA runs on every typed name, which the C# server does answer (an empty reply crashed the
    game), so that crash is not expected here. This is inferred, not yet played.
- **Starter squad:** 23 untradeable base cards rated 55-74, one per f442 position, then the substitutes and reserves.
  - Each slot is drawn at random, seeded by the club and nation, from the 20 eligible cards rated closest to 63.
  - Every other slot comes from the chosen country.
  - No footballer appears twice.
  - A manager of that country rated 74 or below completes the squad.
  - This ports the Python server's `onboarding.py` (`13345f3`).
- **Loans:** for each of 8 positions, the 5 best base cards (rareflag ≤ 1, not special) of footballers the club does not
  own, for 7 matches.
  - Offer ids are `10001 + position × 300 + place`, so they stay stable whatever the club owns.
  - A seeded club, which already owns the best base cards, gets the next best, never a duplicate.
  - FIFA is sent a loan's games as its contract count (`ItemFactory.ShownContracts`, in `NativeItems.PlayerItem` and
    the squad's `RenderNativePlayer`). The stored loan keeps 0 contracts, so loans signed before this fix show right too.
  - Nothing counts loan games (or contracts) down after a match yet, so a loan never runs out.
- **Kits and badges:** 9 home kits, 9 away kits and 9 badges drawn at random from the catalogue for each club, filling
  the 3×3 grid. The badge screen is assumed to use the same grid. The choices are seeded, so each visit shows the same ones.

## Still to check in the game

1. The loan cards show 7, the kit grid is full on both lists, and the badge grid has no blank tiles.
2. The club-name screen appears at the first FUT login after onboarding (not yet seen: no login since the 2026-09-29
   creation), and
   Apply is accepted without a crash.

Watch the unknown-request log (`debug_unknown_http`) and the `ONBOARDING` log lines during the first new-player launch.
