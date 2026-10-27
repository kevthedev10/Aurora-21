import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { href } from '../../app/router';
import { Badge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { DataTable, type Column } from '../../ui/DataTable';
import { Field } from '../../ui/Field';
import { ago, num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section, Stat, Stats } from '../../ui/Layout';
import { searchText } from '../../ui/search';
import './operations-review.css';

interface MarketLogReply { hours?: number; totals?: { trades?: number; flagged?: number; coins?: number }; trades?: Trade[] }
interface Party { id: number; discordId?: string | null; personaId?: number | null; name?: string }
interface Trade {
  tradeId: number; soldAt?: string; sale?: string; listedForSeconds?: number; seller: Party; buyer: Party;
  card: { resourceId: number; name?: string; rating?: number; rareflag?: number };
  price?: number; sellerProceeds?: number; startingBid?: number; buyNow?: number; medianPrice?: number | null; referenceSales?: number; pairTrades?: number; flags?: string[];
}

const flagText: Record<string, { label: string; title: string }> = {
  overpriced: { label: 'Overpriced', title: 'At least 5x the card’s median sale price over the surrounding week' },
  underpriced: { label: 'Underpriced', title: 'At most a fifth of the card’s median sale price' },
  instantBuy: { label: 'Instant buy', title: 'Bought with Buy Now within 15 seconds of listing' },
  repeatPair: { label: 'Repeat pair', title: 'Three or more sales from this seller to this buyer in the period' },
  sameIp: { label: 'Same IP', title: 'Seller and buyer connected from the same address in the last 30 days' },
};
const periods = [{ hours: 24, label: 'Last 24 hours' }, { hours: 72, label: 'Last 3 days' }, { hours: 168, label: 'Last 7 days' }, { hours: 720, label: 'Last 30 days' }];
const seconds = (value?: number) => value == null ? '–' : value < 120 ? `${value}s` : value < 7200 ? `${Math.round(value / 60)}m` : `${Math.round(value / 3600)}h`;

function PartyLink({ party }: { party: Party }) {
  return party.personaId == null ? <span>{party.name}</span> : <a href={href('players', party.personaId)} title={party.discordId ?? ''}>{party.name}</a>;
}

export function MarketLogPage() {
  const [hours, setHours] = useState(24);
  const [flagged, setFlagged] = useState(true);
  const [search, setSearch] = useState('');
  const query = useQuery({
    queryKey: ['market-log', hours, flagged],
    queryFn: ({ signal }) => api<MarketLogReply>(`market-log?hours=${hours}&limit=2000${flagged ? '&flagged=1' : ''}`, { signal }), retry: false,
  });
  const trades = (query.data?.trades ?? []).filter(row => !search.trim()
    || searchText(`${row.seller.name} ${row.seller.discordId} ${row.buyer.name} ${row.buyer.discordId} ${row.card.name}`).includes(searchText(search.trim())));
  const columns: Column<Trade>[] = [
    { key: 'at', header: 'Sold', width: 95, sort: row => row.soldAt, cell: row => <span title={row.soldAt}>{ago(row.soldAt)}</span> },
    { key: 'card', header: 'Card', width: 170, sort: row => row.card.name, cell: row => <span title={`resource ${row.card.resourceId}`}>{row.card.name} <span className="dim">{row.card.rating}</span></span> },
    { key: 'seller', header: 'Seller', width: 150, sort: row => row.seller.name, cell: row => <PartyLink party={row.seller} /> },
    { key: 'buyer', header: 'Buyer', width: 150, sort: row => row.buyer.name, cell: row => <PartyLink party={row.buyer} /> },
    { key: 'price', header: 'Price', width: 100, sort: row => row.price, cell: row => <span className="mono">{num(row.price)}</span> },
    { key: 'median', header: 'Median', width: 100, sort: row => row.medianPrice, cell: row => <span className="mono" title={`${row.referenceSales ?? 0} other sales`}>{num(row.medianPrice)}</span> },
    { key: 'sale', header: 'How', width: 110, sort: row => row.sale, cell: row => <span title={`listed ${seconds(row.listedForSeconds)} before`}>{row.sale === 'buyNow' ? 'Buy Now' : row.sale === 'auction' ? 'Auction' : 'Before log'} <span className="dim">{seconds(row.listedForSeconds)}</span></span> },
    { key: 'flags', header: 'Flags', width: 230, sort: row => row.flags?.length, cell: row => <>{(row.flags ?? []).map(flag => <Badge key={flag} tone={flag === 'sameIp' ? 'red' : 'flare'} title={flagText[flag]?.title ?? flag}>{flagText[flag]?.label ?? flag}</Badge>)}</> },
  ];
  return <div className="operations-review">
    <PageHeader title="Market log" description="Every completed transfer market sale, with signs of coins being passed between players flagged."
      actions={<Button onClick={() => void query.refetch()} disabled={query.isFetching}>{query.isFetching ? 'Refreshing…' : 'Refresh'}</Button>} />
    {query.isError && <ErrorPanel what="The market log" error={query.error} onRetry={() => void query.refetch()} />}
    {query.isPending ? <Loading /> : !query.data ? null : <>
      <Stats>
        <Stat label="Sales" value={num(query.data.totals?.trades)} />
        <Stat label="Flagged" value={num(query.data.totals?.flagged)} />
        <Stat label="Coins traded" value={num(query.data.totals?.coins)} />
      </Stats>
      <Section title="Filter"><div className="logs-tools">
        <Field label="Period"><select value={hours} onChange={event => setHours(Number(event.target.value))}>{periods.map(period => <option key={period.hours} value={period.hours}>{period.label}</option>)}</select></Field>
        <Field label="Show"><select value={flagged ? 'flagged' : 'all'} onChange={event => setFlagged(event.target.value === 'flagged')}><option value="flagged">Flagged sales</option><option value="all">All sales</option></select></Field>
        <Field label="Find"><input type="search" placeholder="Player, Discord ID or card…" value={search} onChange={event => setSearch(event.target.value)} /></Field>
      </div></Section>
      <Section title={flagged ? 'Flagged sales' : 'Sales'}>
        <DataTable key={`${hours}:${flagged}:${search}`} rows={trades} pageSize={25} fixedLayout rowKey={row => row.tradeId} columns={columns} empty={<EmptyState title={search ? 'No matching sales' : flagged ? 'No flagged sales in this period' : 'No sales in this period'} />} />
      </Section>
    </>}
  </div>;
}
