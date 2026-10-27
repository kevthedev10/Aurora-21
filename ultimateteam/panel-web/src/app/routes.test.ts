import { describe, expect, it } from 'vitest';
import { GROUPS, PAGES, menu, pageTitle, resolve } from './routes';
import { href, parseHash } from './router';

const at = (hash: string) => { const route = parseHash(hash); return resolve(route.parts, route.params); };

describe('routes', () => {
  it('parses the hash into parts and query', () => {
    const route = parseHash('#/players/42?pile=new');
    expect(route.parts).toEqual(['players', '42']);
    expect(route.params.get('pile')).toBe('new');
    expect(parseHash('').parts).toEqual([]);
    expect(href('players', 42)).toBe('#/players/42');
  });

  it('sends every classic route name to its new page', () => {
    expect(at('#/')).toEqual({ kind: 'redirect', to: 'dashboard' });
    expect(at('#/packcatalog')).toEqual({ kind: 'redirect', to: 'packs/catalogue' });
    expect(at('#/packcatalog?edit=7')).toEqual({ kind: 'redirect', to: 'packs/catalogue/7' });
    expect(at('#/events')).toEqual({ kind: 'redirect', to: 'schedule?section=rotations' });
    expect(at('#/store')).toEqual({ kind: 'redirect', to: 'packs/lineup' });
    // One list holds every pack and pick: the old Picks tab and the short-lived Store/Reward tabs open it.
    expect(at('#/packs/picks')).toEqual({ kind: 'redirect', to: 'packs/catalogue?type=picks' });
    expect(at('#/packs/reward/310')).toEqual({ kind: 'redirect', to: 'packs/catalogue/310' });
    expect(at('#/packs/store/new?type=picks')).toEqual({ kind: 'redirect', to: 'packs/catalogue/new?type=picks' });
    expect(at('#/packs/catalogue/7')).toMatchObject({ kind: 'page', page: { id: 'packs' }, rest: ['catalogue', '7'] });
    expect(at('#/history')).toEqual({ kind: 'redirect', to: 'changes/history' });
    expect(at('#/maintenance')).toEqual({ kind: 'redirect', to: 'access' });
    // #/modes is the Modes page now; the classic per-club modes view is Players → club → Modes.
    expect(at('#/modes/champions')).toMatchObject({ kind: 'page', page: { id: 'modes' }, rest: ['champions'] });
    // Classic #/packs was Pack gifts; here it is the Packs page, of which gifts are a tab.
    expect(at('#/packs')).toMatchObject({ kind: 'page', page: { id: 'packs' } });
    expect(at('#/nowhere')).toEqual({ kind: 'missing' });
    expect(at('#/settings')).toEqual({ kind: 'redirect', to: 'access' });
  });

  it('builds the full menu in four groups, with the kit hidden', () => {
    expect(GROUPS.map((group) => menu(group).map((page) => page.label))).toEqual([
      ['Dashboard'], ['Players / Clubs'], ['Schedule', 'Cards', 'Packs / Picks', 'Promos', 'Modes', 'SBCs', 'Objectives', 'Rules', 'Changes'], ['Access & maintenance', 'Matchmaking', 'Leavers', 'Market log', 'Logs'],
    ]);
    expect(PAGES.find((page) => page.id === 'kit')?.group).toBeUndefined();
    expect(pageTitle(at('#/players/1'))).toBe('Players / Clubs · Aurora 21');
    expect(at('#/objectives')).toMatchObject({ kind: 'page', page: { id: 'objectives', group: 'Content' } });
  });
});
