// Inventory (plan 4.6): one pile at a time, split into the Cards page's kinds (players, consumables, club customisation,
// managers), drawn as cards or listed. Read live from the database. Player cards can be removed from the club
// (removeItems); unequipped non-player items can be taken away, so an equipped kit or badge slot stays intact.
// Aurora 21 has no starter pile (the server always answers it empty), so it is not offered.
import { useState } from 'react';
import { useAssets } from '../../api/assets';
import { refusedUnchanged, supports, useClubEdit, useInventory, type InventoryItem, type Pile, type PlayerDetail } from '../../api/players';
import { Badge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { Chips } from '../../ui/Chips';
import { DataTable, type Column } from '../../ui/DataTable';
import { ConfirmDialog, Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { searchText } from '../../ui/search';
import { Tabs } from '../../ui/Tabs';
import { useToast } from '../../ui/Toast';

import { FutCard } from '../../ui/FutCard';
import { EmptyState, ErrorPanel, Loading, Section, Toolbar } from '../../ui/Layout';
import { VirtualCardGrid } from '../../ui/VirtualCardGrid';

const KIND: Record<string, string> = { player: 'Player', special: 'Special', manager: 'Manager', consumable: 'Consumable', clubItem: 'Club item', other: 'Other' };
const VIEW_KEY = 'aurora21.panel.cards.view';
const savedView = (): 'cards' | 'table' => { try { return localStorage.getItem(VIEW_KEY) === 'table' ? 'table' : 'cards'; } catch { return 'cards'; } };

type Group = 'players' | 'consumables' | 'customisation' | 'managers';
const GROUPS: { value: Group; label: string }[] = [
  { value: 'players', label: 'Players' }, { value: 'consumables', label: 'Consumables' },
  { value: 'customisation', label: 'Club customisation' }, { value: 'managers', label: 'Managers' },
];
/** The Cards tab an item belongs to; player picks and anything unknown count as consumables. */
export const groupOf = (item: InventoryItem): Group =>
  item.kind === 'player' || item.kind === 'special' ? 'players' : item.kind === 'manager' ? 'managers' : item.kind === 'clubItem' ? 'customisation' : 'consumables';

const notesOf = (item: InventoryItem) => [
  item.inSquads?.length ? 'In a squad' : null,
  item.equipped ? 'Equipped' : null,
  item.listed ? 'On the market' : null,
  item.untradeable ? 'Untradeable' : null,
].filter((note): note is string => !!note);

/** Fixed column widths, so headers and values keep their place across piles, kinds and pages. */
const WIDTHS: Record<string, number> = { card: 70, name: 300, rating: 90, pos: 100, kind: 200, type: 220, notes: 240 };

const OUTFIELD = ['PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY'];
const KEEPER = ['DIV', 'HAN', 'KIC', 'REF', 'SPD', 'POS'];
/** A player card's face stats with their labels, as the Cards page draws them. */
export const faceStats = (item: InventoryItem) =>
  (item.attributes ?? []).map((value, index) => ({ value, label: (item.position === 'GK' ? KEEPER : OUTFIELD)[index] ?? '' }));

const kindOf = (item: InventoryItem) => item.special ?? item.typeName ?? KIND[item.kind ?? ''] ?? item.kind;

export function InventoryTab({ detail }: { detail: PlayerDetail }) {
  const [pile, setPile] = useState<Pile>('club');
  const [group, setGroup] = useState<Group>('players');
  const [text, setText] = useState('');
  const [view, setView] = useState(savedView);
  const [open, setOpen] = useState<InventoryItem | null>(null);
  const [removing, setRemoving] = useState<InventoryItem | null>(null);
  const inventory = useInventory(detail.id, pile);
  const edit = useClubEdit(detail.id);
  const toast = useToast();
  const assets = useAssets().data;
  const counts = detail.inventory;
  const all = inventory.data?.piles?.[pile] ?? [];
  const inGroup = all.filter((item) => groupOf(item) === group);
  const query = text.trim().toLowerCase();
  const shown = inGroup.filter((item) => !query || [item.name, item.position, item.special, item.typeName, KIND[item.kind ?? ''], item.id, item.assetId]
    .some((value) => value != null && searchText(value).includes(searchText(query))));
  // Cards only for players, and only when every shown one can be drawn.
  const drawable = group === 'players' && !!assets && shown.length > 0 && shown.every((item) => item.art);
  const chooseView = (next: 'cards' | 'table') => { setView(next); try { localStorage.setItem(VIEW_KEY, next); } catch { /* private window */ } };
  const canRemove = (item: InventoryItem) => !!item.removable && supports(detail, 'removeItems');
  const canTake = (item: InventoryItem) => groupOf(item) !== 'players' && !item.equipped && !item.listed && supports(detail, 'takeItem');

  const columns: Column<InventoryItem>[] = group === 'players' ? [
    { key: 'card', header: '', cell: (item) => <FutCard art={item.art} assets={assets} size="xs" /> },
    { key: 'name', header: 'Card', sort: (item) => item.name, cell: (item) => <div><b>{item.name}</b><div className="mono">item {item.id}</div></div> },
    { key: 'rating', header: 'Rating', align: 'end', sort: (item) => item.rating, cell: (item) => item.rating || '–' },
    { key: 'pos', header: 'Position', sort: (item) => item.position, cell: (item) => item.position ?? '–' },
    { key: 'kind', header: 'Kind', sort: (item) => item.special ?? KIND[item.kind ?? ''], cell: (item) => (item.special ? <Badge tone="coin">{item.special}</Badge> : KIND[item.kind ?? ''] ?? item.kind) },
    { key: 'notes', header: 'Notes', cell: (item) => <span className="dim">{notesOf(item).join(' · ')}</span> },
  ] : [
    { key: 'name', header: 'Name', sort: (item) => item.name, cell: (item) => <div><b>{item.name}</b><div className="mono">item {item.id}</div></div> },
    { key: 'type', header: 'Type', sort: kindOf, cell: kindOf },
    { key: 'rating', header: 'Rating', align: 'end', sort: (item) => item.rating, cell: (item) => item.rating || '–' },
    { key: 'notes', header: 'Notes', cell: (item) => <span className="dim">{notesOf(item).join(' · ')}</span> },
  ];

  const remove = (item: InventoryItem) => new Promise<void>((resolve, reject) => {
    edit.mutate(groupOf(item) === 'players' ? { op: 'removeItems', items: [item.id] } : { op: 'takeItem', itemId: item.id }, {
      onSuccess: () => { toast(`Removed ${item.name ?? `item ${item.id}`} from ${detail.name ?? 'the club'}.`); setOpen(null); resolve(); },
      onError: (caught) => { if (refusedUnchanged(caught)) reject(caught); else { toast(caught.message, 'error'); setOpen(null); resolve(); } },
    });
  });

  return (
    <Section title="Inventory" text="Items held in the club, New items and Transfer list. Player cards and unequipped non-player items can be removed from the club.">
      <Toolbar>
        <Chips label="Pile" value={pile} onChange={(next) => { setPile(next); setText(''); }} options={[
          { value: 'club', label: 'Club', count: counts?.club },
          { value: 'new', label: 'New items', count: counts?.new },
          { value: 'transfer', label: 'Transfer list', count: counts?.transfer },
        ]} />
        <Field label="Filter"><input type="search" placeholder="Name, position, type or id" value={text} onChange={(event) => setText(event.target.value)} /></Field>
        {assets && group === 'players' && <Chips label="Show as" value={view} onChange={chooseView} options={[{ value: 'cards', label: 'Cards' }, { value: 'table', label: 'Table' }]} />}
      </Toolbar>
      <Tabs label="Item kinds" value={group} onChange={(next) => { setGroup(next); setText(''); }}
        items={GROUPS.map((option) => ({ ...option, ...(inventory.data && { count: all.filter((item) => groupOf(item) === option.value).length }) }))}>
        {inventory.data?.truncated?.[pile] && <p className="notice">Only the first 2,000 items of this pile are listed.</p>}
        {inventory.isPending ? <Loading /> : inventory.isError ? <ErrorPanel what="This pile" error={inventory.error} onRetry={() => void inventory.refetch()} /> :
          !shown.length ? <EmptyState title={inGroup.length ? 'Nothing matches the filter' : 'None in this pile'} /> :
          view === 'cards' && drawable ? (
            <VirtualCardGrid items={shown} render={(item) => (
              <FutCard key={item.id} art={item.art} assets={assets} onClick={() => setOpen(item)}
                title={[`${item.name} ${item.rating ?? ''} ${item.position ?? ''}`.trim(), item.special ?? KIND[item.kind ?? ''], ...notesOf(item)].filter(Boolean).join('\n')} />
            )} />
          ) : (
            <DataTable fixedLayout columns={columns.map((column) => ({ ...column, width: WIDTHS[column.key] ?? 120 }))} rows={shown} rowKey={(item) => item.id} pageSize={100} initialSort={{ key: 'rating', direction: 'desc' }}
              onRowClick={setOpen} rowLabel={(item) => `Open ${item.name ?? `item ${item.id}`}`} />
          )}
      </Tabs>
      {!!inventory.data?.packs?.length && (
        <p className="dim mt">Unopened packs (My Packs): {inventory.data.packs.map((pack) => `${pack.count} × ${pack.name ?? `pack ${pack.packId}`}`).join(', ')}.</p>
      )}
      <Dialog open={!!open} onOpenChange={(next) => { if (!next) setOpen(null); }} title={open?.name ?? 'Item'} description={open ? `Item ${open.id}` : undefined}
        footer={open && (canRemove(open) || canTake(open)) ? <Button variant="danger" onClick={() => setRemoving(open)}>Remove from club</Button> : undefined}>
        {open && (
          <div className="item-detail">
            {open.art && <FutCard art={open.art} assets={assets} size="lg" stats={faceStats(open)} />}
            <dl className="facts">
              <div><dt>Rating</dt><dd>{open.rating || '–'}</dd></div>
              {groupOf(open) === 'players' && <div><dt>Position</dt><dd>{open.position ?? '–'}</dd></div>}
              <div><dt>Kind</dt><dd>{kindOf(open)}</dd></div>
              <div><dt>Card</dt><dd className="mono">{open.resourceId ?? '–'}</dd></div>
              <div><dt>Tradeable</dt><dd>{open.untradeable ? 'No' : 'Yes'}</dd></div>
              {groupOf(open) === 'players' && <div><dt>In a squad</dt><dd>{open.inSquads?.length ? 'Yes' : 'No'}</dd></div>}
              {groupOf(open) === 'customisation' && <div><dt>Equipped</dt><dd>{open.equipped ? 'Yes' : 'No'}</dd></div>}
              {open.listed && <div><dt>Transfer market</dt><dd>Listed: it can be removed once the listing ends.</dd></div>}
            </dl>
          </div>
        )}
      </Dialog>
      <ConfirmDialog open={!!removing} onOpenChange={(next) => { if (!next) setRemoving(null); }} variant="danger" confirmLabel="Remove item"
        title={`Remove ${removing?.name ?? 'this item'} from ${detail.name ?? 'the club'}?`}
        text={`The item is deleted and pays nothing.${removing?.inSquads?.length ? ' It leaves every squad it is in.' : ''}${detail.online ? ' The player is online: their game may still show the item until it reloads the club.' : ''}`}
        onConfirm={() => (removing ? remove(removing) : undefined)} />
    </Section>
  );
}
