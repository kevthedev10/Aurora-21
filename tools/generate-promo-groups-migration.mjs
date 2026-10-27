// Writes migration 076 from the historical group CSV and the maintainer's assignments for its missing cards.
// Deterministic: the same CSVs give the same file. Run from the root: node tools/generate-promo-groups-migration.mjs
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const csvPath = `${root}tools/fifa21_promo_group_cards.csv`;
const manualPath = `${root}tools/fifa21_promo_groups.csv`;
const outPath = `${root}ultimateteam/server/database-setup/migrations/076-promo-groups.sql`;

const bytes = readFileSync(csvPath);
const sha256 = createHash('sha256').update(bytes).digest('hex');
const text = bytes.toString('utf8').replace(/^﻿/, '');
const manualBytes = readFileSync(manualPath);
const manualSha256 = createHash('sha256').update(manualBytes).digest('hex');

/** One CSV line into fields (double quotes may wrap a field; "" inside is a quote). */
function fields(line) {
  const out = [];
  let current = '';
  let quoted = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (quoted) {
      if (ch === '"' && line[i + 1] === '"') { current += '"'; i++; } else if (ch === '"') quoted = false; else current += ch;
    } else if (ch === '"') quoted = true;
    else if (ch === ',') { out.push(current); current = ''; } else current += ch;
  }
  out.push(current);
  return out;
}

const lines = text.split(/\r?\n/).filter((line) => line.length > 0);
const header = fields(lines[0]);
const rows = lines.slice(1).map((line) => Object.fromEntries(fields(line).map((value, index) => [header[index], value])));
const manualLines = manualBytes.toString('utf8').replace(/^﻿/, '').split(/\r?\n/).filter((line) => line.length > 0);
const manualHeader = fields(manualLines[0]);
const manualRows = manualLines.slice(1).map((line) => Object.fromEntries(fields(line).map((value, index) => [manualHeader[index], value])));
for (const name of ['group_key', 'group_name', 'promo_family', 'category', 'member_order', 'resource_id']) {
  if (!header.includes(name)) throw new Error(`The CSV has no ${name} column.`);
}
for (const name of ['group_key', 'definition_id', 'resource_id', 'footballer_id', 'card_type_id', 'rating', 'position']) {
  if (!manualHeader.includes(name)) throw new Error(`The manual CSV has no ${name} column.`);
}

const sql = (value) => `'${String(value).replace(/'/g, "''")}'`;
const categories = new Set(['historical_squad', 'event_item_group', 'icon_tier']);

// Groups in the CSV's order (the first row of each group names it).
const groups = [];
const seen = new Map();
for (const row of rows) {
  if (!/^[a-z0-9_]{1,64}$/.test(row.group_key)) throw new Error(`Bad group_key ${row.group_key}`);
  if (!categories.has(row.category)) throw new Error(`Unknown category ${row.category} in ${row.group_key}`);
  if (!/^[1-9][0-9]*$/.test(row.resource_id)) throw new Error(`Bad resource_id ${row.resource_id} in ${row.group_key}`);
  if (row.member_order && !/^[0-9]+$/.test(row.member_order)) throw new Error(`Bad member_order in ${row.group_key}`);
  const known = seen.get(row.group_key);
  if (known) {
    if (known.name !== row.group_name || known.family !== row.promo_family || known.category !== row.category)
      throw new Error(`Group ${row.group_key} is named or classed differently on two rows.`);
    continue;
  }
  const group = { code: row.group_key, name: row.group_name, family: row.promo_family, category: row.category, sort: (groups.length + 1) * 10 };
  seen.set(row.group_key, group);
  groups.push(group);
}

// The manual CSV only assigns previously ungrouped cards. '-' explicitly means no group.
const newGroups = [
  { code: 'silver_totw', name: 'Silver TOTW', family: 'Team of the Week', category: 'event_item_group' },
  { code: 'next_gen', name: 'Next-Gen', family: 'Next-Gen', category: 'event_item_group' },
];
const originalGroupCount = groups.length;
for (const group of newGroups) {
  if (seen.has(group.code)) throw new Error(`New group ${group.code} already exists in the historical CSV.`);
  group.sort = (groups.length + 1) * 10;
  seen.set(group.code, group);
  groups.push(group);
}
const historicalResources = new Set(rows.map((row) => row.resource_id));
const manualResources = new Set();
for (const row of manualRows) {
  if (row.group_key !== '-' && !seen.has(row.group_key)) throw new Error(`Unknown manual group ${row.group_key}.`);
  for (const key of ['definition_id', 'resource_id', 'footballer_id', 'card_type_id', 'rating']) {
    if (!/^[1-9][0-9]*$/.test(row[key])) throw new Error(`Bad ${key} in manual row ${row.resource_id}.`);
  }
  if (!/^[A-Z]{1,3}$/.test(row.position)) throw new Error(`Bad position in manual row ${row.resource_id}.`);
  if (historicalResources.has(row.resource_id) || manualResources.has(row.resource_id))
    throw new Error(`Resource ${row.resource_id} occurs in both CSVs or twice in the manual CSV.`);
  manualResources.add(row.resource_id);
}

const groupValues = groups.map((g) => `    (${sql(g.code)}, ${sql(g.name)}, ${sql(g.family)}, ${sql(g.category)}, ${g.sort})`).join(',\n');
const memberValues = rows.map((r) => `    (${sql(r.group_key)}, ${r.resource_id}, ${r.member_order || 'NULL'})`).join(',\n');
// Main's migration 066 replaced these three Pro cards with Olise, Bruno Fernandes and Bas Dost.
// They were all explicit '-' rows; the new Pro cards are excluded below by their native resource IDs.
const replacedProResources = new Set(['167963031', '168006538', '167968024']);
const newProResources = [168019987, 167984358, 167961228];
for (const resource of replacedProResources) {
  const row = manualRows.find((entry) => entry.resource_id === resource);
  if (!row || row.group_key !== '-' || row.card_type_name !== 'Professional Player')
    throw new Error(`Expected replaced Pro card ${resource} to be an explicit '-' row.`);
}
const currentManualRows = manualRows.filter((r) => !replacedProResources.has(r.resource_id));
const manualValues = currentManualRows.map((r) => `    (${sql(r.group_key)}, ${r.definition_id}, ${r.resource_id}, ${r.footballer_id}, ${r.card_type_id}, ${r.rating}, ${sql(r.position)})`).join(',\n');
const source = `tools/fifa21_promo_group_cards.csv sha256=${sha256}`;
const manualSource = `tools/fifa21_promo_groups.csv sha256=${manualSha256}`;
const competitionSort = (groups.length + 1) * 10;

const migration = `-- Promo groups (ultimateteam/promo-groups-plan.md, step 1). GENERATED by tools/generate-promo-groups-migration.mjs from
-- tools/fifa21_promo_group_cards.csv (SHA-256 ${sha256}: ${rows.length} rows, ${originalGroupCount} groups) and
-- tools/fifa21_promo_groups.csv (SHA-256 ${manualSha256}: ${manualRows.length} rows); edit the CSVs and regenerate.
--
-- This migration only stores groups; no draw reads them yet. The maintainer's '-' rows deliberately have no group.
-- Main migration 066 removed three listed Pro cards and added three others, which remain ungrouped too.
-- Groups:
--   * the historical CSV's ${originalGroupCount} groups, matched by native resource id; the three cards migration 050
--     removed are skipped;
--   * the manual CSV adds cards to existing groups and defines Silver TOTW and Next-Gen;
--   * the competition versions the maintainer asked for as one group each: UCL (Champions League Rare and Non-Rare,
--     rareflags 48 and 47), Sudamericana (52) and Libertadores (53); plus FUT Champions (18), the reward-only red cards;
--   * other cards already in the catalogue when this migration runs, in an "(unsorted)" group per card type.
-- Every group starts enabled, with no schedule.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.promos
    ADD COLUMN IF NOT EXISTS family text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS category text NOT NULL DEFAULT 'event_item_group',
    ADD COLUMN IF NOT EXISTS sort_order integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS enabled boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT '';
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='ultimateteam.promos'::regclass AND conname='promos_category') THEN
        ALTER TABLE ultimateteam.promos ADD CONSTRAINT promos_category
            CHECK (category IN ('historical_squad', 'event_item_group', 'icon_tier', 'competition', 'unsorted'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='ultimateteam.promos'::regclass AND conname='promos_window') THEN
        ALTER TABLE ultimateteam.promos ADD CONSTRAINT promos_window
            CHECK (starts_at IS NULL OR ends_at IS NULL OR starts_at < ends_at);
    END IF;
END $$;
ALTER TABLE ultimateteam.promo_cards ADD COLUMN IF NOT EXISTS member_order integer;
-- Existing catalogues may have inserted promo ids explicitly without advancing the sequence.
SELECT setval(pg_get_serial_sequence('ultimateteam.promos', 'promo_id'),
    COALESCE((SELECT max(promo_id) FROM ultimateteam.promos), 1),
    EXISTS (SELECT 1 FROM ultimateteam.promos));

CREATE TEMP TABLE csv_groups(code text, name text, family text, category text, sort_order integer) ON COMMIT DROP;
INSERT INTO csv_groups VALUES
${groupValues};

CREATE TEMP TABLE csv_members(code text, resource bigint, member_order integer) ON COMMIT DROP;
INSERT INTO csv_members VALUES
${memberValues};

CREATE TEMP TABLE manual_members(code text, definition_id bigint, resource bigint, footballer_id bigint,
    card_type_id bigint, rating bigint, position text) ON COMMIT DROP;
INSERT INTO manual_members VALUES
${manualValues};
-- Migration 056 allocates Martial's shell definition after the current catalogue maximum. Its definition id can
-- differ across upgrade histories; the resource and player identity below remain mandatory.
UPDATE manual_members m SET definition_id=d.definition_id
FROM ultimateteam.item_definitions d
WHERE m.resource=167983460 AND d.family_id=1 AND d.native_resource_id=m.resource;
-- Manual identity columns must still describe the same card. The three Pro cards replaced by main's migration 066
-- are skipped; every other manual row is checked.
DO $$ DECLARE mismatch record; BEGIN
    SELECT m.definition_id, m.resource INTO mismatch FROM manual_members m WHERE NOT EXISTS (
        SELECT 1 FROM ultimateteam.item_definitions d
        JOIN ultimateteam.player_card_definitions pc ON pc.definition_id=d.definition_id
        WHERE d.definition_id=m.definition_id AND d.native_resource_id=m.resource AND pc.footballer_id=m.footballer_id
          AND pc.card_type_id=m.card_type_id AND pc.rating=m.rating AND pc.position=m.position)
    ORDER BY m.definition_id LIMIT 1;
    IF FOUND THEN
        RAISE EXCEPTION 'Manual promo CSV card definition % (resource %) no longer matches the catalogue', mismatch.definition_id, mismatch.resource;
    END IF;
END $$;

INSERT INTO ultimateteam.promos(code, name, starts_at, ends_at, status, family, category, sort_order, enabled, source)
SELECT g.code, g.name, NULL, NULL, 'active', g.family, g.category, g.sort_order, true,
    CASE WHEN g.code IN (${newGroups.map((group) => sql(group.code)).join(', ')}) THEN ${sql(manualSource)}
         WHEN EXISTS (SELECT 1 FROM manual_members m WHERE m.code=g.code) THEN ${sql(`${source}; ${manualSource}`)}
         ELSE ${sql(source)} END
FROM csv_groups g ORDER BY g.sort_order
ON CONFLICT (code) DO NOTHING;

INSERT INTO ultimateteam.promo_cards(promo_id, player_card_definition_id, member_order)
SELECT p.promo_id, d.definition_id, m.member_order
FROM csv_members m
JOIN ultimateteam.promos p ON p.code = m.code
JOIN ultimateteam.item_definitions d ON d.family_id = 1 AND d.game_year = 2021 AND d.native_resource_id = m.resource
JOIN ultimateteam.player_card_definitions pc ON pc.definition_id = d.definition_id
ON CONFLICT (promo_id, player_card_definition_id) DO NOTHING;

INSERT INTO ultimateteam.promo_cards(promo_id, player_card_definition_id)
SELECT p.promo_id, m.definition_id
FROM manual_members m JOIN ultimateteam.promos p ON p.code=m.code
WHERE m.code <> '-'
ON CONFLICT (promo_id, player_card_definition_id) DO NOTHING;

-- The competition groups: every card of their card types.
CREATE TEMP TABLE competition_groups(code text, name text, family text, rareflags bigint[], sort_order integer) ON COMMIT DROP;
INSERT INTO competition_groups VALUES
    ('ucl', 'UCL', 'Champions League', '{47,48}', ${competitionSort}),
    ('sudamericana', 'Sudamericana', 'Sudamericana', '{52}', ${competitionSort + 10}),
    ('libertadores', 'Libertadores', 'Libertadores', '{53}', ${competitionSort + 20}),
    ('fut_champions', 'FUT Champions', 'FUT Champions', '{18}', ${competitionSort + 30});
INSERT INTO ultimateteam.promos(code, name, starts_at, ends_at, status, family, category, sort_order, enabled, source)
SELECT code, name, NULL, NULL, 'active', family, 'competition', sort_order, true, 'card types with rareflag ' || array_to_string(rareflags, ', ')
FROM competition_groups ORDER BY sort_order
ON CONFLICT (code) DO NOTHING;
INSERT INTO ultimateteam.promo_cards(promo_id, player_card_definition_id)
SELECT p.promo_id, pc.definition_id
FROM competition_groups g
JOIN ultimateteam.promos p ON p.code = g.code
JOIN ultimateteam.card_types t ON TRUE
JOIN ultimateteam.native_rarities r ON r.native_rarity_id = t.native_rarity_id AND r.native_rareflag = ANY(g.rareflags)
JOIN ultimateteam.player_card_definitions pc ON pc.card_type_id = t.card_type_id
ON CONFLICT (promo_id, player_card_definition_id) DO NOTHING;

-- Every other special card still in no group: one "(unsorted)" group per card type.
CREATE TEMP TABLE unsorted_cards ON COMMIT DROP AS
SELECT pc.definition_id, t.card_type_id, t.name AS type_name, r.native_rareflag
FROM ultimateteam.player_card_definitions pc
JOIN ultimateteam.card_types t ON t.card_type_id = pc.card_type_id
JOIN ultimateteam.native_rarities r ON r.native_rarity_id = t.native_rarity_id
WHERE (r.native_rareflag > 1 OR t.is_special)
  AND NOT EXISTS (SELECT 1 FROM ultimateteam.promo_cards m WHERE m.player_card_definition_id = pc.definition_id)
  AND NOT EXISTS (SELECT 1 FROM manual_members m WHERE m.code = '-' AND m.definition_id = pc.definition_id)
  AND NOT EXISTS (SELECT 1 FROM ultimateteam.item_definitions d WHERE d.definition_id = pc.definition_id
                  AND d.native_resource_id IN (${newProResources.join(', ')}));
INSERT INTO ultimateteam.promos(code, name, starts_at, ends_at, status, family, category, sort_order, enabled, source)
SELECT 'unsorted_' || card_type_id, type_name || ' (unsorted)', NULL, NULL, 'active', type_name, 'unsorted',
       ${competitionSort + 1000} + row_number() OVER (ORDER BY native_rareflag, card_type_id), true,
       'special cards of card type ' || card_type_id || ' in no other group'
FROM (SELECT DISTINCT card_type_id, type_name, native_rareflag FROM unsorted_cards) types
ON CONFLICT (code) DO NOTHING;
INSERT INTO ultimateteam.promo_cards(promo_id, player_card_definition_id)
SELECT p.promo_id, u.definition_id FROM unsorted_cards u JOIN ultimateteam.promos p ON p.code = 'unsorted_' || u.card_type_id
ON CONFLICT (promo_id, player_card_definition_id) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(76);
`;

writeFileSync(outPath, migration);
console.log(`Wrote ${outPath}: ${originalGroupCount} historical groups, ${newGroups.length} new groups, ${rows.length} historical rows, ${manualRows.length} manual rows.`);
