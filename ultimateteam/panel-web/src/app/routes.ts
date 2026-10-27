// Panel pages and navigation share one table. Legacy hash routes redirect below.

export type PageId = 'dashboard' | 'players' | 'clubs' | 'schedule' | 'cards' | 'packs' | 'promos' | 'modes' | 'sbc' | 'objectives' | 'rules' | 'changes' | 'access' | 'matchmaking' | 'leavers' | 'market-log' | 'logs' | 'kit';
export type Group = 'Overview' | 'Clubs' | 'Content' | 'Operations';

export interface PageDef {
  id: PageId;
  /** The first part of the hash route: #/<path>/... */
  path: string;
  label: string;
  /** The menu group; pages without one (the kit) are reachable by address only. */
  group?: Group;
}

export const PAGES: PageDef[] = [
  { id: 'dashboard', path: 'dashboard', label: 'Dashboard', group: 'Overview' },
  { id: 'players', path: 'players', label: 'Players / Clubs', group: 'Clubs' },
  { id: 'clubs', path: 'clubs', label: 'Players / Clubs' },
  { id: 'schedule', path: 'schedule', label: 'Schedule', group: 'Content' },
  { id: 'cards', path: 'cards', label: 'Cards', group: 'Content' },
  { id: 'packs', path: 'packs', label: 'Packs / Picks', group: 'Content' },
  { id: 'promos', path: 'promos', label: 'Promos', group: 'Content' },
  { id: 'modes', path: 'modes', label: 'Modes', group: 'Content' },
  { id: 'sbc', path: 'sbc', label: 'SBCs', group: 'Content' },
  { id: 'objectives', path: 'objectives', label: 'Objectives', group: 'Content' },
  { id: 'rules', path: 'rules', label: 'Rules', group: 'Content' },
  { id: 'changes', path: 'changes', label: 'Changes', group: 'Content' },
  { id: 'access', path: 'access', label: 'Access & maintenance', group: 'Operations' },
  { id: 'matchmaking', path: 'matchmaking', label: 'Matchmaking', group: 'Operations' },
  { id: 'leavers', path: 'leavers', label: 'Leavers', group: 'Operations' },
  { id: 'market-log', path: 'market-log', label: 'Market log', group: 'Operations' },
  { id: 'logs', path: 'logs', label: 'Logs', group: 'Operations' },
  { id: 'kit', path: '_kit', label: 'Component kit' },
];

export const GROUPS: Group[] = ['Overview', 'Clubs', 'Content', 'Operations'];
export const menu = (group: Group) => PAGES.filter((page) => page.group === group);

/** Classic route names, sent to their new pages so old links and habits keep working. */
function redirect(parts: string[], params: URLSearchParams): string | null {
  const [first, second, third] = parts;
  switch (first) {
    case undefined: return 'dashboard';
    case 'packcatalog': {
      const edit = params.get('edit');
      return edit ? `packs/catalogue/${encodeURIComponent(edit)}` : 'packs/catalogue';
    }
    // One list holds every pack and pick: the former Picks tab and the short-lived Store/Reward tabs open it.
    case 'packs':
      if (second !== 'picks' && second !== 'store' && second !== 'reward') return null;
      return `packs/catalogue${third ? `/${encodeURIComponent(third)}` : ''}${second === 'picks' || params.get('type') === 'picks' ? '?type=picks' : ''}`;
    case 'store': return 'packs/lineup';
    case 'events': return 'schedule?section=rotations';
    case 'history': return 'changes/history';
    // The Settings page was removed: maintenance and the player cap are on Access.
    case 'maintenance': case 'settings': return 'access';
    default: return null;
  }
}

export type Resolved =
  | { kind: 'page'; page: PageDef; rest: string[] }
  | { kind: 'redirect'; to: string }
  | { kind: 'missing' };

export function resolve(parts: string[], params: URLSearchParams = new URLSearchParams()): Resolved {
  const to = redirect(parts, params);
  if (to) return { kind: 'redirect', to };
  const page = PAGES.find((candidate) => candidate.path === parts[0]);
  return page ? { kind: 'page', page, rest: parts.slice(1) } : { kind: 'missing' };
}

/** The browser tab title: "Players · Aurora 21". */
export const pageTitle = (resolved: Resolved) => `${resolved.kind === 'page' ? resolved.page.label : 'Not found'} · Aurora 21`;
