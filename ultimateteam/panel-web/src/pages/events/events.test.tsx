import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EventsPage, RotationsSection, type Timing } from './EventsPage';
import { localDate, timingStatus, utcDate } from './time';
import { getContentDestination, setContentDestination } from '../../api/client';
import { isCampaignPromo, isTotwPromo } from '../../api/promoKinds';

let staged: { area: string; key: { code: string }; operation: string; after: Record<string, unknown> }[];
let pending: typeof staged;
let catalogPacks: Record<string, unknown>[];
let packEdits: string[];
let eventRows: (Timing & { code: string })[];
const base = { code: 'base', name: 'Base Icons', enabled: true, starts_at: '2026-09-30T17:00:42Z', ends_at: '2027-01-01T18:00:17Z', promo: 'base_icons' };
const promos = ['bronze_common','bronze_rare','silver_common','silver_rare','gold_common','gold_rare','base_icons','mid_icons','prime_icons','prime_icon_moments','totw_1','totw_2','totw_3','silver_totw','developer_cards','fut_champions'].map(code => ({ code, name: code === 'base_icons' ? 'Base Icons' : code, category: /^totw_\d+$/.test(code) || code === 'silver_totw' ? 'totw' : code, status: 'active', cards: 20, buckets: 1, artwork: [] }))
  .concat([{ code: 'winter_freeze', name: 'Winter Freeze', category: 'other_promo', status: 'active', cards: 20, buckets: 1, artwork: [] }]);
beforeEach(() => {
  setContentDestination(null);
  staged = []; pending = []; catalogPacks = []; packEdits = [];
  eventRows = [{ code: 'initial_totw_1_3', name: 'TOTW 1–3', enabled: true, starts_at: '2026-09-29T18:06:34Z', ends_at: '2026-10-06T18:06:34Z', promos: ['totw_1','totw_2','totw_3'] }];
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() })));
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').split('?')[0]!;
    const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });
    if (path === 'content/changeset/changes' && init?.method === 'PUT') {
      const body = JSON.parse(String(init.body)); staged = [body]; pending = [body];
      if (new URLSearchParams(url.split('?')[1]).get('apply') === '1') {
        if (body.area === 'promo_event') eventRows = [...eventRows.filter(row => row.code !== body.key.code), { code: body.key.code, ...body.after }];
        pending = [];
      }
      return json({ pending: pending.length });
    }
    if (path === 'content/changeset/publish') {
      for (const change of pending) if (change.area === 'promo_event') {
        const row = { code: change.key.code, ...(change.after as unknown as Timing) };
        eventRows = [...eventRows.filter(r => r.code !== row.code), row];
      }
      pending = []; return json({ published: { changes: staged }, content: {} });
    }
    if (path === 'content/changeset') return json({ changeset: { changes: pending } });
    if (path === 'promos') return json({ definitions: promos });
    if (path === 'packs/catalog') return json({ revision: `r${packEdits.length + 1}`, packs: catalogPacks, oddsTemplate: { icon: 0.0005, pickTotw: 0.03 } });
    if (path.startsWith('packs/catalog/') && init?.method === 'PUT') {
      const body = JSON.parse(String(init.body));
      if (body.revision !== `r${packEdits.length + 1}`) return new Response(JSON.stringify({ error: 'Stale pack revision' }), { status: 409 });
      packEdits.push(`${path} ${String(init.body)}`); return json({ pending: 0, applies: 'live' });
    }
    if (path === 'promo-events') return json({ definitions: eventRows });
    if (path.startsWith('promo-events/')) { const { code: _, ...definition } = eventRows.find(row => row.code === path.split('/')[1])!; return json({ definition }); }
    if (path === 'icon-rotations') return json({ definitions: [base] });
    if (path === 'icon-rotations/base') { const { code: _, ...definition } = base; return json({ definition }); }
    if (path === 'promos/availability') return json({ at: '2026-10-01T12:00:00Z', iconPromo: 'base_icons' });
    if (path === 'assets') return json({ available: false, frames: [] });
    throw new Error(`Unexpected request: ${path}`);
  }));
});
afterEach(() => { setContentDestination(null); vi.unstubAllGlobals(); });
function show(section = false, focus?: { kind: 'promo_event' | 'icon_rotation'; code: string }, onFocusHandled?: () => void) {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    {section ? <RotationsSection {...(focus && { focus })} {...(onFocusHandled && { onFocusHandled })} /> : <EventsPage />}
  </QueryClientProvider>);
}
it('converts UK summer and winter times and rejects missing or repeated DST times', () => {
  expect(utcDate('2026-09-30T19:00')).toBe('2026-09-30T18:00:00.000Z');
  expect(utcDate('2027-01-01T19:00')).toBe('2027-01-01T19:00:00.000Z');
  expect(localDate('2027-05-01T17:00:00Z')).toBe('2027-05-01T18:00');
  expect(() => utcDate('2027-03-28T01:30')).toThrow(/does not exist/);
  expect(() => utcDate('2026-10-25T01:30')).toThrow(/occurs twice/);
});
it('uses inclusive starts and exclusive ends for status', () => {
  expect(timingStatus(base, base.starts_at)).toBe('Active');
  expect(timingStatus(base, base.ends_at)).toBe('Ended');
  expect(timingStatus(base, '2026-09-01T00:00:00Z')).toBe('Upcoming');
  expect(timingStatus({ ...base, enabled: false }, base.starts_at)).toBe('Disabled');
});
it('classifies native TOTW codes and category separately from campaign promos', () => {
  expect(isTotwPromo({ code: 'totw_40' })).toBe(true);
  expect(isTotwPromo({ code: 'silver_totw' })).toBe(true);
  expect(isTotwPromo({ code: 'reviewed_week', category: 'totw' })).toBe(true);
  expect(isCampaignPromo({ code: 'winter_freeze' })).toBe(true);
  expect(isCampaignPromo({ code: 'base_icons' })).toBe(false);
  expect(isCampaignPromo({ code: 'gold_rare' })).toBe(false);
});
it('embeds all six standard pools and separate Icon and TOTW schedules without a page header', async () => {
  show(true); await screen.findByRole('heading', { name: 'Standard pool' });
  for (const code of ['bronze_common','bronze_rare','silver_common','silver_rare','gold_common','gold_rare']) expect(screen.getByRole('link', { name: new RegExp(code) }).getAttribute('href')).toBe(`#/promos/${code}`);
  expect(screen.getByRole('heading', { name: 'Standalone rotations' })).toBeTruthy();
  expect(screen.getByRole('heading', { name: 'Icon rotation' })).toBeTruthy();
  expect(screen.getByRole('heading', { name: 'TOTW rotation' })).toBeTruthy();
  expect(screen.getByRole('heading', { name: 'TOTW 1–3' })).toBeTruthy();
  expect(screen.queryByRole('heading', { level: 1 })).toBeNull();
});
it('saves multi-team TOTW rotation using date-only 7pm UK boundaries independently of selected campaign', async () => {
  setContentDestination(57);
  show(); fireEvent.click(await screen.findByRole('button', { name: 'Create TOTW rotation' }));
  const dialog = await screen.findByRole('dialog');
  fireEvent.change(within(dialog).getByLabelText('Schedule name'), { target: { value: 'Winter TOTW' } });
  fireEvent.change(within(dialog).getByLabelText('Start'), { target: { value: '2027-01-01' } });
  fireEvent.change(within(dialog).getByLabelText('End'), { target: { value: '2027-01-08' } });
  fireEvent.click(within(dialog).getByLabelText('totw_1')); fireEvent.click(within(dialog).getByLabelText('totw_2'));
  expect((within(dialog).getByLabelText('Start') as HTMLInputElement).type).toBe('date');
  for (const label of ['gold_rare', 'Base Icons', 'developer_cards', 'fut_champions', 'Winter Freeze']) expect(within(dialog).queryByLabelText(label)).toBeNull();
  expect(within(dialog).getByLabelText('silver_totw')).toBeTruthy();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save rotation' }));
  await waitFor(() => expect(staged[0]?.after).toMatchObject({ promos: ['totw_1','totw_2'], starts_at: '2027-01-01T19:00:00.000Z', ends_at: '2027-01-08T19:00:00.000Z' }));
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  expect(getContentDestination()).toBe(57);
  const requests = vi.mocked(fetch).mock.calls.map(([url]) => String(url)).filter(url => !url.endsWith('/assets'));
  expect(requests.every(url => new URLSearchParams(url.split('?')[1]).get('dropId') === '')).toBe(true);
  const write = vi.mocked(fetch).mock.calls.find(([, init]) => init?.method === 'PUT');
  expect(new URLSearchParams(String(write?.[0]).split('?')[1]).get('apply')).toBe('1');
});
it('keeps exact existing Icon timestamps when changing only the tier', async () => {
  show(); await screen.findByRole('heading', { name: 'Icon rotation' });
  const row = screen.getByRole('heading', { name: 'Base Icons' }).closest('article')!;
  fireEvent.click(within(row).getByRole('button', { name: 'Edit schedule' }));
  fireEvent.change(await screen.findByLabelText('Icon tier'), { target: { value: 'mid_icons' } });
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Save rotation' }));
  await waitFor(() => expect(staged[0]).toMatchObject({ area: 'icon_rotation', operation: 'update', after: { promo: 'mid_icons', starts_at: base.starts_at, ends_at: base.ends_at } }));
});
it('moves only an explicitly edited date to 7pm UK, retaining the other exact boundary', async () => {
  show(true, { kind: 'icon_rotation', code: 'base' });
  const start = await screen.findByLabelText('Start');
  fireEvent.change(start, { target: { value: '2026-10-01' } });
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Save rotation' }));
  await waitFor(() => expect(staged[0]?.after).toMatchObject({ starts_at: '2026-10-01T18:00:00.000Z', ends_at: base.ends_at }));
});
it('supports focus from the calendar and preserves editable legacy non-TOTW schedules', async () => {
  eventRows.push({ code: 'legacy_freeze', name: 'Legacy Freeze', enabled: true, starts_at: '2027-01-01T18:08:44Z', ends_at: '2027-01-08T18:09:11Z', promos: ['winter_freeze'] });
  const handled = vi.fn();
  show(true, { kind: 'promo_event', code: 'legacy_freeze' }, handled);
  await screen.findByLabelText('Schedule name');
  expect(handled).toHaveBeenCalled();
  expect(screen.getByRole('heading', { name: 'Legacy standalone promo events', hidden: true })).toBeTruthy();
  const dialog = screen.getByRole('dialog');
  expect((within(dialog).getByLabelText('Winter Freeze') as HTMLInputElement).checked).toBe(true);
  fireEvent.change(within(dialog).getByLabelText('Schedule name'), { target: { value: 'Legacy Freeze corrected' } });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save rotation' }));
  await waitFor(() => expect(staged[0]?.after).toMatchObject({ name: 'Legacy Freeze corrected', promos: ['winter_freeze'], starts_at: '2027-01-01T18:08:44Z', ends_at: '2027-01-08T18:09:11Z' }));
});
it('can repair an older pending rotation insertion without loading nonexistent live row', async () => {
  pending = [{ area: 'promo_event', key: { code: 'pending_totw' }, operation: 'insert', after: {
    name: 'Pending TOTW', enabled: true, starts_at: '2027-01-01T19:00:00Z', ends_at: '2027-01-08T19:00:00Z', promos: ['totw_1'],
  } }];
  show();
  const heading = await screen.findByRole('heading', { name: 'Pending TOTW' });
  fireEvent.click(within(heading.closest('article')!).getByRole('button', { name: 'Edit schedule' }));
  fireEvent.change(await screen.findByLabelText('Schedule name'), { target: { value: 'Corrected TOTW' } });
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Save rotation' }));
  await waitFor(() => expect(staged[0]).toMatchObject({ operation: 'insert', after: { name: 'Corrected TOTW' } }));
  expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('promo-events/pending_totw'))).toBe(false);
});
it('keeps the odds wizard pack reads and writes in immediate scope', async () => {
  setContentDestination(57);
  catalogPacks = [
    { id: 74, description: 'Gold Pack', config: { name: 'Gold Pack', category_chances: { icon: 0.0005 } } },
    { id: 75, description: 'Premium Gold Pack', config: { name: 'Premium Gold Pack', category_chances: { icon: 0.0005 } } },
    { id: 90, description: 'Event Specials', config: { name: 'Event Specials', promo_only: true, category_chances: {} } },
  ];
  show(); fireEvent.click(await screen.findByRole('button', { name: 'Create TOTW rotation' }));
  const dialog = await screen.findByRole('dialog');
  fireEvent.change(within(dialog).getByLabelText('Schedule name'), { target: { value: 'Weekly team' } });
  fireEvent.change(within(dialog).getByLabelText('Start'), { target: { value: '2027-01-01' } });
  fireEvent.change(within(dialog).getByLabelText('End'), { target: { value: '2027-01-08' } });
  fireEvent.click(within(dialog).getByLabelText('totw_1'));
  fireEvent.click(within(dialog).getByRole('button', { name: 'Save rotation' }));
  const step = await screen.findByRole('dialog', { name: "Odds for the event's promos" });
  expect(await within(step).findByLabelText(/^Gold Pack/)).toBeTruthy();
  expect(within(step).queryByLabelText(/Event Specials/)).toBeNull();
  fireEvent.change(within(step).getByLabelText('TOTW (%)'), { target: { value: '0.3' } });
  fireEvent.click(within(step).getByLabelText(/Premium Gold Pack/));
  fireEvent.click(within(step).getByRole('button', { name: /(?:Stage|Save) odds/ }));
  await waitFor(() => expect(packEdits).toHaveLength(2));
  expect(packEdits[0]).toContain('"totw":0.003');
  expect(packEdits[1]).toContain('"totw":0');
  expect(packEdits[0]).toContain('"revision":"r1"');
  expect(packEdits[1]).toContain('"revision":"r2"');
  const requests = vi.mocked(fetch).mock.calls.map(([url]) => String(url)).filter(url => url.includes('packs/catalog'));
  expect(requests.every(url => new URLSearchParams(url.split('?')[1]).get('dropId') === '')).toBe(true);
  const writes = vi.mocked(fetch).mock.calls.filter(([url, init]) => String(url).includes('packs/catalog/') && init?.method === 'PUT');
  expect(writes.every(([url]) => new URLSearchParams(String(url).split('?')[1]).get('apply') === '1')).toBe(true);
  expect(getContentDestination()).toBe(57);
});
