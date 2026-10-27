import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '../../ui/Toast';
import { PacksPage } from './PacksPage';
import { packFields } from './PackEditor';

// Shaped like OperatorPanel.PackCatalog.cs / OperatorPanel.Packs.cs replies. Every pack has a complete odds table for the
// live categories (TOTW and Icon).
const odds = { totw: 0.006, icon: 0.0005 };
const catalog = { revision: 'r1', groups: ['bronze', 'silver', 'gold', 'special'], publicPackIds: [3],
  promos: [{ code: 'totw_5', name: 'TOTW 5', category: 'totw' }, { code: 'base_icons', name: 'Base Icons', category: 'icon' }],
  oddsTemplate: { icon: 0.0005, pickTotw: 0.03 },
  packs: [
    { id: 3, locked: false, grantOnly: false, inStore: true, usedBy: [], art: 'gold', description: 'Gold Pack',
      config: { name: 'Gold Pack', price: 7500, points_price: 150, size: 12, rare_slots: 1, store_group: 'gold', category_chances: odds } },
    { id: 5, locked: true, grantOnly: false, inStore: false, usedBy: [], art: 'bronze', description: 'Bronze Pack',
      config: { name: 'Bronze Pack', price: 400, size: 12, store_group: 'bronze', category_chances: odds } },
  ] };
const morePacks = [
  { id: 515, grantOnly: false, inStore: false, art: 'special', description: 'Rivals Pick', usedBy: ['rivals_rewards.promotion.1.packs[0].packId'],
    config: { name: 'Rivals Pick', player_picks: [{ count: 1, choice_count: 3 }], category_chances: odds } },
  { id: 74, grantOnly: false, inStore: true, art: 'gold', description: 'Sold And Awarded', usedBy: ['sbc.rewardPacks.gold'],
    config: { name: 'Sold And Awarded', price: 5000, size: 12, rare_slots: 1, store_group: 'gold', category_chances: odds } },
  { id: 564, grantOnly: true, inStore: false, art: 'special', description: 'Fresh Pack', usedBy: [],
    config: { name: 'Fresh Pack', size: 3, rare_slots: 1, admin_only: true, category_chances: odds } },
  { id: 560, grantOnly: false, inStore: false, art: 'special', description: 'Icon Swaps Token', usedBy: [],
    viewOnlyReason: 'This pack draws club items, consumables, managers, stadiums or swap tokens; the panel cannot edit that generator.',
    config: { name: 'Icon Swaps Token', size: 1, swap_token_rareflag: 91 } },
];
const overview = { packs: { packs: [{ id: 3, name: 'Gold Pack' }] },
  clubs: [{ id: 1001, name: 'Northbank', unopenedPacks: 2 }, { id: 1002, name: 'Full', unopenedPacks: 100 }], grants: [] };

let calls: string[] = [];
let replies: Record<string, { status: number; body: unknown }> = {};
beforeEach(() => {
  calls = []; replies = {};
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') calls.push(`${method} ${path} ${String(init?.body ?? '')}`);
    const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    const override = replies[`${method} ${path}`];
    if (override) return json(override.status, override.body);
    if (path === 'packs/catalog') return json(200, catalog);
    if (path === 'promos/availability') return json(200, { promos: [
      { code: 'totw_5', generallyAvailable: true }, { code: 'base_icons', generallyAvailable: true }] });
    if (path === 'content/schedule') return json(200, { now: '2026-10-07T10:00:00Z', campaigns: [], drops: [], legacy: [] });
    if (path === 'content/changeset') return json(200, { open: false, pending: 0 });
    if (path === 'packs') return json(200, overview);
    if (path === 'packs/catalog/test') return json(200, { items: [{ name: 'Messi', rating: 93 }] });
    if (path === 'packs/simulate') return json(200, { rolls: [{ index: 0, items: [{ name: 'Kane', rating: 88 }] }] });
    if (path === 'assets') return json(200, { available: false });
    if (path.startsWith('cards?q=')) return json(200, { cards: [
      { cardKey: 'a', resourceId: 158023, name: 'Lionel Messi', rating: 93, position: 'RW' },
      { cardKey: 'b', resourceId: 50490631, name: 'Lionel Messi', rating: 95, position: 'RW', special: 'TOTW' }] });
    if (path.startsWith('cards?resourceId=')) return json(200, { cards: [{ cardKey: 'a', resourceId: Number(new URLSearchParams(path.split('?')[1]).get('resourceId')), name: 'Lionel Messi', rating: 93, position: 'RW' }] });
    return json(200, {});
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderPage = (rest: string[], query = '') => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider><PacksPage rest={rest} params={new URLSearchParams(query)} /></ToastProvider></QueryClientProvider>);
};
const posted = () => JSON.parse(calls.find((call) => call.startsWith('POST packs/catalog '))!.slice('POST packs/catalog '.length)) as
  Record<string, unknown> & { pack: Record<string, unknown> };
const stageButton = () => screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement;

describe('packFields', () => {
  const values = { name: 'Gold Pack', bio: '', store_group: 'gold', price: '9000', points_price: '150', size: '12', rare_slots: '1',
    gold_weight: '', silver_weight: '', bronze_weight: '', min_rating: '', max_rating: '', guaranteed_specials: '',
    manager_chance: '0.5', chem_style_chance: '', promo_only: false, admin_only: false };
  it('sends only what changed for an edit, and every set field for a new pack', () => {
    expect(packFields(values, catalog.packs[0]!.config, false)).toEqual({ manager_chance: 0.5 });
    expect(packFields(values, {}, true)).toMatchObject({ name: 'Gold Pack', size: 12, promo_only: false });
  });
  it('refuses a chance above 1 or a negative number before sending', () => {
    expect(packFields({ ...values, manager_chance: '2' }, {}, true)).toBe('Manager chance: a number from 0 to 1.');
    expect(packFields({ ...values, size: '-1' }, {}, true)).toBe('Items per pack: a whole number of 0 or more.');
  });
});

describe('Packs page', () => {
  it('keeps price and placement out of the pack editor', async () => {
    renderPage(['catalogue', '3']);
    await screen.findByLabelText('Pack art');
    expect(screen.queryByLabelText('Store section')).toBeNull();
    expect(screen.queryByLabelText('Coin price')).toBeNull();
    expect(screen.queryByLabelText('Aurora Tokens price')).toBeNull();
  });
  it('requires dates and an independent token price when scheduling the same pack in another store', async () => {
    renderPage(['tokens']);
    fireEvent.click(await screen.findByRole('button', { name: 'Add a pack to the token store listing' }));
    const dialog = within(await screen.findByRole('dialog'));
    fireEvent.change(dialog.getByLabelText('Content'), { target: { value: '3' } });
    fireEvent.change(dialog.getByLabelText('Aurora Tokens price'), { target: { value: '25' } });
    expect(dialog.getByLabelText('Tradeability')).toHaveProperty('value', 'untradeable');
    expect(dialog.queryByRole('switch')).toBeNull();
    fireEvent.change(dialog.getByLabelText('End date'), { target: { value: '' } });
    expect(dialog.getByRole('button', { name: 'Save release' })).toHaveProperty('disabled', true);
    expect(calls).toEqual([]);
  });
  it('clears the opposing limit when switching between community and per-club allowances', async () => {
    renderPage(['catalogue', '3']);
    const user = await screen.findByLabelText('Purchase limit per club');
    const community = screen.getByLabelText('Community limit');
    fireEvent.change(user, { target: { value: '3' } });
    fireEvent.change(community, { target: { value: '100' } });
    expect((user as HTMLInputElement).value).toBe('0');
    fireEvent.change(user, { target: { value: '2' } });
    expect((community as HTMLInputElement).value).toBe('0');
    fireEvent.change(community, { target: { value: '100' } });
    await waitFor(() => expect(stageButton().disabled).toBe(false));
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some(call => call.includes('"community_limit":100') && call.includes('"purchase_limit":0'))).toBe(true));
    expect(packFields({ community_limit: '100' }, { purchase_limit: 3 }, false)).toMatch(/not both/);
    expect(packFields({ purchase_limit: '3', community_limit: '100' }, {}, true)).toMatch(/not both/);
  });

  it('stages a per-club purchase limit, including zero to restore unlimited purchases', async () => {
    renderPage(['catalogue', '3']);
    const limit = await screen.findByLabelText('Purchase limit per club');
    fireEvent.change(limit, { target: { value: '3' } });
    await waitFor(() => expect(stageButton().disabled).toBe(false));
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some(call => call.includes('"purchase_limit":3'))).toBe(true));
    expect(packFields({ purchase_limit: '0' }, { purchase_limit: 3 }, false)).toEqual({ purchase_limit: 0 });
  });

  it('lists every pack once with where it is used, filtered by use and by pack or pick', async () => {
    replies['GET packs/catalog'] = { status: 200, body: { ...catalog, publicPackIds: [3, 74], packs: [...catalog.packs, ...morePacks] } };
    renderPage(['catalogue']);
    const row = async (name: string) => (await screen.findByText(name)).closest('tr') as HTMLElement;
    expect(within(await row('Gold Pack')).getByText('Store')).toBeTruthy();
    expect(within(await row('Sold And Awarded')).getByText('Store + Rewards')).toBeTruthy();
    expect(within(await row('Rivals Pick')).getByText('Rewards')).toBeTruthy();
    expect(within(await row('Rivals Pick')).getByText('rivals_rewards › promotion.1')).toBeTruthy();
    expect(within(await row('Fresh Pack')).getByText('Unassigned')).toBeTruthy();
    expect(within(await row('Fresh Pack')).getByText('Grant only')).toBeTruthy();
    expect(within(await row('Icon Swaps Token')).getByText('View only')).toBeTruthy();
    // Hide only applies to a pack the store sells.
    expect(within(await row('Fresh Pack')).queryByRole('button', { name: 'Hide' })).toBeNull();
    expect(within(await row('Gold Pack')).getByRole('button', { name: 'Hide' })).toBeTruthy();
    fireEvent.click(within(screen.getByRole('group', { name: 'Used in' })).getByRole('button', { name: /^Unassigned/ }));
    expect(screen.queryByText('Gold Pack')).toBeNull();
    expect(screen.getByText('Fresh Pack')).toBeTruthy();
    fireEvent.click(within(screen.getByRole('group', { name: 'Used in' })).getByRole('button', { name: /^Anywhere/ }));
    fireEvent.click(within(screen.getByRole('group', { name: 'Show' })).getByRole('button', { name: /^Picks/ }));
    expect(screen.getByText('Rivals Pick')).toBeTruthy();
    expect(screen.queryByText('Fresh Pack')).toBeNull();
  });

  it('creates an unassigned pack with special art and an odds table filled from the template', async () => {
    renderPage(['catalogue', 'new']);
    expect(await screen.findByText(/A new pack is unassigned/)).toBeTruthy();
    expect((screen.getByLabelText('Pack art') as HTMLSelectElement).value).toBe('special');
    expect(screen.queryByRole('radio', { name: /Reward/ })).toBeNull();
    expect((await screen.findByLabelText('Icon (%)') as HTMLInputElement).value).toBe('0.05');
    expect((screen.getByLabelText('TOTW (%)') as HTMLInputElement).value).toBe('0');
    expect(screen.queryByLabelText('Upgraded specials (%)')).toBeNull();
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some((call) => call.startsWith('POST packs/catalog '))).toBe(true));
    const body = posted();
    expect(Object.keys(body).sort()).toEqual(['pack', 'revision']);
    expect(body.pack).toMatchObject({ name: 'New Pack', store_group: 'special', admin_only: false, category_chances: { icon: 0.0005, totw: 0 } });
    await waitFor(() => expect(window.location.hash).toBe('#/packs/catalogue'));
  });

  it('asks for the upgraded specials row once a pack has silver or bronze slots', async () => {
    renderPage(['catalogue', '3']);
    fireEvent.change(await screen.findByLabelText('Silver weight'), { target: { value: '50' } });
    expect(await screen.findByText(/Missing: Upgraded specials/)).toBeTruthy();
    expect(stageButton().disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Upgraded specials (%)'), { target: { value: '0.02' } });
    expect(screen.queryByText(/Missing:/)).toBeNull();
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls).toEqual(['PUT packs/catalog/3 {"revision":"r1","pack":{"silver_weight":50,"category_chances":{"totw":0.006,"icon":0.0005,"upgraded":0.0002}}}']));
  });

  it('stages Grant only, which keeps a pack out of the store lineup', async () => {
    renderPage(['catalogue', '3']);
    fireEvent.click(await screen.findByRole('switch', { name: /^Grant only/ }));
    await waitFor(() => expect(stageButton().disabled).toBe(false));
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls).toEqual(['PUT packs/catalog/3 {"revision":"r1","pack":{"admin_only":true}}']));
  });

  it('lets a view-only pack change only how it is named, sold and shown', async () => {
    replies['GET packs/catalog'] = { status: 200, body: { ...catalog, packs: [...catalog.packs, ...morePacks] } };
    renderPage(['catalogue', '560']);
    expect(await screen.findByText(/View only: .*cannot edit that generator/)).toBeTruthy();
    expect(screen.queryByLabelText('Items per pack')).toBeNull();
    expect(screen.queryByLabelText('Contents type')).toBeNull();
    expect(screen.queryByText('Odds')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Test contents' })).toBeNull();
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Token reward' } });
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls).toEqual(['PUT packs/catalog/560 {"revision":"r1","pack":{"name":"Token reward"}}']));
  });

  it('keeps packs and picks apart: Create pack offers no picks, Create pick only picks', async () => {
    const page = renderPage(['catalogue', 'new']);
    const packTypes = [...(await screen.findByLabelText('Contents type') as HTMLSelectElement).options].map((option) => option.value);
    expect(packTypes).toEqual(['players', 'mixed']);
    page.unmount();
    renderPage(['catalogue', 'new'], 'type=picks');
    const pickTypes = [...(await screen.findByLabelText('Contents type') as HTMLSelectElement).options].map((option) => option.value);
    expect(pickTypes).toEqual(['picks']);
  });

  it('requires every offered category in the odds table and blocks totals above 100% before sending', async () => {
    replies['GET packs/catalog'] = { status: 200, body: { ...catalog, promos: [...catalog.promos,
      { code: 'winter_freeze', name: 'Winter Freeze', category: 'other_promo' },
      { code: 'headliners', name: 'Headliners', category: 'other_promo' }] } };
    renderPage(['catalogue', 'new']);
    await screen.findByLabelText('Icon (%)');
    const selected = screen.getByLabelText('Specific promos') as HTMLSelectElement;
    for (const option of selected.options) option.selected = ['winter_freeze', 'headliners'].includes(option.value);
    fireEvent.change(selected);
    const freeze = await screen.findByLabelText('Winter Freeze (%)');
    expect(screen.queryByLabelText('other promo (%)')).toBeNull();
    // A promo added to the sources has no chance until one is chosen deliberately.
    expect(screen.getByText(/Fill in the odds for every category.*Missing: Headliners, Winter Freeze\./)).toBeTruthy();
    expect(stageButton().disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Test contents' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(freeze, { target: { value: '100' } });
    fireEvent.change(screen.getByLabelText('Headliners (%)'), { target: { value: '100' } });
    expect(screen.getByText('Total category odds cannot exceed 100%.')).toBeTruthy();
    expect(calls).toEqual([]);
    fireEvent.change(freeze, { target: { value: '40' } });
    fireEvent.change(screen.getByLabelText('Headliners (%)'), { target: { value: '59' } });
    expect(screen.queryByRole('alert')).toBeNull();
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some(call => call.startsWith('POST packs/catalog '))).toBe(true));
    expect(posted().pack.category_chances).toEqual({ icon: 0.0005, totw: 0, winter_freeze: 0.4, headliners: 0.59 });
  });

  it('makes every slot special for the all option, whose odds must add up to exactly 100%', async () => {
    renderPage(['catalogue', 'new']);
    const requirement = await screen.findByLabelText('Special player requirement');
    expect(screen.queryByLabelText('Guaranteed specials')).toBeNull();
    expect(screen.queryByRole('switch', { name: /^Specials only/ })).toBeNull();
    fireEvent.change(requirement, { target: { value: 'minimum' } });
    expect((screen.getByLabelText('Guaranteed special players') as HTMLInputElement).value).toBe('1');
    fireEvent.change(requirement, { target: { value: 'all' } });
    fireEvent.change(screen.getByLabelText('Items per pack'), { target: { value: '3' } });
    expect(await screen.findByText('Specials-only odds must add up to exactly 100%.')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('TOTW (%)'), { target: { value: '99.95' } });
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some(call => call.startsWith('POST packs/catalog '))).toBe(true));
    expect(posted().pack).toMatchObject({ size: 3, rare_slots: 3, promo_only: true, guaranteed_specials: 0 });
  });

  it('hides legacy item chances and clears them when staging a player pack', async () => {
    replies['GET packs/catalog'] = { status: 200, body: { ...catalog, packs: [{ ...catalog.packs[0],
      config: { ...catalog.packs[0]!.config, manager_chance: 0.5, chem_style_chance: 0.3 } }] } };
    renderPage(['catalogue', '3']);
    await screen.findByLabelText('Contents type');
    expect(screen.queryByLabelText('Manager chance')).toBeNull();
    expect(screen.queryByLabelText('Chemistry style chance')).toBeNull();
    await waitFor(() => expect(stageButton().disabled).toBe(false));
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some(call => call.startsWith('PUT packs/catalog/3 '))).toBe(true));
    const body = JSON.parse(calls.find(call => call.startsWith('PUT packs/catalog/3 '))!.slice('PUT packs/catalog/3 '.length));
    expect(body).toMatchObject({ pack: { manager_chance: 0, chem_style_chance: 0 } });
  });

  it('shows current and explicitly selected categories, retaining unavailable saved odds', async () => {
    replies['GET packs/catalog'] = { status: 200, body: { ...catalog, promos: [...catalog.promos,
      { code: 'team_of_the_year', name: 'TOTY', category: 'toty' },
      { code: 'toty_honourable_mentions', name: 'TOTY Honourable Mentions', category: 'toty_honourable_mentions' }] } };
    renderPage(['catalogue', 'new']);
    await screen.findByLabelText('TOTW (%)');
    expect(screen.queryByLabelText('TOTY (%)')).toBeNull();
    expect(screen.queryByLabelText('TOTY Honourable Mentions (%)')).toBeNull();
    fireEvent.change(screen.getByLabelText('TOTW (%)'), { target: { value: '50' } });
    const selected = screen.getByLabelText('Specific promos') as HTMLSelectElement;
    for (const option of selected.options) option.selected = option.value === 'team_of_the_year';
    fireEvent.change(selected);
    expect(screen.getByLabelText('TOTY (%)')).toBeTruthy();
    fireEvent.click(screen.getByRole('switch', { name: /^Include standard pool and current event/ }));
    expect(screen.queryByLabelText('TOTW (%)')).toBeNull();
    expect(screen.getByText(/Saved odds for unavailable categories/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Clear unavailable odds' }));
    expect(screen.queryByText(/Saved odds for unavailable categories/)).toBeNull();
  });

  it('opens a new player pick directly with choices and starts its odds from the pick template', async () => {
    renderPage(['catalogue', 'new'], 'type=picks');
    expect(await screen.findByRole('heading', { name: 'Create player pick' })).toBeTruthy();
    expect((screen.getByLabelText('Choices per pick') as HTMLInputElement).value).toBe('3');
    fireEvent.change(screen.getByLabelText('Choices per pick'), { target: { value: '4' } });
    await waitFor(() => expect(stageButton().disabled).toBe(false));
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some(call => call.startsWith('POST packs/catalog '))).toBe(true));
    const body = posted();
    expect(body.pack.player_picks).toEqual([{ count: 1, choice_count: 4, min_rating: 82 }]);
    expect(body.pack.category_chances).toEqual({ icon: 0.0005, totw: 0.03 });
    expect(window.location.hash).toBe('#/packs/catalogue?type=picks');
  });

  it('makes a fixed player pick from chosen cards, its choice count and rating following them', async () => {
    renderPage(['catalogue', 'new'], 'type=picks');
    fireEvent.change(await screen.findByLabelText('Choices'), { target: { value: 'fixed' } });
    expect(screen.queryByLabelText('Choices per pick')).toBeNull();
    for (const name of [/Lionel Messi 93/, /Lionel Messi 95/]) {
      fireEvent.click(screen.getByRole('button', { name: 'Add card…' }));
      const picker = (await screen.findAllByRole('dialog')).at(-1)!;
      fireEvent.change(within(picker).getByLabelText('Player name'), { target: { value: 'Messi' } });
      fireEvent.click(await within(picker).findByRole('button', { name }));
      await waitFor(() => expect(screen.queryAllByRole('dialog')).toHaveLength(0));
    }
    expect(screen.getByText('2 of 2 to 5 cards')).toBeTruthy();
    await waitFor(() => expect(stageButton().disabled).toBe(false));
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some(call => call.startsWith('POST packs/catalog '))).toBe(true));
    expect(posted().pack.player_picks).toEqual([{ count: 1, choices: [158023, 50490631], choice_count: 2, min_rating: 93 }]);
  });

  it('creates a mixed pack with category ranges and a rare count for the entire pack', async () => {
    renderPage(['catalogue', 'new']);
    fireEvent.change(await screen.findByLabelText('Contents type'), { target: { value: 'mixed' } });
    expect(screen.queryByLabelText('Manager chance')).toBeNull();
    fireEvent.change(screen.getByLabelText('Players maximum'), { target: { value: '5' } });
    await waitFor(() => expect(stageButton().disabled).toBe(false));
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some((call) => call.startsWith('POST packs/catalog '))).toBe(true));
    expect(posted().pack).toMatchObject({ size: 12, rare_slots: 1, manager_chance: 0, chem_style_chance: 0,
      mixed_contents: { tier: 'gold', players: { min: 3, max: 5 }, consumables: { min: 5, max: 9 },
        stadium: { min: 0, max: 3 }, staff: { min: 0, max: 2 } } });
  });

  it('edits a My Stadium pack: its slots, item types and pools, with the size following the slots', async () => {
    const stadium = { id: 83, grantOnly: false, inStore: true, art: 'special', description: 'My Stadium Pack', usedBy: [],
      config: { name: 'My Stadium Pack', price: 5000, size: 2, store_group: 'special', include_general_pool: false,
        vanity_pack: { slots: [{ subtypes: [506] }, { subtypes: [500], pools: [{ weight: 70, min: 6800000, max: 6809999 }, { weight: 30, max: 6799999 }] }] } } };
    replies['GET packs/catalog'] = { status: 200, body: { ...catalog, packs: [...catalog.packs, stadium],
      vanityTypes: [{ subtype: 500, name: 'Tifo', items: 368 }, { subtype: 505, name: 'Mow pattern', items: 3 }, { subtype: 506, name: 'Pitch line colour', items: 24 }] } };
    replies['POST packs/catalog/test'] = { status: 200, body: { items: [{ itemType: 'vanity', name: 'Pitch line colour 3' }, { itemType: 'vanity', name: 'Tifo 6800012' }] } };
    renderPage(['catalogue', '83']);
    expect((await screen.findByLabelText('Contents type') as HTMLSelectElement).value).toBe('vanity');
    expect(screen.queryByLabelText('Items per pack')).toBeNull();
    expect(screen.queryByText('Promo sources and odds')).toBeNull();
    fireEvent.change(screen.getByLabelText('Slot 2 pool 2 weight'), { target: { value: '40' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add slot' }));
    const third = screen.getByLabelText('Slot 3 item types') as HTMLSelectElement;
    for (const option of third.options) option.selected = option.value === '505';
    fireEvent.change(third);
    fireEvent.click(screen.getByRole('button', { name: 'Test contents' }));
    expect(await screen.findByText('Tifo 6800012')).toBeTruthy();
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some((call) => call.startsWith('PUT packs/catalog/83 '))).toBe(true));
    const put = JSON.parse(calls.find((call) => call.startsWith('PUT packs/catalog/83 '))!.slice('PUT packs/catalog/83 '.length));
    expect(put.pack).toEqual({ size: 3, vanity_pack: { slots: [{ subtypes: [506] },
      { subtypes: [500], pools: [{ weight: 70, min: 6800000, max: 6809999 }, { weight: 40, max: 6799999 }] }, { subtypes: [505] }] } });
  });

  it('keeps an impossible mixed composition in the form without staging it', async () => {
    renderPage(['catalogue', 'new']);
    fireEvent.change(await screen.findByLabelText('Contents type'), { target: { value: 'mixed' } });
    fireEvent.change(screen.getByLabelText('Players minimum'), { target: { value: '6' } });
    fireEvent.change(screen.getByLabelText('Consumables minimum'), { target: { value: '9' } });
    await waitFor(() => expect(stageButton().disabled).toBe(false));
    fireEvent.click(stageButton());
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'The item ranges cannot add up to the pack size.');
    expect(calls).toEqual([]);
  });

  it('creates two picks with explicit promo sources and previews separate sets of choices', async () => {
    renderPage(['catalogue', 'new'], 'type=picks');
    fireEvent.change(await screen.findByLabelText('Number of picks'), { target: { value: '2' } });
    await screen.findAllByLabelText('TOTW (%)');
    fireEvent.click(screen.getAllByRole('switch', { name: /^Include standard pool and current event/ })[0]!);
    const sources = screen.getAllByLabelText('Specific promos')[0] as HTMLSelectElement;
    sources.options[0]!.selected = true;
    fireEvent.change(sources);
    // The pack's sources now offer only TOTW: drop the Icon row and give the specials-only group all of its slots.
    fireEvent.click(screen.getAllByRole('button', { name: 'Clear unavailable odds' })[0]!);
    fireEvent.change(screen.getAllByLabelText('TOTW (%)')[0]!, { target: { value: '100' } });
    fireEvent.click(screen.getByRole('switch', { name: /^Specials only/ }));
    replies['POST packs/catalog/test'] = { status: 200, body: { items: [
      { name: 'First choice', rating: 84, pickIndex: 0 }, { name: 'Second choice', rating: 85, pickIndex: 0 },
      { name: 'Third choice', rating: 86, pickIndex: 1 }, { name: 'Fourth choice', rating: 87, pickIndex: 1 },
    ] } };
    fireEvent.click(screen.getByRole('button', { name: 'Test contents' }));
    expect(await screen.findByText('Pick 1: choose 1 of 2')).toBeTruthy();
    expect(screen.getByText('Pick 2: choose 1 of 2')).toBeTruthy();
    fireEvent.click(stageButton());
    await waitFor(() => expect(calls.some((call) => call.startsWith('POST packs/catalog '))).toBe(true));
    expect(posted().pack).toMatchObject({ size: 2, rare_slots: 0, include_general_pool: false, promo_codes: ['totw_5'], category_chances: { totw: 1 },
      player_picks: [{ count: 2, choice_count: 3, min_rating: 82, promo_only: true }] });
  });

  it('stages an edit with the catalogue revision, and explains a catalogue changed meanwhile', async () => {
    renderPage(['catalogue', '3']);
    fireEvent.change(await screen.findByLabelText('Name'), { target: { value: 'Renamed Gold Pack' } });
    await waitFor(() => expect(stageButton().disabled).toBe(false));
    replies['PUT packs/catalog/3'] = { status: 409, body: { error: 'The pack catalogue changed. Refresh before saving.' } };
    fireEvent.click(stageButton());
    expect(await screen.findByText(/catalogue changed.*Your values are kept/)).toBeTruthy();
    expect(calls).toEqual(['PUT packs/catalog/3 {"revision":"r1","pack":{"name":"Renamed Gold Pack"}}']);
    delete replies['PUT packs/catalog/3'];
    fireEvent.click(stageButton());
    await waitFor(() => expect(window.location.hash).toBe('#/packs/catalogue'));
  });

  it('test-draws the edited values without staging anything', async () => {
    renderPage(['catalogue', '3']);
    fireEvent.change(await screen.findByLabelText('Items per pack'), { target: { value: '3' } });
    await waitFor(() => expect((screen.getByRole('button', { name: 'Test contents' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Test contents' }));
    expect(await screen.findByText('Messi')).toBeTruthy();
    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatch(/^POST packs\/catalog\/test \{"packId":3,"pack":\{.*"size":3/);
  });

  it('hides a pack only after confirming, and never offers Hide on a locked pack', async () => {
    renderPage(['catalogue']);
    await screen.findByText('Gold Pack');
    expect(screen.getAllByRole('button', { name: 'Hide' })).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Hide' }));
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Hide Gold Pack?' })).getByRole('button', { name: 'Stage hide' }));
    await waitFor(() => expect(calls).toEqual(['DELETE packs/catalog/3 {"revision":"r1"}']));
  });

  const noBlockers = { id: 3, name: 'Gold Pack', kind: 'store', blockers: [], canDelete: true, canForceDelete: false, unopened: { copies: 0, clubs: 0 } };

  it('deletes a pack only after confirming, and disables Delete on a locked pack', async () => {
    replies['GET packs/catalog/3/delete-check'] = { status: 200, body: noBlockers };
    renderPage(['catalogue']);
    await screen.findByText('Gold Pack');
    const buttons = screen.getAllByRole('button', { name: 'Delete' });
    expect(buttons).toHaveLength(2);
    expect((buttons[1] as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(buttons[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Delete Gold Pack #3?' });
    expect(calls).toEqual([]);
    fireEvent.click(await within(dialog).findByRole('button', { name: 'Delete pack' }));
    await waitFor(() => expect(calls).toEqual(['POST packs/catalog/3/delete {"revision":"r1"}']));
  });

  it('explains why a pack cannot be deleted and offers no way to stage it', async () => {
    replies['GET packs/catalog/3/delete-check'] = { status: 200, body: { ...noBlockers, canDelete: false,
      blockers: [{ code: 'in_lineup', message: 'This pack is in the published store lineup. Hide it first.', overridable: false }] } };
    renderPage(['catalogue']);
    await screen.findByText('Gold Pack');
    fireEvent.click(screen.getAllByRole('button', { name: 'Delete' })[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Delete Gold Pack #3?' });
    expect(await within(dialog).findByText('This pack is in the published store lineup. Hide it first.')).toBeTruthy();
    expect(within(dialog).queryByRole('button', { name: 'Delete pack' })).toBeNull();
    expect(within(dialog).queryByRole('button', { name: 'Force delete' })).toBeNull();
    expect(calls).toEqual([]);
  });

  it('force deletes owned copies only after the pack id is typed, sending the counts it showed', async () => {
    replies['GET packs/catalog/3/delete-check'] = { status: 200, body: { ...noBlockers, canDelete: false, canForceDelete: true,
      unopened: { copies: 4, clubs: 2 }, blockers: [{ code: 'owned_copies', message: '4 unopened copies are owned by 2 clubs.', overridable: true }] } };
    renderPage(['catalogue']);
    await screen.findByText('Gold Pack');
    fireEvent.click(screen.getAllByRole('button', { name: 'Delete' })[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Delete Gold Pack #3?' });
    expect(await within(dialog).findByText(/4 unopened copies across 2 clubs are still owned/)).toBeTruthy();
    expect(within(dialog).getByText(/Coins and Aurora Tokens are not refunded/)).toBeTruthy();
    const force = within(dialog).getByRole('button', { name: 'Force delete' }) as HTMLButtonElement;
    expect(force.disabled).toBe(true);
    fireEvent.change(within(dialog).getByLabelText('Type 3 to confirm'), { target: { value: '3' } });
    expect(force.disabled).toBe(false);
    fireEvent.click(force);
    await waitFor(() => expect(calls).toEqual(['POST packs/catalog/3/delete {"revision":"r1","force":true,"copies":4,"clubs":2}']));
  });

  it('shows live offers separately and uses dated releases instead of activation switches', async () => {
    renderPage(['lineup']);
    expect(await screen.findByLabelText('Gold Pack coin price')).toBeTruthy();
    expect(screen.queryByRole('switch')).toBeNull();
    expect(await screen.findByRole('heading', { name: 'Scheduled' })).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Gold Pack coin price'), { target: { value: '8000' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save listing' }));
    await waitFor(() => expect(calls).toEqual(['PUT packs/lineup {"revision":"r1","listing":[{"packId":3,"enabled":true,"untradeable":null,"price":8000}]}']));
  });

  it('removes a pack from the store listing', async () => {
    renderPage(['lineup']);
    fireEvent.click(await screen.findByRole('button', { name: 'Remove Gold Pack from the store listing' }));
    expect(screen.getByText('No packs listed')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save listing' }));
    await waitFor(() => expect(calls).toEqual(['PUT packs/lineup {"revision":"r1","listing":[]}']));
  });

  it('simulates rolls', async () => {
    renderPage(['simulate']);
    fireEvent.click(await screen.findByRole('button', { name: 'Roll' }));
    expect(await screen.findByText('Kane')).toBeTruthy();
    expect(calls).toEqual(['POST packs/simulate {"packId":3,"rolls":3}']);
  });

  it('gifts every club in turn and lists a club the server refuses with its reason', async () => {
    replies['PATCH players/1002'] = { status: 409, body: { error: 'MY PACKS holds 100; a grant may leave at most 100.' } };
    renderPage(['gifts']);
    fireEvent.click(await screen.findByRole('button', { name: 'Gift to 2 clubs' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Gift to every club' }));
    await waitFor(() => expect(calls).toEqual(['PATCH players/1001 {"op":"grantPacks","packId":3,"count":1,"untradeable":false}', 'PATCH players/1002 {"op":"grantPacks","packId":3,"count":1,"untradeable":false}']));
    expect(await screen.findByText('MY PACKS holds 100; a grant may leave at most 100.')).toBeTruthy();
  });
});
