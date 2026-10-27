// An objective's match conditions (rules/objectives.json requirements, counted by ObjectiveMatchProgress.cs): the modes whose
// matches count, which players' goals or assists count ("Score 5 goals with Premier League players"), and how many starters
// must match ("with at least 3 Spain players in your starting XI"). Also writes the in-game text these conditions read as.
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import type { GrindObjective, ObjectivePlayerRestriction, ObjectiveRequirements, ObjectivesReply } from '../../api/objectives';
import { Field } from '../../ui/Field';
import { SwitchRow } from '../../ui/Switch';
import { SearchFilter } from '../cards/SearchFilter';

export const modeNames: Record<string, string> = {
  rivals: 'Division Rivals', weekendLeague: 'Aurora Champions', squadBattles: 'Squad Battles',
  draftOnline: 'Online Draft', draftSinglePlayer: 'Single-player Draft',
};

const fallbackConditions = { modes: Object.keys(modeNames), stats: ['assists', 'cleanSheets', 'goals', 'matches', 'scoringMatches', 'wins'], playerStats: ['assists', 'goals', 'scoringMatches'] };
export const conditionsOf = (reply: ObjectivesReply) => reply.conditions ?? fallbackConditions;

interface Filters { leagues: { id: number; name: string }[]; nations: { id: number; name: string }[]; clubs: { id: number; name: string; leagueName: string }[] }
export const useCardFilters = () => useQuery({ queryKey: ['cards-filters'], queryFn: () => api<Filters>('cards/filters'), staleTime: Infinity });

const isEmpty = (restriction: ObjectivePlayerRestriction | undefined) => !restriction || (!restriction.leagueId && !restriction.nation && !restriction.clubId);

/** "Premier League Spain players" / "Arsenal players" / "players". */
function playersPhrase(restriction: ObjectivePlayerRestriction | undefined, filters: Filters | undefined, count = 2) {
  const name = (list: { id: number; name: string }[] | undefined, id: number | undefined, kind: string) => id ? list?.find((entry) => entry.id === id)?.name ?? `${kind} ${id}` : null;
  const words = [name(filters?.clubs, restriction?.clubId, 'club'), name(filters?.leagues, restriction?.leagueId, 'league'), name(filters?.nations, restriction?.nation, 'nation')].filter(Boolean);
  return `${words.join(' ')}${words.length ? ' ' : ''}${count === 1 ? 'player' : 'players'}`;
}

/** The in-game text the conditions read as; {n} is the target. */
export function conditionsText(objective: GrindObjective, modes: string[], filters: Filters | undefined): string {
  const req = objective.requirements;
  if (!req) return '';
  const all = modes.every((mode) => req.modes.includes(mode));
  const where = all ? 'FUT' : req.modes.map((mode) => modeNames[mode] ?? mode).join(' or ');
  const with_ = isEmpty(req.player) ? '' : ` with ${playersPhrase(req.player, filters)}`;
  const base = ({
    matches: `Play {n} ${where} matches`,
    wins: `Win {n} ${where} matches`,
    goals: `Score {n} goals${with_} in ${where} matches`,
    assists: `Assist {n} goals${with_} in ${where} matches`,
    scoringMatches: `Score at least one goal${with_} in {n} separate ${where} matches`,
    cleanSheets: `Keep {n} clean sheets in ${where} matches`,
  } as Record<string, string>)[objective.stat] ?? `Reach {n} ${objective.stat} in ${where} matches`;
  const squad = req.startingSquad;
  const count = squad?.exactCount ?? squad?.minimumCount ?? 0;
  const xi = squad && !isEmpty(squad) && (squad.exactCount != null || count > 0)
    ? `, with ${squad.exactCount != null ? 'exactly' : 'at least'} ${count} ${playersPhrase(squad, filters, count)} in your starting XI` : '';
  return `${base}${xi}.`;
}

/** Why the conditions cannot be saved, or null. */
export function conditionsProblem(objective: GrindObjective, playerStats: string[]): string | null {
  const req = objective.requirements;
  if (!req) return null;
  if (!req.modes.length) return 'Pick at least one mode the objective counts in.';
  if (req.player && (isEmpty(req.player) || !playerStats.includes(objective.stat))) return 'A "counted players" condition needs a league, nation or club and a goals, assists or scoring stat.';
  if (req.startingSquad && isEmpty(req.startingSquad)) return 'The starting XI condition needs a league, nation or club.';
  return null;
}

/** Drops empty restriction keys so the file stays as the server reads it. */
const clean = (restriction: ObjectivePlayerRestriction): ObjectivePlayerRestriction =>
  Object.fromEntries(Object.entries(restriction).filter(([, value]) => value != null && value !== 0)) as ObjectivePlayerRestriction;

function RestrictionPickers({ value, onChange, filters, label }: { value: ObjectivePlayerRestriction; onChange: (value: ObjectivePlayerRestriction) => void; filters: Filters | undefined; label: string }) {
  const pick = (key: keyof ObjectivePlayerRestriction) => (option: { value: string } | null) => onChange(clean({ ...value, [key]: option ? Number(option.value) : undefined }));
  return <>
    <SearchFilter label={`${label} league`} value={String(value.leagueId ?? '')} options={(filters?.leagues ?? []).map((entry) => ({ value: String(entry.id), label: entry.name }))} onChange={pick('leagueId')} />
    <SearchFilter label={`${label} nation`} value={String(value.nation ?? '')} options={(filters?.nations ?? []).map((entry) => ({ value: String(entry.id), label: entry.name }))} onChange={pick('nation')} />
    <SearchFilter label={`${label} club`} value={String(value.clubId ?? '')} options={(filters?.clubs ?? []).map((entry) => ({ value: String(entry.id), label: `${entry.name} (${entry.leagueName})` }))} onChange={pick('clubId')} />
  </>;
}

export function ObjectiveConditionsEditor({ objective, reply, onChange }: {
  objective: GrindObjective;
  reply: ObjectivesReply;
  onChange: (changes: Partial<GrindObjective>) => void;
}) {
  const conditions = conditionsOf(reply);
  const filters = useCardFilters();
  const req = objective.requirements;
  const setReq = (next: ObjectiveRequirements | undefined) => {
    const changes: Partial<GrindObjective> = { requirements: next };
    // A conditioned objective tracks a stat the conditions can count; a player restriction a per-player one.
    if (next && !conditions.stats.includes(objective.stat)) changes.stat = 'matches';
    if (next?.player && !conditions.playerStats.includes(changes.stat ?? objective.stat)) changes.stat = 'goals';
    onChange(changes);
  };
  const squad = req?.startingSquad;
  const exact = squad?.exactCount != null;
  const special = req?.goalType ?? req?.assistType;
  return (
    <div className="objective-conditions">
      <SwitchRow label="Match conditions" checked={!!req} onChange={(on) => setReq(on ? { modes: [...conditions.modes] } : undefined)}
        text="Only count matches in chosen modes, goals by chosen players, or matches started with enough chosen players." />
      {req && <>
        <fieldset className="objective-condition-group">
          <legend>Counts in</legend>
          <div className="objective-condition-modes">
            {conditions.modes.map((mode) => <label key={mode} className="objective-condition-check"><input type="checkbox" checked={req.modes.includes(mode)}
              onChange={(event) => setReq({ ...req, modes: event.target.checked ? conditions.modes.filter((entry) => entry === mode || req.modes.includes(entry)) : req.modes.filter((entry) => entry !== mode) })} />{modeNames[mode] ?? mode}</label>)}
          </div>
        </fieldset>
        <fieldset className="objective-condition-group">
          <legend>Counted players</legend>
          <SwitchRow label="Only these players' goals or assists count" checked={!!req.player}
            onChange={(on) => { const next = { ...req }; if (on) next.player = {}; else delete next.player; setReq(next); }}
            text={'For example "Score 5 goals with Premier League players". Works with goals, assists and scoring in separate matches.'} />
          {req.player && <div className="form-row"><RestrictionPickers label="Player" value={req.player} filters={filters.data} onChange={(player) => setReq({ ...req, player })} /></div>}
        </fieldset>
        <fieldset className="objective-condition-group">
          <legend>Starting XI</legend>
          <SwitchRow label="Starting XI must include these players" checked={!!squad}
            onChange={(on) => { const next = { ...req }; if (on) next.startingSquad = { minimumCount: 1 }; else delete next.startingSquad; setReq(next); }}
            text="The match only counts when enough of the 11 starters match." />
          {squad && <div className="form-row">
            <RestrictionPickers label="Starter" value={squad} filters={filters.data} onChange={(value) => setReq({ ...req, startingSquad: { ...value, ...(exact ? { exactCount: squad.exactCount! } : { minimumCount: squad.minimumCount ?? 1 }) } })} />
            <Field label="How many">
              <select value={exact ? 'exact' : 'min'} onChange={(event) => {
                const count = squad.exactCount ?? squad.minimumCount ?? 1;
                const { exactCount: _exact, minimumCount: _min, ...rest } = squad;
                setReq({ ...req, startingSquad: event.target.value === 'exact' ? { ...rest, exactCount: Math.max(1, count) } : { ...rest, minimumCount: count } });
              }}>
                <option value="min">At least</option><option value="exact">Exactly</option>
              </select>
            </Field>
            <Field label="Starters"><input type="number" min={exact ? 1 : 0} max={11} value={squad.exactCount ?? squad.minimumCount ?? 0} onChange={(event) => {
              const count = Math.max(exact ? 1 : 0, Math.min(11, Number(event.target.value) || 0));
              setReq({ ...req, startingSquad: exact ? { ...squad, exactCount: count } : { ...squad, minimumCount: count } });
            }} /></Field>
          </div>}
        </fieldset>
        {special && <p className="notice">This objective counts only {special.replace('_', ' ')} {req.goalType ? 'goals' : 'assists'}. Special scoring needs a verified native event mapping before the group can go live.</p>}
      </>}
    </div>
  );
}
