import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '../../ui/Toast';
import { AccessPage } from './AccessPage';
import roles from './accessRoles.json';

const reply = (body: unknown) => new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });
const base = () => ({ maintenance: true, maintenanceMessage: 'Testing', maxPlayers: 0, guildId: '123456789012345678', playRoles: roles.slice(0,2).flatMap(role => role.ids).join(', '), bypassRoles: roles.slice(0,2).flatMap(role => role.ids).join(', '), bypassNames: '', blockedIps: '', connected: 1, discord: { configured: true, tokenSet: true } });
let access = base();
let posts: { path: string; body: Record<string, unknown> }[];
let playerReply: (() => Promise<Response>) | undefined;
let gameSpeed: { value: number } | null = null;
beforeEach(() => {
  access = base(); posts = []; playerReply = undefined; gameSpeed = null;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '');
    if (init?.method === 'POST') {
      const body = JSON.parse(String(init.body ?? '{}')) as Record<string, unknown>;
      posts.push({ path, body });
      if (path === 'maintenance/blaze-access') { access = { ...access, ...body } as typeof access; return reply({ changed: ['roles'] }); }
      if (path === 'maintenance/game-speed') { gameSpeed = { value: Number(body.value) }; return reply(gameSpeed); }
      if (path === 'maintenance/discord/check' && playerReply) return playerReply();
    }
    if (path === 'maintenance') return reply({ blazeAccess: access, onlineGameSpeed: gameSpeed, backups: { error: 'No backup worker' }, restart: { available: false } });
    if (path === 'content') return reply({ revision: 1 });
    if (path === 'maintenance/discord/roles') return reply({ roles: [...roles.flatMap(role => role.ids.map(id => ({ id, name: role.name }))), { id: '999999999999999999', name: 'Extra testers' }] });
    return reply({ status: 'ok', username: 'Synthetic user', canPlay: true });
  }));
});
afterEach(() => vi.unstubAllGlobals());
const show = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><ToastProvider><AccessPage /></ToastProvider></QueryClientProvider>);
  return client;
};
const editMessage = async (message: string) => fireEvent.change(await screen.findByLabelText('Maintenance message'), { target: { value: message } });
const save = async () => { fireEvent.click(screen.getByRole('button', { name: 'Save access' })); fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save access' })); };

it('changes online game speed through maintenance while keeping an unsaved access draft', async () => {
  gameSpeed = { value: 1 };
  show();
  await editMessage('Keep this draft');
  fireEvent.change(await screen.findByLabelText('Game speed'), { target: { value: '2' } });
  await waitFor(() => expect(posts).toEqual([{ path: 'maintenance/game-speed', body: { value: 2 } }]));
  await waitFor(() => expect((screen.getByLabelText('Game speed') as HTMLSelectElement).value).toBe('2'));
  expect((screen.getByLabelText('Maintenance message') as HTMLInputElement).value).toBe('Keep this draft');
});

it('hides game speed when the server has no online matchmaking control', async () => {
  show();
  await screen.findByLabelText('Maintenance message');
  expect(screen.queryByLabelText('Game speed')).toBeNull();
});

it('preserves unsaved edits when connection counts refresh', async () => {
  const client = show();
  await editMessage('Unsaved maintenance message');
  access = { ...access, connected: 2 };
  await act(async () => { await client.invalidateQueries({ queryKey: ['maintenance'] }); });
  expect((screen.getByLabelText('Maintenance message') as HTMLInputElement).value).toBe('Unsaved maintenance message');
  expect((screen.getByRole('button', { name: 'Save access' }) as HTMLButtonElement).disabled).toBe(false);
});

it('keeps a draft on failed refresh and refuses saving until the status recovers', async () => {
  const client = show();
  await editMessage('Keep this draft');
  vi.stubGlobal('fetch', vi.fn(async () => new Response('{"error":"Offline"}', { status: 500 })));
  await act(async () => { await client.invalidateQueries({ queryKey: ['maintenance'] }); });
  await screen.findByText('Maintenance could not be loaded');
  expect((screen.getByLabelText('Maintenance message') as HTMLInputElement).value).toBe('Keep this draft');
  expect((screen.getByRole('button', { name: 'Save access' }) as HTMLButtonElement).disabled).toBe(true);
});

it('refuses to replace settings changed after opening the form', async () => {
  show();
  await editMessage('My draft');
  access = { ...access, maxPlayers: 12 };
  await save();
  await screen.findByText(/Access settings changed while you were editing/);
  expect(posts).toEqual([]);
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' }));
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  fireEvent.click(screen.getByRole('button', { name: 'Reset form' }));
  expect((screen.getByLabelText('Player cap') as HTMLInputElement).value).toBe('12');
});

it('groups both Founder IDs and allows extra roles without granting Booster or Whitelister access', async () => {
  show();
  const group = await screen.findByRole('group', { name: 'Play roles' });
  const founder = within(group).getByRole('checkbox', { name: /Founders/ }) as HTMLInputElement;
  expect(founder.checked).toBe(true);
  expect((within(group).getByRole('checkbox', { name: /Boosters/ }) as HTMLInputElement).checked).toBe(false);
  expect((within(group).getByRole('checkbox', { name: /Whitelisters/ }) as HTMLInputElement).checked).toBe(false);
  fireEvent.change(screen.getByLabelText('Extra role ID'), { target: { value: '888888888888888888' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add role' }));
  await save();
  await waitFor(() => expect(posts).toHaveLength(1));
  const selected = String(posts[0]!.body.playRoles).split(', ');
  expect(selected).toEqual([...roles.slice(0,2).flatMap(role => role.ids), '888888888888888888'].sort());
  expect(posts[0]!.body.maintenance).toBe(true);
});

it('does not show a late player-check response for a different user ID', async () => {
  let finish: (response: Response) => void = () => {};
  playerReply = () => new Promise(resolve => { finish = resolve; });
  show();
  fireEvent.change(await screen.findByLabelText('Discord user ID'), { target: { value: '111111111111111111' } });
  fireEvent.click(screen.getByRole('button', { name: 'Test player' }));
  await waitFor(() => expect(posts).toHaveLength(1));
  fireEvent.change(screen.getByLabelText('Discord user ID'), { target: { value: '222222222222222222' } });
  await act(async () => { finish(reply({ status: 'ok', username: 'Old account', canPlay: true })); });
  expect(screen.queryByText('Old account')).toBeNull();
});

it('pauses all new matches only after confirming, keeps an access draft, and resumes without a dialog', async () => {
  access = { ...access, newMatches: true } as typeof access;
  show();
  await editMessage('Draft kept while pausing');
  const control = (await screen.findByText('All matches')).closest('.match-switch') as HTMLElement;
  fireEvent.click(within(control).getByRole('button', { name: 'Pause all matches' }));
  const dialog = await screen.findByRole('dialog');
  expect(posts).toEqual([]);
  fireEvent.click(within(dialog).getByRole('button', { name: 'Pause all matches' }));
  await waitFor(() => expect(posts).toEqual([{ path: 'maintenance/blaze-access', body: { newMatches: false } }]));
  await waitFor(() => expect(within(control).getByText('Paused')).toBeTruthy());
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  expect((screen.getByLabelText('Maintenance message') as HTMLInputElement).value).toBe('Draft kept while pausing');
  fireEvent.click(within(control).getByRole('button', { name: 'Resume matches' }));
  await waitFor(() => expect(posts.at(-1)).toEqual({ path: 'maintenance/blaze-access', body: { newMatches: true } }));
  expect(screen.queryByRole('dialog')).toBeNull();
});
