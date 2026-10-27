import rawCardCatalog from './card-catalog.json';

interface Env {
  DB: D1Database;
  PUBLIC_BASE_URL: string;
  ADMIN_DISCORD_IDS: string;
  LOCALFUT_SERVER_ADDRESS: string;
  WIREGUARD_ENDPOINT: string;
  WIREGUARD_SERVER_PUBLIC_KEY: string;
  DISCORD_CLIENT_ID: string;
  DISCORD_CLIENT_SECRET: string;
  SESSION_SECRET: string;
  HOST_AGENT_SECRET: string;
}

interface DiscordUser {
  id: string;
  username: string;
  global_name?: string | null;
  avatar?: string | null;
}

interface AccessClaims {
  typ: 'device';
  sub: string;
  did: string;
  exp: number;
}

interface CardCatalogEntry {
  resourceId: number;
  name: string;
  rating: number;
  position: string;
  rarity: string;
  team: string;
  searchText: string;
}

const encoder = new TextEncoder();
const jsonHeaders = { 'content-type': 'application/json; charset=utf-8' };
const cardCatalog: ReadonlyArray<CardCatalogEntry> = rawCardCatalog.map(row => {
  const resourceId = Number(row[0]);
  const name = String(row[1]);
  const rating = Number(row[2]);
  const position = String(row[3]);
  const rarity = String(row[4]);
  const team = String(row[5]);
  return {
    resourceId,
    name,
    rating,
    position,
    rarity,
    team,
    searchText: normalizeSearch(`${name} ${team} ${rarity} ${resourceId}`),
  };
});
const cardResourceIds = new Set(cardCatalog.map(card => card.resourceId));

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      return withSecurityHeaders(await route(request, env));
    } catch (error) {
      console.error('request failed', error);
      return withSecurityHeaders(json({ error: 'internal_error' }, 500));
    }
  },
} satisfies ExportedHandler<Env>;

async function route(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  if (request.method === 'GET' && url.pathname === '/') return home(request, env);
  if (request.method === 'GET' && url.pathname === '/login') return beginLogin(url, env);
  if (request.method === 'GET' && url.pathname === '/oauth/callback') return finishLogin(url, env);
  if (request.method === 'GET' && url.pathname === '/activate') return activateDevice(request, url, env);
  if (request.method === 'POST' && url.pathname === '/api/device/start') return startDevice(request, env);
  if (request.method === 'POST' && url.pathname === '/api/device/poll') return pollDevice(request, env);
  if (request.method === 'GET' && url.pathname === '/api/device/config') return deviceConfig(request, env);
  if (request.method === 'POST' && url.pathname === '/api/device/wireguard') return wireguardProvision(request, env);
  if (request.method === 'POST' && url.pathname === '/api/host/validate') return hostValidate(request, env);
  if (request.method === 'GET' && url.pathname === '/api/host/wireguard-peers') return hostWireGuardPeers(request, env);
  if (request.method === 'GET' && url.pathname === '/api/host/commands') return hostCommands(request, env);
  const resultMatch = url.pathname.match(/^\/api\/host\/commands\/(\d+)\/result$/);
  if (request.method === 'POST' && resultMatch) return hostCommandResult(request, env, Number(resultMatch[1]));
  if (request.method === 'GET' && url.pathname === '/api/admin/users') return adminUsers(request, env);
  if (request.method === 'GET' && url.pathname === '/api/admin/cards') return adminCards(request, url, env);
  if (request.method === 'GET' && url.pathname === '/api/admin/commands') return adminCommands(request, env);
  if (request.method === 'POST' && url.pathname === '/api/admin/command') return adminCommand(request, env);
  if (request.method === 'POST' && url.pathname === '/api/admin/approve') return adminApprove(request, env);
  if (request.method === 'POST' && url.pathname === '/api/admin/ban') return adminBan(request, env, true);
  if (request.method === 'POST' && url.pathname === '/api/admin/unban') return adminBan(request, env, false);
  if (request.method === 'POST' && url.pathname === '/logout') return logout(request, env);
  return json({ error: 'not_found' }, 404);
}

async function home(request: Request, env: Env): Promise<Response> {
  const user = await sessionUser(request, env);
  if (!user) return html(landingHtml());
  if (Number(user.banned)) return html(messageHtml('Access revoked', 'This Discord account is banned.'), 403);
  const isAdmin = isAdminDiscordId(env, String(user.discord_id));
  if (!Number(user.approved) && !isAdmin) return html(messageHtml('Approval pending', 'Your Discord identity is verified. Ask the Project 21 host to approve access.'));
  if (!isAdmin) return html(messageHtml('Project 21 connected', `Signed in as ${escapeHtml(String(user.username))}. You may return to the launcher.`));
  return html(adminHtml(String(user.username)));
}

async function beginLogin(url: URL, env: Env): Promise<Response> {
  assertConfigured(env, ['PUBLIC_BASE_URL', 'DISCORD_CLIENT_ID', 'DISCORD_CLIENT_SECRET']);
  const returnTo = safeReturnPath(url.searchParams.get('return'));
  const state = randomToken(32);
  await env.DB.prepare('INSERT INTO oauth_states(state_hash,return_to,expires_at) VALUES(?,?,?)')
    .bind(await sha256(state), returnTo, now() + 600).run();
  const target = new URL('https://discord.com/oauth2/authorize');
  target.searchParams.set('client_id', env.DISCORD_CLIENT_ID);
  target.searchParams.set('response_type', 'code');
  target.searchParams.set('redirect_uri', `${trimSlash(env.PUBLIC_BASE_URL)}/oauth/callback`);
  target.searchParams.set('scope', 'identify');
  target.searchParams.set('state', state);
  return Response.redirect(target.toString(), 302);
}

async function finishLogin(url: URL, env: Env): Promise<Response> {
  const code = url.searchParams.get('code') ?? '';
  const state = url.searchParams.get('state') ?? '';
  const stateHash = await sha256(state);
  const saved = await env.DB.prepare('SELECT return_to FROM oauth_states WHERE state_hash=? AND expires_at>?')
    .bind(stateHash, now()).first<{ return_to: string }>();
  await env.DB.prepare('DELETE FROM oauth_states WHERE state_hash=?').bind(stateHash).run();
  if (!code || !state || !saved) return html(messageHtml('Login expired', 'Return to the launcher and try again.'), 400);

  const tokenResponse = await fetch('https://discord.com/api/v10/oauth2/token', {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      client_id: env.DISCORD_CLIENT_ID,
      client_secret: env.DISCORD_CLIENT_SECRET,
      grant_type: 'authorization_code',
      code,
      redirect_uri: `${trimSlash(env.PUBLIC_BASE_URL)}/oauth/callback`,
    }),
  });
  if (!tokenResponse.ok) return html(messageHtml('Discord login failed', 'Return to the launcher and retry.'), 502);
  const token = await tokenResponse.json<{ access_token: string }>();
  const userResponse = await fetch('https://discord.com/api/v10/users/@me', {
    headers: { authorization: `Bearer ${token.access_token}` },
  });
  if (!userResponse.ok) return html(messageHtml('Discord profile unavailable', 'Return to the launcher and retry.'), 502);
  const discord = await userResponse.json<DiscordUser>();
  if (!/^\d{5,24}$/.test(discord.id)) return html(messageHtml('Invalid Discord identity', 'Discord returned an invalid account identifier.'), 400);
  const role = isAdminDiscordId(env, discord.id) ? 'admin' : 'player';
  const displayName = (discord.global_name || discord.username).slice(0, 80);
  await env.DB.prepare(`INSERT INTO users(discord_id,username,avatar,role,approved,created_at,last_seen_at)
    VALUES(?,?,?,?,?,?,?) ON CONFLICT(discord_id) DO UPDATE SET username=excluded.username,avatar=excluded.avatar,
    role=excluded.role,approved=CASE WHEN excluded.role='admin' THEN 1 ELSE users.approved END,last_seen_at=excluded.last_seen_at`)
    .bind(discord.id, displayName, discord.avatar ?? null, role, role === 'admin' ? 1 : 0, now(), now()).run();
  const user = await env.DB.prepare('SELECT banned FROM users WHERE discord_id=?').bind(discord.id).first<{ banned: number }>();
  if (Number(user?.banned)) return html(messageHtml('Access revoked', 'This Discord account is banned.'), 403);
  const session = randomToken(32);
  await env.DB.prepare('INSERT INTO sessions(session_hash,discord_id,expires_at) VALUES(?,?,?)')
    .bind(await sha256(session), discord.id, now() + 86400).run();
  return new Response(null, {
    status: 302,
    headers: {
      location: saved.return_to,
      'set-cookie': `p21_session=${session}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=86400`,
    },
  });
}

async function activateDevice(request: Request, url: URL, env: Env): Promise<Response> {
  const code = normalizeCode(url.searchParams.get('code') ?? '');
  if (!code) return html(messageHtml('Invalid code', 'Return to the launcher and try again.'), 400);
  const flow = await env.DB.prepare('SELECT id,device_name,expires_at,discord_id FROM device_flows WHERE user_code=?')
    .bind(code).first<{ id: string; device_name: string; expires_at: number; discord_id: string | null }>();
  if (!flow || flow.expires_at <= now()) return html(messageHtml('Code expired', 'Return to the launcher and request a new code.'), 400);
  const user = await sessionUser(request, env);
  if (!user) return Response.redirect(`${trimSlash(env.PUBLIC_BASE_URL)}/login?return=${encodeURIComponent(`/activate?code=${code}`)}`, 302);
  if (Number(user.banned)) return html(messageHtml('Access revoked', 'This Discord account is banned.'), 403);
  if (flow.discord_id && flow.discord_id !== user.discord_id) return html(messageHtml('Already claimed', 'This device code belongs to another Discord account.'), 409);
  await env.DB.prepare('UPDATE device_flows SET discord_id=?,approved_at=? WHERE id=?')
    .bind(user.discord_id, now(), flow.id).run();
  if (!Number(user.approved) && user.role !== 'admin') {
    await audit(env, String(user.discord_id), String(user.discord_id), 'access_requested', JSON.stringify({ deviceName: flow.device_name }));
    return html(messageHtml('Approval requested', `Signed in as ${escapeHtml(String(user.username))}. Ask the Project 21 host to approve your account; the launcher will continue waiting.`));
  }
  await audit(env, String(user.discord_id), String(user.discord_id), 'device_approved', JSON.stringify({ deviceName: flow.device_name }));
  return html(messageHtml('Launcher approved', `Signed in as ${escapeHtml(String(user.username))}. You may close this page.`));
}

async function startDevice(request: Request, env: Env): Promise<Response> {
  const body = await readJson<{ deviceName?: string }>(request);
  const deviceName = String(body.deviceName ?? 'Windows PC').replace(/[\r\n]/g, ' ').slice(0, 80);
  const id = crypto.randomUUID();
  const deviceId = crypto.randomUUID();
  const pollSecret = randomToken(32);
  let code = userCode();
  for (let attempt = 0; attempt < 5; attempt += 1) {
    try {
      await env.DB.prepare(`INSERT INTO device_flows(id,user_code,poll_hash,device_id,device_name,expires_at)
        VALUES(?,?,?,?,?,?)`).bind(id, code, await sha256(pollSecret), deviceId, deviceName, now() + 600).run();
      return json({
        flowId: id,
        pollSecret,
        userCode: code,
        verificationUri: `${trimSlash(env.PUBLIC_BASE_URL)}/activate`,
        verificationUriComplete: `${trimSlash(env.PUBLIC_BASE_URL)}/activate?code=${code}`,
        expiresIn: 600,
        interval: 3,
      });
    } catch {
      code = userCode();
    }
  }
  return json({ error: 'code_generation_failed' }, 503);
}

async function pollDevice(request: Request, env: Env): Promise<Response> {
  const body = await readJson<{ flowId?: string; pollSecret?: string }>(request);
  const flow = await env.DB.prepare(`SELECT id,poll_hash,discord_id,device_id,device_name,expires_at
    FROM device_flows WHERE id=?`).bind(String(body.flowId ?? '')).first<{
      id: string; poll_hash: string; discord_id: string | null; device_id: string; device_name: string; expires_at: number;
    }>();
  if (!flow || !await hashMatches(String(body.pollSecret ?? ''), flow.poll_hash)) return json({ error: 'invalid_flow' }, 401);
  if (flow.expires_at <= now()) return json({ status: 'expired' }, 410);
  if (!flow.discord_id) return json({ status: 'pending' }, 202);
  const user = await env.DB.prepare('SELECT banned,approved,role FROM users WHERE discord_id=?').bind(flow.discord_id).first<{ banned: number; approved: number; role: string }>();
  if (!user || Number(user.banned)) return json({ status: 'denied' }, 403);
  if (!Number(user.approved) && user.role !== 'admin') return json({ status: 'pending_approval' }, 202);
  await env.DB.prepare(`INSERT INTO devices(id,discord_id,name,created_at,last_seen_at) VALUES(?,?,?,?,?)
    ON CONFLICT(id) DO UPDATE SET last_seen_at=excluded.last_seen_at,name=excluded.name`)
    .bind(flow.device_id, flow.discord_id, flow.device_name, now(), now()).run();
  const accessToken = await signAccessToken({ typ: 'device', sub: flow.discord_id, did: flow.device_id, exp: now() + 15552000 }, env);
  return json({ status: 'approved', accessToken });
}

async function deviceConfig(request: Request, env: Env): Promise<Response> {
  const auth = await authenticatedDevice(request, env);
  if (auth instanceof Response) return auth;
  assertConfigured(env, ['LOCALFUT_SERVER_ADDRESS']);
  await env.DB.prepare('UPDATE devices SET last_seen_at=? WHERE id=?').bind(now(), auth.claims.did).run();
  return json({
    discordId: auth.claims.sub,
    username: auth.user.username,
    deviceId: auth.claims.did,
    serverAddress: env.LOCALFUT_SERVER_ADDRESS,
  }, 200, { 'cache-control': 'no-store' });
}

async function wireguardProvision(request: Request, env: Env): Promise<Response> {
  const auth = await authenticatedDevice(request, env);
  if (auth instanceof Response) return auth;
  assertConfigured(env, ['LOCALFUT_SERVER_ADDRESS', 'WIREGUARD_ENDPOINT', 'WIREGUARD_SERVER_PUBLIC_KEY']);
  const body = await readJson<{ publicKey?: string }>(request);
  const publicKey = String(body.publicKey ?? '').trim();
  if (!validWireGuardPublicKey(publicKey)) return json({ error: 'invalid_wireguard_public_key' }, 400);
  if (!validWireGuardEndpoint(env.WIREGUARD_ENDPOINT)) return json({ error: 'invalid_wireguard_endpoint' }, 500);
  if (!validWireGuardPublicKey(env.WIREGUARD_SERVER_PUBLIC_KEY)) return json({ error: 'invalid_wireguard_server_key' }, 500);

  const current = await env.DB.prepare('SELECT wireguard_public_key,wireguard_address,wireguard_changed_at FROM devices WHERE id=?')
    .bind(auth.claims.did).first<{
      wireguard_public_key: string | null;
      wireguard_address: string | null;
      wireguard_changed_at: number | null;
    }>();
  if (!current) return json({ error: 'unknown_device' }, 404);
  if (current.wireguard_public_key && current.wireguard_public_key !== publicKey &&
      Number(current.wireguard_changed_at ?? 0) > now() - 300) {
    return json({ error: 'wireguard_key_change_rate_limited' }, 429);
  }

  let address = current.wireguard_address;
  const keyOwner = await env.DB.prepare(`SELECT id,discord_id,wireguard_address FROM devices
    WHERE wireguard_public_key=? AND id<>?`).bind(publicKey, auth.claims.did)
    .first<{ id: string; discord_id: string; wireguard_address: string | null }>();
  if (keyOwner && keyOwner.discord_id !== auth.claims.sub) return json({ error: 'wireguard_key_already_registered' }, 409);
  if (keyOwner) {
    address = address ?? keyOwner.wireguard_address;
    await env.DB.prepare('UPDATE devices SET wireguard_public_key=NULL,wireguard_address=NULL WHERE id=?')
      .bind(keyOwner.id).run();
  }
  let saved = false;
  for (let attempt = 0; attempt < 5 && !saved; attempt += 1) {
    if (!address) address = await allocateWireGuardAddress(env, auth.claims.did);
    try {
      const changedAt = current.wireguard_public_key === publicKey
        ? current.wireguard_changed_at
        : now();
      await env.DB.prepare(`UPDATE devices SET wireguard_public_key=?,wireguard_address=?,wireguard_changed_at=?,last_seen_at=? WHERE id=?`)
        .bind(publicKey, address, changedAt, now(), auth.claims.did).run();
      saved = true;
    } catch (error) {
      if (current.wireguard_address || attempt === 4) throw error;
      address = null; // Another concurrent enrollment won this address; select again.
    }
  }
  if (!address || !saved) throw new Error('WireGuard address allocation failed');
  const changed = current.wireguard_public_key !== publicKey || current.wireguard_address !== address;
  if (changed) {
    const payload = JSON.stringify({ deviceId: auth.claims.did, publicKey, address });
    await env.DB.prepare(`INSERT INTO commands(actor_discord_id,target_discord_id,action,payload,created_at)
      VALUES(?,?,?,?,?)`).bind(auth.claims.sub, auth.claims.sub, 'wireguard_add_peer', payload, now()).run();
    await audit(env, auth.claims.sub, auth.claims.sub, 'wireguard_peer_registered', payload);
  }
  return json({
    clientAddress: address,
    serverAddress: env.LOCALFUT_SERVER_ADDRESS,
    serverPublicKey: env.WIREGUARD_SERVER_PUBLIC_KEY,
    endpoint: env.WIREGUARD_ENDPOINT,
    // FIFA's peer-to-peer match traffic uses the other player's Project 21
    // address. Route only the private Project 21 WireGuard pool, not the
    // player's ordinary internet traffic.
    allowedIps: '10.203.20.0/22',
  }, 200, { 'cache-control': 'no-store' });
}

async function hostValidate(request: Request, env: Env): Promise<Response> {
  if (!await hostAuthorized(request, env)) return json({ error: 'unauthorized' }, 401);
  const body = await readJson<{ accessToken?: string }>(request);
  const synthetic = new Request(request.url, { headers: { authorization: `Bearer ${String(body.accessToken ?? '')}` } });
  const auth = await authenticatedDevice(synthetic, env);
  if (auth instanceof Response) return auth;
  return json({ allowed: true, discordId: auth.claims.sub, deviceId: auth.claims.did, username: auth.user.username });
}

async function hostWireGuardPeers(request: Request, env: Env): Promise<Response> {
  if (!await hostAuthorized(request, env)) return json({ error: 'unauthorized' }, 401);
  const rows = await env.DB.prepare(`SELECT d.id AS deviceId,d.discord_id AS discordId,
    d.wireguard_public_key AS publicKey,d.wireguard_address AS address
    FROM devices d JOIN users u ON u.discord_id=d.discord_id
    WHERE d.revoked=0 AND u.banned=0 AND (u.approved=1 OR u.role='admin')
      AND d.wireguard_public_key IS NOT NULL AND d.wireguard_address IS NOT NULL
    ORDER BY d.id LIMIT 1100`).all();
  return json({ peers: rows.results }, 200, { 'cache-control': 'no-store' });
}

async function hostCommands(request: Request, env: Env): Promise<Response> {
  if (!await hostAuthorized(request, env)) return json({ error: 'unauthorized' }, 401);
  const rows = await env.DB.prepare(`SELECT id,target_discord_id,action,payload,created_at FROM commands
    WHERE status='pending' OR (status='running' AND claimed_at<?) ORDER BY id LIMIT 20`).bind(now() - 120).all();
  const commands = rows.results;
  if (commands.length) {
    const ids = commands.map(row => Number(row.id));
    await env.DB.prepare(`UPDATE commands SET status='running',claimed_at=? WHERE id IN (${ids.map(() => '?').join(',')})`)
      .bind(now(), ...ids).run();
  }
  return json({ commands });
}

async function hostCommandResult(request: Request, env: Env, id: number): Promise<Response> {
  if (!await hostAuthorized(request, env)) return json({ error: 'unauthorized' }, 401);
  const body = await readJson<{ success?: boolean; result?: string }>(request);
  await env.DB.prepare(`UPDATE commands SET status=?,result=?,completed_at=? WHERE id=? AND status='running'`)
    .bind(body.success ? 'succeeded' : 'failed', String(body.result ?? '').slice(0, 4000), now(), id).run();
  return json({ ok: true });
}

async function adminUsers(request: Request, env: Env): Promise<Response> {
  const admin = await requireAdmin(request, env);
  if (admin instanceof Response) return admin;
  const rows = await env.DB.prepare(`SELECT u.discord_id,u.username,u.role,u.approved,u.banned,u.ban_reason,u.last_seen_at,
    COUNT(d.id) AS device_count FROM users u LEFT JOIN devices d ON d.discord_id=u.discord_id AND d.revoked=0
    GROUP BY u.discord_id ORDER BY u.last_seen_at DESC LIMIT 500`).all();
  return json({ users: rows.results });
}

async function adminCards(request: Request, url: URL, env: Env): Promise<Response> {
  const admin = await requireAdmin(request, env);
  if (admin instanceof Response) return admin;
  const query = normalizeSearch((url.searchParams.get('q') ?? '').slice(0, 100));
  if (query && query.length < 2 && !/^\d+$/.test(query)) {
    return json({ cards: [] }, 200, { 'cache-control': 'private, no-store' });
  }
  const terms = query.split(/\s+/).filter(Boolean);
  const cards = (terms.length
    ? cardCatalog.filter(card => terms.every(term => card.searchText.includes(term)))
    : cardCatalog
  ).slice(0, 75).map(({ searchText: _searchText, ...card }) => card);
  return json({ cards }, 200, { 'cache-control': 'private, no-store' });
}

async function adminApprove(request: Request, env: Env): Promise<Response> {
  const admin = await requireAdmin(request, env);
  if (admin instanceof Response) return admin;
  const body = await readJson<{ discordId?: string; approved?: boolean }>(request);
  const target = String(body.discordId ?? '');
  if (!/^\d{5,24}$/.test(target)) return json({ error: 'invalid_discord_id' }, 400);
  const approved = body.approved !== false;
  const result = await env.DB.prepare('UPDATE users SET approved=? WHERE discord_id=?')
    .bind(approved ? 1 : 0, target).run();
  if (!result.meta.changes) return json({ error: 'unknown_user' }, 404);
  if (!approved) {
    await env.DB.batch([
      env.DB.prepare('DELETE FROM sessions WHERE discord_id=?').bind(target),
      env.DB.prepare('UPDATE devices SET revoked=1 WHERE discord_id=?').bind(target),
      env.DB.prepare(`INSERT INTO commands(actor_discord_id,target_discord_id,action,payload,created_at)
        VALUES(?,?,?,?,?)`).bind(admin.discord_id, target, 'revoke_user', '{}', now()),
    ]);
  }
  await audit(env, String(admin.discord_id), target, approved ? 'approve_user' : 'remove_approval', '{}');
  return json({ ok: true });
}

async function adminCommands(request: Request, env: Env): Promise<Response> {
  const admin = await requireAdmin(request, env);
  if (admin instanceof Response) return admin;
  const rows = await env.DB.prepare('SELECT * FROM commands ORDER BY id DESC LIMIT 100').all();
  return json({ commands: rows.results });
}

async function adminCommand(request: Request, env: Env): Promise<Response> {
  const admin = await requireAdmin(request, env);
  if (admin instanceof Response) return admin;
  const body = await readJson<{ targetDiscordId?: string; action?: string; amount?: number; packId?: number; count?: number; resourceId?: number }>(request);
  const action = String(body.action ?? '');
  const allowed = new Set(['add_coins', 'grant_pack', 'grant_card', 'kick_user']);
  if (!allowed.has(action)) return json({ error: 'invalid_action' }, 400);
  const target = String(body.targetDiscordId ?? '');
  if (!/^\d{5,24}$/.test(target)) return json({ error: 'invalid_discord_id' }, 400);
  const targetUser = await env.DB.prepare('SELECT banned FROM users WHERE discord_id=?').bind(target).first<{ banned: number }>();
  if (!targetUser) return json({ error: 'unknown_user' }, 404);
  if (Number(targetUser.banned)) return json({ error: 'user_banned' }, 409);
  const payload: Record<string, number> = {};
  try {
    if (action === 'add_coins') payload.amount = boundedInteger(body.amount, 1, 100_000_000);
    if (action === 'grant_pack') {
      payload.packId = boundedInteger(body.packId, 1, 1_000_000);
      payload.count = boundedInteger(body.count ?? 1, 1, 100);
    }
    if (action === 'grant_card') {
      payload.resourceId = boundedInteger(body.resourceId, 1, 2_147_483_647);
      if (!cardResourceIds.has(payload.resourceId)) return json({ error: 'unknown_card' }, 400);
    }
  } catch {
    return json({ error: 'invalid_command_value' }, 400);
  }
  const result = await env.DB.prepare(`INSERT INTO commands(actor_discord_id,target_discord_id,action,payload,created_at)
    VALUES(?,?,?,?,?)`).bind(admin.discord_id, target, action, JSON.stringify(payload), now()).run();
  await audit(env, String(admin.discord_id), target, action, JSON.stringify(payload));
  return json({ ok: true, commandId: result.meta.last_row_id });
}

async function adminBan(request: Request, env: Env, banned: boolean): Promise<Response> {
  const admin = await requireAdmin(request, env);
  if (admin instanceof Response) return admin;
  const body = await readJson<{ discordId?: string; reason?: string }>(request);
  const target = String(body.discordId ?? '');
  if (!/^\d{5,24}$/.test(target)) return json({ error: 'invalid_discord_id' }, 400);
  if (target === admin.discord_id && banned) return json({ error: 'cannot_ban_self' }, 409);
  const update = await env.DB.prepare('UPDATE users SET banned=?,ban_reason=? WHERE discord_id=?')
    .bind(banned ? 1 : 0, banned ? String(body.reason ?? 'Banned by administrator').slice(0, 300) : null, target).run();
  if (!update.meta.changes) return json({ error: 'unknown_user' }, 404);
  if (banned) {
    await env.DB.batch([
      env.DB.prepare('DELETE FROM sessions WHERE discord_id=?').bind(target),
      env.DB.prepare('UPDATE devices SET revoked=1 WHERE discord_id=?').bind(target),
      env.DB.prepare(`INSERT INTO commands(actor_discord_id,target_discord_id,action,payload,created_at)
        VALUES(?,?,?,?,?)`).bind(admin.discord_id, target, 'ban_user', JSON.stringify({ reason: body.reason ?? '' }), now()),
    ]);
  } else {
    await env.DB.prepare(`INSERT INTO commands(actor_discord_id,target_discord_id,action,payload,created_at)
      VALUES(?,?,?,?,?)`).bind(admin.discord_id, target, 'unban_user', '{}', now()).run();
  }
  await audit(env, String(admin.discord_id), target, banned ? 'ban_user' : 'unban_user', JSON.stringify({ reason: body.reason ?? '' }));
  return json({ ok: true });
}

async function logout(request: Request, env: Env): Promise<Response> {
  const token = cookie(request, 'p21_session');
  if (token) await env.DB.prepare('DELETE FROM sessions WHERE session_hash=?').bind(await sha256(token)).run();
  return new Response(null, { status: 302, headers: { location: '/', 'set-cookie': 'p21_session=; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=0' } });
}

async function sessionUser(request: Request, env: Env): Promise<Record<string, unknown> | null> {
  const token = cookie(request, 'p21_session');
  if (!token) return null;
  return env.DB.prepare(`SELECT u.* FROM sessions s JOIN users u ON u.discord_id=s.discord_id
    WHERE s.session_hash=? AND s.expires_at>?`).bind(await sha256(token), now()).first();
}

async function requireAdmin(request: Request, env: Env): Promise<Record<string, unknown> | Response> {
  const user = await sessionUser(request, env);
  if (!user || !isAdminDiscordId(env, String(user.discord_id)) || Number(user.banned)) return json({ error: 'forbidden' }, 403);
  return user;
}

async function authenticatedDevice(request: Request, env: Env): Promise<{ claims: AccessClaims; user: Record<string, unknown> } | Response> {
  const authorization = request.headers.get('authorization') ?? '';
  const token = authorization.startsWith('Bearer ') ? authorization.slice(7) : '';
  const claims = await verifyAccessToken(token, env);
  if (!claims) return json({ error: 'invalid_device_token' }, 401);
  const row = await env.DB.prepare(`SELECT u.username,u.banned,d.revoked FROM devices d JOIN users u ON u.discord_id=d.discord_id
    WHERE d.id=? AND d.discord_id=?`).bind(claims.did, claims.sub).first<Record<string, unknown>>();
  if (!row || Number(row.banned) || Number(row.revoked)) return json({ error: 'access_revoked' }, 403);
  return { claims, user: row };
}

async function hostAuthorized(request: Request, env: Env): Promise<boolean> {
  const supplied = request.headers.get('x-project21-host-secret') ?? '';
  return supplied.length >= 32 && await hashMatches(supplied, await sha256(env.HOST_AGENT_SECRET));
}

async function signAccessToken(claims: AccessClaims, env: Env): Promise<string> {
  const payload = base64Url(encoder.encode(JSON.stringify(claims)));
  const key = await crypto.subtle.importKey('raw', encoder.encode(env.SESSION_SECRET), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const signature = await crypto.subtle.sign('HMAC', key, encoder.encode(payload));
  return `${payload}.${base64Url(new Uint8Array(signature))}`;
}

async function verifyAccessToken(token: string, env: Env): Promise<AccessClaims | null> {
  const [payload, signature, extra] = token.split('.');
  if (!payload || !signature || extra) return null;
  const key = await crypto.subtle.importKey('raw', encoder.encode(env.SESSION_SECRET), { name: 'HMAC', hash: 'SHA-256' }, false, ['verify']);
  const valid = await crypto.subtle.verify('HMAC', key, toArrayBuffer(fromBase64Url(signature)), encoder.encode(payload));
  if (!valid) return null;
  try {
    const claims = JSON.parse(new TextDecoder().decode(fromBase64Url(payload))) as AccessClaims;
    if (claims.typ !== 'device' || claims.exp <= now() || !/^\d{5,24}$/.test(claims.sub) || !claims.did) return null;
    return claims;
  } catch {
    return null;
  }
}

async function audit(env: Env, actor: string, target: string | null, action: string, detail: string): Promise<void> {
  await env.DB.prepare('INSERT INTO audit_events(actor_discord_id,target_discord_id,action,detail,created_at) VALUES(?,?,?,?,?)')
    .bind(actor, target, action, detail.slice(0, 4000), now()).run();
}

async function readJson<T>(request: Request): Promise<T> {
  const type = request.headers.get('content-type') ?? '';
  if (!type.toLowerCase().startsWith('application/json')) throw new Error('JSON content type required');
  return request.json<T>();
}

function json(value: unknown, status = 200, extra: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(value), { status, headers: { ...jsonHeaders, ...extra } });
}

function html(body: string, status = 200): Response {
  return new Response(body, { status, headers: { 'content-type': 'text/html; charset=utf-8' } });
}

function withSecurityHeaders(response: Response): Response {
  const next = new Response(response.body, response);
  next.headers.set('x-content-type-options', 'nosniff');
  next.headers.set('x-frame-options', 'DENY');
  next.headers.set('referrer-policy', 'no-referrer');
  next.headers.set('permissions-policy', 'camera=(), microphone=(), geolocation=()');
  next.headers.set('content-security-policy', "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
  return next;
}

function landingHtml(): string {
  return page('Project 21', `<section><h1>Project 21</h1><p>Connect your Discord identity to the Project 21 launcher.</p><a class="button" href="/login">Continue with Discord</a></section>`);
}

function messageHtml(title: string, message: string): string {
  return page(title, `<section><h1>${escapeHtml(title)}</h1><p>${message}</p></section>`);
}

function adminHtml(username: string): string {
  return page('Project 21 Control Panel', `<header><div><strong>Project 21 Control Panel</strong><small>Signed in as ${escapeHtml(username)}</small></div><form method="post" action="/logout"><button>Sign out</button></form></header>
  <main><section><h2>Players</h2><p class="muted">Each approved Discord ID has an isolated LocalFUT club and device authorization. Grants apply only to the selected player.</p><div id="players">Loading…</div></section>
  <section><h2>Grant content</h2><form id="grant">
    <label>Discord ID<input name="targetDiscordId" required></label>
    <label>Action<select name="action"><option value="add_coins">Add coins</option><option value="grant_pack">Grant pack</option><option value="grant_card">Grant player card</option><option value="kick_user">Kick session</option></select></label>
    <label id="command-value-label"><span id="command-value-title">Amount</span><input name="value" type="number" min="1" required></label>
    <div id="card-picker" hidden>
      <label>Search FIFA 21 cards<input id="card-search" type="search" autocomplete="off" placeholder="Player name, team, rarity or resource ID"></label>
      <label>Player card<select id="card-results" name="cardResourceId" required disabled><option value="">Search for a player first</option></select></label>
      <small id="card-help" class="muted">Choose the exact version and rating you want to grant.</small>
    </div>
    <button>Queue command</button>
  </form><pre id="result"></pre></section>
  <section><h2>Recent commands</h2><div id="commands">Loading…</div></section></main>

  <script>
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
  }[c]));

  const playersEl = document.getElementById('players');
  const commandsEl = document.getElementById('commands');
  const grantForm = document.getElementById('grant');
  const resultEl = document.getElementById('result');
  const cardPicker = document.getElementById('card-picker');
  const cardSearch = document.getElementById('card-search');
  const cardResults = document.getElementById('card-results');
  const cardHelp = document.getElementById('card-help');

  async function load() {
    try {
      const [userResponse, commandResponse] = await Promise.all([
        fetch('/api/admin/users'),
        fetch('/api/admin/commands')
      ]);

      if (!userResponse.ok) {
        throw new Error('Players request failed with HTTP ' + userResponse.status);
      }

      if (!commandResponse.ok) {
        throw new Error('Commands request failed with HTTP ' + commandResponse.status);
      }

      const u = await userResponse.json();
      const c = await commandResponse.json();

      const users = Array.isArray(u.users) ? u.users : [];
      const commands = Array.isArray(c.commands) ? c.commands : [];

      playersEl.innerHTML =
        '<table>' +
          '<tr>' +
            '<th>User</th>' +
            '<th>Discord ID</th>' +
            '<th>Status</th>' +
            '<th>Devices</th>' +
            '<th>Actions</th>' +
          '</tr>' +
          users.map(x =>
            '<tr>' +
              '<td>' + esc(x.username) + '</td>' +
              '<td><code>' + esc(x.discord_id) + '</code></td>' +
              '<td>' +
                (x.banned ? 'BANNED' : (x.approved ? 'Approved' : 'PENDING')) +
              '</td>' +
              '<td>' + esc(x.device_count) + '</td>' +
              '<td>' +
                '<button ' +
                  'data-action="approve" ' +
                  'data-id="' + esc(x.discord_id) + '" ' +
                  'data-on="' + (!x.approved) + '">' +
                  (x.approved ? 'Remove approval' : 'Approve') +
                '</button> ' +
                '<button ' +
                  'data-action="ban" ' +
                  'data-id="' + esc(x.discord_id) + '" ' +
                  'data-on="' + (!x.banned) + '">' +
                  (x.banned ? 'Unban' : 'Ban') +
                '</button>' +
              '</td>' +
            '</tr>'
          ).join('') +
        '</table>';

      commandsEl.innerHTML =
        '<table>' +
          '<tr>' +
            '<th>ID</th>' +
            '<th>Action</th>' +
            '<th>Target</th>' +
            '<th>Status</th>' +
            '<th>Result</th>' +
          '</tr>' +
          commands.map(x =>
            '<tr>' +
              '<td>' + esc(x.id) + '</td>' +
              '<td>' + esc(x.action) + '</td>' +
              '<td>' + esc(x.target_discord_id) + '</td>' +
              '<td>' + esc(x.status) + '</td>' +
              '<td>' + esc(x.result) + '</td>' +
            '</tr>'
          ).join('') +
        '</table>';
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);

      playersEl.innerHTML =
        '<p class="muted">Failed to load players: ' + esc(message) + '</p>';

      commandsEl.innerHTML =
        '<p class="muted">Failed to load commands: ' + esc(message) + '</p>';

      console.error(error);
    }
  }

  async function approve(id, on) {
    const response = await fetch('/api/admin/approve', {
      method: 'POST',
      headers: {
        'content-type': 'application/json'
      },
      body: JSON.stringify({
        discordId: id,
        approved: on
      })
    });

    if (!response.ok) {
      throw new Error('Approval request failed with HTTP ' + response.status);
    }

    await load();
  }

  async function ban(id, on) {
    const reason = on
      ? (prompt('Ban reason?') || 'Banned by administrator')
      : '';

    const response = await fetch(
      '/api/admin/' + (on ? 'ban' : 'unban'),
      {
        method: 'POST',
        headers: {
          'content-type': 'application/json'
        },
        body: JSON.stringify({
          discordId: id,
          reason
        })
      }
    );

    if (!response.ok) {
      throw new Error('Ban request failed with HTTP ' + response.status);
    }

    await load();
  }

  document.addEventListener('click', async event => {
    const target = event.target;

    if (!(target instanceof Element)) {
      return;
    }

    const button = target.closest('button[data-action][data-id]');

    if (!button) {
      return;
    }

    const id = button.getAttribute('data-id') || '';
    const on = button.getAttribute('data-on') === 'true';
    const action = button.getAttribute('data-action');

    button.disabled = true;

    try {
      if (action === 'approve') {
        await approve(id, on);
      }

      if (action === 'ban') {
        await ban(id, on);
      }
    } catch (error) {
      console.error(error);
      alert(error instanceof Error ? error.message : String(error));
    } finally {
      button.disabled = false;
    }
  });

  const actionSelect = grantForm.elements.action;
  const valueLabel = document.getElementById('command-value-label');
  const valueTitle = document.getElementById('command-value-title');
  const valueInput = grantForm.elements.value;
  let cardSearchTimer = 0;
  let cardRequestSequence = 0;

  async function loadCards() {
    const sequence = ++cardRequestSequence;
    const query = cardSearch.value.trim();
    if (query.length === 1 && !/^\\d$/.test(query)) {
      cardResults.replaceChildren(new Option('Type at least 2 characters', ''));
      cardResults.disabled = true;
      cardHelp.textContent = 'Type at least 2 characters to search.';
      return;
    }
    cardResults.disabled = true;
    cardResults.replaceChildren(new Option('Searching…', ''));
    cardHelp.textContent = 'Searching the FIFA 21 card catalogue…';
    try {
      const response = await fetch('/api/admin/cards?q=' + encodeURIComponent(query));
      if (!response.ok) throw new Error('Card search failed with HTTP ' + response.status);
      const body = await response.json();
      if (sequence !== cardRequestSequence) return;
      const cards = Array.isArray(body.cards) ? body.cards : [];
      const options = [new Option(cards.length ? 'Select a player card' : 'No matching cards', '')];
      for (const card of cards) {
        const details = [card.rating + ' OVR', card.position, card.rarity, card.team]
          .filter(Boolean).join(' · ');
        options.push(new Option(card.name + ' — ' + details + ' — ID ' + card.resourceId, String(card.resourceId)));
      }
      cardResults.replaceChildren(...options);
      cardResults.disabled = cards.length === 0;
      cardHelp.textContent = cards.length
        ? 'Showing ' + cards.length + ' results. Refine the search to find another card version.'
        : 'No cards matched that search.';
    } catch (error) {
      if (sequence !== cardRequestSequence) return;
      cardResults.replaceChildren(new Option('Card search unavailable', ''));
      cardResults.disabled = true;
      cardHelp.textContent = error instanceof Error ? error.message : String(error);
    }
  }

  function syncCommandValue() {
    const isCard = actionSelect.value === 'grant_card';
    const needsNumber = actionSelect.value === 'add_coins' || actionSelect.value === 'grant_pack';
    valueLabel.hidden = !needsNumber;
    valueInput.required = needsNumber;
    valueTitle.textContent = actionSelect.value === 'grant_pack' ? 'Pack ID' : 'Coin amount';
    cardPicker.hidden = !isCard;
    cardResults.required = isCard;
    if (isCard && cardResults.options.length <= 1) loadCards();
  }

  actionSelect.addEventListener('change', syncCommandValue);
  cardSearch.addEventListener('input', () => {
    window.clearTimeout(cardSearchTimer);
    cardSearchTimer = window.setTimeout(loadCards, 250);
  });
  cardSearch.addEventListener('focus', () => {
    if (cardResults.options.length <= 1) loadCards();
  });
  syncCommandValue();

  grantForm.onsubmit = async event => {
    event.preventDefault();

    const form = new FormData(grantForm);
    const action = form.get('action');
    const value = Number(form.get('value'));

    const body = {
      targetDiscordId: form.get('targetDiscordId'),
      action
    };

    if (action === 'add_coins') {
      body.amount = value;
    }

    if (action === 'grant_pack') {
      body.packId = value;
      body.count = 1;
    }

    if (action === 'grant_card') {
      body.resourceId = Number(form.get('cardResourceId'));
    }

    try {
      const response = await fetch('/api/admin/command', {
        method: 'POST',
        headers: {
          'content-type': 'application/json'
        },
        body: JSON.stringify(body)
      });

      const responseBody = await response.json();

      resultEl.textContent = JSON.stringify(responseBody, null, 2);

      if (!response.ok) {
        throw new Error('Command request failed with HTTP ' + response.status);
      }

      await load();
    } catch (error) {
      console.error(error);

      if (!resultEl.textContent) {
        resultEl.textContent =
          error instanceof Error ? error.message : String(error);
      }
    }
  };

  load();
  setInterval(load, 5000);
  </script>`);
}

function page(title: string, body: string): string {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escapeHtml(title)}</title><style>
  :root{color-scheme:dark;font-family:Inter,Segoe UI,sans-serif;background:#09090c;color:#f5f5f6}body{margin:0}header,main,section{max-width:1100px;margin:auto}header{display:flex;justify-content:space-between;padding:24px}header small{display:block;color:#999;margin-top:5px}main{display:grid;gap:18px;padding:0 24px 40px}section{box-sizing:border-box;width:100%;padding:24px;background:#141419;border:1px solid #2d2d35;border-radius:14px}h1,h2{margin-top:0}.button,button{display:inline-block;background:#b8ff35;color:#142000;border:0;border-radius:8px;padding:10px 15px;font-weight:700;text-decoration:none;cursor:pointer}label{display:grid;gap:5px;margin:10px 0}input,select{background:#09090c;color:#fff;border:1px solid #3b3b44;border-radius:7px;padding:10px}table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:9px;border-bottom:1px solid #292930;font-size:13px}.muted{color:#aaa}code,pre{font-family:Consolas,monospace;white-space:pre-wrap}</style></head><body>${body}</body></html>`;
}

function cookie(request: Request, name: string): string | null {
  const match = (request.headers.get('cookie') ?? '').match(new RegExp(`(?:^|;\\s*)${name}=([^;]+)`));
  return match ? decodeURIComponent(match[1]) : null;
}

function safeReturnPath(value: string | null): string {
  if (!value || !value.startsWith('/') || value.startsWith('//')) return '/';
  return value.slice(0, 500);
}

function normalizeCode(value: string): string {
  const cleaned = value.toUpperCase().replace(/[^A-Z0-9]/g, '');
  return cleaned.length === 8 ? `${cleaned.slice(0, 4)}-${cleaned.slice(4)}` : value.toUpperCase();
}

function userCode(): string {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  const bytes = crypto.getRandomValues(new Uint8Array(8));
  const raw = Array.from(bytes, value => alphabet[value % alphabet.length]).join('');
  return `${raw.slice(0, 4)}-${raw.slice(4)}`;
}

function randomToken(bytes: number): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(bytes)));
}

function base64Url(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

function fromBase64Url(value: string): Uint8Array {
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=');
  return Uint8Array.from(atob(normalized), character => character.charCodeAt(0));
}

function toArrayBuffer(value: Uint8Array): ArrayBuffer {
  return value.slice().buffer as ArrayBuffer;
}

async function sha256(value: string): Promise<string> {
  return base64Url(new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(value))));
}

async function hashMatches(value: string, expected: string): Promise<boolean> {
  const actual = await sha256(value);
  if (actual.length !== expected.length) return false;
  let difference = 0;
  for (let index = 0; index < actual.length; index += 1) difference |= actual.charCodeAt(index) ^ expected.charCodeAt(index);
  return difference === 0;
}

function boundedInteger(value: unknown, minimum: number, maximum: number): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) throw new Error('invalid integer');
  return parsed;
}

function normalizeSearch(value: string): string {
  return value.normalize('NFKD').replace(/[\u0300-\u036f]/g, '').toLocaleLowerCase('en').trim();
}

function isAdminDiscordId(env: Env, discordId: string): boolean {
  return env.ADMIN_DISCORD_IDS.split(',').some(value => value.trim() === discordId);
}

function validWireGuardPublicKey(value: string): boolean {
  if (!/^[A-Za-z0-9+/]{43}=$/.test(value)) return false;
  try {
    const decoded = Uint8Array.from(atob(value), character => character.charCodeAt(0));
    return decoded.length === 32 && decoded.some(byte => byte !== 0);
  } catch {
    return false;
  }
}

function validWireGuardEndpoint(value: string): boolean {
  const match = value.match(/^([A-Za-z0-9.-]+):(\d{1,5})$/);
  if (!match) return false;
  const port = Number(match[2]);
  return port >= 1 && port <= 65535 && !match[1].startsWith('.') && !match[1].endsWith('.');
}

async function allocateWireGuardAddress(env: Env, deviceId: string): Promise<string> {
  const rows = await env.DB.prepare('SELECT wireguard_address FROM devices WHERE wireguard_address IS NOT NULL')
    .all<{ wireguard_address: string }>();
  const used = new Set(rows.results.map(row => row.wireguard_address));
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(deviceId)));
  const start = ((digest[0] << 8) | digest[1]) % 1021;
  for (let attempt = 0; attempt < 1021; attempt += 1) {
    const offset = 2 + ((start + attempt) % 1021);
    if (offset === 257) continue; // 10.203.21.1 is the server.
    const address = `10.203.${20 + Math.floor(offset / 256)}.${offset % 256}`;
    if (!used.has(address)) return address;
  }
  throw new Error('WireGuard address pool exhausted');
}

function now(): number { return Math.floor(Date.now() / 1000); }
function trimSlash(value: string): string { return value.replace(/\/$/, ''); }
function escapeHtml(value: string): string { return value.replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character] ?? character)); }

function assertConfigured(env: Env, names: Array<keyof Env>): void {
  for (const name of names) {
    const value = String(env[name] ?? '');
    if (!value || value.includes('CHANGE_ME')) throw new Error(`${String(name)} is not configured`);
  }
}
