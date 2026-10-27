// Players and club detail: GET players, GET players/<persona>, PATCH players/<persona> (OperatorPanel.Players.cs).
// Aurora 21 applies each club edit at once in one transaction and answers with the whole detail; it never queues an
// edit (no 202, no 409 pending) and never asks to confirm a signed-in player (no 428), unlike Aurora 16.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from './client';
import type { PlayersReply } from './types';

export interface ClubSummary {
  name?: string | null;
  abbr?: string | null;
  coins?: number;
  points?: number;
  items?: number;
  tradePile?: number;
  unopenedPacks?: number;
  seasonXp?: number;
}

export interface InventoryCounts {
  club?: number;
  new?: number;
  transfer?: number;
  total?: number;
  unopenedPacks?: number;
  byKind?: Partial<Record<'player' | 'manager' | 'consumable' | 'clubItem' | 'other', number>>;
}

export interface PanelEdit {
  id: number;
  patch: string;
  requested_by?: string | null;
  result?: string;
  created_at?: string | null;
  applied_at?: string | null;
}

export interface SessionRow {
  remote?: string | null;
  connected_at?: string | null;
  disconnected_at?: string | null;
}

export interface PlayerDetail {
  id: number;
  discordId?: string | null;
  name?: string | null;
  online?: boolean;
  lastRemote?: string | null;
  logins?: number;
  lastLogin?: string | null;
  club?: ClubSummary | null;
  inventory?: InventoryCounts | null;
  grantablePacks?: { id: number; name?: string }[];
  /** Which club edits this server applies. */
  editOps?: { ops?: string[]; deltas?: boolean } | null;
  sessions?: SessionRow[];
  edits?: PanelEdit[];
  access?: { blocked?: boolean; maintenanceBypass?: boolean; note?: string | null; lastLaunchAt?: string | null; playRolesRequired?: boolean } | null;
}

export const playersKey = ['players'] as const;
export const clubKey = (id: number) => ['player', id] as const;

export const usePlayers = () => useQuery({ queryKey: playersKey, queryFn: () => api<PlayersReply>('players') });

export const useClub = (id: number) => useQuery({ queryKey: clubKey(id), queryFn: () => api<PlayerDetail>(`players/${id}`) });

/** Whether this server applies an edit: `deltas` for add/take away, otherwise an op name. Unknown (no editOps) allows. */
export function supports(detail: PlayerDetail | undefined, what: 'deltas' | string): boolean {
  const ops = detail?.editOps;
  if (!ops) return true;
  return what === 'deltas' ? ops.deltas === true : Array.isArray(ops.ops) && ops.ops.includes(what);
}

/**
 * A refusal the server answered before changing anything (a 4xx): the form keeps its values for another try. After
 * anything else (a dropped connection, a 500) the edit may have been applied, so the form clears rather than invite
 * a second, doubling click.
 */
export const refusedUnchanged = (error: unknown) => error instanceof ApiError && error.status >= 400 && error.status < 500;

/** PATCH one club edit; the reply is the whole detail, which replaces the cached one. */
export function useClubEdit(id: number) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (patch: Record<string, unknown>) => api<PlayerDetail>(`players/${id}`, { method: 'PATCH', body: patch }),
    onSuccess: (detail) => {
      client.setQueryData(clubKey(id), detail);
      void client.invalidateQueries({ queryKey: playersKey });
      void client.invalidateQueries({ queryKey: ['inventory', id] });
      void client.invalidateQueries({ queryKey: ['modes', id] });
    },
    onError: (error) => {
      if (!refusedUnchanged(error)) {
        void client.invalidateQueries({ queryKey: clubKey(id) });
        void client.invalidateQueries({ queryKey: ['inventory', id] });
      }
    },
  });
}

export const MODE_NAMES: Record<string, string> = { rivals: 'Division Rivals', 'squad-battles': 'Squad Battles', champions: 'Aurora Champions', draft: 'FUT Draft' };

/** A past edit in words, from its stored patch (the classic panel's patchText). */
export function describeEdit(patchJson: string, packName: (id: number) => string = (packId) => `pack ${packId}`): string {
  let patch: Record<string, unknown>;
  try {
    const parsed: unknown = JSON.parse(patchJson);
    if (!parsed || typeof parsed !== 'object') return patchJson;
    patch = parsed as Record<string, unknown>;
  } catch {
    return patchJson;
  }
  const count = Number(patch.count) || 1;
  const signed = (value: unknown) => (Number(value) > 0 ? `+${Number(value).toLocaleString('en-GB')}` : `−${Math.abs(Number(value)).toLocaleString('en-GB')}`);
  switch (patch.op) {
    case 'grantSeasonXp': return `Granted ${Number(patch.amount).toLocaleString('en-GB')} Season XP`;
    case 'grantPacks': return `Gifted ${count} × ${packName(Number(patch.packId))}`;
    case 'grantCard': return `Granted card ${String(patch.card)}${count > 1 ? ` × ${count}` : ''}`;
    case 'removeItems': return `Removed ${Array.isArray(patch.items) ? patch.items.length : 0} item(s): ${Array.isArray(patch.items) ? patch.items.join(', ') : ''}`;
    case 'grantItem': return `Granted item ${String(patch.item)}${count > 1 ? ` × ${count}` : ''}`;
    case 'takeItem': return `Took item ${String(patch.itemId)} away`;
    case 'access': {
      const parts = [patch.blocked === true ? 'blocked' : patch.blocked === false ? 'not blocked' : null,
        patch.maintenanceBypass === true ? 'maintenance bypass on' : patch.maintenanceBypass === false ? 'maintenance bypass off' : null,
        typeof patch.note === 'string' && patch.note ? `note “${patch.note}”` : null].filter(Boolean);
      return `Access: ${parts.join(', ') || 'unchanged'}`;
    }
    case 'resetMode': return `Reset ${MODE_NAMES[String(patch.mode)] ?? String(patch.mode)} progress`;
    case 'resetClub': return 'Deleted club contents and restarted onboarding';
    case 'dedupeCards': return patch.all ? 'Removed duplicate player cards' : 'Removed duplicate special cards';
    case undefined: break;
    default: return `${String(patch.op)} edit`;
  }
  if (patch.coinsDelta !== undefined || patch.pointsDelta !== undefined) {
    return [patch.coinsDelta !== undefined ? `Coins ${signed(patch.coinsDelta)}` : null, patch.pointsDelta !== undefined ? `Aurora Tokens ${signed(patch.pointsDelta)}` : null]
      .filter(Boolean).join(', ');
  }
  const set = [patch.coins !== undefined ? `Coins set to ${Number(patch.coins).toLocaleString('en-GB')}` : null,
    patch.points !== undefined ? `Aurora Tokens set to ${Number(patch.points).toLocaleString('en-GB')}` : null].filter(Boolean);
  return set.length ? set.join(', ') : Object.entries(patch).map(([key, value]) => `${key} = ${JSON.stringify(value)}`).join(', ');
}

export type Pile = 'club' | 'new' | 'transfer';

/** One item in a pile (OperatorPanel.Players.cs InventoryAsync). */
export interface InventoryItem {
  id: number;
  assetId?: number | null;
  resourceId?: number | null;
  name?: string;
  rating?: number;
  position?: string | null;
  /** A player card's six face stats in FIFA's order (the goalkeeper ones for a keeper). */
  attributes?: number[] | null;
  kind?: 'player' | 'special' | 'manager' | 'consumable' | 'clubItem' | 'other';
  special?: string | null;
  untradeable?: boolean;
  inSquads?: string[];
  /** What a non-player item is: "Chemistry style", "Kit", "My Stadium: Tifo"... */
  typeName?: string | null;
  /** In one of the club's slots (the active kit, badge, ball or stadium, or a My Stadium slot). */
  equipped?: boolean;
  /** Listed on the transfer market. */
  listed?: boolean;
  /** Whether the removeItems edit takes it: player cards that are not on the market. */
  removable?: boolean;
  art?: import('./assets').CardArt | null;
}

export interface InventoryReply {
  savedAt?: string | null;
  piles?: Partial<Record<Pile, InventoryItem[]>>;
  truncated?: Partial<Record<Pile, boolean>>;
  packs?: { packId: number; count: number; name?: string }[];
  notes?: string[];
}

export const useInventory = (id: number, pile: Pile) =>
  useQuery({ queryKey: ['inventory', id, pile], queryFn: () => api<InventoryReply>(`players/${id}/inventory?pile=${pile}`) });
