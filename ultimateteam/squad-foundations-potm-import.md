# Disabled Squad Foundations and POTM 1 imports (2026-10-04)

The owner supplied `fifa21_league_squad_foundations_objectives.json` (SHA-256
`1253b327c46360ba9ce574cea6a9ee52cbe27a263df6f9a2f3a2abee016074c8`) and
`fifa21_potm_1_sbc (1).json` (SHA-256
`4b58d1f4b5617f7a96003486c2c9fea2f0573c94d7ed16456cd9dcd6ab5a5a1f`).
These files are reference content. Their definitions are adapted to the existing
engine, rather than described as recovered retail server responses.

Migration 154 adds seven Squad Foundations groups, all disabled. Fresh databases
receive the same disabled definitions from `rules/objectives.json`; upgrades append
them to stored rules, preserving existing definitions and live order. New numeric IDs
are allocated beyond catalogue IDs, group start history and reward claims. Source keys
prevent duplicate imports on replay. Counters and settled claims are preserved.

At the owner's request, all these objectives accept Division Rivals or FUT Champions.
Managerial Masterpiece is removed. Minimum eleven league starters becomes three;
minimum seven becomes two. The separate four-Irish-player restriction stays four.
Match, win, goal and assist targets are halved, rounding odd numbers up. Player ratings,
reward quantities and XP are preserved where the source specifies them. Finesse goals,
cross assists and through-ball assists remain special requirements.

The engine adds per-objective counters, starting-XI and scorer league/nation checks,
named player rewards, explicit rewardless claims and group XP. Owned item metadata
supplies league/nation; the settled server mode and outcome supply eligibility and wins.
The existing match settlement callers still decide whether an end counts and guard
replays. A group's counters start from its first live publication through the existing
baseline mechanism. Player rewards go to unassigned, with the authored tradeability.
The panel preserves and displays these restrictions and rewards.

## Source gaps and card provenance

All 21 objective rewards resolve to existing local Squad Foundations cards (rarity 87,
matching ratings). Their IDs are existing `wefut-custom-avatar` catalogue identifiers,
not newly recovered native IDs. The source Aaron Long passing value is 64 whereas the
local card is 65; the existing card is preserved. No player metadata is overwritten.
The original EFL I file gives total XP 1500 and group XP 300 without allocating the
remaining 1200 to its objectives; no per-objective XP is invented. Two EFL I objectives
have unspecified rewards and are explicitly rewardless. Unknown pack tradeability is
conservatively authored untradeable. Project pack 564 supplies the one-player 75+ rare
gold reward, grant only and absent from the shop; a conflicting operator definition
causes the migration to stop rather than overwrite it.

## Native special scoring: still unverified

Offline inspection used the installed supported build:

- FIFA21.exe SHA-256 `44d0acc62a214c6f4dab2b8a4f4bdd068869c636f15cc262035b4edc862a1494`.
- CardsDLL_Win64_retail.dll SHA-256 `37133be975c0552c919e2901b7da203eb458792b5bf43764821ad687de548a49`.

Observed in CardsDLL's match-end serializer, RVA 0x2B1261–0x2B1B72: `events` contain
`attributes`, including `key:"name"` with `value:"goal"` or `"assist"`, `player_id`
serialized as a signed integer string, and repeated integer `flag` attributes. The
name getter at RVA 0x391BA0 maps event types 0 and 1 to goal and assist. The adapter
parses this shape and counts one event once even when a flag repeats.

Unconfirmed: which flag denotes finesse/cross/through-ball, and whether player_id
names a footballer asset ID or an owned item ID. Gameplay ShotFlags names and other
FIFA implementations do not prove the FUT event mapping. No debugger/MCP connection
or new live match capture was used. `scoringEvents` therefore has null mappings, and
content validation refuses to activate groups requiring them. Synthetic adapter tests
verify parsing, bounds and filtering; they do not establish native flag semantics.
Ordinary goals/assists never substitute for the special requirements.

The next verification is a controlled local match report for each scoring type,
correlating the event with known owned item and footballer IDs, or tracing the native
producer's flag assignments for this exact build. Retain only redacted event evidence,
then install the reviewed mapping and test actual objective progress before activation.

## POTM 1

Migration 155 adds one non-repeatable, disabled SBC set with three challenges. Each
requires eleven players, rating 84, one TOTW and one player of its named league:
Premier League chemistry 70, Bundesliga 75, Ligue 1 60. Rewards are exact existing
local POTM cards: Calvert-Lewin 84 (`50553127`), Kramarić 86 (`50548002`), Niane 84
(`67347889`), then Ansu Fati 86 (`84139084`) for completion; all untradeable.
Their catalogue provenance is also `wefut-custom-avatar`. FUTBIN page IDs from the
source are not used as resource IDs. Existing player rewards and set completion
handling are reused. League requirements use the existing LEAGUE_ID eligibility key;
server submission now checks the matching owned-player count. Replay preserves an
operator's existing set edits and live choice.

No migration was applied to the maintainer's club or VPS. No game or service was
restarted. Deployment/start-up applies the embedded migrations; adding content does
not enable either import. Native/in-game verification remains separate from fixture
and database tests.

## Verification

The final focused server run passed 37 checks, covering conditional counters through
owned-item lookup, live baselines, special-event parsing with synthetic flags, once-only
objective/player/group-XP claims, fresh and replayed migrations, retired-ID allocation,
POTM reward cards, league/TOTW submission rejection and disabled panel listings.
An earlier broader panel/server run passed 124 of 125 checks; its sole failure was the
old SBC count (56 rather than 57). The corrected check now explicitly asserts POTM 1
is listed disabled and passed in the focused rerun.

Panel typecheck/build and source stamp passed; 15 objective/SBC frontend checks passed.
The artwork utility's six checks and the 356 repository hygiene checks passed. The
completed artwork folder contains 13,253 PNGs; copied image and index hashes were
verified at the Downloads destination. Touched tracked diffs passed whitespace checks.
The full solution suite, Docker/VPS and live in-game objective/SBC tests were not run.

## Local scoring capture (added after the import)

The shared POST/PUT `/ut/game/fifa21/match/end` route now logs `SCORING EVIDENCE`
at Information level before Draft/club settlement and the objective eligibility gate.
It runs with the normal launcher logging settings, including when the imported groups
are off or an end earns no progress. No guessed finesse/cross/through-ball mapping is
installed. This is receipt evidence, not proof that the match settled or earned rewards;
a repeated HTTP report produces another receipt line without granting progress twice.

The bounded JSON records up to 128 events, each event's goal/assist name, numeric
player_id and all numeric flags among its first 64 attributes, plus up to 23 reported
player slots. Owned item IDs are correlated with catalogue resource/asset IDs, league
and nation. Per-player reported goals/assists help identify the scorer and provider.
Missing events, unknown names and truncated lists are explicit. Arbitrary attributes,
strings, headers, credentials, persona/club IDs and the rest of the request are omitted.
This focused evidence does not require the broad `diagnostics.capture` traffic switch.

For the local launcher, rebuild through `Build-FIFA21-Launcher.ps1` with the owned
launcher/server stopped, then start through `RUN-FIFA21.cmd`. The standard server log
is `%LOCALAPPDATA%\FIFA21UltimateTeam\logs\ultimateteam.log` (or the runtime override's
`logs\ultimateteam.log`), also visible in the panel's Logs section. Search for
`SCORING EVIDENCE` after completing the match. If it reports no events, retain that
line: it establishes that this build/mode did not send events in the expected field.

Use a normal completed match and note player names and approximate minutes for a
normal goal, a finesse goal, a goal assisted by a cross and one assisted by a through
ball. Prefer different scorers/providers where practical; avoid a finesse finish for
the cross/through-ball control goals. The first capture can use Squad Battles, because
this diagnostic reads the same match-end route; whether that native mode populates
these events still needs confirmation. The disabled Rivals/Champions objectives do
not need to be activated for this capture.

Scoring-capture verification: all 14 focused checks passed, including the real POST and
PUT match-end routes with disabled objectives and no-progress outcomes, numeric flag
preservation, catalogue ID correlation, bounds and credential filtering. The supported
builder completed with `-IfNeeded` and replaced the local server executable; no game
or server was launched. This verifies the rebuilt server and synthetic route evidence,
not a native match capture.
