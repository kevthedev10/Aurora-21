import { api } from '../../api/client';
import type { PackCatalogReply } from '../../api/types';
import { AvailabilityDialog, ScheduledSection } from '../schedule/AvailabilitySections';
// Independent store offers share a pack definition, with price and tradeability set in each lineup.
import { useEffect, useMemo, useState } from 'react';
import { useCatalogue, usePackActions } from '../../api/packs';
import type { ListingEntry } from '../../api/types';
import { StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { Chips } from '../../ui/Chips';
import { num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, Section } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';
import './listing.css';

const same = (a: ListingEntry[], b: ListingEntry[]) => JSON.stringify([...a].sort((x, y) => x.packId - y.packId)) === JSON.stringify([...b].sort((x, y) => x.packId - y.packId));
const validPrice = (price: number | undefined) => Number.isSafeInteger(price) && Number(price) > 0 && Number(price) <= 99999999;
function TradeChoices({ label, tokens, value, onChange }: { label: string; tokens: boolean; value: boolean; onChange: (value: boolean) => void }) {
  return <Chips label={label} value={value ? 'untradeable' : 'tradeable'} onChange={next => onChange(next === 'untradeable')} options={[
    { value: 'tradeable', label: <>Tradeable{!tokens && <span className="listing-trade-default">(Default)</span>}</> },
    { value: 'untradeable', label: <>Untradeable{tokens && <span className="listing-trade-default">(Default)</span>}</> },
  ]} />;
}

export function LineupTab({ section = 'standard' }: { section?: 'standard' | 'tokens' }) {
  const tokens = section === 'tokens';
  const title = tokens ? 'Token Store Lineup' : 'Store Lineup';
  const currency = tokens ? 'Aurora Tokens' : 'coins';
  const toast = useToast();
  const catalogue = useCatalogue();
  const actions = usePackActions();
  const action = tokens ? actions.tokenLineup : actions.lineup;
  const published = tokens ? catalogue.data?.tokenListing : catalogue.data?.listing;
  const [listing, setListing] = useState<ListingEntry[]>([]);
  const [adding, setAdding] = useState(false);
  // Reset the draft only when the staged or published contents change.
  const publishedKey = published ? JSON.stringify(published) : '';
  useEffect(() => { if (publishedKey) setListing(JSON.parse(publishedKey) as ListingEntry[]); }, [publishedKey]);
  const packs = useMemo(() => new Map((catalogue.data?.packs ?? []).map(pack => [pack.id, pack])), [catalogue.data]);
  if (catalogue.isPending) return <Loading />;
  if (catalogue.isError || !catalogue.data) return <ErrorPanel what="The pack catalogue" error={catalogue.error} onRetry={() => void catalogue.refetch()} />;
  const data = catalogue.data;
  const current = tokens ? data.tokenListing : data.listing;
  const dirty = !same(listing, current);
  const available = data.packs.filter(pack => !pack.grantOnly && !pack.locked && !listing.some(entry => entry.packId === pack.id && entry.enabled) && pack.pending !== 'Delete');
  const update = (packId: number, change: Partial<ListingEntry>) => setListing(current => current.map(entry => entry.packId === packId ? { ...entry, ...change } : entry));
  const stage = () => action.mutate({ revision: data.revision, listing }, {
    onSuccess: () => toast(`${title} saved.`, 'done'),
    onError: error => toast(error.message, 'error'),
  });
  const sorted = listing.filter(entry => entry.enabled).sort((a, b) => a.packId - b.packId);
  const pending = tokens ? data.tokenLineupPending : data.lineupPending;
  return (
    <><Section title="Live" text={`Packs sold for ${currency}. Set the price and tradeability for each live listing. New offers require start and end dates. The same pack can appear in both stores with different offers. ${tokens ? 'Untradeable' : 'Tradeable'} is the default for new listings.`}
      actions={<>
        {pending && <StagedBadge>A listing is staged: changes here update it</StagedBadge>}
        <Button size="sm" onClick={() => setAdding(true)} disabled={!available.length} aria-label={`Add a pack to the ${tokens ? 'token ' : ''}store listing`}>+ Add pack</Button>
      </>}>
      {!sorted.length ? <EmptyState title="No packs listed" text={`Add a pack to sell it for ${currency}.`} /> : (
        <ul className="listing-rows">
          {sorted.map(entry => {
            const pack = packs.get(entry.packId);
            const name = pack?.description ?? `Pack ${entry.packId}`;
            const locked = !tokens && pack?.locked;
            return (
              <li key={entry.packId} className="listing-row">
                <div className="listing-name">
                  <strong>{name}</strong> <span className="dim">#{entry.packId}</span>
                  <span className="dim listing-detail">{num(pack?.items)} items
                    {pack && Number(pack.config.community_limit) > 0 ? ` · ${num(Number(pack.config.community_limit))} community purchases`
                      : pack && Number(pack.config.purchase_limit) > 0 ? ` · ${num(Number(pack.config.purchase_limit))} purchases per club` : ' · unlimited purchases'}
                    {pack?.pending ? ` · staged ${pack.pending.toLowerCase()}` : ''}{pack?.unavailableReason ? ` · cannot be switched on: ${pack.unavailableReason}` : ''}</span>
                </div>
                <label className="listing-price">
                  <span className="dim">{tokens ? 'Tokens' : 'Coins'}</span>
                  <input type="number" min="1" max="99999999" step="1" aria-label={`${name} ${tokens ? 'token' : 'coin'} price`} value={entry.price ?? 0}
                    aria-invalid={!validPrice(entry.price)} onChange={event => update(entry.packId, { price: Number(event.target.value) })} />
                </label>
                <span className="dim">Live</span>
                <div className="listing-trade">
                  <span className="dim">Tradeability</span>
                  <TradeChoices label={`${name} tradeability`} tokens={tokens} value={entry.untradeable ?? tokens}
                    onChange={untradeable => update(entry.packId, { untradeable })} />
                </div>
                <Button size="sm" variant="ghost" disabled={locked} aria-label={`Remove ${name} from the store listing`}
                  onClick={() => setListing(current => current.filter(candidate => candidate.packId !== entry.packId))}>Remove</Button>
              </li>
            );
          })}
        </ul>
      )}
      <div className="row-actions mt">
        <Button variant="staged" onClick={stage} disabled={!dirty || action.isPending || listing.some(entry => !validPrice(entry.price))}>Save listing</Button>
        <Button variant="ghost" onClick={() => setListing(current)} disabled={!dirty}>Reset</Button>
        <span className="dim">{num(sorted.length)} listed · {num(sorted.filter(entry => entry.enabled).length)} on sale</span>
      </div>
    </Section>
    <ScheduledSection kind={tokens ? 'token' : 'store'} options={data.packs.map(pack => ({ value: String(pack.id), label: pack.description ?? `Pack ${pack.id}` }))} />
    {adding && <AvailabilityDialog kind={tokens ? 'token' : 'store'} currency={tokens ? 'Aurora Tokens' : 'Coins'}
      options={available.map(pack => ({ value: String(pack.id), label: pack.description ?? `Pack ${pack.id}`, price: pack.coins, untradeable: tokens }))}
      onClose={() => setAdding(false)} prepare={async offer => {
        const fresh = await api<PackCatalogReply>('packs/catalog?dropId=');
        const entries = tokens ? fresh.tokenListing ?? [] : fresh.listing ?? [];
        if (entries.some(entry => entry.packId === Number(offer.target) && entry.enabled)) throw new Error('This offer is already live. Refresh the lineup before scheduling it.');
        const next = entries.filter(entry => entry.packId !== Number(offer.target));
        next.push({ packId: Number(offer.target), price: offer.price, untradeable: offer.untradeable, enabled: false });
        await action.mutateAsync({ revision: fresh.revision, listing: next });
      }} />}
    </>
  );
}
