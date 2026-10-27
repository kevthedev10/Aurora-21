-- A keepsake copy of every store pack as it was before the launch store (owner: "a copy in a file", nothing more).
-- Prints one JSON document; README.md saves it next to the predeploy dump. Nothing loads it back.
SELECT json_build_object(
    'exportedAt', now(),
    'note', 'Aurora 21 store packs before the 2026-09-29 launch store; a keepsake copy, not loaded by anything.',
    'storePacks', (SELECT json_agg(p ORDER BY p.pack_id) FROM ultimateteam.store_packs p WHERE p.kind = 'store'));
