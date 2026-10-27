import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '../../ui/Toast';
import { ChangesPage } from './ChangesPage';
import { setContentDestination } from '../../api/client';

// Shaped like OperatorPanel.Content.cs replies.
const change = { id: 9, area: 'store_pack', key: { packId: 7 }, operation: 'update', before: { price: 7500, size: 12 }, after: { price: 9000, size: 12 } };
const open = { areas: ['rules', 'store_pack'], open: true, pending: 1, changeset: { id: 3, createdAt: new Date().toISOString(), changes: [change] } };
const history = { changesets: [{ id: 2, note: 'Cheaper gold', publishedAt: new Date().toISOString(), createdBy: 'op', changes: [change] }] };

let calls: string[] = [];
let replies: Record<string, { status: number; body: unknown }> = {};

beforeEach(() => {
  calls = [];
  replies = {};
  localStorage.clear();
  sessionStorage.clear();
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') calls.push(`${method} ${path}${init?.body ? ` ${String(init.body)}` : ''}`);
    const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    const override = replies[`${method} ${path}`];
    if (override) return json(override.status, override.body);
    if (path === 'content/changeset') return json(200, open);
    if (path.startsWith('content/history?')) return json(200, history);
    if (path === 'content/changeset/validate') return json(200, { ok: true, changes: 1, conflicts: [], errors: [] });
    if (path === 'content/changeset/publish') return json(200, { published: { id: 3 }, content: { revision: 5, warnings: [], notes: [] } });
    return json(200, open);
  }));
});
afterEach(() => { vi.unstubAllGlobals(); setContentDestination(null); window.location.hash = ''; });

const renderPage = (tab?: string) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return { ...render(<QueryClientProvider client={client}><ToastProvider><ChangesPage tab={tab} /></ToastProvider></QueryClientProvider>), client };
};

describe('Changes: pending', () => {
  it('reviews selected drop content through Schedule instead of generic immediate publishing', async () => {
    setContentDestination(42);
    renderPage();
    const review = await screen.findByRole('link', { name: 'Review in Schedule' });
    expect(review.getAttribute('href')).toBe('#/schedule');
    expect(screen.queryByRole('button', { name: 'Validate & publish' })).toBeNull();
    expect(calls).toEqual([]);
  });
  it('blocks publishing after failed validation and lets the same button retry', async () => {
    replies['POST content/changeset/validate'] = { status: 200, body: { ok: false, errors: ['Invalid pack odds'] } };
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    await screen.findByText('Invalid pack odds');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(calls).toEqual(['POST content/changeset/validate {}']);
    delete replies['POST content/changeset/validate'];
    fireEvent.click(screen.getByRole('button', { name: 'Retry validation' }));
    await screen.findByRole('dialog');
    expect(calls).toEqual(['POST content/changeset/validate {}', 'POST content/changeset/validate {}']);
  });

  it('does not accept validation completed after pending edits change', async () => {
    const original = globalThis.fetch;
    let finish!: (reply: Response) => void;
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => url.endsWith('content/changeset/validate')
      ? new Promise<Response>((resolve) => { finish = resolve; }) : original(url, init)));
    const { client } = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    await screen.findByRole('button', { name: 'Validating…' });
    act(() => { client.setQueryData(['changeset'], { ...open, changeset: { ...open.changeset, changes: [{ ...change, after: { price: 10000 } }] } }); });
    await act(async () => { finish(new Response(JSON.stringify({ ok: true, changes: 1 }), { status: 200, headers: { 'Content-Type': 'application/json' } })); });
    await screen.findByRole('button', { name: 'Validate & publish' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(calls).toEqual([]);
  });

  it('invalidates an earlier validation when another edit arrives, preserving the publish note', async () => {
    const { client } = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    await screen.findByText(/Ready to publish/);
    fireEvent.change(screen.getByLabelText('Note'), { target: { value: 'Keep this note' } });
    act(() => { client.setQueryData(['changeset'], { ...open, changeset: { ...open.changeset, changes: [{ ...change, after: { price: 10000 } }] } }); });
    expect(await screen.findByText(/changed since the last check/)).toBeTruthy();
    await waitFor(() => expect(screen.queryByText(/Ready to publish/)).toBeNull());
    expect((screen.getByLabelText('Note') as HTMLInputElement).value).toBe('Keep this note');
  });

  it('closes a publish confirmation when the reviewed values change', async () => {
    const { client } = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    await screen.findByRole('dialog');
    act(() => { client.setQueryData(['changeset'], { ...open, changeset: { ...open.changeset, changes: [{ ...change, after: { price: 10000 } }] } }); });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(calls).toEqual(['POST content/changeset/validate {}']);
  });

  it('closes an open publish confirmation when content monitoring fails', async () => {
    const { client } = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    await screen.findByRole('dialog');
    replies['GET content/status'] = { status: 500, body: { error: 'Status unavailable' } };
    await act(async () => { await client.invalidateQueries({ queryKey: ['content-status'] }); });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect((screen.getByRole('button', { name: 'Validate & publish' }) as HTMLButtonElement).disabled).toBe(true);
    expect(calls).toEqual(['POST content/changeset/validate {}']);
  });

  it('publishes every pending edit even while the review list is filtered and refreshes cached content', async () => {
    const second = { ...change, id: 10, area: 'rules', key: { file: 'market', path: ['price'] }, after: { value: 123 } };
    replies['GET content/changeset'] = { status: 200, body: { ...open, pending: 2, changeset: { ...open.changeset, changes: [change, second] } } };
    const { client } = renderPage();
    client.setQueryData(['cards', ''], { cards: ['old'] });
    client.setQueryData(['packs-catalog'], { revision: 'old' });
    fireEvent.change(await screen.findByLabelText('Content area'), { target: { value: 'rules' } });
    expect(screen.queryByText('pack 7')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Validate & publish' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Publish' }));
    await screen.findByText('Published changeset #3');
    expect(client.getQueryState(['cards', ''])?.isInvalidated).toBe(true);
    expect(client.getQueryState(['packs-catalog'])?.isInvalidated).toBe(true);
  });

  it('retains a draft when the second restaging request fails and retries without another DELETE', async () => {
    replies['POST content/changeset/validate'] = { status: 200, body: { ok: false, conflicts: [{ changeId: 9, area: 'store_pack', key: change.key, expected: change.before, live: { price: 8000 } }], errors: [] } };
    replies['PUT content/changeset/changes'] = { status: 422, body: { error: 'Staging failed' } };
    const page = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    const keep = await screen.findByRole('button', { name: 'Keep my value' });
    replies['GET content/changeset'] = { status: 200, body: { open: false, pending: 0, changeset: null } };
    fireEvent.click(keep);
    expect(await screen.findByText('Your value is retained here')).toBeTruthy();
    expect(screen.getByText(/"price": 9000/)).toBeTruthy();
    await waitFor(() => expect(sessionStorage.getItem('aurora21.panel.changes.recovery')).toContain('9000'));
    page.unmount();
    renderPage();
    await screen.findByText('Your value is retained here');
    replies['PUT content/changeset/changes'] = { status: 200, body: open };
    replies['GET content/changeset'] = { status: 200, body: open };
    fireEvent.click(screen.getByRole('button', { name: 'Retry staging retained value' }));
    await waitFor(() => expect(screen.queryByText('Your value is retained here')).toBeNull());
    expect(calls.filter((call) => call.startsWith('DELETE'))).toHaveLength(1);
    expect(calls.filter((call) => call.startsWith('PUT'))).toHaveLength(2);
  });

  it('requires confirmation for discard and keeps pending edits visible on refusal', async () => {
    replies['POST content/changeset/discard'] = { status: 409, body: { error: 'Could not discard' } };
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Discard all' }));
    expect(calls).toEqual([]);
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard all' }));
    expect(await within(dialog).findByText('Could not discard')).toBeTruthy();
    expect(screen.getByText('pack 7')).toBeTruthy();
  });

  it('retains the draft on a failed DELETE without sending the replacement PUT', async () => {
    replies['POST content/changeset/validate'] = { status: 200, body: { ok: false, conflicts: [{ changeId: 9, area: 'store_pack', key: change.key, live: { price: 8000 } }], errors: [] } };
    replies['DELETE content/changeset/changes/9'] = { status: 500, body: { error: 'Connection outcome uncertain' } };
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Keep my value' }));
    await screen.findByText('Your value is retained here');
    expect(calls.filter((call) => call.startsWith('PUT'))).toHaveLength(0);
    expect(sessionStorage.getItem('aurora21.panel.changes.recovery')).toContain('9000');
  });

  it('reviews card edits one field at a time', async () => {
    replies['GET content/changeset'] = { status: 200, body: { ...open, changeset: { ...open.changeset, changes: [{
      id: 10, area: 'player_card', key: { resourceId: 158023 }, operation: 'update',
      before: { card: { rating: 93, position: 'RW', traits: [1] } },
      after: { card: { rating: 94, position: 'RW', traits: [1, 2] } },
    }] } } };
    renderPage();
    expect(await screen.findByText('card 158023')).toBeTruthy();
    expect(screen.getByText('card.rating')).toBeTruthy();
    expect(screen.getByText('card.traits')).toBeTruthy();
    expect(screen.queryByText('card.position')).toBeNull();
  });

  it('shows each change as a field diff and removes one', async () => {
    renderPage();
    expect(await screen.findByText('pack 7')).toBeTruthy();
    expect(screen.getByText('price')).toBeTruthy();
    expect(screen.queryByText('size')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(calls).toEqual(['DELETE content/changeset/changes/9']));
  });

  it('clears filters without changing the pending edits', async () => {
    renderPage();
    fireEvent.change(await screen.findByLabelText('Find pending changes'), { target: { value: 'no_such_item' } });
    expect(screen.getByText('No matching pending changes')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(screen.getByText('pack 7')).toBeTruthy();
    expect(calls).toEqual([]);
  });

  it('describes rule removal and retains the removed value for review', async () => {
    replies['GET content/changeset'] = { status: 200, body: { ...open, changeset: { ...open.changeset, changes: [{
      ...change, area: 'rules', key: { file: 'market', path: ['_comment'] }, operation: 'hide', before: { value: 'Original documentation' }, after: null,
    }] } } };
    renderPage();
    expect(await screen.findByText('This rule is removed when published.')).toBeTruthy();
    fireEvent.click(screen.getByText('View removed value'));
    expect(screen.getByText(/Original documentation/)).toBeTruthy();
    expect(screen.queryByText(/Existing club items are kept/)).toBeNull();
  });

  it('validates, and publishes with the note and name only after confirming', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    expect(await screen.findByText(/Ready to publish/)).toBeTruthy();
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    fireEvent.change(screen.getByLabelText('Note'), { target: { value: 'Pricier gold' } });
    fireEvent.change(screen.getByLabelText('Published by'), { target: { value: 'Hylke' } });
    fireEvent.click(screen.getByRole('button', { name: 'Publish 1 change' }));
    const dialog = await screen.findByRole('dialog', { name: 'Publish 1 change?' });
    expect(calls).toEqual(['POST content/changeset/validate {}']);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Publish' }));
    await waitFor(() => expect(calls).toContain('POST content/changeset/publish {"note":"Pricier gold","by":"Hylke"}'));
    expect(await screen.findByText('Published changeset #3')).toBeTruthy();
    expect(localStorage.getItem('aurora21.panel.changes.by')).toBe('Hylke');
  });

  it('shows why a publish was refused and offers to keep my value on a conflict', async () => {
    replies['POST content/changeset/publish'] = { status: 409, body: {
      error: '1 pending change(s) no longer match the live content; review them first.',
      conflicts: [{ changeId: 9, area: 'store_pack', key: { packId: 7 }, expected: { price: 7500 }, live: { price: 8000 } }],
    } };
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Publish' }));
    expect(await screen.findByText(/no longer match the live content/)).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: 'Keep my value' }));
    await waitFor(() => expect(calls.slice(-2)).toEqual([
      'DELETE content/changeset/changes/9',
      'PUT content/changeset/changes {"area":"store_pack","key":{"packId":7},"operation":"update","after":{"price":9000,"size":12}}',
    ]));
  });
});

describe('Changes: history', () => {
  it('expands readable history and collapses it without leaving hidden controls accessible', async () => {
    renderPage('history');
    const button = await screen.findByRole('button', { name: 'Show changes' });
    expect(screen.queryByText('pack 7')).toBeNull();
    fireEvent.click(button);
    expect(await screen.findByText('pack 7')).toBeTruthy();
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(document.getElementById(button.getAttribute('aria-controls') ?? '')).toBeTruthy();
    fireEvent.click(button);
    expect(button.getAttribute('aria-expanded')).toBe('false');
    await waitFor(() => expect(screen.queryByText('pack 7')).toBeNull());
  });

  it('does not offer a revert that would hide a newly inserted player card', async () => {
    replies['GET content/history?limit=50'] = { status: 200, body: { changesets: [{ id: 4, publishedAt: new Date().toISOString(), changes: [
      { id: 11, area: 'player_card', key: { resourceId: 16935239 }, operation: 'insert', before: null, after: { base: 158023, cardTypeId: 5, card: { rating: 98 } } },
    ] }] } };
    renderPage('history');
    expect(await screen.findByText(/cannot be reverted because clubs may own it/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Revert' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('stages a revert and goes back to Pending', async () => {
    replies['GET content/changeset'] = { status: 200, body: { open: false, pending: 0, changeset: null } };
    renderPage('history');
    expect(await screen.findByText('Cheaper gold')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Revert' }));
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Revert changeset #2?' })).getByRole('button', { name: 'Stage the revert' }));
    await waitFor(() => expect(calls).toEqual(['POST content/history/2/revert {}']));
    await waitFor(() => expect(window.location.hash).toBe('#/changes'));
  });

  it('blocks a revert while other edits are pending', async () => {
    renderPage('history');
    await screen.findByText('Cheaper gold');
    expect((screen.getByRole('button', { name: 'Revert' }) as HTMLButtonElement).disabled).toBe(true);
    expect(await screen.findByText(/Publish or discard pending edits before staging a revert/)).toBeTruthy();
    expect(calls).toEqual([]);
  });
});
