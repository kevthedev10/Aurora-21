import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { parseJson, parsePath, pathText } from '../../api/rules';
import { ToastProvider } from '../../ui/Toast';
import { RulesPage } from './RulesPage';

// Shaped like OperatorPanel.Content.cs replies.
const staged = { id: 4, area: 'rules', key: { file: 'market', path: ['listingFee'] }, operation: 'update', before: { value: 5 }, after: { value: 6 } };
let calls: string[] = [];
let pending = [staged];
let live = 30;
beforeEach(() => {
  calls = [];
  pending = [staged];
  live = 30;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, '');
    const method = init?.method ?? 'GET';
    if (method !== 'GET') calls.push(`${method} ${path} ${String(init?.body ?? '')}`);
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    if (path.startsWith('content/rules?')) return json({ area: 'rules', files: [{ file: 'market', rules: 2 }, { file: 'cards', rules: 40 }, { file: 'sbc', rules: 80 }] });
    if (path.startsWith('content/rules/market?path=')) {
      const key = JSON.parse(new URLSearchParams(path.split('?')[1]).get('path')!);
      return json({ file: 'market', definition: { value: key[0] === 'listingFee' ? 5 : live } });
    }
    if (path.startsWith('content/rules/market?')) return json({ total: 2, rules: [{ path: ['maintenanceIntervalSeconds'], value: 30 }, { path: ['listingFee'], value: 5 }] });
    if (path === 'content/changeset') return json({ open: true, pending: pending.length, changeset: { id: 3, changes: pending } });
    if (path === 'content/rules/import') return json({ staged: 2 });
    return json({});
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

const renderPage = (rest: string[] = [], params = new URLSearchParams()) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ToastProvider><RulesPage rest={rest} params={params} /></ToastProvider></QueryClientProvider>);
};

describe('rule paths', () => {
  it('parse and print', () => {
    expect(parsePath('["priceBounds", "maximumByRating", 2, "maximum"]')).toEqual(['priceBounds', 'maximumByRating', 2, 'maximum']);
    expect(parsePath('[]')).toBeNull();
    expect(parsePath('"text"')).toBeNull();
    expect(parsePath('["a", -1]')).toBeNull();
    expect(parsePath('[""]')).toBeNull();
    expect(parsePath('[100001]')).toBeNull();
    expect(parseJson('{"value":1e400}').ok).toBe(false);
    expect(pathText(['priceBounds', 2, 'maximum'])).toBe('priceBounds › #2 › maximum');
  });
});

describe('Rules page', () => {
  it('removes Champions from Rules without links to other editors', async () => {
    renderPage(['champions_rewards']);
    expect(await screen.findByText('Rule file not found')).toBeTruthy();
    expect(screen.queryByRole('link', { name: /Aurora Champions/ })).toBeNull();
    cleanup();
    renderPage();
    await screen.findByText('maintenanceIntervalSeconds');
    expect(screen.queryByRole('link', { name: 'Draft' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Packs' })).toBeNull();
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('content/rules?view=rules&comments=0'))).toBe(true);
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('content/rules/market?view=rules&'))).toBe(true);
  });

  it('leaves the SBC rules to SBCs → Settings', async () => {
    renderPage();
    expect(await screen.findByText('market.json')).toBeTruthy();
    expect(screen.queryByText('sbc.json')).toBeNull();
    cleanup();
    renderPage(['sbc']);
    expect(await screen.findByText('Rule file not found')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'SBCs → Settings' })).toBeNull();
  });

  it('marks a staged rule and stages an edit to the rules area', async () => {
    renderPage();
    expect(await screen.findByText('Staged → 6')).toBeTruthy();
    const row = screen.getByText('maintenanceIntervalSeconds').closest('li')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Value'), { target: { value: '45' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(calls).toEqual(['PUT content/changeset/changes {"area":"rules","key":{"file":"market","path":["maintenanceIntervalSeconds"]},"operation":"update","after":{"value":45}}']));
  });

  it('refuses invalid JSON in the edit dialog', async () => {
    renderPage();
    const row = (await screen.findByText('maintenanceIntervalSeconds')).closest('li')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Edit as JSON' }));
    fireEvent.change(within(dialog).getByLabelText('Value'), { target: { value: '{nope' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(within(dialog).getByText('That is not valid JSON.')).toBeTruthy();
    expect(calls).toEqual([]);
  });

  it('stages a removal only after confirming', async () => {
    renderPage();
    const row = (await screen.findByText('maintenanceIntervalSeconds')).closest('li')!;
    fireEvent.click(within(row).getByRole('button', { name: 'Remove' }));
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Remove this rule?' })).getByRole('button', { name: 'Remove rule' }));
    await waitFor(() => expect(calls).toEqual(['PUT content/changeset/changes {"area":"rules","key":{"file":"market","path":["maintenanceIntervalSeconds"]},"operation":"hide","after":null}']));
  });

  it('checks the path before staging a change at any location', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Advanced editing' }));
    const section = (await screen.findByRole('heading', { name: 'Edit any location' })).closest('section')!;
    fireEvent.change(within(section).getByLabelText('Path (JSON list)'), { target: { value: 'priceBounds' } });
    fireEvent.click(within(section).getByRole('button', { name: 'Save changes' }));
    expect(within(section).getByRole('alert').textContent).toContain('JSON list');
    fireEvent.change(within(section).getByLabelText('Path (JSON list)'), { target: { value: '["newKey"]' } });
    fireEvent.change(within(section).getByLabelText('Change'), { target: { value: 'insert' } });
    fireEvent.change(within(section).getByLabelText('Value (JSON)'), { target: { value: 'true' } });
    fireEvent.click(within(section).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(calls).toEqual(['PUT content/changeset/changes {"area":"rules","key":{"file":"market","path":["newKey"]},"operation":"insert","after":{"value":true}}']));
  });

  it('imports every file and opens Changes when something was staged', async () => {
    pending = [];
    renderPage();
    await screen.findByText('maintenanceIntervalSeconds');
    fireEvent.click(screen.getByRole('button', { name: 'Import from files' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'All files' }));
    await waitFor(() => expect(calls).toEqual(['POST content/rules/import {}']));
    await waitFor(() => expect(window.location.hash).toBe('#/changes'));
  });

  it('reopens the staged value even when key property order differs', async () => {
    pending = [{ ...staged, key: { path: ['listingFee'], file: 'market' } }];
    renderPage();
    await screen.findByText('Staged → 6');
    fireEvent.click(within(screen.getByText('listingFee').closest('li')!).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    expect((within(dialog).getByLabelText('Value') as HTMLInputElement).value).toBe('6');
  });

  it('refuses to overwrite a value changed while the editor was open', async () => {
    renderPage();
    await screen.findByText('Staged → 6');
    fireEvent.click(within(screen.getByText('maintenanceIntervalSeconds').closest('li')!).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    live = 31;
    fireEvent.change(within(dialog).getByLabelText('Value'), { target: { value: '45' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    await within(dialog).findByText(/changed while the editor was open/);
    expect(calls).toEqual([]);
  });

  it('blocks imports that could replace pending edits', async () => {
    renderPage();
    await screen.findByText('Staged → 6');
    fireEvent.click(screen.getByRole('button', { name: 'Import from files' }));
    const dialog = await screen.findByRole('dialog');
    expect((within(dialog).getByRole('button', { name: 'Only market' }) as HTMLButtonElement).disabled).toBe(true);
    expect((within(dialog).getByRole('button', { name: 'All files' }) as HTMLButtonElement).disabled).toBe(true);
    expect(calls).toEqual([]);
  });
});
