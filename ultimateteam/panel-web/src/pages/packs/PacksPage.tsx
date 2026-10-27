// Packs (plan 5.3): every pack and pick with where it is used (and the pack editor), Store lineup, Simulate and Gifts, at
// #/packs/<tab>. One kind of pack: the lineup sells some, rewards name some, the rest are unassigned.
import { href } from '../../app/router';
import { PageHeader } from '../../ui/Layout';
import { Tabs } from '../../ui/Tabs';
import { CatalogueTab, type TypeFilter } from './CatalogueTab';
import { GiftsTab } from './GiftsTab';
import { LineupTab } from './LineupTab';
import { PackEditor } from './PackEditor';
import { SimulateTab } from './SimulateTab';

const TABS = ['catalogue', 'lineup', 'tokens', 'simulate', 'gifts'] as const;
type PacksTab = (typeof TABS)[number];
const LABELS: Record<PacksTab, string> = { catalogue: 'Packs', lineup: 'Store lineup', tokens: 'Token Store Lineup', simulate: 'Simulate', gifts: 'Gifts' };

export function PacksPage({ rest, params = new URLSearchParams() }: { rest: string[]; params?: URLSearchParams }) {
  const [tab, id] = rest;
  const type: TypeFilter = params.get('type') === 'picks' ? 'picks' : params.get('type') === 'packs' ? 'packs' : 'all';
  if (tab === 'catalogue' && id) return <PackEditor packId={id === 'new' ? null : Number(id)} picksOnly={id === 'new' && type === 'picks'} />;
  const active: PacksTab = (TABS as readonly string[]).includes(tab ?? '') ? (tab as PacksTab) : 'catalogue';
  return (
    <>
      <PageHeader title="Packs / Picks" description="Every pack and pick, and where it is used: the store lineup sells packs and rewards name them. Definitions and the lineup are staged and published under Changes; simulations and gifts are separate." />
      <Tabs label="Packs" value={active} onChange={(next) => { window.location.hash = href('packs', next); }} items={TABS.map((value) => ({ value, label: LABELS[value] }))}>
        {active === 'catalogue' ? <CatalogueTab key={type} type={type} />
          : active === 'lineup' || active === 'tokens' ? <LineupTab key={active} section={active === 'tokens' ? 'tokens' : 'standard'} /> : active === 'simulate' ? <SimulateTab /> : <GiftsTab />}
      </Tabs>
    </>
  );
}
