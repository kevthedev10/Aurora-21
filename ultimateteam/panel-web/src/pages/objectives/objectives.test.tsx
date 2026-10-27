import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ObjectiveRulesDoc, ObjectivesReply } from '../../api/objectives';
import { ToastProvider } from '../../ui/Toast';
import { ObjectivesPage } from './ObjectivesPage';
import { fromLocal, toLocal } from './GroupEditor';

// Shaped like OperatorPanel.Objectives.cs ObjectivesAsync.
const rules: ObjectiveRulesDoc = {
  welcomeGroup: { id: 1, title: 'Welcome Objectives', subTitle: 'Get started', objective: { id: 1001, name: 'Kick Off', description: 'Play a match.' },
    rewardPackId: 513, rewardPackCount: 3, rewardUntradeable: true, xpAward: 1500, stat: 'matches', target: 1 },
  grindGroups: [{ id: 2, title: 'Matchday Grind', subTitle: 'Play', award: { pack: 403 },
    objectives: [{ id: 201, name: 'Five Matches', stat: 'matches', target: 5, award: { coins: 2000 }, xp: 2000 }] },
  { id: 4, title: 'Collector', subTitle: 'SBCs', award: { coins: 5000 }, objectives: [{ id: 401, name: 'Ten SBCs', stat: 'sbcs', target: 10, award: { coins: 1500 }, xp: 1500 }] }],
  gifts: [{ id: 90, title: 'Thank You', subTitle: 'A gift', objective: { id: 9001, name: 'Thanks', description: 'For testing.' }, award: { pack: 542, untradeable: true },
    availableFrom: null, availableUntil: '2026-10-04T23:59:59+01:00' }],
  seasonProgress: { levelCount: 3, xpPerLevel: 2500, rewardLevelCap: 2, levelRewards: [{ coins: 10000 }, { pack: 304 }, { coins: 5000 }] },
  campaign: { id: 1, name: 'Aurora 21 Season', startedDaysAgo: 30, endsInDays: 365 },
  statLabels: { matches: 'Play {n} matches in FUT.', sbcs: 'Complete {n} Squad Building Challenges.' },
};
const NOW = '2026-10-07T12:00:00Z';
it('edits objective dates on the UK clock and rejects invalid clock-change times', () => {
  expect(toLocal('2026-07-01T18:00:00Z')).toBe('2026-07-01T19:00');
  expect(fromLocal('2026-07-01T19:00')).toBe('2026-07-01T18:00:00Z');
  expect(fromLocal('2026-12-01T19:00')).toBe('2026-12-01T19:00:00Z');
  expect(() => fromLocal('2026-03-29T01:30')).toThrow('does not exist');
});
let seasons: ObjectiveRulesDoc['seasons'];
const reply = (live?: ObjectiveRulesDoc['live']): ObjectivesReply => ({
  settingsFile: 'objectives', rules: { ...rules, ...(live !== undefined && { live }), ...(seasons && { seasons }) }, liveGroupIds: [1, 2, 4, 90], stats: ['matches', 'sbcs'],
  seasons: { now: NOW, current: seasons?.find((season) => season.startsAt <= NOW && season.endsAt > NOW)?.id ?? 1,
    defaultId: 1, nextId: 3, played: [{ season: 1, clubs: 12, xp: 340000 }] },
  nextGroupId: 91, nextObjectiveId: 9002, started: [{ group: 2, at: '2026-09-29T10:00:00Z' }],
  packs: [{ id: 304, name: 'Gold Pack', size: 12, enabled: true }, { id: 403, name: 'Mega Pack', size: 30, enabled: true },
    { id: 513, name: '82+ Player Pick', size: 1, enabled: false, pick: true }, { id: 542, name: 'TOTW Player', size: 1, enabled: false }],
});
let live: ObjectiveRulesDoc['live'] = [{ group: 1, enabled: true }, { group: 2, enabled: true }, { group: 4, enabled: false }];
let calls: string[] = [];
beforeEach(() => {
  calls = [];
  seasons = undefined;
  live = [{ group: 1, enabled: true }, { group: 2, enabled: true }, { group: 4, enabled: false }];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') calls.push(`${method} ${path} ${String(init?.body ?? '')}`);
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    if (path === 'objectives') return json(reply(live));
    if (path === 'content/schedule') return json({ now: '2026-10-07T10:00:00Z', campaigns: [], drops: [], legacy: [] });
    if (path === 'content/changeset') return json({ open: false, pending: 0, changeset: null });
    if (path.startsWith('content/rules/objectives?path=')) {
      const at = JSON.parse(new URLSearchParams(path.split('?')[1]).get('path')!) as (string | number)[];
      const value = at.reduce<unknown>((node, key) => (node as Record<string | number, unknown> | undefined)?.[key], { ...rules, live, seasons });
      return json({ file: 'objectives', definition: value === undefined ? null : { value } });
    }
    return json({});
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderPage = (rest: string[]) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider><ObjectivesPage rest={rest} /></ToastProvider></QueryClientProvider>);
};
const bodies = () => calls.filter((call) => call.startsWith('PUT ')).map((call) => JSON.parse(call.replace('PUT content/changeset/changes ', '')) as
  { key: { file: string; path: (string | number)[] }; operation: string; after: { value: unknown } });

describe('Objectives catalogue', () => {
  it('shows each group’s live state and only removes an unlisted one', async () => {
    renderPage([]);
    const grind = (await screen.findByText('Matchday Grind')).closest('li')!;
    expect(within(grind).getByText('Live')).toBeTruthy();
    expect(within(grind).getByText(/counting since 29\/09\/2026/)).toBeTruthy();
    expect((within(grind).getByRole('button', { name: 'Remove' }) as HTMLButtonElement).disabled).toBe(true);
    expect(within((await screen.findByText('Collector')).closest('li')!).getByText('Off')).toBeTruthy();
    const gift = (await screen.findByText('Thank You')).closest('li')!;
    expect(within(gift).getByText('Not live')).toBeTruthy();
    fireEvent.click(within(gift).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(bodies()).toHaveLength(1));
    expect(bodies()[0]).toMatchObject({ key: { file: 'objectives', path: ['gifts'] }, operation: 'update', after: { value: [] } });
  });
});

describe('Live objectives', () => {
  it('preserves legacy live groups while allowing reordering and removal', async () => {
    live = undefined;
    renderPage(['live']);
    // No list stored yet: every group is live, welcome first.
    expect(await screen.findByRole('button', { name: 'Remove Thank You from Live objectives' })).toBeTruthy();
    expect(screen.queryByRole('switch')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Remove Thank You from Live objectives' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move Collector up' }));
    fireEvent.click(screen.getByRole('button', { name: 'Remove Welcome Objectives from Live objectives' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save Live objectives' }));
    await waitFor(() => expect(bodies()).toHaveLength(1));
    expect(bodies()[0]).toMatchObject({ key: { path: ['live'] }, operation: 'insert',
      after: { value: [{ group: 4, enabled: true }, { group: 2, enabled: true }] } });
  });
});

describe('Objective group editor', () => {
  it('saves editable in-game text and uses standard text when cleared', async () => {
    renderPage(['group', '2']);
    const description = await screen.findByLabelText('In-game description');
    fireEvent.change(description, { target: { value: 'Finish {n} games. Every match counts.' } });
    expect(screen.getByText('In-game preview: Finish 5 games. Every match counts.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(bodies()).toHaveLength(1));
    expect((bodies()[0]!.after.value as ObjectiveRulesDoc['grindGroups'])[0]!.objectives[0]).toMatchObject({ id: 201, description: 'Finish {n} games. Every match counts.', stat: 'matches', target: 5 });
    fireEvent.change(description, { target: { value: '' } });
    expect(screen.getByText('In-game preview: Play 5 matches in FUT.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(bodies()).toHaveLength(2));
    expect((bodies()[1]!.after.value as ObjectiveRulesDoc['grindGroups'])[0]!.objectives[0]).not.toHaveProperty('description');
  });

  it('creates a catalogue group with automatic ids without activating it', async () => {
    renderPage(['new', 'normal']);
    fireEvent.change(await screen.findByLabelText('Title'), { target: { value: 'Weekend Warrior' } });
    expect((screen.getByLabelText('Group id') as HTMLInputElement).value).toBe('91');
    fireEvent.click(screen.getByRole('button', { name: 'Add objective' }));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Ten Wins' } });
    fireEvent.change(screen.getByLabelText('Target'), { target: { value: '10' } });
    expect(screen.getByText('Play 10 matches in FUT.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(bodies()).toHaveLength(1));
    const [groups] = bodies();
    expect(groups).toMatchObject({ key: { path: ['grindGroups'] }, operation: 'update' });
    expect((groups!.after.value as { id: number }[]).at(-1)).toMatchObject({ id: 91, title: 'Weekend Warrior',
      objectives: [{ id: 9002, name: 'Ten Wins', stat: 'matches', target: 10, award: { coins: 1000 }, xp: 1000 }] });
  });
});

describe('Season Pass', () => {
  it('edits the default season as a draft and stages a level reward with coins, Aurora Tokens and packs', async () => {
    renderPage(['season']);
    expect(await screen.findByText('10,000 coins')).toBeTruthy();
    expect(screen.getByText('5,000 coins').closest('li')!.className).toContain('season-level-closed');
    expect(screen.getByText('12 clubs · 340,000 XP earned')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Edit level 2' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Aurora Tokens'), { target: { value: '50' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add pack or pick' }));
    fireEvent.change(within(dialog).getAllByLabelText('Count')[1]!, { target: { value: '2' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Apply' }));
    expect(screen.getByText('Edited')).toBeTruthy();
    expect(bodies()).toHaveLength(0);
    fireEvent.click(screen.getByRole('button', { name: 'Save season' }));
    await waitFor(() => expect(bodies()).toHaveLength(1));
    // The older single pack becomes a line; the new line follows it. The whole level track is one change.
    expect(bodies()[0]).toEqual({ area: 'rules', key: { file: 'objectives', path: ['seasonProgress'] }, operation: 'update',
      after: { value: { levelCount: 3, xpPerLevel: 2500, rewardLevelCap: 2, levelRewards: [{ coins: 10000 },
        { fifaPoints: 50, packs: [{ packId: 304, count: 1, untradeable: false }, { packId: 304, count: 2, untradeable: true }] }, { coins: 5000 }] } } });
  });

  it('renames the default season, adds a level and applies a choice of rewards to every odd level', async () => {
    renderPage(['season']);
    fireEvent.change(await screen.findByLabelText(/^Name/), { target: { value: 'Pre-Season' } });
    fireEvent.click(screen.getByRole('button', { name: 'One level more' }));
    fireEvent.click(screen.getByRole('button', { name: 'Edit level 1' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('switch', { name: /^Club picks one of several rewards/ }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add option' }));
    expect(within(dialog).getByText('3 of 2 to 5 options')).toBeTruthy();
    fireEvent.change(within(dialog).getByLabelText(/^Coins \(option 2\)/), { target: { value: '2500' } });
    fireEvent.change(within(dialog).getByLabelText('Use this reward for'), { target: { value: 'odd' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Apply' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save season' }));
    await waitFor(() => expect(bodies()).toHaveLength(2));
    const choice = { options: [{ coins: 10000 }, { coins: 2500 }, { coins: 1000 }] };
    expect(bodies()[0]).toMatchObject({ key: { path: ['campaign', 'name'] }, after: { value: 'Pre-Season' } });
    // The fourth level copies the last one; the cap stays at 2.
    expect(bodies()[1]).toMatchObject({ key: { path: ['seasonProgress'] },
      after: { value: { levelCount: 4, rewardLevelCap: 2, levelRewards: [choice, { pack: 304 }, choice, { coins: 5000 }] } } });
  });

  it('creates a custom season from the default levels and inserts the season list', async () => {
    // Six calendar weeks on at the same local time of day (the clocks change in between in the UK).
    const sixWeeksOn = '2026-11-18T13:00:00Z';
    renderPage(['season']);
    expect(await screen.findByText(/No custom seasons yet/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '+ New season' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Season name'), { target: { value: 'Winter Season' } });
    fireEvent.change(within(dialog).getByLabelText('Length'), { target: { value: '6' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create draft' }));
    expect(await screen.findByText('New · not saved')).toBeTruthy();
    expect(screen.getByRole('button', { name: /^Winter Season/, pressed: true })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save season' }));
    await waitFor(() => expect(bodies()).toHaveLength(1));
    expect(bodies()[0]).toMatchObject({ key: { path: ['seasons'] }, operation: 'insert', after: { value: [{
      id: 3, name: 'Winter Season', startsAt: '2026-10-07T12:00:00Z', endsAt: sixWeeksOn,
      levelCount: 3, xpPerLevel: 2500, rewardLevelCap: 2, levelRewards: rules.seasonProgress.levelRewards }] } });
  });

  it('starts the default season fresh under the next free season id', async () => {
    renderPage(['season']);
    fireEvent.click(await screen.findByRole('button', { name: 'Start fresh' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/It becomes season 3/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Start fresh' }));
    await waitFor(() => expect(bodies()).toHaveLength(1));
    expect(bodies()[0]).toMatchObject({ key: { path: ['campaign', 'id'] }, operation: 'update', after: { value: 3 } });
  });

  it('selects the live custom season, keeps it from being removed and refuses an overlapping one', async () => {
    seasons = [{ id: 2, name: 'Autumn Season', startsAt: '2026-10-01T00:00:00Z', endsAt: '2026-10-29T00:00:00Z', levelCount: 1, xpPerLevel: 1000,
      rewardLevelCap: null, levelRewards: [{ coins: 500 }] }];
    renderPage(['season']);
    const card = await screen.findByRole('button', { name: /^Autumn Season/, pressed: true });
    expect(within(card).getByText('Live now')).toBeTruthy();
    expect(within(card).getByText('Ends in 22 days')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Remove season' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: '+ New season' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create draft' }));
    // It starts when Autumn ends; moved a week earlier it overlaps.
    expect(await screen.findByText('New · not saved')).toBeTruthy();
    const ends = new Date('2026-10-29T00:00:00Z');
    expect(screen.getByLabelText(/^Starts/)).toBeTruthy();
    const earlier = new Date(ends.getTime() - 7 * 86_400_000);
    const pad = (n: number) => String(n).padStart(2, '0');
    fireEvent.change(screen.getByLabelText(/^Starts/), { target: { value: `${earlier.getFullYear()}-${pad(earlier.getMonth() + 1)}-${pad(earlier.getDate())}T${pad(earlier.getHours())}:${pad(earlier.getMinutes())}` } });
    expect(await screen.findByText(/It overlaps “Autumn Season”/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Save season' }) as HTMLButtonElement).disabled).toBe(true);
  });
  it('shows manually ended seasons as disabled while preserving their definition when edited', async () => {
    seasons = [{ id: 2, name: 'Autumn Season', enabled: false, startsAt: '2026-10-01T00:00:00Z', endsAt: '2026-10-29T00:00:00Z', levelCount: 1, xpPerLevel: 1000, levelRewards: [{ coins: 500 }] }];
    renderPage(['season']);
    const card = await screen.findByRole('button', { name: /^Autumn Season/ });
    expect(within(card).getByText('Disabled')).toBeTruthy();
    expect(within(card).queryByText('Live now')).toBeNull();
    fireEvent.click(card);
    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'Autumn archived' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save season' }));
    await waitFor(() => expect(bodies()[0]).toMatchObject({ after: { value: [{ id: 2, name: 'Autumn archived', enabled: false }] } }));
  });
});
