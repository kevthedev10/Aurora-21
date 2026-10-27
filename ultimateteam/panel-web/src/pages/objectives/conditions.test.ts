import { describe, expect, it } from 'vitest';
import type { GrindObjective } from '../../api/objectives';
import { conditionsProblem, conditionsText } from './ObjectiveConditionsEditor';

const modes = ['rivals', 'weekendLeague', 'squadBattles', 'draftOnline', 'draftSinglePlayer'];
const filters = { leagues: [{ id: 13, name: 'Premier League' }], nations: [{ id: 14, name: 'England' }], clubs: [{ id: 1, name: 'Arsenal', leagueName: 'Premier League' }] };
const objective = (changes: Partial<GrindObjective>): GrindObjective => ({ id: 1, name: 'Test', stat: 'goals', target: 5, xp: 0, award: { coins: 1 }, ...changes });

describe('objective conditions', () => {
  it('reads as the in-game text', () => {
    expect(conditionsText(objective({ requirements: { modes: ['rivals', 'weekendLeague'], player: { leagueId: 13 } } }), modes, filters))
      .toBe('Score {n} goals with Premier League players in Division Rivals or Aurora Champions matches.');
    expect(conditionsText(objective({ stat: 'wins', requirements: { modes, startingSquad: { nation: 14, minimumCount: 3 } } }), modes, filters))
      .toBe('Win {n} FUT matches, with at least 3 England players in your starting XI.');
    expect(conditionsText(objective({ stat: 'scoringMatches', requirements: { modes: ['squadBattles'], player: { clubId: 1 }, startingSquad: { clubId: 1, exactCount: 1 } } }), modes, filters))
      .toBe('Score at least one goal with Arsenal players in {n} separate Squad Battles matches, with exactly 1 Arsenal player in your starting XI.');
  });

  it('refuses conditions the server would refuse', () => {
    expect(conditionsProblem(objective({ requirements: { modes: [] } }), ['goals'])).toMatch(/at least one mode/);
    expect(conditionsProblem(objective({ requirements: { modes, player: {} } }), ['goals'])).toMatch(/league, nation or club/);
    expect(conditionsProblem(objective({ stat: 'wins', requirements: { modes, player: { leagueId: 13 } } }), ['goals'])).toMatch(/goals, assists/);
    expect(conditionsProblem(objective({ requirements: { modes, startingSquad: { minimumCount: 3 } } }), ['goals'])).toMatch(/starting XI/);
    expect(conditionsProblem(objective({ requirements: { modes, player: { leagueId: 13 } } }), ['goals'])).toBeNull();
  });
});
