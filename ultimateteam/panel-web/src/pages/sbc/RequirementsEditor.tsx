// A challenge's requirements: the squad size (Players in squad, which follows the open slots when slots are locked) and
// the requirement rules, each with its comparison and number or quality, shown as FIFA will label it. Attributes the editor
// does not manage (a rating's max, say) are kept.
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { SearchFilter } from '../cards/SearchFilter';
import { ErrorPanel } from '../../ui/Layout';
import type { SbcReply, SbcRequirementType, SbcRule } from '../../api/sbc';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';

const titleCase = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);
/** Picker names describe the requirement; the separate comparison chooses its bound. */
export const typeName = (type: SbcRequirementType) => (({
  CLUBS: 'Clubs in squad', LEAGUES: 'Leagues in squad', NATIONS: 'Nations in squad',
  SAME_CLUB: 'Players from the same club', SAME_LEAGUE: 'Players from the same league', SAME_NATION: 'Players from the same nation',
  LEAGUE_PLAYERS: 'Players from a specific league', NATION_PLAYERS: 'Players from a specific nation',
  CLUB_PLAYERS: 'Players from a specific club', QUALITY_PLAYERS: 'Players of a quality',
} as Record<string, string>)[type.type] ?? type.label).replace(/:?\s*\{value\}.*$/, '').replace(/\b(Min|Max|Exactly)\b\s*/gi, '').replace(/:\s*$/, '').trim();

const omit = (attributes: Record<string, number | string>, ...keys: string[]) =>
  Object.fromEntries(Object.entries(attributes).filter(([key]) => !keys.includes(key)));

export function RequirementsEditor({ data, rules, lockedPlayers, onChange }: {
  data: SbcReply;
  rules: SbcRule[];
  /** The open slots when slots are locked: the squad size is then fixed to it. */
  lockedPlayers: number | null;
  onChange: (rules: SbcRule[]) => void;
}) {
  const filters = useQuery({ queryKey: ['cards-filters'], queryFn: () => api<{ leagues: { id: number; name: string }[]; nations: { id: number; name: string }[]; clubs: { id: number; name: string; leagueName: string }[] }>('cards/filters'), enabled: data.requirements.some(type => type.selector && type.selector !== 'quality') });
  const oldLeagues = ['PREMIER_LEAGUE_PLAYERS', 'BUNDESLIGA_PLAYERS', 'LIGUE_1_PLAYERS'];
  const requirements = data.requirements.filter(type => type.type !== 'AGE');
  const types = requirements.filter(type => type.type !== 'PLAYERS' && (!oldLeagues.includes(type.type) || rules.some(rule => rule.type === type.type)));
  const byType = new Map(requirements.map((type) => [type.type, type]));
  const playersAt = rules.findIndex((rule) => rule.type.toUpperCase() === 'PLAYERS');
  const players = playersAt >= 0 ? Number(rules[playersAt]!.attributes?.max ?? rules[playersAt]!.attributes?.min ?? data.squadSize) : data.squadSize;
  const setPlayers = (count: number) => {
    const rule: SbcRule = { type: 'PLAYERS', attributes: { min: count, max: count } };
    onChange(playersAt >= 0 ? rules.map((entry, at) => at === playersAt ? rule : entry) : [rule, ...rules]);
  };
  const set = (index: number, rule: SbcRule) => onChange(rules.map((entry, at) => at === index ? rule : entry));
  const fresh = (type: string): SbcRule => {
    const def = byType.get(type);
    if (def?.bound === 'value') return { type, attributes: { value: def.qualities[0] ?? 'gold' } };
    return { type, attributes: { [def?.bound === 'max' ? 'max' : 'min']: 1, ...(def?.selector === 'quality' ? { quality: 'gold', selectionName: 'Gold' } : {}) } };
  };
  const unused = types.find((type) => !rules.some((rule) => rule.type.toUpperCase() === type.type)) ?? types[0];

  return (
    <div className="sbc-requirements">
      <div className="sbc-form">
        <Field label="Players in squad" hint={lockedPlayers !== null ? 'Follows the open slots on the pitch.' : 'How many cards the player places (1 to 11).'}>
          <input type="number" min={1} max={data.squadSize} value={lockedPlayers ?? players} disabled={lockedPlayers !== null}
            onChange={(event) => setPlayers(Math.max(1, Math.min(data.squadSize, Number(event.target.value) || 1)))} />
        </Field>
      </div>
      <ul className="sbc-rule-list">
        {rules.map((rule, index) => {
          if (rule.type.toUpperCase() === 'PLAYERS') return null;
          const type = byType.get(rule.type.toUpperCase());
          const attributes = rule.attributes ?? {};
          if (!type) {
            return (
              <li key={index} className="sbc-rule sbc-rule-off">
                <span><strong className="mono">{rule.type}</strong> <span className="dim">Not used: switched off under Settings, so FIFA neither shows nor checks it.</span></span>
                <Button size="sm" variant="ghost" onClick={() => onChange(rules.filter((_, at) => at !== index))}>Remove</Button>
              </li>
            );
          }
          const mode = 'exact' in attributes ? 'exact' : 'min' in attributes ? 'min' : 'max' in attributes ? 'max' : type.bound === 'max' ? 'max' : 'min';
          const amount = Number(attributes[mode] ?? 0);
          const shownValue = type.bound === 'value' ? titleCase(String(attributes.value ?? '')) : String(amount);
          const exactLabel = data.requirementRules.find((entry) => entry.type === type.type)?.exact?.label;
          const comparison = mode === 'exact' ? 'Exactly' : mode === 'min' ? 'Min' : 'Max';
          const label = (mode === 'exact' && exactLabel ? exactLabel : type.label.replace(/Min |Max /g, comparison + ' ')).replace('{value}', shownValue).replace('{comparison}', comparison).replace('{selection}', String(attributes.selectionName ?? attributes.quality ?? 'Choose a selection'));
          const limit = ['RATING', 'CHEMISTRY', 'AGE'].includes(type.type) ? 100 : (lockedPlayers ?? players);
          return (
            <li key={index} className="sbc-rule">
              <div className="sbc-rule-fields">
                <Field label="Requirement">
                  <select value={type.type} onChange={(event) => set(index, fresh(event.target.value))}>
                    {types.map((entry) => <option key={entry.type} value={entry.type}>{typeName(entry)}</option>)}
                  </select>
                </Field>
                {type.selector === 'quality' && <Field label="Player quality"><select value={String(attributes.quality ?? 'gold')} onChange={event => set(index, { type: rule.type, attributes: { ...attributes, quality: event.target.value, selectionName: titleCase(event.target.value) } })}>
                  {type.qualities.map(quality => <option key={quality} value={quality}>{titleCase(quality)}</option>)}
                </select></Field>}
                {type.selector && type.selector !== 'quality' && <SearchFilter label={type.selector === 'leagueId' ? 'League' : type.selector === 'nationId' ? 'Nation' : 'Club'}
                  value={String(attributes[type.selector] ?? '')}
                  options={(type.selector === 'leagueId' ? filters.data?.leagues ?? [] : type.selector === 'nationId' ? filters.data?.nations ?? [] : filters.data?.clubs ?? []).map(option => ({ value: String(option.id), label: option.name + ('leagueName' in option ? ` (${option.leagueName})` : '') }))}
                  onChange={option => set(index, { type: rule.type, attributes: { ...attributes, [type.selector!]: Number(option?.value ?? 0), selectionName: option?.label ?? '' } })} />}
                {type.bound === 'value' ? (
                  <Field label="Quality">
                    <select value={String(attributes.value ?? '')} onChange={(event) => set(index, { type: rule.type, attributes: { ...attributes, value: event.target.value } })}>
                      {type.qualities.map((quality) => <option key={quality} value={quality}>{titleCase(quality)}</option>)}
                    </select>
                  </Field>
                ) : <>
                  <Field label="Comparison">
                    <select value={mode} disabled={!type.exact} onChange={(event) => {
                      const next = event.target.value;
                      set(index, { type: rule.type, attributes: { ...omit(attributes, 'min', 'max', 'exact'), [next]: amount } });
                    }}>
                      {(type.exact || type.bound === 'min') && <option value="min">At least</option>}
                      {(type.exact || type.bound === 'max') && <option value="max">At most</option>}
                      {type.exact && <option value="exact">Exactly</option>}
                    </select>
                  </Field>
                  <Field label="Number">
                    <input type="number" min={0} max={limit} value={amount}
                      onChange={(event) => set(index, { type: rule.type, attributes: { ...attributes, [mode]: Math.max(0, Math.min(limit, Number(event.target.value) || 0)) } })} />
                  </Field>
                </>}
              </div>
              <p className="sbc-rule-label"><span className="dim">Shows as</span> {label}</p>
              <Button size="sm" variant="ghost" className="sbc-rule-remove" aria-label={`Remove ${typeName(type)}`} onClick={() => onChange(rules.filter((_, at) => at !== index))}>Remove</Button>
            </li>
          );
        })}
      </ul>
      {filters.isError && <ErrorPanel what="League, nation and club selections" error={filters.error} onRetry={() => void filters.refetch()} />}
      {unused && <Button size="sm" onClick={() => onChange([...rules, fresh(unused.type)])}>Add requirement</Button>}
    </div>
  );
}
