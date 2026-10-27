// One SBC set: its details and badge, then each challenge with its squad (formation and locked slots), requirements and
// rewards, and the set's group rewards. A preview beside it shows what FIFA will display and why staging would be refused,
// from the server's own checks and text (POST sbc/preview). Address: #/sbc/<key>, #/sbc/new, #/sbc/new/<key to copy>.
import { useEffect, useMemo, useState } from 'react';
import { badgeUrl, useSbcActions, useSbcPreview, type SbcBricks, type SbcChallengeDef, type SbcReply, type SbcRule, type SbcSetDef, type SbcSetRow } from '../../api/sbc';
import { href } from '../../app/router';
import { Badge } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { PageHeader, Section } from '../../ui/Layout';
import { SwitchRow } from '../../ui/Switch';
import { useToast } from '../../ui/Toast';
import { RequirementsEditor } from './RequirementsEditor';
import { RewardsEditor, rewardText } from './RewardsEditor';
import { formationName } from './SbcSettingsTab';
import { SquadEditor } from './SquadEditor';

const go = (path?: string) => { window.location.hash = path ? href('sbc', path) : href('sbc'); };

/** A challenge added in the editor: the server-safe id is assigned automatically (withIds), never typed. */
const AUTO = '__auto';
const emptyChallenge = (index: number): SbcChallengeDef => ({
  id: '', [AUTO]: true, name: index === 1 ? 'Challenge' : `Challenge ${index}`,
  requirements: { rules: [{ type: 'PLAYERS', attributes: { min: 11, max: 11 } }] }, rewards: { entries: [] },
});

/**
 * A new set's key, made from its name ("Bronze Upgrade" → BRONZE_UPGRADE), unique among the catalogue and staged sets.
 * Keys are permanent (clubs' progress and the live list use them), so nobody types one.
 */
export function keyFor(name: string, taken: Set<string>): string {
  const base = name.normalize('NFKD').replace(/[\u0300-\u036f]/g, '').toUpperCase().replace(/[^A-Z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 56) || 'SBC';
  let key = base;
  for (let n = 2; taken.has(key); n++) key = `${base}_${n}`;
  return key;
}

/**
 * The set with every automatic challenge id filled in: <key>_<n> with the smallest n no catalogue or staged challenge
 * uses (completions are stored by challenge id, so an id is never reused or changed), and the editor's marker removed.
 */
export function withIds(set: SbcSetDef, key: string, taken: Set<string>): SbcSetDef {
  const used = new Set([...taken, ...set.challenges.filter((challenge) => !challenge[AUTO]).map((challenge) => challenge.id)]);
  let n = 1;
  return {
    ...set,
    challenges: set.challenges.map((challenge) => {
      if (!challenge[AUTO]) return challenge;
      while (used.has(`${key}_${n}`)) n++;
      const id = `${key}_${n}`;
      used.add(id);
      const { [AUTO]: _, ...rest } = challenge;
      return { ...rest, id } as SbcChallengeDef;
    }),
  };
}

/** A copy to stage as a new set: no key, automatic challenge ids, and no numbers or derived fields. */
function copyOf(definition: SbcSetDef): SbcSetDef {
  const copy = structuredClone(definition);
  for (const field of ['id', 'challengeCount', 'challengeIds', 'image', 'state']) delete copy[field];
  copy.name = `${definition.name} (copy)`;
  copy.challenges = copy.challenges.map((challenge) => {
    const next: SbcChallengeDef = { ...challenge, id: '', [AUTO]: true };
    for (const field of ['number', 'order', 'retail']) delete next[field];
    return next;
  });
  return copy;
}

/** The PLAYERS rule set to the open slots, so FIFA's count and the server's check agree. */
function withPlayers(rules: SbcRule[], count: number): SbcRule[] {
  const rule: SbcRule = { type: 'PLAYERS', attributes: { min: count, max: count } };
  const at = rules.findIndex((entry) => entry.type.toUpperCase() === 'PLAYERS');
  return at >= 0 ? rules.map((entry, index) => index === at ? rule : entry) : [rule, ...rules];
}

export function SbcEditor({ data, existing, pending, copy, staged = [] }: {
  data: SbcReply;
  /** Sets staged as new but not published: their keys and challenge ids are taken too. */
  staged?: { key: string; definition?: SbcSetDef | undefined }[];
  existing: SbcSetRow | null;
  pending: { operation: string; definition: SbcSetDef | null } | null;
  /** A set to start a new one from (Duplicate). */
  copy?: SbcSetRow | undefined;
}) {
  const start = pending?.definition ?? existing?.definition;
  const [set, setSet] = useState<SbcSetDef>(() => start ? structuredClone(start) : copy ? copyOf(copy.definition) : {
    name: '', description: '', tabId: data.tabs[0]?.id ?? 0, repeatable: false, badge: '', challenges: [emptyChallenge(1)],
  });
  const taken = useMemo(() => ({
    keys: new Set([...data.sets.map((row) => row.key), ...staged.map((entry) => entry.key)]),
    ids: new Set([...data.sets, ...staged].flatMap((entry) => entry.definition?.challenges.map((challenge) => challenge.id) ?? [])),
  }), [data.sets, staged]);
  const isNew = !existing;
  const key = existing ? existing.key : set.name.trim() ? keyFor(set.name, taken.keys) : '';
  const resolved = useMemo(() => withIds(set, key || 'SBC', taken.ids), [set, key, taken.ids]);
  const [showBadges, setShowBadges] = useState(false);
  const [debounced, setDebounced] = useState(resolved);
  useEffect(() => { const timer = window.setTimeout(() => setDebounced(resolved), 400); return () => window.clearTimeout(timer); }, [resolved]);
  const preview = useSbcPreview(key, debounced);
  const actions = useSbcActions();
  const toast = useToast();
  const hasGroupReward = set.challenges.length >= 2;
  const groupRewardProblem = hasGroupReward && !(set.rewards?.entries.length)
    ? 'A group reward is required for an SBC with two or more challenges.' : null;
  const canSave = !!key && set.name.trim().length > 0 && set.challenges.length > 0 && !groupRewardProblem && !actions.isPending;
  const patch = (changes: Partial<SbcSetDef>) => setSet((current) => ({ ...current, ...changes }));
  const patchChallenge = (index: number, changes: Partial<SbcChallengeDef>) =>
    setSet((current) => ({ ...current, challenges: current.challenges.map((challenge, at) => at === index ? { ...challenge, ...changes } : challenge) }));
  const move = (index: number, by: number) => setSet((current) => {
    const challenges = [...current.challenges];
    const [moved] = challenges.splice(index, 1);
    challenges.splice(index + by, 0, moved!);
    return { ...current, challenges };
  });
  const setSquad = (index: number, change: { formation?: string | undefined; bricks?: SbcBricks | undefined }) => {
    const challenge = set.challenges[index]!;
    const next: SbcChallengeDef = { ...challenge };
    if ('formation' in change) { if (change.formation) next.formation = change.formation; else delete next.formation; }
    if ('bricks' in change) {
      if (change.bricks) next.bricks = change.bricks; else delete next.bricks;
      next.requirements = { rules: withPlayers(challenge.requirements?.rules ?? [], change.bricks ? change.bricks.freeIndices.length : data.squadSize) };
    }
    setSet((current) => ({ ...current, challenges: current.challenges.map((entry, at) => at === index ? next : entry) }));
  };

  const save = () => {
    if (!canSave) return;
    const definition: SbcSetDef = structuredClone(resolved);
    if (!definition.badge) delete definition.badge;
    if (!definition.description) delete definition.description;
    if (!definition.rewards?.entries.length) delete definition.rewards;
    if (!definition.repeatable) delete definition.rewardsTradeable;
    actions.mutate({ key, operation: isNew ? 'insert' : 'update', definition }, {
      onSuccess: () => { toast(isNew ? `${set.name} saved. Add it to Live SBCs or a scheduled drop to show it in the game.` : `${set.name} saved.`, 'done'); go(); },
      onError: (error) => toast(error.message, 'error'),
    });
  };

  const tabName = data.tabs.find((tab) => tab.id === set.tabId)?.name ?? `Tab ${set.tabId}`;
  const shownPreview = preview.data;
  return (
    <>
      <PageHeader title={isNew ? (copy ? `Copy of ${copy.definition.name}` : set.name || 'New SBC') : set.name || existing.definition.name || existing.key} crumbs={<a href={href('sbc')}>SBCs</a>}
        description={isNew ? 'Name it, choose its tab and badge, then build each challenge: its squad, requirements and rewards.'
          : existing.origin === 'bundled' ? 'From the bundled catalogue: saving stores your version in its place.' : 'Added in the panel.'}
        actions={<>
          <LinkButton href={href('sbc')}>Cancel</LinkButton>
          {existing && <LinkButton href={href('sbc', 'new', existing.key)}>Duplicate</LinkButton>}
          <Button variant="staged" disabled={!canSave} onClick={save}>Save changes</Button>
        </>} />
      {existing && !(existing.listed && existing.enabled) && <p className="notice">{existing.listed ? 'This SBC is on Live SBCs but switched off' : 'This SBC is not on Live SBCs'}, so the game does not show it. <a href={href('sbc', 'live')}>Live SBCs</a></p>}
      <div className="sbc-editor">
        <div className="sbc-editor-main">
          <Section title="Set">
            <div className="sbc-form">
              <Field label="Key" hint={isNew ? 'Made from the name. It is permanent once saved.' : 'Permanent: clubs’ progress and Live SBCs use it.'}>
                <input value={key || 'Name the SBC first'} readOnly disabled />
              </Field>
              <Field label="Name"><input value={set.name} maxLength={80} onChange={(event) => patch({ name: event.target.value })} /></Field>
              <Field label="Game SBC tab">
                <select value={set.tabId} onChange={(event) => patch({ tabId: Number(event.target.value) })}>
                  {data.tabs.map((tab) => <option key={tab.id} value={tab.id}>{tab.name}</option>)}
                </select>
              </Field>
            </div>
            <div className="sbc-form sbc-form-wide">
              <Field label="Description" hint="Leave empty for the standard description (see the preview).">
                <textarea rows={2} maxLength={400} value={set.description ?? ''} onChange={(event) => patch({ description: event.target.value })} />
              </Field>
            </div>
            <div className="sbc-form">
              <Field label="Repeatability">
                <select value={set.repeatable && !set.repeatableDaily && !set.maxCompletions ? 'unlimited' : 'limited'} onChange={event => patch({ repeatable: event.target.value === 'unlimited', repeatableDaily: false, maxCompletions: undefined })}>
                  <option value="limited">Set a limit</option><option value="unlimited">Unlimited</option>
                </select>
              </Field>
              {(!set.repeatable || set.repeatableDaily || !!set.maxCompletions) && <Field label="Completion limit" hint={set.repeatableDaily ? 'Completions per account each day.' : 'Total completions per account.'}>
                <input type="number" min={1} max={10000} step={1} value={set.maxCompletions ?? 1} onChange={event => {
                  const count = Math.max(1, Number(event.target.value) || 1);
                  patch({ repeatable: count > 1 || !!set.repeatableDaily, maxCompletions: count > 1 || set.repeatableDaily ? count : undefined });
                }} />
              </Field>}
              {(!set.repeatable || set.repeatableDaily || !!set.maxCompletions) && <SwitchRow label="Reset daily" checked={!!set.repeatableDaily} onChange={daily => {
                const count = set.maxCompletions ?? 1;
                patch({ repeatableDaily: daily, repeatable: daily || count > 1, maxCompletions: daily || count > 1 ? count : undefined });
              }} text="Daily resets at 7pm London. All challenges must be completed in the same day to earn each group reward." />}
            </div>
            <div className="sbc-switches">
              {set.repeatable && <SwitchRow label="Rewards tradeable" checked={!!set.rewardsTradeable} onChange={(rewardsTradeable) => patch({ rewardsTradeable })}
                text="A repeatable SBC pays untradeable rewards (SBCs → Settings). Switch on only for a set that paid tradeable rewards in retail." />}
            </div>
            <div className="sbc-badge-row">
              <img className="sbc-badge-preview" src={badgeUrl(set.badge || key || `tab-${set.tabId}`)} alt="" onError={(event) => { event.currentTarget.src = badgeUrl(`tab-${set.tabId}`); }} />
              <div>
                <strong>Badge</strong>
                <small>{set.badge ? set.badge : 'The set key’s own art, else its tab’s badge.'}</small>
                <Button size="sm" onClick={() => setShowBadges(!showBadges)}>{showBadges ? 'Close badges' : 'Choose badge'}</Button>
                {set.badge && <Button size="sm" variant="ghost" onClick={() => patch({ badge: '' })}>Use default</Button>}
              </div>
            </div>
            {showBadges && (
              <ul className="sbc-badge-picker" aria-label="SBC badges">
                {data.badges.map((name) => (
                  <li key={name}>
                    <button type="button" aria-pressed={set.badge === name} title={name} onClick={() => { patch({ badge: name }); setShowBadges(false); }}>
                      <img src={badgeUrl(name)} alt="" loading="lazy" /><span>{name}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </Section>

          {set.challenges.map((challenge, index) => {
            const bricks = challenge.bricks as SbcBricks | undefined;
            return (
              <Section key={index} title={`Challenge ${index + 1}: ${challenge.name || 'Untitled'}`} className="sbc-challenge"
                actions={<>
                  <Button size="sm" variant="ghost" disabled={index === 0} aria-label={`Move challenge ${index + 1} up`} onClick={() => move(index, -1)}>↑</Button>
                  <Button size="sm" variant="ghost" disabled={index === set.challenges.length - 1} aria-label={`Move challenge ${index + 1} down`} onClick={() => move(index, 1)}>↓</Button>
                  {set.challenges.length > 1 && <Button size="sm" variant="danger" onClick={() => patch({ challenges: set.challenges.filter((_, at) => at !== index) })}>Remove</Button>}
                </>}>
                <div className="sbc-form">
                  <Field label="Name"><input value={challenge.name} maxLength={80} onChange={(event) => patchChallenge(index, { name: event.target.value })} /></Field>
<Field label="Challenge id" hint={challenge[AUTO] ? 'Assigned automatically when saved.' : 'Permanent: clubs’ completions are stored by it.'}>
                    <input value={resolved.challenges[index]?.id ?? ''} readOnly disabled />
                  </Field>
                </div>
                <div className="sbc-form sbc-form-wide">
                  <Field label="Description" hint="Optional; FIFA shows the requirements either way.">
                    <textarea rows={2} maxLength={400} value={challenge.description ?? ''}
                      onChange={(event) => setSet((current) => ({ ...current, challenges: current.challenges.map((entry, at) => {
                        if (at !== index) return entry;
                        const next: SbcChallengeDef = { ...entry, description: event.target.value };
                        if (!event.target.value) delete next.description;
                        return next;
                      }) }))} />
                  </Field>
                </div>
                <h4 className="sbc-subhead">Squad</h4>
                <SquadEditor data={data} formation={challenge.formation} bricks={bricks} defaultFormation={data.settings.formation}
                  onChange={(change) => setSquad(index, change)} />
                <h4 className="sbc-subhead">Requirements</h4>
                <RequirementsEditor data={data} rules={challenge.requirements?.rules ?? []} lockedPlayers={bricks ? bricks.freeIndices.length : null}
                  onChange={(rules) => patchChallenge(index, { requirements: { rules } })} />
                <h4 className="sbc-subhead">Rewards</h4>
                <RewardsEditor data={data} entries={challenge.rewards?.entries ?? []} onChange={(entries) => patchChallenge(index, { rewards: { entries } })} />
              </Section>
            );
          })}
          <div className="sbc-add-challenge">
            <Button onClick={() => patch({ challenges: [...set.challenges, emptyChallenge(set.challenges.length + 1)] })}>Add challenge</Button>
          </div>
          {hasGroupReward && <Section title="Group rewards" text="Required for sets with two or more challenges. Paid once every challenge of the set is complete.">
            <RewardsEditor data={data} entries={set.rewards?.entries ?? []} onChange={(entries) => patch({ rewards: { entries } })} />
            {groupRewardProblem && <p className="error" role="alert">{groupRewardProblem}</p>}
          </Section>}
        </div>

        <aside className="sbc-editor-preview" aria-label="Preview" aria-busy={preview.isFetching}>
          <div className="sbc-preview-head">
            <img src={badgeUrl(set.badge || key || `tab-${set.tabId}`)} alt="" onError={(event) => { event.currentTarget.src = badgeUrl(`tab-${set.tabId}`); }} />
            <div>
              <strong>{set.name || 'Unnamed SBC'}</strong>
              <small className="dim">{tabName} · {set.challenges.length} challenge{set.challenges.length === 1 ? '' : 's'}{set.repeatableDaily ? ` · ${set.maxCompletions ?? 1} per day` : set.maxCompletions ? ` · up to ${set.maxCompletions} completions` : set.repeatable ? ' · unlimited' : ''}</small>
            </div>
          </div>
          {groupRewardProblem || shownPreview?.problem ? <p className="sbc-preview-problem" role="status">{groupRewardProblem || shownPreview?.problem}</p>
            : shownPreview ? <p className="sbc-preview-ok" role="status"><Badge tone="pitch">Ready</Badge> The server accepts this set.</p> : null}
          {shownPreview && <p className="sbc-preview-text">{shownPreview.description}</p>}
          <ol className="sbc-preview-challenges">
            {set.challenges.map((challenge, index) => (
              <li key={index}>
                <strong>{challenge.name || 'Untitled'}</strong>
                <span>{shownPreview?.challenges[index]?.description ?? '…'}</span>
                <small className="dim">{shownPreview?.challenges[index]?.players ?? '…'} players · {formationName(challenge.formation ?? data.settings.formation)}
                  {(challenge.rewards?.entries.length ?? 0) > 0 && ` · ${challenge.rewards!.entries.map((entry) => rewardText(data, entry)).join(', ')}`}</small>
              </li>
            ))}
          </ol>
          {hasGroupReward && (set.rewards?.entries.length ?? 0) > 0 && <p className="dim">Group: {set.rewards!.entries.map((entry) => rewardText(data, entry)).join(', ')}</p>}
        </aside>
      </div>
    </>
  );
}
