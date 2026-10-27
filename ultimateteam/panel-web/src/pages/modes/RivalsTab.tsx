// Modes → Rivals (todo.md section 2): the Weekly Score settings and rank shares (rules of rivals_rewards.json), each division's
// six ranks of 1-4 weekly reward options and each division's promotion reward, saved immediately.
import { useMemo, useState } from 'react';
import { useChangesetActions, useContentBusy } from '../../api/content';
import { canonicalReward, useModeActions, type Reward } from '../../api/modes';
import { isPick, useCatalogue } from '../../api/packs';
import { RIVALS_PROMOTION_AREA, RIVALS_RANK_AREA, useRivalsActions, useRivalsMode, type RivalsMode, type RivalsRank } from '../../api/rivalsMode';
import type { ChangesetReply } from '../../api/types';
import { href } from '../../app/router';
import { StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { Chips } from '../../ui/Chips';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { ErrorPanel, Loading, Section } from '../../ui/Layout';
import { RewardEditor, rewardProblems, rewardSummary, type RewardAllow, type RewardPackOption } from '../../ui/RewardEditor';
import { SwitchRow } from '../../ui/Switch';
import { useToast } from '../../ui/Toast';

const RIVALS_ALLOW = (maxCount: number): RewardAllow => ({ fcPoints: true, xp: false, maxCount });
const emptyReward = (): Reward => ({ coins: 0, fcPoints: 0, xp: 0, lines: [] });
const optionSource = (index: number, count: number) => count <= 1 ? 'one fixed reward' : `option ${['A', 'B'][index] ?? 'C'}`;

/** A staged rule's value, or undefined when that rule has no pending edit. */
const stagedRule = (open: ChangesetReply | undefined, file: string, path: (string | number)[]) => {
  const change = open?.changeset?.changes?.find((candidate) => candidate.area === 'rules' && candidate.key.file === file
    && JSON.stringify(candidate.key.path) === JSON.stringify(path));
  return change ? (change.after as { value?: unknown } | null)?.value : undefined;
};

export function RivalsTab() {
  const mode = useRivalsMode();
  const catalogue = useCatalogue();
  const [division, setDivision] = useState(1);
  const [editingRank, setEditingRank] = useState<RivalsRank | null>(null);
  const [editingPromotion, setEditingPromotion] = useState<number | null>(null);
  const changeActions = useChangesetActions();
  const busy = useContentBusy();
  const toast = useToast();
  const packs: RewardPackOption[] = useMemo(() => (catalogue.data?.packs ?? []).filter((pack) => pack.pending !== 'Delete')
    .map((pack) => ({ id: pack.id, name: pack.description ?? `Pack ${pack.id}`, pick: isPick(pack.config), href: href('packs', 'catalogue', pack.id) })), [catalogue.data]);
  const packName = (id: number) => packs.find((pack) => pack.id === id)?.name ?? `Pack ${id}`;

  if (mode.isPending || catalogue.isPending) return <Loading />;
  if (mode.isError) return <ErrorPanel what="The Rivals rewards" error={mode.error} onRetry={() => void mode.refetch()} />;
  if (catalogue.isError) return <ErrorPanel what="The pack list" error={catalogue.error} onRetry={() => void catalogue.refetch()} />;
  const data = mode.data!;
  const changes = mode.open.data?.changeset?.changes ?? [];
  const undo = (area: string, match: (key: Record<string, unknown>) => boolean) => {
    const change = changes.find((candidate) => candidate.area === area && match(candidate.key));
    if (change) changeActions.remove.mutate(change.id, { onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error') });
  };
  const ranks = data.divisions.find((entry) => entry.division === division)?.ranks ?? [];
  const shares = (stagedRule(mode.open.data, data.settingsFile, ['rankShares']) as number[] | undefined) ?? data.settings.rankShares;
  const promotionEntry = data.promotion.find((entry) => entry.division === editingPromotion);

  return (
    <div className="stack">
      <RivalsSettings data={data} open={mode.open.data} />
      {(data.divisions.some((entry) => entry.ranks.some((rank) => rank.pending)) || data.promotion.some((entry) => entry.pending))
        && <p className="notice">These rewards have pending edits. <a href={href('changes')}>Review Changes</a> to validate and publish them.</p>}
      <Section title="Weekly rewards" text={`Each division's six ranks, Rank I best. A rank offers 1 to ${data.maxOptions} rewards and the club picks one; FIFA shows them in this order.`}>
        <Chips label="Division" value={String(division)} onChange={(next) => setDivision(Number(next))}
          options={data.divisions.map((entry) => ({ value: String(entry.division), label: `Division ${entry.division}` }))} />
        <ul className="mode-ranks mt">
          {ranks.map((rank) => (
            <li key={rank.rank} className="mode-rank">
              <div className="mode-rank-head">
                <strong>Rank {rank.name}</strong>
                <span className="dim">{shares[rank.rank - 1] !== undefined ? `${shares[rank.rank - 1]}% of the division` : ''}</span>
                {rank.pending && <StagedBadge>Staged edit</StagedBadge>}
              </div>
              <ol className="mode-options">
                {rank.options.map((option, index) => (
                  <li key={index}><span className="dim">{rank.options.length > 1 ? `Option ${index + 1}: ` : ''}</span>{rewardSummary(option, packName)}</li>
                ))}
              </ol>
              <div className="mode-rank-actions">
                <Button size="sm" disabled={busy} onClick={() => setEditingRank(rank)}>Edit</Button>
                {rank.pending && <Button size="sm" variant="ghost" disabled={busy}
                  onClick={() => undo(RIVALS_RANK_AREA, (key) => Number(key.division) === division && Number(key.rank) === rank.rank)}>Undo staged change</Button>}
              </div>
            </li>
          ))}
        </ul>
      </Section>
      <Section title="Promotion rewards" text="Paid once, the first time a club reaches a division. Placement pays the placed division and every one below it.">
        <ul className="mode-ranks">
          {data.promotion.map((entry) => (
            <li key={entry.division} className="mode-rank">
              <div className="mode-rank-head">
                <strong>Division {entry.division}</strong>
                {entry.pending && <StagedBadge>Staged edit</StagedBadge>}
              </div>
              <p className="mode-rank-reward">{rewardSummary(entry.reward, packName)}</p>
              <div className="mode-rank-actions">
                <Button size="sm" disabled={busy} onClick={() => setEditingPromotion(entry.division)}>Edit</Button>
                {entry.pending && <Button size="sm" variant="ghost" disabled={busy}
                  onClick={() => undo(RIVALS_PROMOTION_AREA, (key) => Number(key.division) === entry.division)}>Undo staged change</Button>}
              </div>
            </li>
          ))}
        </ul>
      </Section>
      {editingRank && <RankOptionsEditor key={`${division}-${editingRank.rank}`} division={division} rank={editingRank} data={data} packs={packs}
        onClose={() => setEditingRank(null)} />}
      {promotionEntry && <PromotionEditor key={promotionEntry.division} division={promotionEntry.division} initial={promotionEntry.reward} data={data} packs={packs}
        onClose={() => setEditingPromotion(null)} />}
    </div>
  );
}

const SCORE_SETTINGS: { label: string; key: keyof RivalsMode['settings']; path: string[]; hint: string }[] = [
  { label: 'Win', key: 'win', path: ['weeklyScore', 'win'], hint: 'Weekly Score for a win.' },
  { label: 'Draw', key: 'draw', path: ['weeklyScore', 'draw'], hint: 'Weekly Score for a draw.' },
  { label: 'Loss', key: 'loss', path: ['weeklyScore', 'loss'], hint: 'Weekly Score for a loss.' },
  { label: 'Per goal', key: 'perGoal', path: ['weeklyScore', 'perGoal'], hint: 'Added for each goal scored, up to the goal cap.' },
  { label: 'Goal cap', key: 'goalCap', path: ['weeklyScore', 'goalCap'], hint: 'Goals per match that add Weekly Score.' },
  { label: 'Counting matches', key: 'matchCap', path: ['weeklyScore', 'matchCap'], hint: 'Matches per week that add Weekly Score (Rivals and, when on, Weekend League together).' },
  { label: 'Weekend League match score', key: 'weekendLeagueMatch', path: ['weeklyScore', 'weekendLeagueMatch'], hint: 'Weekly Score for a completed Weekend League match, whatever its result.' },
  { label: 'Pool below clubs', key: 'poolBelowClubs', path: ['poolBelowClubs'], hint: 'A division with fewer clubs than this in a week ranks on one server-wide table; 0 never pools.' },
];

function RivalsSettings({ data, open }: { data: RivalsMode; open: ChangesetReply | undefined }) {
  const actions = useModeActions();
  const busy = useContentBusy();
  const toast = useToast();
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const stage = (label: string, path: (string | number)[], value: unknown, shown: unknown, clear?: () => void) => actions.stageSetting.mutate(
    { file: data.settingsFile, path, value, shown },
    { onSuccess: () => { clear?.(); toast(`${label} saved.`, 'done'); }, onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error') });
  const countsStaged = stagedRule(open, data.settingsFile, ['weeklyScore', 'weekendLeagueCounts']) as boolean | undefined;
  const counts = countsStaged ?? data.settings.weekendLeagueCounts;
  const sharesStaged = stagedRule(open, data.settingsFile, ['rankShares']) as number[] | undefined;
  const sharesShown = sharesStaged ?? data.settings.rankShares;
  const shareDrafts = sharesShown.map((share, index) => drafts[`share.${index}`] ?? String(share));
  const shareValues = shareDrafts.map((text) => (/^\d+$/.test(text.trim()) ? Number(text.trim()) : Number.NaN));
  const shareTotal = shareValues.reduce((sum, value) => sum + (Number.isFinite(value) ? value : 0), 0);
  const sharesValid = shareValues.every((value) => Number.isSafeInteger(value) && value > 0) && shareTotal === 100;
  const sharesDirty = shareValues.some((value, index) => value !== sharesShown[index]);
  return (
    <Section title="Settings" text="Weekly Score and ranking. Schedules, divisions and placement are under Rules (Modes & seasons).">
      <SwitchRow label="Weekend League matches count" checked={counts} disabled={busy}
        text={`${counts ? 'On' : 'Off'}${countsStaged !== undefined ? ' (staged)' : ''}: a completed Weekend League match adds its Weekly Score and uses one of the week's counting matches.`}
        onChange={(next) => stage('Weekend League matches count', ['weeklyScore', 'weekendLeagueCounts'], next, counts)} />
      <div className="mode-settings mt">
        {SCORE_SETTINGS.map((setting) => {
          const key = setting.path.join('.');
          const pending = stagedRule(open, data.settingsFile, setting.path) as number | undefined;
          const shown = pending ?? (data.settings[setting.key] as number);
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
                  onClick={() => stage(setting.label, setting.path, value, shown, () => setDrafts(({ [key]: _, ...others }) => others))}>Save</Button>}
              </div>
            </div>
          );
        })}
      </div>
      <fieldset className="mode-shares">
        <legend>Rank shares (% of each division, Rank I first)</legend>
        <div className="mode-share-inputs">
          {shareDrafts.map((text, index) => (
            <label key={index}><span>Rank {['I', 'II', 'III', 'IV', 'V', 'VI'][index]}</span>
              <input type="number" inputMode="numeric" min={1} max={100} step={1} value={text}
                onChange={(event) => setDrafts({ ...drafts, [`share.${index}`]: event.target.value })} />
            </label>
          ))}
        </div>
        <div className="mode-setting-actions">
          <span className={sharesValid ? 'dim' : 'error'}>Total {shareTotal}%{sharesValid ? '' : ' (must be 100%, each at least 1%)'}</span>
          {sharesStaged && <StagedBadge />}
          {sharesDirty && <Button size="sm" variant="staged" disabled={busy || !sharesValid}
            onClick={() => stage('Rank shares', ['rankShares'], shareValues, sharesShown,
              () => setDrafts((current) => Object.fromEntries(Object.entries(current).filter(([name]) => !name.startsWith('share.')))))}>Save shares</Button>}
        </div>
      </fieldset>
    </Section>
  );
}

function RankOptionsEditor({ division, rank, data, packs, onClose }: {
  division: number;
  rank: RivalsRank;
  data: RivalsMode;
  packs: RewardPackOption[];
  onClose: () => void;
}) {
  const actions = useRivalsActions();
  const toast = useToast();
  const [options, setOptions] = useState<Reward[]>(rank.options);
  const [error, setError] = useState<string | null>(null);
  const allow = RIVALS_ALLOW(data.maxLineCount);
  const problems = options.flatMap((option, index) => rewardProblems(option, allow, packs).map((problem) => `Option ${index + 1}: ${problem}`));
  const unchanged = JSON.stringify(options.map(canonicalReward)) === JSON.stringify(rank.options.map(canonicalReward));
  const stage = () => actions.stageRank.mutate({ division, rank: rank.rank, options }, {
    onSuccess: () => { toast(`Division ${division} Rank ${rank.name} saved.`, 'done'); onClose(); },
    onError: (caught) => setError(caught instanceof Error ? caught.message : String(caught)),
  });
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose(); }} wide title={`Division ${division}, Rank ${rank.name}`}
      description="The club picks one of these rewards. Saving applies these rewards immediately."
      footer={<>
        {problems.length > 0 && <span className="dim">{problems[0]}</span>}
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="staged" disabled={actions.stageRank.isPending || problems.length > 0 || unchanged} onClick={stage}>Save rank</Button>
      </>}>
      <div className="mode-rank-editor">
        {options.map((option, index) => (
          <fieldset key={index} className="mode-option">
            <legend>{options.length > 1 ? `Option ${index + 1}` : 'Reward'} <span className="dim">({optionSource(index, options.length)})</span></legend>
            <RewardEditor value={option} onChange={(next) => setOptions(options.map((existing, at) => (at === index ? next : existing)))} packs={packs} allow={allow} />
            {options.length > 1 && <Button size="sm" variant="ghost" onClick={() => setOptions(options.filter((_, at) => at !== index))}>Remove option {index + 1}</Button>}
          </fieldset>
        ))}
        {options.length < data.maxOptions && <div><Button size="sm" onClick={() => setOptions([...options, emptyReward()])}>Add option</Button></div>}
        {error && <p className="error-box" role="alert">{error}</p>}
      </div>
    </Dialog>
  );
}

function PromotionEditor({ division, initial, data, packs, onClose }: {
  division: number;
  initial: Reward;
  data: RivalsMode;
  packs: RewardPackOption[];
  onClose: () => void;
}) {
  const actions = useRivalsActions();
  const toast = useToast();
  const [reward, setReward] = useState<Reward>(initial);
  const [error, setError] = useState<string | null>(null);
  const allow = RIVALS_ALLOW(data.maxLineCount);
  const problems = rewardProblems(reward, allow, packs);
  const unchanged = JSON.stringify(canonicalReward(reward)) === JSON.stringify(canonicalReward(initial));
  const stage = () => actions.stagePromotion.mutate({ division, reward }, {
    onSuccess: () => { toast(`Promotion to Division ${division} saved.`, 'done'); onClose(); },
    onError: (caught) => setError(caught instanceof Error ? caught.message : String(caught)),
  });
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose(); }} wide title={`Promotion to Division ${division}`}
      description="Paid once, the first time a club reaches this division. Saving applies the reward immediately."
      footer={<>
        {problems.length > 0 && <span className="dim">{problems[0]}</span>}
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="staged" disabled={actions.stagePromotion.isPending || problems.length > 0 || unchanged} onClick={stage}>Save reward</Button>
      </>}>
      <div className="mode-rank-editor">
        <RewardEditor value={reward} onChange={setReward} packs={packs} allow={allow} />
        {error && <p className="error-box" role="alert">{error}</p>}
      </div>
    </Dialog>
  );
}
