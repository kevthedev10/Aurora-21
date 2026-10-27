// The pending content changes, from any page (plan step 3.3). Read-only: reviewing and publishing stay on Changes.
import type { ChangesetReply } from '../api/types';
import { StagedBadge } from '../ui/Badge';
import { LinkButton } from '../ui/Button';
import { Drawer } from '../ui/Drawer';
import { ago } from '../ui/format';
import { EmptyState } from '../ui/Layout';
import { useSbcNames } from '../api/sbc';
import { areaLabel, changeTitle, fieldChanges } from './changes';
import { href } from './router';

export function PendingDrawer({ open, onOpenChange, reply }: { open: boolean; onOpenChange: (open: boolean) => void; reply: ChangesetReply | undefined }) {
  const changes = reply?.changeset?.changes ?? [];
  const sbcName = useSbcNames(open && changes.some((change) => change.area === 'sbc_live'));
  return (
    <Drawer
      open={open}
      onOpenChange={onOpenChange}
      title="Pending changes"
      description="Staged content edits. None of them reach the game until they are published under Changes."
      footer={<LinkButton variant="primary" href={href('changes')} onClick={() => onOpenChange(false)}>Review and publish</LinkButton>}
    >
      {changes.length === 0 ? (
        <EmptyState title="Nothing is pending" text="Edits to cards, packs and rules wait here once they are staged." />
      ) : (
        <ol className="pending-list">
          {changes.map((change) => (
            <li key={change.id}>
              <div className="pending-row">
                <b>{areaLabel(change.area)}</b>
                <span className="dim">{changeTitle(change)}</span>
                <StagedBadge>{change.operation}</StagedBadge>
              </div>
              <ul className="pending-fields">
                {fieldChanges(change, 3, sbcName).map((line) => <li key={line} className="mono">{line}</li>)}
              </ul>
              {change.updatedAt && <small className="dim">Staged {ago(change.updatedAt)}</small>}
            </li>
          ))}
        </ol>
      )}
    </Drawer>
  );
}
