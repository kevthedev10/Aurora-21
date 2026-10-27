// The component kit (#/_kit, not in the navigation): every shared component in its states, for design review
// (ultimateteam/panel-redesign-plan.md, step 2). Sample data only; nothing here calls a write route.
import { useState } from 'react';
import type { CardArt } from '../api/assets';
import { useAssets } from '../api/assets';
import { Badge, StagedBadge, StatusDot } from '../ui/Badge';
import { Button, LinkButton } from '../ui/Button';
import { Chips } from '../ui/Chips';
import { DataTable, type Column } from '../ui/DataTable';
import { ConfirmDialog, Dialog } from '../ui/Dialog';
import { Field } from '../ui/Field';
import { Drawer } from '../ui/Drawer';
import { ago, num } from '../ui/format';
import { CardGrid, CardRow, FutCard, LeagueLogo } from '../ui/FutCard';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section, Stat, Stats, Toolbar } from '../ui/Layout';
import { StatGroup } from '../ui/StatBar';
import { Tabs } from '../ui/Tabs';
import { useToast } from '../ui/Toast';
import { ApiError } from '../api/client';

type Sample = CardArt & { face: number[] };

// Real FIFA 21 ids (from runtime-source players.json), so the downloaded images show.
const CARDS: Sample[] = [
  { resourceId: 158023, assetId: 158023, rareflag: 1, rating: 93, position: 'RW', name: 'Messi', teamId: 241, nationId: 52, leagueId: 53, face: [85, 92, 91, 95, 38, 65] },
  { resourceId: 157481, assetId: 157481, rareflag: 0, rating: 82, position: 'CB', name: 'Raúl Albiol', teamId: 483, nationId: 45, leagueId: 53, face: [45, 41, 62, 64, 86, 75] },
  { resourceId: 49000, assetId: 49000, rareflag: 1, rating: 74, position: 'GK', name: 'McGregor', teamId: 86, nationId: 42, leagueId: 50, face: [74, 71, 66, 79, 36, 73] },
  { resourceId: 204729, assetId: 204729, rareflag: 0, rating: 64, position: 'CDM', name: 'Afonso Taira', teamId: 1889, nationId: 38, leagueId: 308, face: [54, 50, 58, 63, 62, 65] },
  { resourceId: 167792961, assetId: 20801, rareflag: 3, rating: 95, position: 'ST', name: 'Cristiano Ronaldo', teamId: 114153, nationId: 38, leagueId: 31, face: [92, 96, 87, 93, 38, 82] },
  { resourceId: 237067, assetId: 190043, rareflag: 12, rating: 98, position: 'CAM', name: 'Pelé', teamId: 112658, nationId: 54, leagueId: 2118, face: [95, 96, 93, 96, 60, 76] },
  { resourceId: 117598535, assetId: 158023, rareflag: 5, rating: 98, position: 'RW', name: 'Messi', teamId: 241, nationId: 52, leagueId: 53, face: [93, 98, 97, 99, 43, 73] },
  { resourceId: 33733035, assetId: 178603, rareflag: 8, rating: 88, position: 'CB', name: 'Hummels', teamId: 22, nationId: 21, leagueId: 19, face: [62, 60, 80, 75, 91, 81] },
  { resourceId: 100839876, assetId: 176580, rareflag: 21, rating: 90, position: 'ST', name: 'Suárez', teamId: 240, nationId: 60, leagueId: 53, face: [75, 94, 87, 88, 54, 86] },
  { resourceId: 100866672, assetId: 203376, rareflag: 6, rating: 91, position: 'CB', name: 'van Dijk', teamId: 9, nationId: 34, leagueId: 13, face: [77, 60, 80, 72, 92, 87] },
  { resourceId: 151152967, assetId: 158023, rareflag: 11, rating: 98, position: 'ST', name: 'Messi', teamId: 241, nationId: 52, leagueId: 53, face: [91, 99, 96, 99, 43, 80] },
  { resourceId: 218280388, assetId: 176580, rareflag: 4, rating: 90, position: 'ST', name: 'Suárez', teamId: 240, nationId: 60, leagueId: 53, face: [75, 94, 87, 88, 54, 86] },
];
const FACE = ['PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY'];
const faceStats = (card: Sample) => card.face.map((value, index) => ({ label: FACE[index] ?? '', value }));

interface Row { id: number; name: string; persona: number; coins: number; points: number; online: boolean | null; seen: string; card: Sample }
const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();
const ROWS: Row[] = [
  { id: 1, name: 'Northbank', persona: 1001882731, coins: 1_250_000, points: 3_400, online: true, seen: minutesAgo(1), card: CARDS[0]! },
  { id: 2, name: 'Kopite Karen', persona: 1002331907, coins: 84_500, points: 0, online: false, seen: minutesAgo(190), card: CARDS[4]! },
  { id: 3, name: 'Tiki Taka Tom', persona: 1000417755, coins: 9_900_000, points: 12_000, online: null, seen: minutesAgo(60 * 50), card: CARDS[5]! },
  { id: 4, name: 'Park the Bus', persona: 1003019264, coins: 350, points: 150, online: false, seen: minutesAgo(8), card: CARDS[7]! },
];

export function Kit() {
  const assets = useAssets();
  const toast = useToast();
  const [pile, setPile] = useState<'club' | 'new' | 'transfer'>('club');
  const [tab, setTab] = useState<'overview' | 'inventory' | 'squads'>('overview');
  const [dialog, setDialog] = useState(false);
  const [drawer, setDrawer] = useState(false);
  const [confirm, setConfirm] = useState<null | 'danger' | 'staged' | 'refused'>(null);
  const art = assets.data;
  const columns: Column<Row>[] = [
    { key: 'card', header: '', cell: (row) => <FutCard art={row.card} assets={art} size="xs" /> },
    { key: 'name', header: 'Player', sort: (row) => row.name, cell: (row) => <div><b>{row.name}</b><div className="dim mono">{row.persona}</div></div> },
    { key: 'coins', header: 'Coins', align: 'end', sort: (row) => row.coins, cell: (row) => num(row.coins) },
    { key: 'points', header: 'Aurora Tokens', align: 'end', sort: (row) => row.points, cell: (row) => num(row.points) },
    { key: 'status', header: 'Status', cell: (row) => <StatusDot state={row.online}>{row.online === true ? 'Online' : row.online === false ? 'Offline' : 'Not reported'}</StatusDot> },
    { key: 'seen', header: 'Last seen', sort: (row) => row.seen, cell: (row) => ago(row.seen) },
  ];

  return (
    <>
      <PageHeader
        crumbs={<a href="#/dashboard">Dashboard</a>}
        title="Component kit"
        description="Every shared piece of the new panel in its states, with sample data. Review the look here before pages are built."
        actions={<><Button>Refresh</Button><Button variant="primary">Gift packs</Button></>}
      />

      <Section title="Cards" text={art ? `Drawn from the downloaded images: ${num(art.frames?.length)} frames, ${num(art.categories?.players)} player images, ${num(art.categories?.dynamic)} special-card images.` : 'No card images on this server: pages fall back to tables.'}>
        {assets.isPending ? <Loading lines={2} /> : art ? (
          <>
            <CardGrid>{CARDS.map((card) => <FutCard key={card.resourceId} art={card} assets={art} onClick={() => toast(`${card.name} ${card.rating}`)} />)}</CardGrid>
            <h3 className="kit-sub">Detail size, with face stats</h3>
            <CardRow>{CARDS.slice(0, 1).concat(CARDS[4]!, CARDS[5]!).map((card) => <FutCard key={card.resourceId} art={card} assets={art} size="lg" stats={faceStats(card)} />)}</CardRow>
            <h3 className="kit-sub">Card detail layout</h3>
            <div className="kit-detail">
              <FutCard art={CARDS[0]} assets={art} size="lg" stats={faceStats(CARDS[0]!)} />
              <div className="kit-statgrid">
                <StatGroup label="Pace" value={85} stats={[{ label: 'Acceleration', value: 91 }, { label: 'Sprint speed', value: 80 }]} />
                <StatGroup label="Shooting" value={92} stats={[{ label: 'Positioning', value: 93 }, { label: 'Finishing', value: 95 }, { label: 'Shot power', value: 86 }, { label: 'Long shots', value: 94 }]} />
                <StatGroup label="Defending" value={38} stats={[{ label: 'Interceptions', value: 40 }, { label: 'Heading', value: 70 }, { label: 'Marking', value: 32 }, { label: 'Standing tackle', value: 35 }]} />
                <StatGroup label="Physical" value={65} stats={[{ label: 'Jumping', value: 68 }, { label: 'Stamina', value: 72 }, { label: 'Strength', value: 69 }, { label: 'Aggression', value: 44 }]} />
              </div>
              <dl className="kit-facts">
                <dt>League</dt><dd><LeagueLogo leagueId={53} assets={art} /> LaLiga</dd>
                <dt>Card key</dt><dd className="mono">158023:1</dd>
                <dt>Status</dt><dd><StagedBadge>Rating change staged</StagedBadge></dd>
              </dl>
            </div>
          </>
        ) : <EmptyState title="No card images" text="Download them with ultimateteam/download-card-images.py." />}
      </Section>

      {art?.frames?.length ? (
        <Section title="Every frame" text="Each downloaded frame with one player, to check that the rating, position and name stay readable on it.">
          <CardGrid>
            {art.frames.filter((frame) => /^\d+_/.test(frame)).map((frame) => {
              const rareflag = Number(frame.split('_')[0]);
              const rating = frame.includes('silver') ? 70 : frame.includes('bronze') ? 60 : 90;
              return <FutCard key={frame} art={{ ...CARDS[0]!, rareflag, rating, name: frame.replace('.png', '') }} assets={art} />;
            })}
          </CardGrid>
        </Section>
      ) : null}

      <Section title="Figures and state">
        <Stats>
          <Stat label="Coins" value={num(1_250_000)} hint="As of the last save" />
          <Stat label="Aurora Tokens" value={num(3_400)} />
          <Stat label="Club items" value={num(812)} />
          <Stat label="Last seen" value="2 min ago" hint="Signed in now" />
        </Stats>
        <Toolbar>
          <StatusDot state>Blaze</StatusDot>
          <StatusDot state={false}>FUT</StatusDot>
          <StatusDot state={null}>Relay</StatusDot>
          <Badge tone="pitch">Online</Badge>
          <StagedBadge>3 pending</StagedBadge>
          <Badge tone="flare">Maintenance on</Badge>
          <Badge tone="red">Blocked</Badge>
          <Badge tone="coin">Bypass</Badge>
          <Badge>Grant only</Badge>
        </Toolbar>
      </Section>

      <Section title="Actions" text="Apply changes a club at once. Stage change waits under Changes until it is published.">
        <Toolbar>
          <Button variant="primary">Apply</Button>
          <Button variant="staged">Stage change</Button>
          <Button>Test contents</Button>
          <Button variant="danger">Block player</Button>
          <Button variant="ghost">Cancel</Button>
          <Button disabled>Unavailable</Button>
          <Button size="sm">Small</Button>
          <LinkButton href="#/_kit">A link as a button</LinkButton>
        </Toolbar>
        <Toolbar>
          <Button onClick={() => toast('Gave Northbank 5,000 coins.')}>Toast: applied</Button>
          <Button onClick={() => toast('Staged: publish it under Changes.', 'staged')}>Toast: staged</Button>
          <Button onClick={() => toast('That player is signed in; confirm to apply anyway.', 'error')}>Toast: refused</Button>
          <Button onClick={() => setDialog(true)}>Dialog</Button>
          <Button onClick={() => setDrawer(true)}>Drawer</Button>
          <Button onClick={() => setConfirm('danger')}>Confirm: danger</Button>
          <Button onClick={() => setConfirm('staged')}>Confirm: staged</Button>
          <Button onClick={() => setConfirm('refused')}>Confirm: refused</Button>
        </Toolbar>
      </Section>

      <Section title="Forms">
        <div className="kit-form">
          <Field label="Coins +/-" hint="now 1,250,000 (last save)"><input inputMode="numeric" placeholder="e.g. 5000 or -1000" /></Field>
          <Field label="Aurora Tokens +/-" error="Must be a non-zero whole number."><input inputMode="numeric" defaultValue="0" /></Field>
          <Field label="Pack"><select defaultValue="3"><option value="3">Gold Pack</option><option value="4">Premium Gold Pack</option></select></Field>
          <Field label="Note" hint="Shown in the access history"><textarea defaultValue="Blocked for testing." /></Field>
        </div>
      </Section>

      <Section title="Tabs and chips">
        <Tabs label="Club" value={tab} onChange={setTab} items={[{ value: 'overview', label: 'Overview' }, { value: 'inventory', label: 'Inventory', count: 812 }, { value: 'squads', label: 'Squads', count: 3 }]}>
          <Toolbar>
            <Chips label="Pile" value={pile} onChange={setPile} options={[{ value: 'club', label: 'Club', count: 764 }, { value: 'new', label: 'New items', count: 12 }, { value: 'transfer', label: 'Transfer list', count: 36 }]} />
          </Toolbar>
          <p className="dim">Tab: {tab}. Pile: {pile}.</p>
        </Tabs>
      </Section>

      <Section title="Table" text="Sortable, keyboard-openable rows; on a phone each row stacks with its labels.">
        <DataTable columns={columns} rows={ROWS} rowKey={(row) => row.id} onRowClick={(row) => toast(`Open ${row.name}`)} rowLabel={(row) => `Open ${row.name}`} initialSort={{ key: 'coins', direction: 'desc' }} pageSize={3} />
      </Section>

      <Section title="Empty, loading and error">
        <div className="kit-states">
          <EmptyState title="This pile is empty" text="Cards the club opens or is granted appear here." action={<Button size="sm">Grant a card</Button>} />
          <Loading />
          <ErrorPanel what="The inventory" error={new ApiError('That persona has no club yet; the server creates it on their first request.', 404, null)} onRetry={() => toast('Retried')} />
        </div>
      </Section>

      <Drawer open={drawer} onOpenChange={setDrawer} title="Sample drawer" description="Sample data; this does not change a club.">
        <Field label="Review note"><textarea placeholder="Try typing, closing, and reopening the drawer." /></Field>
        <p className="dim">Use Escape, the close button, or the backdrop to close. Focus returns to the Drawer button.</p>
      </Drawer>
      <Dialog open={dialog} onOpenChange={setDialog} title="Gift packs to Northbank" description="Gifts land in MY PACKS at once. The panel cannot take them back." wide
        footer={<><Button variant="ghost" onClick={() => setDialog(false)}>Cancel</Button><Button variant="primary" onClick={() => { setDialog(false); toast('Gifted 2 x Gold Pack.'); }}>Gift 2 packs</Button></>}>
        <div className="kit-form">
          <Field label="Pack"><select defaultValue="3"><option value="3">Gold Pack</option></select></Field>
          <Field label="How many" hint="1–50 per club"><input type="number" defaultValue={2} min={1} max={50} /></Field>
        </div>
        {art && <CardRow>{CARDS.slice(0, 5).map((card) => <FutCard key={card.resourceId} art={card} assets={art} />)}</CardRow>}
      </Dialog>
      <ConfirmDialog open={confirm === 'danger'} onOpenChange={(open) => !open && setConfirm(null)} title="Block Northbank?" text="Their launch codes and session are revoked and their Blaze connections close." confirmLabel="Block player" variant="danger" onConfirm={() => toast('Blocked Northbank.')} />
      <ConfirmDialog open={confirm === 'staged'} onOpenChange={(open) => !open && setConfirm(null)} title="Hide Gold Pack?" text="The pack leaves the store after review and publish. Owned copies remain." confirmLabel="Stage hide" variant="staged" onConfirm={() => toast('Staged: publish it under Changes.', 'staged')} />
      <ConfirmDialog open={confirm === 'refused'} onOpenChange={(open) => !open && setConfirm(null)} title="Reset Division Rivals for Northbank?" text="Settled rewards are kept." confirmLabel="Reset Division Rivals" variant="danger" onConfirm={() => Promise.reject(new Error('The FUT server refused the reset: the club is in a match.'))} />
    </>
  );
}
