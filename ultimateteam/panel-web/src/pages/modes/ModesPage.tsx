// Modes (todo.md section 2): each mode's settings and reward tables. Rewards are content rows (champions_rank, rivals_rank,
// rivals_promotion), settings
// are rules; saving an edit applies it immediately, including to the
// week in progress. Address: #/modes/<mode>.
import { useMemo, useState } from 'react';
import { useChangesetActions, useContentBusy } from '../../api/content';
import { CHAMPIONS_AREA, canonicalRank, rankRangeText, span, previewTopRanks, tableProblems, useChampionsMode, useModeActions, type ChampionsModeReply, type ChampionsRank, type ChampionsRankRow, type Range, type Reward } from '../../api/modes';
import { isPick, useCatalogue } from '../../api/packs';
import type { ChangesetReply } from '../../api/types';
import { href } from '../../app/router';
import { Badge, StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { ConfirmDialog, Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section } from '../../ui/Layout';
import { RewardEditor, rewardProblems, rewardSummary, type RewardAllow, type RewardPackOption } from '../../ui/RewardEditor';
import { Tabs } from '../../ui/Tabs';
import { useToast } from '../../ui/Toast';
import { DraftTab } from './DraftTab';
import { RivalsTab } from './RivalsTab';
import { SquadBattlesTab } from './SquadBattlesTab';
import { CompletionReward } from './CompletionReward';
import './modes.css';

type ModeId = 'champions' | 'rivals' | 'squad-battles' | 'draft';
const MODES: { value: ModeId; label: string }[] = [{ value: 'champions', label: 'Aurora Champions' }, { value: 'rivals', label: 'Division Rivals' },
  { value: 'squad-battles', label: 'Squad Battles' }, { value: 'draft', label: 'Draft' }];

export function ModesPage({ rest }: { rest: string[] }) {
  const mode: ModeId = MODES.some((entry) => entry.value === rest[0]) ? rest[0] as ModeId : 'champions';
  return (
    <>
      <PageHeader title="Modes" description="Each mode's settings and rewards. Saving applies edits immediately, including to the week in progress. Results already settled keep the reward they were given." />
      <Tabs label="Modes" items={MODES} value={mode} onChange={(next) => { window.location.hash = href('modes', next); }}>
        <div className="stack">
          {mode !== 'draft' && <CompletionReward key={mode} mode={mode === 'champions' ? 'weekendLeague' : mode === 'squad-battles' ? 'squadBattles' : 'rivals'} />}
          {mode === 'champions' && <ChampionsTab />}
          {mode === 'rivals' && <RivalsTab />}
          {mode === 'squad-battles' && <SquadBattlesTab />}
          {mode === 'draft' && <DraftTab />}
        </div>
      </Tabs>
    </>
  );
}

const CHAMPIONS_ALLOW = (maxCount: number): RewardAllow => ({ fcPoints: true, xp: false, maxCount });
const rangeText = rankRangeText;

function ChampionsTab() {
  const mode = useChampionsMode();
  const catalogue = useCatalogue();
  const [editing, setEditing] = useState<{ rank: ChampionsRank; isNew: boolean } | null>(null);
  const [removing, setRemoving] = useState<ChampionsRankRow | null>(null);
  const actions = useModeActions();
  const changeActions = useChangesetActions();
  const busy = useContentBusy();
  const toast = useToast();
  const packs: RewardPackOption[] = useMemo(() => (catalogue.data?.packs ?? []).filter((pack) => pack.pending !== 'Delete')
    .map((pack) => ({ id: pack.id, name: pack.description ?? `Pack ${pack.id}`, pick: isPick(pack.config), href: href('packs', 'catalogue', pack.id) })), [catalogue.data]);
  const packName = (id: number) => packs.find((pack) => pack.id === id)?.name ?? `Pack ${id}`;

  if (mode.isPending || catalogue.isPending) return <Loading />;
  if (mode.isError) return <ErrorPanel what="The Champions rewards" error={mode.error} onRetry={() => void mode.refetch()} />;
  if (catalogue.isError) return <ErrorPanel what="The pack list" error={catalogue.error} onRetry={() => void catalogue.refetch()} />;
  const data = mode.data!;
  const top = stagedNumber(mode.open.data, data.seasonsFile, TOP_PATH) ?? data.leaderboardSize;
  const rows = top === data.leaderboardSize ? mode.rows ?? [] : previewTopRanks(mode.rows ?? [], top);
  // A staged match count is what the ranks must cover when both publish together.
  const maxWins = stagedNumber(mode.open.data, data.seasonsFile, MATCHES_PATH) ?? data.maxWins;
  const problems = tableProblems(rows, maxWins, data.clientWinTiers, data.clientTopBands);
  const changeFor = (tierLevel: number) => mode.open.data?.changeset?.changes?.find((change) => change.area === CHAMPIONS_AREA && Number(change.key.tierLevel) === tierLevel);
  const undo = (row: ChampionsRankRow) => {
    const change = changeFor(row.tierLevel);
    if (change) changeActions.remove.mutate(change.id, { onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error') });
  };
  const table = (title: string, text: string, list: ChampionsRankRow[]) => (
    <Section title={title} text={text}>
      {!list.length ? <EmptyState title="No ranks" /> : (
        <ul className="mode-ranks">
          {list.map((row) => (
            <li key={row.tierLevel} className={row.pending === 'Remove' ? 'mode-rank removed' : 'mode-rank'}>
              <div className="mode-rank-head">
                <strong>{row.name}</strong>
                <span className="dim">{rangeText(row)}</span>
                {row.fifaName && <Badge title="What FIFA shows for this rank">FIFA: {row.fifaName}</Badge>}
                {row.pending && <StagedBadge>{row.pending === 'Remove' ? 'Staged removal' : row.pending === 'New' ? 'Staged new rank' : 'Staged edit'}</StagedBadge>}
              </div>
              <p className="mode-rank-reward">{rewardSummary(row.reward, packName)}</p>
              <div className="mode-rank-actions">
                {row.pending !== 'Remove' && <Button size="sm" disabled={busy} onClick={() => setEditing({ rank: row.positions && row.published ? { ...row, positions: row.published.positions!, name: row.published.name } : row, isNew: !row.published })}>Edit</Button>}
                {row.pending ? <Button size="sm" variant="ghost" disabled={busy} onClick={() => undo(row)}>Undo staged change</Button>
                  : !row.positions && <Button size="sm" variant="ghost" disabled={busy} onClick={() => setRemoving(row)}>Remove</Button>}
              </div>
            </li>
          ))}
        </ul>
      )}
    </Section>
  );

  return (
    <div className="stack">
      <ChampionsSettings data={data} open={mode.open.data} />
      {problems.length > 0 && (
        <div className="notice" role="status">
          <strong>Saving will be refused until the table is complete:</strong>
          <ul>{problems.map((problem) => <li key={problem}>{problem}</li>)}</ul>
        </div>
      )}
      {rows.some((row) => row.pending) && <p className="notice">These rewards have pending edits. <a href={href('changes')}>Review Changes</a> to validate and publish them.</p>}
      {table('Leaderboard bands', `Top ${top}, across six permanent reward slots. Rank 1 stands alone; the remaining positions are divided across five slots. Changing Top players updates their ranges automatically and preserves their rewards. Ranked players receive these rewards by leaderboard position, with no minimum wins.`,
        rows.filter((row) => row.positions))}
      {table('Ranks by wins', `By wins out of ${maxWins}. They must cover 0 to ${maxWins} wins exactly once; FIFA shows ${data.clientWinTiers} and names them itself, best first.`,
        rows.filter((row) => row.wins))}
      {editing && <RankEditor key={editing.rank.tierLevel} initial={editing.rank} isNew={editing.isNew} data={{ ...data, maxWins }} packs={packs}
        onClose={() => setEditing(null)}
        onStage={async (rank) => {
          await actions.stageRank.mutateAsync({ rank, operation: editing.isNew ? 'insert' : 'update' });
          toast(`${rank.name} saved.`, 'done');
          setEditing(null);
        }} />}
      <ConfirmDialog open={!!removing} onOpenChange={(open) => { if (!open) setRemoving(null); }} title={`Remove ${removing?.name ?? 'rank'}?`}
        text="The rank and its reward are removed immediately. Results already settled keep the reward they were given." confirmLabel="Remove rank" variant="danger"
        onConfirm={async () => { if (removing) await actions.stageRank.mutateAsync({ rank: removing, operation: 'hide' }); }} />
    </div>
  );
}

const TOP_PATH = ['weekendLeague', 'topX'];
const MATCHES_PATH = ['weekendLeague', 'maxMatches'];
/** A staged numeric rule's value, or undefined when that rule has no pending edit. */
const stagedNumber = (open: ChangesetReply | undefined, file: string, path: (string | number)[]) => {
  const change = open?.changeset?.changes?.find((candidate) => candidate.area === 'rules' && candidate.key.file === file
    && JSON.stringify(candidate.key.path) === JSON.stringify(path));
  return change ? Number((change.after as { value?: unknown } | null)?.value) : undefined;
};

const SETTINGS: { label: string; file: (data: ChampionsModeReply) => string; path: (string | number)[]; hint: string; read: (data: ChampionsModeReply) => number }[] = [
  { label: 'Matches per Weekend League', file: (data) => data.seasonsFile, path: MATCHES_PATH, read: (data) => data.maxWins,
    hint: 'The ranks by wins must cover 0 to this number. The server checks their ranges when saving. A change applies to the Weekend League in progress too.' },
  { label: 'Matches to rank', file: (data) => data.settingsFile, path: ['minMatchesToRank'], hint: 'A club needs this many matches to be ranked at all.', read: (data) => data.settings.minMatchesToRank },
  { label: 'Aurora Champions Points cap', file: (data) => data.settingsFile, path: ['fcPointsCap'], hint: 'Aurora Champions Points a club can hold; more is lost.', read: (data) => data.settings.fcPointsCap },
];

function ChampionsSettings({ data, open }: { data: ChampionsModeReply; open: ChangesetReply | undefined }) {
  const actions = useModeActions();
  const busy = useContentBusy();
  const toast = useToast();
  const [topOpen, setTopOpen] = useState(false);
  const [topDraft, setTopDraft] = useState('');
  const topStaged = stagedNumber(open, data.seasonsFile, TOP_PATH);
  const topShown = topStaged ?? data.leaderboardSize;
  const topValue = Number(topDraft);
  const topValid = /^\d+$/.test(topDraft) && Number.isInteger(topValue) && topValue >= data.leaderboardMinimum && topValue <= data.leaderboardMaximum;
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  return (
    <Section title="Settings" text="Weekend League matches and ranking. The Weekend League schedule and entry cost are under Rules (Modes & seasons).">
      <Dialog open={topOpen} onOpenChange={setTopOpen} title="Top players" description="Six reward slots are permanent. Changing the number adjusts their position ranges and keeps each slot’s rewards. Saving applies the change immediately. Players need the updated client mod and a game restart for the tab caption."
        footer={<><Button variant="ghost" onClick={() => setTopOpen(false)}>Cancel</Button><Button variant="staged" disabled={busy || !topValid || topValue === topShown} onClick={() => actions.stageSetting.mutate(
          { file: data.seasonsFile, path: TOP_PATH, value: topValue, shown: topShown },
          { onSuccess: () => { setTopOpen(false); toast('Top players and reward ranges saved.', 'done'); }, onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error') })}>Save Top players</Button></>}>
        <Field label="Number of top players" hint={`From ${data.leaderboardMinimum} to ${data.leaderboardMaximum}.`} error={!topValid ? `Enter a whole number from ${data.leaderboardMinimum} to ${data.leaderboardMaximum}.` : null}>
          <input type="number" min={data.leaderboardMinimum} max={data.leaderboardMaximum} step={1} value={topDraft} onChange={(event) => setTopDraft(event.target.value)} />
        </Field>
      </Dialog>
      <div className="mode-settings">
        <div className="mode-setting">
          <Field label="Top players" hint={`Rank 1, then five reward slots over places 2–${topShown}. Their places follow this number.`}>
            <input type="number" readOnly value={topShown} />
          </Field>
          <div className="mode-setting-actions">
            {topStaged !== undefined && <StagedBadge />}
            <Button size="sm" disabled={busy} aria-label="Change Top players" onClick={() => { setTopDraft(String(topShown)); setTopOpen(true); }}>Change…</Button>
          </div>
        </div>
        {SETTINGS.map((setting) => {
          const file = setting.file(data);
          const key = `${file}.${setting.path.join('.')}`;
          const pendingValue = stagedNumber(open, file, setting.path);
          const change = pendingValue !== undefined;
          const shown = pendingValue ?? setting.read(data);
          const draft = drafts[key] ?? String(shown);
          const value = /^\d+$/.test(draft.trim()) ? Number(draft.trim()) : Number.NaN;
          const dirty = draft.trim() !== String(shown);
          return (
            <div key={key} className="mode-setting">
              <Field label={setting.label} hint={setting.hint} error={dirty && !Number.isSafeInteger(value) ? 'A whole number from 0.' : null}>
                <input type="number" inputMode="numeric" min={0} step={1} value={draft} onChange={(event) => setDrafts({ ...drafts, [key]: event.target.value })} />
              </Field>
              <div className="mode-setting-actions">
                {change && <StagedBadge />}
                {dirty && <Button size="sm" variant="staged" disabled={busy || !Number.isSafeInteger(value)} onClick={() => actions.stageSetting.mutate(
                  { file, path: setting.path, value, shown },
                  { onSuccess: () => { setDrafts(({ [key]: _, ...others }) => others); toast(`${setting.label} saved.`, 'done'); },
                    onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error') })}>Save</Button>}
              </div>
            </div>
          );
        })}
      </div>
    </Section>
  );
}

function RankEditor({ initial, isNew, data, packs, onClose, onStage }: {
  initial: ChampionsRank;
  isNew: boolean;
  data: ChampionsModeReply;
  packs: RewardPackOption[];
  onClose: () => void;
  onStage: (rank: ChampionsRank) => Promise<void>;
}) {
  // Names and Top positions are set automatically (FIFA's tier names; the Top size divides the leaderboard places), so
  // only a win rank's wins and every rank's reward are edited here.
  const byWins = !initial.positions;
  const [wins, setWins] = useState<Range>(initial.wins ?? [0, 0]);
  const [reward, setReward] = useState<Reward>(initial.reward);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const allow = CHAMPIONS_ALLOW(data.maxLineCount);
  const problems = [
    ...(byWins && (!Number.isSafeInteger(wins[0]) || !Number.isSafeInteger(wins[1]) || wins[0] < 0 || wins[0] > wins[1])
      ? ['The wins run from 0, the first not above the last.'] : []),
    ...(byWins && wins[1] > data.maxWins ? [`A Weekend League has at most ${data.maxWins} wins.`] : []),
    ...rewardProblems(reward, allow, packs),
  ];
  const rank: ChampionsRank = { tierLevel: initial.tierLevel, name: initial.name, ...(byWins ? { wins } : { positions: initial.positions! }), reward };
  const unchanged = !isNew && JSON.stringify(canonicalRank(rank)) === JSON.stringify(canonicalRank(initial));
  const bound = (index: 0 | 1, text: string) => {
    const value = /^\d+$/.test(text) ? Number(text) : Number.NaN;
    setWins(index === 0 ? [value, wins[1]] : [wins[0], value]);
  };
  const stage = async () => {
    setSaving(true);
    setError(null);
    try { await onStage(rank); } catch (caught) { setError(caught instanceof Error ? caught.message : String(caught)); } finally { setSaving(false); }
  };
  const description = byWins
    ? 'Saving applies these rewards immediately.'
    : `Leaderboard ${initial.positions![0] === initial.positions![1] ? 'place' : 'places'} ${span(initial.positions![0], initial.positions![1])}, set by the Top size (${data.leaderboardSize}). Saving applies these rewards immediately.`;
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose(); }} wide title={`Edit ${initial.name}`} description={description}
      footer={<>
        {problems.length > 0 && <span className="dim">{problems[0]}</span>}
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="staged" disabled={saving || problems.length > 0 || unchanged} onClick={() => void stage()}>Save rank</Button>
      </>}>
      <div className="mode-rank-editor">
        {byWins && (
          <div className="mode-rank-fields">
            <Field label="From wins">
              <input type="number" inputMode="numeric" min={0} step={1} value={Number.isFinite(wins[0]) ? wins[0] : ''} onChange={(event) => bound(0, event.target.value.trim())} />
            </Field>
            <Field label="To wins">
              <input type="number" inputMode="numeric" min={0} step={1} value={Number.isFinite(wins[1]) ? wins[1] : ''} onChange={(event) => bound(1, event.target.value.trim())} />
            </Field>
          </div>
        )}
        <RewardEditor value={reward} onChange={setReward} packs={packs} allow={allow} disabled={saving} />
        {problems.length > 1 && <ul className="mode-problems">{problems.map((problem) => <li key={problem}>{problem}</li>)}</ul>}
        {error && <p className="error-box" role="alert">{error}</p>}
      </div>
    </Dialog>
  );
}
