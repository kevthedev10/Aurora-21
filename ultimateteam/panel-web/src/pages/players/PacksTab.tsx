// Packs & gifts (plan 4.8): the club's unopened packs, and gifting packs or granting a card. Both apply at once.
import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useAssets } from '../../api/assets';
import { api } from '../../api/client';
import { refusedUnchanged, supports, useClubEdit, useInventory, type PlayerDetail } from '../../api/players';
import type { CardRow, CardsReply, PacksReply } from '../../api/types';
import { Button } from '../../ui/Button';
import { DataTable } from '../../ui/DataTable';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { FutCard } from '../../ui/FutCard';
import { EmptyState, ErrorPanel, Loading, Section } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';
import { Tradeability } from '../../ui/Tradeability';

/** Server limits (OperatorPanel.Players.cs): packs per gift and cards per grant. How many unopened packs a gift may leave
 *  is the server's call (the game itself allows more); a refusal is shown as the server words it. */
const PACKS_PER_GIFT = 60;

export function PacksTab({ detail }: { detail: PlayerDetail }) {
  const [gift, setGift] = useState(false);
  const [grant, setGrant] = useState(false);
  const inventory = useInventory(detail.id, 'club');
  const held = detail.club?.unopenedPacks ?? 0;
  const packs = inventory.data?.packs ?? [];
  return (
    <Section
      title="Unopened packs"
      text={`My Packs holds ${num(held)} unopened pack${held === 1 ? '' : 's'}. Gifts land there at once; the panel cannot take them back.`}
      actions={<div className="row-actions">
        <Button variant="primary" onClick={() => setGift(true)} disabled={!supports(detail, 'grantPacks')}>Gift packs &amp; picks</Button>
        <Button onClick={() => setGrant(true)} disabled={!supports(detail, 'grantCard')}>Grant a card</Button>
      </div>}
    >
      {inventory.isPending ? <Loading lines={2} /> : inventory.isError ? <ErrorPanel what="Unopened packs" error={inventory.error} onRetry={() => void inventory.refetch()} /> : (
        <DataTable
          columns={[
            { key: 'name', header: 'Pack', sort: (row) => row.name ?? '', cell: (row) => row.name ?? `Pack ${row.packId}` },
            { key: 'id', header: 'Pack id', cell: (row) => <span className="mono">{row.packId}</span> },
            { key: 'count', header: 'Unopened', align: 'end', sort: (row) => row.count, cell: (row) => num(row.count) },
          ]}
          rows={packs}
          rowKey={(row) => row.packId}
          pageSize={20}
          empty={<EmptyState title="No unopened packs" text="Packs the club buys or is gifted wait here until they are opened." />}
        />
      )}
      {gift && <GiftPacks detail={detail} onClose={() => setGift(false)} />}
      {grant && <GrantCard detail={detail} onClose={() => setGrant(false)} />}
    </Section>
  );
}

function GiftPacks({ detail, onClose }: { detail: PlayerDetail; onClose: () => void }) {
  const [untradeable, setUntradeable] = useState(false);
  const toast = useToast();
  const edit = useClubEdit(detail.id);
  const catalogue = useQuery({ queryKey: ['packs-overview'], queryFn: () => api<PacksReply>('packs') });
  const packs = catalogue.data?.packs?.packs ?? [];
  const [packId, setPackId] = useState<number | null>(null);
  const [count, setCount] = useState('1');
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { if (packId === null && packs[0]) setPackId(packs[0].id); }, [packId, packs]);
  const pack = packs.find((candidate) => candidate.id === packId);
  const max = PACKS_PER_GIFT;
  const amount = Number(count);
  const valid = Number.isInteger(amount) && amount >= 1 && amount <= max;
  const send = () => {
    if (!pack || !valid) return;
    setError(null);
    edit.mutate({ op: 'grantPacks', packId: pack.id, count: amount, untradeable }, {
      onSuccess: () => { toast(`Gifted ${amount} × ${pack.name ?? `pack ${pack.id}`} to ${detail.name ?? 'the club'}.`); onClose(); },
      // A refusal keeps the dialog to adjust; after anything else the gift may have landed, so it closes.
      onError: (caught) => { if (refusedUnchanged(caught)) setError(caught.message); else { toast(caught.message, 'error'); onClose(); } },
    });
  };
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose(); }} wide title={`Gift packs & picks to ${detail.name ?? 'this club'}`}
      description="They land in My Packs at once. The panel cannot take them back."
      footer={<>
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" onClick={send} disabled={!pack || !valid || edit.isPending}>{valid ? `Gift ${amount} pack${amount === 1 ? '' : 's'}` : 'Gift'}</Button>
      </>}
    >
      {catalogue.isPending ? <Loading lines={3} /> : catalogue.isError ? <ErrorPanel what="The pack catalogue" error={catalogue.error} onRetry={() => void catalogue.refetch()} /> : !packs.length ? <EmptyState title="No packs available to gift" /> : (
        <div className="form-row">
          <Field label="Pack or player pick">
            <select value={packId ?? ''} onChange={(event) => setPackId(Number(event.target.value))}>
              {packs.map((option) => <option key={option.id} value={option.id}>{option.name ?? `Pack ${option.id}`}{option.enabled === false ? ' (not in the store)' : ''}</option>)}
            </select>
          </Field>
          <Field label="How many" hint={`1 to ${max}`} error={count && !valid ? `A whole number from 1 to ${max}.` : null}>
            <input type="number" min={1} max={max} value={count} onChange={(event) => setCount(event.target.value)} />
          </Field>
        </div>
      )}
      <Tradeability value={untradeable} onChange={setUntradeable} disabled={edit.isPending} />
      {pack && (
        <p className="dim">
          {num(pack.items)} items, at least {num(pack.rare)} rare{pack.ratingMin != null ? `, rated ${pack.ratingMin}–${pack.ratingMax ?? 99}` : ''}.
          {' '}In the store for {num(pack.coins)} coins or {num(pack.points)} Aurora Tokens.{pack.blurb ? ` ${pack.blurb}` : ''}
        </p>
      )}
      {error && <p className="error" role="alert">{error}</p>}
    </Dialog>
  );
}

function GrantCard({ detail, onClose }: { detail: PlayerDetail; onClose: () => void }) {
  const [untradeable, setUntradeable] = useState(false);
  const toast = useToast();
  const edit = useClubEdit(detail.id);
  const assets = useAssets().data;
  const [text, setText] = useState('');
  const [query, setQuery] = useState('');
  const [card, setCard] = useState<CardRow | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { const timer = window.setTimeout(() => setQuery(text.trim()), 250); return () => window.clearTimeout(timer); }, [text]);
  const results = useQuery({
    queryKey: ['cards-search', query, 24],
    queryFn: () => api<CardsReply>(`cards?${new URLSearchParams({ q: query, limit: '24' })}`),
    enabled: query.length >= 2,
  });
  // A panel grant gives one copy of a card.
  const send = () => {
    if (!card) return;
    setError(null);
    edit.mutate({ op: 'grantCard', card: card.cardKey, count: 1, untradeable: !!card.forcedUntradeable || card.rareflag === 10 || untradeable }, {
      onSuccess: () => { toast(`Granted ${card.name ?? card.cardKey} to ${detail.name ?? 'the club'}.`); onClose(); },
      onError: (caught) => { if (refusedUnchanged(caught)) setError(caught.message); else { toast(caught.message, 'error'); onClose(); } },
    });
  };
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose(); }} wide title={`Grant a card to ${detail.name ?? 'this club'}`}
      description="The card lands in New items at once."
      footer={<>
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" onClick={send} disabled={!card || edit.isPending}>{card ? `Grant ${card.name ?? 'card'}` : 'Grant'}</Button>
      </>}
    >
      <div className="form-row">
        <Field label="Find a card" hint="Name or card id, at least two characters"><input type="search" value={text} onChange={(event) => setText(event.target.value)} autoFocus /></Field>
      </div>
      <Tradeability value={untradeable} onChange={setUntradeable} forced={!!card?.forcedUntradeable || card?.rareflag === 10} disabled={edit.isPending} />
      {query.length >= 2 && (results.isFetching && !results.data ? <Loading lines={2} /> : results.isError ? <ErrorPanel what="The card search" error={results.error} onRetry={() => void results.refetch()} /> : (
        <div className="pick-grid" role="listbox" aria-label="Cards">
          {(results.data?.cards ?? []).map((row) => (
            <div key={row.cardKey} role="option" aria-selected={card?.cardKey === row.cardKey} className="pick">
              {assets && row.art ? (
                <FutCard art={row.art} assets={assets} onClick={() => setCard(row)} title={`${row.name} ${row.rating} ${row.special ?? ''}`} />
              ) : (
                <Button onClick={() => setCard(row)}>{row.name} {row.rating} {row.special ?? ''}</Button>
              )}
            </div>
          ))}
          {!results.data?.cards?.length && <p className="dim">No card matches.</p>}
        </div>
      ))}
      {error && <p className="error" role="alert">{error}</p>}
    </Dialog>
  );
}
