import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ContentDrop, ScheduleReply } from '../../api/schedule';
import { AvailabilityDialog, ScheduledSection, type AvailabilityKind } from './AvailabilitySections';

afterEach(() => vi.unstubAllGlobals());
const wrapper = (element: React.ReactNode) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}>{element}</QueryClientProvider>);
  return client;
};

describe('dated availability', () => {
  it.each(['store', 'token', 'sbc', 'objective'] as const)('schedules %s content with dates and the latest attachment version', async kind => {
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [], drops: [], legacy: [] };
    const writes: { path: string; body: Record<string, unknown> }[] = [];
    const close = vi.fn(), prepare = vi.fn(async () => {});
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.replace('/panel/api/', '');
      if (init?.method && init.method !== 'GET') {
        const body = JSON.parse(String(init.body)); writes.push({ path, body });
        if (path === 'content/drops') data.drops.push({ ...body, id: 1, version: 1, status: 'draft', changes: 0, items: [], changesetId: 1, error: null, publishedAt: null });
        if (path.startsWith('content/changeset/changes')) {
          const drop = data.drops[0]!; drop.version++; drop.changes = 1;
          drop.items.push({ id: 1, area: 'content_window', key: body.key, operation: 'update', inheritEnd: true, endsAt: drop.endsAt });
        }
        if (path.endsWith('/schedule')) {
          expect(body.version).toBe(2); data.drops[0]!.status = 'scheduled';
        }
      }
      return new Response(JSON.stringify(path === 'content/schedule' ? data : data.drops[0]), { status: 200 });
    }));
    const client = wrapper(<AvailabilityDialog kind={kind} options={[{ value: '42', label: 'Synthetic release' }]} onClose={close}
      {...(kind === 'store' || kind === 'token' ? { currency: kind === 'token' ? 'Aurora Tokens' as const : 'Coins' as const, prepare } : {})} />);
    fireEvent.change(screen.getByLabelText('Start date'), { target: { value: '2026-10-08' } });
    fireEvent.change(screen.getByLabelText('End date'), { target: { value: '' } });
    expect(screen.getByRole('button', { name: 'Save release' })).toHaveProperty('disabled', true);
    fireEvent.change(screen.getByLabelText('End date'), { target: { value: '2026-10-15' } });
    if (kind === 'store' || kind === 'token') fireEvent.change(screen.getByLabelText(`${kind === 'token' ? 'Aurora Tokens' : 'Coins'} price`), { target: { value: '25' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save release' }));
    await waitFor(() => expect(close).toHaveBeenCalledOnce());
    expect(writes.map(write => write.path)).toEqual(['content/drops', 'content/changeset/changes?dropId=1', 'content/drops/1/schedule']);
    expect(writes[0]!.body).toMatchObject({ campaignId: null, startsAt: '2026-10-08T18:00:00.000Z', endsAt: '2026-10-15T18:00:00.000Z' });
    expect(writes[1]!.body).toMatchObject({ area: 'content_window', key: { kind, target: '42' }, after: { enabled: true } });
    if (kind === 'store' || kind === 'token') expect(prepare).toHaveBeenCalledWith({ target: '42', price: 25, untradeable: kind === 'token' });
    client.clear();
  });

  it('retries an attachment on the same saved release and publishes an already open window', async () => {
    const data: ScheduleReply = { now: '2026-10-07T20:00:00Z', campaigns: [], drops: [], legacy: [] };
    let failed = false; const writes: string[] = []; const close = vi.fn();
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.replace('/panel/api/', '');
      if (init?.method && init.method !== 'GET') {
        writes.push(path); const body = JSON.parse(String(init.body));
        if (path === 'content/drops') data.drops.push({ ...body, id: 1, version: 1, status: 'draft', changes: 0, items: [], changesetId: 1, error: null, publishedAt: null });
        if (path === 'content/drops/1') { expect(body.version).toBe(1); data.drops[0]!.version++; }
        if (path.startsWith('content/changeset/changes')) {
          if (!failed) { failed = true; return new Response(JSON.stringify({ error: 'Synthetic attachment refusal' }), { status: 422 }); }
          data.drops[0]!.version++; data.drops[0]!.changes = 1;
        }
        if (path.endsWith('/publish')) { expect(body.version).toBe(3); data.drops[0]!.status = 'live'; }
      }
      return new Response(JSON.stringify(path === 'content/schedule' ? data : data.drops[0]), { status: 200 });
    }));
    const client = wrapper(<AvailabilityDialog kind="sbc" options={[{ value: 'SYNTH', label: 'Synthetic SBC' }]} onClose={close} />);
    fireEvent.change(screen.getByLabelText('Start date'), { target: { value: '2026-10-07' } });
    fireEvent.change(screen.getByLabelText('End date'), { target: { value: '2026-10-15' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save release' }));
    await screen.findByRole('alert');
    fireEvent.click(screen.getByRole('button', { name: 'Save release' }));
    await waitFor(() => expect(close).toHaveBeenCalledOnce());
    expect(writes.filter(path => path === 'content/drops')).toHaveLength(1);
    expect(writes.at(-1)).toBe('content/drops/1/publish');
    client.clear();
  });

  it('shows only this section’s upcoming content, with dates and a direct review link', async () => {
    const drop: ContentDrop = { id: 5, campaignId: null, name: 'Release', startsAt: '2026-10-08T18:00:00Z', endsAt: '2026-10-15T18:00:00Z', inheritEnd: false, status: 'scheduled', version: 1, changesetId: 1, changes: 1, error: null, publishedAt: null,
      items: [{ id: 1, area: 'content_window', key: { kind: 'sbc', target: 'SYNTH' }, operation: 'update', endsAt: '2026-10-15T18:00:00Z', inheritEnd: true }] };
    const data = { now: '2026-10-07T10:00:00Z', campaigns: [], drops: [drop, { ...drop, id: 6, status: 'live' }, { ...drop, id: 7, items: [{ ...drop.items[0], key: { kind: 'objective', target: '9' } }] }], legacy: [] };
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(data), { status: 200 })));
    const client = wrapper(<ScheduledSection kind="sbc" options={[{ value: 'SYNTH', label: 'Synthetic SBC' }]} />);
    expect(await screen.findByRole('heading', { name: 'Synthetic SBC' })).toBeTruthy();
    expect(screen.getAllByRole('link', { name: 'Review release' })).toHaveLength(1);
    expect(screen.getByRole('link', { name: 'Review release' }).getAttribute('href')).toBe('#/schedule?drop=5');
    expect(screen.getByText('08-10-2026 → 15-10-2026 · 7pm UK')).toBeTruthy();
    client.clear();
  });
});
