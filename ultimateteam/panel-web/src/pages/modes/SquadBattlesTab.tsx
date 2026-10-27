// Modes → Squad Battles (todo.md section 2): the settings (sqbt_rewards.json, and the Top players number in squad_battles.json),
// the six Top slots whose places follow the Top players number, and the twelve points tiers, saved immediately.
import { useMemo, useState } from 'react';
import { useChangesetActions, useContentBusy } from '../../api/content';
import { canonicalReward, useModeActions, type Reward } from '../../api/modes';
import { isPick, useCatalogue } from '../../api/packs';
import { SQUAD_BATTLES_AREA, useSquadBattlesActions, useSquadBattlesMode, type SquadBattlesMode, type SquadBattlesTier } from '../../api/squadBattlesMode';
import type { ChangesetReply } from '../../api/types';
import { href } from '../../app/router';
import { StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { ErrorPanel, Loading, Section } from '../../ui/Layout';
import { RewardEditor, rewardProblems, rewardSummary, type RewardAllow, type RewardPackOption } from '../../ui/RewardEditor';
import { SwitchRow } from '../../ui/Switch';
import { useToast } from '../../ui/Toast';

const SQUAD_BATTLES_ALLOW = (maxCount: number): RewardAllow => ({ fcPoints: false, xp: false, maxCount });
const span = (low: number, high: number) => (low === high ? `${low}` : `${low}–${high}`);

/** The six Top slots' places for a Top size: Rank 1, then the other places split over five slots (as the server places them). */
export const topSlots = (size: number): [number, number][] =>
  [[1, 1], ...Array.from({ length: 5 }, (_, index) => [2 + Math.floor(((size - 1) * index) / 5), 1 + Math.floor(((size - 1) * (index + 1)) / 5)] as [number, number])];

const stagedRule = (open: ChangesetReply | undefined, file: string, path: (string | number)[]) => {
  const change = open?.changeset?.changes?.find((candidate) => candidate.area === 'rules' && candidate.key.file === file
    && JSON.stringify(candidate.key.path) === JSON.stringify(path));
  return change ? (change.after as { value?: unknown } | null)?.value : undefined;
};

export function SquadBattlesTab() {
  const mode = useSquadBattlesMode();
  const catalogue = useCatalogue();
  const [editing, setEditing] = useState<SquadBattlesTier | null>(null);
  const changeActions = useChangesetActions();
  const busy = useContentBusy();
  const toast = useToast();
  const packs: RewardPackOption[] = useMemo(() => (catalogue.data?.packs ?? []).filter((pack) => pack.pending !== 'Delete')
    .map((pack) => ({ id: pack.id, name: pack.description ?? `Pack ${pack.id}`, pick: isPick(pack.config), href: href('packs', 'catalogue', pack.id) })), [catalogue.data]);
  const packName = (id: number) => packs.find((pack) => pack.id === id)?.name ?? `Pack ${id}`;

  if (mode.isPending || catalogue.isPending) return <Loading />;
  if (mode.isError) return <ErrorPanel what="The Squad Battles rewards" error={mode.error} onRetry={() => void mode.refetch()} />;
  if (catalogue.isError) return <ErrorPanel what="The pack list" error={catalogue.error} onRetry={() => void catalogue.refetch()} />;
  const data = mode.data!;
  const changes = mode.open.data?.changeset?.changes ?? [];
  const stagedBoard = stagedRule(mode.open.data, data.boardFile, ['competition', 'boardSize']) as number | undefined;
  const board = stagedBoard ?? data.boardSize;
  const slots = topSlots(board);
  const ranked = data.tiers.filter((tier) => tier.kind === 'ranked').sort((a, b) => a.rankStart - b.rankStart)
    .map((tier, index) => (stagedBoard === undefined ? tier : { ...tier, rankStart: slots[index]![0], rankEnd: slots[index]![1] }));
  const points = data.tiers.filter((tier) => tier.kind === 'points').sort((a, b) => a.tierLevel - b.tierLevel);
  const undo = (tier: SquadBattlesTier) => {
    const change = changes.find((candidate) => candidate.area === SQUAD_BATTLES_AREA && candidate.key.tierId === tier.tierId);
    if (change) changeActions.remove.mutate(change.id, { onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error') });
  };
  const row = (tier: SquadBattlesTier, title: string, detail: string) => (
    <li key={tier.tierId} className="mode-rank">
      <div className="mode-rank-head">
        <strong>{title}</strong>
        <span className="dim">{detail}</span>
        {tier.pending && <StagedBadge>Staged edit</StagedBadge>}
      </div>
      <p className="mode-rank-reward">{rewardSummary(tier.reward, packName)}</p>
      <div className="mode-rank-actions">
        <Button size="sm" disabled={busy} onClick={() => setEditing(tier)}>Edit</Button>
        {tier.pending && <Button size="sm" variant="ghost" disabled={busy} onClick={() => undo(tier)}>Undo staged change</Button>}
      </div>
    </li>
  );

  return (
    <div className="stack">
      <SquadBattlesSettings data={data} open={mode.open.data} />
      {data.tiers.some((tier) => tier.pending) && <p className="notice">These rewards have pending edits. <a href={href('changes')}>Review Changes</a> to validate and publish them.</p>}
      <Section title={`Top ${board}`} text={`Six Top slots paid by leaderboard position once ${num(data.settings.rankedMinParticipants)} clubs have points. Their places follow the Top players number${stagedBoard !== undefined ? ' (as staged)' : ''}.`}>
        <ul className="mode-ranks">
          {ranked.map((tier) => row(tier, tier.rankStart === tier.rankEnd ? `Rank ${tier.rankStart}` : `Rank ${span(tier.rankStart, tier.rankEnd)}`, `Top ${span(tier.rankStart, tier.rankEnd)}`))}
        </ul>
      </Section>
      <Section title="Tiers" text={data.settings.mode === 'percentile'
        ? 'Everyone else, by percentile band of the clubs with points, each also needing its minimum points (retail).'
        : 'Everyone else, by minimum points alone.'}>
        <ul className="mode-ranks">
          {points.map((tier) => row(tier, tier.name, data.settings.mode === 'percentile'
            ? `Top ${span(tier.percentStart, tier.percentEnd)}% · at least ${num(tier.minPoints)} points`
            : `At least ${num(tier.minPoints)} points`))}
        </ul>
      </Section>
      {editing && <TierEditor key={editing.tierId} initial={editing} data={data} packs={packs} onClose={() => setEditing(null)} />}
    </div>
  );
}

function SquadBattlesSettings({ data, open }: { data: SquadBattlesMode; open: ChangesetReply | undefined }) {
  const actions = useModeActions();
  const busy = useContentBusy();
  const toast = useToast();
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [boardOpen, setBoardOpen] = useState(false);
  const [boardDraft, setBoardDraft] = useState('');
  const stage = (label: string, file: string, path: (string | number)[], value: unknown, shown: unknown, done?: () => void) => actions.stageSetting.mutate(
    { file, path, value, shown },
    { onSuccess: () => { done?.(); toast(`${label} saved.`, 'done'); }, onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error') });
  const boardStaged = stagedRule(open, data.boardFile, ['competition', 'boardSize']) as number | undefined;
  const boardShown = boardStaged ?? data.boardSize;
  const boardValue = Number(boardDraft);
  const boardValid = /^\d+$/.test(boardDraft) && boardValue >= data.boardMinimum && boardValue <= data.boardMaximum;
  const modeStaged = stagedRule(open, data.settingsFile, ['mode']) as string | undefined;
  const modeShown = modeStaged ?? data.settings.mode;
  const totwStaged = stagedRule(open, data.totwFile, ['current']) as number | undefined;
  const totwShown = totwStaged ?? data.totw.current;
  const rankedStaged =stagedRule(open, data.settingsFile, ['rankedTiers', 'enabled']) as boolean | undefined;
  const rankedShown = rankedStaged ?? data.settings.rankedEnabled;
  const numbers: { label: string; path: string[]; hint: string; read: number }[] = [
    { label: 'Minimum points to qualify', path: ['minPointsToQualify'], hint: 'A club needs this many points in a week to be placed at all.', read: data.settings.minPointsToQualify },
    { label: 'Clubs before Top slots pay', path: ['rankedTiers', 'minParticipants'], hint: 'The Top slots pay once this many clubs have points.', read: data.settings.rankedMinParticipants },
  ];
  return (
    <Section title="Settings" text="Weekly ranking. Matches, opponents and difficulty are under Rules (Squad Battles).">
      <div className="mode-settings">
        <div className="mode-setting">
          <Field label="Top players" hint={`Rank 1, then five reward slots over places 2–${boardShown}. The leaderboard shows this many players.`}>
            <input type="number" readOnly value={boardShown} />
          </Field>
          <div className="mode-setting-actions">
            {boardStaged !== undefined && <StagedBadge />}
            <Button size="sm" disabled={busy} aria-label="Change Squad Battles Top players" onClick={() => { setBoardDraft(String(boardShown)); setBoardOpen(true); }}>Change…</Button>
          </div>
        </div>
        <div className="mode-setting">
          <Field label="Tiers by" hint="Percentile: retail bands of the clubs with points, each with a minimum. Points: minimum points alone.">
            <select value={modeShown} disabled={busy} onChange={(event) => stage('Tiers by', data.settingsFile, ['mode'], event.target.value, modeShown)}>
              <option value="percentile">Percentile</option>
              <option value="points">Points</option>
            </select>
          </Field>
          <div className="mode-setting-actions">{modeStaged !== undefined && <StagedBadge />}</div>
        </div>
        <div className="mode-setting">
          <Field label="Team of the Week" hint="The TOTW players can play from the Squad Battles hub. Changing it mid-week lets every club score the new one once as well.">
            <select aria-label="Active Team of the Week" value={totwShown} disabled={busy}
              onChange={(event) => stage('Team of the Week', data.totwFile, ['current'], Number(event.target.value), totwShown)}>
              <option value={0}>Off (no TOTW tile)</option>
              {[...data.totw.weeks].reverse().map((week) => (
                <option key={week.week} value={week.week}>{`TOTW ${week.week} · ${week.released}${week.headline ? ` · ${week.headline}` : ''}`}</option>
              ))}
            </select>
          </Field>
          <div className="mode-setting-actions">{totwStaged !== undefined && <StagedBadge />}</div>
        </div>
        {numbers.map((setting) => {
          const key = setting.path.join('.');
          const pending = stagedRule(open, data.settingsFile, setting.path) as number | undefined;
          const shown = pending ?? setting.read;
          const draft = drafts[key] ?? String(shown);
          const value = /^\d+$/.test(draft.trim()) ? Number(draft.trim()) : Number.NaN;
          const dirty = draft.trim() !== String(shown);
          return (
            <div key={key} className="mode-setting">
              <Field label={setting.label} hint={setting.hint} error={dirty && !(Number.isSafeInteger(value) && value >= 1) ? 'A whole number from 1.' : null}>
                <input type="number" inputMode="numeric" min={1} step={1} value={draft} onChange={(event) => setDrafts({ ...drafts, [key]: event.target.value })} />
              </Field>
              <div className="mode-setting-actions">
                {pending !== undefined && <StagedBadge />}
                {dirty && <Button size="sm" variant="staged" disabled={busy || !(Number.isSafeInteger(value) && value >= 1)}
                  onClick={() => stage(setting.label, data.settingsFile, setting.path, value, shown, () => setDrafts(({ [key]: _, ...others }) => others))}>Save</Button>}
              </div>
            </div>
          );
        })}
      </div>
      <SwitchRow label="Top slots pay" checked={rankedShown} disabled={busy}
        text={`${rankedShown ? 'On' : 'Off'}${rankedStaged !== undefined ? ' (staged)' : ''}: off pays everyone by tier.`}
        onChange={(next) => stage('Top slots pay', data.settingsFile, ['rankedTiers', 'enabled'], next, rankedShown)} />
      <Dialog open={boardOpen} onOpenChange={setBoardOpen} title="Squad Battles Top players"
        description="Six reward slots are permanent. Changing the number moves their places and keeps each slot's reward. Players need the updated client mod and a game restart for the tab caption."
        footer={<><Button variant="ghost" onClick={() => setBoardOpen(false)}>Cancel</Button>
          <Button variant="staged" disabled={busy || !boardValid || boardValue === boardShown}
            onClick={() => stage('Squad Battles Top players', data.boardFile, ['competition', 'boardSize'], boardValue, boardShown, () => setBoardOpen(false))}>Save Top players</Button></>}>
        <Field label="Number of top players" hint={`From ${data.boardMinimum} to ${data.boardMaximum}.`}
          error={!boardValid ? `Enter a whole number from ${data.boardMinimum} to ${data.boardMaximum}.` : null}>
          <input type="number" min={data.boardMinimum} max={data.boardMaximum} step={1} value={boardDraft} onChange={(event) => setBoardDraft(event.target.value)} />
        </Field>
      </Dialog>
    </Section>
  );
}

function TierEditor({ initial, data, packs, onClose }: { initial: SquadBattlesTier; data: SquadBattlesMode; packs: RewardPackOption[]; onClose: () => void }) {
  const actions = useSquadBattlesActions();
  const toast = useToast();
  const [reward, setReward] = useState<Reward>(initial.reward);
  const [percentStart, setPercentStart] = useState(String(initial.percentStart));
  const [percentEnd, setPercentEnd] = useState(String(initial.percentEnd));
  const [minPoints, setMinPoints] = useState(String(initial.minPoints));
  const [error, setError] = useState<string | null>(null);
  const ranked = initial.kind === 'ranked';
  const whole = (text: string) => (/^\d+$/.test(text.trim()) ? Number(text.trim()) : Number.NaN);
  const tier: SquadBattlesTier = ranked ? { ...initial, reward } : { ...initial, reward, percentStart: whole(percentStart), percentEnd: whole(percentEnd), minPoints: whole(minPoints) };
  const allow = SQUAD_BATTLES_ALLOW(data.maxLineCount);
  const problems = [
    ...(!ranked && !(tier.percentStart >= 1 && tier.percentEnd <= 100 && tier.percentStart <= tier.percentEnd) ? ['The percent band runs from 1 to 100, the first not above the last.'] : []),
    ...(!ranked && !(Number.isSafeInteger(tier.minPoints) && tier.minPoints >= 0) ? ['Minimum points are a whole number from 0.'] : []),
    ...rewardProblems(reward, allow, packs),
  ];
  const unchanged = JSON.stringify(canonicalReward(reward)) === JSON.stringify(canonicalReward(initial.reward))
    && (ranked || (tier.percentStart === initial.percentStart && tier.percentEnd === initial.percentEnd && tier.minPoints === initial.minPoints));
  const stage = () => actions.stageTier.mutate(tier, {
    onSuccess: () => { toast(`${initial.name} saved.`, 'done'); onClose(); },
    onError: (caught) => setError(caught instanceof Error ? caught.message : String(caught)),
  });
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose(); }} wide title={`Edit ${initial.name}`}
      description={ranked ? `Leaderboard ${initial.rankStart === initial.rankEnd ? 'place' : 'places'} ${span(initial.rankStart, initial.rankEnd)}, set by the Top players number. Saving applies the reward immediately.`
        : 'The whole table is checked before saving (bands run on to 100%, minimums fall down the list in points mode).'}
      footer={<>
        {problems.length > 0 && <span className="dim">{problems[0]}</span>}
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="staged" disabled={actions.stageTier.isPending || problems.length > 0 || unchanged} onClick={stage}>Save tier</Button>
      </>}>
      <div className="mode-rank-editor">
        {!ranked && (
          <div className="mode-rank-fields mode-tier-fields">
            <Field label="From percent"><input type="number" inputMode="numeric" min={1} max={100} step={1} value={percentStart} onChange={(event) => setPercentStart(event.target.value)} /></Field>
            <Field label="To percent"><input type="number" inputMode="numeric" min={1} max={100} step={1} value={percentEnd} onChange={(event) => setPercentEnd(event.target.value)} /></Field>
            <Field label="Minimum points"><input type="number" inputMode="numeric" min={0} step={1} value={minPoints} onChange={(event) => setMinPoints(event.target.value)} /></Field>
          </div>
        )}
        <RewardEditor value={reward} onChange={setReward} packs={packs} allow={allow} />
        {error && <p className="error-box" role="alert">{error}</p>}
      </div>
    </Dialog>
  );
}
