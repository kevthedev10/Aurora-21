import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import type { DashboardReply } from '../../api/types';
import { href } from '../../app/router';
import { StatusDot } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { DataTable } from '../../ui/DataTable';
import { ago, num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section, Stat, Stats } from '../../ui/Layout';

const value = (item: string | number | null | undefined) => item == null || item === '' ? '–' : String(item);
const playerLink = (id?: number) => id == null ? href('players') : href('players', id);

export function DashboardPage() {
  const dashboard = useQuery({ queryKey: ['dashboard'], queryFn: () => api<DashboardReply>('dashboard'), refetchInterval: 10_000 });
  const data = dashboard.data;
  const access = data?.services?.blazeAccess;
  return <>
    <PageHeader title="Dashboard" description={data?.services?.host ? `Aurora 21 at ${data.services.host}` : 'What the server is doing now.'}
      actions={<Button onClick={() => void dashboard.refetch()} disabled={dashboard.isFetching}>Refresh</Button>} />
    {dashboard.isPending ? <Loading /> : dashboard.isError ? <ErrorPanel what="The dashboard" error={dashboard.error} onRetry={() => void dashboard.refetch()} /> : <div className="dashboard-content">
      {access?.maintenance && <p className="notice">Maintenance is on. {num(access.bypassCount)} accounts or roles can bypass it. <a href={href('access')}>Manage access</a></p>}
      {data?.debug?.active && <p className="notice">Test mode is active.</p>}
      {data?.debug?.unknown && !data.debug.active && <p className="notice">Test switch state is unknown.</p>}
      {Boolean(data?.identity?.collisions?.length) && <p className="notice">{data?.identity?.collisions?.length} identity collision(s) reported. Check the server logs.</p>}
      <Stats>
        <Stat label="Online now" value={num(data?.online?.count)} />
        <Stat label="Players" value={num(data?.totals?.players)} hint={`${num(data?.totals?.clubs)} clubs`} />
        <Stat label="Today" value={num(data?.totals?.players_today)} hint={`${num(data?.totals?.logins_today)} logins`} />
        <Stat label="This week" value={num(data?.totals?.players_week)} hint="Distinct players" />
        <Stat label="Coins held" value={num(data?.totals?.coins)} hint={`${num(data?.totals?.points)} Aurora Tokens · ${num(data?.totals?.items)} club items`} />
        {data?.drafts && <Stat label="Open drafts" value={data.drafts.current ? num(data.drafts.count) : 'Unknown'} hint={data.drafts.prizePending ? `${num(data.drafts.prizePending)} prizes pending` : undefined} />}
      </Stats>
      <div className="two-columns">
        <Section title="Services">
          <dl className="facts">
            {(data?.services?.list ?? []).map((service) => <div key={service.key ?? service.label}><dt>{service.label ?? service.key} {service.port && `:${service.port}`}</dt><dd><StatusDot state={service.up}>{service.up ? 'Up' : 'Down'}</StatusDot></dd></div>)}
            {data?.services?.blazeAlive !== undefined && <div><dt>Blaze heartbeat</dt><dd>{data.services.blazeAlive ? 'Alive' : 'Stopped'}</dd></div>}
            <div><dt>Player cap</dt><dd>{access?.maxPlayers == null ? '–' : access.maxPlayers === 0 ? 'Unlimited' : num(access.maxPlayers)}</dd></div>
            <div><dt>Blocked accounts</dt><dd>{num(access?.blockedCount)}</dd></div>
            <div><dt>Database</dt><dd>{value(data?.services?.database)}</dd></div>
          </dl>
        </Section>
        <Section title="Connected now">
          <DataTable rows={data?.online?.players ?? []} pageSize={10} rowKey={(row) => row.id ?? row.name ?? ''} columns={[
            { key: 'player', header: 'Player', cell: (row) => <a href={playerLink(row.id)}>{value(row.name)}</a> },
            { key: 'club', header: 'Club', cell: (row) => value(row.club) },
            { key: 'from', header: 'From', cell: (row) => <span className="mono">{value(row.remote)}</span> },
            { key: 'since', header: 'Since', cell: (row) => ago(row.since) },
          ]} empty={<EmptyState title="Nobody is connected" />} />
        </Section>
      </div>
      <Section title="Recent logins" text="Latest 20 sign-ins." actions={<a href={href('players')}>All players</a>}>
        <DataTable rows={data?.recent?.slice(0, 20) ?? []} rowKey={(row) => `${row.id}-${row.at}`} pageSize={10} columns={[
          { key: 'player', header: 'Player', cell: (row) => <a href={playerLink(row.id)}>{value(row.name)}</a> },
          { key: 'club', header: 'Club', cell: (row) => value(row.club) },
          { key: 'from', header: 'From', cell: (row) => <span className="mono">{value(row.remote)}</span> },
          { key: 'at', header: 'Signed in', cell: (row) => ago(row.at) },
          { key: 'session', header: 'Session', cell: (row) => row.ended ? `Ended ${ago(row.ended)}` : 'Open' },
        ]} empty={<EmptyState title="No logins recorded" />} />
      </Section>
      <Section title="Clubs" text="All clubs, 10 per page." actions={<a href={href('players')}>Browse players</a>}>
        <DataTable rows={data?.clubs ?? []} rowKey={(row) => row.persona_id ?? row.club_name ?? ''} pageSize={10} columns={[
          { key: 'club', header: 'Club', cell: (row) => <a href={playerLink(row.persona_id)}>{value(row.club_name)}</a> },
          { key: 'coins', header: 'Coins', align: 'end', cell: (row) => num(row.coins) },
          { key: 'points', header: 'Aurora Tokens', align: 'end', cell: (row) => num(row.points) },
          { key: 'items', header: 'Club items', align: 'end', cell: (row) => num(row.items) },
          { key: 'new', header: 'New items', align: 'end', cell: (row) => num(row.new_items) },
          { key: 'saved', header: 'Created', cell: (row) => ago(row.updated_at) },
        ]} empty={<EmptyState title="No clubs yet" />} />
      </Section>
      <div className="two-columns">
        <Section title="Last 14 days"><DataTable rows={data?.days?.slice(0, 14) ?? []} rowKey={(row) => row.day ?? ''} columns={[
          { key: 'day', header: 'Day', cell: (row) => value(row.day) },
          { key: 'players', header: 'Players', align: 'end', cell: (row) => num(row.players) },
          { key: 'logins', header: 'Logins', align: 'end', cell: (row) => num(row.logins) },
        ]} empty={<EmptyState title="No activity yet" />} /></Section>
        {(data?.services?.supervised || Boolean(data?.supervisor?.length)) && <Section title="Supervisor" text="Process monitor output.">
          {Boolean(data?.supervisor?.length) && <pre className="log-tail">{data?.supervisor?.slice(-50).join('\n')}</pre>}
        </Section>}
      </div>
      {Boolean(data?.snapshots?.length) && <Section title="Runtime snapshots"><DataTable rows={data?.snapshots ?? []} pageSize={10} rowKey={(row) => row.key ?? ''} columns={[
        { key: 'key', header: 'Key', cell: (row) => value(row.key) },
        { key: 'process', header: 'Writer', cell: (row) => value(row.process) },
        { key: 'status', header: 'Status', cell: (row) => value(row.status) },
        { key: 'at', header: 'Written', cell: (row) => ago(row.updatedAt) },
      ]} /></Section>}
    </div>}
  </>;
}
