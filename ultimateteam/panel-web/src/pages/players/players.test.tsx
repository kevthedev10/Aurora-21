import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { ToastProvider } from '../../ui/Toast';
import { ClubPage } from './ClubPage';
import { PlayersList } from './PlayersList';
import { faceStats } from './InventoryTab';

// Shaped like OperatorPanel.Players.cs replies.
const detail = {
  id: 1001, discordId: '123456789012345678', name: 'Northbank', online: true, logins: 4, lastLogin: new Date().toISOString(),
  club: { name: 'Northbank', abbr: 'NOR', coins: 5000, points: 100, seasonXp: 2000, items: 40, tradePile: 2, unopenedPacks: 1 },
  inventory: { club: 38, new: 2, transfer: 2, total: 42, byKind: { player: 30, manager: 2, consumable: 8, clubItem: 2, other: 0 } },
  grantablePacks: [{ id: 3, name: 'Gold Pack' }],
  editOps: { ops: ['grantPacks', 'grantCard', 'grantSeasonXp', 'access', 'resetClub'], deltas: true },
  squadList: [], sessions: [{ remote: '127.0.0.1', connected_at: new Date().toISOString(), disconnected_at: null }],
  edits: [{ id: 5, patch: '{"op":"grantPacks","packId":3,"count":2}', requested_by: '127.0.0.1', result: 'ok', created_at: new Date().toISOString() }],
  access: { blocked: false, maintenanceBypass: false, note: '', playRolesRequired: true },
};
const players = { players: [
  { id: 1001, name: 'Northbank', online: true, club: { name: 'Northbank', coins: 5000 }, logins: 4 },
  { id: 1002, name: 'Kopite', online: false, club: { name: 'Kopite', coins: 90 }, access: { blocked: true } },
] };

let patches: unknown[] = [];
let patchReply: { status: number; body: unknown } | null = null;

beforeEach(() => {
  patches = [];
  patchReply = null;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '');
    const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    if (init?.method === 'PATCH') {
      const body = JSON.parse(String(init.body)) as Record<string, unknown>;
      patches.push(body);
      if (patchReply) return json(patchReply.status, patchReply.body);
      const club = { ...detail.club, ...(typeof body.coins === 'number' && { coins: body.coins }), ...(typeof body.coinsDelta === 'number' && { coins: detail.club.coins + body.coinsDelta }) };
      return json(200, { ...detail, club, access: body.op === 'access' ? { ...detail.access, blocked: body.blocked } : detail.access });
    }
    if (path === 'players') return json(200, players);
    if (path === 'players/1001') return json(200, detail);
    return json(404, { error: 'not found' });
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const wrap = (node: ReactNode) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider>{node}</ToastProvider></QueryClientProvider>);
};

describe('Players list', () => {
  it('filters and opens a club', async () => {
    wrap(<PlayersList />);
    await screen.findByText('persona 1002');
    fireEvent.click(screen.getByRole('button', { name: /Blocked/ }));
    expect(screen.queryByText('persona 1001')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /All/ }));
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'north' } });
    fireEvent.click(screen.getByRole('row', { name: 'Open Northbank' }));
    expect(window.location.hash).toBe('#/players/1001');
  });
});

describe('Club page', () => {
  it('requires confirmation before deleting club contents and refreshes the returned state', async () => {
    const base = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (url, init) => String(url) === '/panel/api/players/1001' && init?.method === 'GET'
      ? new Response(JSON.stringify({ ...detail, online: false }), { status: 200 }) : base(url, init));
    patchReply = { status: 200, body: { ...detail, online: false,
      club: { ...detail.club, coins: 0, points: 0, items: 0, unopenedPacks: 0, seasonXp: 0 }, inventory: { total: 0 } } };
    wrap(<ClubPage persona={1001} tab="overview" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Reset club' }));
    let dialog = screen.getByRole('dialog', { name: 'Reset club for Northbank?' });
    expect(within(dialog).getByText(/permanently deletes.*persona 1001/)).toBeTruthy();
    expect(patches).toEqual([]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(patches).toEqual([]);
    fireEvent.click(await screen.findByRole('button', { name: 'Reset club' }));
    dialog = screen.getByRole('dialog', { name: 'Reset club for Northbank?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete club contents' }));
    await waitFor(() => expect(patches).toEqual([{ op: 'resetClub', confirm: true, confirmPersonaId: 1001 }]));
    await screen.findByText('Club reset. The player must complete onboarding again.');
    expect(screen.queryByText('5,000')).toBeNull();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('shows a refused club reset in the confirmation dialog without clearing balances', async () => {
    const base = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (url, init) => String(url) === '/panel/api/players/1001' && init?.method === 'GET'
      ? new Response(JSON.stringify({ ...detail, online: false }), { status: 200 }) : base(url, init));
    patchReply = { status: 409, body: { error: 'The player must leave FIFA 21 before resetting their club.' } };
    wrap(<ClubPage persona={1001} tab="overview" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Reset club' }));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete club contents' }));
    expect(await within(dialog).findByRole('alert')).toBeTruthy();
    expect(screen.getByText('5,000')).toBeTruthy();
  });

  it('disables club deletion while the player is online', async () => {
    wrap(<ClubPage persona={1001} tab="overview" />);
    const button = await screen.findByRole('button', { name: 'Reset club' });
    expect((button as HTMLButtonElement).disabled).toBe(true);
    expect(patches).toEqual([]);
  });

  it('offers access and history without requesting nonexistent club data', async () => {
    const base = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (url, init) => String(url) === '/panel/api/players/1001' && init?.method === 'GET'
      ? new Response(JSON.stringify({ ...detail, club: null }), { status: 200 }) : base(url, init));
    wrap(<ClubPage persona={1001} tab="inventory" />);
    await screen.findByText('No club yet');
    expect(screen.getAllByRole('tab').map(tab => tab.textContent)).toEqual(['Access & sessions', 'Log1']);
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/inventory') || String(url).includes('/modes/'))).toBe(false);
  });

  it('shows an unopened-pack load error rather than an empty club', async () => {
    wrap(<ClubPage persona={1001} tab="packs" />);
    await screen.findByText('Unopened packs could not be loaded');
    expect(screen.queryByText('No unopened packs')).toBeNull();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
  });

  it('blocks reset controls while the player is online', async () => {
    const base = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (url, init) => String(url) === '/panel/api/modes/1001'
      ? new Response(JSON.stringify({ rivals: {}, draft: [] }), { status: 200 }) : base(url, init));
    wrap(<ClubPage persona={1001} tab="modes" />);
    await screen.findByText('The player must leave FIFA 21 before resetting competition progress.');
    for (const button of screen.getAllByRole('button', { name: /Reset progress|Reset runs/ })) expect((button as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows identity, balances and the tabs', async () => {
    wrap(<ClubPage persona={1001} tab={undefined} />);
    expect(await screen.findByRole('heading', { name: 'Northbank', level: 1 })).toBeTruthy();
    expect(screen.getByText('5,000')).toBeTruthy();
    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['Overview', 'Inventory', 'Packs & gifts', 'Modes', 'Access & sessions', 'Log1']);
  });

  it('sets only the balances that changed', async () => {
    wrap(<ClubPage persona={1001} tab="overview" />);
    const form = (await screen.findByRole('heading', { name: 'Set balances' })).closest('section')!;
    fireEvent.change(within(form).getByLabelText('Coins'), { target: { value: '1234' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(patches).toEqual([{ coins: 1234 }]));
  });

  it('validates a Season XP grant and refreshes its total after applying', async () => {
    wrap(<ClubPage persona={1001} tab="overview" />);
    const form = (await screen.findByRole('heading', { name: 'Season Progress XP' })).closest('section')!;
    const input = within(form).getByLabelText('XP to grant') as HTMLInputElement;
    const button = within(form).getByRole('button', { name: 'Grant Season XP' });
    for (const value of ['', '0', '-5', '1.5', '100000000']) {
      fireEvent.change(input, { target: { value } });
      fireEvent.click(button);
      expect(patches).toEqual([]);
    }
    patchReply = { status: 200, body: { ...detail, club: { ...detail.club, seasonXp: 3500 } } };
    fireEvent.change(input, { target: { value: '1500' } });
    fireEvent.click(button);
    await waitFor(() => expect(patches).toEqual([{ op: 'grantSeasonXp', amount: 1500 }]));
    await waitFor(() => expect(within(form).getByText('3,500')).toBeTruthy());
    expect(input.value).toBe('');
  });

  it('checks add and take away before sending, and keeps the amounts after a refusal', async () => {
    wrap(<ClubPage persona={1001} tab="overview" />);
    const form = (await screen.findByRole('heading', { name: 'Add or take away' })).closest('section')!;
    const coins = within(form).getByLabelText('Coins +/−') as HTMLInputElement;
    fireEvent.change(coins, { target: { value: '0' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Apply' }));
    expect(within(form).getByRole('alert').textContent).toContain('not 0');
    expect(patches).toEqual([]);
    patchReply = { status: 400, body: { error: 'coinsDelta must be a non-zero whole number.' } };
    fireEvent.change(coins, { target: { value: '-500' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(within(form).getByRole('alert').textContent).toContain('coinsDelta must be'));
    expect(patches).toEqual([{ coinsDelta: -500 }]);
    expect(coins.value).toBe('-500');
  });

  it('clears the amounts when the result is unknown, so a second click cannot grant twice', async () => {
    wrap(<ClubPage persona={1001} tab="overview" />);
    const form = (await screen.findByRole('heading', { name: 'Add or take away' })).closest('section')!;
    const coins = within(form).getByLabelText('Coins +/−') as HTMLInputElement;
    patchReply = { status: 500, body: { error: 'The panel hit an error; see the server log.' } };
    fireEvent.change(coins, { target: { value: '5000' } });
    fireEvent.click(within(form).getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(within(form).getByRole('alert')).toBeTruthy());
    expect(coins.value).toBe('');
  });

  it('asks before blocking, then sends the access edit', async () => {
    wrap(<ClubPage persona={1001} tab="access" />);
    fireEvent.click(await screen.findByRole('switch', { name: /Blocked/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Block player' }));
    const dialog = await screen.findByRole('dialog', { name: 'Block Northbank?' });
    expect(patches).toEqual([]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Block player' }));
    await waitFor(() => expect(patches).toEqual([{ op: 'access', blocked: true, maintenanceBypass: false, note: '' }]));
  });

  it('describes past edits in words', async () => {
    wrap(<ClubPage persona={1001} tab="log" />);
    expect(await screen.findByText('Gifted 2 × Gold Pack')).toBeTruthy();
  });
});

describe('Club tabs that call their own routes', () => {
  it('gifts packs within the server limits', async () => {
    const overview = { packs: { packs: [{ id: 3, name: 'Gold Pack', items: 12, rare: 1, coins: 7500, points: 150 }] } };
    const inventory = { piles: { club: [] }, packs: [{ packId: 3, count: 1, name: 'Gold Pack' }] };
    const base = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (url, init) => {
      const path = String(url).replace('/panel/api/', '');
      if (path === 'packs') return new Response(JSON.stringify(overview), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (path.startsWith('players/1001/inventory')) return new Response(JSON.stringify({ ...inventory, packs: inventory.packs.map(pack => ({ ...pack, count: patches.length ? 3 : 1 })) }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      return base(url, init);
    });
    wrap(<ClubPage persona={1001} tab="packs" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Gift packs & picks' }));
    const dialog = await screen.findByRole('dialog', { name: 'Gift packs & picks to Northbank' });
    await within(dialog).findByText(/12 items, at least 1 rare/);
    fireEvent.change(within(dialog).getByLabelText('How many'), { target: { value: '100' } });
    expect((within(dialog).getByRole('button', { name: 'Gift' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(within(dialog).getByLabelText('How many'), { target: { value: '2' } });
    fireEvent.click(within(dialog).getByLabelText('Untradeable'));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Gift 2 packs' }));
    await waitFor(() => expect(patches).toEqual([{ op: 'grantPacks', packId: 3, count: 2, untradeable: true }]));
    await screen.findByRole('row', { name: 'Gold Pack 3 3' });
  });

  it('resets a mode only after confirming', async () => {
    const posts: string[] = [];
    const base = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (url, init) => {
      const path = String(url).replace('/panel/api/', '');
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (path === 'modes/1001' ) return json({ rivals: { division: 5, wins: 3 }, squadBattles: null, champions: null, draft: [], pending: { rivals: false } });
      if (path === 'players/1001' && init?.method === 'GET') return json({ ...detail, online: false });
      if (init?.method === 'POST') { posts.push(`${path} ${String(init.body)}`); return json({ ok: true }); }
      return base(url, init);
    });
    wrap(<ClubPage persona={1001} tab="modes" />);
    const rivals = (await screen.findByRole('heading', { name: 'Division Rivals' })).closest('section')!;
    fireEvent.click(within(rivals).getByRole('button', { name: 'Reset progress' }));
    const dialog = await screen.findByRole('dialog', { name: 'Reset Division Rivals for Northbank?' });
    expect(posts).toEqual([]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reset progress' }));
    await waitFor(() => expect(posts).toEqual(['modes/1001/rivals/reset {"confirm":true}']));
  });
});

describe('Club inventory', () => {
  const pile = { piles: { club: [
    { id: 11, resourceId: 158023, name: 'Messi', rating: 93, position: 'RW', kind: 'player', inSquads: ['active'], removable: true, attributes: [85, 92, 91, 95, 38, 65] },
    { id: 12, resourceId: 5001, name: 'LW → LM', rating: 75, kind: 'consumable', typeName: 'Position change', removable: false },
    { id: 13, resourceId: 5002013, name: 'Premier League', rating: 75, kind: 'consumable', typeName: 'Manager league', listed: true, removable: false },
    { id: 14, resourceId: 1234, name: 'Tottenham Home Kit', rating: 75, kind: 'clubItem', typeName: 'Kit', equipped: true, removable: false },
    { id: 15, resourceId: 1001067, name: 'Antonio Conte', rating: 80, kind: 'manager', typeName: 'Manager', removable: false },
  ] } };
  beforeEach(() => {
    const base = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (url, init) => {
      const path = String(url).replace('/panel/api/', '');
      if (path === 'players/1001' && init?.method === 'GET')
        return new Response(JSON.stringify({ ...detail, editOps: { ops: [...detail.editOps.ops, 'removeItems'], deltas: true } }), { status: 200 });
      if (path.startsWith('players/1001/inventory')) return new Response(JSON.stringify(pile), { status: 200, headers: { 'Content-Type': 'application/json' } });
      return base(url, init);
    });
  });

  it('labels the face stats of the dialog card, with goalkeeper labels for a keeper', () => {
    expect(faceStats({ id: 1, position: 'RW', attributes: [85, 92, 91, 95, 38, 65] }).map((stat) => `${stat.label} ${stat.value}`))
      .toEqual(['PAC 85', 'SHO 92', 'PAS 91', 'DRI 95', 'DEF 38', 'PHY 65']);
    expect(faceStats({ id: 2, position: 'GK', attributes: [76, 69, 75, 77, 31, 73] })[0]).toEqual({ label: 'DIV', value: 76 });
    expect(faceStats({ id: 3, position: 'CM' })).toEqual([]);
  });

  it('splits a pile into the Cards kinds', async () => {
    wrap(<ClubPage persona={1001} tab="inventory" />);
    await screen.findByText('Messi');
    expect(screen.getByRole('tab', { name: 'Players1' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Consumables2' })).toBeTruthy();
    expect(screen.queryByText('Antonio Conte')).toBeNull();
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Club customisation1' }));
    expect(await screen.findByText('Tottenham Home Kit')).toBeTruthy();
    expect(screen.getByText('Equipped')).toBeTruthy();
    expect(screen.queryByText('Messi')).toBeNull();
  });

  it('removes a player card after confirmation and offers no removal for consumables or managers', async () => {
    wrap(<ClubPage persona={1001} tab="inventory" />);
    await screen.findByText('Messi');
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Managers1' }));
    fireEvent.click(await screen.findByRole('row', { name: 'Open Antonio Conte' }));
    await screen.findByRole('dialog', { name: 'Antonio Conte' });
    expect(screen.queryByRole('button', { name: 'Remove from club' })).toBeNull();
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Antonio Conte' })).toBeNull());

    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Consumables2' }));
    fireEvent.click(await screen.findByRole('row', { name: 'Open LW → LM' }));
    await screen.findByRole('dialog', { name: 'LW → LM' });
    expect(screen.queryByRole('button', { name: 'Remove from club' })).toBeNull();
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'LW → LM' })).toBeNull());

    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Players1' }));
    if (screen.queryByRole('button', { name: 'Table' })) fireEvent.click(screen.getByRole('button', { name: 'Table' }));
    fireEvent.click(await screen.findByRole('row', { name: 'Open Messi' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Remove from club' }));
    const confirm = await screen.findByRole('dialog', { name: 'Remove Messi from Northbank?' });
    expect(within(confirm).getByText(/pays nothing. It leaves every squad it is in./)).toBeTruthy();
    fireEvent.click(within(confirm).getByRole('button', { name: 'Remove item' }));
    await waitFor(() => expect(patches).toEqual([{ op: 'removeItems', items: [11] }]));
  });

  it('keeps one player removal action and takes eligible non-player items away while protecting equipped and listed items', async () => {
    const base = vi.mocked(fetch).getMockImplementation()!;
    vi.mocked(fetch).mockImplementation(async (url, init) => {
      if (String(url).replace('/panel/api/', '') === 'players/1001' && init?.method === 'GET')
        return new Response(JSON.stringify({ ...detail, editOps: { ops: [...detail.editOps.ops, 'removeItems', 'takeItem'], deltas: true } }), { status: 200 });
      return base(url, init);
    });
    wrap(<ClubPage persona={1001} tab="inventory" />);
    await screen.findByText('Messi');
    fireEvent.click(screen.getByRole('row', { name: 'Open Messi' }));
    expect(await screen.findByRole('button', { name: 'Remove from club' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Take away' })).toBeNull();
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Club customisation1' }));
    fireEvent.click(await screen.findByRole('row', { name: 'Open Tottenham Home Kit' }));
    await screen.findByRole('dialog', { name: 'Tottenham Home Kit' });
    expect(screen.queryByRole('button', { name: 'Remove from club' })).toBeNull();
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Consumables2' }));
    fireEvent.click(await screen.findByRole('row', { name: 'Open Premier League' }));
    await screen.findByRole('dialog', { name: 'Premier League' });
    expect(screen.queryByRole('button', { name: 'Remove from club' })).toBeNull();
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    fireEvent.click(screen.getByRole('row', { name: 'Open LW → LM' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Remove from club' }));
    const confirm = await screen.findByRole('dialog', { name: 'Remove LW → LM from Northbank?' });
    fireEvent.click(within(confirm).getByRole('button', { name: 'Remove item' }));
    await waitFor(() => expect(patches).toEqual([{ op: 'takeItem', itemId: 12 }]));
  });
});
