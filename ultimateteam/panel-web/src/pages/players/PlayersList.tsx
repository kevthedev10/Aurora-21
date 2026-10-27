// Players (plan 4.1): everyone who has launched or has a club. A row opens the club page.
import { useMemo, useState } from 'react';
import { usePlayers } from '../../api/players';
import type { PlayerRow } from '../../api/types';
import { href } from '../../app/router';
import { Badge, StatusDot } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { Chips } from '../../ui/Chips';
import { DataTable, type Column } from '../../ui/DataTable';
import { Field } from '../../ui/Field';
import { ago, num } from '../../ui/format';
import { searchText } from '../../ui/search';
import { EmptyState, ErrorPanel, Loading, PageHeader } from '../../ui/Layout';

type Filter = 'all' | 'club' | 'online' | 'blocked' | 'bypass';

const columns: Column<PlayerRow>[] = [
  { key: 'name', header: 'Player', sort: (row) => row.name ?? '', cell: (row) => (
    <div><b>{row.name ?? `Persona ${row.id}`}</b><div className="mono">persona {row.id}</div></div>
  ) },
  { key: 'club', header: 'Club', sort: (row) => row.club?.name ?? '', cell: (row) => (row.club ? row.club.name : <span className="dim">No club yet</span>) },
  { key: 'coins', header: 'Coins', align: 'end', sort: (row) => row.club?.coins, cell: (row) => num(row.club?.coins) },
  { key: 'points', header: 'Aurora Tokens', align: 'end', sort: (row) => row.club?.points, cell: (row) => num(row.club?.points) },
  { key: 'items', header: 'Club items', align: 'end', sort: (row) => row.club?.items, cell: (row) => num(row.club?.items) },
  { key: 'logins', header: 'Logins', align: 'end', sort: (row) => row.logins, cell: (row) => num(row.logins) },
  { key: 'seen', header: 'Last seen', sort: (row) => (row.online ? '9999' : row.lastLogin ?? ''), cell: (row) => (row.online ? <StatusDot state>Now</StatusDot> : ago(row.lastLogin)) },
  { key: 'access', header: 'Access', cell: (row) => (
    row.access?.blocked ? <Badge tone="red">Blocked</Badge> : row.access?.maintenanceBypass ? <Badge tone="coin">Bypass</Badge> : null
  ) },
];

export function PlayersList({ clubsOnly = false }: { clubsOnly?: boolean }) {
  const players = usePlayers();
  const [text, setText] = useState('');
  const [filter, setFilter] = useState<Filter>('all');
  const rows = useMemo(() => (players.data?.players ?? []).filter((row) => !clubsOnly || row.club), [players.data, clubsOnly]);
  const counts = useMemo(() => ({
    all: rows.length,
    club: rows.filter((row) => row.club).length,
    online: rows.filter((row) => row.online).length,
    blocked: rows.filter((row) => row.access?.blocked).length,
    bypass: rows.filter((row) => row.access?.maintenanceBypass).length,
  }), [rows]);
  const query = text.trim().toLowerCase();
  const shown = rows.filter((row) =>
    (filter === 'all' || (filter === 'club' && row.club) || (filter === 'online' && row.online) || (filter === 'blocked' && row.access?.blocked) || (filter === 'bypass' && row.access?.maintenanceBypass))
    && (!query || [row.name, row.id, row.discordId, row.club?.name, row.club?.abbr].some((value) => value != null && searchText(value).includes(searchText(query)))));

  return (
    <>
      <PageHeader
        title="Players / Clubs"
        description={clubsOnly ? 'Players with an existing club. Open a row to inspect and manage the club.' : 'Everyone who has launched or has a club. Each player is their verified Discord account; the persona id is derived from it.'}
        actions={<Button onClick={() => void players.refetch()} disabled={players.isFetching}>Refresh</Button>}
      />
      {players.isPending ? <Loading /> : players.isError ? <ErrorPanel what="The player list" error={players.error} onRetry={() => void players.refetch()} /> : (
        <>
          <div className="players-toolbar">
            <Field label="Search"><input type="search" placeholder="Name, persona, Discord id or club" value={text} onChange={(event) => setText(event.target.value)} /></Field>
            <Chips label="Show" value={filter} onChange={setFilter} options={[
              { value: 'all', label: 'All players', count: counts.all },
              { value: 'club', label: 'With a club', count: counts.club },
              { value: 'online', label: 'Online', count: counts.online },
              { value: 'blocked', label: 'Blocked', count: counts.blocked },
              { value: 'bypass', label: 'Bypass', count: counts.bypass },
            ]} />
          </div>
          <DataTable
            columns={columns}
            rows={shown}
            rowKey={(row) => row.id}
            onRowClick={(row) => { window.location.hash = href(clubsOnly ? 'clubs' : 'players', row.id); }}
            rowLabel={(row) => `Open ${row.name ?? `persona ${row.id}`}`}
            initialSort={{ key: 'seen', direction: 'desc' }}
            pageSize={50}
            empty={<EmptyState title={rows.length ? 'No player matches' : clubsOnly ? 'No clubs yet' : 'No players yet'} text={rows.length ? 'Change the search or the filter.' : clubsOnly ? 'Clubs appear here once created in game.' : 'A player appears here after their first sign-in.'} />}
          />
        </>
      )}
    </>
  );
}
