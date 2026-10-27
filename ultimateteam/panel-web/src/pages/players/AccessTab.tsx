// Access & sessions (plan 4.5): block, maintenance bypass and a note for this account, and its sign-ins.
import { useState } from 'react';
import { useClubEdit, type PlayerDetail, type SessionRow } from '../../api/players';
import { StatusDot } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { DataTable, type Column } from '../../ui/DataTable';
import { ConfirmDialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { ago } from '../../ui/format';
import { EmptyState, Section } from '../../ui/Layout';
import { SwitchRow } from '../../ui/Switch';
import { useToast } from '../../ui/Toast';

export function AccessTab({ detail }: { detail: PlayerDetail }) {
  return (
    <div className="two-columns club-section-grid">
      {detail.access && detail.discordId ? (
        <AccessForm key={JSON.stringify(detail.access)} detail={detail} />
      ) : (
        <Section title="Access"><EmptyState title="No account to set" text="This persona belongs to no Discord account, so it has no access settings." /></Section>
      )}
      <Sessions sessions={detail.sessions ?? []} />
    </div>
  );
}

function AccessForm({ detail }: { detail: PlayerDetail }) {
  const toast = useToast();
  const edit = useClubEdit(detail.id);
  const access = detail.access ?? {};
  const [blocked, setBlocked] = useState(access.blocked === true);
  const [bypass, setBypass] = useState(access.maintenanceBypass === true);
  const [note, setNote] = useState(access.note ?? '');
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dirty = blocked !== (access.blocked === true) || bypass !== (access.maintenanceBypass === true) || note !== (access.note ?? '');
  const name = detail.name ?? 'this player';
  const send = () => new Promise<void>((resolve, reject) => {
    setError(null);
    edit.mutate({ op: 'access', blocked, maintenanceBypass: bypass, note }, {
      onSuccess: () => { toast(blocked && !access.blocked ? `${name} is blocked.` : 'Access saved.'); resolve(); },
      onError: (caught) => { setError(caught.message); reject(caught); },
    });
  });
  // Blocking ends the player's session at once, so it is confirmed; everything else saves directly.
  const save = () => (blocked && !access.blocked ? setConfirming(true) : void send().catch(() => undefined));

  return (
    <Section
      title="Access"
      text={access.playRolesRequired
        ? 'Playing needs a play role in the Aurora Discord server (Access & maintenance). A block overrides every role.'
        : 'No play roles are set, so every verified account may play. A block overrides that.'}
    >
      <SwitchRow label="Blocked" text="Never allowed to launch. Blocking ends their current session." checked={blocked} onChange={setBlocked} danger />
      <SwitchRow label="Bypass maintenance" text="May launch while maintenance is on, whatever their roles." checked={bypass} onChange={setBypass} />
      <div className="stack mt">
        <Field label="Note" hint="Why, for the other operators. Shown in the access history.">
          <input value={note} maxLength={300} onChange={(event) => setNote(event.target.value)} />
        </Field>
        {error && <p className="error" role="alert">{error}</p>}
        <div className="row-actions">
          <Button variant={blocked && !access.blocked ? 'danger' : 'primary'} onClick={save} disabled={!dirty || edit.isPending}>
            {blocked && !access.blocked ? 'Block player' : 'Save access'}
          </Button>
          {access.lastLaunchAt && <span className="dim">Last launch {ago(access.lastLaunchAt)}</span>}
        </div>
      </div>
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={`Block ${name}?`}
        text="They cannot launch again, and their current session ends now."
        confirmLabel="Block player"
        variant="danger"
        onConfirm={send}
      />
    </Section>
  );
}

const minutes = (from: string, to: string) => {
  const span = Math.max(0, Date.parse(to) - Date.parse(from)) / 60_000;
  return span < 60 ? `${Math.round(span)} min` : `${Math.floor(span / 60)} h ${Math.round(span % 60)} min`;
};

const sessionColumns: Column<SessionRow & { index: number }>[] = [
  { key: 'when', header: 'Signed in', cell: (row) => ago(row.connected_at) },
  { key: 'from', header: 'From', cell: (row) => <span className="mono">{row.remote ?? '–'}</span> },
  { key: 'length', header: 'Session', cell: (row) => (row.disconnected_at && row.connected_at ? minutes(row.connected_at, row.disconnected_at) : <StatusDot state>Open</StatusDot>) },
];

function Sessions({ sessions }: { sessions: SessionRow[] }) {
  return (
    <Section title="Sessions" text="The last 50 sign-ins.">
      <DataTable
        columns={sessionColumns}
        rows={sessions.map((session, index) => ({ ...session, index }))}
        rowKey={(row) => row.index}
        pageSize={10}
        empty={<EmptyState title="No sign-ins recorded" />}
      />
    </Section>
  );
}
