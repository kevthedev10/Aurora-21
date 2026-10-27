// Modes → Draft (todo.md section 2): the entry costs (rules of draft.json) and, per mode and wins, the reward every run ending
// there gets plus the weighted choices one is drawn from, saved immediately.
import { useMemo, useState } from 'react';
import { useChangesetActions, useContentBusy } from '../../api/content';
import { DRAFT_AREA, canonicalLevel, chances, useDraftActions, useDraftMode, type DraftChoice, type DraftLevel, type DraftMode, type DraftModeId } from '../../api/draftMode';
import { useModeActions, type Reward } from '../../api/modes';
import { isPick, useCatalogue } from '../../api/packs';
import type { ChangesetReply } from '../../api/types';
import { href } from '../../app/router';
import { StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { Chips } from '../../ui/Chips';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { ErrorPanel, Loading, Section } from '../../ui/Layout';
import { RewardEditor, rewardProblems, rewardSummary, type RewardAllow, type RewardPackOption } from '../../ui/RewardEditor';
import { useToast } from '../../ui/Toast';

import { CompletionReward } from './CompletionReward';
import { DraftPickSettingsEditor } from './DraftPickSettings';

const DRAFT_ALLOW = (maxCount: number): RewardAllow => ({ fcPoints: false, xp: false, maxCount });
const emptyReward = (): Reward => ({ coins: 0, fcPoints: 0, xp: 0, lines: [] });
const percent = (value: number) => `${value >= 10 || value === 0 ? Math.round(value) : value.toFixed(1)}%`;
const winsText = (wins: number) => `${wins} ${wins === 1 ? 'win' : 'wins'}`;

const stagedRule = (open: ChangesetReply | undefined, file: string, path: (string | number)[]) => {
  const change = open?.changeset?.changes?.find((candidate) => candidate.area === 'rules' && candidate.key.file === file
    && JSON.stringify(candidate.key.path) === JSON.stringify(path));
  return change ? (change.after as { value?: unknown } | null)?.value : undefined;
};

export function DraftTab() {
  const mode = useDraftMode();
  const catalogue = useCatalogue();
  const [which, setWhich] = useState<DraftModeId>('online');
  const [view, setView] = useState<'rewards' | 'picks'>('rewards');
  const [editing, setEditing] = useState<DraftLevel | null>(null);
  const changeActions = useChangesetActions();
  const busy = useContentBusy();
  const toast = useToast();
  const packs: RewardPackOption[] = useMemo(() => (catalogue.data?.packs ?? []).filter((pack) => pack.pending !== 'Delete')
    .map((pack) => ({ id: pack.id, name: pack.description ?? `Pack ${pack.id}`, pick: isPick(pack.config), href: href('packs', 'catalogue', pack.id) })), [catalogue.data]);
  const packName = (id: number) => packs.find((pack) => pack.id === id)?.name ?? `Pack ${id}`;

  if (mode.isPending || catalogue.isPending) return <Loading />;
  if (mode.isError) return <ErrorPanel what="The Draft rewards" error={mode.error} onRetry={() => void mode.refetch()} />;
  if (catalogue.isError) return <ErrorPanel what="The pack list" error={catalogue.error} onRetry={() => void catalogue.refetch()} />;
  const data = mode.data!;
  const changes = mode.open.data?.changeset?.changes ?? [];
  const undo = (level: DraftLevel) => {
    const change = changes.find((candidate) => candidate.area === DRAFT_AREA && candidate.key.mode === level.mode && Number(candidate.key.wins) === level.wins);
    if (change) changeActions.remove.mutate(change.id, { onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error') });
  };
  const levels = data.levels.filter((level) => level.mode === which).sort((a, b) => a.wins - b.wins);

  return (
    <div className="stack">
      <DraftSettings data={data} open={mode.open.data} />
      <Chips label="Draft sections" value={view} onChange={setView} options={[
        { value: 'rewards', label: 'Rewards', count: changes.filter(change => change.area === DRAFT_AREA).length || undefined },
        { value: 'picks', label: 'Pick settings', count: changes.filter(change => change.area === 'rules' && change.key.file === data.settingsFile
          && (change.key.path as unknown[])?.[0] === 'picks').length || undefined },
      ]} />
      <div hidden={view !== 'picks'}><DraftPickSettingsEditor data={data} open={mode.open.data} /></div>
      <div hidden={view !== 'rewards'} className="draft-rewards-view">
      <CompletionReward mode="draftOnline" title="Online Draft match completion reward" />
      <CompletionReward mode="draftSinglePlayer" title="Single-player Draft match completion reward" />
      {data.levels.some((level) => level.pending) && <p className="notice">These rewards have pending edits. <a href={href('changes')}>Review Changes</a> to validate and publish them.</p>}
      <Section title="Run rewards" text={`A run ends on a loss or at ${data.winsToComplete} wins. It gets its level's reward and one of the level's choices, drawn by weight when the run ends. Difficulty does not change the reward, and FIFA does not show it before the claim.`}>
        <Chips label="Draft mode" value={which} onChange={(next) => setWhich(next as DraftModeId)}
          options={[{ value: 'online', label: 'Online' }, { value: 'single', label: 'Single-player' }]} />
        <ul className="mode-ranks mt">
          {levels.map((level) => {
            const odds = chances(level.choices);
            return (
              <li key={level.wins} className="mode-rank">
                <div className="mode-rank-head">
                  <strong>{winsText(level.wins)}</strong>
                  <span className="dim">Every run: {rewardSummary(level.reward, packName)}</span>
                  {level.pending && <StagedBadge>Staged edit</StagedBadge>}
                </div>
                <ol className="mode-options" aria-label={`${winsText(level.wins)} choices`}>
                  {level.choices.map((choice, index) => (
                    <li key={index}><span className="dim">{percent(odds[index] ?? 0)}: </span>{rewardSummary(choice.reward, packName)}</li>
                  ))}
                </ol>
                <div className="mode-rank-actions">
                  <Button size="sm" disabled={busy} onClick={() => setEditing(level)} aria-label={`Edit ${level.name}`}>Edit</Button>
                  {level.pending && <Button size="sm" variant="ghost" disabled={busy} onClick={() => undo(level)}>Undo staged change</Button>}
                </div>
              </li>
            );
          })}
        </ul>
      </Section>
      </div>
      {editing && <LevelEditor key={`${editing.mode}-${editing.wins}`} level={editing} data={data} packs={packs} onClose={() => setEditing(null)} />}
    </div>
  );
}

const ENTRY_SETTINGS: { label: string; key: keyof DraftMode['settings']; path: string[]; hint: string }[] = [
  { label: 'Entry (coins)', key: 'entryCoins', path: ['entry', 'coins'], hint: 'What a Draft entry costs in coins.' },
  { label: 'Entry (Aurora Tokens)', key: 'entryPoints', path: ['entry', 'points'], hint: 'What a Draft entry costs in Aurora Tokens.' },
];

function DraftSettings({ data, open }: { data: DraftMode; open: ChangesetReply | undefined }) {
  const actions = useModeActions();
  const busy = useContentBusy();
  const toast = useToast();
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  return (
    <Section title="Entry costs" text="The coins and Aurora Tokens required to enter a Draft.">
      <div className="mode-settings">
        {ENTRY_SETTINGS.map((setting) => {
          const key = setting.path.join('.');
          const pending = stagedRule(open, data.settingsFile, setting.path) as number | undefined;
          const shown = pending ?? data.settings[setting.key];
          const draft = drafts[key] ?? String(shown);
          const value = /^\d+$/.test(draft.trim()) ? Number(draft.trim()) : Number.NaN;
          const dirty = draft.trim() !== String(shown);
          return (
            <div key={key} className="mode-setting">
              <Field label={setting.label} hint={setting.hint} error={dirty && !Number.isSafeInteger(value) ? 'A whole number from 0.' : null}>
                <input type="number" inputMode="numeric" min={0} step={1} value={draft} onChange={(event) => setDrafts({ ...drafts, [key]: event.target.value })} />
              </Field>
              <div className="mode-setting-actions">
                {pending !== undefined && <StagedBadge />}
                {dirty && <Button size="sm" variant="staged" disabled={busy || !Number.isSafeInteger(value)}
                  onClick={() => actions.stageSetting.mutate({ file: data.settingsFile, path: setting.path, value, shown }, {
                    onSuccess: () => { setDrafts(({ [key]: _, ...others }) => others); toast(`${setting.label} saved.`, 'done'); },
                    onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error'),
                  })}>Save</Button>}
              </div>
            </div>
          );
        })}
      </div>
    </Section>
  );
}

function LevelEditor({ level, data, packs, onClose }: {
  level: DraftLevel;
  data: DraftMode;
  packs: RewardPackOption[];
  onClose: () => void;
}) {
  const actions = useDraftActions();
  const toast = useToast();
  const [reward, setReward] = useState<Reward>(level.reward);
  const [choices, setChoices] = useState<DraftChoice[]>(level.choices);
  const [error, setError] = useState<string | null>(null);
  const allow = DRAFT_ALLOW(data.maxLineCount);
  const odds = chances(choices);
  const problems = [
    ...rewardProblems(reward, allow, packs).map((problem) => `Every run: ${problem}`),
    ...(choices.length ? [] : ['Add at least one choice.']),
    ...choices.flatMap((choice, index) => [
      ...(Number.isSafeInteger(choice.weight) && choice.weight >= 1 && choice.weight <= data.maxWeight ? [] : [`Choice ${index + 1}: the weight is 1 to ${data.maxWeight}.`]),
      ...rewardProblems(choice.reward, allow, packs).map((problem) => `Choice ${index + 1}: ${problem}`),
    ]),
  ];
  const unchanged = JSON.stringify(canonicalLevel({ reward, choices })) === JSON.stringify(canonicalLevel(level));
  const setChoice = (index: number, next: DraftChoice) => setChoices(choices.map((existing, at) => (at === index ? next : existing)));
  const stage = () => actions.stageLevel.mutate({ mode: level.mode, wins: level.wins, reward, choices }, {
    onSuccess: () => { toast(`${level.name} saved.`, 'done'); onClose(); },
    onError: (caught) => setError(caught instanceof Error ? caught.message : String(caught)),
  });
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose(); }} wide title={level.name}
      description="Every run ending here gets the first reward and one choice, drawn by weight. Saving applies these rewards immediately."
      footer={<>
        {problems.length > 0 && <span className="dim">{problems[0]}</span>}
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="staged" disabled={actions.stageLevel.isPending || problems.length > 0 || unchanged} onClick={stage}>Save level</Button>
      </>}>
      <div className="mode-rank-editor">
        <fieldset className="mode-option">
          <legend>Every run</legend>
          <RewardEditor value={reward} onChange={setReward} packs={packs} allow={allow} />
        </fieldset>
        {choices.map((choice, index) => (
          <fieldset key={index} className="mode-option">
            <legend>Choice {index + 1} <span className="dim">({percent(odds[index] ?? 0)} chance)</span></legend>
            <div className="mode-rank-fields">
              <Field label="Weight" hint={`1 to ${num(data.maxWeight)}; the chance is its share of all weights.`}>
                <input type="number" inputMode="numeric" min={1} max={data.maxWeight} step={1}
                  value={Number.isFinite(choice.weight) ? choice.weight : ''}
                  onChange={(event) => setChoice(index, { ...choice, weight: /^\d+$/.test(event.target.value.trim()) ? Number(event.target.value.trim()) : Number.NaN })} />
              </Field>
            </div>
            <RewardEditor value={choice.reward} onChange={(next) => setChoice(index, { ...choice, reward: next })} packs={packs} allow={allow} />
            {choices.length > 1 && <div><Button size="sm" variant="ghost" onClick={() => setChoices(choices.filter((_, at) => at !== index))}>Remove choice {index + 1}</Button></div>}
          </fieldset>
        ))}
        {choices.length < data.maxChoices && <div><Button size="sm" onClick={() => setChoices([...choices, { weight: 1, reward: emptyReward() }])}>Add choice</Button></div>}
        {error && <p className="error-box" role="alert">{error}</p>}
      </div>
    </Dialog>
  );
}
