// SBCs → Settings: rules/sbc.json, which the Rules page no longer lists. Each value is staged on its own as a rules change of
// that file and published under Changes. What the client is sent for a requirement type (its eligibility key, value and scope),
// the squad size and the id bases are fixed by FIFA 21's contract, so they are shown but not edited here.
import { useState } from 'react';
import { useSbcSettingActions, type SbcReply, type SbcRequirementRule, type SbcTextSettings } from '../../api/sbc';
import type { RulePath } from '../../api/rules';
import { href } from '../../app/router';
import { StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { ConfirmDialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { Section } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';
import '../modes/modes.css';

type Changes = { area: string; key: Record<string, unknown>; operation: string; after?: unknown }[] | undefined;
type Stage = (path: RulePath, value: unknown, shown: unknown, label: string, done?: () => void) => void;

/** "f4231" → "4-2-3-1", and a variant: "f433a" → "4-3-3 (2)", "f433b" → "4-3-3 (3)". */
export function formationName(formation: string) {
  const match = /^f(\d+)([a-z]?)$/.exec(formation);
  if (!match) return formation;
  const lines = match[1]!.split('').join('-');
  return match[2] ? `${lines} (${match[2].charCodeAt(0) - 'a'.charCodeAt(0) + 2})` : lines;
}

const TEXTS: { key: keyof SbcTextSettings; label: string; hint: string }[] = [
  { key: 'requirementSeparator', label: 'Between requirements', hint: 'Joins a challenge’s requirement lines in its description. Spaces count.' },
  { key: 'noRequirements', label: 'No requirements', hint: 'A challenge with no other requirement. {count} is the players it needs.' },
  { key: 'setDescription', label: 'Set description', hint: 'A set without its own description. {name}, {count} and {challenges} are filled in.' },
  { key: 'unnamedSetDescription', label: 'Unnamed set description', hint: 'The same for a set without a name: {count} and {challenges}.' },
  { key: 'challenge', label: 'One challenge', hint: 'The word {challenges} becomes for one challenge.' },
  { key: 'challenges', label: 'Several challenges', hint: 'The word {challenges} becomes for more than one.' },
  { key: 'unknownChallenge', label: 'Unknown challenge', hint: 'The error FIFA shows for a challenge that is not live.' },
  { key: 'shortSquad', label: 'Squad too small', hint: 'Refusing a submission without enough players. {need} and {placed} are filled in.' },
];

/** A requirement type's name from its label: "Min Squad Rating", "Rare players". */
export const ruleName = (rule: { type: string; label: string }) => rule.label.replace(/:?\s*(Min\s*|Max\s*)?\{value\}.*$/, '').trim() || rule.type;

/** What the client is sent for a requirement type, in words. */
function clientCheck(rule: SbcRequirementRule) {
  if (!rule.eligibilityKey) return 'Description only: not sent to FIFA as a check, not checked on submit.';
  const value = rule.eligibilityValue !== null ? ` ${rule.eligibilityValue}` : '';
  const scope = rule.scope === 'AtLeast' ? 'at least' : rule.scope === 'AtMost' ? 'at most' : 'exactly';
  return `FIFA check ${rule.eligibilityKey}${value}, ${scope}${rule.exact ? ` (or exactly, ${rule.exact.scope})` : ''}.`;
}

export function SbcSettingsTab({ data, changes }: { data: SbcReply; changes: Changes }) {
  const toast = useToast();
  const actions = useSbcSettingActions(data.settingsFile);
  const [restoring, setRestoring] = useState(false);
  const settings = data.settings;
  const staged = (changes ?? []).filter((change) => change.area === 'rules' && change.key.file === data.settingsFile);
  /** The staged value at a path, or undefined. */
  const pending = (path: RulePath) => {
    const change = staged.find((candidate) => JSON.stringify(candidate.key.path) === JSON.stringify(path));
    return change ? { value: (change.after as { value?: unknown } | null)?.value } : undefined;
  };
  const stage: Stage = (path, value, shown, label, done) => actions.stage.mutate({ path, value, shown }, {
    onSuccess: () => { toast(`${label} saved.`, 'done'); done?.(); },
    onError: (error) => toast(error instanceof Error ? error.message : String(error), 'error'),
  });
  const busy = actions.stage.isPending || actions.restore.isPending;
  const ignored = (pending(['ignoredRequirements'])?.value as string[] | undefined) ?? settings.ignoredRequirements;
  const tabName = (id: number) => data.tabs.find((tab) => tab.id === id)?.name ?? `Tab ${id}`;

  return (
    <>
      {staged.length > 0 && <p className="notice">SBC settings have {staged.length} staged edit{staged.length === 1 ? '' : 's'}. <a href={href('changes')}>Review Changes</a> to publish them.</p>}
      <Section title="General" text="How SBCs behave when a set or challenge does not say otherwise."
        actions={<Button size="sm" variant="ghost" disabled={busy} onClick={() => setRestoring(true)}>Restore shipped settings</Button>}>
        <div className="mode-settings">
          <Setting label="Default formation" hint="For a challenge that names no formation of its own." path={['formation']} live={settings.formation} pending={pending(['formation'])}
            busy={busy} stage={stage} kind={{ kind: 'select', options: data.formations.map((formation) => ({ value: formation.id, label: formationName(formation.id) })) }} />
          <Setting label="Player pick reward name" hint="The name of a reward that is a player pick. {rating} becomes its minimum rating." path={['playerPickRewardName']}
            live={settings.playerPickRewardName} pending={pending(['playerPickRewardName'])} busy={busy} stage={stage} kind={{ kind: 'text', max: 80 }}
            example={(value) => String(value).replaceAll('{rating}', '84')} />
          <Setting label="Fallback badge tab" hint="A set with no badge art of its own shows this tab’s badge." path={['images', 'fallbackTabId']}
            live={settings.images.fallbackTabId} pending={pending(['images', 'fallbackTabId'])} busy={busy} stage={stage}
            kind={{ kind: 'select', options: data.tabs.map((tab) => ({ value: String(tab.id), label: tab.name })), numeric: true }} />
          <Setting label="Badge cache (seconds)" hint="How long FIFA keeps an SBC badge before asking again." path={['images', 'cacheSeconds']}
            live={settings.images.cacheSeconds} pending={pending(['images', 'cacheSeconds'])} busy={busy} stage={stage} kind={{ kind: 'number', min: 0 }} />
          <Toggle label="Repeatable rewards untradeable" text="What a repeatable SBC pays is untradeable (packs, players, manager league cards). A set can opt out with its own rewards-tradeable switch."
            path={['repeatableRewardsUntradeable']} live={settings.repeatableRewardsUntradeable} pending={pending(['repeatableRewardsUntradeable'])} busy={busy} stage={stage} />
        </div>
      </Section>

      <Section title="Requirement types" text="The label FIFA shows for each requirement, and whether the type is used at all. {value} becomes the requirement’s number or quality. A type that is not used is left out of every challenge: neither shown nor checked.">
        <ul className="mode-ranks">
          {data.requirementRules.filter(rule => rule.type !== 'AGE').map((rule) => {
            const used = !ignored.includes(rule.type);
            const sample = rule.bound === 'value' ? 'Gold' : rule.bound === 'max' ? '3' : '75';
            return (
              <li key={rule.type} className={used ? 'mode-rank' : 'mode-rank removed'}>
                <div className="mode-rank-head">
                  <strong title={rule.type}>{ruleName(rule)}</strong>
                  <span className="dim">{clientCheck(rule)}</span>
                  {pending(['ignoredRequirements']) && <StagedBadge />}
                </div>
                <div className="mode-settings">
                  <Setting label="Label" path={['requirements', rule.type, 'label']} live={rule.label} pending={pending(['requirements', rule.type, 'label'])}
                    busy={busy} stage={stage} kind={{ kind: 'text', max: 120 }} example={(value) => String(value).replaceAll('{value}', sample)} />
                  {rule.exact && <Setting label="Label for an exact count" path={['requirements', rule.type, 'exact', 'label']} live={rule.exact.label}
                    pending={pending(['requirements', rule.type, 'exact', 'label'])} busy={busy} stage={stage} kind={{ kind: 'text', max: 120 }}
                    example={(value) => String(value).replaceAll('{value}', '3')} />}
                  <div className="mode-setting">
                    <label className="listing-switch">
                      <input type="checkbox" role="switch" checked={used} disabled={busy} aria-label={`${ruleName(rule)} used`}
                        onChange={(event) => stage(['ignoredRequirements'],
                          event.target.checked ? ignored.filter((type) => type !== rule.type) : [...ignored, rule.type].sort(),
                          pending(['ignoredRequirements'])?.value ?? settings.ignoredRequirements, `${ruleName(rule)} ${event.target.checked ? 'used' : 'not used'}`)} />
                      <span>{used ? 'Used' : 'Not used'}</span>
                    </label>
                  </div>
                </div>
              </li>
            );
          })}
        </ul>
      </Section>

      <Section title="Game text" text="Text the server writes into SBC screens. Words in {braces} are filled in.">
        <div className="mode-settings">
          {TEXTS.map((text) => (
            <Setting key={text.key} label={text.label} hint={text.hint} path={['text', text.key]} live={settings.text[text.key]}
              pending={pending(['text', text.key])} busy={busy} stage={stage} kind={{ kind: 'text', max: 200 }} />
          ))}
        </div>
      </Section>

      <Section title="Fixed values" text="Set by FIFA 21’s SBC contract and the art on disk; changing them would break saved squads or the client, so they are not edited here.">
        <dl className="sbc-fixed">
          <div><dt>Squad size</dt><dd>{num(settings.squadSize)} players</dd></div>
          <div><dt>Locked-slot item ids from</dt><dd className="mono">{settings.brickItemIdBase}</dd></div>
          <div><dt>Badge image ids from</dt><dd className="mono">{settings.images.imageIdBase}</dd></div>
          <div><dt>Badge art folder</dt><dd className="mono">{settings.images.artFolder}</dd></div>
          <div><dt>Fallback badge</dt><dd>{tabName(settings.images.fallbackTabId)}</dd></div>
        </dl>
      </Section>

      <ConfirmDialog open={restoring} onOpenChange={setRestoring} title="Restore the shipped SBC settings?"
        text="Stages every difference between these settings and the bundled rules/sbc.json as pending changes. Nothing changes in the game until you publish them."
        confirmLabel="Stage the shipped settings" onConfirm={async () => {
          const reply = await actions.restore.mutateAsync();
          const count = Number(reply.staged) || 0;
          toast(count ? `Staged ${num(count)} difference${count === 1 ? '' : 's'}: review them under Changes.` : 'The settings already match the shipped file.', count ? 'staged' : 'done');
        }} />
    </>
  );
}

type Kind = { kind: 'text'; max: number } | { kind: 'number'; min: number } | { kind: 'select'; options: { value: string; label: string }[]; numeric?: boolean };

/** One value: a draft, and a Stage button once it differs from what is published or staged. */
function Setting({ label, hint, path, live, pending, busy, stage, kind, example }: {
  label: string; hint?: string; path: RulePath; live: string | number; pending: { value: unknown } | undefined;
  busy: boolean; stage: Stage; kind: Kind; example?: (value: string) => string;
}) {
  const shown = (pending?.value ?? live) as string | number;
  const [draft, setDraft] = useState<string | null>(null);
  const text = draft ?? String(shown);
  const value = kind.kind === 'number' || (kind.kind === 'select' && kind.numeric) ? Number(text) : text;
  const error = kind.kind === 'number' && (!/^\d+$/.test(text.trim()) || Number(text) < kind.min) ? `A whole number from ${kind.min}.`
    : kind.kind === 'text' && !text.length ? 'Cannot be empty.' : null;
  const dirty = draft !== null && text !== String(shown);
  return (
    <div className="mode-setting">
      <Field label={label} hint={example && !error ? `${hint ? `${hint} ` : ''}Shows as: ${example(text)}` : hint} error={dirty ? error : null}>
        {kind.kind === 'select'
          ? <select value={text} onChange={(event) => setDraft(event.target.value)}>
              {!kind.options.some((option) => option.value === text) && <option value={text}>{text}</option>}
              {kind.options.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </select>
          : <input type={kind.kind === 'number' ? 'number' : 'text'} value={text} maxLength={kind.kind === 'text' ? kind.max : undefined}
              min={kind.kind === 'number' ? kind.min : undefined} onChange={(event) => setDraft(event.target.value)} />}
      </Field>
      <div className="mode-setting-actions">
        {pending && <StagedBadge />}
        {dirty && <Button size="sm" variant="staged" disabled={busy || !!error} aria-label={`Save ${label}`}
          onClick={() => stage(path, value, shown, label, () => setDraft(null)) }>Save</Button>}
        {dirty && <Button size="sm" variant="ghost" onClick={() => setDraft(null)}>Reset</Button>}
      </div>
    </div>
  );
}

function Toggle({ label, text, path, live, pending, busy, stage }: {
  label: string; text: string; path: RulePath; live: boolean; pending: { value: unknown } | undefined; busy: boolean; stage: Stage;
}) {
  const shown = (pending?.value ?? live) as boolean;
  return (
    <div className="mode-setting">
      <label className="listing-switch">
        <input type="checkbox" role="switch" checked={shown} disabled={busy} aria-label={label}
          onChange={(event) => stage(path, event.target.checked, shown, label)} />
        <span><strong>{label}</strong></span>
      </label>
      <small className="dim">{text}</small>
      <div className="mode-setting-actions">{pending && <StagedBadge />}</div>
    </div>
  );
}
