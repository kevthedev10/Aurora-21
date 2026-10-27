// Modes → Draft (OperatorPanel.Content.cs DraftModeViewAsync, Core/Content/DraftRewardAreas.cs): per mode (online, single) and
// wins the reward level every run ending there gets and the weighted choices one is drawn from, each a content row of area
// draft_level; the entry costs are rules of draft.json. todo.md section 2.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import { changesetKey, contentActionKey, useChangeset } from './content';
import { canonicalReward, type Reward } from './modes';
import type { ChangesetReply } from './types';

export type DraftModeId = 'online' | 'single';

export interface DraftChoice {
  weight: number;
  reward: Reward;
}

export interface DraftLevel {
  mode: DraftModeId;
  wins: number;
  /** "Online, 3 wins". */
  name: string;
  reward: Reward;
  choices: DraftChoice[];
  pending?: boolean;
}

export interface DraftMode {
  area: string;
  settingsFile: string;
  settings: { entryCoins: number; entryPoints: number };
  pickSettings?: DraftPickSettings;
  pickOptions?: { categories: string[]; goldRareGroups: string[] };
  winsToComplete: number;
  maxChoices: number;
  maxWeight: number;
  maxLineCount: number;
  levels: DraftLevel[];
}

export interface DraftPickSettings {
  choicesPerScreen: number;
  minRating: number;
  strongFloor: number;
  captainStrongFloor: number;
  managerPoolSize: number;
  specialBias: number;
  weakRoundSpecialBias: number;
  elite99Chance: number;
  elite99RoundCounts: number[];
  weakRoundCounts: number[];
  goldRareBucketWeights: Record<string, number>;
  weakRoundGoldRareBucketWeights: Record<string, number>;
  categoryWeights: Record<string, number>;
  weakRoundCategoryWeights: Record<string, number>;
}

export const draftKey = ['mode-rewards', 'draft'] as const;
export const DRAFT_AREA = 'draft_level';

/** The level as the server stores and compares it: no key, no display name. */
export const canonicalLevel = (level: Pick<DraftLevel, 'reward' | 'choices'>) => ({
  reward: canonicalReward(level.reward),
  choices: level.choices.map((choice) => ({ weight: choice.weight, reward: canonicalReward(choice.reward) })),
});

/** Each choice's chance in percent, by weight. */
export const chances = (choices: DraftChoice[]) => {
  const total = choices.reduce((sum, choice) => sum + (Number.isFinite(choice.weight) && choice.weight > 0 ? choice.weight : 0), 0);
  return choices.map((choice) => (total > 0 && choice.weight > 0 ? (100 * choice.weight) / total : 0));
};

/** The published levels with the open changeset's level changes laid over them. */
export function mergeDraft(reply: DraftMode, open: ChangesetReply | undefined): DraftMode {
  const changes = open?.changeset?.changes ?? [];
  return {
    ...reply,
    levels: reply.levels.map((level) => {
      const change = changes.find((candidate) => candidate.area === DRAFT_AREA && candidate.operation === 'update'
        && candidate.key.mode === level.mode && Number(candidate.key.wins) === level.wins);
      const after = change?.after as Pick<DraftLevel, 'reward' | 'choices'> | null | undefined;
      return after ? { ...level, reward: after.reward, choices: after.choices, pending: true } : level;
    }),
  };
}

export function useDraftMode() {
  const mode = useQuery({ queryKey: draftKey, queryFn: () => api<DraftMode>('content/modes/draft') });
  const open = useChangeset();
  return { ...mode, data: mode.data ? mergeDraft(mode.data, open.data) : undefined, open, isPending: mode.isPending || open.isPending };
}

export function useDraftActions() {
  const client = useQueryClient();
  return {
    stageLevel: useMutation({
      mutationKey: contentActionKey,
      mutationFn: (level: Pick<DraftLevel, 'mode' | 'wins' | 'reward' | 'choices'>) => api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: {
        area: DRAFT_AREA, key: { mode: level.mode, wins: level.wins }, operation: 'update', after: canonicalLevel(level),
      } }),
      onSuccess: () => Promise.all([client.invalidateQueries({ queryKey: changesetKey }), client.invalidateQueries({ queryKey: draftKey })]),
    }),
  };
}
