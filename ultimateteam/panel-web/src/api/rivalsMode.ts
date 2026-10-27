// Modes → Rivals (OperatorPanel.Content.cs RivalsModeViewAsync, Core/Content/RivalsRewardAreas.cs): each division's six ranks of
// 1-4 weekly reward options (area rivals_rank) and each division's promotion reward (area rivals_promotion), staged under
// Changes; the settings are rules of rivals_rewards.json. todo.md section 2.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import { changesetKey, contentActionKey, useChangeset } from './content';
import { canonicalReward, type Reward } from './modes';
import type { ChangesetReply } from './types';

export interface RivalsSettings {
  win: number;
  draw: number;
  loss: number;
  perGoal: number;
  goalCap: number;
  matchCap: number;
  weekendLeagueCounts: boolean;
  weekendLeagueMatch: number;
  rankShares: number[];
  poolBelowClubs: number;
}

export interface RivalsRank {
  rank: number;
  /** I to VI. */
  name: string;
  options: Reward[];
  pending?: boolean;
}

export interface RivalsDivision {
  division: number;
  ranks: RivalsRank[];
}

export interface RivalsPromotion {
  division: number;
  reward: Reward;
  pending?: boolean;
}

interface RivalsModeReply {
  area: string;
  promotionArea: string;
  settingsFile: string;
  settings: RivalsSettings;
  maxOptions: number;
  maxLineCount: number;
  divisions: { division: number; ranks: { rank: number; name: string; row: { options: Reward[] } }[] }[];
  promotion: { division: number; reward: Reward }[];
}

export interface RivalsMode {
  area: string;
  promotionArea: string;
  settingsFile: string;
  settings: RivalsSettings;
  maxOptions: number;
  maxLineCount: number;
  divisions: RivalsDivision[];
  promotion: RivalsPromotion[];
}

export const rivalsKey = ['mode-rewards', 'rivals'] as const;
export const RIVALS_RANK_AREA = 'rivals_rank';
export const RIVALS_PROMOTION_AREA = 'rivals_promotion';

/** The published tables with the open changeset's rank and promotion changes laid over them. */
export function mergeRivals(reply: RivalsModeReply, open: ChangesetReply | undefined): RivalsMode {
  const changes = open?.changeset?.changes ?? [];
  const staged = (area: string, match: (key: Record<string, unknown>) => boolean) =>
    changes.find((change) => change.area === area && change.operation === 'update' && match(change.key));
  return {
    ...reply,
    divisions: reply.divisions.map((division) => ({
      division: division.division,
      ranks: division.ranks.map((rank) => {
        const change = staged(RIVALS_RANK_AREA, (key) => Number(key.division) === division.division && Number(key.rank) === rank.rank);
        const after = change?.after as { options?: Reward[] } | null | undefined;
        return { rank: rank.rank, name: rank.name, options: after?.options ?? rank.row.options, ...(after?.options && { pending: true }) };
      }),
    })),
    promotion: reply.promotion.map((entry) => {
      const change = staged(RIVALS_PROMOTION_AREA, (key) => Number(key.division) === entry.division);
      const after = change?.after as { reward?: Reward } | null | undefined;
      return { division: entry.division, reward: after?.reward ?? entry.reward, ...(after?.reward && { pending: true }) };
    }),
  };
}

export function useRivalsMode() {
  const mode = useQuery({ queryKey: rivalsKey, queryFn: () => api<RivalsModeReply>('content/modes/rivals') });
  const open = useChangeset();
  return {
    ...mode,
    data: mode.data ? mergeRivals(mode.data, open.data) : undefined,
    open,
    isPending: mode.isPending || open.isPending,
  };
}

export function useRivalsActions() {
  const client = useQueryClient();
  const refresh = () => Promise.all([client.invalidateQueries({ queryKey: changesetKey }), client.invalidateQueries({ queryKey: rivalsKey })]);
  return {
    stageRank: useMutation({
      mutationKey: contentActionKey,
      mutationFn: ({ division, rank, options }: { division: number; rank: number; options: Reward[] }) =>
        api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: {
          area: RIVALS_RANK_AREA, key: { division, rank }, operation: 'update', after: { options: options.map(canonicalReward) },
        } }),
      onSuccess: refresh,
    }),
    stagePromotion: useMutation({
      mutationKey: contentActionKey,
      mutationFn: ({ division, reward }: { division: number; reward: Reward }) =>
        api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: {
          area: RIVALS_PROMOTION_AREA, key: { division }, operation: 'update', after: { reward: canonicalReward(reward) },
        } }),
      onSuccess: refresh,
    }),
  };
}
