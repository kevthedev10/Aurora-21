// Squad Building Challenges (OperatorPanel.Sbc.cs): the catalogue (every set, bundled or added in the panel), the live list
// of the sets the game shows, and what the editor offers. Set edits are content changes of the sbc_set area and the live
// list one change of the sbc_live area, published under Changes like packs and rules.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import { changesetKey, contentActionKey } from './content';
import { changedRule, readRule, ruleChange, type RulePath } from './rules';
import type { CardsReply, ChangesetReply } from './types';

export type SbcBound = 'count' | 'min' | 'max' | 'value';

export interface SbcRequirementType {
  type: string;
  bound: SbcBound;
  label: string;
  /** Whether the type also takes an exact value (for example "Nationalities in Squad: Exactly 3"). */
  exact: boolean;
  qualities: string[];
  selector?: 'leagueId' | 'nationId' | 'clubId' | 'quality' | null;
}

export interface SbcRule { type: string; attributes?: Record<string, number | string> }

export interface SbcReward { type: 'PACK' | 'COINS' | 'XP' | string; value?: number; attributes?: Record<string, number | string> }

export interface SbcChallengeDef {
  id: string;
  order?: number;
  name: string;
  description?: string;
  formation?: string;
  requirements?: { rules: SbcRule[] };
  rewards?: { entries: SbcReward[] };
  [field: string]: unknown;
}

export interface SbcSetDef {
  id?: string;
  name: string;
  description?: string;
  tabId: number;
  repeatable?: boolean;
  repeatableDaily?: boolean;
  maxCompletions?: number | undefined;
  badge?: string;
  challenges: SbcChallengeDef[];
  rewards?: { entries: SbcReward[] };
  [field: string]: unknown;
}

export interface SbcSetRow {
  key: string;
  setId: number | null;
  origin: 'bundled' | 'panel';
  edited: boolean;
  /** On the live list (migration 141); the game shows it only while it is also switched on. */
  listed: boolean;
  enabled: boolean;
  definition: SbcSetDef;
}

export interface SbcLiveEntry { setKey: string; enabled: boolean }

/** One requirement type of rules/sbc.json: what the client is sent (fixed) and the label the panel may change. */
export interface SbcRequirementRule {
  type: string;
  bound: 'min' | 'max' | 'value';
  /** The client's SBCEligibilityKey, or null for a rule that only appears in the description. */
  eligibilityKey: string | null;
  eligibilityValue: number | null;
  scope: string | null;
  playerCount: number;
  label: string;
  exact: { scope: string; label: string } | null;
  qualities: Record<string, number>;
}

export interface SbcTextSettings {
  requirementSeparator: string;
  noRequirements: string;
  setDescription: string;
  unnamedSetDescription: string;
  challenge: string;
  challenges: string;
  unknownChallenge: string;
  shortSquad: string;
}

/** SBC → Settings: rules/sbc.json as published. */
export interface SbcSettings {
  formation: string;
  repeatableRewardsUntradeable: boolean;
  playerPickRewardName: string;
  ignoredRequirements: string[];
  text: SbcTextSettings;
  images: { imageIdBase: number; artFolder: string; fallbackTabId: number; cacheSeconds: number };
  squadSize: number;
  brickItemIdBase: number;
}

export interface SbcReply {
  tabs: { id: number; name: string }[];
  badges: string[];
  requirements: SbcRequirementType[];
  /** pick: a player pick pack; fixed: one that offers chosen cards rather than random ones. */
  packs: { id: number; name: string; size: number; enabled: boolean; pick?: boolean; fixed?: boolean }[];
  sets: SbcSetRow[];
  live: SbcLiveEntry[];
  settingsFile: string;
  settings: SbcSettings;
  requirementRules: SbcRequirementRule[];
  /** Each formation's slots in squad order (slot 0 the goalkeeper), as indexes into positions. */
  formations: { id: string; slots: number[] }[];
  positions: string[];
  squadSize: number;
  managerLeagues: { league: number; name: string; resourceId: number }[];
}

/** Locked squad slots: the slots the player fills, and on locked slots an authored card or a league/nation placeholder. */
export interface SbcBricks {
  freeIndices: number[];
  players?: Record<string, number>;
  dummies?: Record<string, { league: number; nation: number }>;
}

/** The editor's live check of an unsaved set: why staging it would be refused, and the text FIFA would show. */
export interface SbcPreview {
  problem: string | null;
  description: string;
  challenges: { description: string; players: number }[];
}

export const sbcKey = ['sbc'] as const;
export const useSbc = () => useQuery({ queryKey: sbcKey, queryFn: () => api<SbcReply>('sbc') });

export const badgeUrl = (name: string) => `/panel/sbc-badges/${encodeURIComponent(name)}.png`;

/** Stages one catalogue set: insert (a new set), update (an existing one) or hide (remove a panel set, or revert a bundled set's edit). */
export function useSbcActions() {
  const client = useQueryClient();
  const refresh = () => Promise.all([client.invalidateQueries({ queryKey: changesetKey }), client.invalidateQueries({ queryKey: sbcKey })]);
  return useMutation({
    mutationFn: ({ key, operation, definition }: { key: string; operation: 'insert' | 'update' | 'hide'; definition?: SbcSetDef }) =>
      api<ChangesetReply>('content/changeset/changes', {
        method: 'PUT',
        body: { area: 'sbc_set', key: { setKey: key }, operation, after: operation === 'hide' ? null : { definition }, by: 'panel' },
      }),
    onSuccess: refresh,
  });
}

/** Stages the whole live list: the sets the game shows, each switched on or off. */
export function useSbcLiveActions() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (listing: SbcLiveEntry[]) => api<ChangesetReply>('content/changeset/changes', {
      method: 'PUT',
      body: { area: 'sbc_live', key: { scope: 'live' }, operation: 'update', after: { listing }, by: 'panel' },
    }),
    onSuccess: () => Promise.all([client.invalidateQueries({ queryKey: changesetKey }), client.invalidateQueries({ queryKey: sbcKey })]),
  });
}

/**
 * SBC → Settings edits: one rules change of rules/sbc.json per value (refused when the value changed since the page loaded),
 * and "restore shipped settings", which stages every difference from the bundled file.
 */
export function useSbcSettingActions(file: string) {
  const client = useQueryClient();
  const refresh = () => Promise.all([client.invalidateQueries({ queryKey: changesetKey }), client.invalidateQueries({ queryKey: sbcKey })]);
  return {
    stage: useMutation({
      mutationKey: contentActionKey,
      mutationFn: async ({ path, value, shown }: { path: RulePath; value: unknown; shown: unknown }) => {
        const [live, pending] = await Promise.all([readRule(file, path), api<ChangesetReply>('content/changeset')]);
        const staged = ruleChange(pending, file, path);
        const current = staged ? (changedRule(staged)?.after as { value?: unknown } | null)?.value : live.definition?.value;
        if (JSON.stringify(current) !== JSON.stringify(shown)) throw new Error('This setting changed while the page was open. Reload it before saving.');
        return api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: { area: 'rules', key: { file, path }, operation: 'update', after: { value } } });
      },
      onSuccess: refresh,
    }),
    restore: useMutation({
      mutationKey: contentActionKey,
      mutationFn: () => api<{ staged?: number }>('content/rules/import', { method: 'POST', body: { file } }),
      onSuccess: refresh,
    }),
  };
}

/** POST sbc/preview, debounced by the caller: the same checks and text code the server stages and serves with. */
export const useSbcPreview = (setKey: string, definition: SbcSetDef | null) => useQuery({
  queryKey: ['sbc-preview', setKey, definition],
  queryFn: ({ signal }) => api<SbcPreview>('sbc/preview', { method: 'POST', body: { setKey, definition }, signal }),
  enabled: definition !== null,
  placeholderData: (previous) => previous,
  staleTime: 60_000,
});

/** One card by resource id (a locked player or a player reward), for its face and name. */
export const useCard = (resourceId: number | undefined) => useQuery({
  queryKey: ['sbc-card', resourceId],
  queryFn: async () => (await api<CardsReply>(`cards?${new URLSearchParams({ resourceId: String(resourceId), limit: '1' })}`)).cards?.[0] ?? null,
  enabled: !!resourceId,
  staleTime: 300_000,
});

/** Cards matching a name, for the editor's card picker. */
export const useCardSearch = (text: string) => useQuery({
  queryKey: ['sbc-card-search', text],
  queryFn: async ({ signal }) => (await api<CardsReply>(`cards?${new URLSearchParams({ q: text, limit: '24' })}`, { signal })).cards ?? [],
  enabled: text.trim().length >= 2,
  placeholderData: (previous) => previous,
});

/** SBC names by set key, for change summaries; loads the SBC list only when asked to. Unknown keys stay as they are. */
export function useSbcNames(enabled: boolean) {
  const sbc = useQuery({ queryKey: sbcKey, queryFn: () => api<SbcReply>('sbc'), enabled });
  const names = new Map((sbc.data?.sets ?? []).map((set) => [set.key, set.definition.name || set.key]));
  return (key: string) => names.get(key) ?? key;
}
