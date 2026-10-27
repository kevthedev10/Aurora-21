// Cards' non-player tabs (GET cards/items?tab=): consumables, club customisation and managers. A plain table with no
// card art; nothing here is edited, a row only opens Grant to a club (grantItem). Filters live in the address
// (#/cards/<tab>?q=&type=&league=), so Back and a shared link keep them.
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState, type ReactNode } from 'react';
import { api } from '../../api/client';
import type { ItemRow, ItemsReply } from '../../api/types';
import { href } from '../../app/router';
import { Button } from '../../ui/Button';
import { DataTable, type Column } from '../../ui/DataTable';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Toolbar } from '../../ui/Layout';
import { searchText } from '../../ui/search';
import { GrantDialog } from './GrantDialog';
import { SearchFilter } from './SearchFilter';

/** The Cards page's header text, the same on every tab so switching tabs never moves the tab menu. */
export const CARDS_DESCRIPTION = 'Every card and item this server can grant. Player cards can be edited or used as the base of a new version; consumables, club customisation and managers are granted only.';

export const ITEM_TABS = ['consumables', 'customisation', 'managers'] as const;
export type ItemTab = (typeof ITEM_TABS)[number];
export const isItemTab = (value: string | undefined): value is ItemTab => (ITEM_TABS as readonly string[]).includes(value ?? '');

const DESCRIPTIONS: Record<ItemTab, string> = {
  consumables: 'Chemistry styles, position changes and manager league cards, as packs give them. Open one to grant it to a club.',
  customisation: 'Kits, badges, balls, stadiums and My Stadium items. A club holds one of each; My Stadium items are always untradeable.',
  managers: 'Every manager packs can give. Open one to grant it to a club.',
};

const rarity = (item: ItemRow) => (item.rareflag ? 'Rare' : 'Common');
/** Fixed column widths, so headers and values keep their place across pages, filters and tabs. */
const WIDTHS: Record<string, number> = { name: 320, type: 200, team: 220, rating: 90, rarity: 110, resource: 130, nation: 170, league: 220 };

/** The rows an address shows: the search matches the name, type, team, league, nation, key or resource id. */
export function filterItems(items: ItemRow[], params: URLSearchParams): ItemRow[] {
  const text = searchText(params.get('q')?.trim() ?? '');
  const type = params.get('type');
  const league = params.get('league');
  return items.filter((item) => (!type || item.type === type) && (!league || item.league === league)
    && (!text || [item.name, item.typeName, item.team, item.league, item.nation, item.nameKey, item.key, item.resourceId]
      .some((value) => value != null && searchText(value).includes(text))));
}

export function ItemsBrowser({ tab, params, wrap }: { tab: ItemTab; params: URLSearchParams; wrap: (body: ReactNode) => ReactNode }) {
  const baseHref = href('cards', tab);
  const reply = useQuery({ queryKey: ['card-items', tab], queryFn: ({ signal }) => api<ItemsReply>(`cards/items?tab=${tab}`, { signal }) });
  const [text, setText] = useState(params.get('q') ?? '');
  const [granting, setGranting] = useState<ItemRow | null>(null);
  const queryText = params.get('q') ?? '';

  const set = (changes: Record<string, string | null>) => {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(changes)) { if (value) next.set(key, value); else next.delete(key); }
    const qs = next.toString();
    window.location.replace(`${baseHref}${qs ? `?${qs}` : ''}`);
  };
  useEffect(() => setText(queryText), [queryText]);
  useEffect(() => {
    const timer = window.setTimeout(() => { if (text.trim() !== queryText) set({ q: text.trim() || null }); }, 300);
    return () => window.clearTimeout(timer);
    // Re-run when the typed text or the address changes; set() reads the current params.
  }, [text, params]);

  const items = reply.data?.items ?? [];
  const shown = filterItems(items, params);
  const types = reply.data?.types ?? [];
  const leagues = [...new Set(items.map((item) => item.league).filter((league): league is string => !!league))].sort((a, b) => a.localeCompare(b));

  const name: Column<ItemRow> = { key: 'name', header: 'Name', sort: (item) => item.name,
    cell: (item) => <div><b>{item.name}</b>{item.nameKey && <div className="mono dim">{item.nameKey}</div>}</div> };
  const type: Column<ItemRow> = { key: 'type', header: 'Type', sort: (item) => item.typeName, cell: (item) => item.typeName };
  const rating: Column<ItemRow> = { key: 'rating', header: 'Rating', align: 'center', sort: (item) => item.rating, cell: (item) => item.rating || '–' };
  const rare: Column<ItemRow> = { key: 'rarity', header: 'Rarity', sort: rarity, cell: rarity };
  const resource: Column<ItemRow> = { key: 'resource', header: 'Resource id', align: 'end', sort: (item) => item.resourceId, cell: (item) => <span className="mono">{item.resourceId}</span> };
  const columns: Column<ItemRow>[] = tab === 'managers'
    ? [name, rating, { key: 'nation', header: 'Nation', sort: (item) => item.nation, cell: (item) => item.nation ?? '–' },
      { key: 'league', header: 'League', sort: (item) => item.league, cell: (item) => item.league ?? '–' }, rare, resource]
    : tab === 'customisation'
      ? [name, type, { key: 'team', header: 'Team', sort: (item) => item.team, cell: (item) => item.team ?? '–' }, rare, resource]
      : [name, type, rating, rare, resource];

  const body = <>
    <p className="dim tab-intro">{DESCRIPTIONS[tab]}{reply.data ? ` ${num(reply.data.total)} in all.` : ''}</p>
    <Toolbar>
      <Field label="Search"><input type="search" placeholder="Name, type or resource id" value={text} onChange={(event) => setText(event.target.value)} /></Field>
      {types.length > 1 && <SearchFilter label="Type" value={params.get('type') ?? ''} options={types.map((option) => ({ value: option.value, label: `${option.label} (${num(option.count)})` }))}
        onChange={(option) => set({ type: option?.value ?? null })} />}
      {tab === 'managers' && <SearchFilter label="League" value={params.get('league') ?? ''} options={leagues.map((league) => ({ value: league, label: league }))}
        onChange={(option) => set({ league: option?.value ?? null })} />}
      <Button disabled={!params.toString() && !text} onClick={() => { setText(''); window.location.replace(baseHref); }}>Clear filters</Button>
    </Toolbar>
    {reply.isPending ? <Loading /> : reply.isError ? <ErrorPanel what="These items" error={reply.error} onRetry={() => void reply.refetch()} /> : (
      <DataTable fixedLayout columns={columns.map((column) => ({ ...column, width: WIDTHS[column.key] ?? 120 }))} rows={shown} rowKey={(item) => item.key} pageSize={100}
        initialSort={tab === 'managers' || tab === 'consumables' ? { key: 'rating', direction: 'desc' } : { key: 'type', direction: 'asc' }}
        onRowClick={setGranting} rowLabel={(item) => `Grant ${item.name}`}
        empty={<EmptyState title={items.length ? 'Nothing matches the filters' : 'No items'} text={items.length ? 'Change the search or the filters.' : undefined} />} />
    )}
    {granting && <GrantDialog title={`Grant ${granting.name}`} itemName={granting.name} max={granting.countMax}
      description={granting.forcedUntradeable ? 'The My Stadium item lands in the club’s New items at once. A club can hold each one once.'
        : granting.countMax > 1 ? 'The copies land in the club’s New items at once.' : 'The item lands in the club’s New items at once. A club can hold each one once.'}
      forced={granting.forcedUntradeable} forcedReason="My Stadium items are always untradeable." onClose={() => setGranting(null)}
      edit={(count, untradeable) => ({ op: 'grantItem', item: granting.key, count, untradeable })} />}
  </>;

  return (
    <>
      <PageHeader title="Cards" description={CARDS_DESCRIPTION} />
      {wrap(body)}
    </>
  );
}
