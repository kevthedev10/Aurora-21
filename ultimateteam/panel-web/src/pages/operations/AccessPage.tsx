import { useEffect, useState, type FormEvent } from 'react';
import { useIsMutating, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useContentBusy, useReloadContent } from '../../api/content';
import { Button } from '../../ui/Button';
import { ConfirmDialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { ago, num } from '../../ui/format';
import { DataTable } from '../../ui/DataTable';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section, Stat, Stats } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';
import { Reveal } from '../../ui/Reveal';
import { Badge } from '../../ui/Badge';
import './access.css';
import knownRoles from './accessRoles.json';

interface AccessState {
  onlineGameSpeed?: { value: number } | null;
  blazeAccess?: {
    maintenance?: boolean; maintenanceMessage?: string; maxPlayers?: number; connected?: number;
    newMatches?: boolean;
    guildId?: string; playRoles?: string; bypassRoles?: string; bypassNames?: string; blockedIps?: string;
    bypassCount?: number; blockedCount?: number; launchIdentity?: boolean;
    discord?: { configured?: boolean; tokenSet?: boolean; cached?: boolean };
  };
  backups?: { error?: string; dir?: string; keep?: number; inProgress?: boolean; list?: { name: string; bytes?: number; createdAt?: string }[] };
  services?: { key?: string; label?: string; cost?: string; pids?: number[] }[];
  supervised?: boolean;
  restart?: { available?: boolean; pending?: boolean; service?: string; reason?: string | null };
}
interface ContentState { revision?: number; loadedAt?: string; catalogSha256?: string; draftRunsInProgress?: number; counts?: Record<string, number>; warnings?: string[]; notes?: string[] }
interface RoleReply { roles?: { id: string; name: string }[] }
interface CheckReply { status?: string; problem?: string; username?: string; roles?: string[]; canPlay?: boolean; bypass?: boolean; blocked?: boolean }

const ids = (raw?: string) => (raw ?? '').split(/[\s,]+/).filter(Boolean);
const validIds = (raw: string) => ids(raw).every((id) => /^\d{5,32}$/.test(id));
const bytes = (size?: number) => size == null ? '–' : size < 1024 ? `${size} B` : size < 1024 * 1024 ? `${(size / 1024).toFixed(1)} KB` : `${(size / 1024 / 1024).toFixed(1)} MB`;
const maintenanceActionKey = ['maintenance-action'];
const matchState = (access?: AccessState['blazeAccess']) => access?.newMatches === undefined ? '–' : access.newMatches ? 'Running' : 'Paused';
type Access = NonNullable<AccessState['blazeAccess']>;
const accessForm = (access: Access) => ({ maintenance: access.maintenance ?? false, maintenanceMessage: access.maintenanceMessage ?? '', maxPlayers: String(access.maxPlayers ?? 0), guildId: access.guildId ?? '', playRoles: access.playRoles ?? '', bypassRoles: access.bypassRoles ?? '', bypassNames: access.bypassNames ?? '', blockedIps: access.blockedIps ?? '' });
type AccessValues = ReturnType<typeof accessForm>;
const normalize = (form: AccessValues): AccessValues => ({ ...form, maintenanceMessage: form.maintenanceMessage.trim(), guildId: form.guildId.trim(), maxPlayers: String(Number(form.maxPlayers)), ...Object.fromEntries(['playRoles', 'bypassRoles', 'bypassNames', 'blockedIps'].map(key => [key, [...new Set(ids(form[key as keyof AccessValues] as string))].sort().join(', ')])) });
const fingerprint = (form: AccessValues) => JSON.stringify(normalize(form));

export function AccessPage() {
  const state = useQuery({ queryKey: ['maintenance'], queryFn: () => api<AccessState>('maintenance'), refetchInterval: 10_000 });
  const content = useQuery({ queryKey: ['content'], queryFn: () => api<ContentState>('content') });
  const client = useQueryClient();
  const toast = useToast();
  const maintenanceBusy = useIsMutating({ mutationKey: maintenanceActionKey }) > 0;
  const contentBusy = useContentBusy();
  const busy = maintenanceBusy || contentBusy || state.isError;
  const [backupConfirm, setBackupConfirm] = useState(false);
  const [reloadConfirm, setReloadConfirm] = useState(false);
  const [restartConfirm, setRestartConfirm] = useState(false);
  const backup = useMutation({ mutationKey: maintenanceActionKey, mutationFn: () => api('maintenance/backup', { method: 'POST' }), onSuccess: () => { toast('Backup requested. It should appear within about a minute.'); void client.invalidateQueries({ queryKey: ['maintenance'] }); } });
  const reload = useReloadContent();
  const restart = useMutation({ mutationKey: maintenanceActionKey, mutationFn: () => api('maintenance/restart', { method: 'POST', body: { service: 'server', confirm: true } }), onSuccess: () => { toast('Server restart queued. Connected players will be disconnected.'); void client.invalidateQueries({ queryKey: ['maintenance'] }); } });
  return <div className="access-page">
    <PageHeader title="Access & maintenance" description="Who may launch, database backups, game content, and server processes."
      actions={<Button onClick={() => { void state.refetch(); void content.refetch(); }} disabled={state.isFetching || content.isFetching}>Refresh</Button>} />
    {state.isError && <ErrorPanel what="Maintenance" error={state.error} onRetry={() => void state.refetch()} />}
    {state.isPending ? <Loading /> : !state.data ? null : <>
      <Stats>
        <Stat label="Maintenance" value={state.data.blazeAccess?.maintenance ? 'On' : 'Off'} />
        <Stat label="New matches" value={matchState(state.data.blazeAccess)} />
        <Stat label="Connected" value={num(state.data.blazeAccess?.connected)} />
        <Stat label="Blocked accounts" value={num(state.data.blazeAccess?.blockedCount)} />
        <Stat label="Bypass accounts and roles" value={num(state.data.blazeAccess?.bypassCount)} />
      </Stats>
      <MatchPause access={state.data.blazeAccess ?? {}} unavailable={busy} />
      {state.data.onlineGameSpeed && <GameSpeed value={state.data.onlineGameSpeed.value} unavailable={busy} />}
      <AccessForm access={state.data.blazeAccess ?? {}} unavailable={state.isError} />
      <div className="two-columns">
        <Section title="Database backups" text={state.data.backups?.error ?? 'Backups are made by the backup service.'}
          actions={<Button onClick={() => setBackupConfirm(true)} disabled={busy || Boolean(state.data.backups?.error) || state.data.backups?.inProgress}>Back up now</Button>}>
          {state.data.backups?.inProgress && <p className="notice">A backup has already been requested.</p>}
          {state.data.backups?.dir && <p className="dim">Directory: <span className="mono">{state.data.backups.dir}</span> · keep {num(state.data.backups.keep)} daily dumps</p>}
          <DataTable rows={state.data.backups?.list ?? []} pageSize={10} rowKey={(row) => row.name} columns={[
            { key: 'name', header: 'File', cell: (row) => <span className="mono">{row.name}</span> },
            { key: 'size', header: 'Size', cell: (row) => bytes(row.bytes) },
            { key: 'time', header: 'Created', cell: (row) => ago(row.createdAt) },
          ]} empty={<EmptyState title="No dumps listed" />} />
        </Section>
        <Section title="Game content" actions={<Button onClick={() => setReloadConfirm(true)} disabled={busy || content.isPending || content.isError}>Reload content</Button>}>
          {content.isPending ? <Loading lines={2} /> : content.isError ? <ErrorPanel what="Content status" error={content.error} onRetry={() => void content.refetch()} /> : <>
            <dl className="facts"><div><dt>Revision</dt><dd>{num(content.data.revision)}</dd></div><div><dt>Loaded</dt><dd>{ago(content.data.loadedAt)}</dd></div><div><dt>Draft runs in progress</dt><dd>{num(content.data.draftRunsInProgress)}</dd></div></dl>
            <p className="mono">Catalogue: {content.data.catalogSha256 ?? 'not reported'}</p>
            {content.data.counts && <dl className="facts">{Object.entries(content.data.counts).map(([key, count]) => <div key={key}><dt>{key}</dt><dd>{num(count)}</dd></div>)}</dl>}
            {content.data.warnings?.map((warning) => <p className="notice" key={warning}>{warning}</p>)}
            {content.data.notes?.map((note) => <p className="dim" key={note}>{note}</p>)}
          </>}
        </Section>
      </div>
      <Section title="Restart the game server" text="The VPS worker restarts the server container with Docker Compose. Connected players will be dropped."
        actions={<Button size="sm" variant="danger" onClick={() => setRestartConfirm(true)} disabled={busy || !state.data.restart?.available || Boolean(state.data.restart?.pending)}>Restart server</Button>}>
        {state.data.restart?.pending && <p className="notice">A restart is already pending.</p>}
        {!state.data.restart?.available && <p className="dim">{state.data.restart?.reason ?? 'The restart worker is not available.'}</p>}
        <DataTable rows={state.data.services ?? []} rowKey={(row) => row.key ?? row.label ?? ''} columns={[
          { key: 'name', header: 'Service', cell: (row) => row.label ?? row.key ?? '–' },
          { key: 'process', header: 'Process IDs', cell: (row) => row.pids?.length ? row.pids.join(', ') : 'Not reported' },
        ]} empty={<p className="dim">No supervised services are reported.</p>} />
      </Section>
      <ConfirmDialog open={backupConfirm && (!busy || backup.isPending) && !state.data.backups?.error && (!state.data.backups?.inProgress || backup.isPending)} onOpenChange={setBackupConfirm} title="Request a database backup?" text="The backup service will create and verify a dump. It may take about a minute to appear." confirmLabel="Request backup" onConfirm={() => backup.mutateAsync()} />
      <ConfirmDialog open={reloadConfirm && (!busy || reload.isPending)} onOpenChange={setReloadConfirm} title="Reload game content?" text="The next request for every club will use the new revision. Draft runs in progress may see updated content on later screens. Pending edits stay staged; validate them again after reloading." confirmLabel="Reload content" onConfirm={() => reload.mutateAsync().then((reply) => toast(`Content revision ${reply.revision ?? 'unknown'} loaded.`))} />
      <ConfirmDialog open={restartConfirm && (!busy || restart.isPending) && state.data.restart?.available === true && (!state.data.restart?.pending || restart.isPending)} onOpenChange={setRestartConfirm} title="Restart the server container?" text="All connected players will be disconnected. The VPS worker runs docker compose restart server after this request returns." confirmLabel="Restart server" variant="danger" onConfirm={() => restart.mutateAsync()} />
    </>}
  </div>;
}

const pausedText = 'Online searches keep searching without being paired, and Squad Battles and single-player Draft refuse to start. Matches already being played finish and count as usual. Connected players stay connected.';

/** Pauses or resumes every new match at once; it saves on its own, apart from the access form. */
function MatchPause({ access, unavailable }: { access: Access; unavailable: boolean }) {
  const client = useQueryClient();
  const toast = useToast();
  const [confirm, setConfirm] = useState(false);
  const save = useMutation({ mutationKey: maintenanceActionKey, mutationFn: (open: boolean) =>
    api<{ changed?: string[] }>('maintenance/blaze-access', { method: 'POST', body: { newMatches: open } }),
    onSuccess: (reply) => { toast(reply.changed?.length ? `Saved: ${reply.changed.join(', ')}` : 'No change.'); void client.invalidateQueries({ queryKey: ['maintenance'] }); } });
  const open = access.newMatches !== false;
  return <Section title="New matches" text="Pause matchmaking before maintenance or a deploy.">
    <div className="match-switch">
      <span><strong>All matches</strong><small>{open ? 'Division Rivals, Aurora Champions, Draft (online and single-player), online friendlies and Squad Battles. Offline friendlies are not affected.' : pausedText}</small></span>
      <Badge tone={open ? 'pitch' : 'staged'}>{open ? 'Running' : 'Paused'}</Badge>
      <Button size="sm" variant={open ? 'danger' : 'primary'} disabled={unavailable || save.isPending || access.newMatches === undefined}
        onClick={() => open ? setConfirm(true) : save.mutate(true)}>{open ? 'Pause all matches' : 'Resume matches'}</Button>
    </div>
    {save.isError && <p className="error" role="alert">{save.error.message}</p>}
    <ConfirmDialog open={confirm} onOpenChange={setConfirm} title="Pause all new matches?" text={pausedText}
      confirmLabel="Pause all matches" variant="danger" onConfirm={() => save.mutateAsync(false)} />
  </Section>;
}

function GameSpeed({ value, unavailable }: { value: number; unavailable: boolean }) {
  const client = useQueryClient();
  const toast = useToast();
  const save = useMutation({
    mutationKey: maintenanceActionKey,
    mutationFn: (next: number) => api<{ value: number }>('maintenance/game-speed', { method: 'POST', body: { value: next } }),
    onSuccess: (result) => {
      client.setQueryData<AccessState>(['maintenance'], current => current ? { ...current, onlineGameSpeed: result } : current);
      void client.invalidateQueries({ queryKey: ['maintenance'] });
      toast('Game speed saved for the next online match.');
    },
  });
  return <Section title="Online game speed" text="Applies to the next online match for both players. A server restart restores the configured speed.">
    <Field label="Game speed"><select value={value} disabled={unavailable || save.isPending} onChange={event => save.mutate(Number(event.target.value))}>
      <option value={0}>Slow</option><option value={1}>Normal</option><option value={2}>Fast</option>
    </select></Field>
    {save.error && <p className="error" role="alert">{save.error.message}</p>}
  </Section>;
}

function AccessForm({ access, unavailable }: { access: Access; unavailable: boolean }) {
  const client = useQueryClient();
  const toast = useToast();
  const [form, setForm] = useState(() => accessForm(access));
  const [baseline, setBaseline] = useState(() => accessForm(access));
  const liveKey = fingerprint(accessForm(access));
  const baselineKey = fingerprint(baseline);
  const dirty = fingerprint(form) !== baselineKey;
  const stale = liveKey !== baselineKey;
  const maintenanceBusy = useIsMutating({ mutationKey: maintenanceActionKey }) > 0;
  const contentBusy = useContentBusy();
  const [roleSearch, setRoleSearch] = useState('');
  const [help, setHelp] = useState(false);
  const [extraRole, setExtraRole] = useState('');
  const [extraTarget, setExtraTarget] = useState<'playRoles' | 'bypassRoles'>('playRoles');
  const [confirm, setConfirm] = useState(false);
  const [checkId, setCheckId] = useState('');
  const [checked, setChecked] = useState<{ id: string; baseline: string; reply: CheckReply } | null>(null);
  const roles = useQuery({ queryKey: ['discord-roles', form.guildId.trim()], queryFn: () => api<RoleReply>('maintenance/discord/roles'), enabled: Boolean(access.discord?.tokenSet && form.guildId.trim() && form.guildId.trim() === access.guildId), retry: false });
  const save = useMutation({ mutationKey: maintenanceActionKey, mutationFn: async (values: AccessValues) => {
    const current = await api<AccessState>('maintenance');
    client.setQueryData(['maintenance'], current);
    if (fingerprint(accessForm(current.blazeAccess ?? {})) !== baselineKey) throw new Error('Access settings changed while you were editing. Reset the form to review the current settings before saving.');
    return api<{ changed?: string[] }>('maintenance/blaze-access', { method: 'POST', body: values });
  }, onSuccess: (reply, values) => { setBaseline(values); setForm(values); setChecked(null); toast(reply.changed?.length ? `Saved: ${reply.changed.join(', ')}` : 'No access changes.'); void client.invalidateQueries({ queryKey: ['maintenance'] }); void client.invalidateQueries({ queryKey: ['dashboard'] }); } });
  const check = useMutation({ mutationFn: ({ id }: { id: string; baseline: string }) => api<CheckReply>('maintenance/discord/check', { method: 'POST', body: { id } }), onSuccess: (reply, request) => setChecked({ ...request, reply }) });
  // Connection counts are not part of this key. Adopt new settings only when there is no local draft.
  useEffect(() => { if (!dirty) { const next = accessForm(access); setForm(next); setBaseline(next); } }, [liveKey]);
  const checkedReply = checked?.id === checkId.trim() && checked.baseline === liveKey && !dirty && !stale ? checked.reply : null;
  const update = (key: keyof typeof form, value: string | boolean) => setForm((before) => ({ ...before, [key]: value }));
  const valid = (form.guildId.trim().length === 0 || /^\d{5,32}$/.test(form.guildId.trim())) && form.maintenanceMessage.trim().length <= 300 &&
    ['playRoles', 'bypassRoles', 'bypassNames', 'blockedIps'].every((key) => validIds(form[key as keyof typeof form] as string)) &&
    ['playRoles', 'bypassRoles'].every(key => new Set(ids(form[key as 'playRoles' | 'bypassRoles'])).size <= 50) &&
    /^\d+$/.test(form.maxPlayers) && Number(form.maxPlayers) <= 10000;
  const busy = maintenanceBusy || contentBusy || unavailable;
  const submit = (event: FormEvent) => { event.preventDefault(); if (valid && dirty && !stale && !busy) setConfirm(true); };
  const knownIds = new Set(knownRoles.flatMap(role => role.ids));
  const additionalIds = [...new Set([...(roles.data?.roles ?? []).map(role => role.id), ...ids(form.playRoles), ...ids(form.bypassRoles)])].filter(id => !knownIds.has(id) && id !== form.guildId.trim());
  const roleGroups = [...knownRoles, ...additionalIds.map(id => ({ name: roles.data?.roles?.find(role => role.id === id)?.name ?? `Role ${id}`, ids: [id] }))];
  const filteredRoles = roleGroups.filter(role => `${role.name} ${role.ids.join(' ')}`.toLowerCase().includes(roleSearch.toLowerCase()));
  const rolePicker = (field: 'playRoles' | 'bypassRoles') => <div className="role-picker" role="group" aria-label={field === 'playRoles' ? 'Play roles' : 'Bypass roles'}>
    {filteredRoles.map(role => {
      const selected = role.ids.filter(id => ids(form[field]).includes(id)).length;
      const mixed = selected > 0 && selected < role.ids.length;
      return <label key={role.ids.join(',')}><input type="checkbox" ref={node => { if (node) node.indeterminate = mixed; }} aria-checked={mixed ? 'mixed' : selected === role.ids.length} checked={selected === role.ids.length} onChange={event => update(field, [...new Set(event.target.checked ? [...ids(form[field]), ...role.ids] : ids(form[field]).filter(id => !role.ids.includes(id)))].join(', '))} /><span><strong>{role.name}</strong><small>{role.ids.join(' · ')}{mixed ? ' · Partially selected' : ''}</small></span></label>;
    })}
    {!filteredRoles.length && <p className="dim">No matching roles.</p>}
  </div>;
  return <Section title="Who may play" text="Access uses verified Discord account and role IDs. A saved account list replaces the full list; newly blocked players are disconnected at once.">
    {stale && <p className="notice" role="status">Saved access settings changed. Your draft is preserved; reset the form to review the current settings before saving.</p>}
    <form onSubmit={submit}>
      <fieldset className="access-fields" disabled={save.isPending || confirm}>
      <label className="access-toggle"><input type="checkbox" checked={form.maintenance} onChange={(event) => update('maintenance', event.target.checked)} /><span><strong>Maintenance mode</strong><small>Refuse new launches except eligible bypass accounts and roles. Connected players stay connected.</small></span><Badge tone={form.maintenance ? 'staged' : 'neutral'}>{form.maintenance ? 'On' : 'Off'}</Badge></label>
      <div className="access-main-fields">
        <Field label="Maintenance message" hint="Shown to players during maintenance; up to 300 characters."><input maxLength={300} value={form.maintenanceMessage} onChange={(event) => update('maintenanceMessage', event.target.value)} /></Field>
        <Field label="Player cap" hint="0 means unlimited; maximum 10,000"><input type="number" min="0" max="10000" step="1" value={form.maxPlayers} onChange={(event) => update('maxPlayers', event.target.value)} /></Field>
        <Field label="Discord server ID" hint="Save a server ID to load its role names."><input inputMode="numeric" value={form.guildId} onChange={(event) => update('guildId', event.target.value)} /></Field>
      </div>
      <div className="access-role-tools"><Badge tone={access.discord?.configured ? 'pitch' : 'neutral'}>{access.discord?.configured ? 'Discord configured' : 'Discord not configured'}</Badge><Button size="sm" variant="ghost" aria-expanded={help} aria-controls="discord-id-help" onClick={() => setHelp(!help)}>How to get Discord IDs</Button></div>
      <Reveal show={help} id="discord-id-help"><div className="notice"><p>Enable Discord’s User Settings → Advanced → Developer Mode. Right-click the server icon for Copy Server ID, or a user for Copy User ID. In Server Settings → Roles, right-click a role for Copy Role ID. Server ownership is not required; access to role settings depends on your permissions.</p><p>You can also select role names below after saving the server ID and configuring the server’s Discord bot.</p></div></Reveal>
      {!access.discord?.tokenSet && <p className="notice">Role names and player checks require the server’s Discord bot. Configure DISCORD_BOT_TOKEN on the server; no token is entered in this panel.</p>}
      {form.guildId.trim() !== (access.guildId ?? '') && <p className="notice">Save the new Discord server ID before loading its roles.</p>}
      {roles.isLoading && <Loading lines={2} label="Loading Discord roles" />}
      {roles.isError && <div className="notice">Role names could not be loaded: {roles.error.message}. You can still enter role IDs. <Button size="sm" onClick={() => void roles.refetch()}>Retry roles</Button></div>}
      {roles.isSuccess && !roles.data.roles?.length && <p className="notice">No role names were returned. Check the saved server ID and the bot’s access to that server. The named Aurora roles and manual IDs remain available.</p>}
      <Field label="Find a Discord role"><input type="search" placeholder="Search role names or IDs…" value={roleSearch} onChange={e => setRoleSearch(e.target.value)} /></Field>
      <div className="access-role-fields">
        <div className="stack"><Field label="Roles that may play" hint="Discord role IDs, comma or space separated"><textarea rows={2} value={form.playRoles} onChange={(event) => update('playRoles', event.target.value)} /></Field>{rolePicker('playRoles')}</div>
        <div className="stack"><Field label="Maintenance bypass roles" hint="Discord role IDs"><textarea rows={2} value={form.bypassRoles} onChange={(event) => update('bypassRoles', event.target.value)} /></Field>{rolePicker('bypassRoles')}</div>
      </div>
      <div className="access-add-role"><Field label="Extra role ID" hint="Additional server roles also appear automatically when Discord is configured."><input inputMode="numeric" value={extraRole} onChange={e => setExtraRole(e.target.value)} /></Field><Field label="Add role to"><select value={extraTarget} onChange={e => setExtraTarget(e.target.value as typeof extraTarget)}><option value="playRoles">Roles that may play</option><option value="bypassRoles">Maintenance bypass roles</option></select></Field><Button disabled={!/^\d{5,32}$/.test(extraRole.trim()) || extraRole.trim() === form.guildId.trim()} onClick={() => { update(extraTarget, [...new Set([...ids(form[extraTarget]), extraRole.trim()])].join(', ')); setExtraRole(''); setRoleSearch(''); }}>Add role</Button></div>
      <p className="dim">Founders includes both Founder and Co-Founder IDs. With no play roles, verified accounts do not need a role to play. Maintenance bypass does not override blocks or play-role requirements.</p>
      <div className="access-role-fields"><Field label="Bypass accounts" hint="Discord user IDs, comma or space separated"><textarea rows={3} value={form.bypassNames} onChange={(event) => update('bypassNames', event.target.value)} /></Field><Field label="Blocked accounts" hint="Discord user IDs; a block overrides every role"><textarea rows={3} value={form.blockedIps} onChange={(event) => update('blockedIps', event.target.value)} /></Field></div>
      {!valid && <p className="error" role="alert">Use numeric Discord IDs (5–32 digits), at most 50 roles per list, a message up to 300 characters, and a whole player cap from 0 to 10,000.</p>}
      </fieldset>
      <div className="row-actions access-save"><Button variant="primary" type="submit" disabled={!valid || !dirty || stale || busy}>Save access</Button><Button variant="ghost" disabled={save.isPending || (!dirty && !stale)} onClick={() => { const next = accessForm(access); setBaseline(next); setForm(next); setChecked(null); save.reset(); }}>Reset form</Button><span className="dim">{dirty ? 'Unsaved edits — access changes apply immediately when saved.' : 'Access settings are saved.'}</span></div>
    </form>
    <Section className="access-player-test" title="Test a player" text="Check an account against the saved play roles and block list. Maintenance and capacity can still refuse a launch; this is not a launch test.">
      {(dirty || stale) && <p className="notice">Save or reset your edits before testing the saved access rules.</p>}
      <div className="access-test-controls"><Field label="Discord user ID"><input inputMode="numeric" value={checkId} onChange={(event) => { setCheckId(event.target.value); setChecked(null); check.reset(); }} /></Field><Button onClick={() => check.mutate({ id: checkId.trim(), baseline: liveKey })} disabled={!/^\d{5,32}$/.test(checkId.trim()) || check.isPending || dirty || stale || busy || !access.discord?.configured}>Test player</Button></div>
      {check.isError && check.variables?.id === checkId.trim() && check.variables.baseline === liveKey && <p className="error" role="alert">{check.error.message}</p>}
      <Reveal show={checkedReply !== null}>{checkedReply && <><dl className="facts"><div><dt>Account</dt><dd>{checkedReply.username ?? checkId}</dd></div><div><dt>Status</dt><dd>{checkedReply.status ?? '–'}</dd></div><div><dt>Account / role access</dt><dd>{checkedReply.canPlay ? 'Allowed' : 'Refused'}</dd></div><div><dt>Maintenance bypass</dt><dd>{checkedReply.bypass ? 'Yes' : 'No'}</dd></div><div><dt>Blocked</dt><dd>{checkedReply.blocked ? 'Yes' : 'No'}</dd></div><div><dt>Roles</dt><dd>{checkedReply.roles?.join(', ') || 'None reported'}</dd></div></dl>{checkedReply.problem && <p className="notice">{checkedReply.problem}</p>}</>}</Reveal>
    </Section>
    <ConfirmDialog open={confirm} onOpenChange={setConfirm} title="Save access controls?" text={form.maintenance && !access.maintenance ? 'Maintenance will refuse new launches except bypass roles and accounts. Players already connected stay connected.' : 'These lists replace the current lists. Newly blocked accounts are disconnected immediately.'} confirmLabel="Save access" variant={form.maintenance && !access.maintenance ? 'danger' : 'primary'} onConfirm={() => save.mutateAsync(normalize(form))} />
  </Section>;
}
