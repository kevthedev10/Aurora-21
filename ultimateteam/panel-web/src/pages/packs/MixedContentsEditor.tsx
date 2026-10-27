import { Field } from '../../ui/Field';

export const ITEM_CATEGORIES = ['players', 'consumables', 'stadium', 'staff'] as const;
export type MixedContents = { tier: 'bronze' | 'silver' | 'gold' } & Record<typeof ITEM_CATEGORIES[number], { min: number; max: number }>;
export const DEFAULT_MIXED: MixedContents = { tier: 'gold', players: { min: 3, max: 6 }, consumables: { min: 5, max: 9 },
  stadium: { min: 0, max: 3 }, staff: { min: 0, max: 2 } };
const LABELS = { players: 'Players', consumables: 'Consumables', stadium: 'Stadium items', staff: 'Staff' };

export function validateMixedContents(value: MixedContents, size: number, rare: number): string | null {
  if (!Number.isInteger(size) || size < 1 || size > 100 || !Number.isInteger(rare) || rare < 0 || rare > size)
    return 'Choose a pack size from 1 to 100 and a rare count within that size.';
  for (const category of ITEM_CATEGORIES) {
    const { min, max } = value[category];
    if (![min, max].every((number) => Number.isInteger(number) && number >= 0 && number <= size) || min > max)
      return `${LABELS[category]}: enter a minimum and maximum within the pack size.`;
  }
  const min = ITEM_CATEGORIES.reduce((total, category) => total + value[category].min, 0);
  const max = ITEM_CATEGORIES.reduce((total, category) => total + value[category].max, 0);
  if (min > size || max < size) return 'The item ranges cannot add up to the pack size.';
  return value.players.max < rare ? 'Rare slots are rare players: raise the players maximum to at least the rare count.' : null;
}

export function MixedContentsEditor({ value, onChange }: { value: MixedContents; onChange: (value: MixedContents) => void }) {
  return <div className="stack">
    <Field label="Item tier"><select value={value.tier} onChange={(event) => onChange({ ...value, tier: event.target.value as MixedContents['tier'] })}>
      <option value="bronze">Bronze</option><option value="silver">Silver</option><option value="gold">Gold</option>
    </select></Field>
    <div className="form-row">{ITEM_CATEGORIES.map((category) => <div key={category} className="stack">
      <b>{LABELS[category]}</b>
      {(['min', 'max'] as const).map((bound) => <Field key={bound} label={`${LABELS[category]} ${bound === 'min' ? 'minimum' : 'maximum'}`}>
        <input type="number" min="0" max="100" value={value[category][bound]} onChange={(event) => onChange({ ...value,
          [category]: { ...value[category], [bound]: Number(event.target.value) } })} />
      </Field>)}
    </div>)}</div>
    <p className="dim">Counts vary within these ranges and always add up to the pack size. Rare slots are rare players; other items keep their own rarity, which only sets how often they drop. Staff uses managers; stadium items include kits, badges, balls and stadium cards.</p>
  </div>;
}
