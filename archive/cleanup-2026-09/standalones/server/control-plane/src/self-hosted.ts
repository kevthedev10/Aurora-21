import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { Readable } from 'node:stream';
import { mkdir, readFile, readdir, rename, stat, unlink, writeFile } from 'node:fs/promises';
import { dirname, extname, join, resolve, sep } from 'node:path';
import Database from 'better-sqlite3';
import worker from './index.js';

type SqlValue = string | number | null | ArrayBuffer | Uint8Array;

class LocalPreparedStatement {
  private values: SqlValue[] = [];

  constructor(private readonly database: Database.Database, private readonly sql: string) {}

  bind(...values: SqlValue[]): LocalPreparedStatement {
    this.values = values;
    return this;
  }

  async first<T = Record<string, unknown>>(): Promise<T | null> {
    return (this.database.prepare(this.sql).get(...this.values) as T | undefined) ?? null;
  }

  async all<T = Record<string, unknown>>(): Promise<{ results: T[]; success: true; meta: Record<string, unknown> }> {
    const results = this.database.prepare(this.sql).all(...this.values) as T[];
    return { results, success: true, meta: {} };
  }

  async run(): Promise<{ success: true; meta: { changes: number; last_row_id: number | bigint } }> {
    return this.runSync();
  }

  runSync(): { success: true; meta: { changes: number; last_row_id: number | bigint } } {
    const result = this.database.prepare(this.sql).run(...this.values);
    return { success: true, meta: { changes: result.changes, last_row_id: result.lastInsertRowid } };
  }
}

class LocalD1Database {
  constructor(private readonly database: Database.Database) {}

  prepare(sql: string): LocalPreparedStatement {
    return new LocalPreparedStatement(this.database, sql);
  }

  async batch(statements: LocalPreparedStatement[]): Promise<Array<Awaited<ReturnType<LocalPreparedStatement['run']>>>> {
    const execute = this.database.transaction((items: LocalPreparedStatement[]) => {
      return items.map(statement => statement.runSync());
    });
    return execute(statements);
  }
}

class LocalMediaObject {
  readonly size: number;
  readonly body: Uint8Array;

  constructor(readonly key: string, body: Uint8Array, private readonly contentType: string) {
    this.body = body;
    this.size = body.byteLength;
  }

  writeHttpMetadata(headers: Headers): void {
    headers.set('content-type', this.contentType);
  }

  async arrayBuffer(): Promise<ArrayBuffer> {
    return this.body.slice().buffer;
  }

  async text(): Promise<string> {
    return new TextDecoder().decode(this.body);
  }

  async json<T>(): Promise<T> {
    return JSON.parse(await this.text()) as T;
  }
}

class LocalMediaBucket {
  constructor(private readonly root: string) {}

  private pathFor(key: string): string {
    const target = resolve(this.root, ...key.split('/'));
    const prefix = resolve(this.root) + sep;
    if (!target.startsWith(prefix)) throw new Error('Invalid media object key');
    return target;
  }

  async get(key: string): Promise<LocalMediaObject | null> {
    const path = this.pathFor(key);
    try {
      const info = await stat(path);
      if (!info.isFile()) return null;
      const data = await readFile(path);
      return new LocalMediaObject(key, data, contentTypeFor(path));
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
      throw error;
    }
  }

  async put(key: string, value: BodyInit | ReadableStream, options?: { httpMetadata?: { contentType?: string }; sha256?: string }): Promise<{ key: string; size: number }> {
    const path = this.pathFor(key);
    await mkdir(dirname(path), { recursive: true });
    const bytes = new Uint8Array(await new Response(value as BodyInit).arrayBuffer());
    if (options?.sha256) {
      const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
      const actual = Array.from(digest, byte => byte.toString(16).padStart(2, '0')).join('').toUpperCase();
      if (actual !== options.sha256.toUpperCase()) throw new Error('media checksum mismatch');
    }
    const temporary = `${path}.${crypto.randomUUID()}.tmp`;
    await writeFile(temporary, bytes);
    await rename(temporary, path);
    if (options?.httpMetadata?.contentType) {
      await writeFile(`${path}.content-type`, options.httpMetadata.contentType, 'utf8');
    }
    return { key, size: bytes.byteLength };
  }

  async delete(key: string): Promise<void> {
    const path = this.pathFor(key);
    await Promise.all([unlink(path).catch(ignoreMissing), unlink(`${path}.content-type`).catch(ignoreMissing)]);
  }
}

function ignoreMissing(error: unknown): void {
  if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
}

function contentTypeFor(path: string): string {
  switch (extname(path).toLowerCase()) {
    case '.json': return 'application/json; charset=utf-8';
    case '.mp3': return 'audio/mpeg';
    case '.ogg': return 'audio/ogg';
    case '.wav': return 'audio/wav';
    case '.mp4': return 'video/mp4';
    default: return 'application/octet-stream';
  }
}

function required(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`Missing required environment variable ${name}`);
  return value;
}

async function applyMigrations(database: Database.Database, directory: string): Promise<void> {
  database.exec('CREATE TABLE IF NOT EXISTS _project21_migrations (name TEXT PRIMARY KEY, applied_at INTEGER NOT NULL)');
  const applied = new Set(
    (database.prepare('SELECT name FROM _project21_migrations').all() as Array<{ name: string }>).map(row => row.name),
  );
  const files = (await readdir(directory)).filter(name => /^\d+.*\.sql$/i.test(name)).sort();
  for (const name of files) {
    if (applied.has(name)) continue;
    const sql = await readFile(join(directory, name), 'utf8');
    database.transaction(() => {
      database.exec(sql);
      database.prepare('INSERT INTO _project21_migrations(name,applied_at) VALUES(?,unixepoch())').run(name);
    })();
    console.log(JSON.stringify({ event: 'migration_applied', name }));
  }
}

function applyInitialSafetyMode(database: Database.Database): void {
  database.exec('CREATE TABLE IF NOT EXISTS _project21_selfhost_state (key TEXT PRIMARY KEY, value TEXT NOT NULL)');
  const applied = database.prepare("SELECT value FROM _project21_selfhost_state WHERE key='initial_owner_only_applied'").get();
  if (applied) return;
  database.transaction(() => {
    database.prepare(`UPDATE control_settings SET value='1',updated_by_discord_id='system',updated_at=unixepoch()
      WHERE key='owner_only_mode'`).run();
    database.prepare("INSERT INTO _project21_selfhost_state(key,value) VALUES('initial_owner_only_applied','1')").run();
  })();
  console.log(JSON.stringify({ event: 'initial_owner_only_mode_enabled' }));
}

async function readBody(request: IncomingMessage): Promise<Buffer | undefined> {
  if (request.method === 'GET' || request.method === 'HEAD') return undefined;
  const chunks: Buffer[] = [];
  for await (const chunk of request) chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  return Buffer.concat(chunks);
}

async function sendResponse(response: Response, target: ServerResponse): Promise<void> {
  target.statusCode = response.status;
  response.headers.forEach((value, key) => target.setHeader(key, value));
  if (!response.body) {
    target.end();
    return;
  }
  Readable.fromWeb(response.body as never).pipe(target);
}

const dataRoot = resolve(process.env.PROJECT21_CONTROL_DATA_DIR || '/var/lib/project21-control-plane');
const databasePath = resolve(process.env.PROJECT21_CONTROL_DATABASE || join(dataRoot, 'control-plane.sqlite3'));
const mediaRoot = resolve(process.env.PROJECT21_CONTROL_MEDIA_DIR || join(dataRoot, 'media'));
const migrationsDirectory = resolve(process.env.PROJECT21_CONTROL_MIGRATIONS_DIR || join(process.cwd(), 'migrations'));
await mkdir(dirname(databasePath), { recursive: true });
await mkdir(mediaRoot, { recursive: true });

const database = new Database(databasePath);
database.pragma('journal_mode = WAL');
database.pragma('foreign_keys = ON');
database.pragma('busy_timeout = 5000');
await applyMigrations(database, migrationsDirectory);
applyInitialSafetyMode(database);

const environment = {
  DB: new LocalD1Database(database),
  MEDIA: new LocalMediaBucket(mediaRoot),
  PUBLIC_BASE_URL: required('PUBLIC_BASE_URL').replace(/\/$/, ''),
  ADMIN_DISCORD_IDS: required('ADMIN_DISCORD_IDS'),
  LOCALFUT_SERVER_ADDRESS: required('LOCALFUT_SERVER_ADDRESS'),
  WIREGUARD_ENDPOINT: required('WIREGUARD_ENDPOINT'),
  WIREGUARD_SERVER_PUBLIC_KEY: required('WIREGUARD_SERVER_PUBLIC_KEY'),
  DISCORD_CLIENT_ID: required('DISCORD_CLIENT_ID'),
  DISCORD_CLIENT_SECRET: required('DISCORD_CLIENT_SECRET'),
  DISCORD_GUILD_ID: process.env.DISCORD_GUILD_ID || '',
  DISCORD_AUTO_APPROVE_ROLE_IDS: process.env.DISCORD_AUTO_APPROVE_ROLE_IDS || '',
  SESSION_SECRET: required('SESSION_SECRET'),
  HOST_AGENT_SECRET: required('HOST_AGENT_SECRET'),
};

const listenHost = process.env.PROJECT21_CONTROL_HOST || '127.0.0.1';
const listenPort = Number(process.env.PROJECT21_CONTROL_PORT || '8787');
if (!Number.isInteger(listenPort) || listenPort < 1 || listenPort > 65535) throw new Error('Invalid PROJECT21_CONTROL_PORT');

const server = createServer(async (incoming, outgoing) => {
  try {
    const url = new URL(incoming.url || '/', environment.PUBLIC_BASE_URL);
    if (incoming.method === 'GET' && url.pathname === '/healthz') {
      outgoing.writeHead(200, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
      outgoing.end(JSON.stringify({ ok: true }));
      return;
    }
    const body = await readBody(incoming);
    const headers = new Headers();
    for (let index = 0; index < incoming.rawHeaders.length; index += 2) {
      headers.append(incoming.rawHeaders[index], incoming.rawHeaders[index + 1]);
    }
    const request = new Request(url, {
      method: incoming.method,
      headers,
      body: body as unknown as BodyInit,
    });
    const response = await worker.fetch(request, environment as never);
    await sendResponse(response, outgoing);
  } catch (error) {
    console.error(JSON.stringify({ event: 'request_error', error: error instanceof Error ? error.stack : String(error) }));
    if (!outgoing.headersSent) outgoing.writeHead(500, { 'content-type': 'application/json; charset=utf-8' });
    outgoing.end(JSON.stringify({ error: 'internal_error' }));
  }
});

server.listen(listenPort, listenHost, () => {
  console.log(JSON.stringify({ event: 'control_plane_listening', host: listenHost, port: listenPort, databasePath, mediaRoot }));
});

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.on(signal, () => {
    server.close(() => {
      database.close();
      process.exit(0);
    });
  });
}
