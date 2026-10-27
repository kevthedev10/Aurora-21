// Gifts: packs to every club at once, each club's unopened packs, and the recent gifts (GET packs, PATCH players/<id>).
// A gift applies at once to each club in turn; the result per club stays listed.
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../api/client';
import { playersKey } from '../../api/players';
import type { PacksReply } from '../../api/types';
import { href } from '../../app/router';
import { Badge, StatusDot } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { DataTable } from '../../ui/DataTable';
import { ConfirmDialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { Tradeability } from '../../ui/Tradeability';
import { ago, num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, Section } from '../../ui/Layout';

const PACKS_PER_GIFT = 60;

interface ClubRow { id: number; name?: string; online?: boolean; unopenedPacks?: number; owned?: { packs?: { packId: number; count: number; name?: string }[] } }
interface GiftRow { id: number; personaId: number; packId: number; count: number; createdAt?: string | null; result?: string }
type Overview = PacksReply & { clubs?: ClubRow[]; grants?: GiftRow[] };
interface Outcome { id: number; name: string; ok: boolean; message: string }

export function GiftsTab() {
  const client = useQueryClient();
  const overview = useQuery({ queryKey: ['packs-overview'], queryFn: () => api<Overview>('packs') });
  const [packId, setPackId] = useState<number | null>(null);
  const [count, setCount] = useState('1');
  const [untradeable, setUntradeable] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [outcomes, setOutcomes] = useState<Outcome[] | null>(null);
  if (overview.isPending) return <Loading />;
  if (overview.isError) return <ErrorPanel what="The packs overview" error={overview.error} onRetry={() => void overview.refetch()} />;
  const data = overview.data;
  const packs = data.packs?.packs ?? [];
  const clubs = data.clubs ?? [];
  const target = packId ?? packs[0]?.id ?? null;
  const pack = packs.find((candidate) => candidate.id === target);
  const amount = Number(count);
  const valid = Number.isInteger(amount) && amount >= 1 && amount <= PACKS_PER_GIFT;
  const names = new Map(packs.map((candidate) => [candidate.id, candidate.name ?? `pack ${candidate.id}`]));
  const clubNames = new Map(clubs.map((club) => [club.id, club.name ?? `persona ${club.id}`]));

  // One PATCH per club, in turn. A club the server refuses (for example its unopened-pack limit) is listed with its reason.
  const giftAll = async () => {
    if (!pack || !valid) return;
    const results: Outcome[] = [];
    for (const club of clubs) {
      const name = club.name ?? `persona ${club.id}`;
      try {
        await api(`players/${club.id}`, { method: 'PATCH', body: { op: 'grantPacks', packId: pack.id, count: amount, untradeable } });
        results.push({ id: club.id, name, ok: true, message: `Gifted ${amount} × ${pack.name ?? `pack ${pack.id}`}.` });
      } catch (error) {
        results.push({ id: club.id, name, ok: false, message: error instanceof Error ? error.message : String(error) });
      }
      setOutcomes([...results]);
    }
    setOutcomes(results);
    void client.invalidateQueries({ queryKey: ['packs-overview'] });
    void client.invalidateQueries({ queryKey: playersKey });
  };

  return (
    <>
      <Section title="Gift to every club" text="Each club gets the packs in My Packs at once; a club the server refuses is listed with the reason. The panel cannot take gifts back.">
        <div className="pack-action-controls pack-gift-controls">
          <Field label="Pack or player pick">
            <select value={target ?? ''} onChange={(event) => setPackId(Number(event.target.value))}>
              {packs.map((option) => <option key={option.id} value={option.id}>{option.name ?? `Pack ${option.id}`}</option>)}
            </select>
          </Field>
          <Field label="How many per club" hint={`1 to ${PACKS_PER_GIFT}`} error={count && !valid ? `A whole number from 1 to ${PACKS_PER_GIFT}.` : null}>
            <input type="number" min={1} max={PACKS_PER_GIFT} value={count} onChange={(event) => setCount(event.target.value)} />
          </Field>
          <Tradeability value={untradeable} onChange={setUntradeable} disabled={confirming} />
          <Button variant="primary" onClick={() => setConfirming(true)} disabled={!pack || !valid || !clubs.length}>Gift to {num(clubs.length)} clubs</Button>
        </div>
        {outcomes && (
          <ul className="outcomes" aria-live="polite">
            {outcomes.map((outcome) => <li key={outcome.id}><Badge tone={outcome.ok ? 'pitch' : 'red'}>{outcome.ok ? 'Gifted' : 'Not gifted'}</Badge> <b>{outcome.name}</b> <span className="dim">{outcome.message}</span></li>)}
          </ul>
        )}
      </Section>
      <Section title="Unopened packs by club">
        <DataTable
          columns={[
            { key: 'club', header: 'Club', sort: (club) => club.name ?? '', cell: (club) => <a href={href('players', club.id, 'packs')}>{club.name ?? `Persona ${club.id}`}</a> },
            { key: 'online', header: 'Status', cell: (club) => <StatusDot state={club.online ?? null}>{club.online ? 'Online' : 'Offline'}</StatusDot> },
            { key: 'held', header: 'In My Packs', align: 'end', sort: (club) => club.unopenedPacks, cell: (club) => num(club.unopenedPacks) },
            { key: 'packs', header: 'Packs', cell: (club) => (club.owned?.packs ?? []).map((row) => `${row.count} × ${row.name ?? `pack ${row.packId}`}`).join(', ') || '–' },
          ]}
          rows={clubs} rowKey={(club) => club.id} pageSize={50}
          empty={<EmptyState title="No clubs yet" />}
        />
      </Section>
      <Section title="Recent gifts" text="The last 30 pack gifts from the panel.">
        <DataTable
          columns={[
            { key: 'when', header: 'When', cell: (gift) => ago(gift.createdAt) },
            { key: 'club', header: 'Club', cell: (gift) => clubNames.get(gift.personaId) ?? `persona ${gift.personaId}` },
            { key: 'gift', header: 'Gift', cell: (gift) => `${gift.count} × ${names.get(gift.packId) ?? `pack ${gift.packId}`}` },
            { key: 'result', header: 'Result', cell: (gift) => (gift.result === 'ok' ? <Badge tone="pitch">Applied</Badge> : <Badge tone="red">{gift.result ?? '–'}</Badge>) },
          ]}
          rows={data.grants ?? []} rowKey={(gift) => gift.id}
          empty={<EmptyState title="No gifts yet" />}
        />
      </Section>
      <ConfirmDialog open={confirming} onOpenChange={setConfirming}
        title={`Gift ${amount} × ${pack?.name ?? 'pack'} to ${num(clubs.length)} clubs?`}
        text={`Every club receives ${untradeable ? 'untradeable' : 'tradeable'} packs at once. The panel cannot take them back.`} confirmLabel="Gift to every club"
        onConfirm={giftAll} />
    </>
  );
}
