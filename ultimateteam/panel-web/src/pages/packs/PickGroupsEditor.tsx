// A pick pack's groups. A random group draws its choices from ratings, specials and sources; a fixed group (owner,
// 2026-10-04) offers exactly the cards listed in `choices`, for example two versions of one player.
import { useState } from 'react';
import type { Catalogue } from '../../api/packs';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { SwitchRow } from '../../ui/Switch';
import { CardChip, CardPicker } from '../sbc/CardPicker';
import { PromoSourceFields, type DrawSettings } from './PromoSourceFields';
import './listing.css';

export interface PickGroup extends DrawSettings {
  count?: number; choice_count?: number; min_rating?: number; max_rating?: number; promo_only?: boolean;
  label?: string; rareflags?: number[]; choices?: number[]; [key: string]: unknown;
}

export function PickGroupsEditor({ groups, onChange, catalogue, source }: {
  groups: PickGroup[]; onChange: (groups: PickGroup[]) => void; catalogue: Pick<Catalogue, 'promos' | 'pickLimits'>; source: DrawSettings;
}) {
  const update = (index: number, fields: Partial<PickGroup>) => onChange(groups.map((group, i) => i === index ? { ...group, ...fields } : group));
  const replace = (index: number, group: PickGroup) => onChange(groups.map((current, i) => i === index ? group : current));
  const [picking, setPicking] = useState<number | null>(null);
  return <div className="stack">
    {groups.map((group, index) => <fieldset className="stack" key={index}>
      <legend>Pick group {index + 1}</legend>
      <Field label="Choices" hint={Array.isArray(group.choices) ? 'The pick offers exactly these cards, in this order.' : 'Drawn from the ratings, specials and sources below.'}>
        <select value={Array.isArray(group.choices) ? 'fixed' : 'random'} onChange={(event) => replace(index, event.target.value === 'fixed'
          ? { count: group.count ?? 1, choices: [], ...(group.label && { label: group.label }) }
          : { count: group.count ?? 1, choice_count: catalogue.pickLimits.minChoices + 1, min_rating: 82, ...(group.label && { label: group.label }) })}>
          <option value="random">Random player pick</option>
          <option value="fixed">Fixed player pick (chosen cards)</option>
        </select>
      </Field>
      {Array.isArray(group.choices) ? <FixedChoices group={group} limits={catalogue.pickLimits} onPick={() => setPicking(index)}
        onChange={(fields) => update(index, fields)} /> : <>
      <div className="form-row">
        <Field label="Number of picks"><input type="number" min="1" max={catalogue.pickLimits.maxTokensPerGroup} value={group.count ?? 1}
          onChange={(event) => update(index, { count: Number(event.target.value) })} /></Field>
        <Field label="Choices per pick"><input type="number" min={catalogue.pickLimits.minChoices} max={catalogue.pickLimits.maxChoices} value={group.choice_count ?? catalogue.pickLimits.minChoices}
          onChange={(event) => update(index, { choice_count: Number(event.target.value) })} /></Field>
        <Field label="Minimum rating"><input type="number" min="0" max="99" value={group.min_rating ?? 0}
          onChange={(event) => update(index, { min_rating: Number(event.target.value) })} /></Field>
        <Field label="Maximum rating"><input type="number" min="0" max="99" value={group.max_rating ?? 99}
          onChange={(event) => update(index, { max_rating: Number(event.target.value) })} /></Field>
        <Field label="Pick label" hint="Empty for the automatic name"><input value={group.label ?? ''} maxLength={100}
          onChange={(event) => update(index, { label: event.target.value })} /></Field>
      </div>
      <SwitchRow label="Specials only" checked={group.promo_only === true} text="Every offered choice must be a special card."
        onChange={(checked) => update(index, { promo_only: checked })} />
      <div className="stack">
        <h3>Sources and odds for this group</h3>
        <PromoSourceFields value={{ ...source, ...group }} promos={catalogue.promos} inherited specialsOnly={group.promo_only === true} onChange={(value) => update(index, { ...value })} />
      </div></>}
      <Button variant="ghost" onClick={() => onChange(groups.filter((_, i) => i !== index))}>Remove group {index + 1}</Button>
    </fieldset>)}
    <Button onClick={() => onChange([...groups, { count: 1, choice_count: 3, min_rating: 82 }])}>Add pick group</Button>
    <CardPicker open={picking !== null} title="Add a card to the pick" onClose={() => setPicking(null)} onPick={(card) => {
      const group = picking === null ? undefined : groups[picking];
      if (picking !== null && group && card.resourceId && !group.choices?.includes(card.resourceId)) {
        const choices = [...(group.choices ?? []), card.resourceId];
        // The token shows the lowest rating it can give ("84+"), so it never promises more than the weakest card.
        update(picking, { choices, choice_count: choices.length,
          min_rating: Math.min(group.choices?.length ? group.min_rating ?? 99 : 99, card.rating ?? 0) });
      }
      setPicking(null);
    }} />
  </div>;
}

function FixedChoices({ group, limits, onPick, onChange }: {
  group: PickGroup; limits: Catalogue['pickLimits']; onPick: () => void; onChange: (fields: Partial<PickGroup>) => void;
}) {
  const choices = group.choices ?? [];
  const problem = choices.length < limits.minChoices ? `Add at least ${limits.minChoices} cards.` : null;
  return <div className="stack">
    <div className="form-row">
      <Field label="Number of picks"><input type="number" min="1" max={limits.maxTokensPerGroup} value={group.count ?? 1}
        onChange={(event) => onChange({ count: Number(event.target.value) })} /></Field>
      <Field label="Pick label" hint="For example “Messi pick”; empty for the automatic name"><input value={group.label ?? ''} maxLength={100}
        onChange={(event) => onChange({ label: event.target.value })} /></Field>
    </div>
    <ol className="fixed-choices" aria-label="Cards offered">
      {choices.map((resourceId, at) => (
        <li key={resourceId}>
          <CardChip resourceId={resourceId} />
          <Button size="sm" variant="ghost" aria-label={`Remove card ${resourceId}`} onClick={() => {
            const next = choices.filter((id) => id !== resourceId);
            onChange({ choices: next, choice_count: next.length });
          }}>Remove</Button>
          {at > 0 && <Button size="sm" variant="ghost" aria-label={`Move card ${resourceId} up`} onClick={() => {
            const next = [...choices];
            [next[at - 1], next[at]] = [next[at]!, next[at - 1]!];
            onChange({ choices: next });
          }}>↑</Button>}
        </li>
      ))}
    </ol>
    <div className="row-actions">
      <Button size="sm" disabled={choices.length >= limits.maxChoices} onClick={onPick}>Add card…</Button>
      <span className="dim">{choices.length} of {limits.minChoices} to {limits.maxChoices} cards</span>
    </div>
    {problem && <p className="dim" role="status">{problem}</p>}
  </div>;
}
