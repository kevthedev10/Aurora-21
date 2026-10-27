// An objective, group, gift or Season Pass level reward (rules/objectives.json ObjectiveReward): coins, Aurora Tokens and any
// number of packs or player picks, each with a count and its own tradeability. My Stadium rewards (an item pack, a stadium
// tier, slot unlocks) are kept as they are and shown read-only.
import type { ObjectivePackLine, ObjectiveReward, ObjectivesReply } from '../../api/objectives';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';

type Packs = ObjectivesReply['packs'];

/** The reward's pack lines, the older single-pack form included. */
export const packLines = (reward: ObjectiveReward): ObjectivePackLine[] => [
  ...(reward.pack ? [{ packId: reward.pack, count: 1, untradeable: !!reward.untradeable }] : []),
  ...(reward.packs ?? []),
];

/** Whether the reward pays anything at all (the server refuses one that pays nothing); with choices, whether every one does. */
export const paysSomething = (reward: ObjectiveReward): boolean => reward.options?.length ? reward.options.every(paysSomething) : !!reward.none || (reward.players?.length ?? 0) > 0 || (reward.coins ?? 0) > 0 || (reward.fifaPoints ?? 0) > 0 || packLines(reward).length > 0
  || !!reward.vanity || reward.stadiumTier != null || (reward.unlockSlots?.length ?? 0) > 0;

/** "10,000 coins · 50 Aurora Tokens · 2× Gold Pack (untradeable)". */
export function rewardText(reward: ObjectiveReward, packs: Packs): string {
  if (reward.none) return 'No item reward (source unspecified)';
  if (reward.options?.length) return `Choice of ${reward.options.length}: ${reward.options.map((option) => rewardText(option, packs)).join(' / ')}`;
  const name = (id: number) => packs.find((pack) => pack.id === id)?.name ?? `Pack ${id}`;
  const parts = [
    reward.coins ? `${num(reward.coins)} coins` : null,
    reward.fifaPoints ? `${num(reward.fifaPoints)} Aurora Tokens` : null,
    ...packLines(reward).map((line) => `${(line.count ?? 1) > 1 ? `${line.count}× ` : ''}${name(line.packId)}${line.untradeable ? ' (untradeable)' : ''}`),
    ...(reward.players ?? []).map((player) => `Player #${player.resourceId}${player.untradeable !== false ? ' (untradeable)' : ''}`),
    reward.vanity ? `My Stadium: ${reward.vanity.name}` : null,
    reward.stadiumTier != null ? 'A stadium tier' : null,
    reward.unlockSlots?.length ? `Unlocks ${reward.unlockSlots.length} My Stadium slot${reward.unlockSlots.length === 1 ? '' : 's'}` : null,
  ].filter(Boolean);
  return parts.length ? parts.join(' · ') : 'Nothing';
}

export function ObjectiveRewardEditor({ value, onChange, packs, idPrefix }: {
  value: ObjectiveReward;
  onChange: (reward: ObjectiveReward) => void;
  packs: Packs;
  /** Makes the field labels unique when several editors share a page. */
  idPrefix?: string;
}) {
  const lines = packLines(value);
  const sorted = [...packs].sort((a, b) => a.name.localeCompare(b.name, 'en') || a.id - b.id);
  // The editor always writes the line form; the older single pack becomes a line.
  const emit = (changes: Partial<ObjectiveReward> & { lines?: ObjectivePackLine[] }) => {
    const { lines: nextLines, ...rest } = changes;
    const next: ObjectiveReward = { ...value, ...rest };
    delete next.none;
    delete next.pack;
    delete next.untradeable;
    const packList = nextLines ?? lines;
    if (packList.length) next.packs = packList; else delete next.packs;
    if (!next.coins) delete next.coins;
    if (!next.fifaPoints) delete next.fifaPoints;
    onChange(next);
  };
  const setLine = (index: number, line: ObjectivePackLine) => emit({ lines: lines.map((current, at) => at === index ? line : current) });
  const label = (text: string) => idPrefix ? <span>{text}<span className="visually-hidden"> ({idPrefix})</span></span> : text;
  return (
    <div className="objective-reward">
      {value.none && <p className="dim">No item reward was specified by the source. Adding a reward replaces this.</p>}
      {!!value.players?.length && <p className="dim">{rewardText({ players: value.players }, packs)} (kept as is)</p>}
      <div className="form-row">
        <Field label={label('Coins')}>
          <input type="number" min={0} value={value.coins ?? 0} onChange={(event) => emit({ coins: Math.max(0, Number(event.target.value) || 0) })} />
        </Field>
        <Field label={label('Aurora Tokens')} hint="Paid to the club; FIFA 21 shows no tile for them.">
          <input type="number" min={0} value={value.fifaPoints ?? 0} onChange={(event) => emit({ fifaPoints: Math.max(0, Number(event.target.value) || 0) })} />
        </Field>
      </div>
      <ul className="objective-reward-lines">
        {lines.map((line, index) => (
          <li key={index}>
            <Field label={label('Pack or pick')}>
              <select value={line.packId} onChange={(event) => setLine(index, { ...line, packId: Number(event.target.value) })}>
                {!packs.some((pack) => pack.id === line.packId) && <option value={line.packId}>Pack {line.packId} (not in the catalogue)</option>}
                <optgroup label="Packs">{sorted.filter((pack) => !pack.pick).map((pack) => <option key={pack.id} value={pack.id}>{pack.name} · #{pack.id}</option>)}</optgroup>
                <optgroup label="Random player picks">{sorted.filter((pack) => pack.pick && !pack.fixed).map((pack) => <option key={pack.id} value={pack.id}>{pack.name} · #{pack.id}</option>)}</optgroup>
                <optgroup label="Fixed player picks">{sorted.filter((pack) => pack.fixed).map((pack) => <option key={pack.id} value={pack.id}>{pack.name} · #{pack.id}</option>)}</optgroup>
              </select>
            </Field>
            <Field label={label('Count')}>
              <input type="number" min={1} max={100} value={line.count ?? 1} onChange={(event) => setLine(index, { ...line, count: Math.max(1, Math.min(100, Number(event.target.value) || 1)) })} />
            </Field>
            <Field label={label('Tradeability')}>
              <select value={line.untradeable ? 'untradeable' : 'tradeable'} onChange={(event) => setLine(index, { ...line, untradeable: event.target.value === 'untradeable' })}>
                <option value="tradeable">Tradeable</option><option value="untradeable">Untradeable</option>
              </select>
            </Field>
            <Button size="sm" variant="ghost" className="objective-reward-remove" aria-label="Remove pack" onClick={() => emit({ lines: lines.filter((_, at) => at !== index) })}>Remove</Button>
          </li>
        ))}
      </ul>
      <div className="row-actions">
        <Button size="sm" onClick={() => emit({ lines: [...lines, { packId: sorted.find((pack) => !pack.pick)?.id ?? 0, count: 1, untradeable: true }] })}>Add pack or pick</Button>
        {(value.vanity || value.stadiumTier != null || (value.unlockSlots?.length ?? 0) > 0) &&
          <span className="dim">Also: {[value.vanity && `My Stadium ${value.vanity.name}`, value.stadiumTier != null && 'a stadium tier',
            value.unlockSlots?.length && `${value.unlockSlots.length} slot unlock(s)`].filter(Boolean).join(', ')} (kept as is)</span>}
      </div>
      {!paysSomething(value) && <p className="field-problem" role="status">This reward pays nothing yet.</p>}
    </div>
  );
}
