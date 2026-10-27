// Game rules as database rows (OperatorPanel.Content.cs): GET content/rules, GET content/rules/<file>, and edits staged
// as changes to the rules area (PUT content/changeset/changes), published under Changes.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import { changesetKey, contentActionKey } from './content';
import type { ChangesetReply, ContentChange } from './types';

export type RulePath = (string | number)[];
export interface RuleRow { path: RulePath; value: unknown }
export interface RuleFilesReply { area?: string; files?: { file: string; rules: number }[] }
export interface RulePageReply { total?: number; rules?: RuleRow[] }

export const RULES_PAGE = 100;

export const useRuleFiles = (comments = false) => useQuery({ queryKey: ['rule-files', comments],
  queryFn: () => api<RuleFilesReply>(`content/rules?view=rules&comments=${comments ? '1' : '0'}`) });

export const useRulePage = (file: string | undefined, q: string, offset: number, comments = false) => useQuery({
  queryKey: ['rules', file, q, offset, comments],
  queryFn: ({ signal }) => api<RulePageReply>(`content/rules/${encodeURIComponent(file ?? '')}?${new URLSearchParams({ view: 'rules', q, offset: String(offset), limit: String(RULES_PAGE), comments: comments ? '1' : '0' })}`, { signal }),
  enabled: !!file,
  placeholderData: (previous) => previous,
});

/** "priceBounds › maximumByRating › #2 › maximum": list positions are shown with a #. */
export const pathText = (path: RulePath) => path.map((part) => (typeof part === 'number' ? `#${part}` : part)).join(' › ');

/** Parses the JSON an operator typed: a number, true/false, "text" in quotes, a [list] or an {object}. */
export function parseJson(text: string): { ok: true; value: unknown } | { ok: false } {
  try { return { ok: true, value: JSON.parse(text, (_key, value: unknown) => {
    if (typeof value === 'number' && !Number.isFinite(value)) throw new Error('Non-finite number');
    return value;
  }) as unknown }; } catch { return { ok: false }; }
}

/** A path typed as JSON: a non-empty list of keys (text) and positions (whole numbers). */
export function parsePath(text: string): RulePath | null {
  const parsed = parseJson(text);
  if (!parsed.ok || !Array.isArray(parsed.value) || !parsed.value.length || parsed.value.length > 16) return null;
  return parsed.value.every((part) => typeof part === 'string' && part.length > 0 && part.length <= 128 || (typeof part === 'number' && Number.isInteger(part) && part >= 0 && part <= 100_000)) ? parsed.value as RulePath : null;
}

export function sameRuleKey(key: Record<string, unknown>, file: string, path: RulePath) {
  return key.file === file && JSON.stringify(key.path) === JSON.stringify(path);
}
export function ruleChange(reply: ChangesetReply, file: string, path: RulePath) {
  return reply.changeset?.changes?.find(c => c.area === 'rules' && sameRuleKey(c.key, file, path));
}
export function changedRule(change?: ContentChange) {
  return change ? { id: change.id, operation: change.operation, after: change.after ?? null } : null;
}
export const readRule = (file: string, path: RulePath) => api<{ definition: { value: unknown } | null }>(`content/rules/${encodeURIComponent(file)}?${new URLSearchParams({ path: JSON.stringify(path) })}`);

export function useRuleActions() {
  const client = useQueryClient();
  const refresh = () => {
    void client.invalidateQueries({ queryKey: changesetKey });
    void client.invalidateQueries({ queryKey: ['rules'] });
    void client.invalidateQueries({ queryKey: ['rule-files'] });
  };
  return {
    /** update: a new value; insert: a new key, or a list position equal to the list's length; hide: remove the rule. */
    stage: useMutation({
      mutationKey: contentActionKey,
      mutationFn: async ({ file, path, operation, value, baseline }: { file: string; path: RulePath; operation: 'update' | 'insert' | 'hide'; value?: unknown; baseline?: { live: unknown; pending: ReturnType<typeof changedRule> } }) => {
        if (baseline) {
          const [live, pending] = await Promise.all([readRule(file, path), api<ChangesetReply>('content/changeset')]);
          if (JSON.stringify(live.definition) !== JSON.stringify(baseline.live) || JSON.stringify(changedRule(ruleChange(pending, file, path))) !== JSON.stringify(baseline.pending))
            throw new Error('This rule changed while the editor was open. Close and reopen it before saving.');
        }
        return api('content/changeset/changes', { method: 'PUT', body: { area: 'rules', key: { file, path }, operation, after: operation === 'hide' ? null : { value } } });
      },
      onSuccess: refresh,
    }),
    importFiles: useMutation({
      mutationKey: contentActionKey,
      mutationFn: (file: string | null) => api<{ staged?: number }>('content/rules/import', { method: 'POST', body: file ? { file } : {} }),
      onSuccess: refresh,
    }),
  };
}
