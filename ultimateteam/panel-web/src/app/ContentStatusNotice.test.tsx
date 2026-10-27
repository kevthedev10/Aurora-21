import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '../ui/Toast';
import { ContentStatusNotice } from './ContentStatusNotice';

const pending = { open: true, pending: 1, changeset: { id: 3, changes: [{ id: 9 }] } };
const current = { revision: 4, loadedDatabaseRevision: 12, externalChanges: false, reloadRequired: false, tables: [], message: 'Current' };
const external = { ...current, externalChanges: true, reloadRequired: true, tables: [{ table: 'promos', revision: 13, changedAt: '2026-09-30T12:00:00Z' }] };
let status = external;
let statusCode = 200;
let reloadCode = 200;
let writes: string[] = [];

beforeEach(() => {
  status = external; statusCode = 200; reloadCode = 200; writes = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '');
    const method = init?.method ?? 'GET';
    const json = (code: number, body: unknown) => new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } });
    if (method !== 'GET') writes.push(`${method} ${path}`);
    if (path === 'content/status') return json(statusCode, statusCode === 200 ? status : { error: 'Status unavailable' });
    if (path === 'content/changeset') return json(200, pending);
    if (path === 'content/reload') {
      if (reloadCode !== 200) return json(reloadCode, { error: 'Invalid database content; previous revision stays current' });
      status = { ...current, revision: 5 };
      return json(200, { revision: 5, warnings: ['Review this warning'], notes: ['Pending edits remain staged'] });
    }
    return json(404, { error: 'Not found' });
  }));
});
afterEach(() => vi.unstubAllGlobals());

function renderNotice() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><ToastProvider><ContentStatusNotice /></ToastProvider></QueryClientProvider>);
  return client;
}

describe('External content changes', () => {
  it('only warns until reload is confirmed, retains pending edits and refreshes cached content', async () => {
    const client = renderNotice();
    client.setQueryData(['card', '158023'], { rating: 93 });
    await screen.findByRole('region', { name: 'External content changes' });
    expect(screen.getByText('promos')).toBeTruthy();
    expect(writes).toEqual([]);
    fireEvent.click(screen.getByRole('button', { name: 'Reload content' }));
    const dialog = await screen.findByRole('dialog', { name: 'Reload database content?' });
    expect(within(dialog).getByText(/1 pending edit\(s\) stay staged/)).toBeTruthy();
    expect(writes).toEqual([]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reload content' }));
    await screen.findByRole('region', { name: 'Content reload result' });
    expect(screen.getByText('Review this warning')).toBeTruthy();
    await waitFor(() => expect(screen.queryByRole('region', { name: 'External content changes' })).toBeNull());
    expect(writes).toEqual(['POST content/reload']);
    expect(client.getQueryData(['changeset'])).toEqual(pending);
    expect(client.getQueryState(['card', '158023'])?.isInvalidated).toBe(true);
  });

  it('keeps the warning and confirmation on a refused reload', async () => {
    reloadCode = 422;
    const client = renderNotice();
    fireEvent.click(await screen.findByRole('button', { name: 'Reload content' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Reload content' }));
    await within(dialog).findByText(/previous revision stays current/);
    expect(client.getQueryData(['content-status'])).toEqual(external);
    expect(screen.getByRole('region', { name: 'External content changes', hidden: true })).toBeTruthy();
  });

  it('reports monitoring failure with retry, rather than presenting stale content as current', async () => {
    statusCode = 500;
    renderNotice();
    await screen.findByText('Content status could not be checked.');
    statusCode = 200;
    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
    await screen.findByRole('region', { name: 'External content changes' });
    expect(writes).toEqual([]);
  });

  it('supports older servers without a status endpoint', async () => {
    statusCode = 404;
    const client = renderNotice();
    await waitFor(() => expect(client.getQueryState(['content-status'])?.status).toBe('error'));
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Reload content' })).toBeNull();
    expect(writes).toEqual([]);
  });
});
