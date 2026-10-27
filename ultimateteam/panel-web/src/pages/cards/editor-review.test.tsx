import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ToastProvider } from '../../ui/Toast';
import { CardsPage } from './CardsPage';
import { App } from '../../app/App';
import type { CardEditorReply, ChangesetReply, ContentChange } from '../../api/types';
import { cardEditorReply, cardImagesReply, editableCard } from '../../../test/cardFixtures';

let source: CardEditorReply;
let pending: ChangesetReply;
let writes: { path: string; method: string; body: Record<string, unknown> }[];
let refuseStage = false;
let failEditor = false;
let revision = 4;
let immediate = false;
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
beforeEach(() => {
  source = structuredClone(cardEditorReply); pending = { open: false, pending: 0, changeset: null }; writes = [];
  refuseStage = false; failEditor = false;
  revision = 4; immediate = false;
  localStorage.clear(); sessionStorage.clear();
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace('/panel/api/', '').replace(/([?&])apply=1(?:&|$)/, '$1').replace(/[?&]$/, ''); const method = init?.method ?? 'GET';
    if (method !== 'GET') writes.push({ path, method, body: JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown> });
    if (path === 'session') return json({ ok: true });
    if (path === 'dashboard') return json({ services: { fut: true, blaze: true }, online: { count: 0 } });
    if (path.startsWith('cards/editor/')) return failEditor ? json({ error: 'Editor unavailable' }, 500) : json(source);
    if (path === 'cards/images/158023') return json(cardImagesReply);
    if (path === 'cards/new') return json({ resourceId: 33712455 });
    if (path === 'content/status') return json({ revision, loadedDatabaseRevision: 10, externalChanges: false, reloadRequired: false, tables: [] });
    if (path === 'content/changeset') return json(pending);
    if (path === 'content/changeset/changes') {
      if (refuseStage) return json({ error: 'Synthetic stage refusal' }, 400);
      const body = JSON.parse(String(init?.body)) as ContentChange;
      pending = { open: true, pending: 1, changeset: { id: 3, changes: [{ ...body, id: 9, before: { card: source.card } }] } };
      if (immediate) {
        source = { ...source, resourceId: Number(body.key.resourceId), card: structuredClone((body.after as { card: CardEditorReply['card'] }).card), cardTypeId: (body.after as { cardTypeId: number }).cardTypeId };
        const published = { ...pending, open: false, pending: 0, changeset: { ...pending.changeset!, status: 'published' } };
        pending = { open: false, pending: 0, changeset: null };
        return json(published);
      }
      return json(pending);
    }
    if (path === 'content/changeset/validate') return json({ ok: true, changes: 1, conflicts: [], errors: [] });
    if (path === 'content/changeset/publish') {
      source.card = structuredClone((pending.changeset?.changes?.[0]?.after as { card: CardEditorReply['card'] }).card);
      pending = { open: false, pending: 0, changeset: null };
      revision = 5;
      return json({ published: { id: 3 }, content: { revision: 5, warnings: [], notes: [] } });
    }
    if (path.startsWith('content/history?')) return json({ changesets: [] });
    if (path.startsWith('cards/specials')) return json({ types: [] });
    if (path.startsWith('cards?')) return json({ total: 1, cards: [{ cardKey: `158023:${source.card.rating}:1`, resourceId: 158023, name: 'Messi', rating: source.card.rating, rareflag: 1, position: 'RW', assetId: 158023 }] });
    if (path === 'assets') return json({ available: true, baseUrl: '/panel/assets/', frames: ['1_gold.png', '1_silver.png', '1_bronze.png', '3_silver.png', '3_gold.png', '5_toty.png'] });
    return json({ error: 'Not found' }, 404);
  }));
});
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });

function renderEditor(creating = false, draftId?: number, resourceId = 158023) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return { client, ...render(<QueryClientProvider client={client}><ToastProvider><CardsPage rest={[String(resourceId), creating ? 'new' : 'edit']} params={new URLSearchParams(draftId ? `draft=${draftId}` : '')} /></ToastProvider></QueryClientProvider>) };
}

describe('Card editor review', () => {
  it('updates the allocated card on a second immediate save and ignores published history rows', async () => {
    immediate = true;
    renderEditor(true);
    fireEvent.change(await screen.findByLabelText('Overall'), { target: { value: '95' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Card saved');
    expect(writes.filter(write => write.path === 'cards/new')).toHaveLength(1);
    expect(screen.queryByText('Review in Changes')).toBeNull();
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '96' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(writes.filter(write => write.path === 'content/changeset/changes')).toHaveLength(2));
    const saves = writes.filter(write => write.path === 'content/changeset/changes');
    expect(saves[0]!.body).toMatchObject({ operation: 'insert', key: { resourceId: 33712455 }, after: { base: 158023, card: { rating: 95 } } });
    expect(saves[1]!.body).toMatchObject({ operation: 'update', key: { resourceId: 33712455 }, after: { card: { rating: 96 } } });
    expect((saves[1]!.body.after as Record<string, unknown>).base).toBeUndefined();
    expect(writes.filter(write => write.path === 'cards/new')).toHaveLength(1);
    await waitFor(() => expect((screen.getByLabelText('Overall') as HTMLInputElement).value).toBe('96'));
  });

  it('keeps silver/gold Inform identity and frame as rating rises, then switches to Bronze Rare below 65', async () => {
    const page = renderEditor(true);
    fireEvent.change(await screen.findByLabelText('Overall'), { target: { value: '74' } });
    fireEvent.click(screen.getByRole('button', { name: 'Choose card type' }));
    fireEvent.click(screen.getByRole('radio', { name: 'Team of the Week' }).querySelector('.card-type-frame')!);
    expect(page.container.querySelector('img[src="/panel/assets/cards/hd/3_silver.png"]')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Card saved');
    expect(writes.at(-1)?.body).toMatchObject({ after: { cardTypeId: 9 } });
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '75' } });
    expect(page.container.querySelector('img[src="/panel/assets/cards/hd/3_gold.png"]')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await waitFor(() => expect(writes.filter((write) => write.path === 'content/changeset/changes')).toHaveLength(2));
    expect(writes.at(-1)?.body).toMatchObject({ after: { cardTypeId: 8 } });
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '64' } });
    expect(screen.getByRole('button', { name: 'Choose card type' }).textContent).toContain('Rare');
    fireEvent.click(screen.getByRole('button', { name: 'Choose card type' }));
    expect(screen.queryByRole('radio', { name: 'Team of the Week' })).toBeNull();
    fireEvent.click(screen.getByRole('radio', { name: 'Rare' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await waitFor(() => expect(writes.filter((write) => write.path === 'content/changeset/changes')).toHaveLength(3));
    expect(writes.at(-1)?.body).toMatchObject({ after: { cardTypeId: 3, card: { rating: 64 } } });
  });
  it('deduplicates special tiers and excludes Icons and Swaps for regular players', async () => {
    renderEditor(true);
    fireEvent.click(await screen.findByRole('button', { name: 'Choose card type' }));
    expect(screen.getAllByRole('radio', { name: 'Team of the Week' })).toHaveLength(1);
    expect(screen.queryByRole('radio', { name: 'Icon' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Icon Moments' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Icon Swaps 1' })).toBeNull();
    fireEvent.click(screen.getByRole('radio', { name: 'Team of the Week' }).querySelector('span:last-child')!);
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '70' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Card saved');
    expect(writes.at(-1)?.body).toMatchObject({ after: { cardTypeId: 9 } });
  });

  it('offers only Icon types for Icons and blocks overall below 85', async () => {
    source.cardTypeId = 12;
    renderEditor(true);
    fireEvent.click(await screen.findByRole('button', { name: 'Choose card type' }));
    expect(screen.getAllByRole('radio').filter((option) => option.closest('.card-type-options'))).toHaveLength(2);
    expect(screen.getByRole('radio', { name: 'Icon' })).toBeTruthy();
    expect(screen.getByRole('radio', { name: 'Icon Moments' })).toBeTruthy();
    expect(screen.queryByRole('radio', { name: 'Rare' })).toBeNull();
    fireEvent.click(screen.getByRole('radio', { name: 'Icon Moments' }).querySelector('.card-type-frame')!);
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '84' } });
    expect((screen.getByLabelText('Overall') as HTMLInputElement).min).toBe('85');
    expect((screen.getByRole('button', { name: 'Save new card' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText('Icons require an overall of at least 85.')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '85' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Card saved');
    expect(writes.at(-1)?.body).toMatchObject({ after: { cardTypeId: 84, card: { rating: 85 } } });
  });

  it('selects TOTW through the mouse blur/click sequence and stages the chosen rarity', async () => {
    renderEditor(true);
    const opener = await screen.findByRole('button', { name: 'Choose card type' });
    opener.focus(); fireEvent.click(opener);
    const option = screen.getByRole('radio', { name: 'Team of the Week' });
    fireEvent.pointerDown(option);
    fireEvent.blur(opener, { relatedTarget: null });
    expect(screen.getByRole('radio', { name: 'Team of the Week' })).toBeTruthy();
    fireEvent.click(option.querySelector('.card-type-frame')!);
    expect(opener.textContent).toContain('Team of the Week');
    expect(screen.queryByRole('radio', { name: 'Team of the Week' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Card saved');
    expect(writes.at(-1)?.body).toMatchObject({ after: { cardTypeId: 8 } });
  });

  it('keeps each face-stat score available when its attributes are collapsed', async () => {
    renderEditor();
    await screen.findByLabelText('Pace (card)');
    expect(screen.getByLabelText('Shooting (card)')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Shooting' }));
    await waitFor(() => expect(screen.queryByLabelText('Finishing')).toBeNull());
    expect(screen.queryByLabelText('Finishing')).toBeNull();
    fireEvent.change(screen.getByLabelText('Shooting (card)'), { target: { value: '90' } });
    fireEvent.click(screen.getByRole('button', { name: 'Expand Shooting' }));
    expect((screen.getByLabelText('Finishing') as HTMLInputElement).value).toBe('90');
  });

  it.each([false, true])('opens all stat groups initially (creating=%s)', async (creating) => {
    renderEditor(creating);
    await screen.findByLabelText('Pace (card)');
    for (const title of ['Pace', 'Shooting', 'Passing', 'Dribbling', 'Defending', 'Physical']) {
      expect(screen.getByRole('button', { name: `Collapse ${title}` }).getAttribute('aria-expanded')).toBe('true');
    }
    expect(screen.getByLabelText('Finishing')).toBeTruthy();
    expect(screen.getByLabelText('Stamina')).toBeTruthy();
  });

  it('shows rare/non-rare once with frame previews and stages the rating-selected standard tier', async () => {
    renderEditor(true);
    fireEvent.click(await screen.findByRole('button', { name: 'Choose card type' }));
    expect(screen.getAllByRole('radio', { name: 'Rare' })).toHaveLength(1);
    expect(screen.getAllByRole('radio', { name: 'Non-rare' })).toHaveLength(1);
    expect(screen.queryByRole('radio', { name: 'Silver rare' })).toBeNull();
    fireEvent.click(screen.getByRole('radio', { name: 'Rare' }));
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '64' } });
    expect(screen.getByRole('button', { name: 'Choose card type' }).textContent).toContain('bronze');
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Card saved');
    expect(writes.at(-1)?.body).toMatchObject({ after: { cardTypeId: 3, card: { rating: 64 } } });
  });

  it('offers only position-appropriate and shared traits and drops incompatible choices on position change', async () => {
    renderEditor();
    await screen.findByLabelText('Finesse shot');
    expect(screen.queryByLabelText('GK Long Throw')).toBeNull();
    expect(screen.getByLabelText('Injury Prone')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Position'), { target: { value: 'GK' } });
    expect(screen.queryByLabelText('Finesse shot')).toBeNull();
    fireEvent.click(screen.getByLabelText('GK Long Throw'));
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Card saved');
    expect(writes.at(-1)?.body).toMatchObject({ after: { card: { position: 'GK', traits: [15] } } });
  });

  it('updates detailed pace and the live card stat in both directions', async () => {
    renderEditor();
    fireEvent.change(await screen.findByLabelText('Acceleration'), { target: { value: '80' } });
    expect((screen.getByLabelText('Pace (card)') as HTMLInputElement).value).toBe('75');
    fireEvent.change(screen.getByLabelText('Pace (card)'), { target: { value: '85' } });
    expect((screen.getByLabelText('Acceleration') as HTMLInputElement).value).toBe('91');
    expect((screen.getByLabelText('Sprint speed') as HTMLInputElement).value).toBe('80');
  });

  it('selects an existing dynamic head and stages it with the same player’s card', async () => {
    const page = renderEditor();
    const choice = await screen.findByRole('radio', { name: /Messi 98/ });
    fireEvent.click(choice);
    expect([...page.container.querySelectorAll('img')].some((img) => img.src.endsWith('/dynamic/p117598535.png'))).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Card saved');
    expect(writes[0]?.body).toMatchObject({ operation: 'update', after: { card: { head_resource_id: 117598535, custom_head_id: null } } });
    expect(writes).toHaveLength(1);
  });

  it('shows the native bundled preview for the selected own head without staging an override', async () => {
    source.art = { ...source.art, defaultPortraitUrl: '/panel/assets/dynamic/p167984358.png', portraitId: 201538790 };
    renderEditor();
    const own = await screen.findByRole('radio', { name: /This card’s own head/ });
    expect((own as HTMLInputElement).checked).toBe(true);
    expect(own.closest('label')!.querySelector('img')!.getAttribute('src')).toBe('/panel/assets/dynamic/p167984358.png');
    expect(writes).toHaveLength(0);
  });

  it('offers a base image for a special card using only image tiles', async () => {
    source.resourceId = 117598535;
    const page = renderEditor(false, undefined, 117598535);
    const base = await screen.findByRole('radio', { name: /Base player head/ });
    expect(screen.getByRole('heading', { name: 'Card Image' })).toBeTruthy();
    expect(screen.getByRole('group', { name: 'Available images' })).toBeTruthy();
    expect(screen.queryByLabelText('Head')).toBeNull();
    expect(screen.queryByPlaceholderText('Find a portrait')).toBeNull();
    expect(base.closest('label')!.querySelector('img')!.getAttribute('src')).toBe('/panel/assets/players/158023.png');
    fireEvent.click(base);
    expect((base as HTMLInputElement).checked).toBe(true);
    expect(page.getByLabelText('Card preview').querySelector('img[src="/panel/assets/players/158023.png"]')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Card saved');
    expect(writes[0]?.body).toMatchObject({ after: { card: { head_resource_id: 158023, custom_head_id: null } } });
  });

  it('selects an existing upload without uploading it again', async () => {
    renderEditor();
    fireEvent.click(await screen.findByRole('radio', { name: /Winter portrait/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Card saved');
    expect(writes[0]?.body).toMatchObject({ after: { card: { head_resource_id: null, custom_head_id: 42 } } });
    expect(writes.some((write) => write.path === 'heads')).toBe(false);
  });

  it('omits cards without dynamic images and the source card from new-card portraits', async () => {
    renderEditor(true);
    await screen.findByRole('radio', { name: /Messi 98/ });
    const options = screen.getAllByRole('radio').map((option) => (option as HTMLInputElement).value);
    expect(options).not.toContain('card:158023');
    expect(options).not.toContain('card:16935239');
    expect(options).toContain('card:117598535');
  });

  it('keeps a real dynamic portrait selectable if loading its image fails', async () => {
    const page = renderEditor();
    const choice = await screen.findByRole('radio', { name: /Messi 98/ });
    const image = page.container.querySelector('img[src="/panel/assets/dynamic/p117598535.png"]')!;
    fireEvent.error(image);
    fireEvent.click(choice);
    expect((choice as HTMLInputElement).checked).toBe(true);
    expect(screen.getAllByText('Preview unavailable').length).toBeGreaterThan(0);
  });

  it('switches face labels and detailed stats by position without losing hidden values', async () => {
    renderEditor();
    await screen.findByLabelText('Pace (card)');
    expect(screen.queryByRole('button', { name: 'Expand Diving' })).toBeNull();
    fireEvent.change(screen.getByLabelText('Finishing'), { target: { value: '88' } });
    fireEvent.change(screen.getByLabelText('Position'), { target: { value: 'GK' } });
    expect(screen.getByLabelText('Diving (card)')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Collapse Handling' })).toBeTruthy();
    expect(screen.queryByLabelText('Pace (card)')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Expand Shooting' })).toBeNull();
    expect(screen.queryByLabelText('Finishing')).toBeNull();
    fireEvent.change(screen.getByLabelText('Diving attribute'), { target: { value: '90' } });
    fireEvent.change(screen.getByLabelText('Position'), { target: { value: 'RW' } });
    expect(screen.queryByRole('button', { name: 'Expand Diving' })).toBeNull();
    expect((screen.getByLabelText('Finishing') as HTMLInputElement).value).toBe('88');
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Card saved');
    expect(writes[0]?.body).toMatchObject({ after: { card: { position: 'RW', finishing: 88, gk_diving: 90 } } });
  });

  it('saves a new card twice using one resource id, and clears the earlier success feedback on editing', async () => {
    renderEditor(true);
    fireEvent.click(await screen.findByRole('button', { name: 'Choose card type' }));
    fireEvent.click(screen.getByRole('radio', { name: 'Team of the Year' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Card saved');
    expect(window.location.hash).toBe('#/cards/158023/new?draft=33712455');
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '99' } });
    await waitFor(() => expect(screen.queryByText('Card saved')).toBeNull());
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await waitFor(() => expect(writes.filter((write) => write.path === 'content/changeset/changes')).toHaveLength(2));
    expect(writes.filter((write) => write.path === 'cards/new')).toHaveLength(1);
    expect(writes.at(-1)?.body).toMatchObject({ key: { resourceId: 33712455 }, operation: 'insert', after: { cardTypeId: 5, card: { rating: 99 } } });
  });

  it('reuses an allocated new id after a stage refusal and retains the typed values', async () => {
    refuseStage = true;
    renderEditor(true);
    fireEvent.change(await screen.findByLabelText('Overall'), { target: { value: '97' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Synthetic stage refusal');
    expect((screen.getByLabelText('Overall') as HTMLInputElement).value).toBe('97');
    refuseStage = false;
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Card saved');
    expect(writes.filter((write) => write.path === 'cards/new')).toHaveLength(1);
  });

  it('reopens a staged creation with its chosen type, rating and allocated id', async () => {
    pending = { open: true, pending: 1, changeset: { id: 3, changes: [{ id: 9, area: 'player_card', key: { resourceId: 33712455 }, operation: 'insert',
      after: { base: 158023, cardTypeId: 5, card: { ...editableCard, rating: 98, head_resource_id: 117598535 } } }] } };
    renderEditor(true, 33712455);
    expect((await screen.findByLabelText('Overall') as HTMLInputElement).value).toBe('98');
    expect(screen.getByRole('button', { name: 'Choose card type' }).textContent).toContain('Team of the Year');
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '99' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await screen.findByText('Card saved');
    expect(writes.map((write) => write.path)).toEqual(['content/changeset/changes']);
  });

  it('does not recreate a published or discarded draft from its old address', async () => {
    renderEditor(true, 33712455);
    await screen.findByText('This new card is no longer pending');
    expect(screen.queryByRole('button', { name: 'Save new card' })).toBeNull();
    expect(writes).toEqual([]);
  });

  it('refreshes an untouched editor after publication and allows another edit without reopening', async () => {
    const { client } = renderEditor();
    await screen.findByLabelText('Overall');
    source.card.face_slot_0 = 1;
    source.card.acceleration = 1;
    source.card.sprint_speed = 1;
    await act(async () => { await client.invalidateQueries({ queryKey: ['card-editor'] }); });
    await waitFor(() => expect((screen.getByLabelText('Acceleration') as HTMLInputElement).value).toBe('1'));
    expect(screen.queryByText(/Published or pending values changed while/)).toBeNull();
    fireEvent.change(screen.getByLabelText('Acceleration'), { target: { value: '20' } });
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Card saved');
    expect(writes.at(-1)?.body).toMatchObject({ after: { card: { acceleration: 20, sprint_speed: 1 } } });
  });

  it('refreshes clean staged values when publication removes the pending edit', async () => {
    const { client } = renderEditor();
    fireEvent.change(await screen.findByLabelText('Overall'), { target: { value: '94' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Card saved');
    source.card.rating = 94;
    pending = { open: false, pending: 0, changeset: null };
    await act(async () => { await client.invalidateQueries(); });
    await waitFor(() => expect(screen.queryByText(/Published or pending values changed while/)).toBeNull());
    expect((screen.getByLabelText('Overall') as HTMLInputElement).value).toBe('94');
    fireEvent.change(screen.getByLabelText('Overall'), { target: { value: '95' } });
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('preserves dirty fields after a pending edit changes and requires an explicit reload', async () => {
    const { client } = renderEditor();
    fireEvent.change(await screen.findByLabelText('Overall'), { target: { value: '94' } });
    act(() => { client.setQueryData(['changeset'], { open: true, pending: 1, changeset: { id: 3, changes: [{ id: 9, area: 'player_card', operation: 'update', key: { resourceId: 158023 }, after: { card: { ...editableCard, rating: 98 } } }] } }); });
    await screen.findByText(/Published or pending values changed while/);
    expect((screen.getByLabelText('Overall') as HTMLInputElement).value).toBe('94');
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Reload form values' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Reload form values' }));
    await waitFor(() => expect((screen.getByLabelText('Overall') as HTMLInputElement).value).toBe('98'));
  });

  it('retains typed fields when refreshing the editor fails', async () => {
    const { client } = renderEditor();
    fireEvent.change(await screen.findByLabelText('Overall'), { target: { value: '95' } });
    failEditor = true;
    await act(async () => { await client.invalidateQueries({ queryKey: ['card-editor'] }); });
    await screen.findByText('Editor unavailable (HTTP 500)');
    expect((screen.getByLabelText('Overall') as HTMLInputElement).value).toBe('95');
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('preserves edited detailed stats through collapsing and reopening', async () => {
    renderEditor();
    fireEvent.change(await screen.findByLabelText('Finishing'), { target: { value: '90' } });
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Shooting' }));
    await waitFor(() => expect(screen.queryByLabelText('Finishing')).toBeNull());
    fireEvent.click(screen.getByRole('button', { name: 'Expand Shooting' }));
    expect((await screen.findByLabelText('Finishing') as HTMLInputElement).value).toBe('90');
  });

  it('confirms leaving the form after an unstaged edit', async () => {
    renderEditor();
    fireEvent.change(await screen.findByLabelText('Overall'), { target: { value: '94' } });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    const dialog = await screen.findByRole('dialog', { name: 'Leave without saving?' });
    expect(writes).toEqual([]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Leave card editor' }));
    await waitFor(() => expect(window.location.hash).toBe('#/cards'));
  });

  it('does not move back to a card editor when a new-card save finishes after navigation', async () => {
    const original = fetch;
    let finish: ((reply: Response) => void) | undefined;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => url.replace(/\?apply=1$/, '').endsWith('content/changeset/changes')
      ? new Promise<Response>((resolve) => { finish = resolve; }) : original(url, init)));
    const page = renderEditor(true);
    await screen.findByLabelText('Overall');
    fireEvent.click(screen.getByRole('button', { name: 'Save new card' }));
    await waitFor(() => expect(finish).toBeDefined());
    page.unmount();
    window.location.hash = '#/changes';
    await act(async () => { finish?.(json({ open: true, pending: 1 })); });
    expect(window.location.hash).toBe('#/changes');
  });

  it('previews game pixels without saving, then explicitly applies the uploaded head', async () => {
    class Reader {
      result: string | null = null; onload: (() => void) | null = null; onerror: (() => void) | null = null;
      readAsDataURL() { this.result = 'data:image/png;base64,AA=='; this.onload?.(); }
    }
    class Picture {
      naturalWidth = 180; naturalHeight = 180; onload: (() => void) | null = null; onerror: (() => void) | null = null;
      set src(_value: string) { this.onload?.(); }
    }
    vi.stubGlobal('FileReader', Reader); vi.stubGlobal('Image', Picture);
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage: () => {}, getImageData: () => ({ data: new Uint8ClampedArray(180 * 180 * 4) }) } as unknown as CanvasRenderingContext2D);
    vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockReturnValue('data:image/png;base64,AAAA');
    const original = fetch;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url === '/panel/api/heads/preview') return json({ png: 'GAMEPIXELS' });
      if (url === '/panel/api/heads') {
        writes.push({ path: 'heads', method: 'POST', body: JSON.parse(String(init?.body)) as Record<string, unknown> });
        return json({ headId: 43, label: 'Spring portrait', url: '/panel/heads/43.png' });
      }
      return original(url, init);
    }));
    renderEditor();
    const input = await screen.findByLabelText('Upload a portrait');
    fireEvent.change(input, { target: { files: [new File(['synthetic'], 'spring.png', { type: 'image/png' })] } });
    await screen.findByAltText('Decoded FIFA 21 portrait texture');
    expect(writes).toHaveLength(0);
    fireEvent.click(screen.getByRole('button', { name: 'Discard portrait' }));
    expect(screen.queryByAltText('Decoded FIFA 21 portrait texture')).toBeNull();
    expect(writes).toHaveLength(0);
    fireEvent.change(input, { target: { files: [new File(['synthetic'], 'spring.png', { type: 'image/png' })] } });
    await screen.findByAltText('Decoded FIFA 21 portrait texture');
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByRole('slider', { name: 'Portrait size' }), { target: { value: '125' } });
    await waitFor(() => expect((screen.getByRole('button', { name: 'Use portrait' }) as HTMLButtonElement).disabled).toBe(false));
    expect(screen.getByAltText('Decoded FIFA 21 portrait texture').getAttribute('src')).toBe('data:image/png;base64,GAMEPIXELS');
    fireEvent.click(screen.getByRole('button', { name: 'Use portrait' }));
    await screen.findByRole('radio', { name: /Spring portrait/ });
    expect((screen.getByRole('radio', { name: /Spring portrait/ }) as HTMLInputElement).checked).toBe(true);
    expect(writes).toHaveLength(1);
    expect(writes[0]?.path).toBe('heads');
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Card saved');
    expect(writes.at(-1)?.body).toMatchObject({ after: { card: { custom_head_id: 43, head_resource_id: null } } });
    expect(writes.some((write) => write.path === 'content/changeset/publish')).toBe(false);
    vi.restoreAllMocks();
  });

  it('connects card staging, review, reopening and confirmed publishing across modules 2 and 3', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    window.location.hash = '#/cards/158023/edit';
    render(<QueryClientProvider client={client}><ToastProvider><App /></ToastProvider></QueryClientProvider>);
    fireEvent.change(await screen.findByLabelText('Overall'), { target: { value: '94' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    fireEvent.click(await screen.findByRole('link', { name: 'Review in Changes' }));
    await screen.findByText('card.rating');
    expect(screen.getByRole('link', { name: 'Edit staged card' }).getAttribute('href')).toBe('#/cards/158023/edit');
    fireEvent.click(screen.getByRole('link', { name: 'Edit staged card' }));
    expect((await screen.findByLabelText('Overall') as HTMLInputElement).value).toBe('94');
    fireEvent.click(screen.getByRole('link', { name: 'Review pending card' }));
    // One action validates and, when every edit passes, opens the publish confirmation.
    fireEvent.click(await screen.findByRole('button', { name: 'Validate & publish' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Publish' }));
    await screen.findByText('Published changeset #3');
    expect(source.card.rating).toBe(94);
    expect(pending.pending).toBe(0);
    expect(writes.filter((write) => write.path === 'content/changeset/publish')).toHaveLength(1);
  });
});
