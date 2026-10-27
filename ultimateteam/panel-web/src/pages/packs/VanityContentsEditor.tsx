// A My Stadium pack's slots (store_packs.vanity_pack, migration 123): one item per slot, drawn from the slot's item types.
// A slot may weigh asset id ranges against each other (pools), e.g. basic tifos against club and player tifos. The server
// checks the same rules (VanityRules.PackIssue) and never draws reward-only items or one the club owns.
import type { VanityType } from '../../api/types';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';

export interface VanityPool { weight: number; min?: number | undefined; max?: number | undefined }
export interface VanitySlot { subtypes: number[]; pools?: VanityPool[] }
export interface VanityPack { slots: VanitySlot[] }

export const MAX_VANITY_SLOTS = 12;
export const DEFAULT_VANITY: VanityPack = { slots: [{ subtypes: [500] }] };

export function validateVanityPack(value: VanityPack, types: VanityType[]): string | null {
  const known = new Set(types.map((type) => type.subtype));
  if (!value.slots.length || value.slots.length > MAX_VANITY_SLOTS) return `A My Stadium pack needs 1 to ${MAX_VANITY_SLOTS} slots.`;
  for (const [index, slot] of value.slots.entries()) {
    const name = `Slot ${index + 1}`;
    if (!slot.subtypes.length || slot.subtypes.some((subtype) => !known.has(subtype))) return `${name}: choose at least one item type.`;
    for (const pool of slot.pools ?? []) {
      if (!(pool.weight > 0)) return `${name}: a pool's weight is a number above 0.`;
      if ([pool.min, pool.max].some((bound) => bound !== undefined && !Number.isInteger(bound))) return `${name}: asset ids are whole numbers.`;
      if (pool.min !== undefined && pool.max !== undefined && pool.min > pool.max) return `${name}: a pool's lowest asset id exceeds its highest.`;
    }
  }
  return null;
}

/** Drops empty pool lists and unset bounds, so the saved spec matches what the server stores. */
export function cleanVanityPack(value: VanityPack): VanityPack {
  return { slots: value.slots.map((slot) => slot.pools?.length
    ? { subtypes: slot.subtypes, pools: slot.pools.map((pool) => ({ weight: pool.weight,
      ...(pool.min !== undefined ? { min: pool.min } : {}), ...(pool.max !== undefined ? { max: pool.max } : {}) })) }
    : { subtypes: slot.subtypes }) };
}

const bound = (text: string) => (text.trim() === '' ? undefined : Number(text));

export function VanityContentsEditor({ value, types, onChange }: { value: VanityPack; types: VanityType[]; onChange: (value: VanityPack) => void }) {
  const setSlot = (index: number, slot: VanitySlot) => onChange({ slots: value.slots.map((current, at) => (at === index ? slot : current)) });
  return <div className="stack">
    {value.slots.map((slot, index) => <div key={index} className="stack">
      <div className="form-row">
        <b>Slot {index + 1}</b>
        <Button size="sm" variant="ghost" disabled={value.slots.length <= 1}
          onClick={() => onChange({ slots: value.slots.filter((_, at) => at !== index) })}>Remove slot</Button>
      </div>
      <Field label={`Slot ${index + 1} item types`} hint="One item is drawn from these types.">
        <select multiple value={slot.subtypes.map(String)} onChange={(event) => setSlot(index, { ...slot,
          subtypes: [...event.target.selectedOptions].map((option) => Number(option.value)) })}>
          {types.map((type) => <option key={type.subtype} value={type.subtype}>{type.name} ({type.items} items)</option>)}
        </select>
      </Field>
      {(slot.pools ?? []).map((pool, poolIndex) => {
        const setPool = (next: VanityPool) => setSlot(index, { ...slot, pools: slot.pools!.map((current, at) => (at === poolIndex ? next : current)) });
        return <div key={poolIndex} className="form-row">
          <Field label={`Slot ${index + 1} pool ${poolIndex + 1} weight`}>
            <input type="number" min="0" value={pool.weight} onChange={(event) => setPool({ ...pool, weight: Number(event.target.value) })} />
          </Field>
          <Field label={`Slot ${index + 1} pool ${poolIndex + 1} lowest asset id`} hint="Empty for no limit">
            <input inputMode="numeric" value={pool.min ?? ''} onChange={(event) => setPool({ ...pool, min: bound(event.target.value) })} />
          </Field>
          <Field label={`Slot ${index + 1} pool ${poolIndex + 1} highest asset id`} hint="Empty for no limit">
            <input inputMode="numeric" value={pool.max ?? ''} onChange={(event) => setPool({ ...pool, max: bound(event.target.value) })} />
          </Field>
          <Button size="sm" variant="ghost" onClick={() => setSlot(index, { ...slot, pools: slot.pools!.filter((_, at) => at !== poolIndex) })}>Remove pool</Button>
        </div>;
      })}
      <div className="row-actions">
        <Button size="sm" onClick={() => setSlot(index, { ...slot, pools: [...(slot.pools ?? []), { weight: 1 }] })}>Add pool</Button>
      </div>
    </div>)}
    <div className="row-actions">
      <Button disabled={value.slots.length >= MAX_VANITY_SLOTS} onClick={() => onChange({ slots: [...value.slots, { subtypes: [500] }] })}>Add slot</Button>
    </div>
    <p className="dim">Each slot gives one item the club does not own yet, never one only rewards give. Without pools a slot draws evenly from its types;
      pools weigh asset id ranges against each other (basic tifos are 6800000 to 6809999, club and player tifos are below 6800000). The pack size is the number of slots.</p>
  </div>;
}
