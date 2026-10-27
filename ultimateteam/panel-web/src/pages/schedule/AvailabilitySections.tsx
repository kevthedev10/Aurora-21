import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { contentActionKey } from '../../api/content';
import { useSchedule, type ContentDrop, type ScheduleReply } from '../../api/schedule';
import { href } from '../../app/router';
import { Badge } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { EmptyState, ErrorPanel, Loading, Section } from '../../ui/Layout';
import { contentDateInstant, dateWindowProblem, shiftDay, ukDateLabel, ukDay } from './time';
import styles from './SchedulePage.module.css';

export type AvailabilityKind = 'store' | 'token' | 'sbc' | 'objective';
export interface AvailabilityOption { value: string; label: string; price?: number | undefined; untradeable?: boolean }
export interface AvailabilityOffer { target: string; price: number; untradeable: boolean }

export function ScheduledSection({ kind, options }: { kind: AvailabilityKind; options: AvailabilityOption[] }) {
  const schedule = useSchedule();
  const labels = new Map(options.map(option => [option.value, option.label]));
  const rows = schedule.data?.drops.filter(drop => ['scheduled', 'draft', 'failed'].includes(drop.status)).flatMap(drop =>
    drop.items.filter(item => item.area === 'content_window' && item.key.kind === kind).map(item => ({ drop, item })))
    .sort((a, b) => Date.parse(a.drop.startsAt) - Date.parse(b.drop.startsAt)) ?? [];
  return <Section title="Scheduled" text="Content with a dated release window. Drafts and failed releases need attention before they can go live." actions={<LinkButton size="sm" href={href('schedule')}>Content schedule</LinkButton>}>
    {schedule.isPending ? <Loading label="Loading scheduled content" /> : schedule.isError ? <ErrorPanel what="Scheduled content" error={schedule.error} onRetry={() => void schedule.refetch()} />
      : !rows.length ? <EmptyState title="No scheduled content" text="Choose content and set its start and end dates to schedule a release." />
      : <div className={styles.agenda}>{rows.map(({ drop, item }) => <article key={`${drop.id}-${item.id}`} className={`${styles.entry} ${styles.agendaEntry}`}>
        <h3>{labels.get(String(item.key.target)) ?? String(item.key.target)}</h3>
        <div className={styles.actions}><Badge tone={drop.status === 'failed' ? 'red' : drop.status === 'draft' ? 'staged' : 'neutral'}>{drop.status}</Badge><span>{drop.name}</span></div>
        <p>{ukDateLabel(drop.startsAt)} → {item.endsAt ? ukDateLabel(item.endsAt) : 'No end date'} · 7pm UK</p>
        {drop.error && <p className="error">{drop.error}</p>}
        <LinkButton size="sm" href={`${href('schedule')}?drop=${drop.id}`}>Review release</LinkButton>
      </article>)}</div>}
  </Section>;
}

export function AvailabilityDialog({ kind, options, onClose, currency, prepare }: {
  kind: AvailabilityKind; options: AvailabilityOption[]; onClose: () => void;
  currency?: 'Coins' | 'Aurora Tokens'; prepare?: (offer: AvailabilityOffer) => Promise<void>;
}) {
  const client = useQueryClient();
  const [target, setTarget] = useState(options[0]?.value ?? '');
  const [price, setPrice] = useState(String(options[0]?.price ?? ''));
  const [untradeable, setUntradeable] = useState(options[0]?.untradeable ?? kind === 'token');
  const today = ukDay(new Date().toISOString());
  const [start, setStart] = useState(today);
  const [end, setEnd] = useState(shiftDay(today, 7));
  const [draft, setDraft] = useState<ContentDrop | null>(null);
  const problem = !target ? 'Choose content.' : dateWindowProblem(start, end)
    ?? (currency && (!Number.isSafeInteger(Number(price)) || Number(price) < 1 || Number(price) > 99999999) ? 'Enter a positive whole-number price.' : null);
  const request = useMutation({ mutationKey: contentActionKey, mutationFn: async () => {
    if (problem) throw new Error(problem);
    const startsAt = contentDateInstant(start), endsAt = contentDateInstant(end);
    let current = await api<ScheduleReply>('content/schedule');
    if (Date.parse(endsAt) <= Date.parse(current.now)) throw new Error('Choose an end date in the future.');
    if (prepare) await prepare({ target, price: Number(price), untradeable });
    const previous = draft ? current.drops.find(row => row.id === draft.id) : null;
    if (draft && !previous) throw new Error('The saved release could not be found. Reopen the schedule to review it.');
    const owner = await api<ContentDrop>(`content/drops${previous ? `/${previous.id}` : ''}`, { method: previous ? 'PUT' : 'POST', body: {
      campaignId: null, name: options.find(option => option.value === target)?.label ?? target,
      startsAt, endsAt, inheritEnd: false, ...(previous ? { version: previous.version } : {}),
    } });
    setDraft(owner);
    await api(`content/changeset/changes?dropId=${owner.id}`, { method: 'PUT', body: {
      area: 'content_window', key: { kind, target }, operation: 'update', after: { enabled: true },
    } });
    current = await api<ScheduleReply>('content/schedule');
    const saved = current.drops.find(row => row.id === owner.id);
    if (!saved) throw new Error('The saved release could not be refreshed.');
    setDraft(saved);
    const action = Date.parse(saved.startsAt) <= Date.parse(current.now) ? 'publish' : 'schedule';
    const result = await api<ContentDrop>(`content/drops/${saved.id}/${action}`, { method: 'POST', body: { version: saved.version } });
    if (result.status === 'failed') throw new Error(result.error ?? 'This release failed validation.');
    await client.invalidateQueries();
  }, onSuccess: onClose });
  const busy = request.isPending;
  return <Dialog open wide title="Schedule content" description="Choose a start and end date. Dates use 7pm UK time. A window that has already started goes live when saved; future windows stay scheduled." onOpenChange={open => { if (!open && !busy) onClose(); }} footer={<>
    <Button disabled={busy} onClick={onClose}>Cancel</Button><Button variant="primary" disabled={busy || Boolean(problem)} aria-busy={busy} onClick={() => request.mutate()}>{busy ? <><span className={styles.spinner} aria-hidden="true" />Saving release…</> : 'Save release'}</Button>
  </>}>
    <div className={styles.form}>
      <Field label="Content"><select value={target} disabled={busy || draft !== null} onChange={event => {
        const next = options.find(option => option.value === event.target.value); setTarget(event.target.value);
        setPrice(String(next?.price ?? '')); setUntradeable(next?.untradeable ?? kind === 'token');
      }}>{options.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select></Field>
      {currency && <><Field label={`${currency} price`}><input type="number" min="1" max="99999999" value={price} disabled={busy} onChange={event => setPrice(event.target.value)} /></Field>
        <Field label="Tradeability"><select value={untradeable ? 'untradeable' : 'tradeable'} disabled={busy} onChange={event => setUntradeable(event.target.value === 'untradeable')}><option value="tradeable">Tradeable</option><option value="untradeable">Untradeable</option></select></Field></>}
      <Field label="Start date"><input type="date" value={start} disabled={busy} onChange={event => setStart(event.target.value)} /></Field>
      <Field label="End date"><input type="date" value={end} min={start ? shiftDay(start, 1) : undefined} disabled={busy} onChange={event => setEnd(event.target.value)} /></Field>
    </div>
    {problem && <p role="status">{problem}</p>}{request.error && <p className="error" role="alert">{request.error.message}</p>}
  </Dialog>;
}
