import { afterEach, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '../../ui/Toast';
import { AccessPage } from './AccessPage';
import { LogsPage } from './LogsPage';
import { DashboardPage } from './DashboardPage';

const show = (page: React.ReactElement) => render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><ToastProvider>{page}</ToastProvider></QueryClientProvider>);
const reply = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
afterEach(() => vi.unstubAllGlobals());

it('keeps dashboard lists bounded and pages all clubs without an empty supervisor panel', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => reply({
    services: { supervised: false, list: [{ key: 'fut', label: 'FUT', up: true }] },
    online: { count: 23, players: Array.from({ length: 23 }, (_, index) => ({ id: index + 1, name: `Online ${index + 1}` })) },
    recent: Array.from({ length: 1000 }, (_, index) => ({ id: index + 1, name: `Login ${index + 1}`, at: String(index) })),
    clubs: Array.from({ length: 23 }, (_, index) => ({ persona_id: index + 1, club_name: `Club ${index + 1}` })),
    days: [], supervisor: [],
  })));
  show(<DashboardPage />);
  await screen.findByRole('heading', { name: 'Services' });
  expect(screen.queryByRole('heading', { name: 'Supervisor' })).toBeNull();
  expect(screen.queryByText('Blaze heartbeat')).toBeNull();
  const section = (title: string) => within(screen.getByRole('heading', { name: title }).closest('section')!);
  for (const [title, prefix, count] of [['Connected now', 'Online', 23], ['Recent logins', 'Login', 20], ['Clubs', 'Club', 23]] as const) {
    const panel = section(title);
    expect(panel.getByText(`1–10 of ${count}`)).toBeTruthy();
    expect(panel.queryByText(`${prefix} 11`)).toBeNull();
    fireEvent.click(panel.getByRole('button', { name: 'Next' }));
    expect(panel.getByText(`${prefix} 11`)).toBeTruthy();
    if (title === 'Recent logins') expect(panel.getByRole('button', { name: 'Next' }).hasAttribute('disabled')).toBe(true);
  }
  expect(screen.queryByText('Login 21')).toBeNull();
});

it('confirms a server-only restart when the worker is active', async () => {
  const calls: { url: string; body?: unknown }[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    if (url.endsWith('/maintenance')) return reply({ blazeAccess: {}, services: [{ key: 'server', label: 'Game server' }], restart: { available: true, pending: false } });
    if (url.endsWith('/content')) return reply({ revision: 2 });
    if (url.endsWith('/maintenance/restart')) return reply({ requested: true }, 202);
    return reply({ error: 'missing' }, 404);
  }));
  show(<AccessPage />);
  fireEvent.click(await screen.findByRole('button', { name: 'Restart server' }));
  const dialog = await screen.findByRole('dialog', { name: 'Restart the server container?' });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Restart server' }));
  await waitFor(() => expect(calls.some((call) => call.url.endsWith('/maintenance/restart') && JSON.stringify(call.body) === '{"service":"server","confirm":true}')).toBe(true));
});

it('filters a selected log file after the search debounce', async () => {
  const calls: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    calls.push(url);
    if (url.endsWith('/logs')) return reply({ pinned: ['ultimateteam.log'], files: [{ name: 'ultimateteam.log' }] });
    if (url.includes('/logs/ultimateteam.log')) return reply({ name: 'ultimateteam.log', lines: ['match found'] });
    return reply({ error: 'missing' }, 404);
  }));
  show(<LogsPage file="ultimateteam.log" />);
  fireEvent.change(await screen.findByRole('searchbox', { name: 'Filter lines' }), { target: { value: 'match' } });
  await waitFor(() => expect(calls.some((url) => url.includes('q=match'))).toBe(true));
});
