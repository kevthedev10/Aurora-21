import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { agendaEntries, SchedulePage } from './SchedulePage';
import type { ScheduleReply } from '../../api/schedule';
afterEach(() => vi.unstubAllGlobals());

describe('schedule agenda', () => {
  it.each(['Save drop', 'Schedule drop'])('includes selected content when clicking %s without a separate Add click', async action => {
    const calls: string[] = [];
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [{ id: 2, name: 'Rulebreakers', startsAt: '2026-10-09T18:00:00Z', endsAt: '2026-10-16T18:00:00Z', version: 1 }], drops: [], legacy: [] };
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.replace('/panel/api/', '');
      const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
      if (init?.method && init.method !== 'GET') {
        calls.push(path); const body = JSON.parse(String(init.body));
        if (path === 'content/drops') {
          const saved: ScheduleReply['drops'][number] = { ...body, id: 3, status: 'draft', version: 1, changesetId: 30, changes: 0, items: [], error: null, publishedAt: null };
          data.drops.push(saved); return json(saved);
        }
        if (path === 'content/changeset/changes?dropId=3') {
          expect(body.after.promos).toEqual(['csl_tots']);
          const saved = data.drops[0]!;
          saved.version++; saved.changes = 1;
          saved.items.push({ id: 31, area: body.area, operation: body.operation, key: body.key, endsAt: saved.endsAt, inheritEnd: true });
          return json({ pending: 1 });
        }
        if (path === 'content/drops/3/schedule') {
          const saved = data.drops[0]!;
          if (!saved.changes) return json({ error: 'Add content before scheduling.' }, 400);
          expect(body.version).toBe(saved.version);
          saved.version++; saved.status = 'scheduled'; return json(saved);
        }
      }
      if (path === 'content/schedule') return json(data);
      if (path.startsWith('promos')) return json({ definitions: [{ code: 'csl_tots', name: 'CSL TOTS', cards: 10 }] });
      if (path === 'packs/catalog') return json({ packs: [] });
      if (path === 'sbc') return json({ sets: [] });
      if (path === 'objectives') return json({ settingsFile: 'objectives', rules: { welcomeGroup: { id: 1, title: 'Welcome' }, grindGroups: [] } });
      return json({ changeset: null });
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
    fireEvent.click(await screen.findByRole('button', { name: 'Add drop' }));
    const dialog = within(screen.getByRole('dialog'));
    fireEvent.change(dialog.getByLabelText('Drop name'), { target: { value: 'CSL TOTS' } });
    await dialog.findByRole('option', { name: 'CSL TOTS' });
    fireEvent.change(dialog.getByLabelText('Content'), { target: { value: 'csl_tots' } });
    await waitFor(() => expect((dialog.getByRole('button', { name: action }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(dialog.getByRole('button', { name: action }));
    await waitFor(() => expect(data.drops[0]?.changes).toBe(1));
    if (action === 'Schedule drop') await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    else { await dialog.findByText(/Uses the drop expiry/); expect(data.drops[0]?.status).toBe('draft'); }
    expect(calls).toEqual(['content/drops', 'content/changeset/changes?dropId=3', ...(action === 'Schedule drop' ? ['content/drops/3/schedule'] : [])]);
    expect(screen.queryByText('Add content before scheduling.')).toBeNull();
    client.clear();
  });
  it('shows one campaign, jumps to the active campaign, and pages its drops', async () => {
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [
      { id: 1, name: 'September', startsAt: '2026-09-01T18:00:00Z', endsAt: '2026-09-30T18:00:00Z', version: 1 },
      { id: 2, name: 'Halloween', startsAt: '2026-10-01T18:00:00Z', endsAt: '2026-10-28T19:00:00Z', version: 1 },
      { id: 3, name: 'Winter', startsAt: '2026-12-01T19:00:00Z', endsAt: '2026-12-31T19:00:00Z', version: 1 },
    ], drops: Array.from({ length: 8 }, (_, index) => ({ id: index + 1, campaignId: 2, name: `Drop ${index + 1}`, startsAt: '2026-10-08T18:00:00Z', endsAt: '2026-10-14T18:00:00Z', status: 'draft' as const, inheritEnd: false, version: 1, changesetId: index + 1, changes: 0, error: null, publishedAt: null, items: [] })), legacy: [] };
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(data), { status: 200 })));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
    await screen.findByRole('heading', { name: 'Halloween', level: 3 });
    expect(screen.queryByRole('heading', { name: 'September', level: 3 })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Winter', level: 3 })).toBeNull();
    expect(screen.getByText('Drops 1–5 of 8')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Drop 5' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Drop 6' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Next drops' }));
    expect(screen.getByText('Drops 6–8 of 8')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Drop 5' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Drop 6' })).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Next drops' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Next campaign' }));
    expect(screen.getByRole('heading', { name: 'Winter', level: 3 })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Go to active campaign' }));
    expect(screen.getByRole('heading', { name: 'Halloween', level: 3 })).toBeTruthy();
    expect(screen.getByText('Drops 1–5 of 8')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Previous campaign' }));
    expect(screen.getByRole('heading', { name: 'September', level: 3 })).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Previous campaign' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Campaign to view'), { target: { value: '2' } });
    expect(screen.getByRole('heading', { name: 'Halloween', level: 3 })).toBeTruthy();
    client.clear();
  });
  it('adds content during creation, keeps the editor open and retries attachment without creating another drop', async () => {
    const calls: { path: string; body: Record<string, unknown> }[] = [];
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [{ id: 2, name: 'Halloween', startsAt: '2026-10-01T18:00:00Z', endsAt: '2026-10-28T19:00:00Z', version: 1 }], drops: [], legacy: [] };
    let failAttachment = true;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.replace('/panel/api/', '');
      const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
      if (init?.method && init.method !== 'GET') {
        const body = JSON.parse(String(init.body)); calls.push({ path, body });
        if (path === 'content/drops') {
          const saved: ScheduleReply['drops'][number] = { ...body, id: 71, status: 'draft', version: 1, changesetId: 9, error: null, changes: 0, publishedAt: null, items: [] };
          data.drops.push(saved); return json(saved);
        }
        if (path === 'content/changeset/changes?dropId=71') {
          if (failAttachment) { failAttachment = false; return json({ error: 'Attachment failed. Retry adding it.' }, 500); }
          return json({ pending: 1 });
        }
      }
      if (path === 'content/schedule') return json(data);
      if (path.startsWith('promos')) return json({ definitions: [{ code: 'winter_freeze', name: 'Winter Freeze', cards: 20 }, { code: 'halloween', name: 'Halloween', cards: 20 }] });
      if (path.startsWith('content/changeset')) return json({ changeset: null });
      if (path === 'packs/catalog') return json({ packs: [] });
      if (path === 'sbc') return json({ sets: [] });
      if (path === 'objectives') return json({ settingsFile: 'rules/objectives.json', rules: { welcomeGroup: { id: 1, title: 'Welcome' }, grindGroups: [] } });
      return json({});
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
    expect(within(await screen.findByLabelText('Content')).getAllByRole('option').map(option => option.textContent)).toEqual(['All content', 'Promos', 'TOTW', 'Icons', 'Store packs', 'Token Store packs', 'SBCs', 'Objectives', 'Seasons']);
    expect(screen.getByText('01-10-2026 → 28-10-2026 · 7pm UK')).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: 'Add drop' }));
    expect(screen.getByLabelText('Content kind')).toBeTruthy();
    expect(calls).toEqual([]);
    fireEvent.change(screen.getByLabelText('Drop name'), { target: { value: 'Team 1' } });
    await screen.findByRole('option', { name: 'Winter Freeze' });
    fireEvent.change(within(screen.getByRole('dialog')).getByLabelText('Content'), { target: { value: 'winter_freeze' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add to drop' }));
    await screen.findByText('Attachment failed. Retry adding it.');
    expect(screen.getByRole('dialog')).toBeTruthy();
    await waitFor(() => expect((screen.getByRole('button', { name: 'Add to drop' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Add to drop' }));
    await screen.findByText('Content attached to this draft.');
    fireEvent.change(within(screen.getByRole('dialog')).getByLabelText('Content'), { target: { value: 'halloween' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add to drop' }));
    await waitFor(() => expect(calls).toHaveLength(4));
    expect(calls.filter(call => call.path === 'content/drops')).toHaveLength(1);
    expect(calls[1]?.body).toMatchObject({ area: 'promo_event', key: { code: 'drop_71_winter_freeze' }, after: { starts_at: '2026-10-01T18:00:00.000Z', ends_at: '2026-10-28T19:00:00.000Z', promos: ['winter_freeze'] } });
    expect(calls[3]?.body).toMatchObject({ key: { code: 'drop_71_halloween' } });
    expect(screen.getByRole('button', { name: 'Schedule drop' })).toBeTruthy();
    expect(calls.some(call => call.path.endsWith('/schedule'))).toBe(false);
    client.clear();
  });
  it('saves edited dates before scheduling, shows busy feedback, and avoids refreshing unrelated catalogues', async () => {
    const calls: string[] = [];
    const drop: ScheduleReply['drops'][number] = { id: 42, campaignId: 2, name: 'Team 1', startsAt: '2026-10-07T18:00:00Z', endsAt: '2026-10-14T18:00:00Z', status: 'draft', inheritEnd: false, version: 3, changesetId: 4, changes: 1, error: null, publishedAt: null, items: [] };
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [{ id: 2, name: 'Halloween', startsAt: drop.startsAt, endsAt: '2026-10-28T19:00:00Z', version: 1 }], drops: [drop], legacy: [] };
    let finishSave!: () => void;
    let finishSchedule!: () => void;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.replace('/panel/api/', ''); calls.push(`${init?.method ?? 'GET'} ${path}`);
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });
      if (path === 'content/drops/42' && init?.method === 'PUT') {
        const body = JSON.parse(String(init.body)); expect(body.version).toBe(data.drops[0]!.version);
        await new Promise<void>(resolve => { finishSave = resolve; });
        data.drops[0] = { ...data.drops[0]!, ...body, version: body.version + 1 }; return json(data.drops[0]);
      }
      if (path === 'content/drops/42/schedule') {
        expect(JSON.parse(String(init?.body)).version).toBe(5);
        await new Promise<void>(resolve => { finishSchedule = resolve; });
        data.drops[0] = { ...data.drops[0]!, status: 'scheduled', version: 5 }; return json(data.drops[0]);
      }
      if (path === 'content/schedule') return json(data);
      if (path.startsWith('promos')) return json({ definitions: [] });
      if (path === 'packs/catalog') return json({ packs: [] });
      if (path === 'sbc') return json({ sets: [] });
      if (path === 'objectives') return json({ settingsFile: 'objectives', rules: { welcomeGroup: { id: 1, title: 'Welcome' }, grindGroups: [] } });
      return json({ changeset: null });
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
    fireEvent.click(await screen.findByRole('button', { name: 'Today' }));
    fireEvent.click(screen.getByRole('button', { name: 'Review drop' }));
    await waitFor(() => expect(calls).toContain('GET objectives'));
    const dialog = within(screen.getByRole('dialog'));
    fireEvent.change(dialog.getByLabelText('End date'), { target: { value: '2026-10-15' } });
    fireEvent.click(dialog.getByRole('button', { name: 'Save drop' }));
    const saving = await dialog.findByRole('button', { name: 'Saving…' });
    expect(saving.getAttribute('aria-busy')).toBe('true');
    finishSave();
    await dialog.findByRole('button', { name: 'Save drop' });
    fireEvent.change(dialog.getByLabelText('Drop name'), { target: { value: 'Team 1 revised' } });
    fireEvent.click(dialog.getByRole('button', { name: 'Schedule drop' }));
    const scheduling = await dialog.findByRole('button', { name: 'Scheduling…' });
    expect(scheduling.getAttribute('aria-busy')).toBe('true');
    // The second metadata save uses the refreshed version.
    await waitFor(() => expect(calls.filter(call => call === 'PUT content/drops/42')).toHaveLength(2));
    finishSave();
    await waitFor(() => expect(calls).toContain('POST content/drops/42/schedule'));
    finishSchedule();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    for (const path of ['packs/catalog', 'sbc', 'objectives']) expect(calls.filter(call => call === `GET ${path}`)).toHaveLength(1);
    client.clear();
  });
  it('opens standalone rotations from the calendar and separates them from campaign promo choices', async () => {
    vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() })));
    const drop: ScheduleReply['drops'][number] = { id: 42, campaignId: 2, name: 'Team 1', startsAt: '2026-10-07T18:00:00Z', endsAt: '2026-10-14T18:00:00Z', status: 'draft', inheritEnd: false, version: 1, changesetId: 4, changes: 0, error: null, publishedAt: null, items: [] };
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [{ id: 2, name: 'Halloween', startsAt: drop.startsAt, endsAt: drop.endsAt, version: 1 }], drops: [drop], legacy: [{ kind: 'icon_rotation', code: 'icons', name: 'Base Icons rotation', startsAt: drop.startsAt, endsAt: drop.endsAt!, enabled: true }] };
    const definitions = ['gold_rare', 'base_icons', 'totw_1', 'custom_informs', 'winter_freeze'].map(code => ({ code, name: code, category: code === 'custom_informs' ? 'totw' : code, status: 'active', cards: 20, buckets: 1, artwork: [] }));
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      const path = url.replace('/panel/api/', '');
      const body = path === 'content/schedule' ? data
        : path.startsWith('content/changeset') ? { changeset: null }
        : path.startsWith('promos/availability') ? { at: data.now, iconPromo: 'base_icons' }
        : path.startsWith('promos') ? { definitions }
        : path.startsWith('icon-rotations/icons') ? { definition: { name: 'Base Icons rotation', promo: 'base_icons', starts_at: drop.startsAt, ends_at: drop.endsAt, enabled: true } }
        : path.startsWith('icon-rotations') ? { definitions: [{ code: 'icons', name: 'Base Icons rotation', promo: 'base_icons', starts_at: drop.startsAt, ends_at: drop.endsAt, enabled: true }] }
        : path.startsWith('promo-events') ? { definitions: [] }
        : path === 'packs/catalog' ? { packs: [] }
        : path === 'sbc' ? { sets: [] }
        : path === 'objectives' ? { settingsFile: 'rules/objectives.json', rules: { welcomeGroup: { id: 1, title: 'Welcome' }, grindGroups: [] } }
        : { available: false, frames: [] };
      return new Response(JSON.stringify(body), { status: 200 });
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
    fireEvent.click(await screen.findByRole('button', { name: 'Today' }));
    fireEvent.click(screen.getByRole('button', { name: 'Review drop' }));
    const dropDialog = within(screen.getByRole('dialog'));
    await dropDialog.findByRole('option', { name: 'winter_freeze' });
    for (const code of ['gold_rare', 'base_icons', 'totw_1', 'custom_informs']) expect(dropDialog.queryByRole('option', { name: code })).toBeNull();
    expect(dropDialog.queryByRole('button', { name: 'Use this drop in editors' })).toBeNull();
    fireEvent.click(dropDialog.getByRole('button', { name: 'Close' }));
    fireEvent.click(screen.getByRole('button', { name: 'Edit rotation' }));
    await screen.findByRole('heading', { name: 'Standard pool', hidden: true });
    expect(await screen.findByLabelText('Icon tier')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Campaigns' })).toBeNull();
    client.clear();
  });
  it('creates a campaign using only start and end dates', async () => {
    const calls: { path: string; body: unknown }[] = [];
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [], drops: [], legacy: [] };
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method === 'POST') {
        const body = JSON.parse(String(init.body));
        calls.push({ path: url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, ''), body });
        const saved = { ...body, id: 2, version: 1 }; data.campaigns.push(saved);
        return new Response(JSON.stringify(saved), { status: 200 });
      }
      return new Response(JSON.stringify(data), { status: 200 });
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
    fireEvent.click(await screen.findByRole('button', { name: 'Today' }));
    fireEvent.click(screen.getByRole('button', { name: 'New campaign' }));
    const dialog = within(screen.getByRole('dialog'));
    expect((dialog.getByLabelText('Start date') as HTMLInputElement).type).toBe('date');
    expect((dialog.getByLabelText('End date') as HTMLInputElement).type).toBe('date');
    fireEvent.change(dialog.getByLabelText('Name'), { target: { value: 'Halloween' } });
    fireEvent.change(dialog.getByLabelText('Start date'), { target: { value: '2026-10-01' } });
    fireEvent.change(dialog.getByLabelText('End date'), { target: { value: '2026-10-28' } });
    fireEvent.click(dialog.getByRole('button', { name: 'Save campaign' }));
    await waitFor(() => expect(calls).toEqual([{ path: 'content/campaigns', body: { name: 'Halloween', startsAt: '2026-10-01T18:00:00.000Z', endsAt: '2026-10-28T19:00:00.000Z' } }]));
    client.clear();
  });
  it('asks to complete dates on a legacy campaign before adding a drop', async () => {
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [{ id: 2, name: 'Legacy campaign', startsAt: null, endsAt: null, version: 1 }], drops: [], legacy: [] };
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(data), { status: 200 })));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
    expect(await screen.findByText('Needs start and end dates')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Add drop' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Edit campaign' }));
    expect((screen.getByLabelText('Start date') as HTMLInputElement).value).toBe('');
    expect((screen.getByRole('button', { name: 'Save campaign' }) as HTMLButtonElement).disabled).toBe(true);
    client.clear();
  });
  it('attaches a staged new pack and requires a valid item expiry or inherited drop expiry', async () => {
    const calls: { path: string; body: unknown }[] = [];
    const drop: ScheduleReply['drops'][number] = { id: 42, campaignId: 2, name: 'Team 1', startsAt: '2026-10-07T18:00:00Z', endsAt: '2026-10-14T18:00:00Z', status: 'draft', inheritEnd: true, version: 3, changesetId: 4, changes: 1, error: null, publishedAt: null, items: [{ id: 7, area: 'content_window', operation: 'update', key: { kind: 'store', target: '1' }, endsAt: '2026-10-14T18:00:00Z', inheritEnd: true }] };
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [{ id: 2, name: 'Halloween', startsAt: '2026-10-07T18:00:00Z', endsAt: '2026-10-14T18:00:00Z', version: 1 }], drops: [drop], legacy: [] };
    const pending = { changeset: { changes: [{ id: 8, area: 'store_pack', key: { packId: 9 }, operation: 'insert', after: { pack: { name: 'New promo pack', size: 12 } } }] } };
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
      let body: unknown = {};
      if (init?.method && init.method !== 'GET') {
        const saved = JSON.parse(String(init.body)); calls.push({ path, body: saved });
        if (path === 'content/drops/42/items/7/end') {
          drop.version++;
          drop.items[0]!.inheritEnd = Boolean(saved.inheritEnd);
          drop.items[0]!.endsAt = saved.inheritEnd ? drop.endsAt : saved.endsAt;
        }
      }
      else if (path === 'content/schedule') body = data;
      else if (path === 'content/changeset?dropId=42') body = pending;
      else if (path === 'packs/catalog') body = { packs: [] };
      else if (path === 'sbc') body = { sets: [] };
      else if (path === 'objectives') body = { settingsFile: 'rules/objectives.json', rules: { welcomeGroup: { id: 1, title: 'Welcome' }, grindGroups: [] } };
      else if (path.startsWith('promos')) body = { definitions: [] };
      return new Response(JSON.stringify(body), { status: 200 });
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
    fireEvent.click(await screen.findByRole('button', { name: 'Today' }));
    fireEvent.click(screen.getByRole('button', { name: 'Review drop' }));
    const dialog = within(screen.getByRole('dialog'));
    expect((dialog.getByLabelText('Start date') as HTMLInputElement).value).toBe('2026-10-07');
    expect(dialog.queryByRole('button', { name: 'Validate saved draft' })).toBeNull();
    expect(dialog.queryByRole('button', { name: 'Publish saved draft now' })).toBeNull();
    fireEvent.change(dialog.getByLabelText('Content kind'), { target: { value: 'store' } });
    await dialog.findByRole('option', { name: 'New promo pack' });
    fireEvent.change(dialog.getByLabelText('Content'), { target: { value: '9' } });
    fireEvent.click(dialog.getByRole('button', { name: 'Add to drop' }));
    await waitFor(() => expect(calls[0]).toEqual({ path: 'content/changeset/changes?dropId=42', body: { area: 'content_window', key: { kind: 'store', target: '9' }, operation: 'update', after: { enabled: true } } }));
    fireEvent.change(dialog.getByLabelText('Item end date'), { target: { value: '' } });
    expect(dialog.getByRole('button', { name: 'Save expiry' })).toHaveProperty('disabled', true);
    fireEvent.change(dialog.getByLabelText('Item end date'), { target: { value: '2026-10-13' } });
    fireEvent.click(dialog.getByRole('button', { name: 'Save expiry' }));
    await waitFor(() => expect(calls[1]).toEqual({ path: 'content/drops/42/items/7/end', body: { version: 3, endsAt: '2026-10-13T18:00:00.000Z' } }));
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Use drop expiry' })).toHaveProperty('disabled', false));
    fireEvent.click(dialog.getByRole('button', { name: 'Use drop expiry' }));
    await waitFor(() => expect(calls[2]).toEqual({ path: 'content/drops/42/items/7/end', body: { version: 4, inheritEnd: true } }));
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Use drop expiry' })).toHaveProperty('disabled', true));
    fireEvent.click(dialog.getByRole('button', { name: 'Schedule drop' }));
    await waitFor(() => expect(calls[3]).toEqual({ path: 'content/drops/42/schedule', body: { version: 5 } }));
    expect(calls.some(call => call.path.endsWith('/validate'))).toBe(false);
    client.clear();
  });
  it('adds a draft inside a campaign with its date defaults and does not schedule it implicitly', async () => {
    const calls: { path: string; body: unknown }[] = [];
    const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [{ id: 2, name: 'Halloween', startsAt: '2026-10-01T18:00:00Z', endsAt: '2026-10-28T19:00:00Z', version: 1 }], drops: [], legacy: [] };
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
      if (init?.method === 'POST') {
        const body = JSON.parse(String(init.body)); calls.push({ path, body });
        const saved: ScheduleReply['drops'][number] = { ...body, id: 72, status: 'draft', version: 1, changesetId: 10, changes: 0, error: null, publishedAt: null, items: [] };
        data.drops.push(saved);
        return new Response(JSON.stringify(saved), { status: 200 });
      }
      const body = path === 'content/schedule' ? data
        : path === 'objectives' ? { settingsFile: 'rules/objectives.json', rules: { welcomeGroup: { id: 1, title: 'Welcome' }, grindGroups: [] } }
        : path === 'packs/catalog' ? { packs: [] }
        : path === 'sbc' ? { sets: [] }
        : path.startsWith('promos') ? { definitions: [] }
        : { changeset: null };
      return new Response(JSON.stringify(body), { status: 200 });
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
    fireEvent.click(await screen.findByRole('button', { name: 'Today' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add drop' }));
    expect((screen.getByLabelText('Start date') as HTMLInputElement).value).toBe('2026-10-01');
    expect((screen.getByLabelText('End date') as HTMLInputElement).value).toBe('2026-10-28');
    fireEvent.change(screen.getByLabelText('Drop name'), { target: { value: 'Team 1' } });
    fireEvent.change(screen.getByLabelText('Start date'), { target: { value: '2026-09-30' } });
    expect((screen.getByRole('button', { name: 'Save drop' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText('The drop must stay within its campaign dates.')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Start date'), { target: { value: '2026-10-01' } });
    fireEvent.change(screen.getByLabelText('End date'), { target: { value: '2026-10-01' } });
    expect((screen.getByRole('button', { name: 'Save drop' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Start date'), { target: { value: '2026-10-07' } });
    fireEvent.change(screen.getByLabelText('End date'), { target: { value: '2026-10-14' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save drop' }));
    await waitFor(() => expect(calls).toEqual([{ path: 'content/drops', body: { campaignId: 2, name: 'Team 1', startsAt: '2026-10-07T18:00:00.000Z', endsAt: '2026-10-14T18:00:00.000Z', inheritEnd: false } }]));
    await screen.findByRole('button', { name: 'Schedule drop' });
    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(calls.some(call => call.path.endsWith('/schedule'))).toBe(false);
    client.clear();
  });
  it('shows draft starts and expiries as drafts alongside legacy windows', () => {
    const data: ScheduleReply = { now: '2026-10-07T18:00:00Z', campaigns: [], drops: [{ id: 1, campaignId: 2, name: 'Team 1', startsAt: '2026-10-07T18:00:00Z', endsAt: '2026-10-14T18:00:00Z', status: 'draft', inheritEnd: true, version: 1, changesetId: 4, changes: 1, error: null, publishedAt: null, items: [{ id: 7, area: 'promo_event', operation: 'insert', key: { code: 'halloween' }, endsAt: null, inheritEnd: true }] }], legacy: [{ kind: 'season', code: 'season_1', name: 'Season 1', startsAt: '2026-10-01T18:00:00Z', endsAt: '2026-10-07T18:00:00Z', enabled: true }] };
    const entries = agendaEntries(data);
    expect(entries).toHaveLength(4);
    expect(entries.filter(entry => entry.drop).map(entry => [entry.status, entry.end])).toEqual([['draft', false], ['draft', true]]);
    expect(entries.filter(entry => entry.legacy).every(entry => entry.status === 'ended')).toBe(true);
    expect(entries.find(entry => entry.drop)?.types).toEqual(['Promos']);
  });
});
