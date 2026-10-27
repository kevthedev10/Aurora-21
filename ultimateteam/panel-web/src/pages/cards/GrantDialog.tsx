// Grant to a club: a player card (grantCard) or a consumable, club customisation item or manager (grantItem). One club,
// how many (when more than one copy is allowed) and whether the copies are untradeable; the server applies it at once.
import { useState } from 'react';
import { refusedUnchanged, useClubEdit, usePlayers } from '../../api/players';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { EmptyState, ErrorPanel, Loading } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';
import { Tradeability } from '../../ui/Tradeability';

export function GrantDialog({ title, itemName, description, max, forced = false, forcedReason, edit: patchFor, onClose }: {
  title: string;
  /** What the toast calls the granted item. */
  itemName: string;
  description: string;
  /** The most copies one grant may give; 1 hides the count. */
  max: number;
  /** Always untradeable, whatever is ticked. */
  forced?: boolean;
  forcedReason?: string;
  /** The club edit to send for this many copies. */
  edit: (count: number, untradeable: boolean) => Record<string, unknown>;
  onClose: () => void;
}) {
  const [untradeable, setUntradeable] = useState(false);
  const toast = useToast();
  const players = usePlayers();
  const clubs = (players.data?.players ?? []).filter((row) => row.club);
  const [persona, setPersona] = useState<number | null>(null);
  const [count, setCount] = useState('1');
  const [error, setError] = useState<string | null>(null);
  const target = persona ?? clubs[0]?.id ?? null;
  const edit = useClubEdit(target ?? 0);
  const amount = max > 1 ? Number(count) : 1;
  const valid = Number.isInteger(amount) && amount >= 1 && amount <= max;
  const name = clubs.find((row) => row.id === target)?.name ?? 'the club';
  const send = () => {
    if (target === null || !valid) return;
    setError(null);
    edit.mutate(patchFor(amount, forced || untradeable), {
      onSuccess: () => { toast(`Granted ${amount > 1 ? `${amount} × ` : ''}${itemName} to ${name}.`); onClose(); },
      onError: (caught) => { if (refusedUnchanged(caught)) setError(caught.message); else { toast(caught.message, 'error'); onClose(); } },
    });
  };
  return (
    <Dialog open onOpenChange={(open) => { if (!open && !edit.isPending) onClose(); }} title={title} description={description}
      footer={<><Button variant="ghost" onClick={onClose} disabled={edit.isPending}>Cancel</Button><Button variant="primary" onClick={send} disabled={target === null || !valid || edit.isPending || players.isError}>Grant to {name}</Button></>}>
      {players.isPending ? <Loading lines={2} /> : players.isError ? <ErrorPanel what="The clubs" error={players.error} onRetry={() => void players.refetch()} /> : !clubs.length ? <EmptyState title="No club yet" text="A club appears after its player's first Ultimate Team request." /> : (
        <fieldset disabled={edit.isPending} className="form-row card-editor-fields">
          <Field label="Club">
            <select value={target ?? ''} onChange={(event) => setPersona(Number(event.target.value))}>
              {clubs.map((row) => <option key={row.id} value={row.id}>{row.name ?? `Persona ${row.id}`}{row.online ? ' (online)' : ''}</option>)}
            </select>
          </Field>
          {max > 1 && <Field label="How many" hint={`1 to ${max}`} error={count && !valid ? `A whole number from 1 to ${max}.` : null}>
            <input type="number" min={1} max={max} value={count} onChange={(event) => setCount(event.target.value)} />
          </Field>}
        </fieldset>
      )}
      <Tradeability value={untradeable} onChange={setUntradeable} forced={forced} {...(forcedReason && { forcedReason })} disabled={edit.isPending} />
      {error && <p className="error" role="alert">{error}</p>}
    </Dialog>
  );
}
