import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { motion } from 'motion/react';
import { useEffect, useState } from 'react';
import { api, setContentDestination, type ApiOptions } from '../../api/client';
import { useAssets, type CardArt } from '../../api/assets';
import { contentActionKey, useChangeset } from '../../api/content';
import { iconPromoCodes as icons, standardPromoCodes as standard, isTotwPromo, isCampaignPromo } from '../../api/promoKinds';
import type { ChangesetReply } from '../../api/types';
import type { PromoDefinition } from '../promos/BucketEditor';
import { href } from '../../app/router';
import { Badge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { FutCard } from '../../ui/FutCard';
import { EmptyState, ErrorPanel, Loading, PageHeader } from '../../ui/Layout';
import { usePanelMotion } from '../../ui/Motion';
import { useToast } from '../../ui/Toast';
import { NewPromoOddsDialog, type EventPromo } from '../packs/NewPromoOddsDialog';
import { displayDate, timingStatus } from './time';
import { contentDateInstant, ukDay } from '../schedule/time';
import './events.css';

interface Promo { code: string; name: string; category: string; status: string; cards: number; buckets: number; artwork: CardArt[] }
export interface Timing { name: string; enabled: boolean; starts_at: string; ends_at: string; promo?: string; promos?: string[] }
interface Row extends Timing { code: string }
type Kind = 'promo_event' | 'icon_rotation';
// An empty explicit dropId opts out of the selected campaign without changing the operator's destination.
function standaloneApi<T>(path: string, options: ApiOptions = {}) {
  return api<T>(`${path}${path.includes('?') ? '&' : '?'}dropId=`, options);
}
const useStandaloneChangeset = () => useChangeset(undefined, 'immediate');
const endpoint = (kind: Kind) => kind === 'icon_rotation' ? 'icon-rotations' : 'promo-events';
const withoutCode = ({ code: _code, ...row }: Row): Timing => row;
function effective(live: Timing | null, pending: ChangesetReply, kind: Kind, code: string) {
  const change = pending.changeset?.changes?.find(c => c.area === kind && c.key.code === code);
  return change?.operation === 'hide' && live ? { ...live, enabled: false } : (change?.after as Timing | undefined) ?? live;
}

export function EventsPage() {
  return <><PageHeader title="Schedule" description="Standard cards, TOTW and Icon rotations are managed separately from promotional campaigns." /><RotationsSection /></>;
}

export function RotationsSection({ focus, onFocusHandled }: { focus?: { kind: Kind; code: string } | null; onFocusHandled?: () => void } = {}) {
  const promos = useQuery({ queryKey: ['standalone-promos'], queryFn: ({ signal }) => standaloneApi<{ definitions: Promo[] }>('promos', { signal }) });
  const events = useQuery({ queryKey: ['standalone-promo-events'], queryFn: ({ signal }) => standaloneApi<{ definitions: Row[] }>('promo-events?standalone=1', { signal }) });
  const rotations = useQuery({ queryKey: ['standalone-icon-rotations'], queryFn: ({ signal }) => standaloneApi<{ definitions: Row[] }>('icon-rotations', { signal }) });
  const availability = useQuery({ queryKey: ['standalone-promo-availability'], queryFn: ({ signal }) => standaloneApi<{ at: string; iconPromo: string | null }>('promos/availability', { signal }), refetchInterval: 60_000 });
  const pending = useStandaloneChangeset();
  const assets = useAssets().data;
  const { reduced, transition } = usePanelMotion();
  const [editing, setEditing] = useState<{ kind: Kind; code?: string } | null>(null);
  useEffect(() => {
    if (!focus) return;
    setEditing(focus);
    onFocusHandled?.();
  }, [focus, onFocusHandled]);
  // A staged promo event's promos: packs drawing the current event that have no odds for one of them get them next.
  const [oddsFor, setOddsFor] = useState<EventPromo[] | null>(null);
  const queries = [promos, events, rotations, availability, pending];
  const error = queries.find(q => q.isError);
  if (error) return <ErrorPanel what="The events" error={error.error} onRetry={() => queries.forEach(q => void q.refetch())} />;
  if (!promos.data || !events.data || !rotations.data || !availability.data || !pending.data) return <Loading />;
  const all = [...promos.data.definitions];
  for (const change of pending.data.changeset?.changes ?? []) {
    if (change.area !== 'promo') continue;
    const code = String(change.key.code);
    const existing = all.findIndex(p => p.code === code);
    if (change.operation === 'hide') {
      if (existing >= 0) all[existing] = { ...all[existing]!, status: 'inactive' };
      continue;
    }
    if (!change.after) continue;
    const definition = change.after as PromoDefinition;
    const row: Promo = { code, name: definition.name, category: definition.odds_category, status: definition.status,
      cards: definition.buckets.reduce((count, bucket) => count + bucket.cards.length, 0), buckets: definition.buckets.length,
      artwork: existing < 0 ? [] : all[existing]!.artwork };
    if (existing >= 0) all[existing] = row;
    else if (change.operation === 'insert') all.push(row);
  }
  const current = availability.data;
  const pendingRows = (rows: Row[], kind: Kind) => {
    const merged = rows.map(row => ({ ...row, ...effective(withoutCode(row), pending.data!, kind, row.code) }));
    for (const c of pending.data!.changeset?.changes ?? []) if (c.area === kind && c.operation === 'insert' && c.after && !merged.some(r => r.code === c.key.code))
      merged.push({ code: String(c.key.code), ...(c.after as Timing) });
    return merged.sort((a, b) => Date.parse(a.starts_at) - Date.parse(b.starts_at) || a.code.localeCompare(b.code));
  };
  const art = (promo?: Promo) => assets && promo?.artwork?.[0] ? <FutCard art={promo.artwork[0]} assets={assets} size="sm" /> : <span className="event-art-placeholder" aria-hidden="true">21</span>;
  const rowView = (row: Row, kind: Kind) => {
    const staged = pending.data!.changeset?.changes?.some(c => c.area === kind && c.key.code === row.code);
    const codes = kind === 'icon_rotation' ? [row.promo!] : row.promos ?? [];
    const status = timingStatus(row, current.at);
    return <motion.article className={`event-row ${!staged && status === 'Active' ? 'event-active' : ''}`} key={row.code} layout={!reduced} transition={transition}>
      <div className="event-row-art" aria-hidden="true">{art(all.find(p => p.code === codes[0]))}</div>
      <div className="event-row-body"><div className="event-row-heading"><h3>{row.name}</h3><Badge tone={staged ? 'staged' : status === 'Active' ? 'pitch' : 'neutral'}>{staged ? 'Pending' : status}</Badge></div>
        <p className="event-dates"><time dateTime={row.starts_at}>{displayDate(row.starts_at)}</time><span>→</span><time dateTime={row.ends_at}>{displayDate(row.ends_at)}</time></p>
        <div className="event-promo-links">{codes.map(code => <a key={code} href={href('promos', code)}>{all.find(p => p.code === code)?.name ?? code}</a>)}</div>
      </div><Button onClick={() => setEditing({ kind, code: row.code })}>Edit schedule</Button>
    </motion.article>;
  };
  return <>
    <div className="standalone-rotations-heading"><h2>Standalone rotations</h2><p>Standard cards stay on. TOTW and Icons have their own schedule outside campaigns.</p></div>
    <p className="event-timezone">New rotations start and end at 7pm UK time. End times are exclusive. Existing schedules retain their exact times until their dates change.</p>
    {pending.data.changeset?.changes?.some(c => c.area === 'promo_event' || c.area === 'icon_rotation') && <p className="notice">Older pending rotation changes remain under Changes until you <a href={href('changes')} onClick={() => setContentDestination(null)}>publish standalone Changes</a>.</p>}
    <section className="event-section"><header><div><h2>Standard pool</h2><p>Always available · common and rare cards</p></div><Badge tone="pitch">Always on</Badge></header>
      <div className="event-standard-grid">{standard.map(code => { const promo = all.find(p => p.code === code); return promo && <motion.a key={code} href={href('promos', code)} className="event-standard-card" whileHover={reduced ? {} : { y: -3 }} transition={transition}>
        <div aria-hidden="true">{art(promo)}</div><h3>{promo.name}</h3><span>{promo.cards.toLocaleString()} cards · {promo.buckets} {promo.buckets === 1 ? 'bucket' : 'buckets'}</span>
      </motion.a>; })}</div>
    </section>
    <section className="event-section"><header><div><h2>Icon rotation</h2><p>One tier joins the standard pool at a time. Currently: {all.find(p => p.code === current.iconPromo)?.name ?? 'None'}.</p></div><Button onClick={() => setEditing({ kind: 'icon_rotation' })}>Add rotation</Button></header>
      <div className="event-timeline">{pendingRows(rotations.data.definitions, 'icon_rotation').map(row => rowView(row, 'icon_rotation'))}</div>
      <p className="dim event-footnote">Outside enabled rotation windows, Base Icons are used.</p>
    </section>
    <section className="event-section"><header><div><h2>TOTW rotation</h2><p>Schedule numbered TOTW and Silver TOTW teams independently of campaigns.</p></div><Button variant="primary" onClick={() => setEditing({ kind: 'promo_event' })}>Create TOTW rotation</Button></header>
      {pendingRows(events.data.definitions, 'promo_event').filter(row => (row.promos ?? []).every(code => isTotwPromo(all.find(p => p.code === code) ?? { code }))).length
        ? <div className="event-timeline">{pendingRows(events.data.definitions, 'promo_event').filter(row => (row.promos ?? []).every(code => isTotwPromo(all.find(p => p.code === code) ?? { code }))).map(row => rowView(row, 'promo_event'))}</div>
        : <EmptyState title="No TOTW rotations" text="Choose the TOTW teams and their release dates." />}
    </section>
    {pendingRows(events.data.definitions, 'promo_event').some(row => (row.promos ?? []).some(code => !isTotwPromo(all.find(p => p.code === code) ?? { code }))) && <section className="event-section"><header><div><h2>Legacy standalone promo events</h2><p>Existing promo events remain editable. Schedule new promotional content in campaigns above.</p></div></header><div className="event-timeline">{pendingRows(events.data.definitions, 'promo_event').filter(row => (row.promos ?? []).some(code => !isTotwPromo(all.find(p => p.code === code) ?? { code }))).map(row => rowView(row, 'promo_event'))}</div></section>}
    {editing && <ScheduleEditor key={`${editing.kind}:${editing.code ?? 'new'}`} kind={editing.kind} code={editing.code} promos={all} onClose={() => setEditing(null)}
      onStaged={(codes) => { if (editing.kind === 'promo_event') setOddsFor(all.filter((promo) => codes.includes(promo.code))); }} />}
    {oddsFor && oddsFor.length > 0 && <NewPromoOddsDialog destination="immediate" promos={oddsFor} onClose={() => setOddsFor(null)} />}
  </>;
}

function ScheduleEditor({ kind, code, promos, onClose, onStaged }: { kind: Kind; code: string | undefined; promos: Promo[]; onClose: () => void; onStaged: (codes: string[]) => void }) {
  const pending = useStandaloneChangeset();
  const inserted = pending.data?.changeset?.changes?.find(c => c.area === kind && c.key.code === code && c.operation === 'insert');
  const live = useQuery({ queryKey: ['standalone-schedule-definition', kind, code], queryFn: () => standaloneApi<{ definition: Timing }>(`${endpoint(kind)}/${encodeURIComponent(code!)}`), enabled: !!code && !!pending.data && !inserted, staleTime: 0 });
  const title = `${code ? 'Edit' : 'Create'} ${kind === 'icon_rotation' ? 'Icon rotation' : 'promo event'}`;
  if (pending.isError || code && live.isError) return <Dialog open title={title} onOpenChange={open => { if (!open) onClose(); }}><ErrorPanel error={pending.error ?? live.error} onRetry={() => { void pending.refetch(); void live.refetch(); }} /></Dialog>;
  if (!pending.data || code && !live.data && !inserted) return <Dialog open title={title} onOpenChange={open => { if (!open) onClose(); }}><Loading /></Dialog>;
  const initial = code ? effective(live.data?.definition ?? null, pending.data, kind, code)! : null;
  return <ScheduleForm kind={kind} code={code} initial={initial} promos={promos} title={title} onClose={onClose} onStaged={onStaged} />;
}

function ScheduleForm({ kind, code, initial, promos, title, onClose, onStaged }: { kind: Kind; code: string | undefined; initial: Timing | null; promos: Promo[]; title: string; onClose: () => void; onStaged: (codes: string[]) => void }) {
  const client = useQueryClient(), toast = useToast();
  const [baseline] = useState(initial);
  const [name, setName] = useState(initial?.name ?? '');
  const [enabled, setEnabled] = useState(initial?.enabled ?? true);
  const [start, setStart] = useState(initial ? ukDay(initial.starts_at) : '');
  const [end, setEnd] = useState(initial ? ukDay(initial.ends_at) : '');
  const [selected, setSelected] = useState<string[]>(initial?.promos ?? (initial?.promo ? [initial.promo] : []));
  const [search, setSearch] = useState('');
  const legacy = initial?.promos?.some(code => !isTotwPromo(promos.find(p => p.code === code) ?? { code })) ?? false;
  const candidates = promos.filter(p => kind === 'icon_rotation' ? icons.includes(p.code) : isTotwPromo(p) || legacy && isCampaignPromo(p))
    .filter(p => p.status === 'active' || selected.includes(p.code)).sort((a, b) => kind === 'icon_rotation' ? icons.indexOf(a.code) - icons.indexOf(b.code) : a.name.localeCompare(b.name, 'en', { numeric: true }));
  let dates: { starts_at: string; ends_at: string } | null = null, problem = '';
  try { dates = { starts_at: baseline && start === ukDay(baseline.starts_at) ? baseline.starts_at : contentDateInstant(start), ends_at: baseline && end === ukDay(baseline.ends_at) ? baseline.ends_at : contentDateInstant(end) }; if (Date.parse(dates.starts_at) >= Date.parse(dates.ends_at)) problem = 'End must be after start.'; } catch (e) { problem = (e as Error).message; }
  if (!name.trim() || name.trim().length > 120) problem = 'Enter a schedule name (up to 120 characters).';
  else if (!selected.length) problem = 'Choose at least one promo.';
  const stage = useMutation({ mutationKey: contentActionKey, mutationFn: async () => {
    if (problem || !dates) throw new Error(problem);
    let targetCode = code, operation = 'update';
    const pending = await standaloneApi<ChangesetReply>('content/changeset');
    if (code) {
      operation = pending.changeset?.changes?.find(c => c.area === kind && c.key.code === code)?.operation === 'insert' ? 'insert' : 'update';
      const live = operation === 'insert' ? null : (await standaloneApi<{ definition: Timing }>(`${endpoint(kind)}/${encodeURIComponent(code)}`)).definition;
      if (JSON.stringify(effective(live, pending, kind, code)) !== JSON.stringify(baseline)) throw new Error('This schedule changed. Close and reopen the editor before saving.');
    } else {
      operation = 'insert';
      const existing = await standaloneApi<{ definitions: Row[] }>(endpoint(kind));
      const stem = `event_${name.trim().normalize('NFKD').replace(/[\u0300-\u036f]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/_+$/, '') || 'new'}`.slice(0, 65);
      const used = new Set([...existing.definitions.map(r => r.code), ...(pending.changeset?.changes?.filter(c => c.area === kind).map(c => String(c.key.code)) ?? [])]);
      targetCode = stem; for (let n = 2; used.has(targetCode); n++) targetCode = `${stem}_${n}`;
    }
    return standaloneApi('content/changeset/changes', { method: 'PUT', body: { area: kind, key: { code: targetCode }, operation,
      after: { name: name.trim(), enabled, ...dates, ...(kind === 'icon_rotation' ? { promo: selected[0] } : { promos: [...selected].sort() }) } } });
  }, onSuccess: async () => { await client.invalidateQueries(); toast('Rotation saved.', 'done'); onClose(); if (enabled) onStaged(selected); } });
  return <Dialog open wide title={title} description="Start and end dates use 7pm UK time. Unchanged dates preserve existing times. Saving updates this standalone rotation immediately."
    onOpenChange={open => { if (!open && !stage.isPending) onClose(); }} footer={<><Button disabled={stage.isPending} onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!!problem || stage.isPending} onClick={() => stage.mutate()}>{stage.isPending ? 'Saving…' : 'Save rotation'}</Button></>}>
    <fieldset className="event-editor" disabled={stage.isPending}>
      <Field label="Schedule name"><input maxLength={120} value={name} onChange={e => setName(e.target.value)} /></Field>
      <div className="event-editor-dates"><Field label="Start"><input type="date" value={start} onChange={e => setStart(e.target.value)} /></Field><Field label="End"><input type="date" value={end} onChange={e => setEnd(e.target.value)} /></Field></div>
      <label className="event-check"><input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} />Enabled</label>
      {kind === 'icon_rotation' ? <Field label="Icon tier"><select value={selected[0] ?? ''} onChange={e => setSelected([e.target.value])}><option value="">Choose a tier</option>{candidates.map(p => <option key={p.code} value={p.code}>{p.name}</option>)}</select></Field> : <>
        <Field label="Find promos"><input type="search" value={search} onChange={e => setSearch(e.target.value)} placeholder="Search promo names…" /></Field>
        <p className="dim">{selected.length} {selected.length === 1 ? 'promo' : 'promos'} selected</p>
        <div className="event-promo-picker">{candidates.filter(p => p.name.toLowerCase().includes(search.toLowerCase())).map(p => <label className="event-check" key={p.code}><input type="checkbox" checked={selected.includes(p.code)} onChange={e => setSelected(e.target.checked ? [...selected, p.code] : selected.filter(c => c !== p.code))} />{p.name}</label>)}</div>
      </>}
    </fieldset>
    {problem && <p className="dim">{problem}</p>}{stage.isError && <p className="error-box" role="alert">{stage.error.message}</p>}
  </Dialog>;
}
