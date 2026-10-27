import { afterEach, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SchedulePage } from './SchedulePage';
import type { ScheduleReply } from '../../api/schedule';

afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });
function setup(existing = false) {
  const writes: { path: string; url: string; body: any }[] = [];
  const packs = [
    { id: 74, description: 'Gold Pack', config: { category_chances: { icon: 0.001 } } },
    { id: 75, description: 'Premium Gold Pack', config: { category_chances: { totw: 0.01 } } },
    { id: 76, description: 'Configured Pack', config: { category_chances: { rulebreakers: 0.05 } } },
  ];
  let revision = 1;
  const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [{ id: 2, name: 'Rulebreakers', startsAt: '2026-10-09T18:00:00Z', endsAt: '2026-10-16T18:00:00Z', version: 1 }], drops: [], legacy: [] };
  let attached = existing;
  if (existing) { data.drops.push({ id: 3, campaignId: 2, name: 'Team 1', startsAt: '2026-10-09T18:00:00Z', endsAt: '2026-10-16T18:00:00Z', inheritEnd: false, status: 'draft', version: 1, changesetId: 3, changes: 1, publishedAt: null, error: null, items: [] }); window.location.hash = '#/schedule?drop=3'; }
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').split('?')[0]!;
    const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
    if (init?.method && init.method !== 'GET') {
      const body = JSON.parse(String(init.body)); writes.push({ path, url, body });
      if (path === 'content/drops') data.drops.push({ ...body, id: 3, version: 1, status: 'draft', changes: 0, changesetId: 3, items: [], publishedAt: null, error: null });
      if (path === 'content/changeset/changes') { attached = true; data.drops[0]!.version++; data.drops[0]!.changes = 1; }
      if (path.startsWith('packs/catalog/')) {
        expect(body.revision).toBe(`r${revision}`); revision++;
        packs.find(pack => pack.id === Number(path.split('/').at(-1)))!.config.category_chances = body.pack.category_chances;
      }
      if (path.endsWith('/schedule')) {
        expect(packs.every(pack => 'rulebreakers' in pack.config.category_chances)).toBe(true);
        expect(body.version).toBe(data.drops[0]!.version); data.drops[0]!.status = 'scheduled';
      }
      return json(data.drops[0] ?? {});
    }
    if (path === 'content/schedule') return json(data);
    if (path === 'promos') return json({ definitions: [{ code: 'rulebreakers', name: 'Rulebreakers', category: 'other_promo', cards: 20 }] });
    if (path === 'packs/catalog') return json({ revision: `r${revision}`, packs });
    if (path === 'content/changeset') return json({ changeset: { changes: url.includes('dropId=3') && attached ? [{ area: 'promo_event', key: { code: 'drop_3_rulebreakers' }, operation: 'insert', after: { promos: ['rulebreakers'] } }] : [] } });
    if (path === 'sbc') return json({ sets: [] });
    if (path === 'objectives') return json({ settingsFile: 'objectives', rules: { welcomeGroup: { id: 1, title: 'Welcome' }, grindGroups: [] } });
    return json({});
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
  return { data, writes, packs, client };
}
it.each(['Add to drop', 'Save drop', 'Schedule drop'])('asks for existing pack odds when attaching with %s', async action => {
  const { data, writes, packs, client } = setup();
  fireEvent.click(await screen.findByRole('button', { name: 'Add drop' }));
  const drop = within(screen.getByRole('dialog'));
  fireEvent.change(drop.getByLabelText('Drop name'), { target: { value: 'Team 1' } });
  await within(drop.getByLabelText('Content')).findByRole('option', { name: 'Rulebreakers' });
  fireEvent.change(drop.getByLabelText('Content'), { target: { value: 'rulebreakers' } });
  await waitFor(() => expect(drop.getByRole('button', { name: action })).toHaveProperty('disabled', false));
  fireEvent.click(drop.getByRole('button', { name: action }));
  const odds = within(await screen.findByRole('dialog', { name: "Odds for the event's promos" }));
  expect(writes.some(write => write.path.endsWith('/schedule'))).toBe(false);
  expect(odds.queryByLabelText(/Configured Pack/)).toBeNull();
  fireEvent.change(await odds.findByLabelText('Rulebreakers (%)'), { target: { value: '2' } });
  fireEvent.click(odds.getByLabelText(/Premium Gold Pack/));
  fireEvent.click(odds.getByRole('button', { name: 'Save odds' }));
  await waitFor(() => expect(screen.queryByRole('dialog', { name: "Odds for the event's promos" })).toBeNull());
  expect(packs[0]!.config.category_chances).toEqual({ icon: 0.001, rulebreakers: 0.02 });
  expect(packs[1]!.config.category_chances).toEqual({ totw: 0.01, rulebreakers: 0 });
  expect(packs[2]!.config.category_chances).toEqual({ rulebreakers: 0.05 });
  const packWrites = writes.filter(write => write.path.startsWith('packs/catalog/'));
  expect(packWrites).toHaveLength(2);
  expect(packWrites.every(write => new URLSearchParams(write.url.split('?')[1]).get('dropId') === '')).toBe(true);
  expect(packWrites.every(write => write.url.includes('apply=1'))).toBe(true);
  if (action === 'Schedule drop') await waitFor(() => expect(data.drops[0]!.status).toBe('scheduled'));
  else await waitFor(() => expect(drop.getByRole('button', { name: 'Save drop' })).toHaveProperty('disabled', false));
  client.clear();
});
it('keeps an existing draft unscheduled when missing odds are skipped', async () => {
  const { writes, client } = setup(true);
  const drop = within(await screen.findByRole('dialog', { name: 'Team 1' }));
  fireEvent.click(drop.getByRole('button', { name: 'Schedule drop' }));
  const odds = within(await screen.findByRole('dialog', { name: "Odds for the event's promos" }));
  fireEvent.click(await odds.findByRole('button', { name: 'Skip for now' }));
  await drop.findByText('Promo added to the draft. Set its pack odds before scheduling.');
  expect(writes.some(write => write.path.endsWith('/schedule'))).toBe(false);
  fireEvent.click(drop.getByRole('button', { name: 'Schedule drop' }));
  expect(await screen.findByRole('dialog', { name: "Odds for the event's promos" })).toBeTruthy();
  client.clear();
});
