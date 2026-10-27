# Local FIFA panel acceptance

Run FIFA and the React panel through the normal launcher, using the same local
database. Stop the throwaway review server first. Back up the local database and
record original values before edits. Publish only the changes intended for each test.

For each test, record **pass/fail**, the observed result and whether FIFA needed
screen re-entry or a new login. Do not mark a test passed from the panel response alone.

| Done | Test | Expected result to check in FIFA |
| --- | --- | --- |
| [x] | Grant a player to your club | PASS — maintainer confirmed card grants worked in FIFA on 2026-10-01. |
| [x] | Grant tradeable and untradeable copies | PASS — maintainer confirmed both options worked in FIFA on 2026-10-01. |
| [x] | Grant Developer / Pro Cards | PASS — maintainer confirmed grants worked and these cards were forced untradeable on 2026-10-01. |
| [x] | Grant a pack | PASS — maintainer confirmed the grant/opening test worked for both tradeable and untradeable packs on 2026-10-01. |
| [x] | Grant a player pick | PASS — maintainer confirmed a 1-of-4 pick worked and selected an 84-rated player on 2026-10-01. Pick tradeability, token consumption and absence of extra grants were not separately reported. |
| [x] | Edit an existing card's rating and stats | PASS after fix — maintainer confirmed Bruno's Pro Card no longer resets to 99 on R3 inspection on 2026-10-01. Six focused Pro Card tests also passed. Before-Publish behavior and every individual in-game stat were not separately confirmed in the native test. |
| [x] | Change card type and image | PASS with refresh limitation — maintainer confirmed on 2026-10-01 that club-only cards update type and images apply after leaving and re-entering FUT. Immediate image refresh remains in the [backlog](../../../todo.md). |
| [x] | Publish edits while the card is already in a squad | PASS with refresh limitation — maintainer confirmed on 2026-10-01 that leaving and re-entering FUT applies squad changes. An isolated API regression confirms fresh saved-squad responses carry published stats without removing the card. Immediate squad refresh remains in the [backlog](../../../todo.md). |
| [x] | Reopen the card editor after publishing | PASS — maintainer confirmed on 2026-10-01 that the caching/staging issue is fixed. Thirty focused editor tests also passed. Unsaved edits still require an explicit reload when published/pending values change. |
| [x] | Create a new card, publish and grant it | PASS — maintainer confirmed creation, publication and granting worked in FIFA on 2026-10-01. Name/frame/portrait details were not separately reported. Newly inserted cards cannot be reverted through History. |
| [x] | Remove a pack from the store lineup | PASS — maintainer confirmed on 2026-10-01 that disabling/publishing removes it from the store while an already-owned copy still opens. |
| [x] | Edit a pack's price and contents | PASS — maintainer confirmed the store price, purchase deduction and configured contents test worked in FIFA on 2026-10-01. |
| [x] | Change promo/event availability | PASS — maintainer confirmed the remaining availability test works on 2026-10-01, following the earlier guaranteed TOTW pack test. |
| [x] | Revert an existing-card or pack edit | PASS — maintainer confirmed reverting from History works on 2026-10-01. |
| [x] | Restart through the normal launcher | PASS — maintainer confirmed on 2026-10-01 that state has persisted throughout repeated launcher restarts during testing. |

For the card-grant passes above, screen re-entry/new-login requirements and an
actual market listing attempt were not separately reported.

For the first pass, use a low-value card and inexpensive pack. Test large balance
changes, destructive mode resets and maintenance separately if needed.
Do not expect a short pack-opening sample to prove bucket probabilities.
At the end, restore original card/pack values and store/event settings. Keep notes
and logs for failures; do not reset the club as cleanup.

## React replacement — 2026-10-01

The maintainer approved removing the classic panel. React is now the only UI at
`/panel/`; former `/panel/next/` bookmarks redirect to it. Classic assets and
fallback links were removed, and frontend output moved to `Resources/panel/`.
Twenty-six focused frontend tests, three server cutover checks, the source stamp
and the Windows server publish passed. No live runtime was restarted for that
change. Browser review after rebuilding/restarting and hosted hub framing remain
separate acceptance checks.
