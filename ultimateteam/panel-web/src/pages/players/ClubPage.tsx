// One player's club (plan 4.2): header with identity and balances, and tabs routed as #/players/<persona>/<tab>.
import type { ReactNode } from 'react';
import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useClub, type PlayerDetail } from '../../api/players';
import { href } from '../../app/router';
import { Badge, StatusDot } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { ago, num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Stat, Stats } from '../../ui/Layout';
import { Tabs } from '../../ui/Tabs';
import { AccessTab } from './AccessTab';
import { InventoryTab } from './InventoryTab';
import { LogTab } from './LogTab';
import { ModesTab } from './ModesTab';
import { OverviewTab } from './OverviewTab';
import { PacksTab } from './PacksTab';

export const CLUB_TABS = ['overview', 'inventory', 'packs', 'modes', 'access', 'log'] as const;
export type ClubTab = (typeof CLUB_TABS)[number];
const LABELS: Record<ClubTab, string> = {
  overview: 'Overview', inventory: 'Inventory', packs: 'Packs & gifts', modes: 'Modes', access: 'Access & sessions', log: 'Log',
};

export function ClubPage({ persona, tab, basePath = 'players' }: { persona: number; tab: string | undefined; basePath?: 'players' | 'clubs' }) {
  const club = useClub(persona);
  const client = useQueryClient();
  const [refreshing, setRefreshing] = useState(false);
  const requested: ClubTab = (CLUB_TABS as readonly string[]).includes(tab ?? '') ? (tab as ClubTab) : 'overview';
  const crumbs = <a href={href(basePath)}>Players / Clubs</a>;
  const refresh = async () => {
    setRefreshing(true);
    try {
      await Promise.all([club.refetch(), client.invalidateQueries({ queryKey: ['inventory', persona] }), client.invalidateQueries({ queryKey: ['modes', persona] })]);
    } finally { setRefreshing(false); }
  };

  if (club.isPending) return <><PageHeader crumbs={crumbs} title="Loading…" /><Loading /></>;
  if (club.isError) return <><PageHeader crumbs={crumbs} title={`Persona ${persona}`} /><ErrorPanel what="This player" error={club.error} onRetry={() => void club.refetch()} /></>;
  const detail = club.data;
  const tabs: readonly ClubTab[] = detail.club ? CLUB_TABS : CLUB_TABS.filter(value => value === 'access' || value === 'log');
  const active = tabs.includes(requested) ? requested : 'access';

  return (
    <>
      <PageHeader crumbs={crumbs} title={detail.name ?? `Persona ${persona}`} description={<Identity detail={detail} />}
        actions={<Button onClick={() => void refresh()} disabled={refreshing || club.isFetching}>{refreshing ? 'Refreshing…' : 'Refresh'}</Button>} />
      <Balances detail={detail} />
      <Tabs
        label="Club"
        value={active}
        onChange={(next) => { window.location.hash = href(basePath, persona, next); }}
        items={tabs.map((value) => ({
          value, label: LABELS[value],
          count: value === 'log' ? detail.edits?.length : undefined,
        }))}
      >
        <TabBody tab={active} detail={detail} />
      </Tabs>
    </>
  );
}

function Identity({ detail }: { detail: PlayerDetail }) {
  const parts: ReactNode[] = [
    <StatusDot key="online" state={detail.online ?? null}>{detail.online ? 'Online' : 'Offline'}</StatusDot>,
    <span key="persona" className="mono">persona {detail.id}</span>,
  ];
  if (detail.discordId) parts.push(<span key="discord" className="mono">Discord {detail.discordId}</span>);
  if (detail.club?.name) parts.push(<span key="club">Club: <b>{detail.club.name}</b>{detail.club.abbr ? ` (${detail.club.abbr})` : ''}</span>);
  parts.push(<span key="logins">{num(detail.logins)} login{detail.logins === 1 ? '' : 's'}</span>);
  if (detail.lastLogin) parts.push(<span key="last">last {ago(detail.lastLogin)}</span>);
  if (detail.access?.blocked) parts.push(<Badge key="blocked" tone="red">Blocked</Badge>);
  if (detail.access?.maintenanceBypass) parts.push(<Badge key="bypass" tone="coin">Maintenance bypass</Badge>);
  return <span className="club-identity">{parts}</span>;
}

function Balances({ detail }: { detail: PlayerDetail }) {
  const club = detail.club;
  if (!club) return <EmptyState title="No club yet" text="The server creates the club on this player's first Ultimate Team request." />;
  const counts = detail.inventory;
  return (
    <div className="club-stats">
      <Stats>
        <Stat label="Coins" value={num(club.coins)} />
        <Stat label="Aurora Tokens" value={num(club.points)} />
        <Stat label="In the club" value={num(counts?.club ?? club.items)} />
        <Stat label="New items" value={num(counts?.new)} />
        <Stat label="Transfer list" value={num(counts?.transfer ?? club.tradePile)} />
        <Stat label="Unopened packs" value={num(club.unopenedPacks)} />
      </Stats>
    </div>
  );
}

function TabBody({ tab, detail }: { tab: ClubTab; detail: PlayerDetail }) {
  switch (tab) {
    case 'overview': return <OverviewTab detail={detail} />;
    case 'inventory': return <InventoryTab detail={detail} />;
    case 'packs': return <PacksTab detail={detail} />;
    case 'modes': return <ModesTab detail={detail} />;
    case 'access': return <AccessTab detail={detail} />;
    case 'log': return <LogTab detail={detail} />;
    default:
      return (
        <EmptyState
          title={`${LABELS[tab]} is not built yet`}
          text="Choose an available club tab."
          action={<LinkButton href={href('players', detail.id)}>Back to overview</LinkButton>}
        />
      );
  }
}
