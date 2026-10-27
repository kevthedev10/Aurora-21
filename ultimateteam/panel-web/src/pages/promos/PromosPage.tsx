import { useQuery } from '@tanstack/react-query';
import { motion } from 'motion/react';
import { useState } from 'react';
import { useAssets, type CardArt } from '../../api/assets';
import { api } from '../../api/client';
import { href } from '../../app/router';
import { Badge } from '../../ui/Badge';
import { Field } from '../../ui/Field';
import { FutCard } from '../../ui/FutCard';
import { EmptyState, ErrorPanel, Loading, PageHeader } from '../../ui/Layout';
import { usePanelMotion } from '../../ui/Motion';
import { num } from '../../ui/format';
import { CardBrowser } from '../cards/CardsPage';
import { Button } from '../../ui/Button';
import { useChangeset } from '../../api/content';
import { BucketEditor } from './BucketEditor';
import { curatedMembership, PromoGroupEditor } from './PromoGroupEditor';
import type { PromoDefinition } from './BucketEditor';
import './promos.css';

interface Promo {
  code: string; name: string; category: string; status: string;
  cards: number; buckets: number; artwork: CardArt[];
  draft?: boolean;
}
interface PromosReply { definitions: Promo[] }
const STANDARD_ORDER = ['bronze_common', 'bronze_rare', 'silver_common', 'silver_rare', 'gold_common', 'gold_rare'];
const ICON_ORDER = ['base_icons', 'mid_icons', 'prime_icons', 'prime_icon_moments'];
const alphabetical = new Intl.Collator('en', { numeric: true, sensitivity: 'base' });
export function comparePromos(a: { code: string; name: string }, b: { code: string; name: string }) {
  const aStandard = STANDARD_ORDER.indexOf(a.code), bStandard = STANDARD_ORDER.indexOf(b.code);
  if (aStandard >= 0 || bStandard >= 0) return (aStandard < 0 ? STANDARD_ORDER.length : aStandard) - (bStandard < 0 ? STANDARD_ORDER.length : bStandard);
  const family = (p: { code: string; name: string }) => ICON_ORDER.includes(p.code) ? 'Icons'
    : /(^|_)tots(_|$)/.test(p.code) ? 'TOTS' : /(^|_)potm(_|$)/.test(p.code) ? 'POTM'
    : /(^|_)totw(_|$)/.test(p.code) ? 'TOTW' : /(^|_)motm(_|$)/.test(p.code) ? 'MOTM'
    : /flashback/.test(p.code) ? 'Flashbacks' : p.code === 'team_of_the_year' || p.code.startsWith('toty_') ? 'TOTY' : p.name;
  const byFamily = alphabetical.compare(family(a), family(b));
  if (byFamily) return byFamily;
  if (ICON_ORDER.includes(a.code) && ICON_ORDER.includes(b.code)) return ICON_ORDER.indexOf(a.code) - ICON_ORDER.indexOf(b.code);
  return alphabetical.compare(a.name, b.name) || alphabetical.compare(a.code, b.code);
}

export function PromosPage({ rest, params }: { rest: string[]; params: URLSearchParams }) {
  const promos = useQuery({ queryKey: ['promos'], queryFn: ({ signal }) => api<PromosReply>('promos', { signal }) });
  const assets = useAssets().data;
  const { reduced, transition } = usePanelMotion();
  const [search, setSearch] = useState('');
  const [creating, setCreating] = useState(false);
  const pending = useChangeset();
  if (promos.isPending) return <Loading />;
  if (promos.isError) return <ErrorPanel what="The promos" error={promos.error} onRetry={() => void promos.refetch()} />;
  const definitions = [...promos.data.definitions];
  for (const change of pending.data?.changeset?.changes ?? []) {
    if (change.area !== 'promo' || change.operation !== 'insert' || !change.after) continue;
    const definition = change.after as PromoDefinition;
    const code = String(change.key.code);
    if (!definitions.some(p => p.code === code)) definitions.push({ code, name: definition.name, category: definition.odds_category,
      status: definition.status, cards: definition.buckets.reduce((count, b) => count + b.cards.length, 0),
      buckets: definition.buckets.length, artwork: [], draft: true });
  }
  const codes = definitions.map(p => p.code);
  if (rest[0]) {
    const promo = definitions.find(p => p.code === rest[0]);
    return promo ? <PromoDetail key={promo.code} params={params} promo={promo} existingCodes={codes} />
      : <EmptyState title="Promo not found" text="This promo is not in the published catalogue." action={<a href={href('promos')}>All promos</a>} />;
  }
  const visible = definitions.filter(p => `${p.name} ${p.code}`.toLowerCase().includes(search.trim().toLowerCase())).sort(comparePromos);
  return <>
    <PageHeader title="Promos" description="Create promos and manage their cards and weights. Assign promos to campaign drops; TOTW and Icons use standalone rotations." actions={<Button variant="primary" onClick={() => setCreating(true)}><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><path d="M12 5v14M5 12h14" /></svg>Create promo</Button>} />
    <div className="promo-toolbar"><Field label="Find a promo"><input type="search" placeholder="Search promo names…" value={search} onChange={e => setSearch(e.target.value)} /></Field><span className="dim">{num(visible.length)} promos</span></div>
    {!visible.length ? <EmptyState title="No promos found" text="Try another name." /> : <div className="promo-grid">
      {visible.map(promo => <motion.a key={promo.code} className="promo-tile" href={href('promos', promo.code)}
        whileHover={reduced ? {} : { y: -4 }} transition={transition}>
        <div className="promo-art" aria-hidden="true">
          {assets && promo.artwork?.length ? promo.artwork.map((art, i) => <div className={`promo-art-card promo-art-card-${i}`} key={art.resourceId ?? i}><FutCard art={art} assets={assets} size="sm" /></div>) : <span className="promo-art-placeholder">{promo.name.slice(0, 2).toUpperCase()}</span>}
        </div>
        <div className="promo-tile-body"><span className="promo-category">{promo.category.replaceAll('_', ' ')}</span><h2>{promo.name}</h2>
          <div className="promo-tile-meta"><span>{num(promo.cards)} {promo.cards === 1 ? 'card' : 'cards'} · {num(promo.buckets)} {promo.buckets === 1 ? 'bucket' : 'buckets'}</span><span aria-hidden="true">↗</span></div>
          {promo.status !== 'active' && <Badge>{promo.status}</Badge>}
          {promo.draft && <Badge tone="staged">Draft</Badge>}
        </div>
      </motion.a>)}
    </div>}
    {creating && <PromoGroupEditor existingCodes={codes} onClose={() => setCreating(false)} />}
  </>;
}

function PromoDetail({ promo, params, existingCodes }: { promo: Promo; params: URLSearchParams; existingCodes: string[] }) {
  const [editing, setEditing] = useState<string | null>(null);
  const [membership, setMembership] = useState(false);
  const pending = useChangeset();
  const hasPending = pending.data?.changeset?.changes?.some(c => c.area === 'promo' && c.key.code === promo.code);
  return <>
    {hasPending && <p className="notice">This promo has pending group changes.{!promo.draft && ' This page shows published membership.'} <a href={href('changes')}>Review Changes</a></p>}
    {promo.draft ? <><PageHeader title={promo.name} description="Manage this promo’s cards. Assign the promo to a campaign drop to schedule availability." /><Button variant="primary" onClick={() => setMembership(true)}>Manage cards</Button></>
      : <CardBrowser params={params} promo={promo} onEditBucket={setEditing} actions={<><Button variant="primary" onClick={() => setEditing('')}>Create bucket</Button><Button disabled={curatedMembership(promo.code)} onClick={() => setMembership(true)}>Manage cards</Button></>} />}
    {curatedMembership(promo.code) && <p className="dim">Standard pools and Icon tiers retain their curated membership. Use the bucket editor to organise these cards.</p>}
    {membership && <PromoGroupEditor code={promo.code} existingCodes={existingCodes} onClose={() => setMembership(false)} />}
    {editing !== null && <BucketEditor key={editing} code={promo.code} {...(editing && { bucketCode: editing })} onClose={() => setEditing(null)} />}
  </>;
}
