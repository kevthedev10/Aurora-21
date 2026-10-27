// Modes and their rewards (OperatorPanel.Content.cs ChampionsModeView, Core/Content/ModeRewardAreas.cs). Every mode shares one
// reward shape; a mode's ranks are content rows staged under Changes, its settings are rules. todo.md section 2.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import { changesetKey, contentActionKey, useChangeset } from './content';
import { changedRule, readRule } from './rules';
import type { ChangesetReply } from './types';

export interface RewardLine {
  packId?: number;
  cardResourceId?: number;
  count: number;
  /** The line's own choice: what the club receives. */
  untradeable: boolean;
  source?: string;
}

export interface Reward {
  coins: number;
  fcPoints: number;
  xp: number;
  lines: RewardLine[];
}

export type Range = [number, number];

export interface ChampionsRank {
  tierLevel: number;
  /** FIFA's tier number; absent for a rank that is only staged. */
  clientTier?: number;
  /** What FIFA shows: "Elite 1" for a win rank, "Band 2" for a leaderboard band. */
  fifaName?: string;
  name: string;
  wins?: Range;
  positions?: Range;
  reward: Reward;
}

export interface ChampionsSettings {
  minMatchesToRank: number;
  fcPointsCap: number;
}

export interface ChampionsModeReply {
  area: string;
  settingsFile: string;
  /** seasons: holds weekendLeague.maxMatches, the matches a Weekend League has (maxWins). */
  seasonsFile: string;
  settings: ChampionsSettings;
  maxWins: number;
  leaderboardSize: number;
  leaderboardMinimum: number;
  leaderboardMaximum: number;
  clientWinTiers: number;
  clientTopBands: number;
  maxTierLevel: number;
  maxLineCount: number;
  ranks: ChampionsRank[];
}

export type RankPending = 'New' | 'Edit' | 'Remove';
export interface ChampionsRankRow extends ChampionsRank {
  pending?: RankPending;
  /** The published rank, when it exists. */
  published?: ChampionsRank;
}

export const championsKey = ['mode-rewards', 'champions'] as const;

/** A range as people read it: "1" when it is a single number, "2–5" otherwise. */
export const span = (low: number, high: number) => (low === high ? `${low}` : `${low}–${high}`);

/** "Top 1", "Top 2–5", "1 win", "18 wins", "0–1 wins". */
export const rankRangeText = (rank: Pick<ChampionsRank, 'wins' | 'positions'>) => rank.wins
  ? `${span(rank.wins[0], rank.wins[1])} ${rank.wins[0] === rank.wins[1] && rank.wins[0] === 1 ? 'win' : 'wins'}`
  : `Top ${span(rank.positions![0], rank.positions![1])}`;
export const CHAMPIONS_AREA = 'champions_rank';

/** The rank as the server stores and compares it: key fields left out, lines without empty optional fields. */
export function canonicalRank(rank: ChampionsRank): Record<string, unknown> {
  return {
    name: rank.name,
    ...(rank.wins ? { wins: rank.wins } : { positions: rank.positions }),
    reward: canonicalReward(rank.reward),
  };
}

export const canonicalReward = (reward: Reward): Reward => ({
  coins: reward.coins, fcPoints: reward.fcPoints, xp: reward.xp,
  lines: reward.lines.map((line) => ({
    ...(line.packId !== undefined && { packId: line.packId }),
    ...(line.cardResourceId !== undefined && { cardResourceId: line.cardResourceId }),
    count: line.count, untradeable: line.untradeable,
    ...(line.source && { source: line.source }),
  })),
});

/** The published ranks with the open changeset's rank changes laid over them, by tierLevel. */
export function mergeRanks(published: ChampionsRank[], open: ChangesetReply | undefined): ChampionsRankRow[] {
  const rows = new Map<number, ChampionsRankRow>(published.map((rank) => [rank.tierLevel, { ...rank, published: rank }]));
  for (const change of open?.changeset?.changes ?? []) {
    if (change.area !== CHAMPIONS_AREA) continue;
    const tierLevel = Number(change.key.tierLevel);
    const existing = rows.get(tierLevel);
    if (change.operation === 'hide') {
      if (existing) rows.set(tierLevel, { ...existing, pending: 'Remove' });
      continue;
    }
    const after = change.after as Omit<ChampionsRank, 'tierLevel'> | null | undefined;
    if (!after) continue;
    rows.set(tierLevel, {
      ...after, tierLevel,
      ...(existing?.published && { published: existing.published, clientTier: existing.published.clientTier, fifaName: existing.published.fifaName }),
      pending: change.operation === 'insert' ? 'New' : 'Edit',
    });
  }
  return [...rows.values()].sort((a, b) => a.tierLevel - b.tierLevel);
}

/**
 * Whether the win ranks (as staged) cover 0 to maxWins exactly once, at most clientWinTiers of them, and the Top 200
 * bands run on from position 1: the checks publishing makes on the whole table. Returns the problems found.
 */
export function tableProblems(rows: ChampionsRankRow[], maxWins: number, clientWinTiers: number, clientTopBands = 6): string[] {
  const live = rows.filter((row) => row.pending !== 'Remove');
  const problems: string[] = [];
  const winRanks = live.filter((row) => row.wins);
  const covered = new Map<number, number>();
  for (const row of winRanks) for (let wins = row.wins![0]; wins <= row.wins![1]; wins++) covered.set(wins, (covered.get(wins) ?? 0) + 1);
  const missing = Array.from({ length: maxWins + 1 }, (_, wins) => wins).filter((wins) => !covered.has(wins));
  const twice = [...covered].filter(([wins, count]) => count > 1 || wins > maxWins).map(([wins]) => wins).sort((a, b) => a - b);
  if (missing.length) problems.push(`No rank covers ${missing.length === 1 ? `${missing[0]} wins` : `wins ${missing.join(', ')}`}.`);
  if (twice.length) problems.push(`More than one rank, or a rank beyond ${maxWins}, covers ${twice.join(', ')} wins.`);
  if (winRanks.length > clientWinTiers) problems.push(`FIFA shows at most ${clientWinTiers} ranks by wins; there are ${winRanks.length}.`);
  const bands = live.filter((row) => row.positions).sort((a, b) => a.positions![0] - b.positions![0]);
  if (bands.length > clientTopBands) problems.push(`FIFA shows at most ${clientTopBands} leaderboard bands; there are ${bands.length}.`);
  bands.forEach((band, index) => {
    const start = index === 0 ? 1 : bands[index - 1]!.positions![1] + 1;
    if (band.positions![0] !== start) problems.push(`${band.name} should start at position ${start}.`);
  });
  return problems;
}

/** Preview the same six fixed slots the server assigns when the Top setting publishes. */
export function previewTopRanks(rows: ChampionsRankRow[], top: number): ChampionsRankRow[] {
  const bands = rows.filter((row) => row.positions && row.pending !== 'Remove').sort((a, b) => a.positions![0] - b.positions![0]);
  if (bands.length !== 6 || !Number.isInteger(top) || top < 6 || top > 200) return rows;
  const ranges: Range[] = [[1, 1], ...Array.from({ length: 5 }, (_, i): Range => [2 + Math.floor((top - 1) * i / 5), 1 + Math.floor((top - 1) * (i + 1) / 5)])];
  const byTier = new Map(bands.map((row, i) => [row.tierLevel, ranges[i]!]));
  return rows.map((row) => {
    const positions = byTier.get(row.tierLevel);
    return positions ? { ...row, positions, name: positions[0] === positions[1] ? `Rank ${positions[0]}` : `Rank ${positions[0]}-${positions[1]}` } : row;
  });
}

export function useChampionsMode() {
  const mode = useQuery({ queryKey: championsKey, queryFn: () => api<ChampionsModeReply>('content/modes/champions') });
  const open = useChangeset();
  return {
    ...mode,
    rows: mode.data ? mergeRanks(mode.data.ranks, open.data) : undefined,
    open,
    isPending: mode.isPending || open.isPending,
  };
}

export function useModeActions() {
  const client = useQueryClient();
  const refresh = () => Promise.all([
    client.invalidateQueries({ queryKey: changesetKey }),
    client.invalidateQueries({ queryKey: championsKey }),
  ]);
  return {
    /** Stages a whole rank: update or insert its value, or hide (remove) it. */
    stageRank: useMutation({
      mutationKey: contentActionKey,
      mutationFn: ({ rank, operation }: { rank: ChampionsRank; operation: 'insert' | 'update' | 'hide' }) =>
        api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: {
          area: CHAMPIONS_AREA, key: { tierLevel: rank.tierLevel }, operation,
          after: operation === 'hide' ? null : canonicalRank(rank),
        } }),
      onSuccess: refresh,
    }),
    /** Stages one Champions setting, a rule of champions_rewards.json; refused if it changed since the page loaded. */
    stageSetting: useMutation({
      mutationKey: contentActionKey,
      mutationFn: async ({ file, path, value, shown }: { file: string; path: (string | number)[]; value: unknown; shown: unknown }) => {
        const [live, pending] = await Promise.all([readRule(file, path), api<ChangesetReply>('content/changeset')]);
        const staged = pending.changeset?.changes?.find((change) => change.area === 'rules' && change.key.file === file
          && JSON.stringify(change.key.path) === JSON.stringify(path));
        const current = staged ? (changedRule(staged)?.after as { value?: unknown } | null)?.value : live.definition?.value;
        if (JSON.stringify(current) !== JSON.stringify(shown)) throw new Error('This setting changed while the page was open. Reload it before saving.');
        return api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: { area: 'rules', key: { file, path }, operation: 'update', after: { value } } });
      },
      onSuccess: refresh,
    }),
  };
}
