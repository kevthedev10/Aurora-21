// Objectives and the Season Pass (OperatorPanel.Objectives.cs): rules/objectives.json, whose welcome group, grind groups and
// gifts are the catalogue and whose live list says which groups the game shows. Every edit is a rules change of that file,
// staged and published under Changes; the page shows the published file with the staged edits laid over it.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import { changesetKey, contentActionKey } from './content';
import { changedRule, readRule, ruleChange, type RulePath } from './rules';
import type { ChangesetReply, ContentChange } from './types';

export interface ObjectivePackLine { packId: number; count?: number; untradeable?: boolean }

/** An objective, group, gift or level reward. pack/untradeable is the older single-pack form; the editor writes packs. */
export interface ObjectiveReward {
  none?: boolean;
  players?: { resourceId: number; untradeable?: boolean }[];
  coins?: number | null;
  fifaPoints?: number;
  pack?: number | null;
  untradeable?: boolean;
  packs?: ObjectivePackLine[];
  vanity?: { name: string; subtypes: number[]; count?: number; assets?: number[] } | null;
  stadiumTier?: number | null;
  unlockSlots?: number[];
  /** A Season Pass level's choices: the club picks one of these (2 to 5); the level then pays nothing else. */
  options?: ObjectiveReward[];
}

/** Which players a condition names: any of league, nation and club (the catalogue's teamid); all named must match. */
export interface ObjectivePlayerRestriction { leagueId?: number; nation?: number; clubId?: number }
/**
 * An objective's match conditions (ObjectiveMatchProgress.cs): only matches in these modes count; a starting XI restriction
 * needs that many matching starters; a player restriction counts only those players' goals or assists.
 */
export interface ObjectiveRequirements {
  modes: string[];
  startingSquad?: ObjectivePlayerRestriction & { minimumCount?: number; exactCount?: number };
  player?: ObjectivePlayerRestriction;
  goalType?: string;
  assistType?: string;
}
export interface GrindObjective { id: number; name: string; stat: string; target: number; award: ObjectiveReward; xp: number;
  description?: string | undefined; requirements?: ObjectiveRequirements | undefined }
export interface GrindGroup { id: number; title: string; subTitle: string; award: ObjectiveReward; objectives: GrindObjective[]; xp?: number; sourceKey?: string }
export interface ObjectiveDefinition { id: number; name: string; description: string }
export interface ObjectiveGift {
  id: number; title: string; subTitle: string; objective: ObjectiveDefinition; award: ObjectiveReward;
  availableFrom?: string | null; availableUntil?: string | null; stat?: string | null; target?: number;
}
export interface WelcomeGroup {
  id: number; title: string; subTitle: string; objective: ObjectiveDefinition; rewardPackId: number; rewardPackCount: number;
  rewardUntradeable?: boolean; xpAward: number; stat?: string | null; target?: number;
}
export interface ObjectiveLiveEntry { group: number; enabled: boolean }
export interface SeasonProgress { levelCount: number; xpPerLevel: number; rewardLevelCap?: number | null; levelRewards: ObjectiveReward[] }
/** A custom season (rules/objectives.json seasons): a named Season Pass with dates; clubs' Season XP is kept per season id. */
export interface ObjectiveSeason extends SeasonProgress { id: number; name: string; startsAt: string; endsAt: string; enabled?: boolean }
/** The default season's campaign: its id keys clubs' Season XP outside custom seasons; the window is rolling. */
export interface ObjectiveCampaign { id: number; name: string; startedDaysAgo: number; endsInDays: number }

export interface ObjectiveRulesDoc {
  welcomeGroup: WelcomeGroup;
  grindGroups: GrindGroup[];
  gifts?: ObjectiveGift[];
  live?: ObjectiveLiveEntry[] | null;
  seasonProgress: SeasonProgress;
  campaign: ObjectiveCampaign;
  seasons?: ObjectiveSeason[];
  statLabels: Record<string, string>;
  [key: string]: unknown;
}

export interface ObjectivesReply {
  settingsFile: string;
  rules: ObjectiveRulesDoc;
  liveGroupIds: number[];
  stats: string[];
  nextGroupId: number;
  nextObjectiveId: number;
  started: { group: number; at: string }[];
  packs: { id: number; name: string; size: number; enabled: boolean; pick?: boolean; fixed?: boolean }[];
  /** What match conditions can name: modes, the stats a conditioned objective tracks, and those a player restriction applies to. */
  conditions?: { modes: string[]; stats: string[]; playerStats: string[] };
  /** The season in force (server time), the next free season id and how many clubs have Season XP in each season. */
  seasons?: { now: string; current: number; defaultId: number; nextId: number; played: { season: number; clubs: number; xp: number }[] };
}

export const objectivesKey = ['objectives'] as const;
export const useObjectives = () => useQuery({ queryKey: objectivesKey, queryFn: () => api<ObjectivesReply>('objectives') });

/** The rules changes staged for this file. */
export const stagedObjectiveChanges = (changes: ContentChange[] | undefined, file: string) =>
  (changes ?? []).filter((change) => change.area === 'rules' && change.key.file === file);

/** The published document with the staged edits laid over it, in the order they were staged. */
export function withStaged(rules: ObjectiveRulesDoc, staged: ContentChange[]): ObjectiveRulesDoc {
  const doc = structuredClone(rules) as Record<string, unknown>;
  for (const change of staged) {
    const path = change.key.path as RulePath;
    let parent: Record<string | number, unknown> | unknown[] = doc;
    for (const part of path.slice(0, -1)) {
      const next = (parent as Record<string | number, unknown>)[part];
      if (typeof next !== 'object' || next === null) { parent = []; break; }
      parent = next as Record<string | number, unknown>;
    }
    const last = path[path.length - 1]!;
    if (change.operation === 'hide') {
      if (Array.isArray(parent) && typeof last === 'number') parent.splice(last, 1); else delete (parent as Record<string | number, unknown>)[last];
    } else {
      (parent as Record<string | number, unknown>)[last] = structuredClone((change.after as { value?: unknown } | null)?.value);
    }
  }
  return doc as ObjectiveRulesDoc;
}

/** The value a change at this path compares against: the one staged at exactly that path, else the published one. */
export function shownAt(rules: ObjectiveRulesDoc, staged: ContentChange[], path: RulePath): unknown {
  const same = staged.filter((change) => JSON.stringify(change.key.path) === JSON.stringify(path)).at(-1);
  if (same) return same.operation === 'hide' ? null : (same.after as { value?: unknown } | null)?.value ?? null;
  return path.reduce<unknown>((node, key) => (node as Record<string | number, unknown> | null | undefined)?.[key], rules) ?? null;
}

/** Every group in the catalogue, welcome first, then the grind groups, then the gifts. */
export function catalogueGroups(doc: ObjectiveRulesDoc) {
  return [
    { id: doc.welcomeGroup.id, kind: 'welcome' as const, title: doc.welcomeGroup.title, subTitle: doc.welcomeGroup.subTitle },
    ...doc.grindGroups.map((group) => ({ id: group.id, kind: 'normal' as const, title: group.title, subTitle: group.subTitle })),
    ...(doc.gifts ?? []).map((gift) => ({ id: gift.id, kind: 'gift' as const, title: gift.title, subTitle: gift.subTitle })),
  ];
}

/** The live list as the game reads it: the stored list, or (none stored yet) every group switched on. */
export const liveListOf = (doc: ObjectiveRulesDoc): ObjectiveLiveEntry[] =>
  doc.live ?? catalogueGroups(doc).map((group) => ({ group: group.id, enabled: true }));

/**
 * Stages one value of rules/objectives.json at a path: an update, or an insert when the key does not exist yet (the live list
 * before it is first saved). Refused when the value changed since the page loaded.
 */
export function useObjectiveActions(file: string) {
  const client = useQueryClient();
  const refresh = () => Promise.all([client.invalidateQueries({ queryKey: changesetKey }), client.invalidateQueries({ queryKey: objectivesKey })]);
  return useMutation({
    mutationKey: contentActionKey,
    mutationFn: async ({ path, value, shown }: { path: RulePath; value: unknown; shown: unknown }) => {
      const [live, pending] = await Promise.all([readRule(file, path), api<ChangesetReply>('content/changeset')]);
      const staged = ruleChange(pending, file, path);
      const current = staged ? (changedRule(staged)?.after as { value?: unknown } | null)?.value : live.definition?.value;
      if (JSON.stringify(current ?? null) !== JSON.stringify(shown ?? null)) throw new Error('This changed while the page was open. Reload it before saving.');
      const operation = staged ? staged.operation === 'insert' ? 'insert' : 'update' : live.definition ? 'update' : 'insert';
      return api<ChangesetReply>('content/changeset/changes', { method: 'PUT', body: { area: 'rules', key: { file, path }, operation, after: { value } } });
    },
    onSuccess: refresh,
  });
}
