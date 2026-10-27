import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { mergeRanks, previewTopRanks, rankRangeText, tableProblems, type ChampionsRank } from '../../api/modes';
import { rewardSummary } from '../../ui/RewardEditor';
import { ToastProvider } from '../../ui/Toast';
import { ModesPage } from './ModesPage';

// Shaped like OperatorPanel.Content.cs ChampionsModeView and OperatorPanel.PackCatalog.cs replies.
const rankOne: ChampionsRank = { tierLevel: 1, clientTier: 1, fifaName: 'Band 1', name: 'Rank 1', positions: [1, 1],
  reward: { coins: 150000, fcPoints: 2000, xp: 0, lines: [{ packId: 541, count: 1, untradeable: false }, { packId: 550, count: 5, untradeable: true }] } };
const eliteOne: ChampionsRank = { tierLevel: 5, clientTier: 1, fifaName: 'Elite 1', name: 'Elite 1', wins: [1, 2],
  reward: { coins: 75000, fcPoints: 2000, xp: 0, lines: [{ packId: 541, count: 2, untradeable: false }] } };
const bronze: ChampionsRank = { tierLevel: 6, clientTier: 2, fifaName: 'Elite 2', name: 'Bronze 3', wins: [0, 0],
  reward: { coins: 1500, fcPoints: 250, xp: 0, lines: [{ packId: 300, count: 1, untradeable: false }] } };
const mode = {
  area: 'champions_rank', settingsFile: 'champions_rewards', seasonsFile: 'seasons', maxWins: 2, leaderboardSize: 25, leaderboardMinimum: 6, leaderboardMaximum: 200, clientWinTiers: 12, clientTopBands: 6, maxTierLevel: 999, maxLineCount: 100,
  settings: { minMatchesToRank: 5, fcPointsCap: 2000 },
  ranks: [rankOne, eliteOne, bronze],
};
const catalog = { revision: 'r1', groups: [], publicPackIds: [], promos: [], pickLimits: { minChoices: 2, maxChoices: 5, maxTokensPerGroup: 3 }, oddsTemplate: { icon: 0, pickTotw: 0 }, vanityTypes: [],
  packs: [
    { id: 300, description: 'Gold Pack', inStore: false, usedBy: ['Champions: Bronze 3'], config: { name: 'Gold Pack' } },
    { id: 541, description: 'Premium TOTW Pack', inStore: false, usedBy: [], config: { name: 'Premium TOTW Pack' } },
    { id: 550, description: 'Red Player Pick', inStore: false, usedBy: [], config: { name: 'Red Player Pick', player_pick_count: 1 } },
  ] };
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
    if (path === 'content/modes/champions') return json(mode);
    if (path === 'packs/catalog') return json(catalog);
    if (path === 'content/changeset') return json({ open: pending.length > 0, pending: pending.length, changeset: pending.length ? { id: 3, changes: pending } : null });
    if (path.startsWith('content/rules/seasons?path=')) return json({ file: 'seasons', definition: { value: 25 } });
    if (path.startsWith('content/rules/champions_rewards?path=')) return json({ file: 'champions_rewards', definition: { value: 5 } });
    return json({});
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider><ModesPage rest={['champions']} /></ToastProvider></QueryClientProvider>);
};

describe('Champions reward helpers', () => {
  it('lays staged rank changes over the published ranks', () => {
    const rows = mergeRanks([rankOne, bronze], { open: true, pending: 3, changeset: { id: 1, changes: [
      { id: 1, area: 'champions_rank', key: { tierLevel: 6 }, operation: 'update', before: null, after: { ...bronze, tierLevel: undefined, name: 'Bronze 3b' } },
      { id: 2, area: 'champions_rank', key: { tierLevel: 1 }, operation: 'hide', before: null, after: null },
      { id: 3, area: 'champions_rank', key: { tierLevel: 7 }, operation: 'insert', before: null, after: { name: 'New', wins: [3, 3], reward: bronze.reward } },
      { id: 4, area: 'rules', key: { file: 'x', path: ['y'] }, operation: 'update', before: null, after: { value: 1 } },
    ] } } as never);
    expect(rows.map((row) => [row.tierLevel, row.name, row.pending ?? ''])).toEqual([[1, 'Rank 1', 'Remove'], [6, 'Bronze 3b', 'Edit'], [7, 'New', 'New']]);
    expect(rows[1]!.clientTier).toBe(2);
  });

  it('finds an incomplete table as publishing would', () => {
    const rows = mergeRanks(mode.ranks, undefined);
    expect(tableProblems(rows, 2, 12)).toEqual([]);
    expect(tableProblems(rows.filter((row) => row.tierLevel !== 6), 2, 12)).toEqual(['No rank covers 0 wins.']);
    expect(tableProblems([...rows, { ...bronze, tierLevel: 9, name: 'Extra' }], 2, 12)).toEqual(['More than one rank, or a rank beyond 2, covers 0 wins.']);
    expect(tableProblems(rows, 2, 1)).toEqual(['FIFA shows at most 1 ranks by wins; there are 2.']);
    expect(tableProblems([{ ...rankOne, positions: [2, 5] }, eliteOne, bronze], 2, 12)).toEqual(['Rank 1 should start at position 1.']);
    const bands = Array.from({ length: 7 }, (_, index) => ({ ...rankOne, tierLevel: 20 + index, positions: [index + 1, index + 1] as [number, number] }));
    expect(tableProblems([...bands, eliteOne, bronze], 2, 12, 6)).toEqual(['FIFA shows at most 6 leaderboard bands; there are 7.']);
  });

  it('shows a single place or win count without a range', () => {
    expect([rankRangeText({ positions: [1, 1] }), rankRangeText({ positions: [2, 5] }), rankRangeText({ wins: [1, 1] }),
      rankRangeText({ wins: [18, 18] }), rankRangeText({ wins: [0, 1] })]).toEqual(['Top 1', 'Top 2–5', '1 win', '18 wins', '0–1 wins']);
  });

  it('summarises a reward with pack names and tradeability', () => {
    expect(rewardSummary(rankOne.reward, (id) => (id === 550 ? 'Red Player Pick' : `Pack ${id}`)))
      .toBe('150,000 coins · 2,000 ACP · Pack 541 · 5× Red Player Pick (untradeable)');
  });
});

describe('Modes page', () => {
  it('shows the ranks with pack names, then stages an edited rank as one change', async () => {
    renderPage();
    const elite = (await screen.findByText('Elite 1', { selector: 'strong' })).closest('li')!;
    // FIFA names the tier itself; there is no way to add a rank.
    expect(within(elite).getByText('FIFA: Elite 1')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Add rank' })).toBeNull();
    expect(within(elite).getByText('75,000 coins · 2,000 ACP · 2× Premium TOTW Pack')).toBeTruthy();
    expect(screen.getByText('150,000 coins · 2,000 ACP · Premium TOTW Pack · 5× Red Player Pick (untradeable)')).toBeTruthy();
    fireEvent.click(within(elite).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Coins'), { target: { value: '80000' } });
    const line = within(dialog).getByRole('group', { name: 'Line 1' });
    fireEvent.change(within(line).getByLabelText('Tradeability'), { target: { value: 'untradeable' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add pack' }));
    fireEvent.change(within(within(dialog).getByRole('group', { name: 'Line 2' })).getByLabelText('Pack'), { target: { value: '300' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save rank' }));
    await waitFor(() => expect(calls).toEqual([`PUT content/changeset/changes ${JSON.stringify({
      area: 'champions_rank', key: { tierLevel: 5 }, operation: 'update',
      after: { name: 'Elite 1', wins: [1, 2], reward: { coins: 80000, fcPoints: 2000, xp: 0,
        lines: [{ packId: 541, count: 2, untradeable: true }, { packId: 300, count: 1, untradeable: false }] } },
    })}`]));
  });

  it('refuses a count out of range before staging', async () => {
    renderPage();
    const bronzeRow = (await screen.findByText('Bronze 3')).closest('li')!;
    fireEvent.click(within(bronzeRow).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(within(dialog).getByRole('group', { name: 'Line 1' })).getByLabelText('Count'), { target: { value: '101' } });
    expect((within(dialog).getByRole('button', { name: 'Save rank' }) as HTMLButtonElement).disabled).toBe(true);
    expect(within(dialog).getAllByText('Line 1: the count is 1 to 100.').length).toBeGreaterThan(0);
    expect(calls).toEqual([]);
  });

  it('marks a staged removal and warns that the wins are no longer covered', async () => {
    pending = [{ id: 8, area: 'champions_rank', key: { tierLevel: 6 }, operation: 'hide', before: bronze, after: null }];
    renderPage();
    expect(await screen.findByText('Staged removal')).toBeTruthy();
    expect(screen.getByText('No rank covers 0 wins.')).toBeTruthy();
    fireEvent.click(within(screen.getByText('Bronze 3').closest('li')!).getByRole('button', { name: 'Undo staged change' }));
    await waitFor(() => expect(calls).toEqual(['DELETE content/changeset/changes/8 ']));
  });

  it('checks the ranks against a staged number of matches', async () => {
    pending = [{ id: 9, area: 'rules', key: { file: 'seasons', path: ['weekendLeague', 'maxMatches'] }, operation: 'update', before: { value: 2 }, after: { value: 3 } }];
    renderPage();
    expect((await screen.findByLabelText('Matches per Weekend League') as HTMLInputElement).value).toBe('3');
    expect(screen.getByText('No rank covers 3 wins.')).toBeTruthy();
  });

  it('stages a setting as a champions_rewards rule', async () => {
    renderPage();
    const input = await screen.findByLabelText('Matches to rank');
    fireEvent.change(input, { target: { value: '6' } });
    fireEvent.click(within(input.closest('.mode-setting')! as HTMLElement).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls).toEqual([
      'PUT content/changeset/changes {"area":"rules","key":{"file":"champions_rewards","path":["minMatchesToRank"]},"operation":"update","after":{"value":6}}',
    ]));
  });
  it('stages Top players and rejects a number below six', async () => {
    renderPage();
    expect((await screen.findByLabelText('Top players') as HTMLInputElement).value).toBe('25');
    // A fixed Top size does not shrink with the field, so the band sizing fields are not offered.
    expect(screen.queryByLabelText('Band reference field')).toBeNull();
    expect(screen.queryByLabelText('Band minimum field')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Change Top players' }));
    const dialog = await screen.findByRole('dialog');
    const input = within(dialog).getByLabelText('Number of top players');
    expect(input.getAttribute('min')).toBe('6');
    fireEvent.change(input, { target: { value: '5' } });
    expect((within(dialog).getByRole('button', { name: 'Save Top players' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(input, { target: { value: '50' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save Top players' }));
    await waitFor(() => expect(calls).toEqual(['PUT content/changeset/changes {"area":"rules","key":{"file":"seasons","path":["weekendLeague","topX"]},"operation":"update","after":{"value":50}}']));
  });

  it('edits a Top slot\'s reward only: its name and places come from the Top size', async () => {
    renderPage();
    const slot = (await screen.findByText('Rank 1', { selector: 'strong' })).closest('li')!;
    fireEvent.click(within(slot).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).queryByLabelText('Name')).toBeNull();
    expect(within(dialog).queryByLabelText(/position|wins/i)).toBeNull();
    expect(within(dialog).getByText(/Leaderboard place 1, set by the Top size \(25\)/)).toBeTruthy();
    fireEvent.change(within(dialog).getByLabelText('Coins'), { target: { value: '160000' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save rank' }));
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(JSON.parse(calls[0]!.replace('PUT content/changeset/changes ', '')).after).toMatchObject({ name: 'Rank 1', positions: [1, 1], reward: { coins: 160000 } });
  });

  it('protects leaderboard slots while allowing win-rank removal', async () => {
    renderPage();
    const row = (await screen.findByText('Rank 1')).closest('li')!;
    expect(within(row).queryByRole('button', { name: 'Remove' })).toBeNull();
    expect(within(screen.getByText('Bronze 3').closest('li')!).getByRole('button', { name: 'Remove' })).toBeTruthy();
    fireEvent.click(within(row).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    // The places are set by the Top size: the editor has no position fields at all.
    expect(within(dialog).queryByLabelText('From position')).toBeNull();
    expect(within(dialog).queryByLabelText('To position')).toBeNull();
  });

  it('previews exactly six automatically resized slots and keeps rewards', () => {
    const bands = Array.from({ length: 6 }, (_, i) => ({ ...rankOne, tierLevel: i + 20, positions: [i + 1, i + 1] as [number, number] }));
    const preview = previewTopRanks(bands, 25);
    expect(preview.map((row) => row.positions)).toEqual([[1, 1], [2, 5], [6, 10], [11, 15], [16, 20], [21, 25]]);
    expect(previewTopRanks(bands, 6).map((row) => row.positions)).toEqual([[1, 1], [2, 2], [3, 3], [4, 4], [5, 5], [6, 6]]);
    expect(preview.map((row) => row.reward)).toEqual(bands.map((row) => row.reward));
  });

});
