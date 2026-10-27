import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import { contentActionKey, useChangeset } from '../../api/content';
import type { CardRow, CardsReply, ChangesetReply } from '../../api/types';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { ErrorPanel, Loading } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';
import { effectiveDefinition, type PromoDefinition } from './BucketEditor';
import { filterMembers, loadMembers } from './members';

const icons = ['base_icons', 'mid_icons', 'prime_icons', 'prime_icon_moments'];
const standard = ['bronze_common', 'bronze_rare', 'silver_common', 'silver_rare', 'gold_common', 'gold_rare'];
export const curatedMembership = (code: string) => icons.includes(code) || standard.includes(code);
export function groupCode(name: string) {
  return (`promo_${name.normalize('NFKD').replace(/[\u0300-\u036f]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/_+$/, '') || 'new'}`).slice(0, 80);
}
export function cardRestriction(code: string, card: CardRow): string | null {
  if (!card.resourceId || !Number.isSafeInteger(card.resourceId)) return 'Missing native resource ID';
  if (card.rareflag === 12 || card.rareflag === 84) return 'Curated Icon tier membership';
  if (card.rareflag === 18 && code !== 'fut_champions') return 'Aurora Champions reward only';
  if (card.rareflag === 10 && code !== 'developer_cards') return 'Developer group only';
  return null;
}
export function changeMembers(original: PromoDefinition, ids: number[], bucketCode: string, remove: boolean): PromoDefinition {
  if (!original.buckets.some(b => b.code === bucketCode)) throw new Error('Choose an existing destination bucket.');
  const selected = new Set(ids);
  const known = new Set(original.buckets.flatMap(b => b.cards));
  return { ...original, buckets: original.buckets.map(b => ({ ...b,
    cards: remove ? b.cards.filter(id => !selected.has(id)) : b.code === bucketCode ? [...b.cards, ...[...selected].filter(id => !known.has(id))] : [...b.cards],
  })), card_reductions: Object.fromEntries(Object.entries(original.card_reductions ?? {}).filter(([id]) => !remove || !selected.has(Number(id)))) };
}

export function PromoGroupEditor({ code, onClose, existingCodes }: { code?: string; onClose: () => void; existingCodes: string[] }) {
  const pending = useChangeset();
  const definition = useQuery({ queryKey: ['promo-definition', code], enabled: !!code,
    queryFn: ({ signal }) => api<{ definition: PromoDefinition }>(`promos/${encodeURIComponent(code!)}`, { signal }), retry: false });
  const pendingChange = pending.data?.changeset?.changes?.find(c => c.area === 'promo' && c.key.code === code);
  const title = code ? 'Manage promo cards' : 'Create promo';
  const close = (open: boolean) => { if (!open) onClose(); };
  if (pending.isPending || code && !pendingChange && definition.isPending) return <Dialog open title={title} onOpenChange={close}><Loading /></Dialog>;
  if (pending.isError || code && !pendingChange && definition.isError) return <Dialog open title={title} onOpenChange={close}><ErrorPanel error={pending.error ?? definition.error} /></Dialog>;
  if (pendingChange?.operation === 'hide') return <Dialog open title={title} onOpenChange={close}><ErrorPanel error={new Error('Resolve this group’s pending hide before editing membership.')} /></Dialog>;
  const baseline = (pendingChange?.after as PromoDefinition | undefined) ?? definition.data?.definition;
  return <GroupForm {...(code && { code })} {...(baseline && { baseline })} existingCodes={existingCodes} inserting={!code || pendingChange?.operation === 'insert'} onClose={onClose} />;
}

function GroupForm({ code: initialCode, baseline: initial, existingCodes, inserting, onClose }: {
  code?: string; baseline?: PromoDefinition; existingCodes: string[]; inserting: boolean; onClose: () => void;
}) {
  const [baseline] = useState<PromoDefinition>(initial ?? { name: '', odds_category: '', draw_weight: 1, status: 'active', buckets: [{ code: 'all_cards', name: 'All cards', draw_weight: 1, enabled: true, cards: [] }] });
  const [name, setName] = useState(baseline.name);
  const [code, setCode] = useState(initialCode ?? '');
  const [category, setCategory] = useState(baseline.odds_category);
  const [bucket, setBucket] = useState(baseline.buckets[0]!.code);
  const [mode, setMode] = useState<'add' | 'remove'>('add');
  const [selected, setSelected] = useState<number[]>([]);
  const [text, setText] = useState('');
  const [query, setQuery] = useState('');
  const [page, setPage] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const client = useQueryClient();
  const toast = useToast();
  useEffect(() => { const timer = window.setTimeout(() => { setQuery(text.trim()); setPage(0); }, 250); return () => window.clearTimeout(timer); }, [text]);
  const known = new Set(baseline.buckets.flatMap(b => b.cards));
  const membership = useQuery({ queryKey: ['promo-draft-members', code, [...known]], enabled: mode === 'remove',
    queryFn: ({ signal }) => loadMembers([...known], signal) });
  const globalCards = useQuery({ queryKey: ['promo-group-cards', code, query, page], enabled: mode === 'add', queryFn: ({ signal }) => api<CardsReply>(
    `cards?${new URLSearchParams({ q: query, limit: '50', offset: String(page * 50) })}`, { signal }) });
  const matchingMembers = filterMembers(membership.data ?? [], query);
  const results = mode === 'add' ? globalCards : { ...membership, data: { total: matchingMembers.length, cards: matchingMembers.slice(page * 50, (page + 1) * 50) } };
  const events = useQuery({ queryKey: ['promo-events'], enabled: !!initialCode, queryFn: ({ signal }) => api<{ definitions: { name: string; promos: string[]; starts_at: string; ends_at: string }[] }>('promo-events', { signal }) });
  const cards = results.data?.cards?.filter(card => mode === 'add' || card.resourceId && known.has(card.resourceId)) ?? [];
  const problem = !name.trim() || name.trim().length > 120 ? 'Enter a group name (up to 120 characters).'
    : !/^[a-z][a-z0-9_]{0,79}$/.test(code) || !initialCode && existingCodes.includes(code) ? 'Choose an unused lowercase code, starting with a letter.'
    : !/^[a-z][a-z0-9_]{0,79}$/.test(category) || !initialCode && [...standard, 'icon'].includes(category) ? 'Choose a non-standard odds category, using lowercase letters, digits and underscores.' : null;
  const save = useMutation({ mutationKey: contentActionKey, mutationFn: async () => {
    if (problem) throw new Error(problem);
    if (initialCode) {
      const changes = await api<ChangesetReply>('content/changeset');
      const pending = changes.changeset?.changes?.find(c => c.area === 'promo' && c.key.code === initialCode);
      const current = pending?.operation === 'insert' ? pending.after : effectiveDefinition((await api<{ definition: PromoDefinition }>(`promos/${encodeURIComponent(initialCode)}`)).definition, changes, initialCode);
      if (JSON.stringify(current) !== JSON.stringify(baseline)) throw new Error('This promo changed while you were editing. Reopen it before saving.');
    }
    return api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: { area: 'promo', key: { code }, operation: inserting ? 'insert' : 'update',
      after: { ...changeMembers(baseline, selected, bucket, mode === 'remove'), name: name.trim(), odds_category: category } } });
  }, onSuccess: async () => { await client.invalidateQueries(); toast('Promo saved.', 'done'); onClose(); }, onError: caught => setError(caught.message) });
  const shownIds = cards.filter(card => card.resourceId && !cardRestriction(code, card) && (mode === 'remove' || !known.has(card.resourceId))).map(card => card.resourceId!);
  return <Dialog open wide title={initialCode ? 'Manage promo cards' : 'Create promo'} description="Manage the cards in this promo. Saving updates the promo immediately; its drop or rotation controls availability."
    onOpenChange={open => { if (!open && !save.isPending) onClose(); }} footer={<><Button disabled={save.isPending} onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!!problem || save.isPending} onClick={() => save.mutate()}>{save.isPending ? 'Saving…' : 'Save promo'}</Button></>}>
    <fieldset className="bucket-editor-fields" disabled={save.isPending}>
      <Field label="Promo name"><input value={name} maxLength={120} onChange={e => { setName(e.target.value); if (!initialCode && (!code || code === groupCode(name))) { const next = groupCode(e.target.value); setCode(next); if (!category || category === code) setCategory(next); } }} /></Field>
      <div className="bucket-editor-row"><Field label="Group code" hint="Stable identity used by content drops and packs."><input value={code} disabled={!!initialCode} maxLength={80} onChange={e => setCode(e.target.value)} /></Field>
        <Field label="Odds category" hint="Configure pack odds for a new category before scheduling availability."><input value={category} disabled={!!initialCode} maxLength={80} onChange={e => setCategory(e.target.value)} /></Field></div>
      <p>{known.size} existing cards · {selected.length} selected to {mode}</p>
      <div className="row-actions"><Button aria-pressed={mode === 'add'} onClick={() => { setMode('add'); setSelected([]); setPage(0); }}>Add cards</Button><Button aria-pressed={mode === 'remove'} onClick={() => { setMode('remove'); setSelected([]); setPage(0); }}>Remove cards</Button></div>
      {mode === 'add' && <Field label="Destination bucket"><select value={bucket} onChange={e => setBucket(e.target.value)}>{baseline.buckets.map(b => <option key={b.code} value={b.code}>{b.name}{b.enabled === false ? ' (disabled)' : ''}</option>)}</select></Field>}
      <Field label="Find native card versions"><input type="search" value={text} onChange={e => setText(e.target.value)} /></Field>
      <div className="row-actions"><Button disabled={!shownIds.length} onClick={() => setSelected([...new Set([...selected, ...shownIds])])}>Select eligible cards on this page</Button><Button disabled={!selected.length} onClick={() => setSelected([])}>Clear selection</Button></div>
      {results.isPending ? <Loading /> : results.isError ? <ErrorPanel error={results.error} onRetry={() => void results.refetch()} /> : <>
        <div className="bucket-member-list">{cards.map(card => { const restriction = cardRestriction(code, card); const member = !!card.resourceId && known.has(card.resourceId); return <label className="bucket-member" key={card.cardKey}>
          <input type="checkbox" checked={selected.includes(card.resourceId!)} disabled={!!restriction || mode === 'add' && member} onChange={e => setSelected(e.target.checked ? [...selected, card.resourceId!] : selected.filter(id => id !== card.resourceId))} />
          <span><strong>{card.name} · {card.rating} · {card.position}</strong><span className="dim"> Native ID {card.resourceId} · {card.special ?? `rarity ${card.rareflag}`}{restriction ? ` · ${restriction}` : mode === 'add' && member ? ' · Already in group' : ''}</span></span>
        </label>; })}</div>{!cards.length && <p className="dim">No matching cards on this page.</p>}
        <div className="row-actions"><Button disabled={page === 0} onClick={() => setPage(page - 1)}>Previous cards</Button><span>Page {page + 1}</span><Button disabled={(page + 1) * 50 >= (results.data.total ?? 0)} onClick={() => setPage(page + 1)}>Next cards</Button></div>
      </>}
    </fieldset>
    {initialCode && <div className="bucket-preview"><h3>Affected promo events</h3>{events.isPending ? <Loading /> : events.isError ? <ErrorPanel error={events.error} onRetry={() => void events.refetch()} /> : <>{(events.data.definitions ?? []).filter(event => event.promos.includes(initialCode)).map(event => <p key={event.name}>{event.name} · {new Date(event.starts_at).toLocaleString('en-GB', { timeZone: 'Europe/London' })} – {new Date(event.ends_at).toLocaleString('en-GB', { timeZone: 'Europe/London' })} UK</p>)}{!(events.data.definitions ?? []).some(event => event.promos.includes(initialCode)) && <p className="dim">No published promo events use this group.</p>}</>}<p className="dim">Packs selecting this group use its saved membership. Removing membership preserves owned cards.</p></div>}
    {problem && <p className="dim">{problem}</p>}{error && <p className="error-box" role="alert">{error}</p>}
  </Dialog>;
}
