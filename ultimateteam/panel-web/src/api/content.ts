// Content changesets (OperatorPanel.Content.cs): the open changeset, validate, publish, discard, history and revert.
// Staged edits reach the game only through Publish; every mutation here refreshes the pending badge.
import { useIsMutating, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { api, ApiError } from './client';
import { useSyncExternalStore } from 'react';
import { getContentDestination, subscribeContentDestination } from './client';
import type { Changeset, ChangesetReply } from './types';

export const changesetKey = ['changeset'] as const;
export const useContentDestination = () => useSyncExternalStore(subscribeContentDestination, getContentDestination, () => null);
export const historyKey = ['content-history'] as const;
export const contentStatusKey = ['content-status'] as const;
export const contentActionKey = ['content-action'] as const;
const actionKey = contentActionKey;
export const recoveryKey = 'aurora21.panel.changes.recovery';
export const recoveryStorageKey = () => getContentDestination() === null ? recoveryKey : `${recoveryKey}.drop.${getContentDestination()}`;
export function rememberRecovery(change: RestageError['change'] | null) {
  try {
    if (change) sessionStorage.setItem(recoveryStorageKey(), JSON.stringify(change));
    else sessionStorage.removeItem(recoveryStorageKey());
  } catch { /* Visible recovery remains available when browser storage is disabled. */ }
}

export interface ContentStatus {
  revision: number;
  loadedDatabaseRevision: number;
  externalChanges: boolean;
  reloadRequired: boolean;
  tables: { table: string; revision: number; changedAt: string }[];
  message: string;
}

export interface ContentReport {
  revision?: number;
  warnings?: string[];
  notes?: string[];
}

export const useContentStatus = () => useQuery({
  queryKey: contentStatusKey,
  queryFn: () => api<ContentStatus>('content/status'),
  refetchInterval: 10_000,
  retry: false,
});

// Refetch server data without resetting local editor state or reloading the browser.
export const refreshContent = (client: QueryClient) => client.invalidateQueries();

export const useContentBusy = () => useIsMutating({ mutationKey: actionKey }) > 0;

export const useReloadContent = () => {
  const client = useQueryClient();
  return useMutation({ mutationKey: actionKey,
    mutationFn: () => api<ContentReport>('content/reload', { method: 'POST', body: {} }),
    onSuccess: () => refreshContent(client),
  });
};

export interface Conflict {
  changeId: number;
  area: string;
  key: Record<string, unknown>;
  /** The live value the change was staged against. */
  expected?: unknown;
  /** The live value now. */
  live?: unknown;
}

export interface ValidationReply {
  ok?: boolean;
  changes?: number;
  conflicts?: Conflict[];
  errors?: string[];
}

export interface PublishReply {
  published?: Changeset;
  content?: ContentReport;
}

export const useChangeset = (refetchInterval?: number, scope?: 'immediate') => {
  const selected = useContentDestination();
  const destination = scope === 'immediate' ? null : selected;
  return useQuery({
  queryKey: destination === null ? changesetKey : [...changesetKey, destination],
  queryFn: () => api<ChangesetReply>(scope === 'immediate' ? 'content/changeset?dropId=' : 'content/changeset'),
  ...(refetchInterval && { refetchInterval }),
  // An API without changesets (404) simply has nothing pending.
  retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 2,
  });
};

export const useHistory = () => useQuery({
  queryKey: historyKey,
  queryFn: () => api<{ changesets?: Changeset[] }>('content/history?limit=50'),
});

/** A refused publish carries its reasons: a 409 lists conflicting rows, a 422 says why nothing was published. */
export function refusal(error: unknown): ValidationReply {
  const body = error instanceof ApiError && typeof error.body === 'object' && error.body ? error.body as { conflicts?: Conflict[] } : {};
  return { ok: false, errors: [error instanceof Error ? error.message : String(error)], conflicts: body.conflicts ?? [] };
}

/** Every changeset mutation, each refreshing the open changeset (and the history where it changes). */
export function useChangesetActions() {
  const client = useQueryClient();
  const refresh = () => Promise.all([
    client.invalidateQueries({ queryKey: changesetKey }),
    client.invalidateQueries({ queryKey: historyKey }),
  ]);
  return {
    remove: useMutation({ mutationKey: actionKey, mutationFn: (changeId: number) => api<ChangesetReply>(`content/changeset/changes/${changeId}`, { method: 'DELETE' }), onSuccess: refresh }),
    validate: useMutation({ mutationKey: actionKey, mutationFn: () => api<ValidationReply>('content/changeset/validate', { method: 'POST', body: {} }) }),
    publish: useMutation({
      mutationKey: actionKey,
      mutationFn: (body: { note: string | null; by: string | null }) => api<PublishReply>('content/changeset/publish', { method: 'POST', body }),
      onSuccess: () => refreshContent(client),
    }),
    discard: useMutation({ mutationKey: actionKey, mutationFn: () => api('content/changeset/discard', { method: 'POST', body: {} }), onSuccess: refresh }),
    revert: useMutation({
      mutationKey: actionKey,
      mutationFn: ({ id, by }: { id: number; by: string | null }) => api<ChangesetReply>(`content/history/${id}/revert`, { method: 'POST', body: by ? { by } : {} }),
      onSuccess: refresh,
    }),
    /** "Keep my value": drop the conflicting change and stage the same value again on top of the live row. */
    restage: useMutation({
      mutationKey: actionKey,
      mutationFn: async (change: { id: number; area: string; key: Record<string, unknown>; operation: string; after?: unknown }) => {
        // Persist before the first request, including when navigation interrupts the page callbacks.
        rememberRecovery(change);
        try {
          await api(`content/changeset/changes/${change.id}`, { method: 'DELETE' });
          const reply = await api<ChangesetReply>('content/changeset/changes', { method: 'PUT', staged: true, body: { area: change.area, key: change.key, operation: change.operation, after: change.after } });
          rememberRecovery(null);
          return reply;
        } catch (error) {
          throw new RestageError(error, change);
        }
      },
      onSuccess: refresh,
      onError: refresh,
    }),
    recover: useMutation({ mutationKey: actionKey,
      mutationFn: (change: RestageError['change']) => api<ChangesetReply>('content/changeset/changes', {
        method: 'PUT', staged: true, body: { area: change.area, key: change.key, operation: change.operation, after: change.after },
      }), onSuccess: () => { rememberRecovery(null); return refresh(); }, onError: refresh,
    }),
  };
}

/** DELETE and PUT are separate API transactions; a network failure may leave either outcome unknown. */
export class RestageError extends Error {
  constructor(error: unknown, readonly change: { id: number; area: string; key: Record<string, unknown>; operation: string; after?: unknown }) {
    super(`Restaging did not complete: ${error instanceof Error ? error.message : String(error)}`);
  }
}

/** The name entered with a publish, remembered in this browser only (the panel has no accounts). */
const BY_KEY = 'aurora21.panel.changes.by';
export const rememberedBy = () => { try { return localStorage.getItem(BY_KEY) ?? ''; } catch { return ''; } };
export const rememberBy = (value: string) => { try { localStorage.setItem(BY_KEY, value); } catch { /* private window */ } };
