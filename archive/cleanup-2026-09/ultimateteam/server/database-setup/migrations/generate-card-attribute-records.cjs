// Builds 047-card-attribute-records.sql: verified card corrections plus attribute gaps and traits filled from FIFA 21's
// own player table, ported from the Python server (fix/rivals-osdk-pregame: e701202, 9410058, 73301ed, fe783d5).
// Inputs: the catalogue the seed was built from (players.json, custom-players.json), card-corrections.json (FUTWIZ and
// FUTBIN scans, accepted by the maintainer 2026-09-25) and the native cards_ng_db players export.
// Run: node ultimateteam/server/database-setup/migrations/generate-card-attribute-records.cjs
const fs = require('fs'), crypto = require('crypto');
process.chdir(require('path').resolve(__dirname, '../../../..'));
const base = 'ultimateteam/server/database-setup/migrations/';
const paths = {
  catalogue: 'ultimateteam/runtime-source/ultimateteam21/players.json',
  custom: 'ultimateteam/runtime-source/ultimateteam21/custom-players.json',
  corrections: 'ultimateteam/runtime-source/ultimateteam21/card-corrections.json',
  native: 'ultimateteam/runtime/mined/tables/cards_ng_db/players.json',
  normalization: 'ultimateteam/server/database-handoff/NORMALIZATION-AUDIT.json',
};
const hash = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const read = p => JSON.parse(fs.readFileSync(p, 'utf8'));

// FIFA's slot order for the 34 in-game attributes (CardsDLL Attribute Details reads them by fixed offset).
const ORDER = ['acceleration', 'sprintspeed', 'agility', 'balance', 'jumping', 'stamina', 'strength', 'reactions', 'aggression',
  'composure', 'interceptions', 'positioning', 'vision', 'ballcontrol', 'crossing', 'dribbling', 'finishing', 'freekickaccuracy',
  'headingaccuracy', 'longpassing', 'shortpassing', 'marking', 'shotpower', 'longshots', 'standingtackle', 'slidingtackle',
  'volleys', 'curve', 'penalties', 'gkdiving', 'gkhandling', 'gkkicking', 'gkreflexes', 'gkpositioning'];
const COLUMN = {
  acceleration: 'acceleration', sprintspeed: 'sprint_speed', agility: 'agility', balance: 'balance', jumping: 'jumping',
  stamina: 'stamina', strength: 'strength', reactions: 'reactions', aggression: 'aggression', composure: 'composure',
  interceptions: 'interceptions', positioning: 'positioning', vision: 'vision', ballcontrol: 'ball_control', crossing: 'crossing',
  dribbling: 'dribbling', finishing: 'finishing', freekickaccuracy: 'free_kick_accuracy', headingaccuracy: 'heading_accuracy',
  longpassing: 'long_passing', shortpassing: 'short_passing', marking: 'marking', shotpower: 'shot_power', longshots: 'long_shots',
  standingtackle: 'standing_tackle', slidingtackle: 'sliding_tackle', volleys: 'volleys', curve: 'curve', penalties: 'penalties',
  gkdiving: 'gk_diving', gkhandling: 'gk_handling', gkkicking: 'gk_kicking', gkreflexes: 'gk_reflexes', gkpositioning: 'gk_positioning',
};
const FACE_WEIGHTS = [
  { acceleration: .45, sprintspeed: .55 },
  { finishing: .45, longshots: .20, shotpower: .20, positioning: .05, volleys: .05, penalties: .05 },
  { shortpassing: .35, vision: .20, crossing: .20, longpassing: .15, curve: .05, freekickaccuracy: .05 },
  { dribbling: .50, ballcontrol: .35, agility: .10, balance: .05 },
  { marking: .30, standingtackle: .30, interceptions: .20, headingaccuracy: .10, slidingtackle: .10 },
  { strength: .50, stamina: .25, aggression: .20, jumping: .05 },
];
const GK_FACE_WEIGHTS = [{ gkdiving: 1 }, { gkhandling: 1 }, { gkkicking: 1 }, { gkreflexes: 1 },
  { acceleration: .45, sprintspeed: .55 }, { gkpositioning: 1 }];
const REVISION = 16777216;

// FIFA's own table: 34 attributes in slot order, birth date and trait bitfields per player id.
const epoch = Date.UTC(1582, 9, 14);
const table = new Map(read(paths.native).map(r => [r.playerid, {
  a: ORDER.map(k => r[k]), dob: new Date(epoch + r.birthdate * 86400000).toISOString().slice(0, 10), t: [r.trait1, r.trait2],
}]));

// The catalogue as the seed has it, with the corrections applied on top.
const catalogue = new Map();
for (const [id, card] of Object.entries(read(paths.catalogue))) catalogue.set(Number(card.resourceId ?? id), structuredClone(card));
for (const [id, card] of Object.entries(read(paths.custom))) if (/^\d+$/.test(id)) catalogue.set(Number(id), { resourceId: Number(id), ...structuredClone(card) });
const corrections = read(paths.corrections).cards;
const corrected = new Map();
for (const [id, fix] of Object.entries(corrections)) {
  const card = catalogue.get(Number(id));
  if (!card) continue;
  const change = {};
  if (fix.attributes) { card.attributes = { ...(card.attributes || {}) }; for (const [k, v] of Object.entries(fix.attributes)) { card.attributes[k] = v; } change.attributes = fix.attributes; }
  if (fix.face) { card.face = fix.face; change.face = fix.face; }
  for (const key of ['workRateAttack', 'workRateDefense', 'skillMoves', 'weakFoot']) if (fix[key] !== undefined) { card[key] = fix[key]; change[key] = fix[key]; }
  corrected.set(Number(id), change);
}

// The seed renumbered 101 catalogue cards whose resource id FIFA uses for another card; rows are written under the
// seeded id, while the catalogue id (the one Python serves) still drives the lookups below.
const seededId = new Map(read(paths.normalization).replacements.map(r => [r.oldNativeResourceId, r.newNativeResourceId]));
const seeded = resource => seededId.get(resource) ?? resource;

const norm = k => k.toLowerCase().replaceAll('_', '');
const tableRow = (playerId, card) => { const row = table.get(playerId); return row && row.dob === card.dateOfBirth ? row : null; };
const faceMatches = (values, face, goalkeeper) => {
  if (!face || face.length < 6) return false;
  const named = Object.fromEntries(ORDER.map((k, i) => [k, values[i]]));
  return (goalkeeper ? GK_FACE_WEIGHTS : FACE_WEIGHTS).every((weights, i) =>
    Math.abs(Math.floor(Object.entries(weights).reduce((s, [k, w]) => s + named[k] * w, 0) + 0.5) - face[i]) <= 4);
};

const fills = [], traitRows = [];
let complete = 0;
for (const [resource, card] of catalogue) {
  const supplied = Object.fromEntries(Object.entries(card.attributes || {}).map(([k, v]) => [norm(k), Number(v) || 0]));
  const asset = Number(card.assetId) || 0;
  const goalkeeper = String(card.preferredPosition || '').toUpperCase() === 'GK';
  const playerId = resource & (REVISION - 1);
  let exact = tableRow(playerId, card)?.a ?? null;
  if (exact && resource >= REVISION && (playerId === asset || !faceMatches(exact, card.face, goalkeeper))) exact = null;
  let baseRow = exact;
  if (!exact && asset) {
    baseRow = tableRow(asset, card)?.a ?? null;
    const baseCard = catalogue.get(asset);
    if (baseRow && asset !== resource && baseCard && JSON.stringify(baseCard.face) === JSON.stringify(card.face) && baseCard.rating === card.rating) exact = baseRow;
  }
  const values = [], filled = {};
  let known = true;
  ORDER.forEach((name, i) => {
    let value = supplied[name] ?? 0;
    if (value <= 0) {
      if (exact) value = exact[i];
      else if (baseRow && name.startsWith('gk') && !goalkeeper) value = baseRow[i];
      if (value > 0) filled[name] = Math.min(99, value);
    }
    if (!(value >= 1 && value <= 99)) known = false;
    values.push(Math.max(0, Math.min(99, value)));
  });
  if (known && faceMatches(values, card.face, goalkeeper)) complete++;
  const change = corrected.get(resource);
  if (change || Object.keys(filled).length) fills.push({ resource: seeded(resource), change: change || {}, filled });
  // Traits: the card's own row, else its asset's row (birth date checked); linked only where the card has none.
  const traitSource = tableRow(playerId, card) ?? (asset ? tableRow(asset, card) : null);
  if (traitSource && (traitSource.t[0] || traitSource.t[1])) traitRows.push({ resource: seeded(resource), native: tableRow(playerId, card) ? playerId : asset, t1: traitSource.t[0], t2: traitSource.t[1] });
}
fills.sort((a, b) => a.resource - b.resource);
traitRows.sort((a, b) => a.resource - b.resource);

const sqlq = s => "'" + String(s).replaceAll("'", "''") + "'";
const lit = v => v === undefined || v === null ? 'NULL' : typeof v === 'string' ? sqlq(v) : String(v);
const columns = [...ORDER.map(k => COLUMN[k]), 'face_slot_0', 'face_slot_1', 'face_slot_2', 'face_slot_3', 'face_slot_4', 'face_slot_5',
  'work_rate_attack', 'work_rate_defense', 'skill_moves', 'weak_foot'];
const rowSql = f => {
  const attr = ORDER.map(k => f.change.attributes?.[k] ?? f.filled[k]);
  const face = [0, 1, 2, 3, 4, 5].map(i => f.change.face?.[i]);
  return `(${[f.resource, ...attr, ...face, f.change.workRateAttack, f.change.workRateDefense, f.change.skillMoves, f.change.weakFoot].map(lit).join(',')})`;
};
let sql = `-- Card attribute records ported from the Python server (fix/rivals-osdk-pregame e701202, 9410058, 73301ed, fe783d5).
-- Generated by generate-card-attribute-records.cjs; do not edit by hand.
-- Corrections ${paths.corrections} SHA-256 ${hash(paths.corrections)} (FUTWIZ scan checked against FUTBIN; base cards and
--   icon versions follow FIFA 21's own table; accepted by the maintainer 2026-09-25).
-- Native ${paths.native} SHA-256 ${hash(paths.native)} (FIFA 21 cards_ng_db players).
-- Catalogue ${paths.catalogue} SHA-256 ${hash(paths.catalogue)}; ${paths.custom} SHA-256 ${hash(paths.custom)}; the seed's
--   renumbered ids from ${paths.normalization} SHA-256 ${hash(paths.normalization)}.
-- 1. Corrections set absolute values; attribute gaps are filled from FIFA's row for the card's player (resourceId & 0xFFFFFF,
--    an icon version's own row) or its base card's row, only when the birth date matches (some imported cards reuse ids).
-- 2. Trait links are added from the same rows for cards that have none.
SET CONSTRAINTS ALL IMMEDIATE;
CREATE TEMP TABLE card_attribute_fix(resource_id bigint PRIMARY KEY, ${columns.map(c => c + (c.startsWith('work_rate') ? ' text' : ' bigint')).join(', ')}) ON COMMIT DROP;
`;
for (let i = 0; i < fills.length; i += 250) {
  sql += `INSERT INTO card_attribute_fix VALUES\n${fills.slice(i, i + 250).map(rowSql).join(',\n')};\n`;
}
sql += `ALTER TABLE ultimateteam.player_card_definitions DISABLE TRIGGER lfv4_trg_r2_player_card_definitions;
UPDATE ultimateteam.player_card_definitions p SET ${columns.map(c => `${c} = COALESCE(x.${c}, p.${c})`).join(', ')}
FROM card_attribute_fix x JOIN ultimateteam.item_definitions d ON d.native_resource_id = x.resource_id AND d.family_id = 1
WHERE p.definition_id = d.definition_id;
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.player_card_definitions ENABLE TRIGGER lfv4_trg_r2_player_card_definitions;
CREATE TEMP TABLE card_trait_fill(resource_id bigint PRIMARY KEY, native_player_id bigint NOT NULL, trait1 bigint NOT NULL, trait2 bigint NOT NULL) ON COMMIT DROP;
`;
for (let i = 0; i < traitRows.length; i += 500) {
  sql += `INSERT INTO card_trait_fill VALUES\n${traitRows.slice(i, i + 500).map(t => `(${t.resource},${t.native},${t.t1},${t.t2})`).join(',\n')};\n`;
}
sql += `INSERT INTO ultimateteam.player_card_traits(definition_id, trait_id, source_native_player_id, source_sha256)
SELECT p.definition_id, t.trait_id, s.native_player_id, '${hash(paths.native)}'
FROM card_trait_fill s
JOIN ultimateteam.item_definitions d ON d.native_resource_id = s.resource_id AND d.family_id = 1
JOIN ultimateteam.player_card_definitions p ON p.definition_id = d.definition_id
JOIN ultimateteam.traits t ON ((CASE t.mask_word WHEN 1 THEN s.trait1 ELSE s.trait2 END) & t.bit_value) <> 0
WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.player_card_traits x WHERE x.definition_id = p.definition_id)
ORDER BY p.definition_id, t.trait_id;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(47);
`;
fs.writeFileSync(base + '047-card-attribute-records.sql', sql);
const audit = {
  inputs: Object.fromEntries(Object.entries(paths).map(([k, p]) => [k, { path: p, sha256: hash(p) }])),
  catalogueCards: catalogue.size, correctionsInCatalogue: corrected.size, correctionsNotInCatalogue: Object.keys(corrections).length - corrected.size,
  cardsWithFixRows: fills.length, cardsWithFilledAttributes: fills.filter(f => Object.keys(f.filled).length).length,
  cardsWithTraitRows: traitRows.length, renumberedCards: [...catalogue.keys()].filter(r => seededId.has(r)).length, completeFaceConsistentCards: complete,
};
fs.writeFileSync(base + 'CARD-ATTRIBUTES-AUDIT.json', JSON.stringify(audit, null, 2) + '\n');
console.log(audit);
