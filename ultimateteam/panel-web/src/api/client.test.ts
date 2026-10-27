import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, destinationPath, setContentDestination, sessionEvents } from './client';

const reply = (status: number, body: unknown) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

afterEach(() => { vi.unstubAllGlobals(); setContentDestination(null); });

describe('api', () => {
  it('saves normal content live while keeping explicit drop attachments as drafts', async () => {
    const fetch = vi.fn(async (_url: string) => reply(200, {})); vi.stubGlobal('fetch', fetch);
    await api('content/changeset/changes', { method: 'PUT', body: { area: 'player_card' } });
    await api('packs/catalog/3', { method: 'PUT', body: {} });
    await api('content/changeset/changes?dropId=', { method: 'PUT', body: { area: 'icon_rotation' } });
    await api('content/changeset/changes?dropId=42', { method: 'PUT', body: { area: 'promo_event' } });
    await api('content/changeset/changes', { method: 'PUT', staged: true, body: { area: 'rules' } });
    expect(fetch.mock.calls.map(call => call[0])).toEqual([
      '/panel/api/content/changeset/changes?apply=1', '/panel/api/packs/catalog/3?apply=1',
      '/panel/api/content/changeset/changes?dropId=&apply=1', '/panel/api/content/changeset/changes?dropId=42',
      '/panel/api/content/changeset/changes',
    ]);
  });
  it('scopes content edits and promo previews without changing operational APIs', () => {
    setContentDestination(42);
    expect(destinationPath('content/changeset')).toBe('content/changeset?dropId=42');
    expect(destinationPath('content/changeset/changes?dropId=7')).toBe('content/changeset/changes?dropId=7');
    expect(destinationPath('promos/availability?at=2026-10-07')).toBe('promos/availability?at=2026-10-07&dropId=42');
    expect(destinationPath('content/schedule')).toBe('content/schedule');
    expect(destinationPath('content/history/5/revert')).toBe('content/history/5/revert?dropId=42');
    expect(destinationPath('content/history?limit=50')).toBe('content/history?limit=50');
    expect(destinationPath('packs/catalog')).toBe('packs/catalog?dropId=42');
    expect(destinationPath('packs/catalog/3')).toBe('packs/catalog/3?dropId=42');
    expect(destinationPath('packs/lineup')).toBe('packs/lineup?dropId=42');
    expect(destinationPath('packs/tokens-lineup')).toBe('packs/tokens-lineup?dropId=42');
    expect(destinationPath('packs/catalog/test')).toBe('packs/catalog/test?dropId=42');
    expect(destinationPath('packs/simulate')).toBe('packs/simulate?dropId=42');
    expect(destinationPath('packs/odds?packId=3')).toBe('packs/odds?packId=3&dropId=42');
    expect(destinationPath('packs')).toBe('packs');
    expect(destinationPath('players/1')).toBe('players/1');
    setContentDestination(null);
    expect(destinationPath('content/changeset')).toBe('content/changeset');
  });
  it('refuses scheduled pack deletion and force deletion before sending a request', async () => {
    const fetch = vi.fn(); vi.stubGlobal('fetch', fetch);
    setContentDestination(42);
    await expect(api('packs/catalog/3/delete', { method: 'POST', body: {} })).rejects.toThrow('cannot be scheduled');
    await expect(api('packs/catalog/3/delete', { method: 'POST', body: { force: true } })).rejects.toThrow('cannot be scheduled');
    expect(fetch).not.toHaveBeenCalled();
  });
  it('reads from /panel/api/ without a Content-Type', async () => {
    const fetch = vi.fn().mockResolvedValue(reply(200, { ok: true }));
    vi.stubGlobal('fetch', fetch);
    await expect(api('session')).resolves.toEqual({ ok: true });
    expect(fetch).toHaveBeenCalledWith('/panel/api/session', { method: 'GET', credentials: 'same-origin' });
  });

  it('sends JSON on every write, even without a body (the server refuses other writes)', async () => {
    const fetch = vi.fn().mockResolvedValue(reply(200, {}));
    vi.stubGlobal('fetch', fetch);
    await api('maintenance/backup', { method: 'POST' });
    await api('login', { method: 'POST', body: { password: 'x' } });
    expect(fetch.mock.calls[0]?.[1]).toEqual({ method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' } });
    expect(fetch.mock.calls[1]?.[1]).toMatchObject({ headers: { 'Content-Type': 'application/json' }, body: '{"password":"x"}' });
  });

  it('carries the server message, status and body on a refusal', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(reply(428, { error: 'That player is signed in.', code: 'needsConfirm' })));
    const error = await api('players/1', { method: 'PATCH', body: {} }).catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ message: 'That player is signed in.', status: 428, body: { code: 'needsConfirm' } });
  });

  it('announces a 401 so the app can show sign-in', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(reply(401, { error: 'no' })));
    const listener = vi.fn();
    sessionEvents.addEventListener('signedout', listener);
    await expect(api('dashboard')).rejects.toMatchObject({ status: 401 });
    sessionEvents.removeEventListener('signedout', listener);
    expect(listener).toHaveBeenCalledOnce();
  });
});
