import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SbcReply, SbcSetDef } from '../../api/sbc';
import { ToastProvider } from '../../ui/Toast';
import { SbcPage } from './SbcPage';
import { keyFor, withIds } from './SbcEditor';
import { formationName } from './SbcSettingsTab';

// Shaped like OperatorPanel.Sbc.cs SbcAsync.
const set = (name: string, tabId = 0): SbcSetDef => ({ name, tabId, challenges: [{ id: `${name}_1`, name: 'Challenge', requirements: { rules: [] } }] });
const row = (key: string, name: string, listed: boolean, enabled: boolean, origin: 'bundled' | 'panel' = 'bundled', edited = false, definition = set(name)) =>
  ({ key, setId: 900000, origin, edited, listed, enabled, definition });
const bronze: SbcSetDef = { name: 'Bronze Upgrade', tabId: 4, repeatable: true, challenges: [{ id: 'CH_128', name: 'Bronze Upgrade', number: 1,
  requirements: { rules: [{ type: 'PLAYERS', attributes: { min: 11, max: 11 } }, { type: 'RATING', attributes: { min: 50, max: 99 } }] },
  rewards: { entries: [{ type: 'PACK', attributes: { packId: 304, count: 1, name: 'Gold Pack' } }] } }] };
const reply: SbcReply = {
  tabs: [{ id: 0, name: 'Foundations' }, { id: 4, name: 'Upgrades' }], badges: [],
  requirements: [
    { type: 'PLAYERS', bound: 'count', label: 'Players in squad: {value}', exact: false, qualities: [] },
    { type: 'RATING', bound: 'min', label: 'Min Squad Rating: {value}', exact: false, qualities: [] },
    { type: 'NATIONS', bound: 'min', label: 'Nationalities in Squad: Min {value}', exact: true, qualities: [] },
    { type: 'CLUBS', bound: 'min', label: 'Clubs in Squad: Min {value}', exact: true, qualities: [] },
    { type: 'LEAGUES', bound: 'min', label: 'Leagues in Squad: Min {value}', exact: true, qualities: [] },
    { type: 'AGE', bound: 'max', label: 'Player Age: Max {value}', exact: false, qualities: [] },
  ],
  packs: [{ id: 304, name: 'Gold Pack', size: 12, enabled: true }, { id: 82, name: '2 Rare Gold Players Pack', size: 2, enabled: false },
    { id: 514, name: '84+ Player Pick', size: 1, enabled: false, pick: true }, { id: 600, name: 'Messi pick', size: 1, enabled: false, pick: true, fixed: true }],
  sets: [row('UPG_BRONZE', 'Bronze Upgrade', true, true, 'bundled', false, bronze), row('UPG_GOLD', 'Gold Upgrade', true, false, 'bundled', false, set('Gold Upgrade', 4)),
    row('UP_82', '82+ Player Pick', false, false, 'bundled', true), row('MY_SET', 'My set', false, false, 'panel')],
  live: [{ setKey: 'UPG_BRONZE', enabled: true }, { setKey: 'UPG_GOLD', enabled: false }],
  settingsFile: 'sbc',
  settings: {
    formation: 'f442', repeatableRewardsUntradeable: true, playerPickRewardName: '{rating}+ Player Pick', ignoredRequirements: [],
    text: { requirementSeparator: '  |  ', noRequirements: 'Players in Squad: {count}', setDescription: 'Complete the {name} SBC.', unnamedSetDescription: 'Complete this SBC.',
      challenge: 'challenge', challenges: 'challenges', unknownChallenge: 'Unknown challenge.', shortSquad: 'Squad needs {need} players ({placed} placed).' },
    images: { imageIdBase: 1000000, artFolder: 'sbc-art', fallbackTabId: 0, cacheSeconds: 86400 }, squadSize: 11, brickItemIdBase: 700000000000,
  },
  requirementRules: [
    { type: 'RATING', bound: 'min', eligibilityKey: 'TeamRating', eligibilityValue: null, scope: 'AtLeast', playerCount: 0, label: 'Min Squad Rating: {value}', exact: null, qualities: {} },
    { type: 'NATIONS', bound: 'min', eligibilityKey: 'NationCount', eligibilityValue: null, scope: 'AtLeast', playerCount: 0, label: 'Nationalities in Squad: Min {value}',
      exact: { scope: 'Exact', label: 'Nationalities in Squad: Exactly {value}' }, qualities: {} },
    { type: 'AGE', bound: 'max', eligibilityKey: null, eligibilityValue: null, scope: null, playerCount: 0, label: 'Player Age: Max {value}', exact: null, qualities: {} },
  ],
  formations: [{ id: 'f4231', slots: [0, 3, 4, 6, 7, 9, 11, 17, 18, 19, 25] }, { id: 'f442', slots: [0, 3, 4, 6, 7, 12, 13, 15, 16, 24, 26] }],
  positions: ['GK', 'SW', 'RWB', 'RB', 'RCB', 'CB', 'LCB', 'LB', 'LWB', 'RDM', 'CDM', 'LDM', 'RM', 'RCM', 'CM', 'LCM', 'LM', 'RAM', 'CAM', 'LAM', 'RF', 'CF', 'LF', 'RW', 'RS', 'ST', 'LS', 'LW'],
  squadSize: 11,
  managerLeagues: [{ league: 13, name: 'Premier League Manager League', resourceId: 9100013 }],
};
reply.requirements.push(
  { type: 'LEAGUE_PLAYERS', bound: 'min', label: 'League players ({selection}): {comparison} {value}', exact: true, qualities: [], selector: 'leagueId' },
  { type: 'NATION_PLAYERS', bound: 'min', label: 'Nation players ({selection}): {comparison} {value}', exact: true, qualities: [], selector: 'nationId' },
  { type: 'CLUB_PLAYERS', bound: 'min', label: 'Club players ({selection}): {comparison} {value}', exact: true, qualities: [], selector: 'clubId' },
  { type: 'QUALITY_PLAYERS', bound: 'min', label: '{selection} players: {comparison} {value}', exact: true, qualities: ['bronze', 'silver', 'gold'], selector: 'quality' },
);
let calls: string[] = [];
const preview: { problem: string | null } = { problem: null };
const staged = () => calls.filter((call) => call.startsWith('PUT ')).map((call) => JSON.parse(call.replace('PUT content/changeset/changes ', '')) as {
  area: string; key: unknown; operation: string; after: { definition: SbcSetDef } });
beforeEach(() => {
  calls = [];
  preview.problem = null;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') calls.push(`${method} ${path} ${String(init?.body ?? '')}`);
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    if (path === 'sbc') return json(reply);
    if (path === 'cards/filters') return json({ leagues: [{ id: 53, name: 'LaLiga' }, { id: 10, name: 'Eredivisie' }], nations: [{ id: 52, name: 'Argentina' }], clubs: [{ id: 241, name: 'Barcelona', leagueName: 'LaLiga' }] });
    if (path === 'content/schedule') return json({ now: '2026-10-07T10:00:00Z', campaigns: [], drops: [], legacy: [] });
    if (path === 'content/changeset') return json({ open: false, pending: 0, changeset: null });
    if (path === 'content/rules/sbc?path=%5B%22requirements%22%2C%22RATING%22%2C%22label%22%5D') return json({ definition: { value: 'Min Squad Rating: {value}' } });
    if (path === 'content/rules/sbc?path=%5B%22ignoredRequirements%22%5D') return json({ definition: { value: [] } });
    if (path === 'content/rules/import') return json({ staged: 3 });
    if (path === 'sbc/preview') return json({ problem: preview.problem, description: 'Complete the Bronze Upgrade SBC (1 challenge).', challenges: [{ description: 'Min Squad Rating: 50', players: 11 }] });
    if (path.startsWith('cards?q=')) return json({ cards: [{ cardKey: 'k1', resourceId: 158023, name: 'Lionel Messi', rating: 93, position: 'RW', leagueId: 53, art: { nationId: 52, leagueId: 53 } }] });
    if (path.startsWith('cards?resourceId=')) return json({ cards: [{ cardKey: 'k1', resourceId: 158023, name: 'Lionel Messi', rating: 93, position: 'RW' }] });
    return json({});
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderPage = (rest: string[]) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider><SbcPage rest={rest} /></ToastProvider></QueryClientProvider>);
};
const card = async (name: string) => (await screen.findByText(name, { selector: 'strong' })).closest('li')!;

describe('SBC catalogue', () => {
  it('shows each set’s live state and removes only an unlisted panel set', async () => {
    renderPage([]);
    expect(within(await card('Bronze Upgrade')).getByText('Live')).toBeTruthy();
    expect(within(await card('Gold Upgrade')).getByText('Off')).toBeTruthy();
    const pick = await card('82+ Player Pick');
    expect(within(pick).getByText('Not live')).toBeTruthy();
    expect(within(pick).queryByRole('button', { name: 'Remove' })).toBeNull();
    fireEvent.click(within(pick).getByRole('button', { name: 'Revert' }));
    fireEvent.click(within(await card('My set')).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(calls).toHaveLength(2));
    expect(calls.map((call) => JSON.parse(call.replace('PUT content/changeset/changes ', '')) as { area: string; key: unknown; operation: string }))
      .toEqual([expect.objectContaining({ area: 'sbc_set', key: { setKey: 'UP_82' }, operation: 'hide' }),
        expect.objectContaining({ area: 'sbc_set', key: { setKey: 'MY_SET' }, operation: 'hide' })]);
  });
});

describe('Live SBCs', () => {
  it('keeps inactive SBCs out of Live and requires dates for a new release', async () => {
    renderPage(['live']);
    expect(await screen.findByText('Bronze Upgrade')).toBeTruthy();
    expect(screen.queryByText('Gold Upgrade')).toBeNull();
    expect(screen.queryByRole('switch')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Add an SBC to Live SBCs' }));
    const dialog = within(await screen.findByRole('dialog'));
    fireEvent.change(dialog.getByLabelText('Content'), { target: { value: 'UP_82' } });
    fireEvent.change(dialog.getByLabelText('End date'), { target: { value: '' } });
    expect(dialog.getByRole('button', { name: 'Save release' })).toHaveProperty('disabled', true);
    expect(calls).toEqual([]);
  });
});

describe('SBC settings', () => {
  it('names formations as FIFA does', () => {
    expect(['f4231', 'f433a', 'f41212a', 'f433d'].map(formationName)).toEqual(['4-2-3-1', '4-3-3 (2)', '4-1-2-1-2 (2)', '4-3-3 (5)']);
  });

  it('stages a requirement label, switches a type off and restores the shipped file', async () => {
    renderPage(['settings']);
    const rating = (await screen.findByText('Min Squad Rating')).closest('li')!;
    expect(screen.queryByText('Player Age')).toBeNull();
    expect(screen.queryByText('Description only: not sent to FIFA as a check, not checked on submit.')).toBeNull();
    fireEvent.change(within(rating).getByLabelText('Label'), { target: { value: 'Squad Rating: Min {value}' } });
    expect(within(rating).getByText(/Shows as: Squad Rating: Min 75/)).toBeTruthy();
    fireEvent.click(within(rating).getByRole('button', { name: 'Save Label' }));
    await waitFor(() => expect(calls).toHaveLength(1));
    fireEvent.click(within(rating).getByRole('switch', { name: 'Min Squad Rating used' }));
    await waitFor(() => expect(calls).toHaveLength(2));
    const bodies = calls.map((call) => JSON.parse(call.replace('PUT content/changeset/changes ', '')) as unknown);
    expect(bodies).toEqual([
      { area: 'rules', key: { file: 'sbc', path: ['requirements', 'RATING', 'label'] }, operation: 'update', after: { value: 'Squad Rating: Min {value}' } },
      { area: 'rules', key: { file: 'sbc', path: ['ignoredRequirements'] }, operation: 'update', after: { value: ['RATING'] } },
    ]);
    fireEvent.click(screen.getByRole('button', { name: 'Restore shipped settings' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Stage the shipped settings' }));
    await waitFor(() => expect(calls).toContain('POST content/rules/import {"file":"sbc"}'));
  });
});

describe('SBC player picks', () => {
  it('rewards a fixed player pick as its pick pack, listing random and fixed picks apart', async () => {
    renderPage(['UPG_BRONZE']);
    fireEvent.click((await screen.findAllByRole('button', { name: 'Add reward' }))[0]!);
    fireEvent.change(screen.getAllByLabelText('Reward')[1]!, { target: { value: 'PICK' } });
    const pick = screen.getByLabelText('Player pick') as HTMLSelectElement;
    expect([...pick.querySelectorAll('optgroup')].map((group) => [group.label, [...group.querySelectorAll('option')].map((option) => option.value)]))
      .toEqual([['Random player picks', ['514']], ['Fixed player picks (chosen cards)', ['600']]]);
    fireEvent.change(pick, { target: { value: '600' } });
    // The pack select of a plain pack reward lists no picks.
    expect([...(screen.getAllByLabelText('Pack')[0] as HTMLSelectElement).options].map((option) => option.value)).not.toContain('600');
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(staged()).toHaveLength(1));
    expect(staged()[0]!.after.definition.challenges[0]!.rewards!.entries[1]).toEqual({ type: 'PACK', attributes: { packId: 600, count: 1, name: 'Messi pick' } });
  });
});

describe('SBC groups', () => {
  it('lists the catalogue in the game’s tabs', async () => {
    renderPage([]);
    const upgrades = await screen.findByRole('region', { name: 'Upgrades' });
    expect(within(upgrades).getByText('Bronze Upgrade', { selector: 'strong' })).toBeTruthy();
    expect(within(upgrades).getByText('1 live of 2')).toBeTruthy();
    expect(within(screen.getByRole('region', { name: 'Foundations' })).getByText('My set', { selector: 'strong' })).toBeTruthy();
  });
});

describe('SBC editor', () => {
  it('shows and requires group rewards only with two or more challenges', async () => {
    renderPage(['UPG_BRONZE']);
    await screen.findByText('The server accepts this set.');
    expect(screen.queryByRole('heading', { name: 'Group rewards' })).toBeNull();
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Add challenge' }));
    const group = screen.getByRole('heading', { name: 'Group rewards' }).closest('section')!;
    expect(within(group).getByRole('alert').textContent).toContain('A group reward is required');
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByText('The server accepts this set.')).toBeNull();
    fireEvent.click(within(group).getByRole('button', { name: 'Add reward' }));
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(within(group).getByRole('button', { name: 'Remove reward' }));
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getAllByRole('button', { name: 'Remove' }).at(-1)!);
    expect(screen.queryByRole('heading', { name: 'Group rewards' })).toBeNull();
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(staged()).toHaveLength(1));
    expect(staged()[0]!.after.definition.challenges).toHaveLength(1);
  });

  it('locks slots, places a card, keeps the PLAYERS count in step and stages it', async () => {
    renderPage(['UPG_BRONZE']);
    expect(await screen.findByText('The server accepts this set.')).toBeTruthy();
    fireEvent.click(screen.getByRole('switch', { name: /^Locked slots/ }));
    expect((screen.getByLabelText('Players in squad') as HTMLInputElement).value).toBe('10');
    fireEvent.click(screen.getByRole('button', { name: 'Slot 1, GK: locked' }));
    let dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('radio', { name: /Locked card/ }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Choose card…' }));
    const picker = (await screen.findAllByRole('dialog')).at(-1)!;
    fireEvent.change(within(picker).getByLabelText('Player name'), { target: { value: 'Messi' } });
    fireEvent.click(await within(picker).findByRole('button', { name: /Lionel Messi/ }));
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Choose the locked card' })).toBeNull());
    dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText('Lionel Messi')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(screen.queryAllByRole('dialog')).toHaveLength(0));
    fireEvent.change(screen.getByLabelText('Formation'), { target: { value: 'f4231' } });
    fireEvent.change(screen.getByLabelText('Number'), { target: { value: '60' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(staged()).toHaveLength(1));
    const challenge = staged()[0]!.after.definition.challenges[0]!;
    expect(challenge.formation).toBe('f4231');
    expect(challenge.bricks).toEqual({ freeIndices: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], players: { 0: 158023 } });
    // The rating keeps its authored max; the squad size follows the ten open slots.
    expect(challenge.requirements!.rules).toEqual([{ type: 'PLAYERS', attributes: { min: 10, max: 10 } }, { type: 'RATING', attributes: { min: 60, max: 99 } }]);
    expect(challenge.number).toBe(1);
  });

  it('pays a manager league and a picked player, and shows why the server would refuse it', async () => {
    preview.problem = 'challenge 1: a pack reward names its pack by packId.';
    renderPage(['UPG_BRONZE']);
    expect(await screen.findByText(preview.problem)).toBeTruthy();
    fireEvent.click(screen.getAllByRole('button', { name: 'Add reward' })[0]!);
    const rewards = screen.getAllByLabelText('Reward');
    fireEvent.change(rewards[1]!, { target: { value: 'MANAGER_LEAGUE' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Add reward' })[0]!);
    fireEvent.change(screen.getAllByLabelText('Reward')[2]!, { target: { value: 'PLAYER' } });
    const picker = (await screen.findAllByRole('dialog')).at(-1)!;
    fireEvent.change(within(picker).getByLabelText('Player name'), { target: { value: 'Messi' } });
    fireEvent.click(await within(picker).findByRole('button', { name: /Lionel Messi/ }));
    await waitFor(() => expect(screen.queryAllByRole('dialog')).toHaveLength(0));
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(staged()).toHaveLength(1));
    expect(staged()[0]!.after.definition.challenges[0]!.rewards!.entries).toEqual([
      { type: 'PACK', attributes: { packId: 304, count: 1, name: 'Gold Pack' } },
      { type: 'MANAGER_LEAGUE', attributes: { league: 13, name: 'Premier League Manager League' } },
      { type: 'PLAYER', attributes: { resourceId: 158023 } },
    ]);
  });

  it('duplicates a set with a key made from its name and automatic challenge ids', async () => {
    renderPage(['new', 'UPG_BRONZE']);
    const name = (await screen.findAllByLabelText('Name'))[0] as HTMLInputElement;
    expect(name.value).toBe('Bronze Upgrade (copy)');
    expect((screen.getByLabelText('Key') as HTMLInputElement).value).toBe('BRONZE_UPGRADE_COPY');
    fireEvent.change(name, { target: { value: 'Bronze Two' } });
    expect((screen.getByLabelText('Key') as HTMLInputElement).value).toBe('BRONZE_TWO');
    expect((screen.getByLabelText('Challenge id') as HTMLInputElement).value).toBe('BRONZE_TWO_1');
    expect((screen.getByLabelText('Challenge id') as HTMLInputElement).readOnly).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(staged()).toHaveLength(1));
    const body = staged()[0]!;
    expect([body.operation, body.key]).toEqual(['insert', { setKey: 'BRONZE_TWO' }]);
    expect(body.after.definition.challenges[0]).toMatchObject({ id: 'BRONZE_TWO_1' });
    expect(body.after.definition.challenges[0]).not.toHaveProperty('number');
    expect(body.after.definition.challenges[0]).not.toHaveProperty('__auto');
  });

  it('makes keys and challenge ids that nothing else uses', () => {
    expect(keyFor('Pelé Icon!', new Set())).toBe('PELE_ICON');
    expect(keyFor('Pelé Icon', new Set(['PELE_ICON', 'PELE_ICON_2']))).toBe('PELE_ICON_3');
    expect(keyFor('  ', new Set())).toBe('SBC');
    const set: SbcSetDef = { name: 'X', tabId: 0, challenges: [{ id: 'X_2', name: 'Kept' }, { id: '', __auto: true, name: 'New' }, { id: '', __auto: true, name: 'New 2' }] };
    expect(withIds(set, 'X', new Set(['X_1'])).challenges.map((challenge) => challenge.id)).toEqual(['X_2', 'X_3', 'X_4']);
  });
});

it('saves daily repeatability with its London reset and can return to once', async () => {
  renderPage(['UPG_BRONZE']);
  await screen.findByText('The server accepts this set.');
  fireEvent.change(screen.getByLabelText('Repeatability'), { target: { value: 'limited' } });
  fireEvent.click(screen.getByRole('switch', { name: /^Reset daily/ }));
  expect(screen.getByText(/Daily resets at 7pm London/)).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(staged()).toHaveLength(1));
  expect(staged()[0]!.after.definition).toMatchObject({ repeatable: true, repeatableDaily: true });
});

it.each([3, 5])('saves a limit of %i completions per account', async (limit) => {
  renderPage(['UPG_BRONZE']);
  await screen.findByText('The server accepts this set.');
  fireEvent.change(screen.getByLabelText('Repeatability'), { target: { value: 'limited' } });
  fireEvent.change(screen.getByLabelText('Completion limit'), { target: { value: String(limit) } });
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(staged()).toHaveLength(1));
  expect(staged()[0]!.after.definition).toMatchObject({ repeatable: true, repeatableDaily: false, maxCompletions: limit });
});

it.each([1, 3])('saves a daily limit of %i completions per account', async (limit) => {
  renderPage(['UPG_BRONZE']);
  await screen.findByText('The server accepts this set.');
  fireEvent.change(screen.getByLabelText('Repeatability'), { target: { value: 'limited' } });
  fireEvent.click(screen.getByRole('switch', { name: /^Reset daily/ }));
  fireEvent.change(screen.getByLabelText('Completion limit'), { target: { value: String(limit) } });
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(staged()).toHaveLength(1));
  expect(staged()[0]!.after.definition).toMatchObject({ repeatable: true, repeatableDaily: true, maxCompletions: limit });
});

it('clears the completion limit when returning to unlimited', async () => {
  renderPage(['UPG_BRONZE']);
  await screen.findByText('The server accepts this set.');
  fireEvent.change(screen.getByLabelText('Repeatability'), { target: { value: 'limited' } });
  fireEvent.change(screen.getByLabelText('Repeatability'), { target: { value: 'unlimited' } });
  expect(screen.queryByLabelText('Completion limit')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(staged()).toHaveLength(1));
  expect(staged()[0]!.after.definition).not.toHaveProperty('maxCompletions');
});

it.each([
  ['LEAGUE_PLAYERS', 'League', 'laliga', 'LaLiga', 'leagueId', 53],
  ['NATION_PLAYERS', 'Nation', 'argentina', 'Argentina', 'nationId', 52],
  ['CLUB_PLAYERS', 'Club', 'barcelona', 'Barcelona (LaLiga)', 'clubId', 241],
] as const)('saves searchable selections for %s with an exact player count', async (type, label, search, option, selector, id) => {
  renderPage(['UPG_BRONZE']);
  await screen.findByText('The server accepts this set.');
  fireEvent.change(screen.getByLabelText('Requirement'), { target: { value: type } });
  fireEvent.click(screen.getByRole('button', { name: label }));
  fireEvent.change(screen.getByLabelText(`Search ${label.toLowerCase()}`), { target: { value: search } });
  fireEvent.click(await screen.findByRole('option', { name: option }));
  fireEvent.change(screen.getByLabelText('Comparison'), { target: { value: 'exact' } });
  fireEvent.change(screen.getByLabelText('Number'), { target: { value: '3' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(staged()).toHaveLength(1));
  expect(staged()[0]!.after.definition.challenges[0]!.requirements!.rules[1]).toEqual({ type, attributes: { exact: 3, [selector]: id, selectionName: option } });
});

it('supports every count comparison and counted bronze silver gold players', async () => {
  renderPage(['UPG_BRONZE']);
  await screen.findByText('The server accepts this set.');
  fireEvent.change(screen.getByLabelText('Requirement'), { target: { value: 'NATIONS' } });
  expect(within(screen.getByLabelText('Comparison')).getAllByRole('option').map(option => option.textContent)).toEqual(['At least', 'At most', 'Exactly']);
  fireEvent.change(screen.getByLabelText('Comparison'), { target: { value: 'max' } });
  fireEvent.change(screen.getByLabelText('Number'), { target: { value: '4' } });
  expect(screen.getByText(/Nationalities in Squad: Max 4/)).toBeTruthy();
  fireEvent.change(screen.getByLabelText('Requirement'), { target: { value: 'QUALITY_PLAYERS' } });
  fireEvent.change(screen.getByLabelText('Player quality'), { target: { value: 'bronze' } });
  fireEvent.change(screen.getByLabelText('Comparison'), { target: { value: 'max' } });
  fireEvent.change(screen.getByLabelText('Number'), { target: { value: '0' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(staged()).toHaveLength(1));
  expect(staged()[0]!.after.definition.challenges[0]!.requirements!.rules[1]).toEqual({ type: 'QUALITY_PLAYERS', attributes: { max: 0, quality: 'bronze', selectionName: 'Bronze' } });
});

it.each([['CLUBS', 'Clubs in squad'], ['LEAGUES', 'Leagues in squad'], ['NATIONS', 'Nations in squad']])('uses a neutral %s name while saving a maximum and excludes player age', async (type, name) => {
  renderPage(['UPG_BRONZE']);
  await screen.findByText('The server accepts this set.');
  const requirement = screen.getByLabelText('Requirement');
  expect(within(requirement).queryByRole('option', { name: /Player Age/ })).toBeNull();
  expect(within(requirement).getByRole('option', { name })).toBeTruthy();
  fireEvent.change(requirement, { target: { value: type } });
  fireEvent.change(screen.getByLabelText('Comparison'), { target: { value: 'max' } });
  fireEvent.change(screen.getByLabelText('Number'), { target: { value: '3' } });
  expect(within(requirement).getByRole('option', { name })).toHaveProperty('selected', true);
  expect(screen.getByText(/in Squad: Max 3/)).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(staged()).toHaveLength(1));
  expect(staged()[0]!.after.definition.challenges[0]!.requirements!.rules[1]).toEqual({ type, attributes: { max: 3 } });
});
