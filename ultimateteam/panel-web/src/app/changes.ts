// Short, readable summaries of staged content changes, for the pending drawer and the Changes page.
import type { ContentChange } from '../api/types';

const AREA_LABEL: Record<string, string> = { rules: 'Rules', store_pack: 'Pack', store_pack_delete: 'Pack delete', store_pack_force_delete: 'Pack force delete', token_store_lineup: 'Token Store Lineup', store_lineup: 'Store lineup', player_card: 'Player cards', promo: 'Promos', promo_bucket: 'Promo buckets', promo_event: 'Promo events', icon_rotation: 'Icon rotations', player_pick: 'Player picks', champions_rank: 'Champions rewards', rivals_rank: 'Rivals rewards', rivals_promotion: 'Rivals promotion rewards', squad_battles_tier: 'Squad Battles rewards', draft_level: 'Draft rewards', sbc_set: 'SBC', sbc_live: 'Live SBCs' };

export const areaLabel = (area: string) => AREA_LABEL[area] ?? area.replace(/_/g, ' ');

/** The row a change is about: "market › maintenanceIntervalSeconds", "pack 7". */
export function keyLabel(key: Record<string, unknown>): string {
  const parts = Object.entries(key ?? {}).map(([name, value]) =>
    Array.isArray(value) ? value.map(String).join(' › ') : name === 'packId' ? `pack ${String(value)}` : name === 'resourceId' ? `card ${String(value)}` : name === 'tierLevel' ? `tier ${String(value)}` : name === 'division' ? `Division ${String(value)}`
      : name === 'rank' ? `Rank ${['I', 'II', 'III', 'IV', 'V', 'VI'][Number(value) - 1] ?? String(value)}`
      : name === 'mode' && (value === 'online' || value === 'single') ? (value === 'online' ? 'Online' : 'Single-player')
      : name === 'wins' ? `${String(value)} ${Number(value) === 1 ? 'win' : 'wins'}` : String(value));
  return parts.join(' › ') || '—';
}

const shown = (value: unknown) => {
  if (value === undefined) return '–';
  const text = JSON.stringify(value);
  return text.length > 40 ? `${text.slice(0, 39)}…` : text;
};

const isObject = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);

/** "3 unopened copies from 1 club", read from the counts a force delete was staged with. */
export function unopenedText(before: unknown): string {
  const counts = isObject(before) && isObject(before.unopened) ? before.unopened : {};
  const copies = Number(counts.copies ?? 0);
  const clubs = Number(counts.clubs ?? 0);
  return `${copies} unopened ${copies === 1 ? 'copy' : 'copies'} from ${clubs} ${clubs === 1 ? 'club' : 'clubs'}`;
}

/** What a change is about, for a title: an SBC by its name, the live SBC list as a whole, any other row by its key. */
export function changeTitle(change: ContentChange): string {
  if (change.area === 'sbc_live') return 'Live SBCs';
  if (change.area === 'sbc_set') {
    const name = (value: unknown) => (isObject(value) && isObject(value.definition) && typeof value.definition.name === 'string' ? value.definition.name : '');
    return name(change.after) || name(change.before) || keyLabel(change.key);
  }
  return keyLabel(change.key);
}

/** Show actual nested fields, keeping arrays (membership, pick groups) together for review. */
export function reviewValues(change: ContentChange): { before: Record<string, unknown>; after: Record<string, unknown> | null } {
  const before = isObject(change.before) ? change.before : {};
  const after = isObject(change.after) ? change.after : null;
  const fields = (value: Record<string, unknown>, prefix = ''): Record<string, unknown> => Object.fromEntries(
    Object.entries(value).flatMap(([key, field]) => {
      const name = prefix ? `${prefix}.${key}` : key;
      return isObject(field) && Object.keys(field).length ? Object.entries(fields(field, name)) : [[name, field]];
    }));
  return { before: fields(before), after: after ? fields(after) : null };
}

/** What changed, as "field: old → new" lines (top-level fields of an object row; the whole value otherwise). */
export function fieldChanges(change: ContentChange, limit = 3, sbcName: (key: string) => string = (key) => key): string[] {
  if (change.area === 'store_pack_delete') return ['pack definition deleted'];
  if (change.area === 'store_pack_force_delete') return [`pack definition deleted, ${unopenedText(change.before)} removed`];
  if (change.area === 'sbc_live') return liveSbcChanges(change, limit, sbcName);
  if (change.area === 'sbc_set' && change.operation === 'hide') return ['removed from the catalogue, or a bundled SBC reverted to its sbc.json definition'];
  if (change.operation === 'hide' || change.operation === 'delete') return ['hidden from the game'];
  const { before, after } = reviewValues(change);
  if (!after) return [`${shown(change.before)} → ${shown(change.after)}`];
  if (!isObject(change.before) && change.area !== 'player_card') return [`${shown(change.before)} → ${shown(change.after)}`];
  const names = [...new Set([...Object.keys(before), ...Object.keys(after)])]
    .filter((name) => JSON.stringify(before[name]) !== JSON.stringify(after[name]));
  const lines = names.slice(0, limit).map((name) => `${name}: ${shown(before[name])} → ${shown(after[name])}`);
  if (names.length > limit) lines.push(`and ${names.length - limit} more`);
  return lines.length ? lines : ['no field changes'];
}

/** The live SBC list as "82+ Player Pick listed, on" / "Gold Upgrade switched off" / "Silver Upgrade taken off Live SBCs" lines. */
function liveSbcChanges(change: ContentChange, limit: number, name: (key: string) => string): string[] {
  const entries = (value: unknown) => new Map((isObject(value) && Array.isArray(value.listing) ? value.listing : [])
    .filter(isObject).map((entry) => [String(entry.setKey), entry.enabled === true] as const));
  const before = entries(change.before);
  const after = entries(change.after);
  const lines = [
    ...[...after].filter(([key]) => !before.has(key)).map(([key, on]) => `${name(key)} listed, ${on ? 'on' : 'off'}`),
    ...[...after].filter(([key, on]) => before.has(key) && before.get(key) !== on).map(([key, on]) => `${name(key)} switched ${on ? 'on' : 'off'}`),
    ...[...before.keys()].filter((key) => !after.has(key)).map((key) => `${name(key)} taken off Live SBCs`),
  ];
  if (lines.length > limit) return [...lines.slice(0, limit), `and ${lines.length - limit} more`];
  return lines.length ? lines : ['no changes to Live SBCs'];
}
