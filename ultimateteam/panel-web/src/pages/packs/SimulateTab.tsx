// Simulate: open any pack a number of times without giving anything to anyone (POST packs/simulate).
import { useState } from 'react';
import { useAssets } from '../../api/assets';
import { useCatalogue, usePackActions, type DrawnItem } from '../../api/packs';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { ErrorPanel, Loading, Section } from '../../ui/Layout';
import { DrawnCards } from './PackEditor';

const MAX_ROLLS = 20;

export function SimulateTab() {
  const catalogue = useCatalogue();
  const actions = usePackActions();
  const assets = useAssets().data;
  const [packId, setPackId] = useState<number | null>(null);
  const [rolls, setRolls] = useState('3');
  const [result, setResult] = useState<{ index: number; items?: DrawnItem[] }[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  if (catalogue.isPending) return <Loading />;
  if (catalogue.isError || !catalogue.data) return <ErrorPanel what="The pack catalogue" error={catalogue.error} onRetry={() => void catalogue.refetch()} />;
  const packs = catalogue.data.packs.filter((pack) => pack.pending !== 'New');
  const target = packId ?? packs[0]?.id ?? null;
  const count = Number(rolls);
  const valid = Number.isInteger(count) && count >= 1 && count <= MAX_ROLLS;
  const roll = () => {
    if (target === null || !valid) return;
    setError(null);
    actions.simulate.mutate({ packId: target, rolls: count }, { onSuccess: (reply) => setResult(reply.rolls ?? []), onError: (caught) => setError(caught.message) });
  };
  return (
    <Section title="Simulate" text="Opens a pack from its published definition as many times as you like. Nobody receives anything.">
      <div className="pack-action-controls">
        <Field label="Pack">
          <select value={target ?? ''} onChange={(event) => setPackId(Number(event.target.value))}>
            {packs.map((pack) => <option key={pack.id} value={pack.id}>{pack.description} (#{pack.id})</option>)}
          </select>
        </Field>
        <Field label="Rolls" hint={`1 to ${MAX_ROLLS}`} error={rolls && !valid ? `A whole number from 1 to ${MAX_ROLLS}.` : null}>
          <input type="number" min={1} max={MAX_ROLLS} value={rolls} onChange={(event) => setRolls(event.target.value)} />
        </Field>
        <Button variant="primary" onClick={roll} disabled={!valid || actions.simulate.isPending}>{actions.simulate.isPending ? 'Opening…' : 'Roll'}</Button>
      </div>
      {error && <p className="error-box" role="alert">{error}</p>}
      {result?.map((one) => (
        <div key={one.index} className="roll">
          <h3 className="kit-sub">Roll {one.index + 1}</h3>
          <DrawnCards items={one.items ?? []} assets={assets} />
        </div>
      ))}
    </Section>
  );
}
