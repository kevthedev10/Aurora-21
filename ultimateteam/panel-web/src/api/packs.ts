// Packs (OperatorPanel.PackCatalog.cs, OperatorPanel.Packs.cs). There is one kind of pack: the store lineup sells some,
// rewards name some, the rest are unassigned. Pack definitions and the store lineup are content: every save stages a
// change, published under Changes. Test draws and simulations change nothing.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { CardArt } from './assets';
import { api } from './client';
import { changesetKey, useChangeset } from './content';
import type { CatalogPack, ChangesetReply, ListingEntry, PackCatalogReply, PromoOption, VanityType } from './types';

export type PendingOp = 'New' | 'Edit' | 'Hide' | 'Delete';
export interface CatalogueEntry extends CatalogPack { pending?: PendingOp; config: Record<string, unknown> }

/** Where a pack is used: sold in the published store lineup, named by a published reward rule, both, or neither. */
export type PackUsage = 'store-rewards' | 'store' | 'rewards' | 'unassigned';
export const USAGE_LABEL: Record<PackUsage, string> = { 'store-rewards': 'Store + Rewards', store: 'Store', rewards: 'Rewards', unassigned: 'Unassigned' };
export const usageOf = (pack: CatalogPack): PackUsage => {
  const rewards = (pack.usedBy?.length ?? 0) > 0;
  return pack.inStore ? (rewards ? 'store-rewards' : 'store') : rewards ? 'rewards' : 'unassigned';
};

export interface Catalogue {
  revision: string | undefined;
  groups: string[];
  packs: CatalogueEntry[];
  publicPackIds: number[];
  /** The packs for sale as staged, or as published when nothing is staged: listed and switched on. */
  lineup: number[];
  /** The store listing as staged, or as published. */
  listing: ListingEntry[];
  lineupPending: boolean;
  tokenListing: ListingEntry[];
  tokenLineupPending: boolean;
  promos: PromoOption[];
  pickLimits: { minChoices: number; maxChoices: number; maxTokensPerGroup: number };
  oddsTemplate: { icon: number; pickTotw: number };
  vanityTypes: VanityType[];
}

/** Whether a definition offers player picks rather than drawing its items. */
export const isPick = (config: Record<string, unknown>) =>
  (Array.isArray(config.player_picks) && config.player_picks.length > 0) || Number(config.player_pick_count ?? 0) > 0;

/** The published catalogue with the open changeset's pack and lineup changes laid over it (the classic panel's load()). */
export function mergeCatalogue(catalog: PackCatalogReply, open: ChangesetReply | undefined): Catalogue {
  const packs: CatalogueEntry[] = (catalog.packs ?? []).map((pack) => ({ ...pack, config: { ...(pack.config ?? {}) } }));
  const changes = open?.changeset?.changes ?? [];
  for (const change of changes.filter((candidate) => candidate.area === 'store_pack_delete' || candidate.area === 'store_pack_force_delete')) {
    const pack = packs.find((candidate) => candidate.id === Number(change.key.packId));
    if (pack) pack.pending = 'Delete';
  }
  for (const change of changes.filter((candidate) => candidate.area === 'store_pack')) {
    const id = Number(change.key.packId);
    let pack = packs.find((candidate) => candidate.id === id);
    const after = change.after as { pack?: Record<string, unknown> } | null | undefined;
    // A staged new pack is unassigned: out of the store and named by no reward until those are published.
    if (!pack && change.operation === 'insert') { pack = { id, config: {}, locked: false, grantOnly: false, inStore: false, usedBy: [] }; packs.push(pack); }
    if (!pack || pack.pending === 'Delete') continue;
    if (after?.pack) pack.config = { ...pack.config, ...after.pack };
    pack.pending = change.operation === 'hide' ? 'Hide' : change.operation === 'insert' ? 'New' : 'Edit';
  }
  for (const pack of packs) {
    const config = pack.config;
    pack.description = typeof config.name === 'string' ? config.name : pack.description ?? `Pack ${pack.id}`;
    pack.blurb = typeof config.bio === 'string' ? config.bio : pack.blurb ?? '';
    pack.group = typeof config.store_group === 'string' && config.store_group ? config.store_group : pack.group ?? 'Automatic';
    pack.coins = Number(config.price ?? pack.coins ?? 0);
    pack.points = Number(config.points_price ?? pack.points ?? 0);
    pack.items = Number(config.size ?? pack.items ?? 12);
    pack.rare = Number(config.rare_slots ?? pack.rare ?? 0);
    pack.grantOnly = config.admin_only === true || (config.admin_only === undefined && pack.grantOnly === true);
    if (typeof config.store_group === 'string' && config.store_group) pack.art = config.store_group;
  }
  const lineup = changes.find((change) => change.area === 'store_lineup');
  const after = lineup?.after as { listing?: ListingEntry[]; publicPackIds?: number[] } | undefined;
  // An older staged change holds publicPackIds: those packs listed and switched on.
  const listing: ListingEntry[] = after?.listing ?? after?.publicPackIds?.map((packId) => ({ packId, enabled: true, untradeable: null }))
    ?? catalog.listing ?? (catalog.publicPackIds ?? []).map((packId) => ({ packId, enabled: true, untradeable: null }));
  const tokenLineup = changes.find((change) => change.area === 'token_store_lineup');
  const tokenAfter = tokenLineup?.after as { listing?: ListingEntry[] } | undefined;
  const tokenListing = tokenAfter?.listing ?? catalog.tokenListing ?? [];
  const pricedListing = listing.map(entry => ({ ...entry, price: entry.price ?? packs.find(pack => pack.id === entry.packId)?.coins ?? 0 }));
  return {
    tokenListing, tokenLineupPending: !!tokenLineup,
    revision: catalog.revision, groups: catalog.groups ?? [], packs: packs.sort((a, b) => a.id - b.id),
    publicPackIds: catalog.publicPackIds ?? [], listing: pricedListing, lineup: listing.filter((entry) => entry.enabled).map((entry) => entry.packId),
    lineupPending: !!lineup,
    promos: catalog.promos ?? [], pickLimits: catalog.pickLimits ?? { minChoices: 2, maxChoices: 5, maxTokensPerGroup: 10 },
    oddsTemplate: catalog.oddsTemplate ?? { icon: 0, pickTotw: 0 }, vanityTypes: catalog.vanityTypes ?? [],
  };
}

/** GET packs/catalog/{id}/delete-check: what still depends on a pack (OperatorPanel.PackDelete.cs). */
export interface PackDeleteCheck {
  id: number;
  name?: string | null;
  kind?: string | null;
  blockers: { code: string; message: string; overridable: boolean }[];
  canDelete: boolean;
  /** Refused only because clubs own unopened copies: Force delete may remove them. */
  canForceDelete: boolean;
  unopened: { copies: number; clubs: number };
}

export const catalogKey = ['packs-catalog'] as const;

export function usePackDeleteCheck(id: number | null) {
  return useQuery({
    queryKey: ['pack-delete-check', id], enabled: id !== null, gcTime: 0,
    queryFn: () => api<PackDeleteCheck>(`packs/catalog/${id}/delete-check`),
  });
}

export function useCatalogue(scope?: 'immediate') {
  const catalog = useQuery({ queryKey: scope === 'immediate' ? [...catalogKey, 'immediate'] : catalogKey, queryFn: () => api<PackCatalogReply>(scope === 'immediate' ? 'packs/catalog?dropId=' : 'packs/catalog') });
  const open = useChangeset(undefined, scope);
  return {
    ...catalog,
    data: catalog.data ? mergeCatalogue(catalog.data, open.data) : undefined,
    isPending: catalog.isPending || open.isPending,
  };
}

export interface DrawnItem { itemType?: string; name?: string; rating?: number; art?: CardArt | null; pickIndex?: number; choiceCount?: number }

export function usePackActions(scope?: 'immediate') {
  const path = (value: string) => scope === 'immediate' ? `${value}?dropId=` : value;
  const client = useQueryClient();
  const refresh = () => {
    void client.invalidateQueries({ queryKey: catalogKey });
    void client.invalidateQueries({ queryKey: changesetKey });
  };
  return {
    /** Stage a new pack (POST) or an edit (PUT) with only the fields that change. */
    stage: useMutation({
      mutationFn: ({ id, revision, pack }: { id: number | null; revision: string | undefined; pack: Record<string, unknown> }) =>
        api<{ id: number; pending: number }>(path(`packs/catalog${id ? `/${id}` : ''}`), { method: id ? 'PUT' : 'POST', body: { revision, pack } }),
      onSuccess: refresh,
    }),
    hide: useMutation({
      mutationFn: ({ id, revision }: { id: number; revision: string | undefined }) => api(path(`packs/catalog/${id}`), { method: 'DELETE', body: { revision } }),
      onSuccess: refresh,
    }),
    /** Stage deleting the definition. Force also sends the copy and club counts the operator confirmed. */
    remove: useMutation({
      mutationFn: ({ id, revision, force }: { id: number; revision: string | undefined; force?: { copies: number; clubs: number } }) =>
        api(path(`packs/catalog/${id}/delete`), { method: 'POST', body: { revision, ...(force && { force: true, copies: force.copies, clubs: force.clubs }) } }),
      onSuccess: refresh,
    }),
    lineup: useMutation({
      mutationFn: ({ revision, listing }: { revision: string | undefined; listing: ListingEntry[] }) =>
        api(path('packs/lineup'), { method: 'PUT', body: { revision, listing: [...listing].sort((a, b) => a.packId - b.packId) } }),
      onSuccess: refresh,
    }),
    tokenLineup: useMutation({
      mutationFn: ({ revision, listing }: { revision: string | undefined; listing: ListingEntry[] }) =>
        api(path('packs/tokens-lineup'), { method: 'PUT', body: { revision, listing: [...listing].sort((a, b) => a.packId - b.packId) } }),
      onSuccess: refresh,
    }),
    test: useMutation({
      mutationFn: ({ packId, pack }: { packId: number | null; pack: Record<string, unknown> }) =>
        api<{ items?: DrawnItem[] }>(path('packs/catalog/test'), { method: 'POST', body: packId ? { packId, pack } : { pack } }),
    }),
    simulate: useMutation({
      mutationFn: ({ packId, rolls }: { packId: number; rolls: number }) =>
        api<{ rolls?: { index: number; items?: DrawnItem[] }[] }>(path('packs/simulate'), { method: 'POST', body: { packId, rolls } }),
    }),
  };
}
