// The Season Pass tab: the default season (campaign + seasonProgress, in force whenever no custom season runs) and the custom
// seasons (rules/objectives.json seasons), each a named Season Pass with its own dates, levels, XP per level and rewards.
// Clubs' Season XP and level claims are kept per season id on the server (migration 166), so a new season starts at 0 XP and a
// season that comes back never pays a level twice. A season is edited as a draft and staged in one go, then published under
// Changes. Address: #/objectives/season.
import { useMemo, useState } from 'react';
import { shownAt, useObjectiveActions, type ObjectiveReward, type ObjectiveRulesDoc, type ObjectiveSeason, type ObjectivesReply, type SeasonProgress } from '../../api/objectives';
import type { ContentChange } from '../../api/types';
import { Badge, StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { Section } from '../../ui/Layout';
import { SwitchRow } from '../../ui/Switch';
import { useToast } from '../../ui/Toast';
import { fromLocal, toLocal } from './GroupEditor';
import { ObjectiveRewardEditor, paysSomething, rewardText } from './ObjectiveRewardEditor';
import { scheduleZone, shiftDay, ukLocal, ukInstant } from '../schedule/time';

const DAY = 86_400_000;
const MAX_LEVELS = 100;
const MAX_OPTIONS = 5;

type Status = 'live' | 'upcoming' | 'ended' | 'standby' | 'disabled';
const STATUS_LABEL: Record<Status, string> = { live: 'Live now', upcoming: 'Upcoming', ended: 'Ended', standby: 'Between seasons', disabled: 'Disabled' };
const STATUS_TONE: Record<Status, 'pitch' | 'flare' | 'neutral'> = { live: 'pitch', upcoming: 'flare', ended: 'neutral', standby: 'neutral', disabled: 'neutral' };

/** One season as the tab shows it: the default season (no dates) or a custom one. */
interface SeasonView {
  id: number;
  name: string;
  progress: SeasonProgress;
  startsAt: string | null;
  endsAt: string | null;
  isDefault: boolean;
  enabled?: boolean;
  /** Created here and not staged yet. */
  isNew?: boolean;
}

const progressOf = (season: SeasonProgress): SeasonProgress => ({
  levelCount: season.levelCount, xpPerLevel: season.xpPerLevel, rewardLevelCap: season.rewardLevelCap ?? null, levelRewards: season.levelRewards,
});
const defaultView = (doc: ObjectiveRulesDoc): SeasonView => ({
  id: doc.campaign.id, name: doc.campaign.name, progress: progressOf(doc.seasonProgress), startsAt: null, endsAt: null, isDefault: true,
});
const customView = (season: ObjectiveSeason, isNew = false): SeasonView => ({
  id: season.id, name: season.name, progress: progressOf(season), startsAt: season.startsAt, endsAt: season.endsAt, isDefault: false, isNew, ...(season.enabled !== undefined ? { enabled: season.enabled } : {}),
});
const time = (iso: string | null) => (iso ? new Date(iso).getTime() : NaN);
const iso = (ms: number) => new Date(ms).toISOString().replace(/\.\d{3}Z$/, 'Z');
/** Whole calendar weeks later at the same local time of day, so a season over a clock change still ends at its start time. */
const addWeeks = (start: string, weeks: number) => { const wall = ukLocal(start); return ukInstant(`${shiftDay(wall.slice(0, 10), weeks * 7)}${wall.slice(10)}`).replace(/\.\d{3}Z$/, 'Z'); };
const byStart = (a: { startsAt: string }, b: { startsAt: string }) => time(a.startsAt) - time(b.startsAt);

function statusOf(view: SeasonView, now: number, current: number | undefined): Status {
  if (view.isDefault) return current === undefined || current === view.id ? 'live' : 'standby';
  if (view.enabled === false) return 'disabled';
  if (now < time(view.startsAt)) return 'upcoming';
  return now < time(view.endsAt) ? 'live' : 'ended';
}

/** "3 days", "5 hours". */
function span(ms: number) {
  const abs = Math.abs(ms);
  if (abs < DAY) { const hours = Math.max(1, Math.round(abs / 3_600_000)); return `${hours} hour${hours === 1 ? '' : 's'}`; }
  const days = Math.round(abs / DAY);
  return `${days} day${days === 1 ? '' : 's'}`;
}
const dateText = (iso: string | null) => (iso ? new Date(iso).toLocaleString('en-GB', { timeZone: scheduleZone, day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '');
const shortDate = (ms: number) => new Date(ms).toLocaleDateString('en-GB', { timeZone: scheduleZone, day: 'numeric', month: 'short' });
const lengthText = (start: string | null, end: string | null) => {
  const days = Math.round((time(end) - time(start)) / DAY);
  return days % 7 === 0 ? `${days / 7} week${days === 7 ? '' : 's'}` : `${days} day${days === 1 ? '' : 's'}`;
};

function whenText(view: SeasonView, status: Status, now: number) {
  if (view.isDefault) return status === 'live' ? 'Running now: no custom season is live' : 'Runs whenever no custom season is live';
  if (status === 'disabled') return 'Availability ended; player progress is preserved';
  if (status === 'upcoming') return `Starts in ${span(time(view.startsAt) - now)}`;
  if (status === 'live') return `Ends in ${span(time(view.endsAt) - now)}`;
  return `Ended ${span(now - time(view.endsAt))} ago`;
}

export function SeasonPassTab({ reply, doc, staged }: { reply: ObjectivesReply; doc: ObjectiveRulesDoc; staged: ContentChange[] }) {
  const now = reply.seasons ? new Date(reply.seasons.now).getTime() : Date.now();
  const current = reply.seasons?.current;
  const custom = useMemo(() => [...(doc.seasons ?? [])].sort(byStart), [doc.seasons]);
  const [pending, setPending] = useState<ObjectiveSeason | null>(null);
  const [creating, setCreating] = useState(false);
  // A created season leaves the pending slot once it is staged (it is then in the document).
  const draftNew = pending && !custom.some((season) => season.id === pending.id) ? pending : null;
  const views: SeasonView[] = [defaultView(doc), ...custom.map((season) => customView(season)), ...(draftNew ? [customView(draftNew, true)] : [])];
  const liveId = views.find((view) => statusOf(view, now, current) === 'live' && !view.isDefault)?.id ?? doc.campaign.id;
  const [selectedId, setSelectedId] = useState<number>(liveId);
  const selected = views.find((view) => view.id === selectedId) ?? views[0]!;
  const played = new Map((reply.seasons?.played ?? []).map((row) => [row.season, row]));
  const publishedSeasons = new Map((reply.rules.seasons ?? []).map((season) => [season.id, JSON.stringify(season)]));
  const isStaged = (view: SeasonView) => view.isDefault
    ? JSON.stringify(progressOf(reply.rules.seasonProgress)) !== JSON.stringify(view.progress) || reply.rules.campaign.name !== view.name
      || reply.rules.campaign.id !== view.id
    : !view.isNew && publishedSeasons.get(view.id) !== JSON.stringify(custom.find((season) => season.id === view.id));
  const nextId = Math.max(reply.seasons?.nextId ?? 0, doc.campaign.id + 1, ...custom.map((season) => season.id + 1));

  return <>
    <Section title="Seasons" text="Each season is its own Season Pass with a name, dates, levels and rewards. Clubs start a new season at 0 XP; their progress in every season is kept, so a season that comes back never pays a level twice. The default season runs whenever no custom season is live."
      actions={<Button size="sm" variant="primary" onClick={() => setCreating(true)} disabled={!!draftNew}>+ New season</Button>}>
      <SeasonTimeline seasons={views.filter((view) => !view.isDefault)} now={now} selectedId={selected.id} onSelect={setSelectedId} />
      <ul className="season-cards" aria-label="Seasons">
        {views.map((view) => {
          const status = statusOf(view, now, current);
          const stats = played.get(view.id);
          const open = view.progress.rewardLevelCap ?? view.progress.levelCount;
          return (
            <li key={view.id}>
              <button type="button" className={`season-card season-card-${status}${view.id === selected.id ? ' season-card-selected' : ''}`}
                aria-pressed={view.id === selected.id} onClick={() => setSelectedId(view.id)}>
                <span className="season-card-head">
                  <strong>{view.name || 'Untitled season'}</strong>
                  <Badge tone={STATUS_TONE[status]}>{STATUS_LABEL[status]}</Badge>
                </span>
                <span className="season-card-dates">{view.isDefault ? 'Default season' : `${dateText(view.startsAt)} → ${dateText(view.endsAt)}`}</span>
                <span className="season-card-when">{whenText(view, status, now)}</span>
                <span className="season-card-meta dim">
                  {view.progress.levelCount} levels · {num(view.progress.xpPerLevel)} XP per level{open < view.progress.levelCount ? ` · ${open} open` : ''}
                  {!view.isDefault && ` · ${lengthText(view.startsAt, view.endsAt)}`}
                </span>
                <span className="season-card-foot">
                  <span className="dim">{stats ? `${num(stats.clubs)} club${stats.clubs === 1 ? '' : 's'} · ${num(stats.xp)} XP earned` : 'No club has played it yet'}</span>
                  {view.isNew ? <Badge tone="staged">New · not saved</Badge> : isStaged(view) && <StagedBadge />}
                </span>
              </button>
            </li>
          );
        })}
      </ul>
    </Section>
    <SeasonEditor key={`${selected.id}:${JSON.stringify(selected)}`} view={selected} status={statusOf(selected, now, current)} reply={reply} doc={doc}
      staged={staged} others={custom.filter((season) => season.id !== selected.id)} nextId={nextId}
      onDiscardNew={() => { setPending(null); setSelectedId(liveId); }} onDeleted={() => setSelectedId(doc.campaign.id)} />
    {creating && <NewSeasonDialog nextId={nextId} now={now} doc={doc} custom={custom} onClose={() => setCreating(false)}
      onCreate={(season) => { setPending(season); setSelectedId(season.id); setCreating(false); }} />}
  </>;
}

/** The custom seasons on a calendar strip, with today marked; the default season fills the gaps. */
function SeasonTimeline({ seasons, now, selectedId, onSelect }: { seasons: SeasonView[]; now: number; selectedId: number; onSelect: (id: number) => void }) {
  if (!seasons.length) return <p className="season-timeline-empty dim">No custom seasons yet: the default season runs all year. Add one to give the next weeks a name, dates and their own rewards.</p>;
  const starts = seasons.map((view) => time(view.startsAt));
  const ends = seasons.map((view) => time(view.endsAt));
  const from = Math.min(now, ...starts) - 3 * DAY;
  let to = Math.max(now, ...ends) + 3 * DAY;
  if (to - from < 42 * DAY) to = from + 42 * DAY;
  const at = (ms: number) => `${((ms - from) / (to - from)) * 100}%`;
  const months: number[] = [];
  for (let month = new Date(new Date(from).getFullYear(), new Date(from).getMonth() + 1, 1).getTime(); month < to;
    month = new Date(new Date(month).getFullYear(), new Date(month).getMonth() + 1, 1).getTime()) months.push(month);
  return (
    <div className="season-timeline" role="group" aria-label="Season calendar">
      <div className="season-timeline-track">
        {months.map((month) => <span key={month} className="season-timeline-month" style={{ left: at(month) }}>{new Date(month).toLocaleDateString('en-GB', { timeZone: scheduleZone, month: 'short' })}</span>)}
        {seasons.map((view) => (
          <button key={view.id} type="button" title={`${view.name}: ${dateText(view.startsAt)} → ${dateText(view.endsAt)}`}
            className={`season-timeline-bar${view.id === selectedId ? ' season-timeline-bar-selected' : ''}${view.isNew ? ' season-timeline-bar-new' : ''}`}
            style={{ left: at(time(view.startsAt)), width: `calc(${at(time(view.endsAt))} - ${at(time(view.startsAt))})` }}
            onClick={() => onSelect(view.id)} aria-label={`Select ${view.name}`}>
            <span>{view.name}</span>
          </button>
        ))}
        {/* The label hangs right of the marker, or left of it near the end of the strip, so it is never cut off. */}
        <span className={(now - from) / (to - from) > 0.7 ? 'season-timeline-now season-timeline-now-end' : 'season-timeline-now'} style={{ left: at(now) }}>
          <span>Today · {shortDate(now)}</span>
        </span>
      </div>
    </div>
  );
}

interface Draft { name: string; startsAt: string | null; endsAt: string | null; progress: SeasonProgress }

function SeasonEditor({ view, status, reply, doc, staged, others, nextId, onDiscardNew, onDeleted }: {
  view: SeasonView; status: Status; reply: ObjectivesReply; doc: ObjectiveRulesDoc; staged: ContentChange[]; others: ObjectiveSeason[];
  nextId: number; onDiscardNew: () => void; onDeleted: () => void;
}) {
  const initial: Draft = { name: view.name, startsAt: view.startsAt, endsAt: view.endsAt, progress: view.progress };
  const [draft, setDraft] = useState<Draft>(() => structuredClone(initial));
  const [editing, setEditing] = useState<{ level: number; reward: ObjectiveReward; apply: Apply } | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [freshening, setFreshening] = useState(false);
  const [dateError, setDateError] = useState<string | null>(null);
  const actions = useObjectiveActions(reply.settingsFile);
  const toast = useToast();
  const progress = draft.progress;
  const open = progress.rewardLevelCap ?? progress.levelCount;
  const dirty = !!view.isNew || JSON.stringify(draft) !== JSON.stringify(initial);
  const setProgress = (changes: Partial<SeasonProgress>) => setDraft((current) => ({ ...current, progress: { ...current.progress, ...changes } }));

  const setLevelCount = (wanted: number) => {
    const count = Math.max(1, Math.min(MAX_LEVELS, Math.round(wanted) || 1));
    const rewards = progress.levelRewards;
    const filler = rewards.at(-1) ?? { coins: 1000 };
    const levelRewards = count > rewards.length ? [...rewards, ...Array.from({ length: count - rewards.length }, () => structuredClone(filler))] : rewards.slice(0, count);
    const cap = progress.rewardLevelCap ?? null;
    setProgress({ levelCount: count, levelRewards, rewardLevelCap: cap !== null && cap < count ? cap : null });
  };

  const overlap = !view.isDefault && draft.startsAt && draft.endsAt
    ? others.find((season) => season.enabled !== false && time(season.startsAt) < time(draft.endsAt) && time(draft.startsAt) < time(season.endsAt)) : undefined;
  const emptyLevel = progress.levelRewards.findIndex((reward) => !paysSomething(reward));
  const problem = dateError ?? (!draft.name.trim() ? 'Give the season a name.'
    : !view.isDefault && (!draft.startsAt || !draft.endsAt) ? 'Set when the season starts and ends.'
    : !view.isDefault && time(draft.endsAt) <= time(draft.startsAt) ? 'The season must end after it starts.'
    : overlap ? `It overlaps “${overlap.name}” (${dateText(overlap.startsAt)} → ${dateText(overlap.endsAt)}). Seasons cannot overlap.`
    : !(progress.xpPerLevel >= 1) ? 'XP per level is a whole number from 1.'
    : emptyLevel >= 0 ? `Level ${emptyLevel + 1} pays nothing.`
    : null);

  const stage = async () => {
    try {
      if (view.isDefault) {
        if (draft.name.trim() !== doc.campaign.name)
          await actions.mutateAsync({ path: ['campaign', 'name'], value: draft.name.trim(), shown: shownAt(reply.rules, staged, ['campaign', 'name']) });
        if (JSON.stringify(progress) !== JSON.stringify(initial.progress))
          await actions.mutateAsync({ path: ['seasonProgress'], value: progress, shown: shownAt(reply.rules, staged, ['seasonProgress']) });
      } else {
        const season: ObjectiveSeason = { id: view.id, name: draft.name.trim(), startsAt: draft.startsAt!, endsAt: draft.endsAt!, ...progress, ...(view.enabled !== undefined ? { enabled: view.enabled } : {}) };
        await actions.mutateAsync({ path: ['seasons'], value: [...others, season].sort(byStart), shown: shownAt(reply.rules, staged, ['seasons']) });
      }
      toast(`${draft.name.trim()} saved.`, 'done');
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), 'error');
    }
  };
  // Season XP and level claims are kept per season id, so the default season starts over under a new id; the old id's
  // progress stays in the season history and comes back only if that id is ever used again.
  const startFresh = () => actions.mutate({ path: ['campaign', 'id'], value: nextId, shown: shownAt(reply.rules, staged, ['campaign', 'id']) }, {
    onSuccess: () => { toast(`${view.name} restarts as season ${nextId} once published. Every club starts it at 0 XP with no level claimed.`, 'staged'); setFreshening(false); },
    onError: (error) => toast(error.message, 'error'),
  });
  const remove = () => actions.mutate({ path: ['seasons'], value: others, shown: shownAt(reply.rules, staged, ['seasons']) }, {
    onSuccess: () => { toast(`Removal of ${view.name} saved. Clubs keep their progress in it and its id is never reused.`, 'staged'); setDeleting(false); onDeleted(); },
    onError: (error) => toast(error.message, 'error'),
  });

  const applyLevel = (target: { level: number; reward: ObjectiveReward; apply: Apply }) => {
    const levels = progress.levelRewards.map((reward, index) => {
      const level = index + 1;
      const hit = target.apply === 'this' ? level === target.level : target.apply === 'odd' ? level % 2 === 1 : target.apply === 'even' ? level % 2 === 0
        : target.apply === 'after' ? level >= target.level : true;
      return hit ? structuredClone(target.reward) : reward;
    });
    setProgress({ levelRewards: levels });
    setEditing(null);
  };

  const milestones = [...new Set([1, Math.ceil(progress.levelCount / 2), open, progress.levelCount])].filter((level) => level >= 1).sort((a, b) => a - b);
  return <>
    <Section className="season-editor" title={<span className="season-editor-title">{draft.name || 'Untitled season'}<Badge tone={STATUS_TONE[status]}>{STATUS_LABEL[status]}</Badge>{view.isDefault && <Badge>Default</Badge>}</span>}
      text={view.isDefault
        ? `The default season (id ${view.id}) is the Season Pass whenever no custom season is live. FIFA counts it down on a rolling window.`
        : `Season id ${view.id}. Clubs earn its Season XP from completed matches and objectives while it is live, and FIFA counts down to its end.`}>
      <div className="season-form">
        <Field label="Name" hint="FIFA shows it on the Season Progress screen.">
          <input type="text" maxLength={48} value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} />
        </Field>
        {!view.isDefault && <>
          <Field label="Starts" hint="UK time (Europe/London).">
            <input type="datetime-local" value={toLocal(draft.startsAt)} onChange={(event) => {
              try {
                const startsAt = fromLocal(event.target.value);
                const length = time(draft.endsAt) - time(draft.startsAt);
                setDraft({ ...draft, startsAt, endsAt: startsAt && Number.isFinite(length) && length > 0 ? iso(time(startsAt) + length) : draft.endsAt });
                setDateError(null);
              } catch (error) { setDateError(error instanceof Error ? error.message : String(error)); }
            }} />
          </Field>
          <Field label="Ends" hint={draft.startsAt && draft.endsAt && time(draft.endsAt) > time(draft.startsAt) ? `Lasts ${lengthText(draft.startsAt, draft.endsAt)}.` : 'Exclusive: the season is over at this time.'}>
            <input type="datetime-local" value={toLocal(draft.endsAt)} onChange={(event) => { try { setDraft({ ...draft, endsAt: fromLocal(event.target.value) }); setDateError(null); } catch (error) { setDateError(error instanceof Error ? error.message : String(error)); } }} />
          </Field>
        </>}
      </div>
      {!view.isDefault && draft.startsAt && <div className="row-actions season-lengths" role="group" aria-label="Season length">
        <span className="dim">Length:</span>
        {[2, 4, 6, 8, 12].map((weeks) => <Button key={weeks} size="sm" variant="ghost" aria-pressed={lengthText(draft.startsAt, draft.endsAt) === `${weeks} weeks`}
          onClick={() => { try { setDraft({ ...draft, endsAt: addWeeks(draft.startsAt!, weeks) }); setDateError(null); } catch (error) { setDateError(error instanceof Error ? error.message : String(error)); } }}>{weeks} weeks</Button>)}
      </div>}
      <div className="season-form season-form-numbers">
        <Field label="Levels" hint={`1 to ${MAX_LEVELS}. New levels copy the last level's reward.`}>
          <span className="season-stepper">
            <Button size="sm" variant="ghost" aria-label="One level fewer" disabled={progress.levelCount <= 1} onClick={() => setLevelCount(progress.levelCount - 1)}>−</Button>
            <input type="number" min={1} max={MAX_LEVELS} aria-label="Levels" value={progress.levelCount} onChange={(event) => setLevelCount(Number(event.target.value))} />
            <Button size="sm" variant="ghost" aria-label="One level more" disabled={progress.levelCount >= MAX_LEVELS} onClick={() => setLevelCount(progress.levelCount + 1)}>+</Button>
          </span>
        </Field>
        <Field label="XP per level" hint="Level n needs n × this much Season XP.">
          <input type="number" min={1} value={progress.xpPerLevel} onChange={(event) => setProgress({ xpPerLevel: Math.max(0, Math.round(Number(event.target.value) || 0)) })} />
        </Field>
        <Field label="Levels open" hint="Clubs can reach and claim levels 1 to this one.">
          <input type="number" min={1} max={progress.levelCount} value={open}
            onChange={(event) => { const cap = Math.max(1, Math.min(progress.levelCount, Math.round(Number(event.target.value) || 1))); setProgress({ rewardLevelCap: cap >= progress.levelCount ? null : cap }); }} />
        </Field>
      </div>
      <ul className="season-milestones" aria-label="XP needed">
        {milestones.map((level) => <li key={level}><span className="dim">Level {level}</span><strong>{num(level * progress.xpPerLevel)} XP</strong></li>)}
      </ul>
      <h3 className="objective-subhead">Levels</h3>
      <ol className="season-levels">
        {progress.levelRewards.map((reward, index) => {
          const level = index + 1;
          const changed = JSON.stringify(reward) !== JSON.stringify(initial.progress.levelRewards[index]);
          return (
            <li key={level} className={level > open ? 'season-level season-level-closed' : 'season-level'}>
              <span className="season-level-number">{level}</span>
              <div className="season-level-body">
                <strong>{rewardText(reward, reply.packs)}</strong>
                <small className="dim">{num(level * progress.xpPerLevel)} XP{level > open ? ' · not open yet' : ''}</small>
              </div>
              {changed ? <Badge tone="flare">Edited</Badge> : <span />}
              <Button size="sm" onClick={() => setEditing({ level, reward: structuredClone(reward), apply: 'this' })} aria-label={`Edit level ${level}`}>Edit</Button>
            </li>
          );
        })}
      </ol>
      <div className="season-actionbar" role="region" aria-label="Season changes">
        <span className={problem ? 'field-problem' : 'dim'} role="status">{problem ?? (dirty ? 'Unsaved edits. Save the season to apply them.' : 'No unsaved edits.')}</span>
        <div className="row-actions">
          {view.isDefault && <Button variant="danger" disabled={dirty || actions.isPending}
            title={dirty ? 'Save or discard the edits first.' : undefined} onClick={() => setFreshening(true)}>Start fresh</Button>}
          {!view.isDefault && !view.isNew && <Button variant="danger" disabled={status === 'live' || actions.isPending}
            title={status === 'live' ? 'A live season cannot be removed: move its end date to end it first.' : undefined} onClick={() => setDeleting(true)}>Remove season</Button>}
          <Button variant="ghost" disabled={!dirty || actions.isPending} onClick={() => (view.isNew ? onDiscardNew() : setDraft(structuredClone(initial)))}>{view.isNew ? 'Discard new season' : 'Discard edits'}</Button>
          <Button variant="staged" disabled={!dirty || !!problem || actions.isPending} onClick={() => void stage()}>Save season</Button>
        </div>
      </div>
    </Section>
    {editing && <Dialog open wide onOpenChange={(next) => { if (!next) setEditing(null); }} title={`Level ${editing.level} reward`}
      description={`Paid when a club reaches ${num(editing.level * progress.xpPerLevel)} Season XP and claims it.`}
      footer={<><Button variant="ghost" onClick={() => setEditing(null)}>Cancel</Button>
        <Button variant="primary" disabled={!paysSomething(editing.reward)} onClick={() => applyLevel(editing)}>Apply</Button></>}>
      <div className="stack">
        <LevelRewardEditor value={editing.reward} packs={reply.packs} onChange={(reward) => setEditing({ ...editing, reward })} />
        <Field label="Use this reward for">
          <select value={editing.apply} onChange={(event) => setEditing({ ...editing, apply: event.target.value as Apply })}>
            <option value="this">Level {editing.level} only</option>
            <option value="after">Level {editing.level} and every level after it</option>
            <option value="odd">Every odd level (1, 3, 5…)</option>
            <option value="even">Every even level (2, 4, 6…)</option>
            <option value="all">Every level</option>
          </select>
        </Field>
      </div>
    </Dialog>}
    {deleting && <Dialog open onOpenChange={(next) => { if (!next) setDeleting(false); }} title={`Remove ${view.name}?`}
      description="Clubs keep the progress they made in it, and its id is never reused. Removal applies immediately."
      footer={<><Button variant="ghost" onClick={() => setDeleting(false)}>Keep it</Button><Button variant="danger" disabled={actions.isPending} onClick={remove}>Remove season</Button></>}>
      <p className="dim">{dateText(view.startsAt)} → {dateText(view.endsAt)} · {view.progress.levelCount} levels</p>
    </Dialog>}
    {freshening && <Dialog open onOpenChange={(next) => { if (!next) setFreshening(false); }} title={`Start ${view.name} fresh?`}
      description={`It becomes season ${nextId}, with the same name, levels and rewards. Once published, every club starts it at 0 Season XP with no level claimed.`}
      footer={<><Button variant="ghost" onClick={() => setFreshening(false)}>Cancel</Button><Button variant="danger" disabled={actions.isPending} onClick={startFresh}>Start fresh</Button></>}>
      <p className="dim">Progress in season {view.id} is kept, not paid out again; it only returns if season {view.id} is used again. Objectives and their rewards are not reset.</p>
    </Dialog>}
  </>;
}

type Apply = 'this' | 'after' | 'odd' | 'even' | 'all';

/** A new custom season: its name, start, length and the levels it starts from. */
function NewSeasonDialog({ nextId, now, doc, custom, onClose, onCreate }: {
  nextId: number; now: number; doc: ObjectiveRulesDoc; custom: ObjectiveSeason[]; onClose: () => void; onCreate: (season: ObjectiveSeason) => void;
}) {
  // Starts when the last season ends (or now), on the hour.
  const lastEnd = Math.max(now, ...custom.filter(season => season.enabled !== false).map((season) => time(season.endsAt)));
  const firstStart = Math.ceil(lastEnd / 3_600_000) * 3_600_000;
  const [name, setName] = useState(`Season ${custom.length + 2}`);
  const [startsAt, setStartsAt] = useState<string | null>(iso(firstStart));
  const [weeks, setWeeks] = useState(4);
  const [template, setTemplate] = useState<string>('default');
  const [dateError, setDateError] = useState<string | null>(null);
  const source = template === 'default' ? doc.seasonProgress : template === 'blank' ? null : custom.find((season) => String(season.id) === template) ?? null;
  const create = () => {
    const progress: SeasonProgress = source ? structuredClone(progressOf(source))
      : { levelCount: 10, xpPerLevel: doc.seasonProgress.xpPerLevel, rewardLevelCap: null, levelRewards: Array.from({ length: 10 }, () => ({ coins: 1000 })) };
    try { onCreate({ id: nextId, name: name.trim(), startsAt: startsAt!, endsAt: addWeeks(startsAt!, weeks), ...progress }); }
    catch (error) { setDateError(error instanceof Error ? error.message : String(error)); }
  };
  return (
    <Dialog open onOpenChange={(next) => { if (!next) onClose(); }} title="New season"
      description="It opens in the editor below as a draft: adjust the levels and rewards, then save it. The season becomes available when its start date arrives."
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!name.trim() || !startsAt || dateError !== null} onClick={create}>Create draft</Button></>}>
      <div className="stack">
        {dateError && <p className="error" role="alert">{dateError}</p>}
        <Field label="Season name"><input type="text" maxLength={48} value={name} onChange={(event) => setName(event.target.value)} /></Field>
        <div className="form-row">
          <Field label="Starts" hint="UK time (Europe/London)."><input type="datetime-local" value={toLocal(startsAt)} onChange={(event) => { try { setStartsAt(fromLocal(event.target.value)); setDateError(null); } catch (error) { setDateError(error instanceof Error ? error.message : String(error)); } }} /></Field>
          <Field label="Length">
            <select value={weeks} onChange={(event) => { setWeeks(Number(event.target.value)); setDateError(null); }}>
              {[1, 2, 3, 4, 5, 6, 8, 10, 12].map((count) => <option key={count} value={count}>{count} week{count === 1 ? '' : 's'}</option>)}
            </select>
          </Field>
        </div>
        <Field label="Levels and rewards" hint="Copied into the new season; edit them freely afterwards.">
          <select value={template} onChange={(event) => setTemplate(event.target.value)}>
            <option value="default">Copy the default season ({doc.seasonProgress.levelCount} levels)</option>
            {custom.map((season) => <option key={season.id} value={season.id}>Copy {season.name} ({season.levelCount} levels)</option>)}
            <option value="blank">Start simple: 10 levels of 1,000 coins</option>
          </select>
        </Field>
        <p className="dim">Season id {nextId}. Clubs start it at 0 Season XP.</p>
      </div>
    </Dialog>
  );
}

/** A level's reward: one reward, or a choice the club picks one of (FIFA's level reward options). */
function LevelRewardEditor({ value, onChange, packs }: { value: ObjectiveReward; onChange: (reward: ObjectiveReward) => void; packs: ObjectivesReply['packs'] }) {
  const options = value.options ?? [];
  const choice = options.length > 0;
  return <div className="stack">
    <SwitchRow label="Club picks one of several rewards" checked={choice}
      text="FIFA shows the options side by side; the club claims one of them, for example 1 of 3 packs."
      onChange={(on) => onChange(on ? { options: [value, { coins: 1000 }] } : options[0] ?? { coins: 1000 })} />
    {!choice ? <ObjectiveRewardEditor value={value} packs={packs} onChange={onChange} /> : <>
      {options.map((option, index) => (
        <fieldset key={index} className="level-option">
          <legend>Option {index + 1}</legend>
          <ObjectiveRewardEditor value={option} packs={packs} idPrefix={`option ${index + 1}`}
            onChange={(reward) => onChange({ options: options.map((current, at) => at === index ? reward : current) })} />
          {options.length > 2 && <Button size="sm" variant="ghost" onClick={() => onChange({ options: options.filter((_, at) => at !== index) })}>Remove option {index + 1}</Button>}
        </fieldset>
      ))}
      <div className="row-actions">
        <Button size="sm" disabled={options.length >= MAX_OPTIONS} onClick={() => onChange({ options: [...options, { coins: 1000 }] })}>Add option</Button>
        <span className="dim">{options.length} of 2 to {MAX_OPTIONS} options</span>
      </div>
    </>}
  </div>;
}
