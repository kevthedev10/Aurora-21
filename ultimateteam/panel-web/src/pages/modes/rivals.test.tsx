import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { mergeRivals } from '../../api/rivalsMode';
import { keyLabel } from '../../app/changes';
import { ToastProvider } from '../../ui/Toast';
import { ModesPage } from './ModesPage';

// Shaped like OperatorPanel.Content.cs RivalsModeViewAsync and OperatorPanel.PackCatalog.cs replies.
const coins = (value: number, fcPoints = 100) => ({ coins: value, fcPoints, xp: 0, lines: [] });
const packs = (fcPoints: number, untradeable: boolean) => ({ coins: 0, fcPoints, xp: 0, lines: [{ packId: 405, count: 1, untradeable, ...(untradeable && { source: 'rivals_option_c' }) }] });
const division = (number: number) => ({
  division: number,
  ranks: [1, 2, 3, 4, 5, 6].map((rank) => ({ rank, name: ['I', 'II', 'III', 'IV', 'V', 'VI'][rank - 1]!,
    row: { options: rank <= 4 ? [coins(10000 * (11 - number) - rank), packs(100, false), packs(100, true)] : [packs(50, false)] } })),
});
const mode = {
  area: 'rivals_rank', promotionArea: 'rivals_promotion', settingsFile: 'rivals_rewards', maxOptions: 4, maxLineCount: 100,
  settings: { win: 500, draw: 300, loss: 150, perGoal: 90, goalCap: 5, matchCap: 30, weekendLeagueCounts: true, weekendLeagueMatch: 400, rankShares: [20, 25, 25, 18, 9, 3], poolBelowClubs: 20 },
  divisions: Array.from({ length: 10 }, (_, index) => division(index + 1)),
  promotion: Array.from({ length: 10 }, (_, index) => ({ division: index + 1, reward: coins(1000 * (10 - index), 0) })),
};
const catalog = { revision: 'r1', groups: [], publicPackIds: [], promos: [], pickLimits: { minChoices: 2, maxChoices: 5, maxTokensPerGroup: 3 }, oddsTemplate: { icon: 0, pickTotw: 0 }, vanityTypes: [],
  packs: [{ id: 405, description: 'Ultimate Pack', inStore: false, usedBy: [], config: { name: 'Ultimate Pack' } }, { id: 300, description: 'Gold Pack', inStore: false, usedBy: [], config: { name: 'Gold Pack' } }] };
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
    if (path === 'content/modes/rivals') return json(mode);
    if (path === 'packs/catalog') return json(catalog);
    if (path === 'content/changeset') return json({ open: pending.length > 0, pending: pending.length, changeset: pending.length ? { id: 3, changes: pending } : null });
    if (path.startsWith('content/rules/rivals_rewards?path=')) {
      const key = JSON.parse(new URLSearchParams(path.split('?')[1]).get('path')!) as string[];
      const value = key[0] === 'rankShares' ? mode.settings.rankShares : key[1] === 'weekendLeagueCounts' ? true : key[1] === 'win' ? 500 : 0;
      return json({ file: 'rivals_rewards', definition: { value } });
    }
    return json({});
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider><ModesPage rest={['rivals']} /></ToastProvider></QueryClientProvider>);
};
const put = (body: unknown) => `PUT content/changeset/changes ${JSON.stringify(body)}`;

describe('Rivals helpers', () => {
  it('lays staged rank and promotion changes over the published tables', () => {
    const merged = mergeRivals(mode, { open: true, pending: 2, changeset: { id: 1, changes: [
      { id: 1, area: 'rivals_rank', key: { division: 2, rank: 5 }, operation: 'update', before: null, after: { options: [coins(1)] } },
      { id: 2, area: 'rivals_promotion', key: { division: 4 }, operation: 'update', before: null, after: { reward: coins(7) } },
    ] } } as never);
    expect(merged.divisions[1]!.ranks[4]).toMatchObject({ pending: true, options: [coins(1)] });
    expect(merged.divisions[0]!.ranks[4]!.pending).toBeUndefined();
    expect(merged.promotion[3]).toMatchObject({ pending: true, reward: coins(7) });
  });

  it('labels a Rivals change by division and rank', () => {
    expect(keyLabel({ division: 5, rank: 2 })).toBe('Division 5 › Rank II');
  });
});

describe('Rivals tab', () => {
  it('shows a division\'s ranks, then stages an added option for a rank as one change', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Division 3' }));
    const rank = screen.getByText('Rank I', { selector: 'strong' }).closest('li')!;
    expect(within(rank).getByText('20% of the division')).toBeTruthy();
    expect(within(rank).getByText('79,999 coins · 100 ACP')).toBeTruthy();
    fireEvent.click(within(rank).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add option' }));
    expect(within(dialog).queryByRole('button', { name: 'Add option' })).toBeNull();
    const fourth = within(dialog).getByText('Option 4').closest('fieldset')!;
    fireEvent.change(within(fourth).getByLabelText('Coins'), { target: { value: '5000' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save rank' }));
    await waitFor(() => expect(calls).toHaveLength(1));
    const body = JSON.parse(calls[0]!.replace('PUT content/changeset/changes ', '')) as { area: string; key: unknown; after: { options: unknown[] } };
    expect([body.area, body.key, body.after.options.length]).toEqual(['rivals_rank', { division: 3, rank: 1 }, 4]);
    expect(body.after.options[3]).toEqual(coins(5000, 0));
    // An untradeable option C line keeps its tradeability and source.
    expect(body.after.options[2]).toEqual(packs(100, true));
  });

  it('stages a promotion reward', async () => {
    renderPage();
    const promotion = (await screen.findByText('Division 4', { selector: 'strong' })).closest('li')!;
    fireEvent.click(within(promotion).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Coins'), { target: { value: '12000' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save reward' }));
    await waitFor(() => expect(calls).toEqual([put({ area: 'rivals_promotion', key: { division: 4 }, operation: 'update', after: { reward: coins(12000, 0) } })]));
  });

  it('stages the rank shares as one list only when they add up to 100', async () => {
    renderPage();
    const first = await screen.findByLabelText('Rank I');
    fireEvent.change(first, { target: { value: '21' } });
    expect((screen.getByRole('button', { name: 'Save shares' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/Total 101%/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Rank VI'), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save shares' }));
    await waitFor(() => expect(calls).toEqual([put({ area: 'rules', key: { file: 'rivals_rewards', path: ['rankShares'] }, operation: 'update', after: { value: [21, 25, 25, 18, 9, 2] } })]));
  });

  it('stages the Weekend League switch and a score setting as rules', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('switch', { name: /Weekend League matches count/ }));
    await waitFor(() => expect(calls).toEqual([put({ area: 'rules', key: { file: 'rivals_rewards', path: ['weeklyScore', 'weekendLeagueCounts'] }, operation: 'update', after: { value: false } })]));
    fireEvent.change(screen.getByLabelText('Win'), { target: { value: '600' } });
    fireEvent.click(within(screen.getByLabelText('Win').closest('.mode-setting') as HTMLElement).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls[1]).toBe(put({ area: 'rules', key: { file: 'rivals_rewards', path: ['weeklyScore', 'win'] }, operation: 'update', after: { value: 600 } })));
  });
});
