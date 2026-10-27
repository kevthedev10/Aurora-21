import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { AnimatePresence, LayoutGroup, motion } from 'motion/react';
import { usePanelMotion } from '../ui/Motion';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, sessionEvents } from '../api/client';
import { useChangeset, useContentDestination } from '../api/content';
import { SchedulePage } from '../pages/schedule/SchedulePage';
import type { DashboardReply, SessionReply } from '../api/types';
import { CardsPage } from '../pages/cards/CardsPage';
import { PromosPage } from '../pages/promos/PromosPage';
import { ChangesPage } from '../pages/changes/ChangesPage';
import { Kit } from '../pages/Kit';
import { PacksPage } from '../pages/packs/PacksPage';
import { ModesPage } from '../pages/modes/ModesPage';
import { RulesPage } from '../pages/rules/RulesPage';
import { ObjectivesPage } from '../pages/objectives/ObjectivesPage';
import { SbcPage } from '../pages/sbc/SbcPage';
import { ClubPage } from '../pages/players/ClubPage';
import { PlayersList } from '../pages/players/PlayersList';
import { AccessPage } from '../pages/operations/AccessPage';
import { DashboardPage } from '../pages/operations/DashboardPage';
import { LogsPage } from '../pages/operations/LogsPage';
import { MatchmakingPage } from '../pages/operations/MatchmakingPage';
import { LeaversPage } from '../pages/operations/LeaversPage';
import { MarketLogPage } from '../pages/operations/MarketLogPage';
import { Badge, StatusDot } from '../ui/Badge';
import { Button, LinkButton } from '../ui/Button';
import { Field } from '../ui/Field';
import { Drawer } from '../ui/Drawer';
import { useMediaQuery } from '../ui/useMediaQuery';
import { EmptyState, PageHeader } from '../ui/Layout';
import { CommandPalette } from './CommandPalette';
import { PageErrorBoundary } from './ErrorBoundary';
import { PendingDrawer } from './PendingDrawer';
import { ContentStatusNotice } from './ContentStatusNotice';
import { GROUPS, menu, pageTitle, resolve, type Group, type Resolved } from './routes';
import { href, useRoute } from './router';

const STATUS_POLL_MS = 10_000;

export function App() {
  const client = useQueryClient();
  const session = useQuery({ queryKey: ['session'], queryFn: () => api<SessionReply>('session'), retry: false });
  const [signedOut, setSignedOut] = useState(false);

  useEffect(() => {
    const onSignedOut = () => setSignedOut(true);
    sessionEvents.addEventListener('signedout', onSignedOut);
    return () => sessionEvents.removeEventListener('signedout', onSignedOut);
  }, []);

  if (session.isPending) return <p className="boot">Loading…</p>;
  // Sign-in keeps the address, so the same page opens once it succeeds.
  if (session.isError || signedOut) {
    return (
      <SignIn
        onSignedIn={() => {
          setSignedOut(false);
          void client.invalidateQueries();
        }}
      />
    );
  }
  return <Shell />;
}

/** Loopback mode: the SSH tunnel is the credential, and sign-in only confirms the panel answers. */
function SignIn({ onSignedIn }: { onSignedIn: () => void }) {
  const [password, setPassword] = useState('');
  const login = useMutation({
    mutationFn: () => api<SessionReply>('login', { method: 'POST', body: { password } }),
    onSuccess: onSignedIn,
  });
  const submit = (event: FormEvent) => {
    event.preventDefault();
    login.mutate();
  };
  return (
    <main className="signin">
      <form className="signin-card" onSubmit={submit}>
        <Brand />
        <Field label="Password" hint="This panel is reached through a private tunnel; the password is not checked.">
          <input type="password" value={password} autoComplete="current-password" autoFocus onChange={(event) => setPassword(event.target.value)} />
        </Field>
        {login.isError && <p className="error" role="alert">{login.error.message}</p>}
        <Button variant="primary" type="submit" disabled={login.isPending}>
          Sign in
        </Button>
      </form>
    </main>
  );
}

function Brand() {
  return (
    <a className="brand" href={href('dashboard')}>
      Aurora <b>21</b>
    </a>
  );
}

function Shell() {
  const destination = useContentDestination();
  const route = useRoute();
  const resolved = resolve(route.parts, route.params);
  const pageId = resolved.kind === 'page' ? resolved.page.id === 'clubs' ? 'players' : resolved.page.id : null;
  const [menuOpen, setMenuOpen] = useState(false);
  const [palette, setPalette] = useState(false);
  const changes = useChangeset(STATUS_POLL_MS);
  const pending = Number(changes.data?.pending) || 0;
  const narrow = useMediaQuery('(max-width: 760px)');

  // Classic route names move to their new address (replace: Back skips the old one).
  useEffect(() => {
    if (resolved.kind === 'redirect') window.location.replace(`#/${resolved.to}`);
  }, [resolved]);
  useEffect(() => { document.title = pageTitle(resolved); }, [resolved]);
  // Phones: the menu closes when a page is picked.
  useEffect(() => setMenuOpen(false), [route]);
  useEffect(() => { if (!narrow) setMenuOpen(false); }, [narrow]);
  // Ctrl K (Cmd K on a Mac) opens the search from anywhere.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setPalette((open) => !open);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  return (
    <div className="shell">
      <a className="skip-link" href="#panel-main" onClick={(event) => {
        event.preventDefault();
        document.getElementById('panel-main')?.focus();
      }}>Skip to content</a>
      <aside className="rail">
        <div className="rail-head">
          <Brand />
          {!narrow ? <div className="header-controls">
            <DesktopNavigation pageId={pageId} onSearch={() => setPalette(true)} />
          </div> : <SearchButton onSearch={() => setPalette(true)} />}
          <Button size="sm" variant="ghost" className="menu-button" aria-expanded={menuOpen} aria-haspopup="dialog" onClick={() => setMenuOpen(true)}>
            Menu
          </Button>
        </div>
      </aside>
      <div className="main">
        <main className="page" id="panel-main" tabIndex={-1}>
          {pending > 0 && <section className="pending-notice" aria-label="Unpublished changes">
            <span className="pending-count" aria-hidden="true">{pending}</span>
            <div className="pending-notice-copy" role="status">
              <strong>{pending} {pending === 1 ? 'change' : 'changes'} saved {destination ? 'in the selected drop' : 'for publication'}</strong>
              <span>The game still uses the published values.</span>
            </div>
            <LinkButton variant="primary" href={href('changes')}>Review changes</LinkButton>
          </section>}
          <ContentStatusNotice />
          {/* Card query changes preserve focus and drafts; changing the card/page still resets the boundary. */}
          <PageErrorBoundary key={`${destination ?? 'immediate'}:${pageId === 'cards' ? route.parts.join('/') : window.location.hash}`}>
            <Page resolved={resolved} params={route.params} />
          </PageErrorBoundary>
        </main>
        <StatusBar />
      </div>
      <Drawer open={narrow && menuOpen} onOpenChange={setMenuOpen} title="Navigation">
        <PanelNavigation pageId={pageId} onNavigate={() => setMenuOpen(false)} />
      </Drawer>
      <CommandPalette open={palette} onOpenChange={setPalette} />
    </div>
  );
}

const PAGE_DESCRIPTIONS: Record<string, string> = {
  schedule: 'Plan campaigns, content drops and expiries.',
  cards: 'Browse the catalogue and edit cards.',
  packs: 'Manage packs, picks and the store lineup.',
  promos: 'Review promo pools and bucket odds.',
  events: 'Schedule promos and Icon rotations.',
  modes: 'Configure game modes and rewards.',
  sbc: 'Manage squad building challenges.',
  objectives: 'Objectives, live objectives and the Season Pass.',
  rules: 'Review and edit shared game rules.',
  changes: 'Publish pending edits and review history.',
  access: 'Manage player access and maintenance.',
  matchmaking: 'Inspect queues and match sessions.',
  leavers: 'Find players who keep leaving matches early.',
  'market-log': 'Review market sales for coin transfers.',
  logs: 'Browse server logs and live output.',
};

function DesktopNavigation({ pageId, onSearch }: { pageId: string | null; onSearch: () => void }) {
  const id = useId();
  const root = useRef<HTMLElement>(null);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [open, setOpen] = useState<Group | null>(null);
  const { reduced, transition } = usePanelMotion();
  const cancelClose = () => { if (closeTimer.current) clearTimeout(closeTimer.current); };
  const close = () => { cancelClose(); setOpen(null); };

  useEffect(() => {
    const onPointer = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(null);
    };
    document.addEventListener('pointerdown', onPointer);
    return () => { document.removeEventListener('pointerdown', onPointer); cancelClose(); };
  }, []);
  useEffect(() => { setOpen(null); }, [pageId]);

  return <LayoutGroup id={id}><nav ref={root} className="desktop-navigation" aria-label="Panel"
    onMouseEnter={cancelClose} onMouseLeave={() => {
      cancelClose();
      // Keep keyboard focus in an open dropdown until the user moves it away.
      if (!root.current?.contains(document.activeElement)) closeTimer.current = setTimeout(() => setOpen(null), 150);
    }}
    onBlur={(event) => { if (!event.currentTarget.contains(event.relatedTarget)) close(); }}
    onKeyDown={(event) => {
      if (event.key === 'Escape' && open) {
        event.preventDefault();
        root.current?.querySelector<HTMLButtonElement>(`[data-group="${open}"]`)?.focus();
        close();
      }
    }}>
    <div className="desktop-nav-links">
      {(['Overview', 'Clubs'] as Group[]).flatMap((group) => menu(group)).map((page) => <a key={page.id}
        className="desktop-nav-link" href={href(page.path)} aria-current={page.id === pageId ? 'page' : undefined}
        onMouseEnter={close} onClick={close}><NavIcon page={page.id} />{page.label}</a>)}
      {(['Content', 'Operations'] as Group[]).map((group) => {
        const active = menu(group).some((page) => page.id === pageId);
        return <button key={group} type="button" className="mega-trigger" data-group={group}
          aria-expanded={open === group} aria-controls={`${id}-${group}`} data-active={active || undefined}
          onMouseEnter={() => { cancelClose(); if (open) setOpen(group); }}
          onClick={() => { cancelClose(); setOpen((current) => current === group ? null : group); }}
          onKeyDown={(event) => {
            if (event.key === 'ArrowDown') {
              event.preventDefault(); cancelClose(); setOpen(group);
              requestAnimationFrame(() => document.getElementById(`${id}-${group}`)?.querySelector<HTMLAnchorElement>('a')?.focus());
            }
          }}>
          {(open === group || active) && <motion.span className="nav-active" aria-hidden="true"
            {...(!reduced && open === group && { layoutId: 'mega-indicator' })} transition={transition} />}
          {group}<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><path d="m6 9 6 6 6-6" /></svg>
        </button>;
      })}
    </div>
    <SearchButton onSearch={() => { close(); onSearch(); }} />
    {open && <motion.div key={open} className="mega-menu" id={`${id}-${open}`} role="group" aria-label={open}
      initial={{ opacity: 0, y: reduced ? 0 : -6 }} animate={{ opacity: 1, y: 0 }} transition={transition}>
      <div className="mega-menu-heading"><span>{open}</span><span>Manage your Aurora 21 server</span></div>
      <div className="mega-menu-grid">
        {menu(open).map((page) => <a key={page.id} href={href(page.path)} aria-current={page.id === pageId ? 'page' : undefined}
          onClick={close}><span className="mega-menu-icon"><NavIcon page={page.id} /></span>
          <span><strong>{page.label}</strong><small>{PAGE_DESCRIPTIONS[page.id]}</small></span>
        </a>)}
      </div>
    </motion.div>}
  </nav></LayoutGroup>;
}

function PanelNavigation({ pageId, onNavigate }: { pageId: string | null; onNavigate?: () => void }) {
  const id = useId();
  const { reduced, transition } = usePanelMotion();
  const [hovered, setHovered] = useState<string | null>(null);
  return <LayoutGroup id={id}><nav className="panel-navigation" aria-label="Panel" onMouseLeave={() => setHovered(null)}>
    {GROUPS.map((group) => <div className="nav-group" key={group} role="group" aria-label={group}>
      <h2>{group}</h2>
      {menu(group).map((page) => <a key={page.id} href={href(page.path)}
        onClick={onNavigate} onMouseEnter={() => setHovered(page.id)} onFocus={() => setHovered(page.id)} onBlur={() => setHovered(null)}
        aria-current={page.id === pageId ? 'page' : undefined}>
        <AnimatePresence>{hovered === page.id && page.id !== pageId && <motion.span className="nav-hover" aria-hidden="true"
          initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }}
          {...(!reduced && { layoutId: 'hover-page' })} transition={transition} />}</AnimatePresence>
        {page.id === pageId && <motion.span className="nav-active" aria-hidden="true"
          {...(!reduced && { layoutId: 'active-page' })} transition={transition} />}
        <NavIcon page={page.id} /><span>{page.label}</span>
      </a>)}
    </div>)}
  </nav></LayoutGroup>;
}

function NavIcon({ page }: { page: string }) {
  const paths: Record<string, string> = {
    dashboard: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
    players: 'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 3a4 4 0 1 0 0 8a4 4 0 0 0 0-8 M18 8a3 3 0 0 1 0 6 M22 21v-2a4 4 0 0 0-3-4',
    clubs: 'M12 3l8 3v6c0 5-8 9-8 9s-8-4-8-9V6z M8 10h8 M12 7v9',
    promos: 'M3 4h9l9 9-8 8-10-10z M7 8h.01',
    events: 'M4 5h16v16H4z M8 3v4 M16 3v4 M4 10h16 M8 14h3 M14 14h2',
    schedule: 'M4 5h16v16H4z M8 3v4 M16 3v4 M4 10h16 M8 14h3 M14 14h2',
    modes: 'M8 4h8v5a4 4 0 0 1-8 0z M8 6H5a3 3 0 0 0 3 4 M16 6h3a3 3 0 0 1-3 4 M12 13v4 M8 21h8 M10 17h4',
    cards: 'M5 3h14v15l-7 3-7-3z M9 7h6 M9 11h6 M9 15h3',
    packs: 'M4 5h16v15H4z M4 9h16 M9 5V3h6v2 M12 12v5',
    rules: 'M4 6h16 M4 12h16 M4 18h16 M8 3v6 M16 9v6 M10 15v6',
    sbc: 'M12 3l7 3v6c0 5-7 9-7 9s-7-4-7-9V6z M9 12l2 2 4-4',
    objectives: 'M12 3a9 9 0 1 0 0 18a9 9 0 0 0 0-18 M12 7a5 5 0 1 0 0 10a5 5 0 0 0 0-10 M12 11v2 M11 12h2',
    changes: 'M5 4h14v16H5z M9 8h6 M9 12h6 M9 16h3',
    access: 'M12 3l8 3v6c0 5-8 9-8 9s-8-4-8-9V6z M8 12l3 3 5-6',
    matchmaking: 'M4 7h16 M16 3l4 4-4 4 M20 17H4 M8 13l-4 4 4 4',
    leavers: 'M10 4H5v16h5 M15 8l4 4-4 4 M19 12H9',
    'market-log': 'M4 20V10 M10 20V4 M16 20v-7 M3 20h18',
    logs: 'M4 5h16 M4 10h10 M4 15h16 M4 20h10',
  };
  return <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[page]} /></svg>;
}

function Page({ resolved, params }: { resolved: Resolved; params: URLSearchParams }) {
  if (resolved.kind === 'page' && resolved.page.id === 'schedule') return <SchedulePage />;
  if (resolved.kind === 'redirect') return null;
  if (resolved.kind === 'page' && resolved.page.id === 'kit') return <Kit />;
  if (resolved.kind === 'page' && resolved.page.id === 'cards') return <CardsPage rest={resolved.rest} params={params} />;
  if (resolved.kind === 'page' && resolved.page.id === 'promos') return <PromosPage rest={resolved.rest} params={params} />;
  if (resolved.kind === 'page' && resolved.page.id === 'packs') return <PacksPage rest={resolved.rest} params={params} />;
  if (resolved.kind === 'page' && resolved.page.id === 'modes') return <ModesPage rest={resolved.rest} />;
  if (resolved.kind === 'page' && resolved.page.id === 'rules') return <RulesPage rest={resolved.rest} params={params} />;
  if (resolved.kind === 'page' && resolved.page.id === 'sbc') return <SbcPage rest={resolved.rest} />;
  if (resolved.kind === 'page' && resolved.page.id === 'objectives') return <ObjectivesPage rest={resolved.rest} />;
  if (resolved.kind === 'page' && resolved.page.id === 'changes') return <ChangesPage tab={resolved.rest[0]} />;
  if (resolved.kind === 'page' && resolved.page.id === 'dashboard') return <DashboardPage />;
  if (resolved.kind === 'page' && resolved.page.id === 'access') return <AccessPage />;
  if (resolved.kind === 'page' && resolved.page.id === 'matchmaking') return <MatchmakingPage />;
  if (resolved.kind === 'page' && resolved.page.id === 'leavers') return <LeaversPage />;
  if (resolved.kind === 'page' && resolved.page.id === 'market-log') return <MarketLogPage />;
  if (resolved.kind === 'page' && resolved.page.id === 'logs') return <LogsPage {...(resolved.rest[0] && { file: resolved.rest[0] })} />;
  if (resolved.kind === 'page' && resolved.page.id === 'clubs') {
    const [persona, tab] = resolved.rest;
    return persona && /^\d+$/.test(persona) ? <ClubPage persona={Number(persona)} tab={tab} basePath="clubs" /> : <PlayersList clubsOnly />;
  }
  if (resolved.kind === 'page' && resolved.page.id === 'players') {
    const [persona, tab] = resolved.rest;
    return persona && /^\d+$/.test(persona) ? <ClubPage persona={Number(persona)} tab={tab} /> : <PlayersList />;
  }
  return <NotBuilt resolved={resolved} />;
}

function SearchButton({ onSearch }: { onSearch: () => void }) {
  const mac = typeof navigator !== 'undefined' && /Mac/i.test(navigator.platform);
  return <button type="button" className="search-button" onClick={onSearch} aria-label="Search players, cards, packs">
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true"><circle cx="10" cy="10" r="6" /><path d="m15 15 6 6" /></svg>
    <span>Search</span><kbd>{mac ? '⌘' : 'Ctrl'} K</kbd>
  </button>;
}

function StatusBar() {
  const [drawer, setDrawer] = useState(false);
  const dashboard = useQuery({
    queryKey: ['dashboard'],
    queryFn: () => api<DashboardReply>('dashboard'),
    refetchInterval: STATUS_POLL_MS,
  });
  const changes = useChangeset(STATUS_POLL_MS);
  const services = dashboard.data?.services;
  const online = dashboard.data?.online?.count;
  const pending = Number(changes.data?.pending) || 0;
  return (
    <footer className="statusbar" aria-label="Server status">
      <StatusDot state={services?.blaze}>Blaze</StatusDot>
      <StatusDot state={services?.fut}>FUT</StatusDot>
      <span className="status">{online === undefined ? 'Online: not reported' : `${online} online`}</span>
      {services?.blazeAccess?.maintenance === true && (
        <Badge tone="flare" href={href('access')}>Maintenance on</Badge>
      )}
      {(dashboard.data?.debug?.active || dashboard.data?.debug?.unknown) && (
        <Badge tone="flare" href={href('dashboard')}>{dashboard.data.debug.active ? 'Test mode' : 'Test mode?'}</Badge>
      )}
      {pending > 0 && (
        <button type="button" className="pending" onClick={() => setDrawer(true)} aria-haspopup="dialog">
          <span className="pending-count" aria-hidden="true">{pending}</span>
          <span>{`${pending} pending ${pending === 1 ? 'change' : 'changes'}`}</span>
          <span aria-hidden="true">→</span>
        </button>
      )}
      {dashboard.isError && <span className="status error">Status: {dashboard.error.message}</span>}
      <PendingDrawer open={drawer} onOpenChange={setDrawer} reply={changes.data} />
    </footer>
  );
}

function NotBuilt({ resolved }: { resolved: Resolved }) {
  const known = resolved.kind === 'page';
  return (
    <>
      <PageHeader title={known ? resolved.page.label : 'No page here'} />
      <EmptyState
        title={known ? 'This page is not available yet' : 'This address is not a page'}
        text={known ? 'This page is planned. Use the menu to open an available page.' : 'Pick a page from the menu, or search with Ctrl K.'}
        action={<LinkButton href={href('dashboard')}>Go to the dashboard</LinkButton>}
      />
    </>
  );
}
