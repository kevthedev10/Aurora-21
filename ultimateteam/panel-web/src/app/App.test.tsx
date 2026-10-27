import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App } from './App';
import { PageErrorBoundary } from './ErrorBoundary';

// Replies shaped like the real panel API (OperatorPanel*.cs); anything else answers 404.
const REPLIES: Record<string, unknown> = {
  session: { ok: true, mode: 'loopback' },
  dashboard: { services: { blaze: true, fut: true }, online: { count: 2 } },
  'content/changeset': {
    open: true, pending: 1,
    changeset: { id: 3, changes: [{ id: 9, area: 'store_pack', key: { packId: 7 }, operation: 'update', before: { price: 7500 }, after: { price: 9000 } }] },
  },
  players: { players: [{ id: 1001, name: 'Northbank', online: true }, { id: 1002, name: 'Kopite' }] },
  'packs/catalog': { packs: [{ id: 7, description: 'Gold Pack' }] },
  assets: { available: false },
};

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const path = url.replace('/panel/api/', '').split('?')[0] ?? '';
    const body = REPLIES[path];
    return new Response(JSON.stringify(body ?? { error: 'not found' }), { status: body ? 200 : 404, headers: { 'Content-Type': 'application/json' } });
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderApp = (hash: string) => {
  window.location.hash = hash;
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><App /></QueryClientProvider>);
};

describe('shell', () => {
  it('keeps global search in the brand row and server status in the footer', async () => {
    const page = renderApp('#/players');
    const search = await screen.findByRole('button', { name: 'Search players, cards, packs' });
    expect(search.closest('.rail-head')).toBeTruthy();
    expect(search.closest('.header-controls')?.contains(screen.getByRole('navigation', { name: 'Panel' }))).toBe(true);
    const status = page.container.querySelector('footer[aria-label="Server status"]')!;
    expect(within(status as HTMLElement).getByText('Blaze')).toBeTruthy();
    expect(within(status as HTMLElement).getByText('FUT')).toBeTruthy();
    expect(await within(status as HTMLElement).findByText('2 online')).toBeTruthy();
    expect(status.contains(search)).toBe(false);
  });

  it('keeps card search focus while updating the address and follows an externally changed search', async () => {
    const original = fetch;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => url.startsWith('/panel/api/cards?')
      ? new Response(JSON.stringify({ total: 0, cards: [] }), { status: 200 }) : original(url, init)));
    renderApp('#/cards');
    const input = await screen.findByLabelText('Search');
    input.focus();
    fireEvent.change(input, { target: { value: 'Messi' } });
    await waitFor(() => expect(window.location.hash).toBe('#/cards?q=Messi'));
    expect(document.activeElement).toBe(input);
    expect(screen.getByLabelText('Search')).toBe(input);
    act(() => { window.location.hash = '#/cards?q=Ronaldo'; });
    await waitFor(() => expect((input as HTMLInputElement).value).toBe('Ronaldo'));
    await new Promise((resolve) => setTimeout(resolve, 350));
    expect(window.location.hash).toBe('#/cards?q=Ronaldo');
  });

  it('labels retained card results with their original page while the next page is loading', async () => {
    const original = fetch;
    let finish: ((reply: Response) => void) | undefined;
    const card = { cardKey: '158023:93:1', resourceId: 158023, name: 'Messi', rating: 93 };
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.startsWith('/panel/api/cards?')) {
        const offset = new URLSearchParams(url.split('?')[1]).get('offset');
        if (offset === '64') return new Promise<Response>((resolve) => { finish = resolve; });
        return new Response(JSON.stringify({ total: 120, offset: 0, cards: [card] }), { status: 200 });
      }
      return original(url, init);
    }));
    renderApp('#/cards');
    const next = await screen.findByRole('button', { name: 'Next' });
    fireEvent.click(next);
    await screen.findByText('Updating results…');
    expect(screen.getByText('1–1 of 120')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Next' }) as HTMLButtonElement).disabled).toBe(true);
    await act(async () => { finish?.(new Response(JSON.stringify({ total: 120, offset: 64, cards: [card] }), { status: 200 })); });
    await screen.findByText('65–65 of 120');
  });

  it('moves a classic address to its new page', async () => {
    renderApp('#/packcatalog?edit=7');
    await waitFor(() => expect(window.location.hash).toBe('#/packs/catalogue/7'));
  });

  it('moves the old pick, store and reward tabs to the one pack list, and the old store address to the lineup', async () => {
    renderApp('#/packs/picks/new');
    await waitFor(() => expect(window.location.hash).toBe('#/packs/catalogue/new?type=picks'));
    window.location.hash = '#/packs/reward';
    await waitFor(() => expect(window.location.hash).toBe('#/packs/catalogue'));
    window.location.hash = '#/store';
    await waitFor(() => expect(window.location.hash).toBe('#/packs/lineup'));
  });

  it('groups desktop links into compact navigation and marks the current page', async () => {
    renderApp('#/players/1001/inventory');
    const nav = await screen.findByRole('navigation', { name: 'Panel' });
    expect(within(nav).getAllByRole('link').map((link) => link.textContent)).toEqual(['Dashboard', 'Players / Clubs']);
    expect(within(nav).getByRole('link', { name: 'Players / Clubs' }).getAttribute('aria-current')).toBe('page');
    fireEvent.click(within(nav).getByRole('button', { name: 'Content' }));
    const content = within(nav).getByRole('group', { name: 'Content' });
    expect(within(content).getAllByRole('link').map((link) => link.querySelector('strong')?.textContent)).toEqual([
      'Schedule', 'Cards', 'Packs / Picks', 'Promos', 'Modes', 'SBCs', 'Objectives', 'Rules', 'Changes',
    ]);
    fireEvent.click(within(nav).getByRole('button', { name: 'Operations' }));
    expect(within(nav).queryByRole('group', { name: 'Content' })).toBeNull();
    expect(within(within(nav).getByRole('group', { name: 'Operations' })).getAllByRole('link')
      .map((link) => link.querySelector('strong')?.textContent)).toEqual(['Access & maintenance', 'Matchmaking', 'Leavers', 'Market log', 'Logs']);
    expect(screen.queryByRole('link', { name: /classic panel/i })).toBeNull();
    await waitFor(() => expect(document.title).toBe('Players / Clubs · Aurora 21'));
  });

  it('opens dropdowns by keyboard, returns focus on Escape and dismisses outside', async () => {
    renderApp('#/objectives');
    const content = await screen.findByRole('button', { name: 'Content' });
    expect(content.getAttribute('data-active')).toBe('true');
    content.focus();
    fireEvent.keyDown(content, { key: 'ArrowDown' });
    const group = await screen.findByRole('group', { name: 'Content' });
    await waitFor(() => expect(document.activeElement).toBe(within(group).getAllByRole('link')[0]));
    expect(within(group).getByRole('link', { name: /Objectives/ }).getAttribute('aria-current')).toBe('page');
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    expect(screen.queryByRole('group', { name: 'Content' })).toBeNull();
    expect(document.activeElement).toBe(content);
    expect(content.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(content);
    fireEvent.pointerDown(document.body);
    expect(screen.queryByRole('group', { name: 'Content' })).toBeNull();
    fireEvent.click(content);
    fireEvent.blur(content, { relatedTarget: document.getElementById('panel-main') });
    expect(screen.queryByRole('group', { name: 'Content' })).toBeNull();
  });

  it('opens Objectives from Content and closes the dropdown', async () => {
    renderApp('#/dashboard');
    fireEvent.click(await screen.findByRole('button', { name: 'Content' }));
    const objective = screen.getByRole('link', { name: /Objectives/ });
    expect(objective.getAttribute('href')).toBe('#/objectives');
    fireEvent.click(objective);
    // jsdom does not follow hash links reliably; emulate browser navigation.
    act(() => { window.location.hash = objective.getAttribute('href')!; });
    await screen.findByRole('heading', { name: 'Objectives' });
    expect(screen.queryByText('Coming soon')).toBeNull();
    expect(screen.queryByRole('group', { name: 'Content' })).toBeNull();
    await waitFor(() => expect(document.title).toBe('Objectives · Aurora 21'));
  });

  it('lists pending changes in the drawer, read-only', async () => {
    renderApp('#/dashboard');
    fireEvent.click(await screen.findByRole('button', { name: /1 pending/ }));
    const drawer = await screen.findByRole('dialog', { name: 'Pending changes' });
    expect(within(drawer).getByText('pack 7')).toBeTruthy();
    expect(within(drawer).getByText('price: 7500 → 9000')).toBeTruthy();
    expect(within(drawer).getByRole('link', { name: 'Review and publish' }).getAttribute('href')).toBe('#/changes');
  });

  it('finds a player with Ctrl K and opens their page', async () => {
    renderApp('#/dashboard');
    await screen.findByRole('navigation', { name: 'Panel' });
    fireEvent.keyDown(window, { key: 'k', ctrlKey: true });
    const input = await screen.findByRole('combobox');
    fireEvent.change(input, { target: { value: 'north' } });
    await screen.findByRole('option', { name: /Northbank/ });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(window.location.hash).toBe('#/players/1001'));
  });

  it('opens mobile navigation as a dialog and closes it on a link to the current page', async () => {
    vi.stubGlobal('matchMedia', vi.fn((query: string) => ({
      matches: query === '(max-width: 760px)', media: query,
      addEventListener: vi.fn(), removeEventListener: vi.fn(), addListener: vi.fn(), removeListener: vi.fn(),
    })));
    renderApp('#/dashboard');
    const menu = await screen.findByRole('button', { name: 'Menu' });
    expect(screen.queryByRole('navigation', { name: 'Panel' })).toBeNull();
    menu.focus();
    fireEvent.click(menu);
    const drawer = await screen.findByRole('dialog', { name: 'Navigation' });
    expect(within(drawer).getByRole('link', { name: 'Objectives' }).getAttribute('href')).toBe('#/objectives');
    expect(menu.getAttribute('aria-expanded')).toBe('true');
    fireEvent.click(within(drawer).getByRole('link', { name: 'Dashboard' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Navigation' })).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(menu));
    expect(menu.getAttribute('aria-expanded')).toBe('false');
  });

  it('skips navigation without changing the route', async () => {
    renderApp('#/dashboard');
    await screen.findByRole('navigation', { name: 'Panel' });
    fireEvent.click(screen.getByRole('link', { name: 'Skip to content' }));
    expect(document.activeElement?.id).toBe('panel-main');
    expect(window.location.hash).toBe('#/dashboard');
  });

  it('keeps keyboard search usable after ArrowDown on an empty result list', async () => {
    renderApp('#/dashboard');
    fireEvent.click(await screen.findByRole('button', { name: /Search players/ }));
    const input = await screen.findByRole('combobox');
    fireEvent.change(input, { target: { value: 'zzzzzz' } });
    await waitFor(() => expect(screen.queryAllByRole('option')).toHaveLength(0));
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.change(input, { target: { value: 'north' } });
    await screen.findByRole('option', { name: /Northbank/ });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(window.location.hash).toBe('#/players/1001'));
  });
});

describe('PageErrorBoundary', () => {
  it('keeps a crashing page inside its own error panel', async () => {
    let crash = true;
    const Boom = () => { if (crash) throw new Error('kaboom'); return <p>recovered</p>; };
    const spy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    render(<PageErrorBoundary><Boom /></PageErrorBoundary>);
    expect(screen.getByRole('alert').textContent).toContain('kaboom');
    crash = false;
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Try again' })); });
    expect(screen.getByText('recovered')).toBeTruthy();
    spy.mockRestore();
  });
});
