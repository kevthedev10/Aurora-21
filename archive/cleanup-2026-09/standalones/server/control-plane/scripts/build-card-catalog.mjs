import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const sourcePaths = [
  resolve(scriptDirectory, '../../standalone-server/runtime-source/localfut21/players.json'),
  resolve(scriptDirectory, '../../standalone-server/runtime-source/localfut21/custom-players.json'),
];
const destinationPath = resolve(scriptDirectory, '../src/card-catalog.json');
const source = {};
for (const sourcePath of sourcePaths) {
  const catalogue = JSON.parse(readFileSync(sourcePath, 'utf8'));
  if (!catalogue || typeof catalogue !== 'object' || Array.isArray(catalogue)) {
    throw new Error(`The FIFA 21 player catalogue ${sourcePath} must be a JSON object.`);
  }
  Object.assign(source, catalogue);
}

// Match server.py's collision-safe migration for zero-revision Icon Moments
// IDs. The panel must submit the effective resource ID that PLAYER_DB exposes.
const revisionSize = 0x01000000;
for (const [key, metadata] of Object.entries(source)) {
  const oldResourceId = Number(key);
  if (Number(metadata?.rareflag ?? 0) !== 84 || oldResourceId >= revisionSize) continue;
  let newResourceId = oldResourceId + revisionSize;
  while (Object.hasOwn(source, String(newResourceId))) newResourceId += revisionSize;
  delete source[key];
  source[String(newResourceId)] = { ...metadata, resourceId: newResourceId };
}

const rows = [];
for (const [key, metadata] of Object.entries(source)) {
  if (!metadata || typeof metadata !== 'object' || Array.isArray(metadata)) continue;
  const resourceId = Number(metadata.resourceId ?? key);
  const name = String(metadata.name ?? '').trim();
  const rating = Number(metadata.rating ?? 0);
  const rareflag = Number(metadata.rareflag ?? 0);
  const rarityName = metadata.rarityName ?? (rareflag === 10 ? 'Pro Player' : `Rarity ${rareflag}`);
  if (!Number.isSafeInteger(resourceId) || resourceId < 1 || resourceId > 2_147_483_647 || !name) continue;
  rows.push([
    resourceId,
    name.slice(0, 100),
    Number.isSafeInteger(rating) ? rating : 0,
    String(metadata.preferredPosition ?? metadata.position ?? '-').slice(0, 20),
    String(rarityName).slice(0, 80),
    String(metadata.teamName ?? '').slice(0, 100),
  ]);
}

rows.sort((left, right) =>
  right[2] - left[2] ||
  left[1].localeCompare(right[1], 'en', { sensitivity: 'base' }) ||
  left[0] - right[0]
);

writeFileSync(destinationPath, `${JSON.stringify(rows)}\n`, 'utf8');
console.log(`Wrote ${rows.length} cards to ${destinationPath}`);
