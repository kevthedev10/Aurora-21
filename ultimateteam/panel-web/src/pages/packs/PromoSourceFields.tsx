// A pack's or pick group's sources and its odds table (pack odds v3, docs/pack-odds-v3.md): one chance per special category
// the sources offer, always complete, with the rest drawing standard cards. There are no default odds: an empty table
// starts from the template values, and a category the table leaves out has no chance.
import { useEffect, useRef } from 'react';
import type { PromoOption } from '../../api/types';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { SwitchRow } from '../../ui/Switch';

export interface DrawSettings {
  include_general_pool?: boolean;
  promo_codes?: string[];
  category_chances?: Record<string, number>;
}

const standard = new Set(['gold_rare', 'gold_common', 'silver_rare', 'silver_common', 'bronze_rare', 'bronze_common']);

/** The odds-table row for specials rated above a silver or bronze slot whose base card is in that tier. */
export const UPGRADED = 'upgraded';

/** The special categories a pack's sources offer, each with its label: Icon and TOTW by category, other promos by code. */
export interface OddsCategories { categories: string[]; labels: Map<string, string> }

/** The promos in the published event right now; null until loaded (or when the request failed). */
export function usePromoAvailability(enabled = true) {
  const availability = useQuery({ queryKey: ['promo-availability', 'published'],
    queryFn: () => api<{ promos: { code: string; generallyAvailable: boolean }[] }>('promos/availability'),
    enabled, refetchInterval: 10_000, retry: false });
  const active = availability.isSuccess ? new Set(availability.data.promos.filter(promo => promo.generallyAvailable).map(promo => promo.code)) : null;
  return { availability, active };
}

/**
 * The rows these sources offer; null when they include the current event and it has not loaded. Upgraded specials are a
 * row of packs with silver or bronze slots.
 */
export function oddsCategories(value: DrawSettings, promos: PromoOption[], active: ReadonlySet<string> | null, upgraded = false): OddsCategories | null {
  const general = value.include_general_pool !== false;
  if (general && !active) return null;
  const labels = new Map(promos.filter(promo => (general && active?.has(promo.code))
    || value.promo_codes?.includes(promo.code)).filter(promo => !standard.has(promo.category)).map(promo =>
      [promo.category === 'icon' || promo.category === 'totw' ? promo.category : promo.code,
        promo.category === 'icon' ? 'Icon' : promo.category === 'totw' ? 'TOTW' : promo.name]));
  const categories = [...labels.keys()].sort((a, b) => labels.get(a)!.localeCompare(labels.get(b)!));
  if (upgraded) { labels.set(UPGRADED, 'Upgraded specials'); categories.push(UPGRADED); }
  return { categories, labels };
}

/**
 * Why these odds cannot be staged, or null. The table must be complete: every offered row needs a value (0% leaves it
 * out), so adding or dropping a promo from a pack is a deliberate choice rather than an empty field. A specials-only pack
 * or pick has no standard rest, so its odds must add up to exactly 100%.
 */
export function oddsIssue(value: DrawSettings, shown: OddsCategories | null, specialsOnly = false): string | null {
  const chances = Object.values(value.category_chances ?? {});
  if (chances.some(chance => !Number.isFinite(chance) || chance < 0 || chance > 1)) return 'Each category chance must be between 0% and 100%.';
  const sum = chances.reduce((total, chance) => total + chance, 0);
  if (sum > 1.0000001) return 'Total category odds cannot exceed 100%.';
  if (!shown) return 'The current event categories have not loaded, so these odds cannot be checked yet.';
  const missing = shown.categories.filter(category => value.category_chances?.[category] === undefined);
  if (missing.length) return `Fill in the odds for every category (0% to leave one out). Missing: ${missing.map(category => shown.labels.get(category)).join(', ')}.`;
  if (specialsOnly && chances.length > 0 && Math.abs(sum - 1) > 0.0000001) return 'Specials-only odds must add up to exactly 100%.';
  return null;
}

export function PromoSourceFields({ value, onChange, promos, inherited = false, upgraded = false, specialsOnly = false, template }: {
  value: DrawSettings; onChange: (value: DrawSettings) => void; promos: PromoOption[]; inherited?: boolean;
  /** Show the Upgraded specials row (packs with silver or bronze slots). */
  upgraded?: boolean;
  specialsOnly?: boolean;
  /** The starting chance of each row for an empty table; any other row starts at 0%. */
  template?: Record<string, number>;
}) {
  const general = value.include_general_pool !== false;
  const { availability, active } = usePromoAvailability(general);
  const shown = oddsCategories(value, promos, active, upgraded);
  const categories = shown?.categories ?? [];
  const labels = shown?.labels ?? new Map<string, string>();
  // An empty table starts from the template once the offered rows are known, so every value is visible before staging.
  const prefilled = useRef(false);
  const empty = Object.keys(value.category_chances ?? {}).length === 0;
  useEffect(() => {
    if (!template || inherited || prefilled.current || !shown || !empty) return;
    prefilled.current = true;
    onChange({ ...value, category_chances: Object.fromEntries(shown.categories.map((category) => [category, template[category] ?? 0])) });
  }, [template, inherited, shown, empty, value, onChange]);
  const inactive = Object.keys(value.category_chances ?? {}).filter(category => !categories.includes(category));
  const total = Object.values(value.category_chances ?? {}).reduce((sum, chance) => sum + chance, 0) * 100;
  const issue = oddsIssue(value, shown, specialsOnly);
  return <div className="stack">
    <SwitchRow label="Include standard pool and current event" checked={value.include_general_pool !== false}
      text={inherited ? 'Starts with the pack sources; changes here apply to this pick group.' : 'Bronze, Silver, Gold, the current Icon tier and promos in the active event.'}
      onChange={(checked) => onChange({ ...value, include_general_pool: checked })} />
    <Field label="Specific promos" hint="These are included even outside an event. Turn off the general pool to use only these promos.">
      <select multiple value={value.promo_codes ?? []} onChange={(event) => onChange({ ...value,
        promo_codes: Array.from(event.target.selectedOptions, (option) => option.value) })}>
        {promos.map((promo) => <option key={promo.code} value={promo.code}>{promo.name}</option>)}
      </select>
    </Field>
    <div className="stack">
      <h3>Odds</h3>
      <p className="dim">Percent per rare slot (per choice for picks). Every row needs a value: 0% leaves that category out, and the rest draws standard cards.
        {specialsOnly ? ' Specials only: the rows share out every slot, so they must add up to exactly 100%.' : ''}
        {upgraded ? ' Icons only drop in gold slots and upgraded specials only in silver and bronze slots; elsewhere that share draws a standard card.' : ''}</p>
      <p className="dim">A chosen category with no eligible card for a slot falls back to a standard rare. Use a special player requirement to guarantee specials.</p>
      {general && availability.isPending && <p className="dim">Loading current event categories…</p>}
      {general && availability.isError && <p className="error-box" role="alert">Current event categories could not be loaded. <Button size="sm" onClick={() => void availability.refetch()}>Retry</Button></p>}
      {inactive.length > 0 && (!general || availability.isSuccess) && <p className="notice">
        Saved odds for unavailable categories are retained: {inactive.map(category => `${category.replaceAll('_', ' ')} (${(value.category_chances?.[category] ?? 0) * 100}%)`).join(', ')}.
        {' '}<Button size="sm" onClick={() => onChange({ ...value, category_chances: Object.fromEntries(
          Object.entries(value.category_chances ?? {}).filter(([category]) => categories.includes(category))) })}>Clear unavailable odds</Button>
      </p>}
      {!categories.length && (!general || availability.isSuccess) && <p className="dim">No special categories are available from these promo sources.</p>}
      <div className="form-row">
        {categories.map((category) => <Field key={category} label={`${labels.get(category)} (%)`}>
          <input type="number" min="0" max="100" step="any" aria-invalid={!!issue} value={value.category_chances?.[category] === undefined ? '' : Number((value.category_chances[category]! * 100).toFixed(6))}
            onChange={(event) => {
              const chances = { ...value.category_chances };
              if (event.target.value === '') delete chances[category];
              else chances[category] = Number(event.target.value) / 100;
              onChange({ ...value, category_chances: chances });
            }} />
        </Field>)}
      </div>
      <p className="dim">Specials: {Number(total.toFixed(4))}% · Standard: {Number(Math.max(0, 100 - total).toFixed(4))}%</p>
      {issue && <p className="error-box" role="alert">{issue}</p>}
    </div>
  </div>;
}
