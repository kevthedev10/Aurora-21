#!/usr/bin/env node
// Replays one deterministic FUT request sequence against the Python runtime and the C# runtime,
// each in its own isolated HTTP-only test profile, and reports every response difference.
//
// Usage:
//   node localfut/server/tools/parity/compare.mjs --python <python3> --csharp <server exe or .dll> [--dotnet <dotnet>] [--keep]
//
// After the live sequence both servers are restarted: Python on its own profile, C# on a fresh
// profile holding a copy of the Python club database, which the C# server imports on start.
// Every C# profile's managed PostgreSQL cluster is stopped through that server's own command
// before its disposable runtime directory is removed.
//
// Volatile values (clocks, random session ids, build hashes) are normalized; responses whose
// contents are random by design (pack contents, player-pick choices) are compared by shape.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { copyFile, mkdtemp, mkdir, rm, access } from 'node:fs/promises';
import http from 'node:http';
import net from 'node:net';
import { tmpdir } from 'node:os';
import { isAbsolute, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const G = '/ut/game/fifa21';
const PRODUCTION_PORTS = [8090, 8081, 42230, 42231, 42232, 44321, 10853, 17502, 3216];

// Keys whose values depend on the clock, a random session secret or the implementation.
const VOLATILE_KEYS = new Set([
  'timestamp', 'serverTime', 'lastOnlineTime', 'currentTime', 'crtTime', 'serverCrtTime', 'serverTimeInSeconds',
  'startTime', 'endTime', 'startTimeStamp', 'endTimeStamp', 'eventStartTime', 'eventEndTime', 'nextEventStartTime',
  'secondsUntilEnd', 'timeUntilEnd', 'untilEndSeconds', 'nextReset', 'nextResetTime', 'weeklyResetTime',
  'registrationCloseTime', 'rewardsTime', 'generatedAt', 'rewardingTime', 'established', 'startDateTime',
  'nextSquadRefreshTimeStamp', 'transactionId', 'sid', 'runtimeRoot', 'dbPath', 'database', 'clubId',
  'serverSourceSha256', 'onboardingSourceSha256', 'serverBuildSha256', 'serverImplementation',
]);

function parseArgs(argv) {
  const options = { dotnet: 'dotnet', keep: false };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--keep') options.keep = true;
    else if (arg === '--python') options.python = argv[++i];
    else if (arg === '--baseline') options.baseline = argv[++i];
    else if (arg === '--legacy-db') options.legacyDb = argv[++i];
    else if (arg === '--csharp') options.csharp = argv[++i];
    else if (arg === '--dotnet') options.dotnet = argv[++i];
    else throw new Error(`Unknown argument ${arg}`);
  }
  assert((options.python || options.baseline) && options.csharp && !(options.python && options.baseline),
    'Usage: compare.mjs (--python <python3> | --baseline <earlier server exe|dll>) --csharp <server exe|dll> [--dotnet <dotnet>] [--keep]');
  assert(isAbsolute(options.csharp), '--csharp must be an absolute path');
  assert(!options.baseline || isAbsolute(options.baseline), '--baseline must be an absolute path');
  return options;
}

// Baseline mode compares two C# builds, so JSON key order must match exactly as well.
let orderedKeys = false;

async function freePort() {
  const socket = net.createServer();
  await new Promise((done, fail) => { socket.once('error', fail); socket.listen(0, '127.0.0.1', done); });
  const { port } = socket.address();
  await new Promise((done) => socket.close(done));
  assert(!PRODUCTION_PORTS.includes(port));
  return port;
}

function request(port, method, path, body) {
  return new Promise((done, fail) => {
    const payload = body === undefined ? undefined : Buffer.from(JSON.stringify(body));
    const outgoing = http.request({
      hostname: '127.0.0.1', family: 4, port, path, method, agent: false,
      headers: payload ? { 'Content-Type': 'application/json', 'Content-Length': payload.length } : {},
    }, (response) => {
      const chunks = [];
      response.on('data', (chunk) => chunks.push(chunk));
      response.on('end', () => {
        const raw = Buffer.concat(chunks);
        const contentType = response.headers['content-type'] || '';
        let json;
        if (contentType.includes('json')) {
          try { json = JSON.parse(raw.toString('utf8')); } catch { json = undefined; }
        }
        done({ status: response.statusCode, headers: response.headers, json, raw });
      });
    });
    outgoing.setTimeout(60000, () => outgoing.destroy(new Error(`timeout ${method} ${path}`)));
    outgoing.on('error', fail);
    outgoing.end(payload);
  });
}

async function startServer(name, command, commandArgs, root, databaseStopArgs = null) {
  const configRoot = join(root, 'config');
  await mkdir(configRoot, { recursive: true });
  const port = await freePort();
  let log = '';
  const childEnvironment = {
    ...process.env,
    PYTHONDONTWRITEBYTECODE: '1',
    LOCALFUT_RUNTIME_ROOT: root,
    LOCALFUT21_USER_CONFIG_ROOT: configRoot,
    // A parity run must always own disposable state; never inherit a developer's
    // external/live PostgreSQL connection.
    LOCALFUT_POSTGRES_CONNECTION: '',
  };
  const child = spawn(command, [...commandArgs, '--http-only-test-port', String(port), '--lsx-mode', 'off'], {
    cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: childEnvironment,
  });
  const server = { name, port, child, command, databaseStopArgs, root, configRoot, childEnvironment, stopped: false };
  child.stdout.on('data', (data) => { log = (log + data).slice(-8000); });
  child.stderr.on('data', (data) => { log = (log + data).slice(-8000); });
  try {
    const deadline = Date.now() + 120000;
    while (Date.now() < deadline) {
      if (child.exitCode !== null) throw new Error(`${name} exited early:\n${log}`);
      try {
        const status = await request(port, 'GET', '/localfut/test/status');
        if (status.status === 200) return server;
      } catch { /* not ready yet */ }
      await delay(250);
    }
    throw new Error(`${name} did not become ready:\n${log}`);
  } catch (error) {
    await stop(server).catch((cleanupError) => {
      error.message += `\n${name} cleanup also failed: ${cleanupError.message}`;
    });
    throw error;
  }
}

// ---- normalization and comparison -------------------------------------------------------

function normalize(value, key) {
  if (key !== undefined && VOLATILE_KEYS.has(key)) return '<volatile>';
  if (Array.isArray(value)) return value.map((item) => normalize(item));
  if (value && typeof value === 'object') {
    const out = {};
    for (const [childKey, child] of Object.entries(value)) out[childKey] = normalize(child, childKey);
    return out;
  }
  if (typeof value === 'string' && /^(pow|ut)\.[0-9a-f]{32}$/.test(value)) return '<session-sid>';
  return value;
}

// Special cards (Icons, TOTY, ...) carry catalogue name fields that plain cards omit, so in
// random pack contents these keys are expected to come and go.
const OPTIONAL_SHAPE_KEYS = new Set(['firstName', 'lastName', 'commonName', 'commonname', 'knownAs', 'name', 'playerName', 'duplicate', 'isDuplicate']);

function shape(value) {
  if (Array.isArray(value)) return value.length ? [shape(value[0])] : [];
  if (value && typeof value === 'object') {
    const out = {};
    for (const key of Object.keys(value).sort()) {
      if (!OPTIONAL_SHAPE_KEYS.has(key)) out[key] = shape(value[key]);
    }
    return out;
  }
  return value === null ? 'null' : typeof value;
}

function diff(left, right, path = '$', out = [], lenient = false) {
  if (out.length > 25) return out;
  // In shape mode an empty random list (e.g. no duplicates this time) is compatible with a populated one.
  if (lenient && Array.isArray(left) && Array.isArray(right) && (left.length === 0 || right.length === 0)) return out;
  if (typeof left === 'number' && typeof right === 'number') {
    if (left !== right) out.push(`${path}: ${left} != ${right}`);
    return out;
  }
  if (Array.isArray(left) && Array.isArray(right)) {
    if (left.length !== right.length) out.push(`${path}: array length ${left.length} != ${right.length}`);
    for (let i = 0; i < Math.min(left.length, right.length); i++) diff(left[i], right[i], `${path}[${i}]`, out, lenient);
    return out;
  }
  if (left && right && typeof left === 'object' && typeof right === 'object' && !Array.isArray(left) && !Array.isArray(right)) {
    const leftKeys = Object.keys(left);
    const rightKeys = Object.keys(right);
    for (const key of leftKeys) if (!(key in right)) out.push(`${path}.${key}: missing in C#`);
    for (const key of rightKeys) if (!(key in left)) out.push(`${path}.${key}: only in C#`);
    for (const key of leftKeys) if (key in right) diff(left[key], right[key], `${path}.${key}`, out, lenient);
    return out;
  }
  if (JSON.stringify(left) !== JSON.stringify(right)) {
    out.push(`${path}: ${JSON.stringify(left)?.slice(0, 120)} != ${JSON.stringify(right)?.slice(0, 120)}`);
  }
  return out;
}

// ---- the replayed sequence -------------------------------------------------------------------

// mode: 'exact' (normalized values), 'shape' (keys/types only), 'status' (status + content type)
const sequence = [
  ['exact', 'GET', '/ut/auth'],
  ['exact', 'GET', '/ut/shards/v2'],
  ['exact', 'GET', '//pow/auth'],
  ['exact', 'GET', '/pow/bank/user/account'],
  ['exact', 'GET', `${G}/resourcemapping`],
  ['exact', 'GET', `${G}/user/accountinfo`],
  ['exact', 'GET', `${G}/settings`],
  ['exact', 'GET', `${G}/userdata`],
  ['exact', 'GET', `${G}/clientdata/pilesize`],
  ['exact', 'GET', `${G}/clientdata/userhubdata`],
  ['exact', 'GET', `${G}/clientdata/managerquest`],
  ['exact', 'GET', `${G}/clientdata/tutorialpopups`],
  ['exact', 'GET', `${G}/eventfeed`],
  ['exact', 'GET', `${G}/clubuser`],
  ['exact', 'GET', `${G}/leaderboards/options`],
  ['exact', 'GET', `${G}/user/credits`],
  ['exact', 'GET', `${G}/tradepile`],
  ['exact', 'GET', `${G}/user`],
  ['exact', 'GET', `${G}/squad/active`],
  ['exact', 'GET', `${G}/hub`],
  ['exact', 'GET', `${G}/userMassInfo?screen=futlivemsggen4`],
  ['exact', 'GET', `${G}/loan/players`],
  ['exact', 'GET', `${G}/stadium`],
  ['exact', 'GET', `${G}/club?count=20`],
  ['exact', 'POST', `${G}/club`, { type: 'manager', sort: 'desc', start: 0, count: 91 }],
  ['exact', 'POST', `${G}/club`, { type: 'player', sort: 'asc', start: 30, count: 15 }],
  ['exact', 'POST', `${G}/club`, { searchCriteria: { type: 'clubInfo', category: 'kit', start: 0, count: 20 } }],
  ['exact', 'GET', `${G}/club?type=clubInfo&cat=badge&count=20`],
  ['exact', 'GET', `${G}/club/stats/summary`],
  ['exact', 'GET', `${G}/club/consumables/development`],
  ['exact', 'GET', `${G}/store/purchasegroup/all?ppInfo=true`],
  ['exact', 'GET', `${G}/store/purchasegroup/mypacks`],
  ['exact', 'GET', `${G}/purchased/items`],
  ['exact', 'GET', `${G}/sbs/sets`],
  ['exact', 'GET', `${G}/sbs/setId/900000/challenges`],
  ['exact', 'GET', `${G}/sbs/setId/903100/challenges`],
  ['exact', 'GET', `${G}/sbs/challenge/900001/squad`],
  ['exact', 'GET', `${G}/scmp/campaign/active`],
  ['exact', 'PUT', `${G}/scmp/active/category/1/details`, { groupList: [{ groupId: 1, groupType: 0 }] }],
  ['exact', 'GET', `${G}/rivals/user/hub`],
  ['exact', 'GET', `${G}/champion/user/hub`],
  ['exact', 'GET', `${G}/champion/user/stats?save=false&period=curr&type=base`],
  ['exact', 'GET', `${G}/champion/leaderboard?save=false&period=curr&type=base`],
  ['exact', 'GET', '/fut/seasonal/seasonalconfig.json'],
  ['exact', 'GET', `${G}/sqbt/user/hub`],
  ['exact', 'GET', `${G}/sqbt/user/opponentsquad/0`],
  ['exact', 'GET', `${G}/sqbt/user/opponentsquad/3`],
  ['exact', 'GET', `${G}/attributes/metadata?defIds=158023,20801,167963031,190871,1000000`],
  ['exact', 'GET', `${G}/defid?defId=158023`],
  ['exact', 'GET', `${G}/squad/mode/draft/state?mode=SINGLE_PLAYER`],
  ['exact', 'POST', `${G}/match`, { squadId: 1, type: 'OFFLINE', sqbtOpponentSquadId: 1 }],
  ['exact', 'POST', `${G}/match/end`, { endReason: 'WIN' }],
  ['exact', 'GET', `${G}/offlinesquad/1`],
  ['exact', 'GET', `${G}/item?idList=900000000001,900000000002,999999999999`],
  ['exact', 'GET', `${G}/item/900000000003`],
  ['exact', 'GET', `${G}/item/999999999999`],
  ['exact', 'GET', `${G}/unknown/route`],
  ['status', 'GET', '/fut/packs/loc/storepackdescriptions.en_gb.xml'],
  ['status', 'GET', '/fut/loc/pc/leaderboards.eng_us.xml'],
  ['exact', 'POST', '/localfut/customisation/grant-all', {}],
  // onboarding flow
  ['exact', 'PUT', `${G}/loan/player/10001`],
  ['exact', 'PUT', `${G}/loan/player/10002`],
  ['exact', 'PUT', `${G}/item`, { itemData: [{ id: 900000000254, pile: 'squad' }] }],
  ['exact', 'GET', `${G}/onboarding/kits`],
  ['exact', 'POST', `${G}/onboarding/kits`, { homeKitId: 6300074, awayKitId: 6301345 }],
  ['exact', 'GET', `${G}/onboarding/badges`],
  ['exact', 'POST', `${G}/onboarding/badge/6000057`],
  ['exact', 'PUT', `${G}/clientdata/squad`, { entries: [{ key: 5, value: 0 }] }],
  ['exact', 'PUT', `${G}/clientdata/onboarding`, { entries: [{ key: 7, value: true }] }],
  ['exact', 'GET', `${G}/clientdata/onboarding`],
  ['exact', 'GET', `${G}/clientdata/loanplayerreward`],
  ['exact', 'GET', `${G}/userMassInfo?screen=futlivemsggen4`],
  // SBC submit and free reward pack
  ['exact', 'PUT', `${G}/sbs/challenge/900001/squad`, { players: [{ itemData: { id: 900000000001 } }] }],
  ['exact', 'PUT', `${G}/sbs/challenge/900001`, {}],
  ['exact', 'GET', `${G}/store/purchasegroup/mypacks`],
  ['shape', 'POST', `${G}/purchased/items`, { packId: 100, useCredits: 0, usePreOrder: 1, currency: 'PREORDER' }],
  ['shape', 'GET', `${G}/purchased/items`],
  ['exact', 'POST', `${G}/purchased/items`, { useCredits: 1 }],
  ['shape', 'POST', `${G}/purchased/items`, { packId: 3, useCredits: 1, currency: 'COINS' }],
  ['exact', 'GET', `${G}/user/credits`],
  ['exact', 'DELETE', `${G}/item/900000000010`],
  ['exact', 'POST', '/ut/delete/game/fifa21/item', { itemId: [900000000011, 900000000012] }],
  ['exact', 'GET', `${G}/user`],
  ['exact', 'POST', `${G}/scmp/campaign/rewards/all`],
  ['exact', 'POST', `${G}/scmp/campaign/level/1/reward/0`],
  ['exact', 'GET', `${G}/scmp/campaign/active`],
];

const afterImport = [
  ['exact', 'GET', `${G}/user`],
  ['exact', 'GET', `${G}/userMassInfo?screen=futlivemsggen4`],
  ['exact', 'GET', `${G}/club?count=500`],
  ['exact', 'POST', `${G}/club`, { type: 'manager', start: 0, count: 91 }],
  ['exact', 'GET', `${G}/user/credits`],
  ['exact', 'GET', `${G}/store/purchasegroup/mypacks`],
  ['exact', 'GET', `${G}/clientdata/onboarding`],
  ['exact', 'GET', `${G}/clientdata/squad`],
  ['exact', 'GET', `${G}/clientdata/loanplayerreward`],
  ['exact', 'GET', `${G}/sbs/setId/900000/challenges`],
  ['exact', 'GET', `${G}/scmp/campaign/active`],
  ['exact', 'GET', `${G}/stadium`],
  ['shape', 'GET', `${G}/purchased/items`],
];

// Baseline mode only: flows the Python comparison never covered. Draft choices and player picks
// are random by design, so their responses are compared by shape.
const baselineOnly = [
  ['raw', 'GET', '/fut/packs/loc/storepackdescriptions.en_us.xml'],
  ['exact', 'GET', '/pow/store/game/fifa21/catalog/list'],
  ['exact', 'GET', '/pow/store/game/fifa21/catalog/0/item/list'],
  ['exact', 'GET', `${G}/phishing/trusteddevice`],
  ['exact', 'GET', `${G}/match/reset`],
  ['exact', 'GET', `${G}/featuredsquad/user/stats`],
  ['exact', 'GET', `${G}/squadbattles/legacy`],
  ['exact', 'GET', `${G}/champion/events/upcoming`],
  ['exact', 'POST', `${G}/champion/user/prize`],
  ['exact', 'GET', `${G}/champion/type/weekend/period/curr/friends/stats`],
  ['exact', 'GET', `${G}/scmp/world/campaign/active`],
  ['exact', 'GET', `${G}/scmp/learning/campaign/rewards/all`],
  ['exact', 'GET', `${G}/sbs/sets/tag`],
  ['exact', 'PUT', `${G}/stadium`, { stadiumId: 2 }],
  ['exact', 'GET', `${G}/stadium`],
  ['exact', 'PUT', `${G}/squad/1`, { squadName: 'Parity XI', formation: 'f433', players: [] }],
  ['exact', 'GET', `${G}/squad/active`],
  ['exact', 'POST', `${G}/purchase/mode/1/draft`, { currency: 'COINS' }],
  ['exact', 'GET', `${G}/user/credits`],
  ['shape', 'GET', `${G}/squad/mode/draft/state`],
  ['exact', 'PUT', `${G}/squad/mode/draft/choose/difficulty`, { difficultyName: 'PROFESSIONAL' }],
  ['shape', 'GET', `${G}/squad/mode/draft/choices/formation`],
  ['exact', 'PUT', `${G}/squad/mode/draft/choose`, { choiceIndex: 1 }],
  ['shape', 'GET', `${G}/squad/mode/draft/choices/captain`],
  ['exact', 'PUT', `${G}/squad/mode/draft/choose`, { choiceIndex: 0, positionId: 0 }],
  ['shape', 'PUT', `${G}/squad/mode/draft/choices/player`, { positionId: 1 }],
  ['shape', 'POST', `${G}/squad/mode/draft/autocomplete`],
  ['shape', 'GET', `${G}/squad/900001`],
  ['shape', 'POST', `${G}/match`],
  ['shape', 'GET', `${G}/offlinesquad/7`],
  ['shape', 'POST', `${G}/match/end`, { endReason: 'LOSS', secondsPlayed: 5400 }],
  ['exact', 'GET', `${G}/draft/mode/1/stats`],
  ['exact', 'POST', `${G}/squad/mode/draft/grant/award`],
  ['exact', 'GET', `${G}/user/credits`],
  ['exact', 'GET', `${G}/squad/mode/draft/state`],
  ['shape', 'POST', `${G}/purchased/items`, { packId: 21, useCredits: 1, currency: 'COINS' }],
  ['shape', 'GET', `${G}/playerpicks/pending`],
  ['shape', 'POST', `${G}/item/nontargeted`, { choiceIndex: 0 }],
  ['exact', 'GET', `${G}/user/credits`],
];

async function compareSequence(python, csharp, steps) {
  let failures = 0;
  for (const [mode, method, path, body] of steps) {
    const [left, right] = [await request(python.port, method, path, body), await request(csharp.port, method, path, body)];
    const label = `${method} ${path}`;
    const problems = [];
    if (left.status !== right.status) problems.push(`status ${left.status} != ${right.status}`);
    const leftType = (left.headers['content-type'] || '').split(';')[0];
    const rightType = (right.headers['content-type'] || '').split(';')[0];
    if (leftType !== rightType) problems.push(`content-type ${leftType} != ${rightType}`);
    for (const header of ['cache-control', 'x-localfut15-unknown', 'x-pow-sid', 'x-ut-sid', 'allow']) {
      if ((header in left.headers) !== (header in right.headers)) problems.push(`header ${header} presence differs`);
    }
    if (mode === 'exact') {
      if (left.json !== undefined || right.json !== undefined) {
        const [a, b] = [normalize(left.json), normalize(right.json)];
        problems.push(...diff(a, b));
        if (orderedKeys && !problems.length && JSON.stringify(a) !== JSON.stringify(b)) problems.push('JSON key order differs');
      } else if (!left.raw.equals(right.raw)) problems.push('raw body differs');
    } else if (mode === 'raw') {
      if (!left.raw.equals(right.raw)) problems.push('raw body differs');
    } else if (mode === 'shape') {
      problems.push(...diff(shape(left.json), shape(right.json), '$', [], true));
    }
    if (problems.length) {
      failures++;
      console.log(`DIFF ${label}`);
      for (const problem of problems.slice(0, 25)) console.log(`     ${problem}`);
    } else {
      console.log(`SAME ${label}`);
    }
  }
  return failures;
}

async function stop(server) {
  if (!server || server.stopped) return;
  if (server.child.exitCode === null) {
    const closed = new Promise((done) => server.child.once('close', done));
    server.child.kill();
    await closed;
  }
  if (server.databaseStopArgs) {
    await new Promise((done, fail) => {
      let output = '';
      const stopper = spawn(server.command, server.databaseStopArgs, {
        cwd: server.root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: server.childEnvironment,
      });
      stopper.stdout.on('data', (data) => { output = (output + data).slice(-4000); });
      stopper.stderr.on('data', (data) => { output = (output + data).slice(-4000); });
      stopper.on('error', fail);
      stopper.on('close', (code) => code === 0
        ? done()
        : fail(new Error(`${server.name} postgres stop exited ${code}: ${output}`)));
    });
  }
  server.stopped = true;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const here = fileURLToPath(new URL('.', import.meta.url));
  const pythonServer = join(here, '..', '..', '..', 'runtime-source', 'localfut21', 'server.py');
  const scratch = await mkdtemp(join(tmpdir(), 'Project21-parity-'));
  const serverCommand = (path) => path.endsWith('.dll')
    ? [options.dotnet, [path, 'serve'], [path, 'postgres', 'stop']]
    : [path, ['serve'], ['postgres', 'stop']];
  const [csharpCommand, csharpArgs, csharpStopArgs] = serverCommand(options.csharp);
  // The left side is the Python runtime, or in baseline mode an earlier C# build (labelled "python" below).
  const [leftCommand, leftArgs, leftStopArgs] = options.baseline
    ? serverCommand(options.baseline)
    : [options.python, ['-u', pythonServer], null];
  orderedKeys = Boolean(options.baseline);

  let python;
  let csharp;
  let failures = 0;
  const steps = options.baseline ? [...sequence, ...baselineOnly] : sequence;
  try {
    const started = await Promise.allSettled([
      startServer(options.baseline ? 'baseline' : 'python', leftCommand, leftArgs, join(scratch, 'python'), leftStopArgs),
      startServer('csharp', csharpCommand, csharpArgs, join(scratch, 'csharp'), csharpStopArgs),
    ]);
    if (started[0].status === 'fulfilled') python = started[0].value;
    if (started[1].status === 'fulfilled') csharp = started[1].value;
    const startupFailure = started.find((result) => result.status === 'rejected');
    if (startupFailure) throw startupFailure.reason;

    failures += await compareSequence(python, csharp, steps);

    await Promise.all([stop(python), stop(csharp)]);
    if (options.baseline && !options.legacyDb) {
      console.log('---- legacy import skipped (pass --legacy-db <localfut15.sqlite3> in baseline mode) ----');
      return finish(0);
    }
    console.log('---- legacy import: C# server started on a copy of the Python club database ----');
    const legacySource = options.legacyDb ?? join(scratch, 'python', 'data', 'localfut15.sqlite3');
    async function importProfile(name) {
      const root = join(scratch, name);
      await mkdir(join(root, 'data'), { recursive: true });
      for (const suffix of ['', '-wal', '-shm']) {
        try { await access(legacySource + suffix); } catch { continue; }
        await copyFile(legacySource + suffix, join(root, 'data', 'localfut15.sqlite3' + suffix));
      }
      return root;
    }
    const restarted = await Promise.allSettled([
      options.baseline
        ? importProfile('baseline-imported').then((root) => startServer('baseline-imported', leftCommand, leftArgs, root, leftStopArgs))
        : startServer('python', options.python, ['-u', pythonServer], join(scratch, 'python')),
      importProfile('csharp-imported').then((root) => startServer('csharp-imported', csharpCommand, csharpArgs, root, csharpStopArgs)),
    ]);
    if (restarted[0].status === 'fulfilled') python = restarted[0].value;
    if (restarted[1].status === 'fulfilled') csharp = restarted[1].value;
    const restartFailure = restarted.find((result) => result.status === 'rejected');
    if (restartFailure) throw restartFailure.reason;
    failures += await compareSequence(python, csharp, afterImport);
  } finally {
    await Promise.all([stop(python), stop(csharp)]);
    if (!options.keep) {
      await delay(500);
      await rm(scratch, { recursive: true, force: true }).catch(() => {});
    } else {
      console.log(`Profiles kept at ${scratch}`);
    }
  }
  function finish(importSteps) {
    const total = steps.length + importSteps;
    console.log(failures ? `RESULT ${failures}/${total} requests differ` : `RESULT all ${total} requests match`);
    process.exitCode = failures ? 1 : 0;
  }
  finish(options.baseline && !options.legacyDb ? 0 : afterImport.length);
}

main().catch((error) => {
  console.error(`FAIL ${error.stack || error.message}`);
  process.exitCode = 2;
});
