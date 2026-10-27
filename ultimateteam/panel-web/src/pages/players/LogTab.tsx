// Log (plan 4.10): this club's panel edits, newest first, in words.
import { describeEdit, type PanelEdit, type PlayerDetail } from '../../api/players';
import { Badge } from '../../ui/Badge';
import { DataTable, type Column } from '../../ui/DataTable';
import { ago } from '../../ui/format';
import { EmptyState, Section } from '../../ui/Layout';

export function LogTab({ detail }: { detail: PlayerDetail }) {
  const packs = new Map((detail.grantablePacks ?? []).map((pack) => [pack.id, pack.name ?? `pack ${pack.id}`]));
  const columns: Column<PanelEdit>[] = [
    { key: 'when', header: 'When', cell: (edit) => <span title={edit.created_at ?? ''}>{ago(edit.created_at)}</span> },
    { key: 'change', header: 'Change', cell: (edit) => describeEdit(edit.patch, (id) => packs.get(id) ?? `pack ${id}`) },
    { key: 'by', header: 'From', cell: (edit) => <span className="mono">{edit.requested_by ?? '–'}</span> },
    { key: 'result', header: 'Result', cell: (edit) => (edit.result === 'ok' ? <Badge tone="pitch">Applied</Badge> : <Badge tone="red">{edit.result ?? 'Unknown'}</Badge>) },
  ];
  return (
    <Section title="Panel edits" text="Every change made to this club from the panel, newest first (the last 50).">
      <DataTable columns={columns} rows={detail.edits ?? []} rowKey={(edit) => edit.id} pageSize={20}
        empty={<EmptyState title="No panel edits yet" text="Balance changes, gifts, grants and access changes show here." />} />
    </Section>
  );
}
