import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import { contentActionKey, useChangeset } from '../../api/content';
import type { CardsReply, ChangesetReply } from '../../api/types';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { ErrorPanel, Loading } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';
import { filterMembers, loadMembers } from './members';

export interface BucketDefinition { code: string; name: string; draw_weight: number; cards: number[]; enabled?: boolean }
export interface PromoDefinition { name: string; odds_category: string; draw_weight: number; status: string; buckets: BucketDefinition[]; card_reductions?: Record<string, number> }
interface DefinitionReply { definition: PromoDefinition }

export function bucketCode(name: string, existing: BucketDefinition[]) {
  const stem = `bucket_${name.normalize('NFKD').replace(/[\u0300-\u036f]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/_+$/, '') || 'new'}`.slice(0, 70);
  let code = stem;
  for (let suffix = 2; existing.some(b => b.code === code); suffix++) code = `${stem}_${suffix}`;
  return code;
}

/** A range seeds membership once. Moving cards retains the donors, even when they become empty. */
export function withBucket(original: PromoDefinition, bucket: BucketDefinition, removedTo?: string): PromoDefinition {
  const moving = new Set(bucket.cards);
  const previous = original.buckets.find(b => b.code === bucket.code);
  const removed = previous?.cards.filter(id => !moving.has(id)) ?? [];
  if (removed.length && !original.buckets.some(b => b.code === removedTo && b.code !== bucket.code))
    throw new Error('Choose another bucket for the removed cards.');
  const buckets = original.buckets.map(b => b.code === bucket.code ? bucket : {
    ...b, cards: [...b.cards.filter(id => !moving.has(id)), ...(b.code === removedTo ? removed : [])],
  });
  return { ...original, buckets: previous ? buckets : [...buckets, bucket] };
}

export function effectiveDefinition(live: PromoDefinition, changes: ChangesetReply, code: string) {
  const change = changes.changeset?.changes?.find(c => c.area === 'promo' && c.key.code === code);
  if (change?.operation === 'hide') throw new Error('This promo has a pending hide. Resolve it in Changes before editing its buckets.');
  return (change?.after as PromoDefinition | undefined) ?? live;
}

function BucketMembers({ code, baseline, members, disabled, onChange, reductions, onReduction }: {
  code: string; baseline: PromoDefinition; members: number[]; disabled: boolean; onChange: (ids: number[]) => void;
  reductions: Record<string, string>; onReduction: (id: number, value?: string) => void;
}) {
  const [text, setText] = useState('');
  const [query, setQuery] = useState('');
  const [page, setPage] = useState(0);
  useEffect(() => { const timer = window.setTimeout(() => { setQuery(text.trim()); setPage(0); }, 250); return () => window.clearTimeout(timer); }, [text]);
  const known = new Set(baseline.buckets.flatMap(b => b.cards));
  const membership = useQuery({ queryKey: ['promo-draft-members', code, [...known]], queryFn: ({ signal }) => loadMembers([...known], signal) });
  const matching = filterMembers(membership.data ?? [], query);
  const results = { ...membership, data: { total: matching.length, cards: matching.slice(page * 50, (page + 1) * 50) } };
  return <div className="bucket-member-picker">
    <Field label="Find cards in this promo" hint="Select cards to keep or move into this bucket."><input type="search" value={text} disabled={disabled} onChange={e => setText(e.target.value)} /></Field>
    <p>{members.length.toLocaleString('en-GB')} selected for this bucket</p>
    {code !== 'gold_common' && <p className="dim">Mark a desirable player and choose how many times harder it is to draw within its bucket. This applies only in this promo.</p>}
    {results.isPending ? <Loading /> : results.isError ? <ErrorPanel error={results.error} onRetry={() => void results.refetch()} /> : <>
      <div className="bucket-member-list">{results.data.cards?.filter(card => card.resourceId && known.has(card.resourceId)).map(card => {
        const id = card.resourceId!;
        const owner = baseline.buckets.find(b => b.cards.includes(id));
        return <div className="bucket-member" key={id}><label className="bucket-member-selection"><input type="checkbox" disabled={disabled} checked={members.includes(id)}
          onChange={e => onChange(e.target.checked ? [...members, id] : members.filter(member => member !== id))} />
          <span><strong>{card.name ?? `Card ${id}`}</strong><span className="dim">{card.rating} · {card.position} · {owner?.name ?? 'Unassigned'}</span></span></label>
          {code !== 'gold_common' && <div className="bucket-member-reduction">
            <label><input type="checkbox" aria-label={`Desirable card ${id}`} disabled={disabled} checked={reductions[id] !== undefined}
              onChange={e => onReduction(id, e.target.checked ? '5' : undefined)} />Desirable</label>
            {reductions[id] !== undefined && <label><input type="number" aria-label={`Reduction for card ${id}`} min="1.000001" max="999999" step="any"
              disabled={disabled} value={reductions[id]} onChange={e => onReduction(id, e.target.value)} />× harder</label>}
          </div>}
        </div>;
      })}</div>
      {!results.data.cards?.length && <p className="dim">No matching cards.</p>}
      <div className="row-actions"><Button disabled={disabled || page === 0} onClick={() => setPage(page - 1)}>Previous cards</Button>
        <span className="dim">Page {page + 1}</span><Button disabled={disabled || (page + 1) * 50 >= (results.data.total ?? 0)} onClick={() => setPage(page + 1)}>Next cards</Button></div>
    </>}
  </div>;
}

export function BucketEditor({ code, bucketCode: editingCode, onClose }: { code: string; bucketCode?: string; onClose: () => void }) {
  const title = editingCode ? 'Edit bucket' : 'Create bucket';
  const definition = useQuery({ queryKey: ['promo-definition', code], queryFn: ({ signal }) => api<DefinitionReply>(`promos/${encodeURIComponent(code)}`, { signal }), staleTime: 0 });
  const pending = useChangeset();
  if (definition.isPending || pending.isPending) return <Dialog open onOpenChange={open => { if (!open) onClose(); }} title={title}><Loading /></Dialog>;
  if (definition.isError || pending.isError) return <Dialog open onOpenChange={open => { if (!open) onClose(); }} title={title}><ErrorPanel error={definition.error ?? pending.error} onRetry={() => { void definition.refetch(); void pending.refetch(); }} /></Dialog>;
  let original: PromoDefinition;
  try { original = effectiveDefinition(definition.data.definition, pending.data, code); }
  catch (error) { return <Dialog open onOpenChange={open => { if (!open) onClose(); }} title={title}><ErrorPanel error={error} /></Dialog>; }
  const bucket = editingCode ? original.buckets.find(b => b.code === editingCode) : undefined;
  if (editingCode && !bucket) return <Dialog open onOpenChange={open => { if (!open) onClose(); }} title={title}><ErrorPanel error={new Error('This bucket is no longer in the promo. Reopen the promo to refresh it.')} /></Dialog>;
  return <BucketForm code={code} original={original} {...(bucket && { bucket })} onClose={onClose} />;
}

function BucketForm({ code, original, bucket: initialBucket, onClose }: { code: string; original: PromoDefinition; bucket?: BucketDefinition; onClose: () => void }) {
  const client = useQueryClient();
  const toast = useToast();
  // Capture the starting definition so refreshes cannot silently replace a preview's donors.
  const [baseline] = useState(original);
  const [bucket] = useState(initialBucket);
  const [name, setName] = useState(bucket?.name ?? '');
  const [weight, setWeight] = useState(String(bucket?.draw_weight ?? 1));
  const [enabled, setEnabled] = useState(bucket?.enabled ?? true);
  const [members, setMembers] = useState(bucket?.cards ?? []);
  const [reductions, setReductions] = useState<Record<string, string>>(() => Object.fromEntries(
    Object.entries(original.card_reductions ?? {}).filter(([, value]) => value > 1).map(([id, value]) => [id, String(value)])));
  const [removedTo, setRemovedTo] = useState('');
  const [range, setRange] = useState(false);
  const [minimum, setMinimum] = useState('80');
  const [maximum, setMaximum] = useState('84');
  const [preview, setPreview] = useState<{ ids: number[]; samples: CardsReply['cards'] } | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const reset = () => { setPreview(null); setConfirmed(false); setError(null); };
  const newCode = bucket?.code ?? bucketCode(name.trim(), baseline.buckets);
  const problem = !name.trim() || name.trim().length > 80 ? 'Enter a bucket name (up to 80 characters).'
    : !/^\d+(?:\.\d{1,6})?$/.test(weight) || Number(weight) <= 0 || Number(weight) > 999999 ? 'Weight must be positive, at most 999999, with up to six decimal places.'
    : range && (![minimum, maximum].every(v => /^\d{1,2}$/.test(v)) || Number(minimum) > Number(maximum)) ? 'Choose a rating range from 0 to 99, with the lower rating first.'
    : Object.values(reductions).some(value => !/^\d+(?:\.\d{1,6})?$/.test(value) || Number(value) <= 1 || Number(value) > 999999)
      ? 'Desirable-player reductions must be greater than 1, at most 999999, with up to six decimal places.'
    : !bucket && baseline.buckets.length >= 200 ? 'This promo already has the maximum of 200 buckets.' : null;
  const selected = new Set(preview?.ids ?? []);
  const donors = baseline.buckets.filter(b => b.code !== bucket?.code).map(b => ({ code: b.code, name: b.name, count: b.cards.filter(id => selected.has(id)).length })).filter(b => b.count > 0);
  const removed = bucket?.cards.filter(id => !selected.has(id)) ?? [];
  const moveCount = donors.reduce((sum, b) => sum + b.count, 0);
  const makePreview = useMutation({ mutationFn: async () => {
    if (problem) throw new Error(problem);
    if (!range) return { ids: members, samples: [] };
    const reply = await api<{ buckets: BucketDefinition[]; samples?: CardsReply['cards'] }>(`promos/${encodeURIComponent(code)}/buckets/from-ratings`, {
      method: 'POST', body: { pending: true, definition: { ranges: [{ code: newCode, name: name.trim(), draw_weight: Number(weight), min_rating: Number(minimum), max_rating: Number(maximum) }] } },
    });
    const ids = reply.buckets[0]?.cards ?? [];
    return { ids, samples: reply.samples ?? [] };
  }, onSuccess: result => { setPreview(result); setConfirmed(false); setError(null); }, onError: e => setError(e.message) });
  const stage = useMutation({ mutationKey: contentActionKey, mutationFn: async () => {
    if (problem || !preview || (moveCount > 0 || removed.length > 0) && !confirmed) throw new Error(problem ?? 'Preview the cards and confirm their move first.');
    const [live, changes] = await Promise.all([api<DefinitionReply>(`promos/${encodeURIComponent(code)}`), api<ChangesetReply>('content/changeset')]);
    if (JSON.stringify(effectiveDefinition(live.definition, changes, code)) !== JSON.stringify(baseline))
      throw new Error('This promo changed while you were editing the bucket. Close this dialog and reopen it to preview the latest membership.');
    const known = new Set(baseline.buckets.flatMap(b => b.cards));
    if (preview.ids.some(id => !known.has(id))) throw new Error('The promo membership changed. Reopen this dialog before saving.');
    return api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: { area: 'promo', key: { code }, operation: 'update',
      after: { ...withBucket(baseline, { code: newCode, name: name.trim(), draw_weight: Number(weight), enabled, cards: preview.ids }, removedTo),
        card_reductions: Object.fromEntries(Object.entries(reductions).map(([id, value]) => [id, Number(value)])) } } });
  }, onSuccess: async () => { await client.invalidateQueries(); toast('Bucket saved.', 'done'); onClose(); }, onError: e => setError(e.message) });
  const busy = makePreview.isPending || stage.isPending;
  return <Dialog open wide onOpenChange={open => { if (!open && !busy) onClose(); }} title={bucket ? 'Edit bucket' : 'Create bucket'}
    description={`Organise cards already in ${baseline.name}. A rating range fills this bucket once; it is not an automatic rule.`}
    footer={<><Button disabled={busy} variant="ghost" onClick={onClose}>Cancel</Button><Button disabled={busy || !!problem} onClick={() => makePreview.mutate()}>{makePreview.isPending ? 'Loading…' : 'Preview bucket'}</Button>
      <Button variant="primary" disabled={busy || !!problem || !preview || (moveCount > 0 || removed.length > 0) && !confirmed || removed.length > 0 && !removedTo} onClick={() => stage.mutate()}>{stage.isPending ? 'Saving…' : 'Save bucket'}</Button></>}>
    <fieldset className="bucket-editor-fields" disabled={busy}>
      <div className="bucket-editor-row"><Field label="Bucket name"><input value={name} maxLength={80} placeholder="e.g. 85–89 or Standout players" onChange={e => { setName(e.target.value); reset(); }} /></Field>
        <Field label="Relative weight" hint="Compared with other buckets. Packs can override this weight."><input type="number" min="0.000001" max="999999" step="any" value={weight} onChange={e => { setWeight(e.target.value); reset(); }} /></Field></div>
      <label className="bucket-editor-toggle"><input type="checkbox" checked={range} onChange={e => { setRange(e.target.checked); reset(); }} />Fill from a rating range</label>
      <label className="bucket-editor-toggle"><input type="checkbox" checked={enabled} onChange={e => { setEnabled(e.target.checked); reset(); }} />Enabled for card draws</label>
      {range && <div className="bucket-editor-row"><Field label="Minimum rating"><input type="number" min={0} max={99} value={minimum} onChange={e => { setMinimum(e.target.value); reset(); }} /></Field>
        <Field label="Maximum rating"><input type="number" min={0} max={99} value={maximum} onChange={e => { setMaximum(e.target.value); reset(); }} /></Field></div>}
    </fieldset>
    {!range && <BucketMembers code={code} baseline={baseline} members={members} disabled={busy} reductions={reductions}
      onReduction={(id, value) => { setReductions(previous => { const next = { ...previous }; if (value === undefined) delete next[id]; else next[id] = value; return next; }); reset(); }}
      onChange={ids => { setMembers(ids); reset(); }} />}
    {preview && <div className="bucket-preview" aria-live="polite"><h3>{preview.ids.length} cards selected</h3>
      {!preview.ids.length && <p>This bucket will be empty. You can populate it later.</p>}
      {!!preview.samples?.length && <ul>{preview.samples.map(card => <li key={card.resourceId}>{card.name} · {card.rating} · {card.position}</li>)}</ul>}
      {!!preview.samples?.length && preview.ids.length > 12 && <p className="dim">Showing up to 12 of {preview.ids.length} matching cards.</p>}
      {moveCount > 0 && <><p>These cards will move out of their current buckets:</p><ul>{donors.map(b => <li key={b.code}>{b.name}: {b.count} cards</li>)}</ul></>}
      {removed.length > 0 && <Field label="Move removed cards to" hint={`${removed.length} cards will leave this bucket and stay in the promo.`}><select value={removedTo} disabled={busy} onChange={e => { setRemovedTo(e.target.value); setConfirmed(false); }}><option value="">Choose a bucket</option>{baseline.buckets.filter(b => b.code !== bucket?.code).map(b => <option key={b.code} value={b.code}>{b.name}{b.enabled === false ? ' (disabled)' : ''}</option>)}</select></Field>}
      {(moveCount > 0 || removed.length > 0) && <label className="bucket-editor-toggle"><input type="checkbox" disabled={busy} checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />{bucket ? 'Confirm these card moves' : `Move these ${moveCount} cards into the new bucket`}</label>}
      <p className="dim">Saving updates the group immediately. Promo availability follows its scheduled events.</p>
    </div>}
    {error && <p className="error-box" role="alert">{error}</p>}
  </Dialog>;
}
