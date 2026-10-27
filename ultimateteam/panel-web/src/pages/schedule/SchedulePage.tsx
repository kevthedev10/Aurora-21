import { useEffect, useImperativeHandle, useRef, useState, type Ref } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { mergeCatalogue } from '../../api/packs';
import { useSbc, type SbcSetDef } from '../../api/sbc';
import { useObjectives, catalogueGroups, stagedObjectiveChanges, withStaged } from '../../api/objectives';
import type { ChangesetReply, PackCatalogReply } from '../../api/types';
import { contentActionKey, refusal, useContentBusy, type ValidationReply } from '../../api/content';
import { useSchedule, useScheduleActions, useSchedulePreview, scheduleKey, type Campaign, type ContentDrop, type DropStatus, type ScheduleReply } from '../../api/schedule';
import { href } from '../../app/router';
import { RotationsSection } from '../events/EventsPage';
import { NewPromoOddsDialog, type EventPromo } from '../packs/NewPromoOddsDialog';
import { isCampaignPromo } from '../../api/promoKinds';
import { Badge } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { ConfirmDialog, Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section, Toolbar } from '../../ui/Layout';
import { calendarDays, contentDateInstant, dateWindowProblem, shiftDay, ukDateLabel, ukDay, ukDisplay } from './time';
import styles from './SchedulePage.module.css';

const statuses: DropStatus[] = ['draft', 'scheduled', 'live', 'ended', 'cancelled', 'failed'];
const areaLabels: Record<string, string> = { promo: 'Promos', promo_event: 'Promos', icon_rotation: 'Promos', pack: 'Store packs', pack_odds: 'Store packs', store_pack: 'Store packs', store: 'Store packs', sbc: 'SBCs', objective_group: 'Objectives', season: 'Seasons', rules: 'Rules', card: 'Cards' };
function contentType(area: string, key: Record<string, unknown>) {
  if (area === 'content_window') return key.kind === 'token' ? 'Token Store packs' : key.kind === 'store' ? 'Store packs' : key.kind === 'sbc' ? 'SBCs' : key.kind === 'objective' ? 'Objectives' : 'Availability';
  if (area !== 'rules') return areaLabels[area] ?? area;
  const target = `${key.file ?? ''} ${key.path ?? ''}`;
  return /season/i.test(target) ? 'Seasons' : /objective/i.test(target) ? 'Objectives' : /sbc/i.test(target) ? 'SBCs' : /store/i.test(target) ? 'Store packs' : 'Rules';
}
interface AgendaEntry { id: string; name: string; at: string; end: boolean; status: DropStatus; types: string[]; campaignId: number | null; drop: ContentDrop | null; legacy: boolean }
export function agendaEntries(data: ScheduleReply): AgendaEntry[] {
  return [
    ...data.drops.flatMap(drop => {
      const base = { name: drop.name, status: drop.status, types: [...new Set(drop.items.map(item => contentType(item.area, item.key)))], campaignId: drop.campaignId, drop, legacy: false };
      return [{ ...base, id: `drop-${drop.id}-start`, at: drop.startsAt, end: false }, ...(drop.endsAt ? [{ ...base, id: `drop-${drop.id}-end`, at: drop.endsAt, end: true }] : [])];
    }),
    ...data.legacy.flatMap(entry => {
      const base = { name: entry.name, status: (!entry.enabled ? 'cancelled' : Date.parse(data.now) >= Date.parse(entry.endsAt) ? 'ended' : Date.parse(data.now) >= Date.parse(entry.startsAt) ? 'live' : 'scheduled') as DropStatus,
        types: [entry.kind === 'season' ? 'Seasons' : entry.kind === 'icon_rotation' ? 'Icons' : entry.rotationType === 'totw' ? 'TOTW' : 'Promos'], campaignId: null, drop: null, legacy: true };
      return [{ ...base, id: `legacy-${entry.kind}-${entry.code}-start`, at: entry.startsAt, end: false }, { ...base, id: `legacy-${entry.kind}-${entry.code}-end`, at: entry.endsAt, end: true }];
    }),
  ].sort((a, b) => Date.parse(a.at) - Date.parse(b.at) || Number(a.end) - Number(b.end) || a.id.localeCompare(b.id));
}

export function SchedulePage() {
  const schedule = useSchedule();
  const actions = useScheduleActions();
  const [campaignToDelete, setCampaignToDelete] = useState<Campaign | null>(null);
  const [day, setDay] = useState(() => ukDay(new Date().toISOString()));
  const [view, setView] = useState<'month' | 'week'>('month');
  const [campaign, setCampaign] = useState('');
  const [campaignToView, setCampaignToView] = useState<number | null>(null);
  const [dropPage, setDropPage] = useState(0);
  const [type, setType] = useState('');
  const [status, setStatus] = useState('');
  const [campaignEdit, setCampaignEdit] = useState<Campaign | 'new' | null>(null);
  const [dropEdit, setDropEdit] = useState<ContentDrop | 'new' | number | null>(() => Number(new URLSearchParams(window.location.hash.split('?')[1]).get('drop')) || null);
  const [section, setSection] = useState<'campaigns' | 'rotations'>(() => new URLSearchParams(window.location.hash.split('?')[1]).get('section') === 'rotations' ? 'rotations' : 'campaigns');
  const [rotationFocus, setRotationFocus] = useState<{ kind: 'promo_event' | 'icon_rotation'; code: string } | null>(null);
  const [previewAt, setPreviewAt] = useState<string | null>(null);
  const preview = useSchedulePreview(previewAt);
  if (schedule.isError) return <ErrorPanel what="The schedule" error={schedule.error} onRetry={() => void schedule.refetch()} />;
  if (!schedule.data) return <Loading label="Loading schedule" />;
  const data = schedule.data;
  const now = Date.parse(data.now);
  const campaigns = [...data.campaigns].sort((a, b) => Date.parse(a.startsAt ?? '9999-01-01') - Date.parse(b.startsAt ?? '9999-01-01') || a.id - b.id);
  const activeCampaign = campaigns.filter(row => row.startsAt && row.endsAt && Date.parse(row.startsAt) <= now && now < Date.parse(row.endsAt)).at(-1);
  const defaultCampaign = activeCampaign ?? campaigns.find(row => row.startsAt && Date.parse(row.startsAt) > now) ?? campaigns.at(-1);
  const shownCampaign = campaigns.find(row => row.id === campaignToView) ?? defaultCampaign;
  const campaignIndex = shownCampaign ? campaigns.findIndex(row => row.id === shownCampaign.id) : -1;
  const campaignDrops = data.drops.filter(drop => drop.campaignId === shownCampaign?.id).sort((a, b) => Date.parse(a.startsAt) - Date.parse(b.startsAt) || a.id - b.id);
  const dropPages = Math.max(1, Math.ceil(campaignDrops.length / 5));
  const shownDropPage = Math.min(dropPage, dropPages - 1);
  const viewCampaign = (id: number) => { setCampaignToView(id); setDropPage(0); };
  const entries = agendaEntries(data).filter(entry => (!campaign || entry.campaignId === Number(campaign)) && (!type || entry.types.includes(type)) && (!status || entry.status === status));
  const today = ukDay(data.now);
  const days = calendarDays(day, view);
  const agenda = entries.filter(entry => ukDay(entry.at) === day);
  const advance = (direction: number) => {
    if (view === 'week') setDay(shiftDay(day, direction * 7));
    else { const date = new Date(`${day.slice(0, 7)}-01T12:00:00Z`); date.setUTCMonth(date.getUTCMonth() + direction); setDay(date.toISOString().slice(0, 10)); }
  };
  return <>
    <PageHeader title="Content schedule" description="Plan campaign promo drops and manage standalone TOTW, Icon and Standard rotations. New dates use 7pm UK time." actions={<Button variant="primary" onClick={() => setCampaignEdit('new')}>New campaign</Button>} />
    <nav className={styles.sectionTabs} aria-label="Schedule sections">
      <Button variant={section === 'campaigns' ? 'primary' : 'ghost'} aria-pressed={section === 'campaigns'} onClick={() => setSection('campaigns')}>Campaigns &amp; drops</Button>
      <Button variant={section === 'rotations' ? 'primary' : 'ghost'} aria-pressed={section === 'rotations'} onClick={() => setSection('rotations')}>Rotations</Button>
    </nav>
    {section === 'campaigns' && <>
    <Section className={styles.campaignSection!} title="Campaigns" text="Give each campaign a start and end date, then add promo drops within those dates. All dates begin or end at 7pm UK time." actions={shownCampaign && <div className={styles.campaignNavigation}>
      <Button size="sm" disabled={campaignIndex <= 0} onClick={() => viewCampaign(campaigns[campaignIndex - 1]!.id)}>Previous campaign</Button>
      <Field label="Campaign to view" inline><select value={shownCampaign.id} onChange={event => viewCampaign(Number(event.target.value))}>{campaigns.map(row => <option key={row.id} value={row.id}>{row.name}</option>)}</select></Field>
      <Button size="sm" disabled={campaignIndex >= campaigns.length - 1} onClick={() => viewCampaign(campaigns[campaignIndex + 1]!.id)}>Next campaign</Button>
      <Button size="sm" variant="primary" disabled={!activeCampaign} title={activeCampaign ? activeCampaign.name : 'No campaign is currently active'} onClick={() => { if (activeCampaign) viewCampaign(activeCampaign.id); }}>Go to active campaign</Button>
    </div>}>
      {!data.campaigns.length && <EmptyState title="Create your first campaign" text="For example, Halloween with Team 1, daily content and Team 2." />}
      <div className={styles.campaigns}>{(shownCampaign ? [shownCampaign] : []).map(row => <article key={row.id} className={styles.entry}><h3>{row.name}</h3>
        <p>{row.startsAt && row.endsAt ? `${ukDateLabel(row.startsAt)} → ${ukDateLabel(row.endsAt)} · 7pm UK` : 'Needs start and end dates'}</p>
        <div className={styles.actions}><Button variant="primary" size="sm" disabled={!row.startsAt || !row.endsAt} onClick={() => { setCampaign(String(row.id)); setDropEdit('new'); }}>Add drop</Button><Button size="sm" onClick={() => setCampaign(String(row.id))}>Show in calendar</Button><Button size="sm" onClick={() => setCampaignEdit(row)}>Edit campaign</Button>{!(row.startsAt && row.endsAt && Date.parse(row.startsAt) <= now && now < Date.parse(row.endsAt)) && !data.drops.some(drop => drop.campaignId === row.id && drop.status === 'live') && <Button size="sm" variant="danger" onClick={() => setCampaignToDelete(row)}>Delete campaign</Button>}</div>
        <ul className={styles.campaignDrops}>{campaignDrops.slice(shownDropPage * 5, (shownDropPage + 1) * 5).map(drop => <li key={drop.id} className={styles.campaignDrop}><div className={styles.dropHeading}><Button size="sm" variant="ghost" onClick={() => setDropEdit(drop)}>{drop.name}</Button><Badge>{drop.status}</Badge></div><small className={styles.dropDates}>{ukDateLabel(drop.startsAt)} → {drop.endsAt ? ukDateLabel(drop.endsAt) : 'Needs an end date'}</small></li>)}</ul>
        {!campaignDrops.length && <p>No drops yet. Add the first drop to this campaign.</p>}
        {dropPages > 1 && <nav className={styles.dropPagination} aria-label="Campaign drops pages">
          <Button size="sm" disabled={shownDropPage === 0} onClick={() => setDropPage(shownDropPage - 1)}>Previous drops</Button>
          <small role="status">Drops {shownDropPage * 5 + 1}–{Math.min((shownDropPage + 1) * 5, campaignDrops.length)} of {campaignDrops.length}</small>
          <Button size="sm" disabled={shownDropPage === dropPages - 1} onClick={() => setDropPage(shownDropPage + 1)}>Next drops</Button>
        </nav>}
      </article>)}</div>
    </Section>
    <Toolbar>
      <Field label="Campaign"><select value={campaign} onChange={event => setCampaign(event.target.value)}><option value="">All campaigns</option>{data.campaigns.map(row => <option key={row.id} value={row.id}>{row.name}</option>)}</select></Field>
      <Field label="Content"><select value={type} onChange={event => setType(event.target.value)}><option value="">All content</option>{['Promos', 'TOTW', 'Icons', 'Store packs', 'Token Store packs', 'SBCs', 'Objectives', 'Seasons'].map(label => <option key={label}>{label}</option>)}</select></Field>
      <Field label="Status"><select value={status} onChange={event => setStatus(event.target.value)}><option value="">All statuses</option>{statuses.map(value => <option key={value} value={value}>{value}</option>)}</select></Field>
    </Toolbar>
    <div className={styles.workspace}>
      <Section className={styles.panel!} title={new Intl.DateTimeFormat('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${day}T12:00:00Z`))} actions={<div className={styles.actions}><Button size="sm" aria-label="Previous period" onClick={() => advance(-1)}>←</Button><Button size="sm" onClick={() => setDay(today)}>Today</Button><Button size="sm" aria-label="Next period" onClick={() => advance(1)}>→</Button><Field label="View" inline><select aria-label="Calendar view" value={view} onChange={event => setView(event.target.value as 'month' | 'week')}><option value="month">Month</option><option value="week">Week</option></select></Field></div>}>
        <div className={styles.calendar} role="group" aria-label="Content calendar">
          {['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map(label => <div className={styles.weekday} key={label}>{label}</div>)}
          {days.map(date => {
            const matches = entries.filter(entry => ukDay(entry.at) === date);
            return <button type="button" key={date} className={styles.day} data-selected={date === day || undefined} data-today={date === today || undefined} data-other-month={date.slice(0, 7) !== day.slice(0, 7) || undefined} aria-pressed={date === day} aria-label={`${date}, ${matches.length} starts and expiries`} onClick={() => setDay(date)}>
              <strong>{Number(date.slice(-2))}</strong>
              {matches.slice(0, 3).map(entry => <span key={entry.id} data-end={entry.end || undefined}>{entry.end ? '↘ ' : '↗ '}{entry.name}</span>)}
              {matches.length > 3 && <small>+{matches.length - 3} more</small>}
            </button>;
          })}
        </div>
      </Section>
      <Section className={styles.panel!} title={`Agenda · ${day}`} actions={<Button size="sm" onClick={() => setPreviewAt(contentDateInstant(day))}>Preview at 7pm</Button>}>
        {!agenda.length && <EmptyState title="No starts or expiries" text="Add a drop to a campaign, or change the filters." />}
        <div className={styles.agenda}>
          {agenda.map(entry => <article key={entry.id} className={`${styles.entry} ${styles.agendaEntry}`} data-end={entry.end || undefined}>
            <div className={styles.actions}><Badge tone={entry.status === 'live' ? 'pitch' : entry.status === 'failed' ? 'red' : entry.status === 'draft' ? 'staged' : 'neutral'}>{entry.status}</Badge><small>{entry.end ? 'Expires' : 'Starts'} · {ukDisplay(entry.at)}</small></div>
            <h3>{entry.name}</h3><p>{entry.types.join(' · ') || 'No content yet'}{entry.drop && ` · ${entry.drop.changes} changes`}</p>
            {entry.campaignId && <small>{data.campaigns.find(row => row.id === entry.campaignId)?.name}</small>}
            {entry.drop?.error && <p className="error" role="alert">{entry.drop.error}</p>}
            {entry.drop ? <Button size="sm" onClick={() => setDropEdit(entry.drop!)}>Review drop</Button> : entry.types.includes('Seasons') ? <LinkButton size="sm" href={href('objectives/seasons')}>Existing season</LinkButton> : <Button size="sm" onClick={() => {
              const row = data.legacy.find(row => `legacy-${row.kind}-${row.code}-${entry.end ? 'end' : 'start'}` === entry.id);
              if (row && row.kind !== 'season') { setRotationFocus({ kind: row.kind, code: row.code }); setSection('rotations'); }
            }}>Edit rotation</Button>}
          </article>)}
        </div>
      </Section>
    </div>
    </>}
    {section === 'rotations' && <div id="schedule-rotations" className={styles.rotations}><RotationsSection focus={rotationFocus} onFocusHandled={() => setRotationFocus(null)} /></div>}
    <ConfirmDialog open={campaignToDelete !== null} onOpenChange={open => { if (!open) setCampaignToDelete(null); }} title={`Delete ${campaignToDelete?.name ?? 'campaign'}?`}
      text="This removes the campaign and its unpublished drops. Completed release history and content definitions are preserved. Live campaigns cannot be deleted."
      confirmLabel="Delete campaign" variant="danger" onConfirm={async () => {
        if (!campaignToDelete) return;
        await actions.deleteCampaign.mutateAsync(campaignToDelete);
        if (campaign === String(campaignToDelete.id)) setCampaign('');
        setCampaignToView(null); setDropPage(0);
      }} />
    {campaignEdit && <CampaignEditor campaign={campaignEdit === 'new' ? null : campaignEdit} day={day} onSaved={saved => viewCampaign(saved.id)} onClose={() => setCampaignEdit(null)} />}
    {dropEdit && (dropEdit === 'new' || typeof dropEdit !== 'number' || data.drops.some(row => row.id === dropEdit)) && <DropEditor drop={dropEdit === 'new' ? null : typeof dropEdit === 'number' ? data.drops.find(row => row.id === dropEdit)! : data.drops.find(row => row.id === dropEdit.id) ?? dropEdit} data={data} day={day} defaultCampaign={campaign ? Number(campaign) : null} onClose={() => setDropEdit(null)} />}
    <Dialog open={previewAt !== null} onOpenChange={open => { if (!open) setPreviewAt(null); }} title="Effective content preview" description={previewAt ? ukDisplay(previewAt) : undefined}>
      {preview.isPending && <Loading label="Building preview" />}{preview.isError && <ErrorPanel what="The preview" error={preview.error} />}
      {preview.data && <><p>Projected catalogue revision {preview.data.revision}. Draft and cancelled drops are excluded.</p><dl className={styles.preview}><dt>Available promos</dt><dd>{preview.data.promos.filter(promo => promo.generallyAvailable).map(promo => `${promo.name} (${promo.cards} cards)`).join(', ') || 'None'}</dd><dt>Store pack IDs</dt><dd>{preview.data.storePackIds.join(', ') || 'None'}</dd><dt>SBCs</dt><dd>{preview.data.sbcKeys.join(', ') || 'None'}</dd><dt>Objective groups</dt><dd>{preview.data.objectiveGroupIds.join(', ') || 'None'}</dd><dt>Season</dt><dd>{preview.data.seasonId ?? 'None'}</dd></dl></>}
    </Dialog>
  </>;
}

function CampaignEditor({ campaign, day, onSaved, onClose }: { campaign: Campaign | null; day: string; onSaved: (saved: Campaign) => void; onClose: () => void }) {
  const actions = useScheduleActions();
  const [name, setName] = useState(campaign?.name ?? '');
  const [start, setStart] = useState(campaign ? campaign.startsAt ? ukDay(campaign.startsAt) : '' : day);
  const [end, setEnd] = useState(campaign?.endsAt ? ukDay(campaign.endsAt) : shiftDay(day, 7));
  const [error, setError] = useState<string | null>(null);
  const problem = dateWindowProblem(start, end);
  const save = async () => {
    try { setError(null); if (problem) throw new Error(problem); const saved = await actions.campaign.mutateAsync({ name, startDate: start, endDate: end, ...(campaign ? { id: campaign.id, version: campaign.version } : {}) }); onSaved(saved); onClose(); }
    catch (caught) { setError(caught instanceof Error ? caught.message : String(caught)); }
  };
  return <Dialog open onOpenChange={open => { if (!open) onClose(); }} title={campaign ? 'Edit campaign' : 'New campaign'} description="Choose the campaign dates. It starts and ends at 7pm UK time; add its drops after saving." footer={<Button variant="primary" disabled={!name.trim() || Boolean(problem) || actions.campaign.isPending} onClick={() => void save()}>Save campaign</Button>}>
    <Field label="Name"><input value={name} maxLength={160} onChange={event => setName(event.target.value)} /></Field>
    <Field label="Start date"><input type="date" value={start} onChange={event => setStart(event.target.value)} /></Field>
    <Field label="End date"><input type="date" value={end} min={start ? shiftDay(start, 1) : undefined} onChange={event => setEnd(event.target.value)} /></Field>
    {campaign && <p>Keep the campaign dates around all its existing drops.</p>}
    {problem && <p role="status">{problem}</p>}
    {error && <p className="error" role="alert">{error}</p>}
  </Dialog>;
}

function DropEditor({ drop: initialDrop, data, day, defaultCampaign, onClose }: { drop: ContentDrop | null; data: ScheduleReply; day: string; defaultCampaign: number | null; onClose: () => void }) {
  const actions = useScheduleActions();
  const [created, setCreated] = useState<ContentDrop | null>(null);
  const refreshedDrop = data.drops.find(row => row.id === (created ?? initialDrop)?.id);
  const drop = created ? refreshedDrop && refreshedDrop.version >= created.version ? refreshedDrop : created : refreshedDrop ?? initialDrop;
  const contentBusy = useContentBusy();
  const [scheduling, setScheduling] = useState(false);
  const [saving, setSaving] = useState(false);
  const [hasSelectedContent, setHasSelectedContent] = useState(false);
  const attachment = useRef<AttachmentHandle>(null);
  const [name, setName] = useState(drop?.name ?? '');
  const [campaignId, setCampaignId] = useState(drop ? drop.campaignId : defaultCampaign);
  const standalone = initialDrop?.campaignId === null;
  const selectedCampaign = data.campaigns.find(row => row.id === campaignId);
  const savedCampaign = data.campaigns.find(row => row.id === drop?.campaignId);
  const [start, setStart] = useState(drop ? ukDay(drop.startsAt) : selectedCampaign?.startsAt ? ukDay(selectedCampaign.startsAt) : day);
  const [end, setEnd] = useState(drop?.endsAt ? ukDay(drop.endsAt) : selectedCampaign?.endsAt ? ukDay(selectedCampaign.endsAt) : shiftDay(day, 7));
  const [error, setError] = useState<string | null>(null);
  const [report, setReport] = useState<ValidationReply | null>(null);
  const [confirm, setConfirm] = useState<'cancel' | 'publish' | 'end' | null>(null);
  const editable = !drop || ['draft', 'scheduled', 'failed'].includes(drop.status);
  const busy = saving || scheduling || contentBusy || actions.drop.isPending || actions.transition.isPending;
  const problem = standalone ? dateWindowProblem(start, end) : !selectedCampaign ? 'Choose a campaign for this drop.'
    : !selectedCampaign.startsAt || !selectedCampaign.endsAt ? 'Set the campaign start and end dates first.'
    : dateWindowProblem(start, end) ?? (start < ukDay(selectedCampaign.startsAt) || end > ukDay(selectedCampaign.endsAt) ? 'The drop must stay within its campaign dates.' : null);
  const run = async () => {
    setScheduling(true);
    try {
      setError(null); setReport(null);
      const selected = await attachment.current?.saveSelection();
      const saved = selected ?? await ensureDrop();
      if (!selected) await attachment.current?.ensurePackOdds(saved);
      if (saved.changes === 0) throw new Error('Choose content for this drop before scheduling.');
      setCreated(saved);
      await actions.transition.mutateAsync({ drop: saved, action: standalone && Date.parse(saved.startsAt) <= Date.parse(data.now) ? 'publish' : 'schedule' }); onClose();
    }
    catch (caught) { setReport(refusal(caught)); }
    finally { setScheduling(false); }
  };
  const persist = async () => {
    if (problem || !standalone && campaignId === null || !name.trim()) throw new Error(problem ?? 'Enter a drop name.');
    const saved = await actions.drop.mutateAsync({ name, campaignId, startDate: start, endDate: end, inheritEnd: false, ...(drop ? { id: drop.id, version: drop.version } : {}) });
    setCreated(saved);
    return saved;
  };
  const ensureDrop = async () => {
    if (drop && drop.name === name && drop.campaignId === campaignId && ukDay(drop.startsAt) === start && drop.endsAt && ukDay(drop.endsAt) === end) return drop;
    return persist();
  };
  const save = async () => {
    setSaving(true);
    try {
      setError(null); setReport(null);
      const attached = await attachment.current?.saveSelection();
      if (attached) setCreated(attached); else await persist();
    }
    catch (caught) { setError(caught instanceof Error ? caught.message : String(caught)); }
    finally { setSaving(false); }
  };
  return <>
    <Dialog open onOpenChange={open => { if (!open && !busy) onClose(); }} wide title={drop ? drop.name : 'New promo drop'} description={standalone ? "This standalone release uses 7pm UK dates. Review its content and dates here." : "Choose this promo drop’s dates within the campaign. Both dates use 7pm UK time. Add content here, then schedule the drop. Adding content saves the drop details automatically."} footer={<>
      {editable && <Button disabled={busy || !name.trim() || Boolean(problem)} aria-busy={saving} onClick={() => void save()}>{saving ? <><span className={styles.spinner} aria-hidden="true" />Saving…</> : 'Save drop'}</Button>}
      {(!drop || ['draft', 'failed'].includes(drop.status)) && <Button variant="primary" disabled={busy || !name.trim() || Boolean(problem) || (!hasSelectedContent && !drop?.changes)} aria-busy={scheduling} onClick={() => void run()}>{scheduling ? <><span className={styles.spinner} aria-hidden="true" />Scheduling…</> : 'Schedule drop'}</Button>}
      {drop && !savedCampaign?.startsAt && Date.parse(drop.startsAt) <= Date.parse(data.now) && ['draft', 'scheduled', 'failed'].includes(drop.status) && <Button disabled={busy} onClick={() => setConfirm('publish')}>Publish saved draft now</Button>}
      {drop && ['draft', 'scheduled', 'failed'].includes(drop.status) && <Button disabled={busy} onClick={() => setConfirm('cancel')}>Cancel drop</Button>}
      {drop?.status === 'live' && <Button disabled={busy} onClick={() => setConfirm('end')}>End now</Button>}
    </>}>
      <div className={styles.editorBody}>
      {drop && <p><Badge tone={drop.status === 'live' ? 'pitch' : drop.status === 'failed' ? 'red' : 'staged'}>{drop.status}</Badge> · {drop.changes} saved changes{drop.error && <span className="error"> · {drop.error}</span>}</p>}
      <div className={styles.form}>
        <Field label="Drop name"><input value={name} disabled={!editable || busy} maxLength={160} onChange={event => setName(event.target.value)} /></Field>
        {!standalone && <Field label="Campaign"><select value={campaignId ?? ''} disabled={!editable || busy} onChange={event => { const next = data.campaigns.find(row => row.id === Number(event.target.value)); setCampaignId(next?.id ?? null); if (next?.startsAt && next.endsAt) { setStart(ukDay(next.startsAt)); setEnd(ukDay(next.endsAt)); } }}><option value="">Choose campaign</option>{data.campaigns.map(row => <option key={row.id} value={row.id}>{row.name}</option>)}</select></Field>}
        <Field label="Start date"><input type="date" value={start} min={selectedCampaign?.startsAt ? ukDay(selectedCampaign.startsAt) : undefined} max={selectedCampaign?.endsAt ? ukDay(selectedCampaign.endsAt) : undefined} disabled={!editable || busy} onChange={event => setStart(event.target.value)} /></Field>
        <Field label="End date"><input type="date" value={end} min={start ? shiftDay(start, 1) : undefined} max={selectedCampaign?.endsAt ? ukDay(selectedCampaign.endsAt) : undefined} disabled={!editable || busy} onChange={event => setEnd(event.target.value)} /></Field>
      </div>
      {problem && <p role="status">{problem}</p>}
      {drop?.status === 'scheduled' && <p>Saving edits returns this drop to Draft. Schedule it again after reviewing the changes.</p>}
      {(!drop || ['draft', 'failed'].includes(drop.status)) && <Section title="Drop content">
        <AvailabilityAttachment drop={drop} ensureDrop={ensureDrop} attachmentRef={attachment} onSelectionChange={setHasSelectedContent} onAttached={() => { setReport(null); setError(null); }} disabled={busy || !name.trim() || Boolean(problem)} />
        <p>Content definitions save live in their editors. Create or edit <a href={href('promos')}>promos</a>, <a href={href('packs')}>packs / store</a>, <a href={href('sbc')}>SBCs</a> or <a href={href('objectives')}>objectives / seasons</a>, then assign them here. TOTW, Icons and the Standard pool are managed separately under Rotations.</p>
      </Section>}
      {drop?.items.length ? <div className={styles.agenda}>{drop.items.map(item => <ItemExpiry key={item.id} drop={drop} item={item} editable={editable} />)}</div> : <p>{hasSelectedContent ? 'Selected content will be included when you save or schedule this drop.' : 'Choose content above to add to this drop.'}</p>}
      {report && <div role="status">{report.ok ? <p>Validation passed for {report.changes ?? drop?.changes} changes.</p> : <p className="error">This drop is not ready.</p>}{report.errors?.map((message, index) => <p className="error" key={index}>{message}</p>)}{Boolean(report.conflicts?.length) && <p className="error">{report.conflicts!.length} content conflicts need review in Changes.</p>}</div>}
      {error && <p className="error" role="alert">{error}</p>}
      </div>
    </Dialog>
    <ConfirmDialog open={confirm !== null} onOpenChange={open => { if (!open) setConfirm(null); }} title={confirm === 'publish' ? 'Publish this saved drop now?' : drop?.status === 'live' ? 'End this drop’s availability?' : 'Cancel this drop?'} text={confirm === 'publish' ? 'The saved content will become live immediately. Unsaved form values are excluded.' : 'Availability windows end; definitions, owned items and player progress are preserved.'} confirmLabel={confirm === 'publish' ? 'Publish now' : drop?.status === 'live' ? 'End availability' : 'Cancel drop'} onConfirm={async () => { if (drop && confirm) { await actions.transition.mutateAsync({ drop, action: confirm }); onClose(); } }} />
  </>;
}

interface AttachmentHandle { saveSelection: () => Promise<ContentDrop | null>; ensurePackOdds: (owner: ContentDrop) => Promise<void> }
function AvailabilityAttachment({ drop, ensureDrop, disabled, attachmentRef, onSelectionChange, onAttached }: { drop: ContentDrop | null; ensureDrop: () => Promise<ContentDrop>; disabled: boolean; attachmentRef: Ref<AttachmentHandle>; onSelectionChange: (selected: boolean) => void; onAttached: () => void }) {
  const client = useQueryClient();
  const promos = useQuery({ queryKey: ['schedule-promo-options', drop?.id ?? null], queryFn: () => api<{ definitions: { code: string; name: string; cards: number; category?: string }[] }>(drop ? `promos?dropId=${drop.id}` : 'promos') });
  const packs = useQuery({ queryKey: ['schedule-pack-options'], queryFn: () => api<PackCatalogReply>('packs/catalog') });
  const staged = useQuery({ queryKey: ['schedule-attachment-changes', drop?.id ?? null], enabled: drop !== null, queryFn: () => api<ChangesetReply>(`content/changeset?dropId=${drop!.id}`) });
  const sbcs = useSbc();
  const objectives = useObjectives();
  const [kind, setKind] = useState<'promo' | 'store' | 'token' | 'sbc' | 'objective'>('promo');
  const [target, setTarget] = useState('');
  const [oddsFor, setOddsFor] = useState<EventPromo[] | null>(null);
  const finishOdds = useRef<((completed: boolean) => void) | null>(null);
  useEffect(() => () => finishOdds.current?.(false), []);
  const ensurePromoOdds = async (codes: string[]) => {
    if (!codes.length) return;
    const definitions = promos.data?.definitions ?? [];
    const required = [...new Set(codes)].map(code => {
      const promo = definitions.find(row => row.code === code);
      return { code, name: promo?.name ?? code, category: promo?.category ?? code };
    });
    const completed = await new Promise<boolean>(resolve => { finishOdds.current = resolve; setOddsFor(required); });
    if (!completed) throw new Error('Promo added to the draft. Set its pack odds before scheduling.');
  };
  const ensurePackOdds = async (owner: ContentDrop) => {
    const pending = await api<ChangesetReply>(`content/changeset?dropId=${owner.id}`);
    const codes = pending.changeset?.changes?.filter(change => change.area === 'promo_event').flatMap(change => {
      const after = change.after as { promos?: string[] } | null;
      return after?.promos ?? [];
    }) ?? [];
    await ensurePromoOdds(codes);
  };
  const packOptions = packs.data ? mergeCatalogue(packs.data, staged.data).packs : [];
  const sbcOptions = new Map((sbcs.data?.sets ?? []).map(row => [row.key, row.definition]));
  for (const change of staged.data?.changeset?.changes ?? []) {
    if (change.area !== 'sbc_set') continue;
    const key = String(change.key.setKey);
    if (change.operation === 'hide') sbcOptions.delete(key);
    else if (change.after && typeof change.after === 'object' && 'definition' in change.after) sbcOptions.set(key, change.after.definition as SbcSetDef);
  }
  const objectiveOptions = objectives.data ? catalogueGroups(withStaged(objectives.data.rules, stagedObjectiveChanges(staged.data?.changeset?.changes, objectives.data.settingsFile))) : [];
  const options = kind === 'promo' ? (promos.data?.definitions ?? []).filter(row => row.cards > 0 && isCampaignPromo(row)).map(row => ({ value: row.code, label: row.name }))
    : kind === 'token' ? packOptions.filter(row => packs.data?.tokenListing?.some(offer => offer.packId === row.id && (offer.price ?? 0) > 0)).map(row => ({ value: String(row.id), label: row.description ?? `Pack ${row.id}` }))
    : kind === 'store' ? packOptions.filter(row => !row.grantOnly && row.pending !== 'Delete').map(row => ({ value: String(row.id), label: row.description ?? `Pack ${row.id}` }))
    : kind === 'sbc' ? [...sbcOptions].map(([key, definition]) => ({ value: key, label: definition.name ?? key }))
    : objectiveOptions.map(row => ({ value: String(row.id), label: row.title }));
  const request = useMutation({ mutationKey: contentActionKey, mutationFn: async () => {
    const owner = await ensureDrop();
    const body = kind === 'promo'
      ? { area: 'promo_event', key: { code: `drop_${owner.id}_${target}` }, operation: 'insert', after: { name: `${owner.name} · ${options.find(row => row.value === target)?.label ?? target}`, enabled: true, starts_at: owner.startsAt, ends_at: owner.endsAt ?? '9999-12-31T23:59:59Z', promos: [target] } }
      : { area: 'content_window', key: { kind, target }, operation: 'update', after: { enabled: true } };
    await api(`content/changeset/changes?dropId=${owner.id}`, { method: 'PUT', body });
    await Promise.all([client.invalidateQueries({ queryKey: scheduleKey }), client.invalidateQueries({ queryKey: ['schedule-attachment-changes'] })]);
    if (kind === 'promo') await ensurePromoOdds([target]);
    return owner;
  }, onSuccess: async () => { setTarget(''); onAttached(); await Promise.all([client.invalidateQueries({ queryKey: scheduleKey }), client.invalidateQueries({ queryKey: ['schedule-attachment-changes'] })]); } });
  useEffect(() => onSelectionChange(Boolean(target)), [target, onSelectionChange]);
  useImperativeHandle(attachmentRef, () => ({ ensurePackOdds, saveSelection: async () => {
    if (!target) return null;
    if (loadError) throw loadError;
    const owner = await request.mutateAsync();
    // Attaching increments the drop version. Read it before scheduling, rather than using the old render's version.
    const current = await api<ScheduleReply>('content/schedule');
    client.setQueryData(scheduleKey, current);
    const saved = current.drops.find(row => row.id === owner.id);
    if (!saved) throw new Error('Content was added, but the drop could not be refreshed. Reopen it to review.');
    return saved;
  } }));
  const loadError = staged.error ?? (kind === 'promo' ? promos.error : kind === 'store' || kind === 'token' ? packs.error : kind === 'sbc' ? sbcs.error : objectives.error);
  return <><div className={styles.attach}>
    <p>Attach existing content for this drop’s availability window. Use the specialist editors to create or edit definitions.</p>
    <div className={styles.form}><Field label="Content kind"><select value={kind} disabled={disabled || request.isPending} onChange={event => { setKind(event.target.value as typeof kind); setTarget(''); request.reset(); }}><option value="promo">Promo</option><option value="store">Store pack</option><option value="token">Token store pack</option><option value="sbc">SBC set</option><option value="objective">Objective group</option></select></Field>
      <Field label="Content"><select value={target} disabled={disabled || request.isPending || Boolean(loadError)} onChange={event => setTarget(event.target.value)}><option value="">Select content…</option>{options.map(row => <option value={row.value} key={row.value}>{row.label}</option>)}</select></Field></div>
    <Button size="sm" disabled={disabled || !target || request.isPending || Boolean(loadError)} onClick={() => request.mutate()}>Add to drop</Button>
    {(loadError || request.error) && <p className="error" role="alert">{(loadError ?? request.error)?.message}</p>}
    {request.isSuccess && <p role="status">Content attached to this draft.</p>}
  </div>
  {oddsFor && <NewPromoOddsDialog destination="immediate" promos={oddsFor} onClose={completed => {
    setOddsFor(null); const finish = finishOdds.current; finishOdds.current = null; finish?.(completed === true);
  }} />}</>;
}

function ItemExpiry({ drop, item, editable }: { drop: ContentDrop; item: ContentDrop['items'][number]; editable: boolean }) {
  const client = useQueryClient();
  const [end, setEnd] = useState(item.endsAt ? ukDay(item.endsAt) : '');
  const availability = ['promo_event', 'icon_rotation', 'content_window'].includes(item.area);
  const save = useMutation({ mutationKey: contentActionKey, mutationFn: () => api<ContentDrop>(`content/drops/${drop.id}/items/${item.id}/end`, { method: 'PUT', body: { version: drop.version, endsAt: end ? contentDateInstant(end) : null } }), onSuccess: () => client.invalidateQueries() });
  const inherit = useMutation({ mutationKey: contentActionKey, mutationFn: () => api<ContentDrop>(`content/drops/${drop.id}/items/${item.id}/end`, { method: 'PUT', body: { version: drop.version, inheritEnd: true } }), onSuccess: () => client.invalidateQueries() });
  const remove = useMutation({ mutationKey: contentActionKey, mutationFn: () => api(`content/changeset/changes/${item.id}?dropId=${drop.id}`, { method: 'DELETE' }), onSuccess: () => client.invalidateQueries() });
  return <article className={styles.entry}>
    <p><strong>{contentType(item.area, item.key)}</strong> · {item.operation} · {Object.values(item.key).join(' / ')}</p>
    {availability ? <><p>{item.inheritEnd ? 'Uses the drop expiry' : item.endsAt ? `Expires ${ukDisplay(item.endsAt)}` : 'Persistent availability'}</p>{editable && <div className={styles.actions}><Field label="Item end date" hint="Required end date, at 7pm UK time."><input type="date" value={end} onChange={event => setEnd(event.target.value)} /></Field><Button size="sm" disabled={save.isPending || remove.isPending || inherit.isPending || Boolean(dateWindowProblem(ukDay(drop.startsAt), end))} onClick={() => save.mutate()}>Save expiry</Button><Button size="sm" disabled={save.isPending || remove.isPending || inherit.isPending || item.inheritEnd} onClick={() => inherit.mutate()}>Use drop expiry</Button></div>}</> : <p>Definition edit persists after availability ends.</p>}
    {editable && <Button size="sm" disabled={save.isPending || remove.isPending} onClick={() => remove.mutate()}>Remove from draft</Button>}
    {(save.error || remove.error || inherit.error) && <p className="error" role="alert">{(save.error ?? remove.error ?? inherit.error)?.message}</p>}
  </article>;
}
