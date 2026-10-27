import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { href } from '../../app/router';
import { Badge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { DataTable, type Column } from '../../ui/DataTable';
import { ago, num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section, Stat, Stats } from '../../ui/Layout';
import { Field } from '../../ui/Field';
import { searchText } from '../../ui/search';
import './matchmaking.css';

interface MatchmakingReply {
  snapshot?: { current?: boolean; updatedAt?: string; ageMs?: number; available?: boolean; accepting?: boolean; reason?: string | null };
  counts?: { searching?: number; match?: number; online?: number; total?: number; games?: number; preMatch?: number; inGame?: number; postMatch?: number };
  splitQueue?: boolean;
  modes?: string[];
  players?: LobbyPlayer[];
}
interface LobbyPlayer { blazeId?: number; discordId?: string; name?: string; state?: string; mode?: string | null; connected?: boolean; connectedSince?: string | null; sessionId?: number | null; gameId?: number | null; gameState?: number | null; phase?: string | null; queuedForMs?: number | null }
const modeKey = (mode?: string | null) => (mode ?? '').toLowerCase();
const modeNames: Record<string, string> = { fut_rivals: 'Division Rivals', fut_draft: 'Online Draft', fut_champions: 'Aurora Champions' };
const modeName = (mode?: string | null) => modeNames[modeKey(mode)] ?? (mode?.replaceAll('_', ' ') || 'Not reported');
const phase = (player: LobbyPlayer) => player.state !== 'match' ? player.state ?? 'online' : player.phase ?? ([1,3,130].includes(player.gameState ?? -1) ? 'pre_match' : player.gameState === 131 ? 'in_match' : player.gameState === 4 ? 'post_match' : 'unknown_match');
const gameStateNames: Record<number, string> = { 1: 'Initializing', 3: 'Connection check', 130: 'Pre-game', 131: 'Playing', 4: 'Post-match' };
const gameStateName = (state?: number | null) => gameStateNames[state ?? -1] ?? 'Phase not reported';
const waiting = (ms?: number | null) => ms == null ? 'Not reported' : ms < 60_000 ? `${Math.floor(ms / 1000)}s` : `${Math.floor(ms / 60_000)}m ${Math.floor(ms / 1000) % 60}s`;

function SnapshotStatus({ snapshot, paused, failed }: { snapshot: MatchmakingReply['snapshot']; paused: boolean; failed: boolean }) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 1000); return () => window.clearInterval(timer); }, []);
  return <div className="matchmaking-snapshot"><Badge tone={failed || snapshot?.current === false ? 'red' : paused ? 'staged' : 'pitch'}>{failed ? 'Refresh failed' : snapshot?.current === false ? 'Unavailable' : paused ? 'Auto-refresh paused' : 'Updating every 3 seconds'}</Badge><span className="dim">Last snapshot: {ago(snapshot?.updatedAt, now)}</span>{snapshot?.accepting === false && <Badge tone="staged">New searches are stopped</Badge>}</div>;
}

export function MatchmakingPage() {
  const [paused, setPaused] = useState(false);
  const [search, setSearch] = useState('');
  const [mode, setMode] = useState('');
  const query = useQuery({ queryKey: ['matchmaking'], queryFn: ({ signal }) => api<MatchmakingReply>('matchmaking', { signal }), refetchInterval: paused ? false : 3000, retry: false });
  const players = query.data?.players ?? [];
  const modes = [...new Set([...players.map(player => modeKey(player.mode)), ...(query.data?.modes ?? []).map(modeKey), mode].filter(Boolean))].sort((a,b) => modeName(a).localeCompare(modeName(b)));
  const visible = players.filter(player => (!mode || modeKey(player.mode) === mode) && (!search.trim() || searchText(`${player.name} ${player.blazeId} ${player.discordId}`).includes(searchText(search.trim()))));
  const group = (state: string) => visible.filter(player => phase(player) === state);
  const columns: Column<LobbyPlayer>[] = [
    { key: 'player', header: 'Player', width: 210, sort: row => row.name, cell: row => <div className="matchmaking-player">{row.blazeId == null ? row.name ?? '–' : <a href={href('players', row.blazeId)}>{row.name ?? `Persona ${row.blazeId}`}</a>}{row.connected === false && <small>No active Blaze session</small>}</div> },
    { key: 'mode', header: 'Mode', width: 140, sort: row => modeName(row.mode), cell: row => modeName(row.mode) },
  ];
  const matchColumns: Column<LobbyPlayer>[] = [...columns, { key: 'phase', header: 'Phase', width: 145, sort: row => row.gameState, cell: row => <Badge tone={phase(row) === 'in_match' ? 'pitch' : 'staged'}>{gameStateName(row.gameState)}</Badge> }, { key: 'game', header: 'Game ID', width: 110, sort: row => row.gameId, cell: row => <span className="mono">{num(row.gameId)}</span> }];
  const table = (state: string, cols: Column<LobbyPlayer>[], empty: string) => <DataTable key={`${state}:${mode}:${search}`} rows={group(state)} pageSize={10} fixedLayout rowKey={row => row.blazeId ?? row.discordId ?? row.name ?? ''} columns={cols} empty={<EmptyState title={search || mode ? 'No matching players' : empty} />} />;
  const count = (state: string) => num(query.data?.snapshot?.available === false ? undefined : players.filter(player => phase(player) === state).length);
  const playerCount = (state: string) => { const count = group(state).length; return <Badge>{num(count)} {count === 1 ? 'player' : 'players'}</Badge>; };
  return <div className="matchmaking-page">
    <PageHeader title="Matchmaking" description="Follow searches, pre-match setup and games in progress. This page shows the server’s lobby state."
      actions={<><Button onClick={() => { setPaused(!paused); if (paused) void query.refetch(); }}>{paused ? 'Resume auto-refresh' : 'Pause auto-refresh'}</Button><Button onClick={() => void query.refetch()} disabled={query.isFetching}>{query.isFetching ? 'Refreshing…' : 'Refresh'}</Button></>} />
    {query.isError && <ErrorPanel what="Matchmaking" error={query.error} onRetry={() => void query.refetch()} />}
    {query.isPending ? <Loading /> : !query.data ? null : <>
      <SnapshotStatus snapshot={query.data.snapshot} paused={paused} failed={query.isError} />
      {(query.isError || query.data.snapshot?.current === false) && <p className="notice">{query.data.snapshot?.reason ?? 'The displayed snapshot is not current. Refresh before relying on these counts.'}</p>}
      <Stats><Stat label="Searching" value={count('searching')} /><Stat label="Pre-match" value={count('pre_match')} hint="Paired; setting up the game" /><Stat label="In-match" value={count('in_match')} hint="Server reports IN_GAME" /><Stat label="Paired games" value={num(query.data.counts?.games)} /><Stat label="Online players" value={num(query.data.counts?.online)} /></Stats>
      <Section title="Filter players" className="matchmaking-filters"><div className="matchmaking-tools"><Field label="Find a player"><input type="search" placeholder="Player name, persona or Discord ID…" value={search} onChange={event => setSearch(event.target.value)} /></Field><Field label="Mode"><select value={mode} onChange={event => setMode(event.target.value)}><option value="">All modes</option>{modes.map(value => <option key={value} value={value}>{modeName(value)}</option>)}</select></Field><Button variant="ghost" disabled={!search && !mode} onClick={() => { setSearch(''); setMode(''); }}>Clear filters</Button></div><p className="dim">Showing {num(visible.length)} of {num(players.length)} players. Summary counts include every player.</p></Section>
      {query.data.splitQueue && <p className="notice">Searches are separated by mode: {(query.data.modes ?? []).map(modeName).join(', ')}. Players in different modes are not paired together.</p>}
      <div className="matchmaking-queues">
        <Section title="Searching" text="Players waiting for an opponent in the same mode." actions={playerCount('searching')}>{table('searching', [...columns, { key: 'waiting', header: 'Waiting', width: 100, sort: row => row.queuedForMs, cell: row => waiting(row.queuedForMs) }], 'Nobody is searching')}</Section>
        <Section title="Pre-match" text="Paired players completing connection checks and game setup." actions={playerCount('pre_match')}>{table('pre_match', matchColumns, 'No pre-match setup')}</Section>
        <Section title="In-match" text="Players whose game has entered IN_GAME on the server." actions={playerCount('in_match')}>{table('in_match', matchColumns, 'No games in progress')}</Section>
      </div>
      {group('post_match').length > 0 && <Section title="Post-match" text="Games awaiting cleanup after the match.">{table('post_match', matchColumns, 'No post-match players')}</Section>}
      {group('unknown_match').length > 0 && <Section title="Matches without a reported phase" text="These paired players are not counted as playing because the server did not report their game state.">{table('unknown_match', matchColumns, 'No matches without a phase')}</Section>}
      <Section title="Other online players" text="Connected players outside the search and match registries.">{table('online', [...columns, { key: 'connected', header: 'Connected', width: 140, sort: row => row.connectedSince, cell: row => ago(row.connectedSince) }], 'No other players online')}</Section>
    </>}
  </div>;
}
