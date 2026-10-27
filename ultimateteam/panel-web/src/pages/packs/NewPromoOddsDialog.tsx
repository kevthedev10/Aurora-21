// After a rotation is saved: packs that draw the current event but have no odds row for one of its promos get one, chosen
// deliberately (pack odds v3, docs/pack-odds-v3.md). Ticked packs get the chance entered; unticked packs get 0%, so their
// table stays complete and the promo stays out of them. Each pack save applies live and refreshes the revision.
import { useEffect, useState } from 'react';
import { useCatalogue, usePackActions, type CatalogueEntry } from '../../api/packs';
import { api } from '../../api/client';
import type { PackCatalogReply } from '../../api/types';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { ErrorPanel, Loading } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';

export interface EventPromo { code: string; name: string; category: string }

/** The odds-table row a promo is drawn through: Icon and TOTW by category, every other promo by its own code. */
export const oddsRow = (promo: EventPromo) => (promo.category === 'icon' || promo.category === 'totw' ? promo.category : promo.code);

const chancesOf = (pack: CatalogueEntry) => (pack.config.category_chances as Record<string, number> | undefined) ?? {};

/** Packs drawing the general pool that lack one of these rows; view-only and specials-only packs are edited by hand. */
export function packsMissing(packs: CatalogueEntry[], rows: string[]) {
  return packs.filter((pack) => !pack.viewOnlyReason && pack.config.include_general_pool !== false && pack.config.promo_only !== true
    && rows.some((row) => chancesOf(pack)[row] === undefined));
}

export function NewPromoOddsDialog({ promos, onClose, destination }: { promos: EventPromo[]; onClose: (completed?: boolean) => void; destination?: 'immediate' }) {
  const catalogue = useCatalogue(destination);
  const actions = usePackActions(destination);
  const toast = useToast();
  const rows = [...new Map(promos.map((promo) => [oddsRow(promo), promo])).entries()];
  const [chances, setChances] = useState<Record<string, string>>({});
  const [unticked, setUnticked] = useState<Set<number>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const missing = catalogue.data ? packsMissing(catalogue.data.packs, rows.map(([row]) => row)) : null;
  // Nothing to ask when every pack already has these rows.
  useEffect(() => { if (missing?.length === 0 && !busy) onClose(true); }, [missing?.length, busy, onClose]);
  if (catalogue.isPending || catalogue.isError) return <Dialog open wide title="Odds for the event's promos" onOpenChange={open => { if (!open) onClose(false); }}
    footer={<Button onClick={() => onClose(false)}>Back to drop</Button>}>
    {catalogue.isError ? <ErrorPanel what="Pack odds" error={catalogue.error} onRetry={() => void catalogue.refetch()} /> : <Loading label="Loading pack odds" />}
  </Dialog>;
  if (!catalogue.data || !missing?.length) return null;
  const values = Object.fromEntries(rows.map(([row]) => [row, Number(chances[row] ?? '0') / 100]));
  const invalid = Object.values(values).some((value) => !Number.isFinite(value) || value < 0 || value > 1);
  const tableFor = (pack: CatalogueEntry) => {
    const table = { ...chancesOf(pack) };
    for (const [row] of rows) if (table[row] === undefined) table[row] = unticked.has(pack.id) ? 0 : values[row]!;
    return table;
  };
  const over = missing.filter((pack) => Object.values(tableFor(pack)).reduce((sum, value) => sum + value, 0) > 1.0000001);
  const stage = async () => {
    setBusy(true); setError(null);
    try {
      for (const pack of missing) {
        const latest = await api<PackCatalogReply>(destination === 'immediate' ? 'packs/catalog?dropId=' : 'packs/catalog');
        const current = latest.packs?.find(row => row.id === pack.id);
        if (!current || JSON.stringify(current.config?.category_chances ?? {}) !== JSON.stringify(chancesOf(pack)))
          throw new Error('Pack odds changed. Reopen this editor before saving.');
        await actions.stage.mutateAsync({ id: pack.id, revision: latest.revision, pack: { category_chances: tableFor(pack) } });
      }
      toast(`Odds saved for ${missing.length} ${missing.length === 1 ? 'pack' : 'packs'}.`, 'staged');
      onClose(true);
    } catch (caught) {
      setError(`${(caught as Error).message} Packs saved before this one keep their changes.`);
    } finally { setBusy(false); }
  };
  return <Dialog open wide title="Odds for the event's promos" onOpenChange={(open) => { if (!open && !busy) onClose(false); }}
    description="These packs draw from the current event but have no odds for these promos yet. Ticked packs get the chance below per rare slot (per choice for picks); unticked packs get 0%, which keeps the promo out of them."
    footer={<><Button disabled={busy} onClick={() => onClose(false)}>Skip for now</Button>
      <Button variant="staged" disabled={busy || invalid || over.length > 0} onClick={() => void stage()}>{busy ? 'Saving…' : 'Save odds'}</Button></>}>
    <div className="stack">
      <div className="form-row">
        {rows.map(([row, promo]) => <Field key={row} label={`${row === 'icon' ? 'Icon' : row === 'totw' ? 'TOTW' : promo.name} (%)`}>
          <input type="number" min="0" max="100" step="any" value={chances[row] ?? '0'} onChange={(event) => setChances({ ...chances, [row]: event.target.value })} />
        </Field>)}
      </div>
      <fieldset className="stack">
        <legend>Packs</legend>
        {missing.map((pack) => <label key={pack.id} className="event-check">
          <input type="checkbox" checked={!unticked.has(pack.id)} onChange={(event) => setUnticked((current) => {
            const next = new Set(current); if (event.target.checked) next.delete(pack.id); else next.add(pack.id); return next;
          })} />{pack.description ?? `Pack ${pack.id}`} <span className="dim">#{pack.id}</span>
        </label>)}
      </fieldset>
      <p className="dim">Specials-only and view-only packs are not listed: set their odds in the pack editor. Skipping leaves these packs incomplete; their editor asks for the missing odds.</p>
      {invalid && <p className="error-box" role="alert">Each chance must be between 0% and 100%.</p>}
      {over.length > 0 && <p className="error-box" role="alert">Odds would exceed 100% in {over.map((pack) => pack.description ?? `Pack ${pack.id}`).join(', ')}.</p>}
      {error && <p className="error-box" role="alert">{error}</p>}
    </div>
  </Dialog>;
}
