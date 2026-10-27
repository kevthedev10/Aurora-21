import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { mergeSquadBattles, type SquadBattlesMode } from '../../api/squadBattlesMode';
import { ToastProvider } from '../../ui/Toast';
import { ModesPage } from './ModesPage';
import { topSlots } from './SquadBattlesTab';

// Shaped like OperatorPanel.Content.cs SquadBattlesModeViewAsync and OperatorPanel.PackCatalog.cs replies.
const reward = (coins: number, packId = 300) => ({ coins, fcPoints: 0, xp: 0, lines: [{ packId, count: 1, untradeable: false }] });
const slot = (tierId: string, rankStart: number, rankEnd: number, coins: number) => ({ tierId, name: rankStart === rankEnd ? `Rank ${rankStart}` : `Rank ${rankStart}-${rankEnd}`,
  kind: 'ranked' as const, rankStart, rankEnd, tierLevel: 0, percentStart: 0, percentEnd: 0, minPoints: 0, reward: reward(coins) });
const tier = (tierId: string, name: string, tierLevel: number, percentStart: number, percentEnd: number, minPoints: number) => ({
  tierId, name, kind: 'points' as const, rankStart: 0, rankEnd: 0, tierLevel, percentStart, percentEnd, minPoints, reward: reward(1000 * (13 - tierLevel)) });
const mode: SquadBattlesMode = {
  area: 'squad_battles_tier', settingsFile: 'sqbt_rewards', boardFile: 'squad_battles', boardSize: 25, boardMinimum: 6, boardMaximum: 200, maxLineCount: 100,
  settings: { mode: 'percentile', minPointsToQualify: 1, rankedEnabled: true, rankedMinParticipants: 2 },
  totwFile: 'sqbt_totw',
  totw: { current: 35, weeks: [{ week: 34, released: '2021-05-19', headline: 'Messi 93' }, { week: 35, released: '2021-05-26', headline: 'Benzema 91' }] },
  tiers: [slot('rank1', 1, 1, 60000), slot('top20', 2, 5, 50000), slot('top40', 6, 10, 45000), slot('top100', 11, 15, 40000), slot('ranked5', 16, 20, 40000), slot('top200', 21, 25, 35000),
    tier('elite1', 'Elite 1', 1, 1, 3, 15000), tier('elite2', 'Elite 2', 2, 4, 6, 12000)],
};
const catalog = { revision: 'r1', groups: [], publicPackIds: [], promos: [], pickLimits: { minChoices: 2, maxChoices: 5, maxTokensPerGroup: 3 }, oddsTemplate: { icon: 0, pickTotw: 0 }, vanityTypes: [],
  packs: [{ id: 300, description: 'Gold Pack', inStore: false, usedBy: [], config: { name: 'Gold Pack' } }, { id: 405, description: 'Ultimate Pack', inStore: false, usedBy: [], config: { name: 'Ultimate Pack' } }] };
let calls: string[] = [];
let pending: unknown[] = [];
beforeEach(() => {
  calls = [];
  pending = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') calls.push(`${method} ${path} ${String(init?.body ?? '')}`);
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    if (path === 'content/modes/squad-battles') return json(mode);
    if (path === 'packs/catalog') return json(catalog);
    if (path === 'content/changeset') return json({ open: pending.length > 0, pending: pending.length, changeset: pending.length ? { id: 3, changes: pending } : null });
    if (path.startsWith('content/rules/squad_battles?path=')) return json({ file: 'squad_battles', definition: { value: 25 } });
    if (path.startsWith('content/rules/sqbt_totw?path=')) return json({ file: 'sqbt_totw', definition: { value: 35 } });
    return json({});
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider><ModesPage rest={['squad-battles']} /></ToastProvider></QueryClientProvider>);
};
const bodyOf = (call: string) => JSON.parse(call.replace('PUT content/changeset/changes ', '')) as { area: string; key: unknown; after: Record<string, unknown> };

describe('Squad Battles helpers', () => {
  it('places six Top slots as the server does', () => {
    expect(topSlots(25)).toEqual([[1, 1], [2, 5], [6, 10], [11, 15], [16, 20], [21, 25]]);
    expect(topSlots(6)).toEqual([[1, 1], [2, 2], [3, 3], [4, 4], [5, 5], [6, 6]]);
    expect(topSlots(50)).toEqual([[1, 1], [2, 10], [11, 20], [21, 30], [31, 40], [41, 50]]);
  });

  it('lays a staged tier over the published one', () => {
    const merged = mergeSquadBattles(mode, { open: true, pending: 1, changeset: { id: 1, changes: [
      { id: 1, area: 'squad_battles_tier', key: { tierId: 'elite2' }, operation: 'update', before: null, after: { kind: 'points', rankStart: 0, rankEnd: 0, tierLevel: 2, percentStart: 4, percentEnd: 6, minPoints: 11000, reward: reward(11000) } },
    ] } } as never);
    expect(merged.tiers[7]).toMatchObject({ tierId: 'elite2', name: 'Elite 2', minPoints: 11000, pending: true });
  });
});

describe('Squad Battles tab', () => {
  it('stages a points tier with its band, minimum and reward', async () => {
    renderPage();
    const elite = (await screen.findByText('Elite 2', { selector: 'strong' })).closest('li')!;
    expect(within(elite).getByText('Top 4–6% · at least 12,000 points')).toBeTruthy();
    fireEvent.click(within(elite).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Minimum points'), { target: { value: '11000' } });
    fireEvent.change(within(dialog).getByLabelText('Coins'), { target: { value: '12000' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save tier' }));
    await waitFor(() => expect(calls).toHaveLength(1));
    const body = bodyOf(calls[0]!);
    expect([body.area, body.key]).toEqual(['squad_battles_tier', { tierId: 'elite2' }]);
    expect(body.after).toEqual({ kind: 'points', rankStart: 0, rankEnd: 0, tierLevel: 2, percentStart: 4, percentEnd: 6, minPoints: 11000, reward: reward(12000) });
  });

  it('edits a Top slot\'s reward only; its places follow the Top players number', async () => {
    renderPage();
    expect(await screen.findByText('Top 25', { selector: 'h2, h3, strong, span, div' })).toBeTruthy();
    const slotRow = screen.getByText('Rank 2–5', { selector: 'strong' }).closest('li')!;
    fireEvent.click(within(slotRow).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).queryByLabelText('From percent')).toBeNull();
    expect(within(dialog).getByText(/Leaderboard places 2–5, set by the Top players number/)).toBeTruthy();
    fireEvent.change(within(dialog).getByLabelText('Coins'), { target: { value: '55000' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save tier' }));
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(bodyOf(calls[0]!).after).toMatchObject({ kind: 'ranked', rankStart: 2, rankEnd: 5, reward: { coins: 55000 } });
  });

  it('stages the Top players number with a minimum of six, and shows the slots it would give', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Change Squad Battles Top players' }));
    const dialog = await screen.findByRole('dialog');
    const input = within(dialog).getByLabelText('Number of top players');
    fireEvent.change(input, { target: { value: '5' } });
    expect((within(dialog).getByRole('button', { name: 'Save Top players' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(input, { target: { value: '50' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save Top players' }));
    await waitFor(() => expect(calls).toEqual(['PUT content/changeset/changes {"area":"rules","key":{"file":"squad_battles","path":["competition","boardSize"]},"operation":"update","after":{"value":50}}']));
  });

  it('previews the slots for a staged Top players number', async () => {
    pending = [{ id: 5, area: 'rules', key: { file: 'squad_battles', path: ['competition', 'boardSize'] }, operation: 'update', before: { value: 25 }, after: { value: 50 } }];
    renderPage();
    expect(await screen.findByText('Rank 41–50', { selector: 'strong' })).toBeTruthy();
    expect(screen.getByText('Top 50', { selector: 'h2, h3, strong, span, div' })).toBeTruthy();
  });

  it('stages the active Team of the Week, newest first, with an off option', async () => {
    renderPage();
    const select = await screen.findByLabelText('Active Team of the Week') as HTMLSelectElement;
    expect(select.value).toBe('35');
    expect([...select.options].map((option) => option.textContent)).toEqual(['Off (no TOTW tile)', 'TOTW 35 · 2021-05-26 · Benzema 91', 'TOTW 34 · 2021-05-19 · Messi 93']);
    fireEvent.change(select, { target: { value: '34' } });
    await waitFor(() => expect(calls).toEqual(['PUT content/changeset/changes {"area":"rules","key":{"file":"sqbt_totw","path":["current"]},"operation":"update","after":{"value":34}}']));
  });

  it('shows a staged Team of the Week', async () => {
    pending = [{ id: 6, area: 'rules', key: { file: 'sqbt_totw', path: ['current'] }, operation: 'update', before: { value: 35 }, after: { value: 34 } }];
    renderPage();
    const select = await screen.findByLabelText('Active Team of the Week') as HTMLSelectElement;
    await waitFor(() => expect(select.value).toBe('34'));
  });
});
