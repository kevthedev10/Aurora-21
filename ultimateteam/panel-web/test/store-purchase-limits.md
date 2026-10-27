# Store purchase limits — native test evidence

## Implemented contract

The pack editor's `purchase_limit` is a lifetime allowance per club and pack ID.
Zero is unlimited. Successful store purchases increment the persisted counter in
the same transaction as payment and item generation. Gifts/rewards do not consume
the allowance. Failed draws roll back payment and the counter. Exhausted packs
are omitted from the store listing. Disabling and re-enabling a pack does not reset
usage. Purchases before migration 105 are not reconstructed.

The limits are mutually exclusive:

- Per-club packs send `purchaseLimit` and `purchaseCount`, with `quantity` equal
  to that club's remaining allowance and `globalAvailableQuantity: -1`.
- Community packs use migration 107's persistent `community_pack_purchases`
  counter, shared by every club. They send `purchaseLimit: 0`, with `quantity`
  and `globalAvailableQuantity` equal to remaining shared stock.
- Unlimited packs send `purchaseLimit: 0`, `quantity: 0` and
  `globalAvailableQuantity: -1`.

Community purchases lock the shared counter before payment, and commit its
increment with the pack draw. Failed draws/payments and gifts/rewards do not
consume stock. Exhausted packs disappear from every club's store and purchases
from stale listings are refused. Deleting a club never restores stock. Community
counts start with migration 107, including subsequent unlimited store purchases;
disabling/re-enabling or changing limit mode does not reset either counter.
Increase the configured total to add more allowance, or use a new pack ID for a
new independent sale. The panel clears the opposing limit when setting a nonzero
value; backend staging and database constraints forbid both nonzero limits.
The separate `packContentInfo.itemQuantity` continues to describe items in a pack.

## Native evidence (2026-10-01)

Read-only inspection of the installed FIFA 21 `CardsDLL_Win64_retail.dll`, SHA-256
`37133be975c0552c919e2901b7da203eb458792b5bf43764821ad687de548a49`:

- Observed native JSON field strings: `purchaseLimit` (file offset `0x494c18`),
  `purchaseCount` (`0x494ce8`), `quantity` (`0x494e00`) and
  `globalAvailableQuantity` (`0x493208`).
- Observed native UI strings: `USER_PACKS_REMAINING`, `PACKS_REMAINING`, and
  `GLOBAL_PACKS_REMAINING`.
- Observed UI instructions at VA `0x18004d832`–`0x18004d8ea` subtract values
  returned by getters `0x180049640` and `0x180049630`, then populate both the
  packs remaining and user packs remaining UI fields. These getters read object
  offsets `0x70` and `0x6c`. The finite-count branch also checks the getter at
  `0x180049620` (offset `0x68`) is positive and less than 1,000,000.

Follow-up after the maintainer's live test: enforcement worked, but the counter
did not display. Further read-only inspection traced the JSON-to-UI mapping:

| JSON field | Native enum | Parser branch VA | Pack record offset | UI object offset |
| --- | --- | --- | --- | --- |
| globalAvailableQuantity | 0x1dd | 0x180327743 | 0x600 | 0x68 |
| purchaseLimit | 0x3a4 | 0x1803279b0 | 0x604 | 0x70 |
| purchaseCount | 0x3a0 | 0x1803279d2 | 0x608 | 0x6c |
| quantity | 0x3b1 | 0x18032797c | 0x5fc | 0x64 |

Observed: the native enum name table at VA `0x18044aac0`, accessed by
`0x18037ad50`, maps these enum values to these names. Parser `0x1803270e0`
dispatches them via its switch tables and fills a temporary pack record at
`rbp+8`. Copying preserves those offsets; `0x180074a30`–`0x180074a54` copies the
record values into the UI object. Missing available quantity stays nonfinite.

Observed: the branch at `0x18004d7d9`–`0x18004d80d` requires a positive purchase
limit and available quantity between 1 and 999,999 before assigning
`USER_PACKS_REMAINING = purchaseLimit - purchaseCount`. Otherwise, the code
leaves that user-specific field at zero and fills only `PACKS_REMAINING`.

Inferred root cause: the original response omitted the availability field, so
the user-specific display branch was skipped. The corrected response supplies
the club's remaining allowance to activate it. API assertions alone do not prove
the revised native UI shows the count; the live game retest below remains required.

## Next game check

1. Restart through `RUN-FIFA21.cmd` after stopping the runtime. The panel is
   already built; the launcher rebuilds changed server sources/resources.
2. Stage/publish a store pack with limit 3 and a nonzero Aurora Tokens price.
3. Check the store displays the remaining allowance and both prices.
4. Purchase three times, dealing with Unassigned between purchases. Check the
   allowance falls to 2, then 1, and the pack disappears after the third purchase.
5. Re-enter FUT/restart and verify usage is retained. Check a gifted copy still
   opens and another club has its own allowance.

Migration 106 enables `club.fifaPointsPurchases`, which previously removed Points
from the tile's currencies despite its price fields being present. This shared
rule also permits paid Draft Points entry. It grants no Points or daily drops.

## Automated checks

On 2026-10-01:

- 18 focused pack panel tests and the production panel build passed.
- Four store-limit tests passed against throwaway PostgreSQL databases: finite
  allowance/Points payment/gift exception, failed-draw rollback, club reopening
  with independent club counters, and upgrading from migration 104 while keeping
  items and balances. The reopening test passed on its focused rerun after fixing
  the fixture's maximum open-club capacity.
- Three focused economy/Draft cases passed, covering no daily Points grants and
  paid Draft entry with the purchasing rule disabled/enabled.
- Four promo selector/per-promo pick odds cases passed separately.

The maintainer's first live test confirmed enforcement but failed counter display.
The maintainer's second live retest still did not show the counter after the
availability-field correction. No full regression suite was run for these changes.

## Quantity-sale classification follow-up (2026-10-01)

Observed in the same CardsDLL build:

- Native field enum `0x11d` is `dealType`; its parser branch starts at
  `0x1803273a4`. It lowercases the string and compares it with `promo`
  (string VA `0x18048cdd4`), then sets the record's promo flag at offset `0x614`.
- The pack UI constructor copies that flag to UI object offset `0x102`
  (`0x180074b12`), read by getter `0x180049520`. The provider sends it to
  `IS_PROMO` at `0x18004cef9`–`0x18004cf1e`.
- Native field enum `0x402` is `saleType`; its parser starts at
  `0x1803279f4`, accepts `NONE`, `QUANTITY`, `TIME` and `TIME_QUANTITY`, and
  stores sale mode 1 for `QUANTITY` at `0x180327a4e`.

The response previously omitted both classifications. Limited store tiles now
send `dealType: "promo"` and `saleType: "QUANTITY"`; unlimited tiles send an
empty deal type and `NONE`. The configured tab, pack art, contents and prices
remain driven by their existing configuration.

Inferred: the missing native sale/promo classification prevented the expected
limited-pack presentation despite supplying count values. The precise authored
UI visibility conditions have not been recovered, so this is a candidate fix;
live verification is required. The target is FIFA's details pane showing
`Packs Remaining:` with the remaining number underneath, as in the maintainer's
retail reference. Limited-time packs and previews are separate TODO items.

Verification: seven focused checks passed (two store allowance cases against
throwaway PostgreSQL databases and five response-writer parity cases). The response
writers and their manifest were regenerated. The native display remains unconfirmed
until the game retest; no live server was restarted during this change.

## Separate community and per-club allowances (2026-10-01)

Observed: the maintainer's screenshot at 18:39 shows both `Community Limit: 2`
and `Packs Remaining: 2`. This confirms the native count presentation works,
but also confirms that sending a club allowance as `globalAvailableQuantity`
misrepresents it as shared stock. The current contract above removes that alias
and adds real community stock instead.

Game checks after restarting the updated runtime:

1. Publish a per-club allowance. Verify `Packs Remaining:` still shows the club's
   remaining count, with no artificial community limit.
2. Set a community allowance in the editor and verify the per-club field clears.
   Publish it. Confirm FIFA displays the shared remaining amount; record its
   exact native label and check purchases reduce it for everyone.
3. Exhaust stock and verify another club cannot purchase from an old listing.
   Gifts still open without consuming stock. Restart and confirm the counter
   stays exhausted. Setting both limits must be rejected, including partial
   API updates which conflict with the stored opposing limit.

The panel is built. No live runtime was restarted or real club changed for the
implementation checks. Limited-time packs and previews remain separate work.

Verification: 19 pack panel tests and the production build passed. All eight
focused backend cases passed across the focused run and last-copy rerun,
including concurrent cross-club purchases, gift exemption, failure rollback,
mode switching through Publish, and upgrade preservation.

Observed in the maintainer's subsequent game test on 2026-10-01: both exclusive
limit modes display `Packs Remaining:`. The extra `Community Limit:` label no
longer appears. This confirms the display checks for both modes; the same label
represents a club's remaining allowance or shared stock according to the selected
mode. Cross-club deduction, exhaustion and restart checks above remain pending
live confirmation.
