import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { href } from '../../app/router';
import { Badge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { DataTable, type Column } from '../../ui/DataTable';
import { Field } from '../../ui/Field';
import { ago, num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section, Stat, Stats } from '../../ui/Layout';
import { searchText } from '../../ui/search';
import { useToast } from '../../ui/Toast';
import './operations-review.css';

interface LeaversReply {
  rules?: { winFromMinute?: number; earlyLeaves?: number; windowHours?: number; fromMinute?: number };
  queueCooldown?: { earlyLeaves?: number; windowSeconds?: number; ladderSeconds?: number[]; offenceMemoryDays?: number };
  penalties?: Penalty[];
  hours?: number;
  leavers?: Leaver[];
  recent?: Leave[];
}
interface Leaver { discordId: string; name?: string; personaId?: number; leaves?: number; earlyLeaves?: number; earlyInWindow?: number; serialLeaves?: number; serialNow?: boolean; earlyOpponents?: number; modes?: string[]; lastLeftAt?: string; penalty?: 'cooldown' | 'banned' | null }
interface Penalty { discordId: string; name?: string; offence: number; banned: boolean; until?: string | null }
interface Who { discordId: string; name?: string }
interface Leave { leaver: Who; stayer: Who; mode?: string; match?: string; minute?: number; score?: string; early?: boolean; serial?: boolean; stayerOutcome?: string; at?: string }

const modeNames: Record<string, string> = { rivals: 'Rivals', weekendLeague: 'Weekend League', draftOnline: 'Online Draft' };
const modeName = (mode?: string) => modeNames[mode ?? ''] ?? mode ?? '–';
const duration = (seconds?: number) => !seconds ? '–' : seconds % 86400 === 0 ? `${seconds / 86400} day${seconds === 86400 ? '' : 's'}` : seconds % 3600 === 0 ? `${seconds / 3600} hour${seconds === 3600 ? '' : 's'}` : `${Math.round(seconds / 60)} min`;
const untilText = (iso: string) => new Date(iso).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' });
const periods = [{ hours: 24, label: 'Last 24 hours' }, { hours: 168, label: 'Last 7 days' }, { hours: 720, label: 'Last 30 days' }];

export function LeaversPage() {
  const [hours, setHours] = useState(168);
  const [search, setSearch] = useState('');
  const query = useQuery({ queryKey: ['leavers', hours], queryFn: ({ signal }) => api<LeaversReply>(`leavers?hours=${hours}&limit=500`, { signal }), retry: false });
  const rules = query.data?.rules;
  const cooldown = query.data?.queueCooldown;
  const toast = useToast();
  const lift = useMutation({
    mutationFn: (discordId: string) => api(`leavers/${encodeURIComponent(discordId)}/penalty`, { method: 'DELETE' }),
    onSuccess: () => { toast('Penalty lifted: the player can search online again, and it no longer counts toward a ban.'); void query.refetch(); },
  });
  const matches = (text: string) => !search.trim() || searchText(text).includes(searchText(search.trim()));
  const leavers = (query.data?.leavers ?? []).filter(row => matches(`${row.name} ${row.discordId} ${row.personaId}`));
  const recent = (query.data?.recent ?? []).filter(row => matches(`${row.leaver.name} ${row.leaver.discordId} ${row.stayer.name} ${row.stayer.discordId}`));
  const player = (who: Who) => <span title={who.discordId}>{who.name ?? who.discordId}</span>;
  const leaverColumns: Column<Leaver>[] = [
    { key: 'player', header: 'Player', width: 200, sort: row => row.name, cell: row => row.personaId == null ? row.name ?? row.discordId : <a href={href('players', row.personaId)} title={row.discordId}>{row.name ?? row.discordId}</a> },
    { key: 'early', header: `Early (< ${rules?.winFromMinute ?? 10}')`, width: 110, sort: row => row.earlyLeaves, cell: row => num(row.earlyLeaves) },
    { key: 'window', header: `Early, last ${rules?.windowHours ?? 24}h`, width: 130, sort: row => row.earlyInWindow, cell: row => <>{num(row.earlyInWindow)} {row.serialNow && <Badge tone="red">Serial leaver</Badge>} {row.penalty && <Badge tone="red">{row.penalty === 'banned' ? 'Banned' : 'Locked out'}</Badge>}</> },
    { key: 'opponents', header: 'Opponents', width: 100, sort: row => row.earlyOpponents, cell: row => num(row.earlyOpponents) },
    { key: 'leaves', header: 'All leaves', width: 90, sort: row => row.leaves, cell: row => num(row.leaves) },
    { key: 'serial', header: 'Wins given', width: 100, sort: row => row.serialLeaves, cell: row => num(row.serialLeaves) },
    { key: 'modes', header: 'Modes', width: 170, cell: row => (row.modes ?? []).map(modeName).join(', ') },
    { key: 'last', header: 'Last leave', width: 110, sort: row => row.lastLeftAt, cell: row => ago(row.lastLeftAt) },
  ];
  const leaveColumns: Column<Leave>[] = [
    { key: 'at', header: 'When', width: 100, sort: row => row.at, cell: row => <span title={row.at}>{ago(row.at)}</span> },
    { key: 'leaver', header: 'Left', width: 170, sort: row => row.leaver.name, cell: row => player(row.leaver) },
    { key: 'stayer', header: 'Stayed', width: 170, sort: row => row.stayer.name, cell: row => player(row.stayer) },
    { key: 'mode', header: 'Mode', width: 130, sort: row => row.mode, cell: row => modeName(row.mode) },
    { key: 'minute', header: 'Minute', width: 80, sort: row => row.minute, cell: row => `${row.minute ?? 0}'` },
    { key: 'score', header: 'Score (leaver first)', width: 140, cell: row => <span className="mono">{row.score}</span> },
    { key: 'kind', header: 'Kind', width: 140, sort: row => (row.serial ? 2 : row.early ? 1 : 0), cell: row => row.serial ? <Badge tone="red">Serial, counted</Badge> : row.early ? <Badge tone="staged">Early</Badge> : <Badge>After {rules?.winFromMinute ?? 10}'</Badge> },
    { key: 'outcome', header: 'Stayer got', width: 100, sort: row => row.stayerOutcome, cell: row => row.stayerOutcome === 'None' ? 'No result' : row.stayerOutcome },
  ];
  const all = query.data?.leavers ?? [];
  return <div className="operations-review">
    <PageHeader title="Leavers" description="Who leaves online matches and when. Leaving before the win minute gives the opponent no result; a serial leaver's early leaves count as normal leaves."
      actions={<Button onClick={() => void query.refetch()} disabled={query.isFetching}>{query.isFetching ? 'Refreshing…' : 'Refresh'}</Button>} />
    {query.isError && <ErrorPanel what="Leavers" error={query.error} onRetry={() => void query.refetch()} />}
    {query.isPending ? <Loading /> : !query.data ? null : <>
      <p className="notice">{rules?.earlyLeaves
        ? `Serial leaver: ${rules.earlyLeaves} early leaves (minute ${rules.fromMinute ?? 1} to ${(rules.winFromMinute ?? 10) - 1}) within ${rules.windowHours}h. After that, each early leave gives a stayer who is level or ahead the win.`
        : 'The serial-leaver rule is off (earlyLeaves 0): leaves are only logged.'} Rules: seasons rewardEligibility.opponentLeft.serialLeaver.</p>
      <Stats>
        <Stat label="Players who left" value={num(all.length)} />
        <Stat label="Early leaves" value={num(all.reduce((sum, row) => sum + (row.earlyLeaves ?? 0), 0))} />
        <Stat label="Serial leavers now" value={num(all.filter(row => row.serialNow).length)} />
        <Stat label="Wins given by the rule" value={num(all.reduce((sum, row) => sum + (row.serialLeaves ?? 0), 0))} />
      </Stats>
      <p className="notice">{cooldown?.earlyLeaves
        ? `Offence: ${cooldown.earlyLeaves} early leaves within ${duration(cooldown.windowSeconds)}. Penalties: ${[...(cooldown.ladderSeconds ?? []).map(duration), 'ban until lifted'].join(', then ')} (offences from the last ${cooldown.offenceMemoryDays} days count; lifted ones do not).`
        : 'Leaver penalties are off (queueCooldown.earlyLeaves 0).'} Rules: seasons rewardEligibility.opponentLeft.queueCooldown.</p>
      <Section title="Locked out of online" text="A penalised player's searches never find a match until the penalty ends; a ban lasts until lifted. Lift it if an appeal is upheld.">
        {(query.data.penalties ?? []).length === 0 ? <EmptyState title="Nobody is locked out" /> :
          <DataTable rows={query.data.penalties ?? []} pageSize={15} fixedLayout rowKey={row => row.discordId} columns={[
            { key: 'player', header: 'Player', width: 220, sort: row => row.name, cell: row => <span title={row.discordId}>{row.name ?? row.discordId}</span> },
            { key: 'offence', header: 'Offence', width: 90, sort: row => row.offence, cell: row => num(row.offence) },
            { key: 'until', header: 'Until', width: 160, sort: row => row.until ?? '9', cell: row => row.banned || !row.until ? <Badge tone="red">Banned</Badge> : <span title={row.until}>{untilText(row.until)}</span> },
            { key: 'lift', header: '', width: 120, cell: row => <Button size="sm" disabled={lift.isPending} onClick={() => lift.mutate(row.discordId)}>Lift</Button> },
          ] as Column<Penalty>[]} />}
      </Section>
      <Section title="Filter"><div className="logs-tools">
        <Field label="Period"><select value={hours} onChange={event => setHours(Number(event.target.value))}>{periods.map(period => <option key={period.hours} value={period.hours}>{period.label}</option>)}</select></Field>
        <Field label="Find a player"><input type="search" placeholder="Name, persona or Discord ID…" value={search} onChange={event => setSearch(event.target.value)} /></Field>
      </div></Section>
      <Section title="Players by early leaves">
        <DataTable key={`l:${hours}:${search}`} rows={leavers} pageSize={15} fixedLayout rowKey={row => row.discordId} columns={leaverColumns} empty={<EmptyState title={search ? 'No matching players' : 'Nobody left a match in this period'} />} />
      </Section>
      <Section title="Recent leaves" text="Leaves the relay heard the leaver play through are not logged: their own match end decides.">
        <DataTable key={`r:${hours}:${search}`} rows={recent} pageSize={20} fixedLayout rowKey={row => `${row.leaver.discordId}:${row.match}`} columns={leaveColumns} empty={<EmptyState title="No leaves in this period" />} />
      </Section>
    </>}
  </div>;
}
