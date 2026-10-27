// Every pack and pick with its staged state and where it is used: Store (the published lineup sells it), Rewards (a
// published rule names it), Store + Rewards, or Unassigned. Hide stages taking a pack out of the store; owned copies stay.
// Delete stages removing the definition and is refused by the server while anything still uses it.
import { useState } from 'react';
import { isPick, useCatalogue, usageOf, usePackActions, USAGE_LABEL, type CatalogueEntry, type PackUsage } from '../../api/packs';
import { href } from '../../app/router';
import { Badge, StagedBadge } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { Chips } from '../../ui/Chips';
import { DataTable, type Column } from '../../ui/DataTable';
import { ConfirmDialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { DeletePackDialog } from './DeletePackDialog';
import { num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, Toolbar } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';

export type TypeFilter = 'all' | 'packs' | 'picks';
type UsageFilter = 'all' | PackUsage;

export const artLabel = (art: string | null | undefined) => (art ? art[0]!.toUpperCase() + art.slice(1) : 'Automatic');

/** How many slots a My Stadium pack has (store_packs.vanity_pack), or 0 for any other pack. */
const vanitySlots = (config: Record<string, unknown>) => (config.vanity_pack as { slots?: unknown[] } | undefined)?.slots?.length ?? 0;

const pickSummary = (config: Record<string, unknown>) =>
  (Array.isArray(config.player_picks) ? config.player_picks as { count?: number; choice_count?: number }[] : [{ count: 1, choice_count: Number(config.player_pick_count) }])
    .map((group) => `${group.count ?? 1} × choose 1 of ${group.choice_count ?? 3}`).join(' · ');

/** Where a pack is awarded: "rivals_rewards.promotion.1.packs[0].packId" reads as "rivals_rewards › promotion.1". */
const usedByLabel = (path: string) => {
  const [file, ...rest] = path.split('.');
  return `${file} › ${rest.slice(0, 2).join('.').replace(/\[\d+\]$/, '')}`;
};

const USAGE_TONE: Record<PackUsage, 'pitch' | 'neutral'> = { 'store-rewards': 'pitch', store: 'pitch', rewards: 'pitch', unassigned: 'neutral' };

export function CatalogueTab({ type: initialType = 'all' }: { type?: TypeFilter }) {
  const toast = useToast();
  const catalogue = useCatalogue();
  const actions = usePackActions();
  const [text, setText] = useState('');
  const [type, setType] = useState<TypeFilter>(initialType);
  const [usage, setUsage] = useState<UsageFilter>('all');
  const [hiding, setHiding] = useState<CatalogueEntry | null>(null);
  const [deleting, setDeleting] = useState<CatalogueEntry | null>(null);
  if (catalogue.isPending) return <Loading />;
  if (catalogue.isError || !catalogue.data) return <ErrorPanel what="The pack catalogue" error={catalogue.error} onRetry={() => void catalogue.refetch()} />;
  const data = catalogue.data;
  const query = text.trim().toLowerCase();
  const ofType = data.packs.filter((pack) => type === 'all' || isPick(pack.config) === (type === 'picks'));
  const picks = data.packs.filter((pack) => isPick(pack.config)).length;
  const rows = ofType.filter((pack) => (usage === 'all' || usageOf(pack) === usage)
    && (!query || `${pack.id} ${pack.description} ${pack.blurb} ${pack.group} ${(pack.usedBy ?? []).join(' ')}`.toLowerCase().includes(query)));
  const status = (pack: CatalogueEntry) => (
    <div className="row-actions">
      {pack.pending ? <StagedBadge>{`Staged: ${pack.pending.toLowerCase()}`}</StagedBadge> : null}
      {pack.grantOnly && <Badge>Grant only</Badge>}
      {pack.viewOnlyReason && <Badge>View only</Badge>}
    </div>
  );
  const columns: Column<CatalogueEntry>[] = [
    { key: 'pack', header: 'Pack', sort: (pack) => pack.description, cell: (pack) => <div><b>{pack.description}</b><div className="dim">#{pack.id} · {artLabel(pack.art)} art</div></div> },
    { key: 'contents', header: 'Contents', sort: (pack) => pack.items, cell: (pack) => pack.viewOnlyReason && !isPick(pack.config) ? <span className="dim">Its own draw rules</span>
      : vanitySlots(pack.config) ? `${num(vanitySlots(pack.config))} My Stadium items`
      : isPick(pack.config) ? pickSummary(pack.config) : `${num(pack.items)} items · ${num(pack.rare)} rare` },
    { key: 'used', header: 'Used in', sort: (pack) => USAGE_LABEL[usageOf(pack)], cell: (pack) => (
      <div>
        <Badge tone={USAGE_TONE[usageOf(pack)]}>{USAGE_LABEL[usageOf(pack)]}</Badge>
        {(pack.usedBy?.length ?? 0) > 0 && <div className="dim">{pack.usedBy!.slice(0, 2).map(usedByLabel).join(', ')}{pack.usedBy!.length > 2 ? ` and ${pack.usedBy!.length - 2} more` : ''}</div>}
      </div>
    ) },
    { key: 'status', header: 'Status', cell: (pack) => <div>{status(pack)}{pack.unavailableReason && <div className="dim">{pack.unavailableReason}</div>}</div> },
    { key: 'actions', header: '', cell: (pack) => (
      <span className="row-actions">
        <LinkButton size="sm" href={href('packs', 'catalogue', pack.id)}>Edit</LinkButton>
        {pack.inStore && pack.config.enabled !== false && !pack.locked && pack.pending !== 'Hide' && pack.pending !== 'Delete' && <Button size="sm" variant="ghost" onClick={() => setHiding(pack)}>Hide</Button>}
        {pack.pending !== 'New' && pack.pending !== 'Delete' && (pack.locked
          ? <Button size="sm" variant="ghost" disabled title="This built-in pack must stay available.">Delete</Button>
          : <Button size="sm" variant="ghost" onClick={() => setDeleting(pack)}>Delete</Button>)}
      </span>
    ) },
  ];
  const usageCount = (value: PackUsage) => ofType.filter((pack) => usageOf(pack) === value).length;
  return (
    <>
      <Toolbar>
        <Field label="Search"><input type="search" placeholder="Name, id, category or reward" value={text} onChange={(event) => setText(event.target.value)} /></Field>
        <Chips label="Show" value={type} onChange={setType} options={[
          { value: 'all', label: 'All', count: data.packs.length }, { value: 'packs', label: 'Packs', count: data.packs.length - picks }, { value: 'picks', label: 'Picks', count: picks }]} />
        <LinkButton variant="primary" href={href('packs', 'catalogue', 'new')}>Create pack</LinkButton>
        <LinkButton href={`${href('packs', 'catalogue', 'new')}?type=picks`}>Create pick</LinkButton>
      </Toolbar>
      <Chips label="Used in" value={usage} onChange={setUsage} options={[
        { value: 'all', label: 'Anywhere', count: ofType.length },
        ...(['store-rewards', 'store', 'rewards', 'unassigned'] as const).map((value) => ({ value, label: USAGE_LABEL[value], count: usageCount(value) }))]} />
      <p className="dim">A new pack is unassigned. Sell it by switching it on in the Store lineup; rewards use it once a reward names it.</p>
      <DataTable columns={columns} rows={rows} rowKey={(pack) => pack.id} pageSize={50}
        empty={<EmptyState title={type === 'picks' ? 'No picks' : 'No packs'} text={query || usage !== 'all' ? 'Try a different search or filter.' : 'Create a definition to get started.'} />} />
      <DeletePackDialog pack={deleting} revision={data.revision} onClose={() => setDeleting(null)} />
      <ConfirmDialog open={!!hiding} onOpenChange={(open) => { if (!open) setHiding(null); }}
        title={`Hide ${hiding?.description ?? 'this pack'}?`} text="After it is published, the regular coin listing is switched off. Its token listing keeps its own On switch. Clubs that own it keep it, and rewards that name it still award it."
        confirmLabel="Stage hide" variant="staged"
        onConfirm={() => hiding && actions.hide.mutateAsync({ id: hiding.id, revision: data.revision }).then(() => toast('Hide staged: publish it under Changes.', 'staged'))} />
    </>
  );
}
