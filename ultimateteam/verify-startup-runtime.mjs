#!/usr/bin/env node
// Runs the built UltimateTeam server executable, never the live launcher.
// All requests use a child-owned, attested loopback test listener and fixture state.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile, access } from 'node:fs/promises';
import http from 'node:http';
import net from 'node:net';
import { tmpdir } from 'node:os';
import { isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const args = process.argv.slice(2);
assert(args.length === 2 && args[0] === '--server' && isAbsolute(args[1]),
  'Usage: node ultimateteam/verify-startup-runtime.mjs --server <absolute Aurora21-UltimateTeam-Server executable>');
const server = args[1];
const dataRoot = fileURLToPath(new URL('./runtime-source/ultimateteam21', import.meta.url));
const buildHash = createHash('sha256').update(await readFile(server)).digest('hex');
const scratch = await mkdtemp(join(tmpdir(), 'Aurora21-startup-test-'));
const clientIds = ['ultimateteam21-blaze-server', 'ultimateteam21-blaze-sdk', 'ultimateteam21-ssf'];
const clubIds = new Map();
let requests = 0;

function request(port, path, body) {
  requests++;
  return new Promise((done, fail) => {
    const outgoing = http.request({ hostname: '127.0.0.1', family: 4, port, path, agent: false,
      method: body === undefined ? 'GET' : 'POST', headers: body === undefined ? {} : {
        'Content-Type': 'application/x-www-form-urlencoded', 'Content-Length': Buffer.byteLength(body),
      } }, (response) => {
      let data = '';
      response.on('data', (part) => { data += part; if (data.length > 65536) outgoing.destroy(new Error('Oversized test response')); });
      response.on('error', fail);
      response.on('end', () => {
        try { done({ status: response.statusCode, headers: response.headers, json: JSON.parse(data) }); }
        catch (error) { fail(error); }
      });
    });
    outgoing.setTimeout(2000, () => outgoing.destroy(new Error('Test HTTP timeout')));
    outgoing.on('error', fail);
    outgoing.end(body);
  });
}

async function freePort() {
  const socket = net.createServer();
  await new Promise((done, fail) => { socket.once('error', fail); socket.listen(0, '127.0.0.1', done); });
  const port = socket.address().port;
  await new Promise((done) => socket.close(done));
  assert(![8090, 8081, 42230, 42231, 42232, 44321, 10853, 17502, 3216].includes(port));
  return port;
}

async function checkProfile(name, expectedUserId, expectedPersonaId, custom = false) {
  const root = join(scratch, name);
  const configRoot = join(root, 'config');
  const configFile = join(configRoot, 'config.json');
  await mkdir(configRoot, { recursive: true });
  if (custom) await writeFile(configFile, JSON.stringify({ nucleus_id: expectedUserId,
    persona_id: expectedPersonaId, display_name: 'Portability Fixture', custom_setting: 'preserve-me' }));
  const before = custom ? await readFile(configFile, 'utf8') : null;
  const packFile = join(configRoot, 'pack_settings.json');
  const packPreferences = JSON.stringify({ packs: { 70: { enabled: false, name: 'Synthetic stale file override' } } });
  if (custom) await writeFile(packFile, packPreferences);
  const port = await freePort();
  let stderr = '';
  let childError;
  const child = spawn(server, ['serve', '--http-only-test-port', String(port), '--lsx-mode', 'off', '--data-root', dataRoot], {
    cwd: scratch, windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'],
    env: { ...process.env, ULTIMATETEAM_RUNTIME_ROOT: root, ULTIMATETEAM21_USER_CONFIG_ROOT: configRoot },
  });
  child.stderr.on('data', (data) => { stderr = (stderr + data).slice(-4000); });
  child.on('error', (error) => { childError = error; });
  const closed = new Promise((done) => child.once('close', done));
  try {
    let status;
    // The first start of a profile also creates its PostgreSQL cluster.
    const readyDeadline = Date.now() + 90000;
    while (Date.now() < readyDeadline) {
      if (childError || child.exitCode !== null) throw new Error(`Isolated server failed: ${childError || stderr}`);
      try { status = await request(port, '/ultimateteam/test/status'); break; } catch { await delay(100); }
    }
    assert.equal(status?.status, 200, 'Isolated server did not become ready');
    assert.equal(status.json.testMode, true);
    assert.equal(resolve(status.json.runtimeRoot), resolve(root), 'Listener belongs to a different runtime');
    assert.equal(status.json.database?.engine, 'postgresql');
    assert.equal(status.json.database.backend, 'relational', 'Startup selected the wrong storage backend');
    assert.equal(status.json.database.mode, 'managed', 'Test profile must use its own managed cluster');
    assert.match(status.json.database.server, /^127\.0\.0\.1:\d+$/);
    assert.equal(resolve(status.json.database.dataDirectory), resolve(join(root, 'postgres', 'data-18')), 'Club database is outside the isolated runtime root');
    if (clubIds.has(name)) assert.equal(status.json.clubId, clubIds.get(name), 'Club was not preserved across a restart');
    clubIds.set(name, status.json.clubId);
    const profile = await request(port, '/ultimateteam/auth/bootstrap');
    assert.equal(profile.status, 200);
    assert.equal(profile.json.serverImplementation, 'csharp');
    assert.equal(profile.json.serverBuildSha256, buildHash, 'Listener is a different server build');
    assert.deepEqual(profile.json.clientIds, clientIds);
    assert.equal(profile.json.authCode, 'LOCAL-FUT21-AUTH-CODE');
    assert.equal(profile.json.config.nucleusConnect, 'http://127.0.0.1:8090/');
    assert.equal(profile.json.config.identityRedirectUri, 'http://127.0.0.1:8090/ultimateteam/callback');
    assert.match(profile.headers['cache-control'], /no-store/);
    for (const client_id of clientIds) {
      const form = new URLSearchParams({ grant_type: 'authorization_code', client_id, code: 'LOCAL-FUT21-AUTH-CODE' });
      const token = await request(port, '/connect/token', form.toString());
      assert.equal(token.status, 200);
      assert.equal(token.json.access_token, 'LOCAL-FUT21-ACCESS');
      const info = await request(port, '/connect/tokeninfo?' + new URLSearchParams({ access_token: token.json.access_token, client_id }));
      assert.equal(info.status, 200);
      assert.equal(info.json.user_id, String(expectedUserId));
      assert.equal(info.json.persona_id, String(expectedPersonaId));
    }
    const invalid = await request(port, '/connect/token', 'grant_type=authorization_code&client_id=not-local&code=LOCAL-FUT21-AUTH-CODE');
    assert.equal(invalid.status, 400);
    assert.deepEqual(invalid.json, { error: 'invalid_client' });
    const after = await readFile(configFile, 'utf8');
    if (custom) assert.equal(after, before, 'Existing preferences were overwritten');
    else assert.equal(JSON.parse(after).nucleus_id, expectedUserId, 'Portable default not created');
    // Migration 041 makes PostgreSQL authoritative for the relational store. An existing
    // legacy preference file is preserved, and cannot disable or rename a database pack.
    const store = await request(port, '/ut/game/fifa21/store/purchasegroup/all');
    assert.equal(store.status, 200);
    assert(Array.isArray(store.json.purchase) && store.json.purchase.length > 0, 'No store tiles returned');
    const bronze = store.json.purchase.find(tile => tile.id === 70);
    assert.equal(bronze?.name, 'Bronze Pack', 'Store did not use the seeded database pack');
    assert.equal(bronze.price, 400);
    assert.equal(bronze.packContentInfo.itemQuantity, 12);
    if (custom) assert.equal(await readFile(packFile, 'utf8'), packPreferences, 'Legacy pack preferences overwritten');
    else await assert.rejects(access(packFile), { code: 'ENOENT' });
    console.log(`PASS ${name}: isolated startup, per-user config, three local auth clients and rejection cases`);
  } finally {
    if (child.exitCode === null && !childError) child.kill();
    await closed;
    // A killed Windows child cannot stop its cluster; stop this profile's cluster explicitly.
    await stopCluster(root, configRoot);
  }
}

function stopCluster(root, configRoot) {
  return new Promise((done, fail) => {
    const stopper = spawn(server, ['postgres', 'stop', '--data-root', dataRoot], {
      cwd: scratch, windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'],
      env: { ...process.env, ULTIMATETEAM_RUNTIME_ROOT: root, ULTIMATETEAM21_USER_CONFIG_ROOT: configRoot },
    });
    let output = '';
    stopper.stderr.on('data', (data) => { output = (output + data).slice(-2000); });
    stopper.on('error', fail);
    stopper.on('close', (code) => code === 0 ? done() : fail(new Error(`postgres stop failed (${code}): ${output}`)));
  });
}

try {
  await checkProfile('fresh-profile', 1000000001, 1000000002);
  await checkProfile('custom-profile', 9000000101, 9000000102, true);
  await checkProfile('fresh-profile', 1000000001, 1000000002);
  console.log(`PASS startup runtime smoke test (${requests} loopback requests; fixture state retained at ${scratch})`);
} catch (error) {
  console.error(`FAIL ${error.message}\nFixture state: ${scratch}`);
  process.exitCode = 1;
}
