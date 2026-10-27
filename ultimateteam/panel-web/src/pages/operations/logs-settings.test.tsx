import { afterEach, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '../../ui/Toast';
import { LogsPage } from './LogsPage';

const reply = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const show = (page: React.ReactElement) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><ToastProvider>{page}</ToastProvider></QueryClientProvider>);
  return client;
};
afterEach(() => vi.unstubAllGlobals());

it('keeps loaded log lines after a refresh fails and can pause follow', async () => {
  let fail = false;
  vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/logs') ? reply({ files: [{ name: 'server.log' }] }) : fail ? reply({ error: 'Offline' }, 500) : reply({ lines: ['Synthetic safe log'], truncated: true })));
  const client = show(<LogsPage file="server.log" />);
  await screen.findByText('Synthetic safe log');
  fireEvent.click(screen.getByRole('button', { name: 'Pause follow' }));
  expect(screen.getByRole('button', { name: 'Resume follow' })).toBeTruthy();
  fail = true;
  await act(async () => { await client.invalidateQueries({ queryKey: ['log-tail'] }); });
  await screen.findByText('Refresh failed');
  expect(screen.getByText('Synthetic safe log')).toBeTruthy();
  expect(screen.getByText(/last loaded lines/)).toBeTruthy();
  expect(screen.getByText(/last 4 MB/)).toBeTruthy();
});

it('does not request a tail for an absent file or spin forever on an empty log list', async () => {
  const fetch = vi.fn(async () => reply({ files: [] }));
  vi.stubGlobal('fetch', fetch);
  show(<LogsPage />);
  await screen.findByText('No log file selected');
  expect(screen.queryByRole('status', { name: 'Loading' })).toBeNull();
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));
});
