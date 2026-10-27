// One card (plan 5.2): the large card with its face stats, facts, the player's other versions, and Grant to a club.
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { motion } from 'motion/react';
import { usePanelMotion } from '../../ui/Motion';
import { useAssets } from '../../api/assets';
import { api } from '../../api/client';
import { useChangeset } from '../../api/content';
import type { CardRow, CardsReply } from '../../api/types';
import { href } from '../../app/router';
import { Badge, StagedBadge } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { CardRow as CardStrip, FutCard, LeagueLogo } from '../../ui/FutCard';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section } from '../../ui/Layout';
import { StatBar } from '../../ui/StatBar';
import { GrantDialog } from './GrantDialog';

const openCard = (card: CardRow) => { window.location.hash = href('cards', card.cardKey); };
const versionLabel = (card: CardRow) => `${card.name ?? 'Card'} ${card.rating ?? ''} ${card.special ?? (card.rareflag === 1 ? 'Rare' : 'Common')}`.trim();

const OUTFIELD = ['PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY'];
const KEEPER = ['DIV', 'HAN', 'KIC', 'REF', 'SPD', 'POS'];
const FULL: Record<string, string> = {
  PAC: 'Pace', SHO: 'Shooting', PAS: 'Passing', DRI: 'Dribbling', DEF: 'Defending', PHY: 'Physical',
  DIV: 'Diving', HAN: 'Handling', KIC: 'Kicking', REF: 'Reflexes', SPD: 'Speed', POS: 'Positioning',
};

export function CardDetail({ cardKey }: { cardKey: string }) {
  const assets = useAssets().data;
  const { transition } = usePanelMotion();
  const changes = useChangeset();
  const [granting, setGranting] = useState(false);
  const resourceId = Number(cardKey.split(':')[0]);
  const found = useQuery({
    queryKey: ['card', cardKey],
    queryFn: () => api<CardsReply>(`cards?${new URLSearchParams({ resourceId: String(resourceId), limit: '1' })}`),
    enabled: Number.isSafeInteger(resourceId) && resourceId > 0,
  });
  const card = found.data?.cards?.find((row) => row.resourceId === resourceId);
  const versions = useQuery({
    queryKey: ['card-versions', card?.assetId],
    queryFn: () => api<CardsReply>(`cards?${new URLSearchParams({ q: String(card?.assetId), limit: '200' })}`),
    enabled: !!card?.assetId,
  });
  const crumbs = <a href={href('cards')}>Cards</a>;
  if (!Number.isSafeInteger(resourceId) || resourceId <= 0) return <><PageHeader crumbs={crumbs} title="Invalid card id" /><EmptyState title="Open a card from the catalogue" /></>;

  if (found.isPending) return <><PageHeader crumbs={crumbs} title="Loading…" /><Loading /></>;
  if (found.isError) return <><PageHeader crumbs={crumbs} title={cardKey} /><ErrorPanel what="This card" error={found.error} onRetry={() => void found.refetch()} /></>;
  if (!card) return <><PageHeader crumbs={crumbs} title={cardKey} /><EmptyState title="No card has this key" text="A card key is resourceId:rating:rareflag." /></>;

  const labels = (card.position === 'GK' ? found.data?.gkAttrs ?? KEEPER : found.data?.attrs ?? OUTFIELD);
  const stats = (card.attributes ?? []).map((value, index) => ({ label: labels[index] ?? '', value }));
  const others = (versions.data?.cards ?? []).filter((other) => other.cardKey !== card.cardKey);
  const pending = changes.data?.changeset?.changes?.some((change) => change.area === 'player_card' && Number(change.key.resourceId) === card.resourceId);

  return (
    <>
      <PageHeader crumbs={crumbs} title={`${card.name ?? 'Card'} ${card.rating ?? ''}`}
        description={[card.position, card.special ?? (card.rareflag === 1 ? 'Rare' : 'Common'), card.nation].filter(Boolean).join(' · ')}
        actions={<div className="row-actions"><LinkButton variant="staged" href={href('cards', card.cardKey, 'edit')}>Edit card</LinkButton><LinkButton href={href('cards', card.cardKey, 'new')}>New from this card</LinkButton><Button variant="primary" onClick={() => setGranting(true)}>Grant to a club</Button></div>} />
      {pending && <p className="notice"><StagedBadge>Pending edit</StagedBadge> Published values are shown here until the edit is published. <a href={href('changes')}>Review in Changes</a></p>}
      <motion.div className="card-detail" initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={transition}>
        <FutCard art={card.art} assets={assets} size="lg" stats={stats} />
        <Section title="Face stats">
          <div className="stack">{stats.map((stat) => <StatBar key={stat.label} label={FULL[stat.label] ?? stat.label} value={stat.value} />)}</div>
        </Section>
        <Section title="Card">
          <dl className="facts">
            <div><dt>Card key</dt><dd className="mono">{card.cardKey}</dd></div>
            <div><dt>Resource id</dt><dd className="mono">{card.resourceId}</dd></div>
            <div><dt>Player id</dt><dd className="mono">{card.assetId}</dd></div>
            <div><dt>League</dt><dd><LeagueLogo leagueId={card.leagueId} assets={assets} /> {card.leagueId ?? '–'}</dd></div>
            <div><dt>Pack restriction</dt><dd>{card.inPacks === false ? <Badge>Grant only</Badge> : 'Pack sources and promo events decide availability.'}</dd></div>
          </dl>
        </Section>
      </motion.div>
      <Section title="Other versions" text="Every other card of this player.">
        {versions.isPending ? <Loading lines={2} /> : versions.isError ? <ErrorPanel what="Other card versions" error={versions.error} onRetry={() => void versions.refetch()} /> : others.length ? (
          assets ? (
            <CardStrip>{others.map((other) => <FutCard key={other.cardKey} art={other.art} assets={assets} onClick={() => openCard(other)} title={versionLabel(other)} />)}</CardStrip>
          ) : (
            // Without downloaded images a card draws nothing, so the versions are plain buttons.
            <div className="row-actions">{others.map((other) => <Button key={other.cardKey} size="sm" onClick={() => openCard(other)}>{versionLabel(other)}</Button>)}</div>
          )
        ) : <p className="dim">This player has no other card.</p>}
      </Section>
      {granting && <GrantDialog title={`Grant ${card.name} ${card.rating}`} itemName={card.name ?? 'the card'} max={1}
        description="The published card lands in the club's New items at once. Pending card edits are not included."
        forced={!!(card.forcedUntradeable || card.rareflag === 10)} onClose={() => setGranting(false)}
        edit={(count, untradeable) => ({ op: 'grantCard', card: card.cardKey, count, untradeable })} />}
    </>
  );
}
