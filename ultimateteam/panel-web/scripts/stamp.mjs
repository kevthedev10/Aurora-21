// Records which sources the committed build in Resources/panel/ came from, so a stale build is caught
// (OperatorPanelTests recomputes the same hash in C#; keep the two in step).
//   node scripts/stamp.mjs          write Resources/panel/build-stamp.txt after a build
//   node scripts/stamp.mjs --check  exit 1 when the sources changed since the last build
// Hash: SHA-256 over every source file, sorted by its path relative to panel-web/ ('/' separators, ordinal order),
// each as UTF-8 "<path>\n" + its bytes with every CR (0x0D) removed + "\n". CRs are dropped so a Windows checkout
// (core.autocrlf) hashes the same as WSL. Test files (*.test.ts, *.test.tsx) do not change the build and are skipped.
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, statSync, writeFileSync, existsSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const stampFile = fileURLToPath(new URL('../../server/src/Aurora21.UltimateTeam.Server/Resources/panel/build-stamp.txt', import.meta.url));
const FILES = ['index.html', 'package.json', 'package-lock.json', 'tsconfig.json', 'vite.config.ts'];
const FOLDERS = ['src', 'public', 'scripts'];
const isTest = (name) => /\.test\.tsx?$/.test(name);

function walk(folder, out) {
  if (!existsSync(folder)) return;
  for (const name of readdirSync(folder)) {
    const full = join(folder, name);
    if (statSync(full).isDirectory()) walk(full, out);
    else if (!isTest(name)) out.push(full);
  }
}

export function sourceHash() {
  const files = FILES.map((name) => join(root, name)).filter((file) => existsSync(file));
  for (const folder of FOLDERS) walk(join(root, folder), files);
  const entries = files.map((file) => ({ path: relative(root, file).split(sep).join('/'), file }));
  entries.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
  const hash = createHash('sha256');
  for (const { path, file } of entries) {
    hash.update(`${path}\n`, 'utf8');
    hash.update(readFileSync(file).filter((byte) => byte !== 0x0d));
    hash.update('\n', 'utf8');
  }
  return hash.digest('hex');
}

const current = sourceHash();
if (process.argv.includes('--check')) {
  const recorded = existsSync(stampFile) ? readFileSync(stampFile, 'utf8').trim() : '(none)';
  if (recorded !== current) {
    console.error(`panel is stale: built from ${recorded}, sources are ${current}. Run npm run build.`);
    process.exit(1);
  }
  console.log(`panel matches its sources (${current}).`);
} else {
  writeFileSync(stampFile, `${current}\n`);
  console.log(`Stamped panel with ${current}.`);
}
