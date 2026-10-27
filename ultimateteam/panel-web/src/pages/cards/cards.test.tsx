import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '../../ui/Toast';
import { CardsPage, cardQuery } from './CardsPage';
import { cardForStage, fileAsHead } from './CardEditor';
import { editableCard } from '../../../test/cardFixtures';

// Shaped like OperatorPanel.Cards.cs replies.
const messi = { cardKey: '158023:93:1', resourceId: 158023, assetId: 158023, name: 'Messi', rating: 93, rareflag: 1, position: 'RW', attributes: [85, 92, 91, 95, 38, 65], skillMoves: 4, weakFoot: 4, workRateAttack: 'Medium', workRateDefense: 'Low', nation: 'Argentina', leagueId: 53, inPacks: true };
const toty = { ...messi, cardKey: '117598535:98:5', resourceId: 117598535, rating: 98, rareflag: 5, special: 'Team of the Year' };
let patches: string[] = [];

beforeEach(() => {
  patches = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    if (init?.method === 'PATCH') { patches.push(`${path} ${String(init.body)}`); return json({ id: 1001 }); }
    if (path.startsWith('cards/specials')) return json({ types: [{ flag: 5, name: 'Team of the Year', available: 12 }] });
    if (path === 'cards/filters') return json({ nations: [{ id: 52, name: 'Argentina' }], leagues: [{ id: 13, name: 'Premier League' }, { id: 53, name: 'LaLiga' }], clubs: [{ id: 18, name: 'Tottenham', leagueId: 13, leagueName: 'Premier League' }] });
    if (path.startsWith('cards?')) {
      const q = new URLSearchParams(path.split('?')[1]).get('q');
      if (q === '158023:93:1' || new URLSearchParams(path.split('?')[1]).get('resourceId') === '158023') return json({ cards: [messi], attrs: ['PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY'], grant: { countMax: 24 } });
      if (q === '158023') return json({ cards: [messi, toty] });
      return json({ total: 2, poolSize: 22500, positions: ['GK', 'RW'], cards: [messi, toty] });
    }
    if (path.startsWith('cards/items?')) {
      const tab = new URLSearchParams(path.split('?')[1]).get('tab');
      if (tab === 'consumables') return json({ tab, total: 2, types: [{ value: 'chemistry_style', label: 'Chemistry style', count: 1 }, { value: 'position_modifier', label: 'Position change', count: 1 }, { value: 'manager_league', label: 'Manager league', count: 1 }],
        items: [{ key: 'chemistry_style:5003', type: 'chemistry_style', typeName: 'Chemistry style', name: 'Hunter', rating: 75, rareflag: 1, resourceId: 5003, forcedUntradeable: false, countMax: 24 },
          { key: 'position_modifier:5001', type: 'position_modifier', typeName: 'Position change', name: 'LW → LM', rating: 75, rareflag: 1, resourceId: 5001, forcedUntradeable: false, countMax: 24 },
          { key: 'manager_league:5002013', type: 'manager_league', typeName: 'Manager league', name: 'Premier League', rating: 75, rareflag: 1, resourceId: 5002013, forcedUntradeable: false, countMax: 24 }] });
      if (tab === 'customisation') return json({ tab, total: 2, types: [{ value: 'kit', label: 'Kit', count: 1 }, { value: 'vanity:500', label: 'My Stadium: Tifo', count: 1 }],
        items: [{ key: 'kit:1234', type: 'kit', typeName: 'Kit', name: 'Tottenham Home Kit', team: 'Tottenham', rating: 75, rareflag: 0, resourceId: 1234, forcedUntradeable: false, countMax: 1 },
          { key: 'vanity:500:1041', type: 'vanity:500', typeName: 'My Stadium: Tifo', name: 'Tifo 1041', rating: 65, rareflag: 0, resourceId: 1041, forcedUntradeable: true, countMax: 1 }] });
      return json({ tab, total: 1, types: [{ value: 'manager', label: 'Manager', count: 1 }],
        items: [{ key: 'manager:1001067', type: 'manager', typeName: 'Manager', name: 'Antonio Conte', rating: 80, rareflag: 1, resourceId: 1001067, nation: 'Italy', league: 'Serie A', forcedUntradeable: false, countMax: 24 }] });
    }
    if (path === 'players') return json({ players: [{ id: 1001, name: 'Northbank', club: { name: 'Northbank' } }, { id: 1003, name: 'No club yet' }] });
    if (path === 'assets') return json({ available: false });
    return new Response('{}', { status: 404 });
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderPage = (rest: string[], params = new URLSearchParams()) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider><CardsPage rest={rest} params={params} /></ToastProvider></QueryClientProvider>);
};

describe('card filters', () => {
  it('turn the address into the server query', () => {
    expect(cardQuery(new URLSearchParams('q=messi&pos=RW&type=5&min=80&page=3')).toString()).toBe('limit=64&offset=128&q=messi&pos=RW&special=5&min=80');
    expect(cardQuery(new URLSearchParams('type=rare')).get('rare')).toBe('1');
    expect(cardQuery(new URLSearchParams('type=common')).get('rare')).toBe('0');
    expect(cardQuery(new URLSearchParams('type=base')).get('special')).toBe('base');
    expect(cardQuery(new URLSearchParams('sort=PAC&direction=asc')).get('sort')).toBe('PAC');
    expect(cardQuery(new URLSearchParams('sort=PAC&direction=asc')).get('direction')).toBe('asc');
    expect(cardQuery(new URLSearchParams('sort=unknown')).has('sort')).toBe(false);
    expect(cardQuery(new URLSearchParams('min=abc')).has('min')).toBe(false);
    for (const page of ['Infinity', 'NaN', '1.5', '-2', '0']) expect(cardQuery(new URLSearchParams({ page })).get('offset')).toBe('0');
    expect(Number(cardQuery(new URLSearchParams('page=999999999')).get('offset'))).toBeLessThanOrEqual(1_000_000);
  });

  it('write a new filter into the address, starting again at page 1', async () => {
    window.location.hash = '#/cards?page=2';
    renderPage([], new URLSearchParams('page=2'));
    await screen.findAllByText('Messi', { selector: 'b' });
    fireEvent.click(screen.getByLabelText('Type'));
    fireEvent.click(screen.getByRole('option', { name: 'Team of the Year (12)' }));
    expect(window.location.hash).toBe('#/cards?type=5');
  });

  it('queries grouped positions and every identity filter together', () => {
    const query = cardQuery(new URLSearchParams('pos=ST,CF&nation=52&league=13&team=18&page=2'));
    expect(query.get('pos')).toBe('ST,CF');
    expect(query.get('nation')).toBe('52');
    expect(query.get('league')).toBe('13');
    expect(query.get('team')).toBe('18');
    expect(query.get('offset')).toBe('64');
  });

  it('searches clubs and selects their league even when another league was selected', async () => {
    renderPage([], new URLSearchParams('league=53&page=2'));
    await screen.findAllByText('Messi', { selector: 'b' });
    fireEvent.click(screen.getByLabelText('Club'));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search club' }), { target: { value: 'totten' } });
    fireEvent.click(await screen.findByRole('option', { name: 'Tottenham · Premier League' }));
    const query = new URLSearchParams(window.location.hash.split('?')[1]);
    expect(query.get('team')).toBe('18');
    expect(query.get('league')).toBe('13');
    expect(query.has('page')).toBe(false);
  });

  it('searches nations with keyboard selection and clears all filters', async () => {
    renderPage([], new URLSearchParams('type=5&pos=ST,CF&league=13&team=18'));
    await screen.findAllByText('Messi', { selector: 'b' });
    fireEvent.click(screen.getByLabelText('Nation'));
    const search = screen.getByRole('searchbox', { name: 'Search nation' });
    fireEvent.change(search, { target: { value: 'argent' } });
    fireEvent.keyDown(search, { key: 'Enter' });
    expect(new URLSearchParams(window.location.hash.split('?')[1]).get('nation')).toBe('52');
    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(window.location.hash).toBe('#/cards');
  });

  it('shows skill moves, weak foot, work rates and all face stats in the table', async () => {
    renderPage([]);
    await screen.findAllByText('Messi', { selector: 'b' });
    for (const title of ['SKI', 'WF', 'WR', 'PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY']) expect(screen.getByRole('columnheader', { name: title })).toBeTruthy();
    expect(screen.getAllByText('4 ★').length).toBe(4);
    expect(screen.getAllByText('M / L').length).toBe(2);
    expect(screen.getAllByText('95').length).toBe(2);
    expect(screen.getByRole('columnheader', { name: 'Name' })).toBeTruthy();
    expect(screen.queryByText('158023:93:1')).toBeNull();
    expect(screen.getAllByText('Argentina').length).toBe(2);
    expect(screen.getByRole('button', { name: 'Table' }).getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByRole('button', { name: 'Cards' }).hasAttribute('disabled')).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Rating' }));
    const sort = new URLSearchParams(window.location.hash.split('?')[1]);
    expect(sort.get('sort')).toBe('rating');
    expect(sort.get('direction')).toBe('asc');
    expect(sort.has('page')).toBe(false);
  });

  it('shows invalid rating ranges instead of silently querying different values', async () => {
    renderPage([], new URLSearchParams('min=90&max=80'));
    expect(await screen.findByText('The minimum rating cannot exceed the maximum.')).toBeTruthy();
    expect(vi.mocked(fetch).mock.calls.some(([path]) => String(path).startsWith('/panel/api/cards?'))).toBe(false);
  });
});

describe('card detail', () => {
  it('keeps an old card address usable after a rating edit by resolving its resource id', async () => {
    const original = fetch;
    const updated = { ...messi, rating: 94, cardKey: '158023:94:1' };
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const params = new URLSearchParams(url.split('?')[1]);
      if (url.startsWith('/panel/api/cards?') && params.get('resourceId') === '158023') return new Response(JSON.stringify({ cards: [updated] }), { status: 200 });
      return original(url, init);
    }));
    renderPage(['158023:93:1']);
    await screen.findByRole('heading', { name: 'Messi 94' });
    expect(screen.getByText('158023:94:1')).toBeTruthy();
  });

  it('reports a failed versions request instead of claiming there are no other cards', async () => {
    const original = fetch;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => url.includes('q=158023&')
      ? new Response(JSON.stringify({ error: 'Versions unavailable' }), { status: 500 }) : original(url, init)));
    renderPage(['158023:93:1']);
    await screen.findByText('Versions unavailable (HTTP 500)');
    expect(screen.queryByText('This player has no other card.')).toBeNull();
  });

  it('shows the face stats and other versions, and grants to the chosen club', async () => {
    renderPage(['158023:93:1']);
    expect(await screen.findByRole('heading', { name: 'Messi 93', level: 1 })).toBeTruthy();
    expect(screen.getByText('Dribbling')).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: 'Messi 98 Team of the Year' }));
    expect(window.location.hash).toBe('#/cards/117598535%3A98%3A5');
    fireEvent.click(screen.getByRole('button', { name: 'Grant to a club' }));
    const dialog = await screen.findByRole('dialog', { name: 'Grant Messi 93' });
    await within(dialog).findByLabelText('Club');
    // Only players with a club can receive a card, and a card is granted as one copy.
    expect(within(dialog).queryByText('No club yet')).toBeNull();
    expect(within(dialog).queryByLabelText('How many')).toBeNull();
    fireEvent.click(within(dialog).getByLabelText('Untradeable'));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Grant to Northbank' }));
    await waitFor(() => expect(patches).toEqual(['players/1001 {"op":"grantCard","card":"158023:93:1","count":1,"untradeable":true}']));
  });
});

describe('consumables, club customisation and managers', () => {
  it('opens a list-only tab from the address, never as a card key', async () => {
    renderPage(['managers']);
    expect(await screen.findByText('Antonio Conte')).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Managers' }).getAttribute('aria-selected')).toBe('true');
    for (const header of ['Name', 'Rating', 'Nation', 'League', 'Rarity', 'Resource id']) expect(screen.getByRole('columnheader', { name: header })).toBeTruthy();
    expect(screen.queryByRole('link', { name: /Edit/ })).toBeNull();
    expect(vi.mocked(fetch).mock.calls.some(([path]) => String(path).startsWith('/panel/api/cards?'))).toBe(false);
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Consumables' }));
    fireEvent.click(screen.getByRole('tab', { name: 'Consumables' }));
    expect(window.location.hash).toBe('#/cards/consumables');
  });

  it('keeps the same header on every tab, so switching never moves the tab menu', async () => {
    const players = renderPage([]);
    await screen.findAllByText('Messi', { selector: 'b' });
    const header = (document.querySelector('header p:last-child') as HTMLElement).textContent;
    players.unmount();
    renderPage(['managers']);
    await screen.findByText('Antonio Conte');
    expect((document.querySelector('header p:last-child') as HTMLElement).textContent).toBe(header);
    expect(screen.getByRole('table').getAttribute('style')).toContain('--table-min-width');
  });

  it('filters by type from the address and grants copies with the chosen tradeability', async () => {
    renderPage(['consumables'], new URLSearchParams('type=position_modifier'));
    expect(await screen.findByText('LW → LM')).toBeTruthy();
    expect(screen.queryByText('Hunter')).toBeNull();
    expect(screen.queryByText('Premier League')).toBeNull();
    fireEvent.click(screen.getByRole('row', { name: 'Grant LW → LM' }));
    const dialog = await screen.findByRole('dialog', { name: 'Grant LW → LM' });
    fireEvent.change(await within(dialog).findByLabelText('How many'), { target: { value: '3' } });
    fireEvent.click(within(dialog).getByLabelText('Untradeable'));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Grant to Northbank' }));
    await waitFor(() => expect(patches).toEqual(['players/1001 {"op":"grantItem","item":"position_modifier:5001","count":3,"untradeable":true}']));
  });

  it('lists manager league cards among the consumables and grants them like any consumable', async () => {
    renderPage(['consumables'], new URLSearchParams('type=manager_league'));
    fireEvent.click(await screen.findByRole('row', { name: 'Grant Premier League' }));
    const dialog = await screen.findByRole('dialog', { name: 'Grant Premier League' });
    fireEvent.change(await within(dialog).findByLabelText('How many'), { target: { value: '2' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Grant to Northbank' }));
    await waitFor(() => expect(patches).toEqual(['players/1001 {"op":"grantItem","item":"manager_league:5002013","count":2,"untradeable":false}']));
  });

  it('grants one club customisation item and locks My Stadium items untradeable', async () => {
    renderPage(['customisation']);
    fireEvent.click(await screen.findByRole('row', { name: 'Grant Tifo 1041' }));
    const dialog = await screen.findByRole('dialog', { name: 'Grant Tifo 1041' });
    await within(dialog).findByLabelText('Club');
    expect(within(dialog).queryByLabelText('How many')).toBeNull();
    const untradeable = within(dialog).getByLabelText('Untradeable') as HTMLInputElement;
    expect(untradeable.checked && untradeable.disabled).toBe(true);
    expect(within(dialog).getByText('My Stadium items are always untradeable.')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Grant to Northbank' }));
    await waitFor(() => expect(patches).toEqual(['players/1001 {"op":"grantItem","item":"vanity:500:1041","count":1,"untradeable":true}']));
  });
});

describe('card editor', () => {
  it('reads an upload through a CSP-allowed data URL and prepares its pixels', async () => {
    let source = '';
    class Reader {
      result: string | null = null;
      onload: (() => void) | null = null;
      onerror: (() => void) | null = null;
      readAsDataURL() { this.result = 'data:image/jpeg;base64,AA=='; this.onload?.(); }
    }
    class Picture {
      naturalWidth = 360;
      naturalHeight = 180;
      onload: (() => void) | null = null;
      onerror: (() => void) | null = null;
      set src(value: string) { source = value; this.onload?.(); }
    }
    vi.stubGlobal('FileReader', Reader);
    vi.stubGlobal('Image', Picture);
    const draw = vi.fn();
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage: draw, getImageData: () => ({ data: new Uint8ClampedArray(180 * 180 * 4) }) } as unknown as CanvasRenderingContext2D);
    vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockReturnValue('data:image/png;base64,AAAA');
    const result = await fileAsHead(new File(['image'], 'head.jpg', { type: 'image/jpeg' }));
    expect(source).toBe('data:image/jpeg;base64,AA==');
    expect(result.png).toBe('AAAA');
    expect(draw).toHaveBeenCalledWith(expect.any(Picture), 0, 0, 360, 180);
    expect(draw).toHaveBeenCalledWith(expect.any(HTMLCanvasElement), 0, 0, 360, 180, 9, 84, 162, 81);
    expect(atob(result.rgba)).toHaveLength(180 * 180 * 4);
    vi.restoreAllMocks();
  });

  it('keeps untouched null attributes and refuses an invalid edited number', () => {
    expect(cardForStage(editableCard, { ...editableCard, rating: '94' })).toMatchObject({ rating: 94, gk_diving: null });
    expect(cardForStage(editableCard, { ...editableCard, rating: '100' })).toMatch(/rating must be a whole number/);
  });

  it('stages an edited card with the complete value and never publishes it', async () => {
    const writes: { path: string; body: Record<string, unknown> }[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (path === 'cards/editor/158023') return json({ resourceId: 158023, assetId: 158023, name: 'Messi', cardTypeId: 1, card: editableCard,
        options: { positions: ['RW', 'ST'], feet: ['Left', 'Right'], workRates: ['High', 'Low'], cardTypes: [{ id: 1, name: 'Gold', rareflag: 1 }], traits: [{ id: 1, name: 'Finesse shot' }] } });
      if (path === 'cards/images/158023') return json({ assetId: 158023, cards: [], uploads: [] });
      if (path === 'content/changeset' && init?.method === 'GET') return json({ open: false, pending: 0, changeset: null });
      if (path === 'content/changeset/changes' && init?.method === 'PUT') {
        writes.push({ path, body: JSON.parse(String(init.body)) as Record<string, unknown> });
        return json({ open: true, pending: 1 });
      }
      if (path === 'assets') return json({ available: false });
      return new Response('{}', { status: 404 });
    }));
    renderPage(['158023:93:1', 'edit']);
    fireEvent.change(await screen.findByLabelText('Overall'), { target: { value: '94' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({ area: 'player_card', key: { resourceId: 158023 }, operation: 'update', after: { card: { rating: 94, gk_diving: null, traits: [1] } } });
    expect(await screen.findByText('Card saved')).toBeTruthy();
    expect(writes.every((write) => write.path !== 'content/changeset/publish')).toBe(true);
  });

  it('requests a free revision id before staging a new card', async () => {
    const writes: { path: string; body: Record<string, unknown> }[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
      const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (path === 'cards/editor/158023') return json({ resourceId: 158023, assetId: 158023, name: 'Messi', cardTypeId: 1, card: editableCard,
        options: { positions: ['RW'], feet: ['Left'], workRates: ['High', 'Low'], cardTypes: [{ id: 1, name: 'Gold', rareflag: 1 }, { id: 5, name: 'Team of the Year', rareflag: 5 }], traits: [] } });
      if (path === 'cards/images/158023') return json({ assetId: 158023, cards: [], uploads: [] });
      if (path === 'content/changeset' && init?.method === 'GET') return json({ open: false, pending: 0, changeset: null });
      if (path === 'cards/new' && init?.method === 'POST') return json({ resourceId: 16935239 });
      if (path === 'content/changeset/changes' && init?.method === 'PUT') { writes.push({ path, body: JSON.parse(String(init.body)) as Record<string, unknown> }); return json({ open: true, pending: 1 }); }
      if (path === 'assets') return json({ available: false });
      return new Response('{}', { status: 404 });
    }));
    renderPage(['158023:93:1', 'new']);
    fireEvent.click(await screen.findByRole('button', { name: 'Choose card type' }));
    fireEvent.click(screen.getByRole('radio', { name: 'Team of the Year' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({ area: 'player_card', key: { resourceId: 16935239 }, operation: 'insert', after: { base: 158023, cardTypeId: 5 } });
  });
});
