// Deleting a store pack definition (OperatorPanel.PackDelete.cs). The server says what still depends on the pack: a
// refusal is shown here with its reasons; clubs owning unopened copies is the one reason Force delete can override.
import { useState } from 'react';
import { usePackActions, usePackDeleteCheck, type CatalogueEntry } from '../../api/packs';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { Loading } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';

export function DeletePackDialog({ pack, revision, onClose }: { pack: CatalogueEntry | null; revision: string | undefined; onClose: () => void }) {
  const toast = useToast();
  const actions = usePackActions();
  const check = usePackDeleteCheck(pack?.id ?? null);
  const [typed, setTyped] = useState('');
  const [error, setError] = useState<string | null>(null);
  const close = () => { if (!actions.remove.isPending) { setTyped(''); setError(null); onClose(); } };
  const name = pack?.description ?? 'this pack';
  const result = check.data;
  const stage = async (force: boolean) => {
    if (!pack || !result) return;
    setError(null);
    try {
      await actions.remove.mutateAsync({ id: pack.id, revision, ...(force && { force: result.unopened }) });
      toast(force ? 'Pack force deleted.' : 'Pack deleted.', 'staged');
      setTyped('');
      onClose();
    } catch (caught) { setError(caught instanceof Error ? caught.message : String(caught)); }
  };
  const copies = result?.unopened.copies ?? 0;
  const clubs = result?.unopened.clubs ?? 0;
  const copiesText = `${copies} unopened ${copies === 1 ? 'copy' : 'copies'} across ${clubs} ${clubs === 1 ? 'club' : 'clubs'}`;
  const hard = result?.blockers.filter((blocker) => !blocker.overridable) ?? [];
  const pending = actions.remove.isPending;
  return (
    <Dialog open={!!pack} onOpenChange={(open) => { if (!open) close(); }} title={`Delete ${name} #${pack?.id ?? ''}?`}
      description="Deleting removes the pack definition itself immediately. Hide only stops selling it."
      footer={<>
        <Button variant="ghost" onClick={close} disabled={pending}>{result && !result.canDelete && !result.canForceDelete ? 'Close' : 'Cancel'}</Button>
        {result?.canDelete && <Button variant="danger" onClick={() => void stage(false)} disabled={pending}>{pending ? 'Working…' : 'Delete pack'}</Button>}
        {result?.canForceDelete && (
          <Button variant="danger" onClick={() => void stage(true)} disabled={pending || typed.trim() !== String(pack?.id)}>{pending ? 'Working…' : 'Force delete'}</Button>
        )}
      </>}>
      {check.isPending && <Loading />}
      {check.isError && <p role="alert">{check.error instanceof Error ? check.error.message : 'The pack could not be checked.'}</p>}
      {result && hard.length > 0 && (
        <div role="alert">
          <p><b>This pack cannot be deleted yet.</b></p>
          <ul>{hard.map((blocker) => <li key={blocker.code}>{blocker.message}</li>)}</ul>
        </div>
      )}
      {result?.canDelete && <p>No club owns an unopened copy and no reward, rule or store lineup uses it. Its purchase counters are removed with it.</p>}
      {result?.canForceDelete && (
        <div>
          <p role="alert"><b>{copiesText} {copies === 1 ? 'is' : 'are'} still owned.</b></p>
          <p>Force delete also removes {copiesText} from the clubs that own them immediately. Coins and Aurora Tokens are not refunded.
            Items already opened from this pack stay untouched. This cannot be undone.</p>
          <Field label={`Type ${pack?.id} to confirm`}>
            <input value={typed} autoComplete="off" onChange={(event) => setTyped(event.target.value)} />
          </Field>
        </div>
      )}
      {error && <p role="alert">{error}</p>}
    </Dialog>
  );
}
