#!/usr/bin/env node
// Aurora21 UltimateTeam v4 — deterministic fresh-schema artifact generator.
//
// Reads the fixed handoff packet (../database-handoff, resolved relative to this
// file, never the shell cwd), verifies every packet hash, and emits plain
// server-executable SQL plus manifest.json and README.md into database-setup/.
//
// Node built-ins only. No timestamps, no randomness, no shell interpolation:
// running twice must produce byte-identical output.

import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";

const TOOL_DIR = path.dirname(fileURLToPath(import.meta.url));
const SETUP_DIR = path.resolve(TOOL_DIR, "..");
const PACKET_DIR = path.resolve(SETUP_DIR, "..", "database-handoff");

const NS = "ultimateteam";
const VERSION_CODE = "ultimateteam-relational-v1";
const POSTGRES_MAJOR = 18;
const SEED_CHUNK = 250;

function fail(msg) {
  console.error("generate: FATAL: " + msg);
  process.exit(1);
}

function sha256File(p) {
  return crypto.createHash("sha256").update(fs.readFileSync(p)).digest("hex");
}

function readJson(p) {
  try {
    return JSON.parse(fs.readFileSync(p, "utf8"));
  } catch (e) {
    fail("cannot read JSON " + p + ": " + e.message);
  }
}

// ---------------------------------------------------------------------------
// 1. Validate the fixed execution packet.
// ---------------------------------------------------------------------------
const execManifest = readJson(path.join(PACKET_DIR, "execution-input-manifest.json"));
for (const entry of execManifest.files) {
  const p = path.join(PACKET_DIR, entry.path);
  if (!fs.existsSync(p)) fail("packet file missing: " + entry.path);
  const buf = fs.readFileSync(p);
  const h = crypto.createHash("sha256").update(buf).digest("hex");
  if (h !== entry.sha256) fail("packet hash mismatch for " + entry.path + ": got " + h + " expected " + entry.sha256);
  if (buf.length !== entry.bytes) fail("packet byte mismatch for " + entry.path + ": got " + buf.length + " expected " + entry.bytes);
}
console.log("packet: " + execManifest.files.length + " files verified against execution-input-manifest.json");

const spec = readJson(path.join(PACKET_DIR, "implementation-specification.json"));
const seedManifest = readJson(path.join(PACKET_DIR, "normalized-seed-manifest.json"));
const fixtures = readJson(path.join(PACKET_DIR, "validation-fixtures.json"));

if (spec.namespace !== NS) fail("spec namespace mismatch");
if (spec.postgresMajor !== POSTGRES_MAJOR) fail("spec postgres major mismatch");

const seedFiles = [];
for (const entry of seedManifest.files) {
  const p = path.join(PACKET_DIR, entry.path);
  const h = sha256File(p);
  if (h !== entry.sha256) fail("normalized seed hash mismatch for " + entry.path);
  if (fs.readFileSync(p).length !== entry.bytes) fail("normalized seed byte mismatch for " + entry.path);
  seedFiles.push({ entry, doc: readJson(p) });
}
console.log("seeds: " + seedFiles.length + " normalized files verified against normalized-seed-manifest.json");

// ---------------------------------------------------------------------------
// SQL literal encoding (standard_conforming_strings = on, the server default).
// ---------------------------------------------------------------------------
function sqlString(s) {
  return "'" + String(s).replace(/'/g, "''") + "'";
}

function sqlValue(v, colType) {
  if (v === null || v === undefined) return "NULL";
  if (typeof v === "boolean") return v ? "TRUE" : "FALSE";
  if (typeof v === "number") {
    if (!Number.isFinite(v)) fail("non-finite number in seed value");
    if ((colType === "integer" || colType === "smallint") && !Number.isSafeInteger(v)) {
      fail("integer column received non-integer value " + v);
    }
    return String(v);
  }
  if (typeof v === "string") {
    if (colType === "json") fail("json column received raw string; expected object");
    return sqlString(v);
  }
  if (typeof v === "object") {
    if (colType !== "json") fail("object value in non-json column");
    return sqlString(JSON.stringify(v)) + "::json";
  }
  fail("unsupported seed value type: " + typeof v);
}

// ---------------------------------------------------------------------------
// 2. schema/001-tables.sql
// ---------------------------------------------------------------------------
const ledger = spec.additionalBootstrapTable;
const ledgerCols = ledger.columns
  .map((c) => {
    let line = "    " + c.name + " " + c.type;
    if (c.identity) line += " " + c.identity;
    if (!c.nullable) line += " NOT NULL";
    if (c.defaultExpression) line += " DEFAULT " + c.defaultExpression;
    return line;
  })
  .join(",\n");

function tableDdl(t) {
  const lines = t.columns.map((c) => {
    let line = "    " + c.name + " " + c.type;
    if (c.identity) line += " " + c.identity;
    if (!c.nullable) line += " NOT NULL";
    if (c.defaultExpression) line += " DEFAULT " + c.defaultExpression;
    return line;
  });
  lines.push("    CONSTRAINT lfv4_pk_" + t.name + " PRIMARY KEY (" + t.primaryKey + ")");
  return "CREATE TABLE " + NS + "." + t.name + " (\n" + lines.join(",\n") + "\n);";
}

const sql001 = [
  "-- " + NS + " 001-tables.sql",
  "-- Generated by tools/generate.mjs from implementation-specification.json (formatVersion " + spec.formatVersion + ").",
  "-- Plain server-executable SQL: no psql meta commands, no transaction control, no ledger insertion.",
  "-- String literals assume standard_conforming_strings = on (server default).",
  "--",
  "-- 66 domain tables plus the standalone setup ledger (67 tables total).",
  "-- IF NOT EXISTS is used only for the schema and the ledger, per the fixed bootstrap contract.",
  "",
  "CREATE SCHEMA IF NOT EXISTS " + NS + ";",
  "",
  "-- Standalone bootstrap ledger (not the server schema_migrations ledger).",
  "-- The validator's ledger helper DDL must match this block exactly.",
  "--BEGIN-LEDGER-DDL",
  "CREATE TABLE IF NOT EXISTS " + NS + "." + ledger.name + " (",
  ledgerCols + ",\n    CONSTRAINT lfv4_pk_setup_artifact_versions PRIMARY KEY (id),\n    CONSTRAINT lfv4_uq_setup_artifact_versions_version UNIQUE (version_code)",
  ");",
  "--END-LEDGER-DDL",
  "",
].join("\n") + spec.tables.map((t) => tableDdl(t) + "\n").join("\n");

// ---------------------------------------------------------------------------
// 3. schema/002-relationships.sql
// ---------------------------------------------------------------------------
const fkParts = [];
for (const t of spec.tables) {
  for (const c of t.columns) {
    if (!c.references) continue;
    const r = c.references;
    let stmt =
      "ALTER TABLE " + NS + "." + t.name + "\n" +
      "    ADD CONSTRAINT lfv4_fk_" + t.name + "_" + c.name +
      " FOREIGN KEY (" + c.name + ")\n" +
      "    REFERENCES " + NS + "." + r.table + " (" + r.column + ")\n" +
      "    ON UPDATE " + r.onUpdate + " ON DELETE " + r.onDelete;
    if (r.deferrable && r.initiallyDeferred) stmt += " DEFERRABLE INITIALLY DEFERRED";
    fkParts.push(stmt + ";");
  }
}

// Unique constraints: one-based ordinal per table in JSON order; an exact
// duplicate listing (same table, columns, deferrability) is not created twice.
const uqParts = [];
const uqSeen = new Map();
const uqOrdinal = new Map();
const uqDeduped = [];
spec.uniqueConstraints.forEach((u, i) => {
  const key = u.table + "|" + u.columns.join(",") + "|" + !!u.deferrable + "|" + !!u.initiallyDeferred;
  if (uqSeen.has(key)) {
    uqDeduped.push({ listedAt: i + 1, duplicateOf: uqSeen.get(key).listedAt, table: u.table, columns: u.columns });
    return;
  }
  const ord = (uqOrdinal.get(u.table) || 0) + 1;
  uqOrdinal.set(u.table, ord);
  uqSeen.set(key, { listedAt: i + 1 });
  let stmt =
    "ALTER TABLE " + NS + "." + u.table + "\n" +
    "    ADD CONSTRAINT lfv4_uq_" + u.table + "_" + ord +
    " UNIQUE (" + u.columns.join(", ") + ")";
  if (u.deferrable && u.initiallyDeferred) stmt += " DEFERRABLE INITIALLY DEFERRED";
  uqParts.push(stmt + ";");
});

const ckParts = spec.checkConstraints.map(
  (c) =>
    "ALTER TABLE " + NS + "." + c.table + "\n" +
    "    ADD CONSTRAINT lfv4_ck_" + c.table + "_" + c.name +
    " CHECK (" + c.expression + ");"
);

const sql002 = [
  "-- " + NS + " 002-relationships.sql",
  "-- Generated by tools/generate.mjs from implementation-specification.json.",
  "-- All declared single-column FKs (DEFERRABLE INITIALLY DEFERRED), the listed UQ",
  "-- constraints and the listed CHECK constraints. Insertion-order cycles are allowed",
  "-- because every FK is deferred.",
  "",
  "-- Foreign keys",
  ...fkParts,
  "",
  "-- Unique constraints",
  ...uqParts,
  ...uqDeduped.map(
    (d) =>
      "-- listed UQ #" + d.listedAt + " (" + d.table + " " + d.columns.join(",") +
      ") duplicates listed UQ #" + d.duplicateOf + "; not created twice"
  ),
  "",
  "-- Check constraints",
  ...ckParts,
  "",
].join("\n");

// ---------------------------------------------------------------------------
// 4. schema/003-invariants.sql — the ten trigger recipes (TRIGGER-RECIPES.md).
// ---------------------------------------------------------------------------
// PL/pgSQL invoker functions: the common rules require SELECT ... FOR KEY SHARE
// parent locking and 23514 raises, which SQL-language functions cannot express.
// Deferred checks are row-level CONSTRAINT TRIGGERs AFTER INSERT/UPDATE/DELETE,
// DEFERRABLE INITIALLY DEFERRED. They check the final stored row by ID (re-queried
// at check time), not stale NEW values; a row inserted and later deleted in the
// same transaction is skipped. Referenced mutable parent rows are locked
// SELECT ... FOR KEY SHARE before validation.

const ATTR_35 = [
  "acceleration", "sprint_speed", "positioning", "finishing", "shot_power", "long_shots",
  "volleys", "penalties", "vision", "crossing", "free_kick_accuracy", "short_passing",
  "long_passing", "curve", "agility", "balance", "reactions", "ball_control", "dribbling",
  "composure", "interceptions", "heading_accuracy", "marking", "standing_tackle",
  "sliding_tackle", "jumping", "stamina", "strength", "aggression", "gk_diving",
  "gk_handling", "gk_kicking", "gk_reflexes", "gk_speed", "gk_positioning",
];
if (ATTR_35.length !== 35) fail("attribute set must have exactly 35 columns");
const attrSetLiteral = ATTR_35.map(sqlString).join(", ");

const slotValues = spec.equipmentSlots
  .map((s) => "(" + s.slotId + ", " + sqlString(s.itemType) + ", " + s.nativeSubtype + ")")
  .join(",\n                ");

function fn(name, body) {
  return (
    "CREATE OR REPLACE FUNCTION " + NS + "." + name + "() RETURNS trigger\n" +
    "LANGUAGE plpgsql\n" +
    "SECURITY INVOKER\n" +
    "SET search_path = pg_catalog, " + NS + "\n" +
    "AS $$\n" + body + "\n$$;\n"
  );
}

// Re-query the final stored row by primary key at check time.
// Returns the PL/pgSQL prologue: id extraction + SELECT ... INTO + NOT FOUND exit.
function requery(table, pk, varName) {
  return (
    "    IF TG_OP = 'DELETE' THEN v_id := OLD." + pk + "; ELSE v_id := NEW." + pk + "; END IF;\n" +
    "    SELECT * INTO " + varName + " FROM " + NS + "." + table + " WHERE " + pk + " = v_id;\n" +
    "    IF NOT FOUND THEN RETURN NULL; END IF;\n"
  );
}

const common =
  "-- Common helper: raise SQLSTATE 23514 for a violated cross-row rule.\n" +
  "CREATE OR REPLACE FUNCTION " + NS + ".lfv4_err(msg text) RETURNS void\n" +
  "LANGUAGE plpgsql\n" +
  "IMMUTABLE\n" +
  "CALLED ON NULL INPUT\n" +
  "SET search_path = pg_catalog\n" +
  "AS $$\nBEGIN\n    RAISE EXCEPTION 'ultimateteam invariant violated: %', msg USING ERRCODE = '23514';\nEND;\n$$;\n";

// Recipe 1: immutable keys and ownership (BEFORE UPDATE, per table).
const r1Tables = [
  ["items", ["club_id", "native_item_id", "definition_id", "family_id"]],
  ["squads", ["club_id", "native_squad_id"]],
  ["squad_players", ["squad_id"]],
  ["squad_managers", ["squad_id"]],
  ["squad_roles", ["squad_id"]],
  ["squad_tactics", ["squad_id"]],
  ["squad_tactic_settings", ["tactic_id"]],
  ["squad_tactic_instructions", ["tactic_id"]],
  ["club_slot_bindings", ["club_id", "slot_id"]],
  ["player_pick_choices", ["token_item_id"]],
  ["draft_runs", ["club_id", "slot"]],
  ["draft_picks", ["run_id"]],
  ["draft_choices", ["run_id"]],
  ["draft_formation_choices", ["run_id"]],
  ["draft_seen_footballers", ["run_id"]],
  ["onboarding_kit_choices", ["club_id", "role"]],
  ["market_listings", [
    "kind", "seller_club_id", "seller_item_id", "definition_id", "resource_id", "rating",
    "asset_id", "nation_id", "league_id", "team_id", "preferred_position", "rareflag", "is_special",
  ]],
];
for (const t of spec.tables) {
  if (!r1Tables.some(([name]) => name === t.name)) r1Tables.push([t.name, []]);
}
const r1Fns = r1Tables
  .map(([t, extra]) => {
    const pk = spec.tables.find((x) => x.name === t).primaryKey;
    const conds = ["NEW." + pk + " IS DISTINCT FROM OLD." + pk];
    for (const c of extra) conds.push("NEW." + c + " IS DISTINCT FROM OLD." + c);
    return fn(
      "lfv4_r1_" + t,
      "BEGIN\n" +
        "    IF TG_OP = 'UPDATE' AND (\n" +
        "        " + conds.join("\n        OR ") + "\n    ) THEN\n" +
        "        PERFORM ultimateteam.lfv4_err('immutable column of " + t + "');\n" +
        "    END IF;\n" +
        "    RETURN NEW;\nEND"
    );
  })
  .join("\n\n");
const r1Triggers = r1Tables
  .map(
    ([t]) =>
      "CREATE TRIGGER lfv4_trg_r1_" + t +
      " BEFORE UPDATE ON " + NS + "." + t +
      " FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r1_" + t + "();"
  )
  .join("\n");

// Recipe 2: immutable catalog (BEFORE UPDATE OR DELETE, shared function).
const r2Tables = [
  "catalog_sources", "footballers", "nations", "leagues", "teams", "item_families",
  "item_definitions", "card_types", "card_designs", "card_type_designs", "promos",
  "promo_cards", "card_rarity_groups", "player_card_definitions", "manager_definitions",
  "consumable_definitions", "customisation_definitions", "definition_extensions",
];
const r2Fn = fn(
  "lfv4_r2_catalog",
  "BEGIN\n" +
    "    IF TG_OP IN ('UPDATE', 'DELETE') THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('catalog table ' || TG_TABLE_NAME || ' is immutable in this artifact version');\n" +
    "    END IF;\n" +
    "    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;\n" +
    "    RETURN NEW;\nEND"
);
const r2Triggers = r2Tables
  .map(
    (t) =>
      "CREATE TRIGGER lfv4_trg_r2_" + t +
      " BEFORE UPDATE OR DELETE ON " + NS + "." + t +
      " FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r2_catalog();"
  )
  .join("\n");

// Recipe 3: exactly-one definition family (constraint triggers on item_definitions + children).
const r3Fn = fn(
  "lfv4_r3_definition",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_def ultimateteam.item_definitions%ROWTYPE;\n" +
    "    v_player boolean;\n" +
    "    v_manager boolean;\n" +
    "    v_consumable boolean;\n" +
    "    v_customisation boolean;\n" +
    "BEGIN\n" +
    "    IF TG_TABLE_NAME = 'item_definitions' THEN\n" +
    "        " + requery("item_definitions", "definition_id", "v_def") +
    "    ELSE\n" +
    "        IF TG_OP = 'DELETE' THEN v_id := OLD.definition_id; ELSE v_id := NEW.definition_id; END IF;\n" +
    "        IF NOT EXISTS (SELECT 1 FROM ultimateteam.player_card_definitions WHERE definition_id = v_id)\n" +
    "           AND NOT EXISTS (SELECT 1 FROM ultimateteam.manager_definitions WHERE definition_id = v_id)\n" +
    "           AND NOT EXISTS (SELECT 1 FROM ultimateteam.consumable_definitions WHERE definition_id = v_id)\n" +
    "           AND NOT EXISTS (SELECT 1 FROM ultimateteam.customisation_definitions WHERE definition_id = v_id) THEN RETURN NULL; END IF;\n" +
    "        SELECT * INTO v_def FROM ultimateteam.item_definitions WHERE definition_id = v_id FOR KEY SHARE;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "    END IF;\n" +
    "    SELECT EXISTS (SELECT 1 FROM ultimateteam.player_card_definitions p WHERE p.definition_id = v_def.definition_id) INTO v_player;\n" +
    "    SELECT EXISTS (SELECT 1 FROM ultimateteam.manager_definitions m WHERE m.definition_id = v_def.definition_id) INTO v_manager;\n" +
    "    SELECT EXISTS (SELECT 1 FROM ultimateteam.consumable_definitions c WHERE c.definition_id = v_def.definition_id) INTO v_consumable;\n" +
    "    SELECT EXISTS (SELECT 1 FROM ultimateteam.customisation_definitions k WHERE k.definition_id = v_def.definition_id) INTO v_customisation;\n" +
    "    IF v_def.family_id = 1 THEN\n" +
    "        IF NOT (v_player AND NOT v_manager AND NOT v_consumable AND NOT v_customisation) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('definition ' || v_def.definition_id || ' (family 1) must have exactly the player child');\n" +
    "        END IF;\n" +
    "    ELSIF v_def.family_id = 2 THEN\n" +
    "        IF NOT (v_manager AND NOT v_player AND NOT v_consumable AND NOT v_customisation) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('definition ' || v_def.definition_id || ' (family 2) must have exactly the manager child');\n" +
    "        END IF;\n" +
    "    ELSIF v_def.family_id = 3 THEN\n" +
    "        IF NOT (v_consumable AND NOT v_player AND NOT v_manager AND NOT v_customisation) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('definition ' || v_def.definition_id || ' (family 3) must have exactly the consumable child');\n" +
    "        END IF;\n" +
    "    ELSIF v_def.family_id = 4 THEN\n" +
    "        IF NOT (v_customisation AND NOT v_player AND NOT v_manager AND NOT v_consumable) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('definition ' || v_def.definition_id || ' (family 4) must have exactly the customisation child');\n" +
    "        END IF;\n" +
    "    ELSIF v_def.family_id = 5 THEN\n" +
    "        IF v_player OR v_manager OR v_consumable OR v_customisation THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('definition ' || v_def.definition_id || ' (family 5) must have none of the four definition children');\n" +
    "        END IF;\n" +
    "    ELSE\n" +
    "        PERFORM ultimateteam.lfv4_err('definition ' || v_def.definition_id || ' has invalid family_id ' || v_def.family_id::text);\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r3Triggers = [
  "item_definitions", "player_card_definitions", "manager_definitions",
  "consumable_definitions", "customisation_definitions",
]
  .map(
    (t) =>
      "CREATE CONSTRAINT TRIGGER lfv4_trg_r3_" + t +
      " AFTER INSERT OR UPDATE OR DELETE ON " + NS + "." + t +
      " DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r3_definition();"
  )
  .join("\n");

// Recipe 4: exactly-one owned family (constraint triggers on items + five owned children).
const r4Fn = fn(
  "lfv4_r4_item",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_item ultimateteam.items%ROWTYPE;\n" +
    "    v_player boolean;\n" +
    "    v_manager boolean;\n" +
    "    v_consumable boolean;\n" +
    "    v_customisation boolean;\n" +
    "    v_pick boolean;\n" +
    "BEGIN\n" +
    "    IF TG_TABLE_NAME = 'items' THEN\n" +
    "        " + requery("items", "id", "v_item") +
    "    ELSE\n" +
    "        IF TG_OP = 'DELETE' THEN v_id := OLD.item_id; ELSE v_id := NEW.item_id; END IF;\n" +
    "        SELECT * INTO v_item FROM ultimateteam.items WHERE id = v_id FOR KEY SHARE;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "    END IF;\n" +
    "    PERFORM 1 FROM ultimateteam.item_definitions d WHERE d.definition_id = v_item.definition_id AND d.family_id = v_item.family_id FOR KEY SHARE;\n" +
    "    IF NOT FOUND THEN PERFORM ultimateteam.lfv4_err('item ' || v_item.id || ' family differs from its definition'); END IF;\n" +
    "    SELECT EXISTS (SELECT 1 FROM ultimateteam.player_items pi WHERE pi.item_id = v_item.id) INTO v_player;\n" +
    "    SELECT EXISTS (SELECT 1 FROM ultimateteam.manager_items mi WHERE mi.item_id = v_item.id) INTO v_manager;\n" +
    "    SELECT EXISTS (SELECT 1 FROM ultimateteam.consumable_items ci WHERE ci.item_id = v_item.id) INTO v_consumable;\n" +
    "    SELECT EXISTS (SELECT 1 FROM ultimateteam.customisation_items ki WHERE ki.item_id = v_item.id) INTO v_customisation;\n" +
    "    SELECT EXISTS (SELECT 1 FROM ultimateteam.player_pick_items ppi WHERE ppi.item_id = v_item.id) INTO v_pick;\n" +
    "    IF v_item.family_id = 1 THEN\n" +
    "        IF NOT (v_player AND NOT v_manager AND NOT v_consumable AND NOT v_customisation AND NOT v_pick) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('item ' || v_item.id || ' (family 1) must have exactly the player owned child');\n" +
    "        END IF;\n" +
    "    ELSIF v_item.family_id = 2 THEN\n" +
    "        IF NOT (v_manager AND NOT v_player AND NOT v_consumable AND NOT v_customisation AND NOT v_pick) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('item ' || v_item.id || ' (family 2) must have exactly the manager owned child');\n" +
    "        END IF;\n" +
    "    ELSIF v_item.family_id = 3 THEN\n" +
    "        IF NOT (v_consumable AND NOT v_player AND NOT v_manager AND NOT v_customisation AND NOT v_pick) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('item ' || v_item.id || ' (family 3) must have exactly the consumable owned child');\n" +
    "        END IF;\n" +
    "    ELSIF v_item.family_id = 4 THEN\n" +
    "        IF NOT (v_customisation AND NOT v_player AND NOT v_manager AND NOT v_consumable AND NOT v_pick) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('item ' || v_item.id || ' (family 4) must have exactly the customisation owned child');\n" +
    "        END IF;\n" +
    "    ELSIF v_item.family_id = 5 THEN\n" +
    "        IF NOT (v_pick AND NOT v_player AND NOT v_manager AND NOT v_consumable AND NOT v_customisation) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('item ' || v_item.id || ' (family 5) must have exactly the player-pick owned child');\n" +
    "        END IF;\n" +
    "    ELSE\n" +
    "        PERFORM ultimateteam.lfv4_err('item ' || v_item.id || ' has invalid family_id ' || v_item.family_id::text);\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r4Triggers = [
  "items", "player_items", "manager_items", "consumable_items",
  "customisation_items", "player_pick_items",
]
  .map(
    (t) =>
      "CREATE CONSTRAINT TRIGGER lfv4_trg_r4_" + t +
      " AFTER INSERT OR UPDATE OR DELETE ON " + NS + "." + t +
      " DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r4_item();"
  )
  .join("\n");

// Recipe 5: squad references (constraint triggers on the four referencing tables).
const r5Fn = fn(
  "lfv4_r5_squad",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_row record;\n" +
    "    v_squad ultimateteam.squads%ROWTYPE;\n" +
    "    v_squad_id bigint;\n" +
    "    v_ref_kind text;\n" +
    "    v_item_id bigint;\n" +
    "    v_def_id bigint;\n" +
    "    v_footballer_id bigint;\n" +
    "    v_tactic_id bigint;\n" +
    "    v_squad_player_id bigint;\n" +
    "BEGIN\n" +
    "    IF TG_OP = 'DELETE' THEN v_id := OLD.id; ELSE v_id := NEW.id; END IF;\n" +
    "    IF TG_TABLE_NAME = 'squad_players' THEN\n" +
    "        SELECT * INTO v_row FROM ultimateteam.squad_players WHERE id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_squad_id := v_row.squad_id; v_ref_kind := v_row.reference_kind;\n" +
    "        v_item_id := v_row.item_id; v_def_id := v_row.definition_id; v_footballer_id := v_row.footballer_id;\n" +
    "    ELSIF TG_TABLE_NAME = 'squad_managers' THEN\n" +
    "        SELECT * INTO v_row FROM ultimateteam.squad_managers WHERE id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_squad_id := v_row.squad_id; v_ref_kind := v_row.reference_kind;\n" +
    "        v_item_id := v_row.item_id; v_def_id := v_row.definition_id;\n" +
    "    ELSIF TG_TABLE_NAME = 'squad_roles' THEN\n" +
    "        SELECT * INTO v_row FROM ultimateteam.squad_roles WHERE id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_squad_id := v_row.squad_id; v_squad_player_id := v_row.squad_player_id;\n" +
    "    ELSE\n" +
    "        SELECT * INTO v_row FROM ultimateteam.squad_tactic_instructions WHERE id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_tactic_id := v_row.tactic_id; v_squad_player_id := v_row.squad_player_id;\n" +
    "    END IF;\n" +
    "    IF TG_TABLE_NAME IN ('squad_players', 'squad_managers') THEN\n" +
    "        SELECT * INTO v_squad FROM ultimateteam.squads WHERE id = v_squad_id FOR KEY SHARE;\n" +
    "        IF NOT FOUND THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('squad ' || v_squad_id || ' does not exist');\n" +
    "        END IF;\n" +
    "    END IF;\n" +
    "    IF TG_TABLE_NAME = 'squad_players' THEN\n" +
    "        IF NOT EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.player_card_definitions d\n" +
    "            WHERE d.definition_id = v_def_id AND d.footballer_id = v_footballer_id\n" +
    "        ) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('squad player ' || v_id || ' definition does not declare footballer ' || v_footballer_id::text);\n" +
    "        END IF;\n" +
    "        IF v_ref_kind = 'owned' AND EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.player_items pi WHERE pi.item_id = v_item_id\n" +
    "        ) AND NOT EXISTS (\n" +
    "            SELECT 1\n" +
    "            FROM ultimateteam.player_items pi\n" +
    "            JOIN ultimateteam.items i ON i.id = pi.item_id\n" +
    "            WHERE pi.item_id = v_item_id\n" +
    "              AND i.club_id = v_squad.club_id\n" +
    "              AND i.definition_id = v_def_id\n" +
    "            FOR KEY SHARE\n" +
    "        ) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('squad player ' || v_id || ' owned item does not match squad club and definition');\n" +
    "        END IF;\n" +
    "    ELSIF TG_TABLE_NAME = 'squad_managers' THEN\n" +
    "        IF v_ref_kind = 'owned' AND EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.manager_items mi WHERE mi.item_id = v_item_id\n" +
    "        ) AND NOT EXISTS (\n" +
    "            SELECT 1\n" +
    "            FROM ultimateteam.manager_items mi\n" +
    "            JOIN ultimateteam.items i ON i.id = mi.item_id\n" +
    "            WHERE mi.item_id = v_item_id\n" +
    "              AND i.club_id = v_squad.club_id\n" +
    "              AND i.definition_id = v_def_id\n" +
    "            FOR KEY SHARE\n" +
    "        ) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('squad manager ' || v_id || ' owned item does not match squad club and definition');\n" +
    "        END IF;\n" +
    "    ELSIF TG_TABLE_NAME = 'squad_roles' THEN\n" +
    "        IF NOT EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.squad_players sp\n" +
    "            WHERE sp.id = v_squad_player_id AND sp.squad_id = v_squad_id\n" +
    "        ) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('squad role ' || v_id || ' references a squad player outside its squad');\n" +
    "        END IF;\n" +
    "    ELSE\n" +
    "        IF v_squad_player_id IS NOT NULL AND NOT EXISTS (\n" +
    "            SELECT 1\n" +
    "            FROM ultimateteam.squad_players sp\n" +
    "            JOIN ultimateteam.squad_tactics st ON st.squad_id = sp.squad_id\n" +
    "            WHERE sp.id = v_squad_player_id AND st.id = v_tactic_id\n" +
    "        ) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('squad tactic instruction ' || v_id || ' references a squad player outside the tactic squad');\n" +
    "        END IF;\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r5Triggers = [
  "squad_players", "squad_managers", "squad_roles", "squad_tactic_instructions",
]
  .map(
    (t) =>
      "CREATE CONSTRAINT TRIGGER lfv4_trg_r5_" + t +
      " AFTER INSERT OR UPDATE OR DELETE ON " + NS + "." + t +
      " DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r5_squad();"
  )
  .join("\n");

// Recipe 6: customization references (constraint triggers on both referencing tables).
const r6Fn = fn(
  "lfv4_r6_club_slot_bindings",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_row ultimateteam.club_slot_bindings%ROWTYPE;\n" +
    "    v_def ultimateteam.item_definitions%ROWTYPE;\n" +
    "    v_slot_item_type text;\n" +
    "    v_slot_native_subtype bigint;\n" +
    "BEGIN\n" +
    "    " + requery("club_slot_bindings", "id", "v_row") +
    "    SELECT d.* INTO v_def FROM ultimateteam.item_definitions d JOIN ultimateteam.customisation_definitions c ON c.definition_id = d.definition_id WHERE d.definition_id = v_row.definition_id;\n" +
    "    IF NOT FOUND THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('club slot binding ' || v_row.id || ' references missing customisation definition ' || v_row.definition_id::text);\n" +
    "    END IF;\n" +
    "    IF v_row.reference_kind = 'owned' AND EXISTS (\n" +
    "        SELECT 1 FROM ultimateteam.customisation_items ki WHERE ki.item_id = v_row.item_id\n" +
    "    ) AND NOT EXISTS (\n" +
    "        SELECT 1\n" +
    "        FROM ultimateteam.customisation_items ki\n" +
    "        JOIN ultimateteam.items i ON i.id = ki.item_id\n" +
    "        WHERE ki.item_id = v_row.item_id\n" +
    "          AND i.club_id = v_row.club_id\n" +
    "          AND i.definition_id = v_row.definition_id\n" +
    "        FOR KEY SHARE\n" +
    "    ) THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('club slot binding ' || v_row.id || ' owned item does not match club and definition');\n" +
    "    END IF;\n" +
    "    SELECT v.item_type, v.native_subtype INTO v_slot_item_type, v_slot_native_subtype\n" +
    "    FROM (VALUES\n" + slotValues + "\n        ) v(slot_id, item_type, native_subtype)\n" +
    "    WHERE v.slot_id = v_row.slot_id;\n" +
    "    IF NOT FOUND THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('club slot binding ' || v_row.id || ' has unknown slot_id ' || v_row.slot_id::text);\n" +
    "    END IF;\n" +
    "    IF v_def.native_item_type IS DISTINCT FROM v_slot_item_type\n" +
    "       OR v_def.native_subtype IS DISTINCT FROM v_slot_native_subtype THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('club slot binding ' || v_row.id || ' definition type/subtype does not match slot ' || v_row.slot_id::text);\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r6bFn = fn(
  "lfv4_r6_onboarding_kit_choices",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_row ultimateteam.onboarding_kit_choices%ROWTYPE;\n" +
    "    v_def ultimateteam.item_definitions%ROWTYPE;\n" +
    "BEGIN\n" +
    "    " + requery("onboarding_kit_choices", "id", "v_row") +
    "    SELECT d.* INTO v_def FROM ultimateteam.item_definitions d JOIN ultimateteam.customisation_definitions c ON c.definition_id = d.definition_id WHERE d.definition_id = v_row.definition_id;\n" +
    "    IF NOT FOUND THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('onboarding kit choice ' || v_row.id || ' references missing customisation definition ' || v_row.definition_id::text);\n" +
    "    END IF;\n" +
    "    IF v_def.native_item_type IS DISTINCT FROM 'kit' THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('onboarding kit choice ' || v_row.id || ' definition is not a kit');\n" +
    "    END IF;\n" +
    "    IF v_row.item_id IS NOT NULL AND EXISTS (\n" +
    "        SELECT 1 FROM ultimateteam.customisation_items ki WHERE ki.item_id = v_row.item_id\n" +
    "    ) AND NOT EXISTS (\n" +
    "        SELECT 1\n" +
    "        FROM ultimateteam.customisation_items ki\n" +
    "        JOIN ultimateteam.items i ON i.id = ki.item_id\n" +
    "        WHERE ki.item_id = v_row.item_id\n" +
    "          AND i.club_id = v_row.club_id\n" +
    "          AND i.definition_id = v_row.definition_id\n" +
    "        FOR KEY SHARE\n" +
    "    ) THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('onboarding kit choice ' || v_row.id || ' owned item does not match club and definition');\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r6Triggers =
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r6_club_slot_bindings AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".club_slot_bindings DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r6_club_slot_bindings();\n" +
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r6_onboarding_kit_choices AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".onboarding_kit_choices DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r6_onboarding_kit_choices();";

// Recipe 7: player style and selected pick (constraint triggers on the two tables).
const r7Fn = fn(
  "lfv4_r7_style_pick",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_row record;\n" +
    "    v_item_id bigint;\n" +
    "    v_style_id bigint;\n" +
    "    v_selected_id bigint;\n" +
    "BEGIN\n" +
    "    IF TG_OP = 'DELETE' THEN v_id := OLD.item_id; ELSE v_id := NEW.item_id; END IF;\n" +
    "    IF TG_TABLE_NAME = 'player_items' THEN\n" +
    "        SELECT * INTO v_row FROM ultimateteam.player_items WHERE item_id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_item_id := v_row.item_id; v_style_id := v_row.style_definition_id;\n" +
    "        IF v_style_id IS NOT NULL AND NOT EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.consumable_definitions cd\n" +
    "            WHERE cd.definition_id = v_style_id AND cd.consumable_kind = 'chemistry_style'\n" +
    "        ) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('player item ' || v_item_id || ' style definition is not a chemistry_style consumable');\n" +
    "        END IF;\n" +
    "    ELSE\n" +
    "        SELECT * INTO v_row FROM ultimateteam.player_pick_items WHERE item_id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_item_id := v_row.item_id; v_selected_id := v_row.selected_choice_id;\n" +
    "        IF v_selected_id IS NOT NULL AND NOT EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.player_pick_choices pc\n" +
    "            WHERE pc.id = v_selected_id AND pc.token_item_id = v_item_id FOR KEY SHARE\n" +
    "        ) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('player pick item ' || v_item_id || ' selected choice does not belong to the token item');\n" +
    "        END IF;\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r7Triggers =
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r7_player_items AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".player_items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r7_style_pick();\n" +
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r7_player_pick_items AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".player_pick_items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r7_style_pick();";

// Recipe 8: active market ownership and snapshot agreement.
const r8ListingFn = fn(
  "lfv4_r8_listing",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_row ultimateteam.market_listings%ROWTYPE;\n" +
    "BEGIN\n" +
    "    " + requery("market_listings", "trade_id", "v_row") +
    "    IF v_row.kind = 'player' AND v_row.state = 'active' AND NOT EXISTS (\n" +
    "        SELECT 1\n" +
    "        FROM ultimateteam.items i\n" +
    "        JOIN ultimateteam.player_items pi ON pi.item_id = i.id\n" +
    "        WHERE i.club_id = v_row.seller_club_id\n" +
    "          AND i.native_item_id = v_row.seller_item_id\n" +
    "          AND i.definition_id = v_row.definition_id\n" +
    "        FOR KEY SHARE\n" +
    "    ) THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('active market listing ' || v_row.trade_id || ' has no matching owned seller item');\n" +
    "    END IF;\n" +
    "    IF NOT EXISTS (SELECT 1 FROM ultimateteam.market_item_snapshots s WHERE s.trade_id = v_row.trade_id) THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('market listing ' || v_row.trade_id || ' has no item snapshot');\n" +
    "    END IF;\n" +
    "    IF NOT EXISTS (SELECT 1 FROM ultimateteam.market_player_snapshots s WHERE s.trade_id = v_row.trade_id) THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('market listing ' || v_row.trade_id || ' has no player snapshot');\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r8ItemFn = fn(
  "lfv4_r8_items",
  "BEGIN\n" +
    "    IF EXISTS (\n" +
    "        SELECT 1 FROM ultimateteam.market_listings ml\n" +
    "        WHERE ml.kind = 'player' AND ml.state = 'active'\n" +
    "          AND ml.seller_club_id = OLD.club_id AND ml.seller_item_id = OLD.native_item_id\n" +
    "    ) THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('active market listing still references deleted item club ' || OLD.club_id::text || ' native ' || OLD.native_item_id::text);\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r8ItemSnapFn = fn(
  "lfv4_r8_item_snapshot",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_snap ultimateteam.market_item_snapshots%ROWTYPE;\n" +
    "    v_listing ultimateteam.market_listings%ROWTYPE;\n" +
    "BEGIN\n" +
    "    " + requery("market_item_snapshots", "trade_id", "v_snap") +
    "    SELECT * INTO v_listing FROM ultimateteam.market_listings WHERE trade_id = v_snap.trade_id FOR KEY SHARE;\n" +
    "    IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "    IF NOT EXISTS (\n" +
    "        SELECT 1\n" +
    "        FROM ultimateteam.item_definitions d\n" +
    "        JOIN ultimateteam.player_card_definitions pcd ON pcd.definition_id = d.definition_id\n" +
    "        JOIN ultimateteam.card_types ct ON ct.card_type_id = pcd.card_type_id\n" +
    "        WHERE d.definition_id = v_listing.definition_id\n" +
    "          AND v_snap.definition_id = v_listing.definition_id\n" +
    "          AND v_snap.definition_revision = d.revision\n" +
    "          AND v_listing.resource_id IS NOT DISTINCT FROM d.native_resource_id\n" +
    "          AND v_listing.rating = pcd.rating\n" +
    "          AND v_listing.asset_id IS NOT DISTINCT FROM d.native_asset_id\n" +
    "          AND v_listing.nation_id IS NOT DISTINCT FROM pcd.native_nation_id\n" +
    "          AND v_listing.league_id IS NOT DISTINCT FROM pcd.native_league_id\n" +
    "          AND v_listing.team_id IS NOT DISTINCT FROM pcd.native_team_id\n" +
    "          AND v_listing.preferred_position IS NOT DISTINCT FROM pcd.position\n" +
    "          AND v_listing.rareflag IS NOT DISTINCT FROM ct.native_rareflag\n" +
    "          AND v_listing.is_special IS NOT DISTINCT FROM ct.is_special\n" +
    "    ) THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('market item snapshot of trade ' || v_snap.trade_id || ' does not agree with its listing and definition');\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r8PlayerSnapFn = fn(
  "lfv4_r8_player_snapshot",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_snap ultimateteam.market_player_snapshots%ROWTYPE;\n" +
    "BEGIN\n" +
    "    " + requery("market_player_snapshots", "trade_id", "v_snap") +
    "    IF NOT EXISTS (SELECT 1 FROM ultimateteam.market_item_snapshots s WHERE s.trade_id = v_snap.trade_id FOR KEY SHARE) THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('market player snapshot of trade ' || v_snap.trade_id || ' has no item snapshot parent');\n" +
    "    END IF;\n" +
    "    IF v_snap.style_definition_id IS NOT NULL AND NOT EXISTS (\n" +
    "        SELECT 1 FROM ultimateteam.consumable_definitions cd\n" +
    "        WHERE cd.definition_id = v_snap.style_definition_id AND cd.consumable_kind = 'chemistry_style'\n" +
    "    ) THEN\n" +
    "        PERFORM ultimateteam.lfv4_err('market player snapshot of trade ' || v_snap.trade_id || ' style definition is not a chemistry_style consumable');\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r8Triggers = [
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r8_market_listings AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".market_listings DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r8_listing();",
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r8_items AFTER DELETE ON " + NS + ".items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r8_items();",
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r8_market_item_snapshots AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".market_item_snapshots DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r8_item_snapshot();",
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r8_market_player_snapshots AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".market_player_snapshots DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r8_player_snapshot();",
].join("\n");

// Recipe 9: immutable published snapshot state (BEFORE row triggers).
const r9ItemFn = fn("lfv4_r9_item_snapshots", `DECLARE
    v_id bigint;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.creation_transaction_id IS DISTINCT FROM pg_catalog.pg_current_xact_id() THEN
            PERFORM ultimateteam.lfv4_err('invalid snapshot creation transaction');
        END IF;
        RETURN NEW;
    END IF;
    v_id := OLD.trade_id;
    IF TG_OP = 'DELETE' AND NOT EXISTS (SELECT 1 FROM ultimateteam.market_listings WHERE trade_id = v_id) THEN
        RETURN OLD;
    END IF;
    IF OLD.creation_transaction_id IS DISTINCT FROM pg_catalog.pg_current_xact_id() THEN
        PERFORM ultimateteam.lfv4_err('published market snapshot is immutable');
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.creation_transaction_id IS DISTINCT FROM OLD.creation_transaction_id THEN
        PERFORM ultimateteam.lfv4_err('snapshot creation transaction cannot change');
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END`);
const r9ChildFn = fn("lfv4_r9_snapshot_children", `DECLARE
    v_id bigint;
    v_creation xid8;
BEGIN
    IF TG_OP = 'DELETE' THEN v_id := OLD.trade_id; ELSE v_id := NEW.trade_id; END IF;
    IF TG_OP = 'UPDATE' AND NEW.trade_id IS DISTINCT FROM OLD.trade_id THEN
        PERFORM ultimateteam.lfv4_err('snapshot child cannot change parent');
    END IF;
    IF TG_OP = 'DELETE' AND NOT EXISTS (SELECT 1 FROM ultimateteam.market_listings WHERE trade_id = v_id) THEN
        RETURN OLD;
    END IF;
    SELECT creation_transaction_id INTO v_creation FROM ultimateteam.market_item_snapshots WHERE trade_id = v_id FOR KEY SHARE;
    IF NOT FOUND OR v_creation IS DISTINCT FROM pg_catalog.pg_current_xact_id() THEN
        PERFORM ultimateteam.lfv4_err('published snapshot child is immutable or parent is missing');
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END`);
const r9Triggers = ["market_item_snapshots", "market_player_snapshots", "market_player_stat_values", "market_player_attribute_overrides"]
    .map(t => "CREATE TRIGGER lfv4_trg_r9_" + t + " BEFORE INSERT OR UPDATE OR DELETE ON " + NS + "." + t + " FOR EACH ROW EXECUTE FUNCTION " + NS + "." + (t === "market_item_snapshots" ? "lfv4_r9_item_snapshots" : "lfv4_r9_snapshot_children") + "();").join("\n");

// Recipe 10: draft player identity and typed overrides.
const r10Fn = fn(
  "lfv4_r10_draft",
  "DECLARE\n" +
    "    v_id bigint;\n" +
    "    v_row record;\n" +
    "    v_kind text;\n" +
    "    v_def_id bigint;\n" +
    "    v_footballer_id bigint;\n" +
    "    v_code text;\n" +
    "BEGIN\n" +
    "    IF TG_OP = 'DELETE' THEN v_id := OLD.id; ELSE v_id := NEW.id; END IF;\n" +
    "    IF TG_TABLE_NAME = 'draft_picks' THEN\n" +
    "        SELECT * INTO v_row FROM ultimateteam.draft_picks WHERE id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_kind := v_row.pick_kind; v_def_id := v_row.definition_id; v_footballer_id := v_row.footballer_id;\n" +
    "        IF v_kind NOT IN ('player', 'manager') THEN PERFORM ultimateteam.lfv4_err('invalid Draft kind'); END IF;\n" +
    "        IF v_kind = 'player' AND (v_footballer_id IS NULL OR NOT EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.player_card_definitions d\n" +
    "            WHERE d.definition_id = v_def_id AND d.footballer_id = v_footballer_id\n" +
    "        )) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('draft pick ' || v_id || ' player kind requires a matching player card definition');\n" +
    "        END IF;\n" +
    "        IF v_kind = 'manager' AND (v_footballer_id IS NOT NULL OR NOT EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.manager_definitions d WHERE d.definition_id = v_def_id\n" +
    "        )) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('draft pick ' || v_id || ' manager kind requires a manager definition and null footballer');\n" +
    "        END IF;\n" +
    "    ELSIF TG_TABLE_NAME = 'draft_choices' THEN\n" +
    "        SELECT * INTO v_row FROM ultimateteam.draft_choices WHERE id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_kind := v_row.choice_kind; v_def_id := v_row.definition_id; v_footballer_id := v_row.footballer_id;\n" +
    "        IF v_kind NOT IN ('player', 'manager') THEN PERFORM ultimateteam.lfv4_err('invalid Draft kind'); END IF;\n" +
    "        IF v_kind = 'player' AND (v_footballer_id IS NULL OR NOT EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.player_card_definitions d\n" +
    "            WHERE d.definition_id = v_def_id AND d.footballer_id = v_footballer_id\n" +
    "        )) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('draft choice ' || v_id || ' player kind requires a matching player card definition');\n" +
    "        END IF;\n" +
    "        IF v_kind = 'manager' AND (v_footballer_id IS NOT NULL OR NOT EXISTS (\n" +
    "            SELECT 1 FROM ultimateteam.manager_definitions d WHERE d.definition_id = v_def_id\n" +
    "        )) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('draft choice ' || v_id || ' manager kind requires a manager definition and null footballer');\n" +
    "        END IF;\n" +
    "    ELSIF TG_TABLE_NAME = 'player_attribute_overrides' THEN\n" +
    "        SELECT * INTO v_row FROM ultimateteam.player_attribute_overrides WHERE id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_code := v_row.attribute_code;\n" +
    "        IF NOT ultimateteam.lfv4_attr_code_allowed(v_code) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('player attribute override ' || v_id || ' has unknown attribute code ' || v_code);\n" +
    "        END IF;\n" +
    "    ELSE\n" +
    "        SELECT * INTO v_row FROM ultimateteam.market_player_attribute_overrides WHERE id = v_id;\n" +
    "        IF NOT FOUND THEN RETURN NULL; END IF;\n" +
    "        v_code := v_row.attribute_code;\n" +
    "        IF NOT ultimateteam.lfv4_attr_code_allowed(v_code) THEN\n" +
    "            PERFORM ultimateteam.lfv4_err('market player attribute override ' || v_id || ' has unknown attribute code ' || v_code);\n" +
    "        END IF;\n" +
    "    END IF;\n" +
    "    RETURN NULL; END"
);
const r10Triggers =
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r10_draft_picks AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".draft_picks DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r10_draft();\n" +
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r10_draft_choices AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".draft_choices DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r10_draft();\n" +
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r10_player_attribute_overrides AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".player_attribute_overrides DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r10_draft();\n" +
  "CREATE CONSTRAINT TRIGGER lfv4_trg_r10_market_player_attribute_overrides AFTER INSERT OR UPDATE OR DELETE ON " + NS + ".market_player_attribute_overrides DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION " + NS + ".lfv4_r10_draft();";

const sql003 = [
  "-- " + NS + " 003-invariants.sql",
  "-- Generated by tools/generate.mjs; behavior fixed by TRIGGER-RECIPES.md (recipes 1-10).",
  "-- PL/pgSQL invoker functions with fixed schema-qualified search_path.",
  "-- Violated cross-row rules raise SQLSTATE 23514 via ultimateteam.lfv4_err.",
  "-- Deferred checks are row-level CONSTRAINT TRIGGERs AFTER INSERT/UPDATE/DELETE,",
  "-- DEFERRABLE INITIALLY DEFERRED. They check the final stored row by ID (re-queried",
  "-- at check time), not stale NEW values; a row inserted and later deleted in the",
  "-- same transaction is skipped.",
  "-- Referenced mutable parent rows are locked SELECT ... FOR KEY SHARE before validation.",
  "",
  "-- Common",
  common,
  "",
  "-- Recipe 1: immutable keys and ownership",
  r1Fns,
  "",
  r1Triggers,
  "",
  "-- Recipe 2: immutable catalog",
  r2Fn,
  "",
  r2Triggers,
  "",
  "-- Recipe 3: exactly-one definition family",
  r3Fn,
  "",
  r3Triggers,
  "",
  "-- Recipe 4: exactly-one owned family",
  r4Fn,
  "",
  r4Triggers,
  "",
  "-- Recipe 5: squad references",
  r5Fn,
  "",
  r5Triggers,
  "",
  "-- Recipe 6: customization references",
  r6Fn,
  "",
  r6bFn,
  "",
  r6Triggers,
  "",
  "-- Recipe 7: player style and selected pick",
  r7Fn,
  "",
  r7Triggers,
  "",
  "-- Recipe 8: active market ownership and snapshot agreement",
  r8ListingFn,
  "",
  r8ItemFn,
  "",
  r8ItemSnapFn,
  "",
  r8PlayerSnapFn,
  "",
  r8Triggers,
  "",
  "-- Recipe 9: immutable published snapshot state",
  r9ItemFn,
  "",
  r9ChildFn,
  "",
  r9Triggers,
  "",
  "-- Recipe 10: draft player identity and typed overrides",
  "-- Attribute-code set for player_attribute_overrides and",
  "-- market_player_attribute_overrides: exactly the 35 detailed attribute columns",
  "-- on player_card_definitions (acceleration through gk_positioning).",
  "CREATE OR REPLACE FUNCTION " + NS + ".lfv4_attr_code_allowed(code text) RETURNS boolean\n" +
    "LANGUAGE sql IMMUTABLE SET search_path = pg_catalog\n" +
    "AS $$\n" +
    "    SELECT code IN (" + attrSetLiteral + ");\n" +
    "$$;\n",
  "",
  r10Fn,
  "",
  r10Triggers,
  "",
].join("\n");

// ---------------------------------------------------------------------------
// 5. schema/004-indexes.sql
// ---------------------------------------------------------------------------
const sql004 = [
  "-- " + NS + " 004-indexes.sql",
  "-- Generated by tools/generate.mjs from implementation-specification.json.",
  "-- The 64 named indexes, in listed order.",
  "",
  ...spec.indexes.map(
    (i) =>
      "CREATE " + (i.unique ? "UNIQUE " : "") + "INDEX " + i.name + " ON " + NS + "." + i.table + " (" + i.columns.join(", ") + ")" + (i.predicate ? " WHERE " + i.predicate : "") + ";"
  ),
  "",
].join("\n");

// ---------------------------------------------------------------------------
// 6. seed/010..017 — normalized rows, 250 rows per INSERT.
// ---------------------------------------------------------------------------
const colType = new Map();
for (const t of spec.tables) for (const c of t.columns) colType.set(t.name + "." + c.name, c.type);
const specColsByName = new Map(spec.tables.map((t) => [t.name, t.columns.map((c) => c.name)]));

function insertStmt(table, rows) {
  if (rows.length === 0) return null;
  const cols = specColsByName.get(table);
  const colLines = cols.map((c) => "    " + c).join(",\n");
  const rowLines = rows.map((r) =>
    "    (" + cols.map((c) => sqlValue(r[c], colType.get(table + "." + c))).join(", ") + ")"
  );
  return (
    "INSERT INTO " + NS + "." + table + " (\n" + colLines + "\n)\nVALUES\n" +
    rowLines.join(",\n") +
    ";"
  );
}

function chunkedInserts(table, rows) {
  const parts = [];
  for (let i = 0; i < rows.length; i += SEED_CHUNK) {
    const stmt = insertStmt(table, rows.slice(i, i + SEED_CHUNK));
    if (stmt) parts.push(stmt);
  }
  return parts;
}

const seedTexts = {};
let totalSeedRows = 0;
for (const sf of seedFiles) {
  const doc = sf.doc;
  const rel = "seed/" + doc.seedFile;
  const parts = [];
  for (const t of doc.tables) {
    if (t.rows.length > 0) {
      parts.push("-- " + t.name + " (" + t.rows.length + " rows)");
      parts.push(...chunkedInserts(t.name, t.rows));
    } else {
      parts.push("-- " + t.name + " (0 rows; no INSERT)");
    }
    totalSeedRows += t.rows.length;
  }
  seedTexts[rel] = [
    "-- " + NS + " " + doc.seedFile,
    "-- Generated by tools/generate.mjs from the verified normalized seed files.",
    "-- Plain INSERT statements only; max " + SEED_CHUNK + " rows per INSERT.",
    "",
    ...parts,
    "",
  ].join("\n");
}

// ---------------------------------------------------------------------------
// 7. seed/018-sequence-state.sql — identity sequences after explicit inserts.
// ---------------------------------------------------------------------------
const identityCols = [];
for (const t of spec.tables) {
  for (const c of t.columns) {
    if (c.identity) identityCols.push({ table: t.name, column: c.name });
  }
}
const maxId = new Map(); // "table.column" -> max integer value in seeded rows
for (const sf of seedFiles) {
  for (const t of sf.doc.tables) {
    for (const row of t.rows) {
      for (const [k, v] of Object.entries(row)) {
        if (typeof v === "number" && Number.isSafeInteger(v)) {
          const key = t.name + "." + k;
          const cur = maxId.get(key);
          if (cur === undefined || v > cur) maxId.set(key, v);
        }
      }
    }
  }
}
const seqLines = identityCols.map(({ table, column }) => {
  const m = maxId.get(table + "." + column);
  const call = m !== undefined;
  return (
    "-- " + table + "." + column + (call ? " (max seeded " + m + ")" : " (no seeded rows)") +
    "\nSELECT pg_catalog.setval(pg_catalog.pg_get_serial_sequence('" + NS + "." + table + "', '" + column + "'), " +
    (call ? m : 1) + ", " + (call ? "TRUE" : "FALSE") + ");"
  );
});
const sql018 = [
  "-- " + NS + " 018-sequence-state.sql",
  "-- Generated by tools/generate.mjs from the verified normalized seed files.",
  "-- Restores every identity sequence after explicit-ID inserts.",
  "-- Seeded columns: set to the maximum inserted value with is_called = true.",
  "-- Unseeded columns: set to 1 with is_called = false.",
  "",
  ...seqLines,
  "",
].join("\n");

// ---------------------------------------------------------------------------
// 8. manifest.json
// ---------------------------------------------------------------------------
const fkCount = spec.tables.reduce((n, t) => n + t.columns.filter((c) => c.references).length, 0);
const artifactFiles = [
  "schema/001-tables.sql",
  "schema/002-relationships.sql",
  "schema/003-invariants.sql",
  "schema/004-indexes.sql",
  ...seedFiles.map((sf) => "seed/" + sf.doc.seedFile),
  "seed/018-sequence-state.sql",
];
const sqlTexts = {
  "schema/001-tables.sql": sql001,
  "schema/002-relationships.sql": sql002,
  "schema/003-invariants.sql": sql003,
  "schema/004-indexes.sql": sql004,
  ...seedTexts,
  "seed/018-sequence-state.sql": sql018,
};
const manifest = {
  formatVersion: spec.formatVersion,
  versionCode: VERSION_CODE,
  postgresMajor: POSTGRES_MAJOR,
  namespace: NS,
  generatedFrom: {
    executionInputManifest: "database-handoff/execution-input-manifest.json",
    packetFiles: execManifest.files.length,
    normalizedSeedFiles: seedFiles.length,
    normalizedSeedManifest: "database-handoff/normalized-seed-manifest.json",
    implementationSpecification: "database-handoff/implementation-specification.json",
  },
  files: artifactFiles.map((rel) => {
    const text = sqlTexts[rel];
    return {
      path: rel,
      sha256: crypto.createHash("sha256").update(text, "utf8").digest("hex"),
      bytes: Buffer.byteLength(text, "utf8"),
    };
  }),
  counts: {
    tables: spec.tables.length,
    bootstrapLedger: 1,
    foreignKeys: fkCount,
    uniqueConstraints: uqParts.length,
    uniqueConstraintsListed: spec.uniqueConstraints.length,
    uniqueConstraintDuplicates: uqDeduped.length,
    checkConstraints: spec.checkConstraints.length,
    indexes: spec.indexes.length,
    triggerFunctions: (sql003.match(/CREATE OR REPLACE FUNCTION/g) || []).length,
    triggers: (sql003.match(/CREATE (?:CONSTRAINT )?TRIGGER/g) || []).length,
    seedFiles: Object.keys(seedTexts).length,
    seedRows: totalSeedRows,
  },
  executionOrder: artifactFiles,
  notes: [
    "The listed unique constraint set contains one exact duplicate (squad_players squad_id, footballer_id); only the first listing is created.",
    "All FKs are DEFERRABLE INITIALLY DEFERRED, so seed and test insertion order is unconstrained by FK cycles.",
    "The ledger table is created but never written by these artifacts; the validator owns ledger writes.",
    "item_definitions rows are split across seed files 013-016 by family; the four files together cover all 24247 definitions.",
  ],
};
manifest.packageSha256 = crypto.createHash("sha256").update(JSON.stringify(manifest.files.map(({ path, sha256 }) => ({ path, sha256 })))).digest("hex");
manifest.seedInputHashes = seedManifest.files.map(({ path, sha256 }) => ({ path, sha256 }));
manifest.seedTableCounts = seedManifest.rows;
const manifestText = JSON.stringify(manifest, null, 2) + "\n";

// ---------------------------------------------------------------------------
// 9. README.md
// ---------------------------------------------------------------------------
const README = `# UltimateTeam v4 fresh-schema artifacts

Deterministic PostgreSQL 18 artifacts for the \`ultimateteam\` relational design.
The SQL, manifest and README are generated by \`tools/generate.mjs\` from the
fixed handoff packet in \`../database-handoff\`; the generator verifies every
packet hash before writing a single byte.

## Files

| File | Purpose |
| --- | --- |
| \`tools/generate.mjs\` | Deterministic artifact generator (Node built-ins only). |
| \`tools/validate.mjs\` | Disposable-database validator; writes \`validation/results.json\`. |
| \`schema/001-tables.sql\` | Schema, standalone bootstrap ledger, ${spec.tables.length} domain tables. |
| \`schema/002-relationships.sql\` | ${fkCount} FKs (all deferred), ${uqParts.length} UQ constraints, ${spec.checkConstraints.length} CHECK constraints. |
| \`schema/003-invariants.sql\` | The ten trigger recipes from \`TRIGGER-RECIPES.md\`. |
| \`schema/004-indexes.sql\` | The ${spec.indexes.length} named indexes. |
| \`seed/010..017-*.sql\` | The ${totalSeedRows} normalized seed rows, max 250 per INSERT. |
| \`seed/018-sequence-state.sql\` | Identity sequence restoration after explicit-ID inserts. |
| \`manifest.json\` | Artifact hashes, byte sizes, counts, execution order. |
| \`validation/results.json\` | Written by the validator; never hand-edited. |

## Execution contract

- PostgreSQL 18 only. One fresh disposable database per scenario.
- Execute the files in \`manifest.json\` \`executionOrder\` exactly, as plain
  server-executable SQL (no psql meta commands, no embedded transactions).
- The generated SQL contains no runtime-generated timestamps, connection strings, cluster
  management, downloads, or ledger inserts.
- The ledger table \`ultimateteam.setup_artifact_versions\` is created by
  001 but written only by the validator's ledger helper.

## Generator

\`\`\`
node tools/generate.mjs
\`\`\`

Re-running produces byte-identical output. The packet directory is resolved
relative to the tool file (\`../../database-handoff\`), never the working
directory.

## Validator

\`\`\`
node tools/validate.mjs
\`\`\`

Runs the bootstrap and domain scenarios from \`FIXED-VALIDATION.md\` against
throwaway PostgreSQL 18 databases and records actual server results in
\`validation/results.json\`. Docker and the local \`postgres:18\` image are required.
The validator removes only its own container.

## Scope and data status

These are fresh-schema artifacts, not a server integration or an existing-club
migration. Identity groups remain unresolved, card types unconfirmed, and item
definitions draft, as recorded in the frozen packet. Successful SQL validation
does not verify native FIFA identities, client compatibility, or performance.
`;

// ---------------------------------------------------------------------------
// 10. Write everything (deterministic, LF-terminated).
// ---------------------------------------------------------------------------
const outputs = { ...sqlTexts, "manifest.json": manifestText, "README.md": README };
for (const [rel, text] of Object.entries(outputs)) {
  if (!text.endsWith("\n")) fail("output " + rel + " must end with a newline");
  const p = path.join(SETUP_DIR, rel);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, text, { flag: "w" });
  console.log("wrote " + rel + " (" + Buffer.byteLength(text, "utf8") + " bytes)");
}
console.log(
  "done: " + spec.tables.length + " tables, " + manifest.counts.foreignKeys + " FKs, " +
  manifest.counts.uniqueConstraints + " UQs (" + manifest.counts.uniqueConstraintDuplicates + " duplicate listing skipped), " +
  manifest.counts.checkConstraints + " CHECKs, " + manifest.counts.indexes + " indexes, " +
  totalSeedRows + " seed rows in " + Object.keys(seedTexts).length + " seed files"
);
