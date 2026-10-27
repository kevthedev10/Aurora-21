// Cards (plan 5.2): every card this server can grant, filtered on the server (GET cards) and paged. Filters live in the
// address (#/cards?q=&pos=&type=&min=&max=&page=), so Back and a shared link keep them. The Consumables, Club
// customisation and Managers tabs (#/cards/<tab>) list the other grantable items (ItemsBrowser).
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState, type ReactNode } from 'react';
import { useAssets } from '../../api/assets';
import { api } from '../../api/client';
import { useChangeset } from '../../api/content';
import type { CardRow, CardsReply, SpecialsReply } from '../../api/types';
import { href } from '../../app/router';
import { Badge, StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { DataTable, type Column } from '../../ui/DataTable';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { CardGrid, FutCard } from '../../ui/FutCard';
import { EmptyState, ErrorPanel, Loading, PageHeader } from '../../ui/Layout';
import { Tabs } from '../../ui/Tabs';
import { CardDetail } from './CardDetail';
import { CardEditor } from './CardEditor';
import { CARDS_DESCRIPTION, isItemTab, ItemsBrowser, type ItemTab } from './ItemsBrowser';
import { SearchFilter, type FilterOption } from './SearchFilter';
import { frameFor } from '../../ui/FutCard';

export const POSITION_GROUPS = [
  { value: 'ST,CF', label: 'ST / CF' }, { value: 'RB,RWB', label: 'RB / RWB' },
  { value: 'LB,LWB', label: 'LB / LWB' }, { value: 'CDM,CM,CAM', label: 'CDM / CM / CAM' },
  { value: 'LM,LW,LF', label: 'LM / LW / LF' }, { value: 'RM,RW,RF', label: 'RM / RW / RF' },
  { value: 'GK', label: 'GK' }, { value: 'CB', label: 'CB' },
  { value: 'ST,LW,RW,LF,RF,CF', label: 'Attackers' },
  { value: 'LM,RM,CM,CDM,CAM', label: 'Midfielders' },
  { value: 'CB,RB,RWB,LWB,LB,GK', label: 'Defenders (including GK)' },
];
interface CardFiltersReply {
  nations: { id: number; name: string }[];
  leagues: { id: number; name: string }[];
  clubs: { id: number; name: string; leagueId: number; leagueName: string }[];
}

const PAGE = 64;
const SORT_KEYS = ['name', 'rating', 'pos', 'skill', 'weak', 'work', 'PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY', 'type', 'nation', 'packs'];
const COLUMN_WIDTHS: Record<string, number> = { card: 80, name: 230, rating: 80, pos: 95, skill: 70, weak: 70, work: 85, type: 185, nation: 135, packs: 180 };
const VIEW_KEY = 'aurora21.panel.cards.view';
const savedView = (): 'cards' | 'table' => { try { return localStorage.getItem(VIEW_KEY) === 'table' ? 'table' : 'cards'; } catch { return 'cards'; } };
export function bucketPercentage(bucket: { weight: number; count: number; enabled: boolean }, buckets: { weight: number; count: number; enabled: boolean }[]) {
  const totalWeight = buckets.filter(b => b.enabled && b.count > 0).reduce((sum, b) => sum + b.weight, 0);
  return bucket.enabled && bucket.count > 0 && totalWeight > 0 ? bucket.weight / totalWeight * 100 : 0;
}
export const cardPage = (params: URLSearchParams) => {
  const raw = Number(params.get('page') ?? 1);
  return Number.isSafeInteger(raw) && raw > 0 ? Math.min(raw, Math.floor(1_000_000 / PAGE) + 1) : 1;
};

const ratingProblem = (params: URLSearchParams) => {
  for (const bound of ['min', 'max']) {
    const raw = params.get(bound);
    if (raw && (!/^\d{1,2}$/.test(raw) || Number(raw) > 99)) return 'Ratings must be whole numbers from 0 to 99.';
  }
  return Number(params.get('min') || 0) > Number(params.get('max') || 99) ? 'The minimum rating cannot exceed the maximum.' : null;
};

/** The server query for a filter set: type is "", "base", "any", "rare", "common" or a special rareflag. */
export function cardQuery(params: URLSearchParams): URLSearchParams {
  const query = new URLSearchParams({ limit: String(PAGE), offset: String((cardPage(params) - 1) * PAGE) });
  const text = params.get('q')?.trim();
  if (text) query.set('q', text);
  const pos = params.get('pos');
  if (pos) query.set('pos', pos);
  for (const key of ['nation', 'league', 'team']) {
    const value = params.get(key);
    if (value) query.set(key, value);
  }
  const type = params.get('type') ?? '';
  if (type === 'rare') query.set('rare', '1');
  else if (type === 'common') query.set('rare', '0');
  else if (type) query.set('special', type);
  for (const bound of ['min', 'max'] as const) {
    const value = params.get(bound);
    if (value && /^\d{1,2}$/.test(value)) query.set(bound, value);
  }
  if (SORT_KEYS.includes(params.get('sort') ?? '')) {
    query.set('sort', params.get('sort')!);
    query.set('direction', params.get('direction') === 'asc' ? 'asc' : 'desc');
  }
  return query;
}

export function CardsPage({ rest, params }: { rest: string[]; params: URLSearchParams }) {
  if (rest[0] && (rest[1] === 'edit' || rest[1] === 'new')) {
    const resourceId = Number(rest[0].split(':')[0]);
    const draft = params.get('draft');
    const draftId = draft && /^\d+$/.test(draft) && Number.isSafeInteger(Number(draft)) && Number(draft) > 0 ? Number(draft) : undefined;
    if (draft && !draftId) return <EmptyState title="Invalid staged card id" text="Open the new card from its saved editor address." />;
    return Number.isSafeInteger(resourceId) && resourceId > 0
      ? <CardEditor key={`${resourceId}-${rest[1]}-${draftId ?? ''}`} resourceId={resourceId} creating={rest[1] === 'new'} {...(draftId && { draftResourceId: draftId })} />
      : <EmptyState title="Invalid card id" text="Open a card from the list first." />;
  }
  if (isItemTab(rest[0])) return <ItemsBrowser key={rest[0]} tab={rest[0]} params={params} wrap={kindTabs(rest[0])} />;
  if (rest[0]) return <CardDetail cardKey={rest[0]} />;
  return <CardBrowser params={params} wrap={kindTabs('players')} />;
}

const KINDS: { value: 'players' | ItemTab; label: string }[] = [
  { value: 'players', label: 'Players' }, { value: 'consumables', label: 'Consumables' },
  { value: 'customisation', label: 'Club customisation' }, { value: 'managers', label: 'Managers' },
];

/** The Players, Consumables, Club customisation and Managers tabs around one tab's filters and list. */
const kindTabs = (active: 'players' | ItemTab) => (body: ReactNode) => (
  <Tabs label="Card kinds" value={active} items={KINDS} onChange={(next) => { window.location.hash = next === 'players' ? href('cards') : href('cards', next); }}>
    {body}
  </Tabs>
);

export function CardBrowser({ params, promo, actions, onEditBucket, wrap = (body) => body }: { params: URLSearchParams; promo?: { code: string; name: string }; actions?: ReactNode; onEditBucket?: (code: string) => void;
  /** Wraps everything below the header (the Cards page's kind tabs). */
  wrap?: (body: ReactNode) => ReactNode }) {
  const baseHref = promo ? href('promos', promo.code) : href('cards');
  const assets = useAssets().data;
  const pending = useChangeset();
  const cardChanges = pending.data?.changeset?.changes?.filter((change) => change.area === 'player_card') ?? [];
  const [view, setView] = useState(savedView);
  const [text, setText] = useState(params.get('q') ?? '');
  const queryText = params.get('q') ?? '';
  const problem = ratingProblem(params);
  const query = cardQuery(params);
  if (promo) {
    query.set('promo', promo.code);
    query.set('group', 'buckets');
    for (const [key, value] of params) if (key.startsWith('bucketPage.')) query.set(key, value);
  }
  const cards = useQuery({ queryKey: ['cards', query.toString()], queryFn: ({ signal }) => api<CardsReply>(`cards?${query}`, { signal }), placeholderData: (previous) => previous, enabled: !problem });
  const specials = useQuery({ queryKey: ['cards-specials'], queryFn: () => api<SpecialsReply>('cards/specials') });
  const filters = useQuery({ queryKey: ['cards-filters'], queryFn: ({ signal }) => api<CardFiltersReply>('cards/filters', { signal }) });
  const page = cardPage(params);

  // Changing a filter rewrites the address (replace: filters do not fill the Back history) and starts at page 1.
  const set = (changes: Record<string, string | null>) => {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(changes)) { if (value) next.set(key, value); else next.delete(key); }
    if (!Object.keys(changes).some(key => key === 'page' || key.startsWith('bucketPage.'))) {
      next.delete('page');
      for (const key of [...next.keys()]) if (key.startsWith('bucketPage.')) next.delete(key);
    }
    const qs = next.toString();
    window.location.replace(`${baseHref}${qs ? `?${qs}` : ''}`);
  };
  useEffect(() => setText(queryText), [queryText]);
  useEffect(() => {
    const timer = window.setTimeout(() => { if (text.trim() !== (params.get('q') ?? '')) set({ q: text.trim() || null }); }, 300);
    return () => window.clearTimeout(timer);
    // Re-run when the typed text or the address changes; set() reads the current params.
  }, [text, params]);
  const chooseView = (next: 'cards' | 'table') => { setView(next); try { localStorage.setItem(VIEW_KEY, next); } catch { /* private window */ } };
  const open = (card: CardRow) => { window.location.hash = href('cards', card.cardKey); };

  const reply = cards.data;
  const list = reply?.cards ?? [];
  const total = reply?.total ?? 0;
  const frame = (flag: number) => {
    const file = frameFor(flag, 85, assets?.frames ?? []);
    return file ? `${assets?.baseUrl ?? '/panel/assets/'}cards/hd/${file}` : undefined;
  };
  const types: FilterOption[] = [
    { value: 'base', label: 'Gold, silver and bronze', image: frame(1) },
    { value: 'rare', label: 'Rare', image: frame(1) }, { value: 'common', label: 'Common', image: frame(0) },
    { value: 'any', label: 'Any special' },
    ...(specials.data?.types ?? []).map(type => ({ value: String(type.flag), label: `${type.name ?? `Type ${type.flag}`} (${num(type.available)})`, image: frame(type.flag) })),
  ];
  const positions = [...POSITION_GROUPS, ...(reply?.positions ?? []).filter(pos => !POSITION_GROUPS.some(group => group.value === pos)).map(pos => ({ value: pos, label: pos }))];
  const nationOptions = (filters.data?.nations ?? []).map(nation => ({ value: String(nation.id), label: nation.name }));
  const leagueOptions = (filters.data?.leagues ?? []).map(league => ({ value: String(league.id), label: league.name }));
  const clubOptions = (filters.data?.clubs ?? [])
    .map(club => ({ value: `${club.id}:${club.leagueId}`, label: `${club.name} · ${club.leagueName}`, leagueId: club.leagueId }));
  const shownOffset = cards.isPlaceholderData ? reply?.offset ?? 0 : (page - 1) * PAGE;
  const columns: Column<CardRow>[] = [
    { key: 'card', header: '', align: 'center', cell: (card) => <FutCard art={card.art} assets={assets} size="thumbnail" /> },
    { key: 'name', header: 'Name', cell: (card) => <div title={card.name ?? undefined}><b>{card.name}</b>{cardChanges.some((change) => Number(change.key.resourceId) === card.resourceId) && <StagedBadge>Pending edit</StagedBadge>}</div> },
    { key: 'rating', header: 'Rating', align: 'center', cell: (card) => card.rating },
    { key: 'pos', header: 'Position', align: 'center', cell: (card) => card.position },
    { key: 'skill', header: 'SKI', align: 'center', cell: (card) => card.skillMoves ? `${card.skillMoves} ★` : '–' },
    { key: 'weak', header: 'WF', align: 'center', cell: (card) => card.weakFoot ? `${card.weakFoot} ★` : '–' },
    { key: 'work', header: 'WR', align: 'center', cell: (card) => <span title="Attacking / Defending">{card.workRateAttack?.charAt(0).toUpperCase() || '–'} / {card.workRateDefense?.charAt(0).toUpperCase() || '–'}</span> },
    ...['PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY'].map((label, index): Column<CardRow> => ({ key: label, header: label, align: 'center',
      cell: card => <span title={card.position === 'GK' ? ['DIV', 'HAN', 'KIC', 'REF', 'SPD', 'POS'][index] : label}>
        {card.position === 'GK' && <small className="dim">{['DIV', 'HAN', 'KIC', 'REF', 'SPD', 'POS'][index]} </small>}{card.attributes?.[index] ?? '–'}</span> })),
    { key: 'type', header: 'Type', cell: (card) => (card.special ? <Badge tone="coin">{card.special}</Badge> : card.rareflag === 1 ? 'Rare' : 'Common') },
    { key: 'nation', header: 'Nation', cell: (card) => card.nation || '–' },
    { key: 'packs', header: 'Pack restriction', cell: (card) => (card.inPacks === false ? <Badge>Grant only</Badge> : 'Pack sources apply') },
  ];

  // The same cards/table renderer is used for the flat catalogue and every promo bucket.
  const renderCards = (rows: CardRow[]) => view === 'cards' && assets ? (
    <CardGrid large>{rows.map(card => <FutCard key={card.cardKey} art={card.art} assets={assets} size="catalogue"
      stats={(card.attributes ?? []).map((value, index) => ({ value, label: (card.position === 'GK' ? reply?.gkAttrs ?? ['DIV', 'HAN', 'KIC', 'REF', 'SPD', 'POS'] : reply?.attrs ?? ['PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY'])[index] ?? '' }))}
      onClick={() => open(card)} title={`${card.name} ${card.rating} ${card.special ?? ''}`.trim()} />)}</CardGrid>
  ) : <DataTable fixedLayout columns={columns.map(column => ({ ...column, width: COLUMN_WIDTHS[column.key] ?? 70, sortable: column.key !== 'card' }))}
    externalSort={{ key: query.get('sort') ?? 'rating', direction: query.get('direction') === 'asc' ? 'asc' : 'desc' }}
    onSortChange={sort => set({ sort: sort.key, direction: sort.direction })}
    rows={rows} rowKey={card => card.cardKey} onRowClick={open} rowLabel={card => `Open ${card.name} ${card.rating}`} />;

  const renderPager = (key: string, current: number, count: number, start: number, length: number) => <div className="pager">
    <span className="dim">{num(length ? start + 1 : 0)}–{num(Math.min(count, start + length))} of {num(count)}</span>
    <span className="row-actions">
      <Button size="sm" disabled={current <= 1 || cards.isPlaceholderData} onClick={() => set({ [key]: String(current - 1) })}>Previous</Button>
      <Button size="sm" disabled={current >= Math.max(1, Math.ceil(count / PAGE)) || cards.isPlaceholderData} onClick={() => set({ [key]: String(current + 1) })}>Next</Button>
    </span>
  </div>;

  return (
    <>
      {promo && <a className="promo-back" href={href('promos')}>← All promos</a>}
      <PageHeader title={promo?.name ?? 'Cards'} actions={actions} description={promo ? `${num(reply?.poolSize ?? 0)} cards, organised by bucket. Percentages show each populated bucket’s share within this promo, before pack overrides. Search filters do not change these percentages.` : CARDS_DESCRIPTION} />
      {wrap(<>
      {!promo && <p className="dim tab-intro">Every grantable player card, specials included{reply?.poolSize ? ` (${num(reply.poolSize)} in all)` : ''}. Open one to edit it or create another version. Pack availability depends on pack sources and promo events.</p>}
      {cardChanges.length > 0 && <p className="notice">{cardChanges.length} pending card change(s). The catalogue shows published values. <a href={href('changes')}>Review pending cards</a></p>}
      <div className="catalogue-toolbar">
        <Field label="Search"><input type="search" placeholder="Name, card id or card key" value={text} onChange={(event) => setText(event.target.value)} /></Field>
        <SearchFilter label="Position" value={params.get('pos') ?? ''} options={positions} searchable={false} onChange={option => set({ pos: option?.value ?? null })} />
        <SearchFilter label="Type" value={params.get('type') ?? ''} options={types} onChange={option => set({ type: option?.value ?? null })} />
        <SearchFilter label="Nation" value={params.get('nation') ?? ''} options={nationOptions} onChange={option => set({ nation: option?.value ?? null })} />
        <SearchFilter label="League" value={params.get('league') ?? ''} options={leagueOptions} onChange={option => set({ league: option?.value ?? null, team: null })} />
        <SearchFilter label="Club" className="club-filter" value={params.get('team') ? `${params.get('team')}:${params.get('league') ?? ''}` : ''} options={clubOptions}
          onChange={option => set(option ? { team: option.value.split(':')[0]!, league: String(option.leagueId) } : { team: null })} />
        <Field label="Rating from"><input type="number" min={0} max={99} value={params.get('min') ?? ''} onChange={(event) => set({ min: event.target.value || null })} /></Field>
        <Field label="to"><input type="number" min={0} max={99} value={params.get('max') ?? ''} onChange={(event) => set({ max: event.target.value || null })} /></Field>
        <div className="catalogue-actions">
        <div className="catalogue-view-switch" role="group" aria-label="Show as">
          <button type="button" aria-pressed={view === 'cards' && !!assets} disabled={!assets} onClick={() => chooseView('cards')}><CatalogueIcon kind="cards" />Cards</button>
          <button type="button" aria-pressed={view === 'table' || !assets} onClick={() => chooseView('table')}><CatalogueIcon kind="table" />Table</button>
        </div>
        <Button className="clear-card-filters" disabled={!params.toString() && !text} onClick={() => { setText(''); window.location.replace(baseHref); }}><CatalogueIcon kind="clear" />Clear filters</Button>
        </div>
      </div>
      {specials.isError && <ErrorPanel what="The card types" error={specials.error} onRetry={() => void specials.refetch()} />}
      {filters.isError && <ErrorPanel what="The nation, league and club filters" error={filters.error} onRetry={() => void filters.refetch()} />}
      {problem ? <p className="error-box" role="alert">{problem}</p> : cards.isPending ? <Loading /> : cards.isError ? <ErrorPanel what="The cards" error={cards.error} onRetry={() => void cards.refetch()} /> :
        reply?.buckets ? <div className="catalogue-results promo-bucket-list" aria-busy={cards.isFetching}>
          {cards.isPlaceholderData && <p className="catalogue-update-status dim" role="status">Updating results…</p>}
          {reply.buckets.map(bucket => <section className="promo-bucket-section" key={bucket.code} aria-label={bucket.name}>
            <header className="promo-bucket-header"><div><h2>{bucket.name}</h2><span className="dim">{num(bucket.count)} cards{bucket.total !== bucket.count ? ` · ${num(bucket.total)} match filters` : ''}</span></div>
              <div className="row-actions">{!bucket.enabled && <Badge>Disabled</Badge>}<Badge tone="coin">Weight {bucket.weight.toLocaleString('en-GB', { maximumFractionDigits: 6 })}</Badge><Badge title="Share within this promo before pack overrides. Empty and disabled buckets have no draw chance.">{bucketPercentage(bucket, reply.buckets!).toLocaleString('en-GB', { maximumFractionDigits: 6 })}%</Badge>{onEditBucket && <Button onClick={() => onEditBucket(bucket.code)} aria-label={`Edit bucket ${bucket.name}`}>Edit bucket</Button>}</div>
            </header>
            {bucket.cards.length ? renderCards(bucket.cards) : <EmptyState title={bucket.count === 0 ? 'Empty bucket' : bucket.total > 0 ? 'This page is empty' : 'No matching cards'} text={bucket.count === 0 ? 'Cards can be moved here later.' : 'Change the filters or return to the first page.'}
              action={bucket.offset > 0 ? <Button onClick={() => set({ [`bucketPage.${bucket.code}`]: null })}>Go to first page</Button> : undefined} />}
            {bucket.total > 0 && renderPager(`bucketPage.${bucket.code}`, Math.floor(bucket.offset / PAGE) + 1, bucket.total, bucket.offset, bucket.cards.length)}
          </section>)}
          {!reply.buckets.length && <EmptyState title="No buckets yet" text="Create a bucket to organise this promo." />}
        </div> : !list.length ? <EmptyState title={page > 1 && total > 0 ? 'This page is empty' : 'No card matches'} text="Change the search or the filters."
          action={page > 1 ? <Button onClick={() => set({ page: null })}>Go to first page</Button> : undefined} /> : (
          <div className="catalogue-results" aria-busy={cards.isFetching}>
            {cards.isPlaceholderData && <p className="catalogue-update-status dim" role="status">Updating results…</p>}
            {renderCards(list)}
            {renderPager('page', page, total, shownOffset, list.length)}
          </div>
        )}
      </>)}
    </>
  );
}

function CatalogueIcon({ kind }: { kind: 'cards' | 'table' | 'clear' }) {
  return <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    {kind === 'cards' ? <><rect x="3" y="4" width="7" height="16" rx="2" /><rect x="14" y="4" width="7" height="16" rx="2" /></> :
      kind === 'table' ? <><rect x="3" y="4" width="18" height="16" rx="2" /><path d="M3 9h18M3 14h18M9 4v16" /></> : <><path d="M3 4h18l-7 8v5l-4 3v-8zM17 17l4 4m0-4-4 4" /></>}
  </svg>;
}
