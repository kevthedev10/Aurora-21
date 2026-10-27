// One objective group of the catalogue: a normal group (its objectives, each a stat to reach with XP and a reward, and a reward
// for finishing them all), a gift (one objective, a date window, optionally earned by playing first) or the welcome group.
// Ids are assigned here and never typed: clubs' claims are stored by them. Address: #/objectives/group/<id>,
// #/objectives/new/<normal|gift>.
import { useState } from 'react';
import { liveListOf, useObjectiveActions, type GrindGroup, type GrindObjective, type ObjectiveGift, type ObjectiveRulesDoc, type ObjectivesReply, type WelcomeGroup } from '../../api/objectives';
import { href } from '../../app/router';
import { Button, LinkButton } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { PageHeader, Section } from '../../ui/Layout';
import { SwitchRow } from '../../ui/Switch';
import { useToast } from '../../ui/Toast';
import { ObjectiveRewardEditor, paysSomething } from './ObjectiveRewardEditor';
import { conditionsOf, conditionsProblem, conditionsText, ObjectiveConditionsEditor, useCardFilters } from './ObjectiveConditionsEditor';
import { ukInstant, ukLocal } from '../schedule/time';

export type GroupKind = 'normal' | 'gift' | 'welcome';
const back = () => { window.location.hash = href('objectives'); };

/** The UK wall clock for every objective and season datetime input. */
export const toLocal = (iso: string | null | undefined) => iso ? ukLocal(iso) : '';
/** The ISO time (UTC) of a datetime-local value, or null when empty. */
export const fromLocal = (local: string) => local ? ukInstant(local).replace(/\.\d{3}Z$/, 'Z') : null;

/** The next free objective id: past every id in the file and every one a club has claimed (the server's nextObjectiveId). */
function nextObjectiveIdOf(doc: ObjectiveRulesDoc, reply: ObjectivesReply, group: GrindGroup | null) {
  const ids = [...doc.grindGroups.flatMap((entry) => entry.objectives.map((objective) => objective.id)), ...(group?.objectives.map((objective) => objective.id) ?? []),
    ...(doc.gifts ?? []).map((gift) => gift.objective.id), doc.welcomeGroup.objective.id];
  return Math.max(reply.nextObjectiveId, ...ids.map((id) => id + 1));
}

export function GroupEditor({ reply, doc, groupId, newKind }: { reply: ObjectivesReply; doc: ObjectiveRulesDoc; groupId: number | null; newKind: GroupKind | null }) {
  const kind: GroupKind = newKind ?? (groupId === doc.welcomeGroup.id ? 'welcome' : (doc.gifts ?? []).some((gift) => gift.id === groupId) ? 'gift' : 'normal');
  const allGroupIds = [doc.welcomeGroup.id, ...doc.grindGroups.map((group) => group.id), ...(doc.gifts ?? []).map((gift) => gift.id)];
  const newId = Math.max(reply.nextGroupId, ...allGroupIds.map((id) => id + 1));
  const stat0 = reply.stats.includes('matches') ? 'matches' : reply.stats[0] ?? 'matches';
  const [normal, setNormal] = useState<GrindGroup>(() => structuredClone(doc.grindGroups.find((group) => group.id === groupId)) ?? {
    id: newId, title: '', subTitle: '', award: { coins: 1000 }, objectives: [],
  });
  const [gift, setGift] = useState<ObjectiveGift>(() => structuredClone((doc.gifts ?? []).find((entry) => entry.id === groupId)) ?? {
    id: newId, title: '', subTitle: '', objective: { id: nextObjectiveIdOf(doc, reply, null), name: '', description: '' }, award: { coins: 1000 },
    availableFrom: null, availableUntil: null,
  });
  const [welcome, setWelcome] = useState<WelcomeGroup>(() => structuredClone(doc.welcomeGroup));
  const [dateError, setDateError] = useState<string | null>(null);
  const setGiftDate = (key: 'availableFrom' | 'availableUntil', value: string) => {
    try { setGift({ ...gift, [key]: fromLocal(value) }); setDateError(null); }
    catch (error) { setDateError(error instanceof Error ? error.message : String(error)); }
  };
  const actions = useObjectiveActions(reply.settingsFile);
  const toast = useToast();
  const isNew = newKind !== null;
  const title = kind === 'welcome' ? welcome.title : kind === 'gift' ? gift.title : normal.title;

  const conditions = conditionsOf(reply);
  const filters = useCardFilters();
  /** The in-game text a conditioned objective reads as when no description is written. */
  const suggested = (objective: GrindObjective) => conditionsText(objective, conditions.modes, filters.data);
  /** Whether the objective's conditions or stat differ from the published file (its text may no longer match them). */
  const conditionsChanged = (objective: GrindObjective) => {
    const published = doc.grindGroups.find((group) => group.id === normal.id)?.objectives.find((entry) => entry.id === objective.id);
    return !published || published.stat !== objective.stat || JSON.stringify(published.requirements ?? null) !== JSON.stringify(objective.requirements ?? null);
  };
  const conditionError = kind === 'normal' ? normal.objectives.map((objective) => conditionsProblem(objective, conditions.playerStats)).find(Boolean) ?? null : null;

  const problem = dateError ?? (!title.trim() ? 'Give the group a title.'
    : kind === 'normal' && !normal.objectives.length ? 'Add at least one objective.'
    : kind === 'normal' && normal.objectives.some((objective) => !objective.name.trim() || objective.target < 1 || !paysSomething(objective.award)) ? 'Every objective needs a name, a target of 1 or more and a reward.'
    : conditionError ? conditionError
    : kind === 'normal' && !paysSomething(normal.award) ? 'The group reward pays nothing.'
    : kind === 'gift' && (!gift.objective.name.trim() || !paysSomething(gift.award)) ? 'The gift needs a name and a reward.'
    : kind === 'gift' && gift.availableFrom && gift.availableUntil && gift.availableFrom > gift.availableUntil ? 'The gift window ends before it starts.'
    : null);

  const stage = async () => {
    try {
      if (isNew && doc.live == null) await actions.mutateAsync({ path: ['live'], value: liveListOf(doc), shown: doc.live ?? null });
      if (kind === 'welcome') {
        await actions.mutateAsync({ path: ['welcomeGroup'], value: welcome, shown: doc.welcomeGroup });
      } else if (kind === 'gift') {
        const gifts = doc.gifts ?? [];
        const next = isNew ? [...gifts, gift] : gifts.map((entry) => entry.id === gift.id ? gift : entry);
        await actions.mutateAsync({ path: ['gifts'], value: next, shown: gifts });
      } else {
        // A conditioned objective without its own text shows what its conditions read as (the stat's standard text would
        // leave the conditions out in game).
        const group = { ...normal, objectives: normal.objectives.map((objective) => objective.requirements && !objective.description?.trim()
          ? { ...objective, description: suggested(objective) } : objective) };
        const next = isNew ? [...doc.grindGroups, group] : doc.grindGroups.map((entry) => entry.id === group.id ? group : entry);
        await actions.mutateAsync({ path: ['grindGroups'], value: next, shown: doc.grindGroups });
      }
      toast(`${title} saved${isNew ? '. Set dates under Live objectives to schedule it' : ''}.`, 'done');
      back();
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), 'error');
    }
  };

  const setObjective = (index: number, changes: Partial<GrindObjective>) =>
    setNormal((current) => ({ ...current, objectives: current.objectives.map((objective, at) => at === index ? { ...objective, ...changes } : objective) }));
  const describe = (stat: string, target: number) => (reply.rules.statLabels[stat] ?? stat).replace('{n}', target.toLocaleString('en-GB'));

  return (
    <>
      <PageHeader crumbs={<a href={href('objectives')}>Objectives</a>}
        title={isNew ? (kind === 'gift' ? 'New gift' : 'New objective group') : title || 'Objective group'}
        description={kind === 'welcome' ? 'The group every new club sees first.' : kind === 'gift' ? 'A one-off gift: one objective, claimable once while its window is open.'
          : 'Objectives the club completes by playing; each pays its reward and XP, and finishing them all pays the group reward.'}
        actions={<><LinkButton href={href('objectives')}>Cancel</LinkButton>
          <Button variant="staged" disabled={!!problem || actions.isPending} title={problem ?? undefined} onClick={() => void stage()}>Save changes</Button></>} />
      {problem && <p className="notice">{problem}</p>}

      <Section title="Group">
        <div className="form-row">
          <Field label="Title"><input value={title} maxLength={60} onChange={(event) => {
            const value = event.target.value;
            if (kind === 'welcome') setWelcome({ ...welcome, title: value }); else if (kind === 'gift') setGift({ ...gift, title: value }); else setNormal({ ...normal, title: value });
          }} /></Field>
          <Field label="Subtitle"><input value={kind === 'welcome' ? welcome.subTitle : kind === 'gift' ? gift.subTitle : normal.subTitle} maxLength={80} onChange={(event) => {
            const value = event.target.value;
            if (kind === 'welcome') setWelcome({ ...welcome, subTitle: value }); else if (kind === 'gift') setGift({ ...gift, subTitle: value }); else setNormal({ ...normal, subTitle: value });
          }} /></Field>
          <Field label="Group id" hint="Assigned automatically; claims are stored by it."><input value={kind === 'welcome' ? welcome.id : kind === 'gift' ? gift.id : normal.id} readOnly disabled /></Field>
        </div>
        {isNew && <p className="dim">New groups stay in the catalogue. Set a start and end date under Live objectives to make them available.</p>}
      </Section>

      {kind === 'normal' && <>
        {normal.objectives.map((objective, index) => (
          <Section key={objective.id} title={`Objective ${index + 1}: ${objective.name || 'Untitled'}`}
            actions={<Button size="sm" variant="danger" onClick={() => setNormal({ ...normal, objectives: normal.objectives.filter((_, at) => at !== index) })}>Remove</Button>}>
            <div className="form-row">
              <Field label="Name"><input value={objective.name} maxLength={60} onChange={(event) => setObjective(index, { name: event.target.value })} /></Field>
              <Field label="Tracks" hint={objective.requirements ? 'Only stats a match condition can count.' : undefined}>
                <select disabled={!!(objective.requirements?.goalType || objective.requirements?.assistType)} value={objective.stat} onChange={(event) => setObjective(index, { stat: event.target.value })}>
                  {reply.stats.filter((stat) => !objective.requirements || (objective.requirements.player ? conditions.playerStats : conditions.stats).includes(stat))
                    .map((stat) => <option key={stat} value={stat}>{describe(stat, 1).replace(/\b1\b/, 'n')}</option>)}
                </select>
              </Field>
              <Field label="Target" hint={objective.requirements ? suggested(objective).replace('{n}', objective.target.toLocaleString('en-GB')) : describe(objective.stat, objective.target)}>
                <input type="number" min={1} value={objective.target} onChange={(event) => setObjective(index, { target: Math.max(1, Number(event.target.value) || 1) })} />
              </Field>
              <Field label="Season XP"><input type="number" min={0} value={objective.xp} onChange={(event) => setObjective(index, { xp: Math.max(0, Number(event.target.value) || 0) })} /></Field>
            </div>
            <ObjectiveConditionsEditor objective={objective} reply={reply} onChange={(changes) => setObjective(index, changes)} />
            <Field label="In-game description" hint={objective.requirements ? 'Use {n} for the target. Leave empty to use the text the conditions read as.' : 'Use {n} for the target. Leave empty to use the standard text for the tracked stat.'}>
              <textarea rows={3} maxLength={500} value={objective.description ?? ''} placeholder={objective.requirements ? suggested(objective) : describe(objective.stat, objective.target)} onChange={(event) => setObjective(index, { description: event.target.value || undefined })} />
            </Field>
            {objective.requirements && objective.description?.trim() && objective.description !== suggested(objective) && conditionsChanged(objective) && <p className="dim">
              Suggested from the conditions: {suggested(objective)} <Button size="sm" variant="ghost" onClick={() => setObjective(index, { description: suggested(objective) })}>Use it</Button></p>}
            <p className="muted">In-game preview: {(objective.description?.trim() || (objective.requirements ? suggested(objective) : reply.rules.statLabels[objective.stat]) || objective.stat).replace('{n}', objective.target.toLocaleString('en-GB'))}</p>
            <h4 className="objective-subhead">Reward</h4>
            <ObjectiveRewardEditor value={objective.award} packs={reply.packs} idPrefix={`objective ${index + 1}`} onChange={(award) => setObjective(index, { award })} />
          </Section>
        ))}
        <div className="row-actions objective-add">
          <Button onClick={() => setNormal({ ...normal, objectives: [...normal.objectives,
            { id: nextObjectiveIdOf(doc, reply, normal), name: '', stat: stat0, target: 10, award: { coins: 1000 }, xp: 1000 }] })}>Add objective</Button>
        </div>
        <Section title="Group reward" text="Paid once every objective in the group has been claimed.">
          <Field label="Group Season XP"><input type="number" min={0} value={normal.xp ?? 0} onChange={(event) => setNormal({ ...normal, xp: Math.max(0, Number(event.target.value) || 0) })} /></Field>
          <ObjectiveRewardEditor value={normal.award} packs={reply.packs} idPrefix="group" onChange={(award) => setNormal({ ...normal, award })} />
        </Section>
      </>}

      {kind === 'gift' && <>
        <Section title="Gift">
          <div className="form-row">
            <Field label="Objective name"><input value={gift.objective.name} maxLength={60} onChange={(event) => setGift({ ...gift, objective: { ...gift.objective, name: event.target.value } })} /></Field>
            <Field label="Available from" hint="UK time. Empty: from now."><input type="datetime-local" value={toLocal(gift.availableFrom)} onChange={(event) => setGiftDate('availableFrom', event.target.value)} /></Field>
            <Field label="Available until" hint="UK time. Empty: no end."><input type="datetime-local" value={toLocal(gift.availableUntil)} onChange={(event) => setGiftDate('availableUntil', event.target.value)} /></Field>
          </div>
          <div className="form-row">
            <Field label="Description"><textarea rows={2} maxLength={200} value={gift.objective.description} onChange={(event) => setGift({ ...gift, objective: { ...gift.objective, description: event.target.value } })} /></Field>
          </div>
          <SwitchRow label="Earned by playing" checked={!!gift.stat} onChange={(on) => {
            const next: ObjectiveGift = { ...gift };
            if (on) { next.stat = stat0; next.target = 5; } else { delete next.stat; delete next.target; }
            setGift(next);
          }} text="The club reaches a target first (counted from when the gift goes live); otherwise it can claim the gift straight away." />
          {gift.stat && <div className="form-row">
            <Field label="Tracks"><select value={gift.stat} onChange={(event) => setGift({ ...gift, stat: event.target.value })}>
              {reply.stats.map((stat) => <option key={stat} value={stat}>{describe(stat, 1).replace(/\b1\b/, 'n')}</option>)}
            </select></Field>
            <Field label="Target" hint={describe(gift.stat, gift.target ?? 1)}><input type="number" min={1} value={gift.target ?? 1} onChange={(event) => setGift({ ...gift, target: Math.max(1, Number(event.target.value) || 1) })} /></Field>
          </div>}
        </Section>
        <Section title="Reward">
          <ObjectiveRewardEditor value={gift.award} packs={reply.packs} onChange={(award) => setGift({ ...gift, award })} />
        </Section>
      </>}

      {kind === 'welcome' && <Section title="Welcome objective" text="Its reward is packs of one kind, plus Season XP.">
        <div className="form-row">
          <Field label="Objective name"><input value={welcome.objective.name} maxLength={60} onChange={(event) => setWelcome({ ...welcome, objective: { ...welcome.objective, name: event.target.value } })} /></Field>
          <Field label="Reward pack">
            <select value={welcome.rewardPackId} onChange={(event) => setWelcome({ ...welcome, rewardPackId: Number(event.target.value) })}>
              {reply.packs.map((pack) => <option key={pack.id} value={pack.id}>{pack.name} · #{pack.id}</option>)}
            </select>
          </Field>
          <Field label="Packs"><input type="number" min={1} max={10} value={welcome.rewardPackCount} onChange={(event) => setWelcome({ ...welcome, rewardPackCount: Math.max(1, Math.min(10, Number(event.target.value) || 1)) })} /></Field>
          <Field label="Tradeability"><select value={welcome.rewardUntradeable ? 'untradeable' : 'tradeable'} onChange={(event) => setWelcome({ ...welcome, rewardUntradeable: event.target.value === 'untradeable' })}>
            <option value="tradeable">Tradeable</option><option value="untradeable">Untradeable</option>
          </select></Field>
          <Field label="Season XP"><input type="number" min={0} value={welcome.xpAward} onChange={(event) => setWelcome({ ...welcome, xpAward: Math.max(0, Number(event.target.value) || 0) })} /></Field>
        </div>
        <div className="form-row">
          <Field label="Description"><textarea rows={2} maxLength={200} value={welcome.objective.description} onChange={(event) => setWelcome({ ...welcome, objective: { ...welcome.objective, description: event.target.value } })} /></Field>
        </div>
        <SwitchRow label="Earned by playing" checked={!!welcome.stat} onChange={(on) => {
          const next: WelcomeGroup = { ...welcome };
          if (on) { next.stat = stat0; next.target = 1; } else { delete next.stat; delete next.target; }
          setWelcome(next);
        }} text="A new club reaches a target first (for example one match); otherwise it can claim straight away." />
        {welcome.stat && <div className="form-row">
          <Field label="Tracks"><select value={welcome.stat} onChange={(event) => setWelcome({ ...welcome, stat: event.target.value })}>
            {reply.stats.map((stat) => <option key={stat} value={stat}>{describe(stat, 1).replace(/\b1\b/, 'n')}</option>)}
          </select></Field>
          <Field label="Target" hint={describe(welcome.stat, welcome.target ?? 1)}><input type="number" min={1} value={welcome.target ?? 1} onChange={(event) => setWelcome({ ...welcome, target: Math.max(1, Number(event.target.value) || 1) })} /></Field>
        </div>}
      </Section>}
    </>
  );
}
