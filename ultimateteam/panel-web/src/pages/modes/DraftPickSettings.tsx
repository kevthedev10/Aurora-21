import { useState } from 'react';
import { useChangesetActions, useContentBusy } from '../../api/content';
import type { DraftMode, DraftPickSettings } from '../../api/draftMode';
import { useModeActions } from '../../api/modes';
import type { ChangesetReply, ContentChange } from '../../api/types';
import { StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { Chips } from '../../ui/Chips';
import { Field } from '../../ui/Field';
import { Section } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';
import './draft-picks.css';

type Key = keyof DraftPickSettings;
type Group = 'basics' | 'weights' | 'rounds';
type Spec = { key: Key; label: string; hint: string; min: number; max?: number; decimal?: boolean; percent?: boolean; counts?: boolean };
const BASICS: Spec[] = [
  { key: 'minRating', label: 'Minimum player rating', hint: 'All offers start at this rating.', min: 1, max: 99 },
  { key: 'strongFloor', label: 'Normal round rating floor', hint: 'Used when enough eligible players reach this rating.', min: 1, max: 99 },
  { key: 'captainStrongFloor', label: 'Captain rating floor', hint: 'Used when enough eligible captains reach this rating.', min: 1, max: 99 },
  { key: 'choicesPerScreen', label: 'Choices per screen', hint: 'Applies to player and manager offers.', min: 1 },
  { key: 'managerPoolSize', label: 'Manager pool size', hint: 'Managers are sampled from this many of the highest-rated managers.', min: 1 },
];
const FALLBACKS: Spec[] = [
  { key: 'specialBias', label: 'Normal round special weight', hint: 'Positive fallback weight for a special category without its own override.', min: 0, decimal: true },
  { key: 'weakRoundSpecialBias', label: 'Weak round special weight', hint: 'Positive fallback weight for a special category in weak rounds.', min: 0, decimal: true },
];
const ROUNDS: Spec[] = [
  { key: 'weakRoundCounts', label: 'Possible weak round counts', hint: 'Comma-separated counts from 0 to 23. One count is drawn per run; those rounds use the weak weights and skip the normal rating floor.', min: 0, max: 23, counts: true },
  { key: 'elite99Chance', label: 'Runs with 99-rated rounds (%)', hint: '0 disables these rounds. A 99-rated card is offered when an eligible one fits the slot.', min: 0, max: 100, decimal: true, percent: true },
  { key: 'elite99RoundCounts', label: 'Possible 99-rated round counts', hint: 'Comma-separated counts from 0 to 23. One count is drawn for each qualifying run.', min: 0, max: 23, counts: true },
];
type WeightKey = 'categoryWeights' | 'weakRoundCategoryWeights' | 'goldRareBucketWeights' | 'weakRoundGoldRareBucketWeights';
const weightLabel = (key: string) => key.replace(/^rating_(\d+)_(\d+)$/, '$1–$2').replaceAll('_', ' ');

export function DraftPickSettingsEditor({ data, open }: { data: DraftMode; open: ChangesetReply | undefined }) {
  const [group, setGroup] = useState<Group>('basics');
  const actions = useModeActions();
  const changes = useChangesetActions();
  const busy = useContentBusy();
  const toast = useToast();
  const staged = (open?.changeset?.changes ?? []).filter(change => change.area === 'rules' && change.key.file === data.settingsFile
    && (change.key.path as unknown[])?.[0] === 'picks');
  const changeFor = (key: Key) => staged.find(change => JSON.stringify(change.key.path) === JSON.stringify(['picks', key]));
  const parent = staged.find(change => JSON.stringify(change.key.path) === '["picks"]');
  const current = (key: Key): DraftPickSettings[Key] => {
    const change = changeFor(key);
    return (change?.after as { value: DraftPickSettings[Key] } | undefined)?.value
      ?? (parent?.after as { value: DraftPickSettings } | undefined)?.value?.[key] ?? data.pickSettings![key];
  };
  const stage = (key: Key, value: unknown, shown: unknown, done: () => void) => actions.stageSetting.mutate({
    file: data.settingsFile, path: ['picks', key], value, shown,
  }, { onSuccess: () => { done(); toast('Pick setting saved.', 'done'); }, onError: error => toast(error.message, 'error') });
  const undo = (change: ContentChange) => changes.remove.mutate(change.id, { onError: error => toast(error.message, 'error') });
  if (!data.pickSettings) return <Section title="Player picks" text="Update the server to load Draft pick settings." />;

  return <Section title="Player picks" text="Shared by online and single-player Draft. Changes apply immediately when saved; previously shown offers stay saved."
    actions={staged.length ? <StagedBadge>{staged.length} staged</StagedBadge> : undefined}>
    <Chips label="Pick setting groups" value={group} onChange={setGroup} options={[
      { value: 'basics', label: 'Basics' }, { value: 'weights', label: 'Weights' }, { value: 'rounds', label: 'Special rounds' },
    ]} />
    <div className="draft-pick-content">
      <div hidden={group !== 'basics'} className="mode-settings">{BASICS.map(spec => <PickField key={spec.key} spec={spec} shown={current(spec.key)}
        change={changeFor(spec.key)} busy={busy} stage={stage} undo={undo} />)}</div>
      <div hidden={group !== 'weights'} className="stack">
        <p className="dim">Relative weights are adjusted to the eligible cards in each offer. Standard categories default to weight 1.</p>
        <div className="mode-settings">{FALLBACKS.map(spec => <PickField key={spec.key} spec={spec} shown={current(spec.key)}
          change={changeFor(spec.key)} busy={busy} stage={stage} undo={undo} />)}</div>
        <WeightTable title="Category overrides" text="An empty entry uses the category's fallback weight. Zero excludes it."
          normal="categoryWeights" weak="weakRoundCategoryWeights" options={data.pickOptions?.categories ?? []} current={current} changeFor={changeFor} busy={busy} stage={stage} undo={undo} />
        <WeightTable title="Gold Rare rating groups" text="Custom weights select these saved groups. An empty entry excludes a group while custom weights remain; clearing every entry restores saved group weights."
          normal="goldRareBucketWeights" weak="weakRoundGoldRareBucketWeights" options={data.pickOptions?.goldRareGroups ?? []} current={current} changeFor={changeFor} busy={busy} stage={stage} undo={undo} />
      </div>
      <div hidden={group !== 'rounds'} className="mode-settings">{ROUNDS.map(spec => <PickField key={spec.key} spec={spec} shown={current(spec.key)}
        change={changeFor(spec.key)} busy={busy} stage={stage} undo={undo} />)}</div>
    </div>
  </Section>;
}

function PickField({ spec, shown, change, busy, stage, undo }: {
  spec: Spec; shown: DraftPickSettings[Key]; change: ContentChange | undefined; busy: boolean;
  stage: (key: Key, value: unknown, shown: unknown, done: () => void) => void; undo: (change: ContentChange) => void;
}) {
  const initial = Array.isArray(shown) ? shown.join(', ') : String(spec.percent ? Number(shown) * 100 : shown);
  const [text, setText] = useState<string | null>(null);
  const raw = text ?? initial;
  const values = spec.counts ? raw.split(',').map(value => value.trim() ? Number(value.trim()) : NaN) : [raw.trim() ? Number(raw) : NaN];
  const valid = values.every(value => Number.isFinite(value) && (spec.decimal || Number.isSafeInteger(value))
    && value >= spec.min && (spec.max === undefined || value <= spec.max))
    && (!FALLBACKS.some(setting => setting.key === spec.key) || values[0]! > 0);
  const value = spec.counts ? values : spec.percent ? values[0]! / 100 : values[0];
  const dirty = text !== null && JSON.stringify(value) !== JSON.stringify(shown);
  return <div className="mode-setting">
    <Field label={spec.label} hint={spec.hint} error={dirty && !valid ? 'Enter valid values within the stated range.' : null}>
      <input type={spec.counts ? 'text' : 'number'} min={spec.min} max={spec.max} step={spec.decimal ? 'any' : 1} value={raw}
        disabled={busy} onChange={event => setText(event.target.value)} />
    </Field>
    <div className="mode-setting-actions">
      {change && <><StagedBadge /><Button size="sm" variant="ghost" disabled={busy} onClick={() => { undo(change); setText(null); }}>Undo</Button></>}
      {dirty && <><Button size="sm" variant="ghost" disabled={busy} onClick={() => setText(null)}>Discard</Button>
        <Button size="sm" variant="staged" disabled={busy || !valid} onClick={() => stage(spec.key, value, shown, () => setText(null))}>Save</Button></>}
    </div>
  </div>;
}

function WeightTable({ title, text, normal, weak, options, current, changeFor, busy, stage, undo }: {
  title: string; text: string; normal: WeightKey; weak: WeightKey; options: string[];
  current: (key: Key) => DraftPickSettings[Key]; changeFor: (key: Key) => ContentChange | undefined; busy: boolean;
  stage: (key: Key, value: unknown, shown: unknown, done: () => void) => void; undo: (change: ContentChange) => void;
}) {
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [added, setAdded] = useState<string[]>([]);
  const keys = [...new Set([...added, ...[normal, weak].flatMap(key => Object.keys(current(key)))])].sort();
  return <div className="draft-weight-group"><h3>{title}</h3><p className="dim">{text}</p>
    <div className="draft-weight-scroll"><table className="draft-weight-table"><thead><tr><th scope="col">Group</th>
      <th scope="col">Normal rounds</th><th scope="col">Weak rounds</th></tr></thead><tbody>
      {keys.map(code => <tr key={code}><th scope="row" title={code}>{weightLabel(code)}</th>{[normal, weak].map(key => {
        const shown = current(key) as Record<string, number>;
        const id = `${key}:${code}`;
        const raw = drafts[id] ?? (shown[code] === undefined ? '' : String(shown[code]));
        const next = { ...shown };
        if (!raw.trim()) delete next[code]; else next[code] = Number(raw);
        const valid = Object.values(next).every(value => Number.isFinite(value) && value >= 0)
          && (normal === 'categoryWeights' || !Object.keys(next).length || Object.values(next).some(value => value > 0));
        const dirty = drafts[id] !== undefined && JSON.stringify(next) !== JSON.stringify(shown);
        return <td key={key}><div className="draft-weight-cell">
          <input type="number" min={0} step="any" value={raw} disabled={busy} aria-label={`${title}: ${weightLabel(code)}, ${key === normal ? 'normal' : 'weak'} rounds`}
            aria-invalid={dirty && !valid} placeholder="—" onChange={event => setDrafts({ ...drafts, [id]: event.target.value })} />
          {dirty && <Button size="sm" variant="staged" disabled={busy || !valid} onClick={() => stage(key, next, shown,
            () => setDrafts(previous => { const updated = { ...previous }; delete updated[id]; return updated; }))}>Save</Button>}
        </div></td>;
      })}</tr>)}
    </tbody></table></div>
    {options.some(code => !keys.includes(code)) && <div className="draft-weight-add"><Field label={`Add ${title.toLowerCase()}`}>
      <select value="" disabled={busy} onChange={event => { if (event.target.value) setAdded([...added, event.target.value]); }}>
        <option value="">Choose a group…</option>{options.filter(code => !keys.includes(code)).map(code => <option key={code} value={code}>{weightLabel(code)}</option>)}
      </select>
    </Field></div>}
    <div className="mode-setting-actions">{[normal, weak].map(key => {
      const change = changeFor(key);
      return change ? <span key={key}><StagedBadge>{key === normal ? 'Normal' : 'Weak'} staged</StagedBadge> <Button size="sm" variant="ghost" disabled={busy}
        onClick={() => { undo(change); setDrafts({}); }}>Undo {key === normal ? 'normal' : 'weak'} weights</Button></span> : null;
    })}</div>
  </div>;
}
