import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PromosPage, comparePromos } from './PromosPage';
import { bucketPercentage } from '../cards/CardsPage';
import { bucketCode, withBucket, type PromoDefinition } from './BucketEditor';
import { cardRestriction, changeMembers, groupCode } from './PromoGroupEditor';

let staged: Record<string, unknown>[];
let live: PromoDefinition;
let pendingDefinition: PromoDefinition | null;
const player = { cardKey: '123:84:3', resourceId: 123, name: 'Promo Player', rating: 84, rareflag: 3, position: 'ST', art: { resourceId: 123, assetId: 123, name: 'Promo Player', rating: 84, rareflag: 3, position: 'ST' } };

beforeEach(() => {
  localStorage.setItem('aurora21.panel.cards.view', 'table');
  staged = [];
  pendingDefinition = null;
  live = { name: 'Team of the Week 1', odds_category: 'totw', draw_weight: 1, status: 'active', buckets: [
    { code: 'rating_80_84', name: '80–84', draw_weight: 10, enabled: true, cards: [123] },
    { code: 'manual', name: 'Manual players', draw_weight: 1, enabled: true, cards: [] },
  ] };
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() })));
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
    const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });
    if (path === 'content/changeset/changes' && init?.method === 'PUT') {
      const body = JSON.parse(String(init.body));
      staged.push(body); pendingDefinition = body.after; return json({ pending: 1 });
    }
    if (path === 'promos/totw_1') return json({ definition: live });
    if (path === 'content/changeset') return json({ changeset: pendingDefinition ? { changes: [{ area: 'promo', key: { code: 'totw_1' }, operation: 'update', after: pendingDefinition }] } : null });
    if (path === 'promos/totw_1/buckets/from-ratings') return json({ buckets: [{ code: 'seed', cards: [123] }], samples: [player] });
    if (path === 'assets') return json({ available: true, frames: [], baseUrl: '/panel/assets/' });
    const body = path === 'promos' ? { definitions: [
      { code: 'totw_1', name: 'Team of the Week 1', category: 'totw', status: 'active', cards: 23, buckets: 3, artwork: [] },
      { code: 'gold_rare', name: 'Gold Rare', category: 'gold_rare', status: 'active', cards: 500, buckets: 4, artwork: [] },
    ] } : path.startsWith('cards?') && !path.includes('group=buckets') ? { total: 1, cards: [player] }
      : path.startsWith('cards?') ? { total: 71, poolSize: 71, cards: [], buckets: [
      { code: 'rating_80_84', name: '80–84', weight: 10, enabled: true, count: 70, total: 70, offset: 0, limit: 64, cards: [player] },
      { code: 'rating_85_89', name: '85–89', weight: 1, enabled: true, count: 1, total: 1, offset: 0, limit: 64, cards: [{ ...player, cardKey: '456:87:3', resourceId: 456, name: 'Other Player', rating: 87 }] },
      { code: 'manual', name: 'Manual players', weight: 0.000001, enabled: true, count: 0, total: 0, offset: 0, limit: 64, cards: [] },
    ] }
      : path === 'cards/specials' ? { types: [] } : path === 'cards/filters' ? { nations: [], leagues: [], clubs: [] } : {};
    return json(body);
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; localStorage.clear(); });
const show = (rest: string[] = [], params = new URLSearchParams()) => render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><PromosPage rest={rest} params={params} /></QueryClientProvider>);

it('shows searchable promo links and group creation controls', async () => {
  show();
  const tile = await screen.findByRole('link', { name: /Team of the Week 1/ });
  expect(tile.getAttribute('href')).toBe('#/promos/totw_1');
  expect(screen.getByRole('link', { name: /Gold Rare/ })).toBeTruthy();
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'week' } });
  expect(screen.queryByRole('link', { name: /Gold Rare/ })).toBeNull();
  expect(screen.getByRole('button', { name: 'Create promo' })).toBeTruthy();
});

it('creates a reusable group draft with one enabled bucket and selected native cards', async () => {
  show();
  fireEvent.click(await screen.findByRole('button', { name: 'Create promo' }));
  fireEvent.change(await screen.findByLabelText('Promo name'), { target: { value: 'Halloween Team 1' } });
  fireEvent.click(await within(screen.getByRole('dialog')).findByRole('checkbox', { name: /Promo Player/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Save promo' }));
  await waitFor(() => expect(staged).toHaveLength(1));
  expect(staged[0]).toMatchObject({ area: 'promo', operation: 'insert', key: { code: 'promo_halloween_team_1' } });
  expect((staged[0]?.after as PromoDefinition).buckets).toEqual([{ code: 'all_cards', name: 'All cards', draw_weight: 1, enabled: true, cards: [123] }]);
  expect(live.buckets[0]?.cards).toEqual([123]);
});

it('removes membership through a draft and clears only removed member reductions', async () => {
  live.card_reductions = { '123': 5 };
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Manage cards' }));
  await screen.findByLabelText('Promo name');
  fireEvent.click(screen.getByRole('button', { name: 'Remove cards' }));
  fireEvent.click(await within(screen.getByRole('dialog')).findByRole('checkbox', { name: /Promo Player/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Save promo' }));
  await waitFor(() => expect(staged).toHaveLength(1));
  const after = staged[0]?.after as PromoDefinition;
  expect(after.buckets[0]).toMatchObject({ cards: [], draw_weight: 10 });
  expect(after.card_reductions).toEqual({});
  expect(live.buckets[0]?.cards).toEqual([123]);
});

it('adds membership without duplicates or changes to existing weights and reductions', () => {
  const baseline = { ...live, card_reductions: { '123': 5 } };
  const after = changeMembers(baseline, [123, 456], 'manual', false);
  expect(after.buckets.map(b => b.cards)).toEqual([[123], [456]]);
  expect(after.buckets.map(b => b.draw_weight)).toEqual([10, 1]);
  expect(after.card_reductions).toEqual({ '123': 5 });
  expect(baseline.buckets[1]?.cards).toEqual([]);
});

it('shows pending-added native members for removal without published promo filtering', async () => {
  pendingDefinition = { ...live, buckets: [live.buckets[0]!, { ...live.buckets[1]!, cards: [456] }] };
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Manage cards' }));
  await screen.findByLabelText('Promo name');
  fireEvent.click(screen.getByRole('button', { name: 'Remove cards' }));
  fireEvent.click(await within(screen.getByRole('dialog')).findByRole('checkbox', { name: /Card 456/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Save promo' }));
  await waitFor(() => expect(staged).toHaveLength(1));
  expect((staged[0]?.after as PromoDefinition).buckets[1]?.cards).toEqual([]);
  expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('resourceIds=123,456'))).toBe(true);
});

it('retains native Icon and reward-only restrictions and stable group codes', () => {
  expect(groupCode('Hálloween Team 1')).toBe('promo_halloween_team_1');
  expect(cardRestriction('halloween', { ...player, rareflag: 12 })).toMatch(/Icon/);
  expect(cardRestriction('halloween', { ...player, rareflag: 18 })).toMatch(/reward/);
  expect(cardRestriction('halloween', { ...player, rareflag: 10 })).toMatch(/Developer/);
  expect(cardRestriction('fut_champions', { ...player, rareflag: 18 })).toBeNull();
  expect(cardRestriction('halloween', player)).toBeNull();
});

it('reuses the card browser and keeps the promo when clearing or sorting', async () => {
  show(['totw_1'], new URLSearchParams('min=80'));
  await screen.findByText('Promo Player');
  expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('promo=totw_1'))).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
  expect(window.location.hash).toBe('#/promos/totw_1');
  fireEvent.click(screen.getAllByRole('button', { name: /Rating/ })[0]!);
  expect(window.location.hash.startsWith('#/promos/totw_1?')).toBe(true);
});

it('groups cards into buckets with one view switch and independent pagination', async () => {
  show(['totw_1'], new URLSearchParams('bucketPage.rating_85_89=3'));
  await screen.findByText('Promo Player');
  expect(screen.getAllByRole('table')).toHaveLength(2);
  expect(screen.getAllByRole('group', { name: 'Show as' })).toHaveLength(1);
  expect(screen.getByText('Weight 0.000001')).toBeTruthy();
  expect(screen.getByText('Empty bucket')).toBeTruthy();
  fireEvent.click(within(screen.getByRole('region', { name: '80–84' })).getByRole('button', { name: 'Next' }));
  const query = new URLSearchParams(window.location.hash.split('?')[1]);
  expect(query.get('bucketPage.rating_80_84')).toBe('2');
  expect(query.get('bucketPage.rating_85_89')).toBe('3');
  fireEvent.click(screen.getByRole('button', { name: 'Cards' }));
  expect(screen.queryAllByRole('table')).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Table' }));
  expect(screen.getAllByRole('table')).toHaveLength(2);
});

it('previews a rating bucket and requires explicit moves before staging', async () => {
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Create bucket' }));
  fireEvent.change(await screen.findByLabelText('Bucket name'), { target: { value: 'Standout cards' } });
  fireEvent.click(screen.getByLabelText('Fill from a rating range'));
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('1 cards selected');
  const save = screen.getByRole('button', { name: 'Save bucket' }) as HTMLButtonElement;
  expect(save.disabled).toBe(true);
  fireEvent.click(screen.getByLabelText('Move these 1 cards into the new bucket'));
  fireEvent.click(save);
  await waitFor(() => expect(staged).toHaveLength(1));
  expect(staged[0]?.area).toBe('promo');
  const after = staged[0]?.after as PromoDefinition;
  expect(after.buckets.find(b => b.code === 'rating_80_84')?.cards).toEqual([]);
  expect(after.buckets.at(-1)).toMatchObject({ name: 'Standout cards', cards: [123], draw_weight: 1 });
  expect(live.buckets[0]?.cards).toEqual([123]);
});

it('creates an empty named bucket without invoking a rating rule', async () => {
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Create bucket' }));
  fireEvent.change(await screen.findByLabelText('Bucket name'), { target: { value: 'Manual cards' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('0 cards selected');
  fireEvent.click(screen.getByRole('button', { name: 'Save bucket' }));
  await waitFor(() => expect(staged).toHaveLength(1));
  expect((staged[0]?.after as PromoDefinition).buckets.at(-1)?.cards).toEqual([]);
  expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('from-ratings'))).toBe(false);
});

it('edits a bucket name and weight while retaining its identity, cards and pending changes', async () => {
  pendingDefinition = { ...live, buckets: [...live.buckets, { code: 'pending', name: 'Earlier edit', draw_weight: 2, enabled: true, cards: [] }] };
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit bucket 80–84' }));
  const name = await screen.findByLabelText('Bucket name') as HTMLInputElement;
  expect(name.value).toBe('80–84');
  expect((screen.getByLabelText('Relative weight') as HTMLInputElement).value).toBe('10');
  fireEvent.change(name, { target: { value: 'Standout players' } });
  fireEvent.change(screen.getByLabelText('Relative weight'), { target: { value: '0.25' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('1 cards selected');
  fireEvent.click(screen.getByRole('button', { name: 'Save bucket' }));
  await waitFor(() => expect(staged).toHaveLength(1));
  const after = staged[0]?.after as PromoDefinition;
  expect(after.buckets).toHaveLength(3);
  expect(after.buckets[0]).toMatchObject({ code: 'rating_80_84', name: 'Standout players', draw_weight: 0.25, cards: [123] });
  expect(after.buckets.at(-1)?.code).toBe('pending');
  expect(live.buckets[0]?.draw_weight).toBe(10);
});

it('moves an individually selected card from its donor bucket with confirmation', async () => {
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit bucket Manual players' }));
  await screen.findByLabelText('Bucket name');
  const dialog = screen.getByRole('dialog');
  fireEvent.click(await within(dialog).findByRole('checkbox', { name: /Promo Player/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('1 cards selected');
  const stage = screen.getByRole('button', { name: 'Save bucket' }) as HTMLButtonElement;
  expect(stage.disabled).toBe(true);
  fireEvent.click(screen.getByLabelText('Confirm these card moves'));
  fireEvent.click(stage);
  await waitFor(() => expect(staged).toHaveLength(1));
  expect((staged[0]?.after as PromoDefinition).buckets.map(b => b.cards)).toEqual([[], [123]]);
});

it('marks a player with a chosen reduction without changing its bucket or weight', async () => {
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit bucket 80–84' }));
  fireEvent.click(await screen.findByLabelText('Desirable card 123'));
  expect((screen.getByLabelText('Reduction for card 123') as HTMLInputElement).value).toBe('5');
  fireEvent.change(screen.getByLabelText('Reduction for card 123'), { target: { value: '10' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('1 cards selected');
  fireEvent.click(screen.getByRole('button', { name: 'Save bucket' }));
  await waitFor(() => expect(staged).toHaveLength(1));
  const after = staged[0]?.after as PromoDefinition;
  expect(after.card_reductions).toEqual({ '123': 10 });
  expect(after.buckets).toEqual(live.buckets);
  expect(live.card_reductions).toBeUndefined();
});

it('unmarks a player and keeps other saved reductions', async () => {
  live.card_reductions = { '123': 5, '456': 2 };
  live.buckets[1]!.cards = [456];
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit bucket 80–84' }));
  fireEvent.click(await screen.findByLabelText('Desirable card 123'));
  expect(screen.queryByLabelText('Reduction for card 123')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('1 cards selected');
  fireEvent.click(screen.getByRole('button', { name: 'Save bucket' }));
  await waitFor(() => expect(staged).toHaveLength(1));
  expect((staged[0]?.after as PromoDefinition).card_reductions).toEqual({ '456': 2 });
});

it('refuses invalid reduction factors and invalidates a preview after changing one', async () => {
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit bucket 80–84' }));
  fireEvent.click(await screen.findByLabelText('Desirable card 123'));
  for (const value of ['1', '0', '-2', '1000000', '1.1234567', '']) {
    fireEvent.change(screen.getByLabelText('Reduction for card 123'), { target: { value } });
    expect((screen.getByRole('button', { name: 'Preview bucket' }) as HTMLButtonElement).disabled).toBe(true);
  }
  fireEvent.change(screen.getByLabelText('Reduction for card 123'), { target: { value: '2' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('1 cards selected');
  fireEvent.change(screen.getByLabelText('Reduction for card 123'), { target: { value: '5' } });
  expect((screen.getByRole('button', { name: 'Save bucket' }) as HTMLButtonElement).disabled).toBe(true);
});

it('requires a destination when deselecting cards and retains promo membership', async () => {
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit bucket 80–84' }));
  await screen.findByLabelText('Bucket name');
  fireEvent.click(await within(screen.getByRole('dialog')).findByRole('checkbox', { name: /Promo Player/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('0 cards selected');
  fireEvent.click(screen.getByLabelText('Confirm these card moves'));
  const stage = screen.getByRole('button', { name: 'Save bucket' }) as HTMLButtonElement;
  expect(stage.disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('Move removed cards to'), { target: { value: 'manual' } });
  fireEvent.click(screen.getByLabelText('Confirm these card moves'));
  fireEvent.click(stage);
  await waitFor(() => expect(staged).toHaveLength(1));
  expect((staged[0]?.after as PromoDefinition).buckets.map(b => b.cards)).toEqual([[], [123]]);
});

it('invalidates an edited preview and refuses saving over a later promo change', async () => {
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit bucket 80–84' }));
  await screen.findByLabelText('Bucket name');
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('1 cards selected');
  fireEvent.change(screen.getByLabelText('Relative weight'), { target: { value: '0' } });
  expect((screen.getByRole('button', { name: 'Save bucket' }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('Relative weight'), { target: { value: '2' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('1 cards selected');
  live = { ...live, name: 'Changed promo' };
  fireEvent.click(screen.getByRole('button', { name: 'Save bucket' }));
  await screen.findByRole('alert');
  expect(staged).toHaveLength(0);
});

it('refuses a stale preview rather than overwriting another bucket change', async () => {
  show(['totw_1']);
  fireEvent.click(await screen.findByRole('button', { name: 'Create bucket' }));
  fireEvent.change(await screen.findByLabelText('Bucket name'), { target: { value: 'Manual cards' } });
  fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
  await screen.findByText('0 cards selected');
  live = { ...live, name: 'Changed promo' };
  fireEvent.click(screen.getByRole('button', { name: 'Save bucket' }));
  await screen.findByRole('alert');
  expect(staged).toHaveLength(0);
});

it('keeps earlier staged buckets when creating another bucket before publication', async () => {
  show(['totw_1']);
  for (const name of ['First manual bucket', 'Second manual bucket']) {
    fireEvent.click(await screen.findByRole('button', { name: 'Create bucket' }));
    fireEvent.change(await screen.findByLabelText('Bucket name'), { target: { value: name } });
    fireEvent.click(screen.getByRole('button', { name: 'Preview bucket' }));
    await screen.findByText('0 cards selected');
    fireEvent.click(screen.getByRole('button', { name: 'Save bucket' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  }
  expect(staged).toHaveLength(2);
  const after = staged[1]?.after as PromoDefinition;
  expect(after.buckets.map(b => b.name)).toContain('First manual bucket');
  expect(after.buckets.map(b => b.name)).toContain('Second manual bucket');
  expect(after.buckets[0]?.cards).toEqual([123]);
});

it('keeps disabled buckets and creates unique stable bucket identities', () => {
  const original = { ...live, buckets: [{ ...live.buckets[0]!, enabled: false }, ...live.buckets.slice(1)] };
  const result = withBucket(original, { code: 'single', name: 'Single', draw_weight: 2, cards: [123] });
  expect(result.buckets[0]).toMatchObject({ enabled: false, cards: [] });
  expect(original.buckets[0]?.cards).toEqual([123]);
  expect(bucketCode('Single', [{ code: 'bucket_single', name: 'Previous', draw_weight: 1, cards: [] }])).toBe('bucket_single_2');
});

it('calculates percentages from populated enabled buckets, independent of search matches', () => {
  const buckets = [
    { weight: 1, count: 20, total: 0, enabled: true }, { weight: 3, count: 5, total: 5, enabled: true },
    { weight: 50, count: 0, total: 0, enabled: true }, { weight: 20, count: 5, total: 5, enabled: false },
  ];
  expect(buckets.map(b => bucketPercentage(b, buckets))).toEqual([25, 75, 0, 0]);
});

it('keeps related promos together and Icons in tier order', () => {
  const promos = ['prime_icon_moments', 'ligue_1_tots', 'mid_icons', 'bundesliga_potm', 'base_icons', 'ultimate_tots', 'prime_icons', 'ligue_1_potm', 'totw_10', 'totw_2', 'fut_birthday', 'gold_rare', 'bronze_common', 'silver_rare', 'gold_common', 'silver_common', 'bronze_rare']
    .map(code => ({ code, name: code.replaceAll('_', ' ') }));
  const codes = promos.sort(comparePromos).map(p => p.code);
  expect(codes.slice(0, 6)).toEqual(['bronze_common', 'bronze_rare', 'silver_common', 'silver_rare', 'gold_common', 'gold_rare']);
  expect(codes.slice(codes.indexOf('base_icons'), codes.indexOf('base_icons') + 4)).toEqual(['base_icons', 'mid_icons', 'prime_icons', 'prime_icon_moments']);
  expect(codes.indexOf('ultimate_tots')).toBe(codes.indexOf('ligue_1_tots') + 1);
  expect(codes.indexOf('ligue_1_potm')).toBe(codes.indexOf('bundesliga_potm') + 1);
  expect(codes.indexOf('totw_10')).toBe(codes.indexOf('totw_2') + 1);
});

it('handles an unknown promo without loading the global card list', async () => {
  show(['missing']);
  await screen.findByText('Promo not found');
  expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('cards?'))).toBe(false);
});
