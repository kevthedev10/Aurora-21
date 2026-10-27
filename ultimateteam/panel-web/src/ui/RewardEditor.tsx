// The shared reward editor (todo.md section 2): coins, Aurora Champions Points, XP and lines, each a pack or pick with a count and its own
// tradeable/untradeable choice. Every mode's reward editor uses it; a mode says which amounts it pays through `allow`.
import { useId } from 'react';
import type { Reward, RewardLine } from '../api/modes';
import { Button } from './Button';
import { num } from './format';
import styles from './RewardEditor.module.css';

export interface RewardPackOption {
  id: number;
  name: string;
  pick?: boolean;
  /** Where the pack's editor opens. */
  href?: string;
}

export interface RewardAllow {
  fcPoints: boolean;
  xp: boolean;
  /** The most copies of one line. */
  maxCount: number;
}

const whole = (text: string) => (/^\d+$/.test(text.trim()) ? Number(text.trim()) : Number.NaN);

/** Why a reward cannot be staged, or an empty list: what the server checks for every mode. */
export function rewardProblems(reward: Reward, allow: RewardAllow, packs: RewardPackOption[]): string[] {
  const problems: string[] = [];
  const amounts: [string, number][] = [['Coins', reward.coins], ['Aurora Champions Points', reward.fcPoints], ['XP', reward.xp]];
  for (const [label, value] of amounts) if (!Number.isSafeInteger(value) || value < 0) problems.push(`${label} must be a whole number from 0.`);
  if (!allow.fcPoints && reward.fcPoints) problems.push('This mode pays no Aurora Champions Points.');
  if (!allow.xp && reward.xp) problems.push('This mode pays no XP.');
  reward.lines.forEach((line, index) => {
    if (line.cardResourceId !== undefined) problems.push(`Line ${index + 1}: player cards are not available in this mode yet.`);
    else if (line.packId === undefined || !packs.some((pack) => pack.id === line.packId)) problems.push(`Line ${index + 1}: choose an existing pack.`);
    if (!Number.isSafeInteger(line.count) || line.count < 1 || line.count > allow.maxCount) problems.push(`Line ${index + 1}: the count is 1 to ${allow.maxCount}.`);
  });
  return problems;
}

/** "75,000 coins · 2,000 ACP · 2× Rare Players · 2× Red Pick (untradeable)". */
export function rewardSummary(reward: Reward, packName: (id: number) => string): string {
  const parts = [
    reward.coins ? `${num(reward.coins)} coins` : null,
    reward.fcPoints ? `${num(reward.fcPoints)} ACP` : null,
    reward.xp ? `${num(reward.xp)} XP` : null,
    ...reward.lines.map((line) => `${line.count > 1 ? `${line.count}× ` : ''}${line.packId !== undefined ? packName(line.packId) : `Card ${line.cardResourceId}`}${line.untradeable ? ' (untradeable)' : ''}`),
  ].filter(Boolean);
  return parts.length ? parts.join(' · ') : 'Nothing';
}

export function RewardEditor({ value, onChange, packs, allow, disabled = false }: {
  value: Reward;
  onChange: (reward: Reward) => void;
  packs: RewardPackOption[];
  allow: RewardAllow;
  disabled?: boolean;
}) {
  const id = useId();
  const sorted = [...packs].sort((a, b) => a.name.localeCompare(b.name, 'en') || a.id - b.id);
  const setLine = (index: number, line: RewardLine) => onChange({ ...value, lines: value.lines.map((existing, at) => (at === index ? line : existing)) });
  const amount = (key: 'coins' | 'fcPoints' | 'xp', label: string) => (
    <label className={styles.amount}>
      <span>{label}</span>
      <input type="number" inputMode="numeric" min={0} step={1} disabled={disabled}
        value={Number.isFinite(value[key]) ? value[key] : ''}
        onChange={(event) => onChange({ ...value, [key]: whole(event.target.value) })} />
    </label>
  );
  return (
    <div className={styles.editor}>
      <div className={styles.amounts}>
        {amount('coins', 'Coins')}
        {allow.fcPoints && amount('fcPoints', 'Aurora Champions Points')}
        {allow.xp && amount('xp', 'XP')}
      </div>
      <fieldset className={styles.lines} disabled={disabled}>
        <legend>Packs and picks</legend>
        {value.lines.length === 0 && <p className="dim">No packs. The reward pays only the amounts above.</p>}
        {value.lines.map((line, index) => {
          const known = line.packId !== undefined && packs.find((pack) => pack.id === line.packId);
          return (
            <div key={index} className={styles.line} role="group" aria-label={`Line ${index + 1}`}>
              {line.cardResourceId !== undefined ? <span className={styles.card}>Player card {line.cardResourceId}</span> : (
                <label className={styles.pack}>
                  <span>Pack</span>
                  <select value={line.packId ?? ''} onChange={(event) => setLine(index, { ...line, packId: Number(event.target.value) })}>
                    {!known && <option value={line.packId ?? ''}>{line.packId === undefined ? 'Choose a pack' : `Pack ${line.packId} (not found)`}</option>}
                    {sorted.map((pack) => <option key={pack.id} value={pack.id}>{pack.name}{pack.pick ? ' (pick)' : ''} · #{pack.id}</option>)}
                  </select>
                </label>
              )}
              <label className={styles.count}>
                <span>Count</span>
                <input type="number" inputMode="numeric" min={1} max={allow.maxCount} step={1} value={Number.isFinite(line.count) ? line.count : ''}
                  onChange={(event) => setLine(index, { ...line, count: whole(event.target.value) })} />
              </label>
              <label className={styles.trade}>
                <span>Tradeability</span>
                <select value={line.untradeable ? 'untradeable' : 'tradeable'}
                  onChange={(event) => setLine(index, { ...line, untradeable: event.target.value === 'untradeable' })}>
                  <option value="tradeable">Tradeable</option>
                  <option value="untradeable">Untradeable</option>
                </select>
              </label>
              <div className={styles.lineActions}>
                {known && known.href && <a href={known.href} className="dim">Open pack</a>}
                <Button variant="ghost" size="sm" aria-label={`Remove line ${index + 1}`}
                  onClick={() => onChange({ ...value, lines: value.lines.filter((_, at) => at !== index) })}>Remove</Button>
              </div>
            </div>
          );
        })}
        <div>
          <Button size="sm" id={`${id}-add`} disabled={disabled || !sorted.length}
            onClick={() => onChange({ ...value, lines: [...value.lines, { packId: sorted[0]!.id, count: 1, untradeable: false }] })}>Add pack</Button>
        </div>
      </fieldset>
    </div>
  );
}
