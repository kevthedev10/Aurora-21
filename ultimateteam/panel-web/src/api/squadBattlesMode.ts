// Modes → Squad Battles (OperatorPanel.Content.cs SquadBattlesModeViewAsync, Core/Content/SquadBattlesRewardAreas.cs): six Top slots
// whose places follow the Top players number, and the twelve points tiers, each a content row of area squad_battles_tier; the
// settings are rules of sqbt_rewards.json and squad_battles.json (the Top players number). todo.md section 2.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import { changesetKey, contentActionKey, useChangeset } from './content';
import { canonicalReward, type Reward } from './modes';
import type { ChangesetReply } from './types';

export interface SquadBattlesTier {
  tierId: string;
  /** "Rank 2-5" for a Top slot, FIFA's "Elite 1" for a points tier. */
  name: string;
  kind: 'ranked' | 'points';
  rankStart: number;
  rankEnd: number;
  tierLevel: number;
  percentStart: number;
  percentEnd: number;
  minPoints: number;
  reward: Reward;
  pending?: boolean;
}

export interface SquadBattlesMode {
  area: string;
  settingsFile: string;
  boardFile: string;
  settings: { mode: 'percentile' | 'points'; minPointsToQualify: number; rankedEnabled: boolean; rankedMinParticipants: number };
  /** rules/sqbt_totw.json: the playable TOTW (current, 0 = none) is a rule of this file. */
  totwFile: string;
  totw: { current: number; weeks: { week: number; released: string; headline: string | null }[] };
  boardSize: number;
  boardMinimum: number;
  boardMaximum: number;
  maxLineCount: number;
  tiers: SquadBattlesTier[];
}

export const squadBattlesKey = ['mode-rewards', 'squad-battles'] as const;
export const SQUAD_BATTLES_AREA = 'squad_battles_tier';

/** The tier as the server stores and compares it: no key, no display name. */
export const canonicalTier = (tier: SquadBattlesTier) => ({
  kind: tier.kind, rankStart: tier.rankStart, rankEnd: tier.rankEnd, tierLevel: tier.tierLevel,
  percentStart: tier.percentStart, percentEnd: tier.percentEnd, minPoints: tier.minPoints, reward: canonicalReward(tier.reward),
});

/** The published tiers with the open changeset's tier changes laid over them. */
export function mergeSquadBattles(reply: SquadBattlesMode, open: ChangesetReply | undefined): SquadBattlesMode {
  const changes = open?.changeset?.changes ?? [];
  return {
    ...reply,
    tiers: reply.tiers.map((tier) => {
      const change = changes.find((candidate) => candidate.area === SQUAD_BATTLES_AREA && candidate.operation === 'update' && candidate.key.tierId === tier.tierId);
      const after = change?.after as Omit<SquadBattlesTier, 'tierId' | 'name'> | null | undefined;
      return after ? { ...tier, ...after, tierId: tier.tierId, name: tier.name, pending: true } : tier;
    }),
  };
}

export function useSquadBattlesMode() {
  const mode = useQuery({ queryKey: squadBattlesKey, queryFn: () => api<SquadBattlesMode>('content/modes/squad-battles') });
  const open = useChangeset();
  return { ...mode, data: mode.data ? mergeSquadBattles(mode.data, open.data) : undefined, open, isPending: mode.isPending || open.isPending };
}

export function useSquadBattlesActions() {
  const client = useQueryClient();
  return {
    stageTier: useMutation({
      mutationKey: contentActionKey,
      mutationFn: (tier: SquadBattlesTier) => api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: {
        area: SQUAD_BATTLES_AREA, key: { tierId: tier.tierId }, operation: 'update', after: canonicalTier(tier),
      } }),
      onSuccess: () => Promise.all([client.invalidateQueries({ queryKey: changesetKey }), client.invalidateQueries({ queryKey: squadBattlesKey })]),
    }),
  };
}
