import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { ToastProvider } from '../../ui/Toast';
import { CompletionReward } from './CompletionReward';

let shown = { coins: 2500, packId: 75 as number | null, untradeable: true };
let written: unknown;
beforeEach(() => {
  shown = { coins: 2500, packId: 75, untradeable: true };
  written = undefined;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
    let body: unknown = {};
    if (path.startsWith('content/rules/seasons?path=')) body = { definition: { value: shown } };
    if (path === 'content/changeset') body = { open: false, pending: 0, changeset: null };
    if (path === 'packs/catalog') body = { packs: [
      { id: 74, description: 'Gold Pack', config: {} }, { id: 75, description: 'Premium Gold Pack', config: {} },
      { id: 550, description: 'Player Pick', config: { player_pick_count: 1 } },
    ] };
    if (init?.method === 'PUT') written = JSON.parse(String(init.body));
    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
  }));
});
afterEach(() => vi.unstubAllGlobals());
const mount = () => render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><ToastProvider><CompletionReward mode="rivals" /></ToastProvider></QueryClientProvider>);

it('stages coins, an optional pack and tradeability together for the selected mode', async () => {
  mount();
  fireEvent.change(await screen.findByLabelText('Coins per completed match'), { target: { value: '3200' } });
  fireEvent.change(screen.getByLabelText('Pack per completed match'), { target: { value: '74' } });
  fireEvent.change(screen.getByLabelText('Pack tradeability'), { target: { value: 'tradeable' } });
  expect(screen.queryByText('Player Pick')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Save completion reward' }));
  await waitFor(() => expect(written).toEqual({ area: 'rules', key: { file: 'seasons', path: ['matchCompletionRewards', 'rivals'] },
    operation: 'update', after: { value: { coins: 3200, fifaPoints: 0, xp: 0, packId: 74, untradeable: false } } }));
});

it('stages Aurora Tokens per completed match, 0 for a server rule without them', async () => {
  mount();
  expect((await screen.findByLabelText('Aurora Tokens per completed match') as HTMLInputElement).value).toBe('0');
  fireEvent.change(screen.getByLabelText('Aurora Tokens per completed match'), { target: { value: '-5' } });
  expect((screen.getByRole('button', { name: 'Save completion reward' }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('Aurora Tokens per completed match'), { target: { value: '25' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save completion reward' }));
  await waitFor(() => expect(written).toMatchObject({ after: { value: { coins: 2500, fifaPoints: 25, packId: 75, untradeable: true } } }));
});

it('stages Season XP per completed match', async () => {
  mount();
  fireEvent.change(await screen.findByLabelText('Season XP per completed match'), { target: { value: '400' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save completion reward' }));
  await waitFor(() => expect(written).toMatchObject({ after: { value: { coins: 2500, xp: 400 } } }));
});

it('supports coins only and refuses negative amounts', async () => {
  mount();
  fireEvent.change(await screen.findByLabelText('Pack per completed match'), { target: { value: '' } });
  expect((screen.getByLabelText('Pack tradeability') as HTMLSelectElement).disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('Coins per completed match'), { target: { value: '-1' } });
  expect((screen.getByRole('button', { name: 'Save completion reward' }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('Coins per completed match'), { target: { value: '0' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save completion reward' }));
  await waitFor(() => expect(written).toMatchObject({ after: { value: { coins: 0, packId: null, untradeable: true } } }));
});

it('refuses to overwrite a reward changed since the editor loaded', async () => {
  mount();
  fireEvent.change(await screen.findByLabelText('Coins per completed match'), { target: { value: '3200' } });
  shown = { coins: 4000, packId: 75, untradeable: true };
  fireEvent.click(screen.getByRole('button', { name: 'Save completion reward' }));
  await screen.findByText('This setting changed while the page was open. Reload it before saving.');
  expect(written).toBeUndefined();
});
