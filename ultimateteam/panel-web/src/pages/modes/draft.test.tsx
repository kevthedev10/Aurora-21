import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { chances, mergeDraft, type DraftMode, type DraftPickSettings } from '../../api/draftMode';
import { keyLabel } from '../../app/changes';
import { ToastProvider } from '../../ui/Toast';
import { ModesPage } from './ModesPage';

// Shaped like OperatorPanel.Content.cs DraftModeViewAsync and OperatorPanel.PackCatalog.cs replies.
const coins = (value: number) => ({ coins: value, fcPoints: 0, xp: 0, lines: [] });
const packs = (packId: number, count: number, untradeable: boolean) => ({ coins: 0, fcPoints: 0, xp: 0, lines: [{ packId, count, untradeable }] });
const level = (mode: 'online' | 'single', wins: number) => ({
  mode, wins, name: `${mode === 'online' ? 'Online' : 'Single-player'}, ${wins} ${wins === 1 ? 'win' : 'wins'}`,
  reward: coins((mode === 'online' ? 3000 : 1500) * (wins + 1)),
  choices: [{ weight: 1, reward: packs(74, 3, mode === 'single') }, { weight: 3, reward: coins(15000) }],
});
const mode: DraftMode = {
  area: 'draft_level', settingsFile: 'draft', settings: { entryCoins: 15000, entryPoints: 300 }, winsToComplete: 4, maxChoices: 50, maxWeight: 1000, maxLineCount: 100,
  pickSettings: { choicesPerScreen: 5, minRating: 75, strongFloor: 82, captainStrongFloor: 86, managerPoolSize: 100,
    specialBias: 0.04, weakRoundSpecialBias: 0.01, elite99Chance: 0, elite99RoundCounts: [4, 5], weakRoundCounts: [2, 3],
    categoryWeights: { icon: 0.014, totw: 0.04 }, weakRoundCategoryWeights: { icon: 0, totw: 0.01 },
    goldRareBucketWeights: { rating_75_79: 1, rating_82_83: 30 }, weakRoundGoldRareBucketWeights: { rating_75_79: 25, rating_82_83: 25 } },
  pickOptions: { categories: ['icon', 'totw', 'gold_rare'], goldRareGroups: ['rating_75_79', 'rating_82_83', 'rating_84_85'] },
  levels: [0, 1, 2, 3, 4].flatMap((wins) => [level('online', wins), level('single', wins)]),
};
const catalog = { revision: 'r1', groups: [], publicPackIds: [], promos: [], pickLimits: { minChoices: 2, maxChoices: 5, maxTokensPerGroup: 3 }, oddsTemplate: { icon: 0, pickTotw: 0 }, vanityTypes: [],
  packs: [{ id: 74, description: 'Gold Pack', inStore: true, usedBy: [], config: { name: 'Gold Pack' } }, { id: 305, description: 'Premium Gold Players Pack', inStore: false, usedBy: [], config: { name: 'Premium Gold Players Pack' } }] };
let calls: string[] = [];
let pending: unknown[] = [];
let livePicks: DraftPickSettings;
beforeEach(() => {
  calls = [];
  pending = [];
  livePicks = structuredClone(mode.pickSettings!);
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') calls.push(`${method} ${path} ${String(init?.body ?? '')}`);
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    if (path === 'content/modes/draft') return json(mode);
    if (path === 'packs/catalog') return json(catalog);
    if (path === 'content/changeset') return json({ open: pending.length > 0, pending: pending.length, changeset: pending.length ? { id: 3, changes: pending } : null });
    if (path.startsWith('content/rules/draft?path=')) {
      const key = JSON.parse(new URLSearchParams(path.split('?')[1]).get('path')!);
      return json({ file: 'draft', definition: { value: key[0] === 'picks' ? livePicks[key[1] as keyof DraftPickSettings] : 15000 } });
    }
    return json({});
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider><ModesPage rest={['draft']} /></ToastProvider></QueryClientProvider>);
};
const bodyOf = (call: string) => JSON.parse(call.replace('PUT content/changeset/changes ', '')) as { area: string; key: unknown; after: Record<string, unknown> };

describe('Draft helpers', () => {
  it('gives each choice its share of the weights', () => {
    expect(chances([{ weight: 1, reward: coins(0) }, { weight: 3, reward: coins(0) }])).toEqual([25, 75]);
    expect(chances([])).toEqual([]);
  });

  it('lays a staged level over the published one and labels its key', () => {
    const merged = mergeDraft(mode, { open: true, pending: 1, changeset: { id: 1, changes: [
      { id: 1, area: 'draft_level', key: { mode: 'single', wins: 2 }, operation: 'update', before: null, after: { reward: coins(1), choices: [{ weight: 2, reward: coins(2) }] } },
    ] } } as never);
    expect(merged.levels.find((entry) => entry.mode === 'single' && entry.wins === 2)).toMatchObject({ pending: true, reward: coins(1), choices: [{ weight: 2 }] });
    expect(merged.levels.find((entry) => entry.mode === 'online' && entry.wins === 2)!.pending).toBeUndefined();
    expect(keyLabel({ mode: 'single', wins: 1 })).toBe('Single-player › 1 win');
  });
});

describe('Draft tab', () => {
  const showPicks = async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /^Pick settings/ }));
    await screen.findByLabelText('Minimum player rating');
  };

  it('groups active pick settings and stages category weights without changing weak rounds', async () => {
    await showPicks();
    expect(screen.queryByRole('heading', { name: 'Run rewards' })).toBeNull();
    expect((screen.getByLabelText('Minimum player rating') as HTMLInputElement).value).toBe('75');
    fireEvent.click(screen.getByRole('button', { name: 'Weights' }));
    const input = screen.getByLabelText('Category overrides: icon, normal rounds');
    expect((screen.getByLabelText('Category overrides: icon, weak rounds') as HTMLInputElement).value).toBe('0');
    fireEvent.change(input, { target: { value: '0.02' } });
    fireEvent.click(within(input.closest('td')!).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(bodyOf(calls[0]!)).toMatchObject({ area: 'rules', key: { file: 'draft', path: ['picks', 'categoryWeights'] },
      after: { value: { icon: 0.02, totw: 0.04 } } });
  });

  it('adds a saved rating group to the custom weights', async () => {
    await showPicks();
    fireEvent.click(screen.getByRole('button', { name: 'Weights' }));
    fireEvent.change(screen.getByLabelText('Add gold rare rating groups'), { target: { value: 'rating_84_85' } });
    const input = screen.getByLabelText('Gold Rare rating groups: 84–85, normal rounds');
    fireEvent.change(input, { target: { value: '35' } });
    fireEvent.click(within(input.closest('td')!).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(bodyOf(calls[0]!).after).toEqual({ value: { rating_75_79: 1, rating_82_83: 30, rating_84_85: 35 } });
  });

  it('keeps unsaved pick edits when switching groups and Draft views', async () => {
    await showPicks();
    fireEvent.change(screen.getByLabelText('Minimum player rating'), { target: { value: '80' } });
    fireEvent.click(screen.getByRole('button', { name: 'Weights' }));
    fireEvent.click(screen.getByRole('button', { name: 'Basics' }));
    fireEvent.click(screen.getByRole('button', { name: 'Rewards' }));
    fireEvent.click(screen.getByRole('button', { name: /^Pick settings/ }));
    expect((screen.getByLabelText('Minimum player rating') as HTMLInputElement).value).toBe('80');
    expect(calls).toEqual([]);
  });

  it('validates round counts and converts the run percentage to a probability', async () => {
    await showPicks();
    fireEvent.click(screen.getByRole('button', { name: 'Special rounds' }));
    const counts = screen.getByLabelText('Possible weak round counts');
    fireEvent.change(counts, { target: { value: '2, 24' } });
    expect((within(counts.closest('.mode-setting')!).getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true);
    const chance = screen.getByLabelText('Runs with 99-rated rounds (%)');
    fireEvent.change(chance, { target: { value: '45' } });
    fireEvent.click(within(chance.closest('.mode-setting')!).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(bodyOf(calls[0]!)).toMatchObject({ key: { file: 'draft', path: ['picks', 'elite99Chance'] }, after: { value: 0.45 } });
  });

  it('shows and undoes staged pick settings', async () => {
    pending = [{ id: 12, area: 'rules', key: { file: 'draft', path: ['picks', 'minRating'] }, operation: 'update', after: { value: 80 } }];
    await showPicks();
    const input = screen.getByLabelText('Minimum player rating');
    expect((input as HTMLInputElement).value).toBe('80');
    fireEvent.click(within(input.closest('.mode-setting')!).getByRole('button', { name: 'Undo' }));
    await waitFor(() => expect(calls[0]).toBe('DELETE content/changeset/changes/12 '));
  });

  it('refuses to overwrite pick settings changed while the page was open', async () => {
    await showPicks();
    const input = screen.getByLabelText('Minimum player rating');
    fireEvent.change(input, { target: { value: '80' } });
    livePicks.minRating = 76;
    fireEvent.click(within(input.closest('.mode-setting')!).getByRole('button', { name: 'Save' }));
    await screen.findByText('This setting changed while the page was open. Reload it before saving.');
    expect(calls).toEqual([]);
  });

  it('shows each level\'s guaranteed reward and choice chances, per mode', async () => {
    renderPage();
    const list = await screen.findByRole('list', { name: '3 wins choices' });
    expect(within(list).getByText(/3× Gold Pack$/)).toBeTruthy();
    expect(within(list).getByText('25%:')).toBeTruthy();
    expect(screen.getAllByText('Every run: 12,000 coins').length).toBe(1);
    fireEvent.click(screen.getByRole('button', { name: 'Single-player' }));
    expect(await screen.findByText('Every run: 6,000 coins')).toBeTruthy();
    expect(screen.getAllByText(/3× Gold Pack \(untradeable\)/).length).toBe(5);
  });

  it('stages a level with a changed reward, a reweighted choice and an added one', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Edit Online, 2 wins' }));
    const dialog = await screen.findByRole('dialog');
    const every = within(dialog).getByText('Every run').closest('fieldset')!;
    fireEvent.change(within(every).getByLabelText('Coins'), { target: { value: '11000' } });
    const second = within(dialog).getByText(/^Choice 2/).closest('fieldset')!;
    fireEvent.change(within(second).getByLabelText(/Weight/), { target: { value: '1' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add choice' }));
    const third = within(dialog).getByText(/^Choice 3/).closest('fieldset')!;
    expect(within(third).getByText('(33% chance)')).toBeTruthy();
    fireEvent.change(within(third).getByLabelText('Coins'), { target: { value: '5000' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save level' }));
    await waitFor(() => expect(calls).toHaveLength(1));
    const body = bodyOf(calls[0]!);
    expect([body.area, body.key]).toEqual(['draft_level', { mode: 'online', wins: 2 }]);
    expect(body.after).toEqual({ reward: coins(11000), choices: [
      { weight: 1, reward: packs(74, 3, false) }, { weight: 1, reward: coins(15000) }, { weight: 1, reward: coins(5000) }] });
  });

  it('refuses a weight of 0 and stages the entry cost as a rule', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Edit Online, 0 wins' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(within(dialog).getByText(/^Choice 1/).closest('fieldset')!).getByLabelText(/Weight/), { target: { value: '0' } });
    expect((within(dialog).getByRole('button', { name: 'Save level' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    fireEvent.change(screen.getByLabelText(/Entry \(coins\)/), { target: { value: '20000' } });
    fireEvent.click(within(screen.getByLabelText(/Entry \(coins\)/).closest('.mode-setting') as HTMLElement).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(calls).toEqual(['PUT content/changeset/changes {"area":"rules","key":{"file":"draft","path":["entry","coins"]},"operation":"update","after":{"value":20000}}']));
  });
});
