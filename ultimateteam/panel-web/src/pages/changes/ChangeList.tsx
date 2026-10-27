// Staged or published changes, grouped by content area, each with its row, operation and field-by-field diff.
import type { ContentChange } from '../../api/types';
import { useSbcNames } from '../../api/sbc';
import { areaLabel, changeTitle, fieldChanges, reviewValues, unopenedText } from '../../app/changes';
import { Badge, type Tone } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { href } from '../../app/router';
import { Section } from '../../ui/Layout';

const OPERATION: Record<string, [Tone, string]> = { insert: ['pitch', 'New'], update: ['staged', 'Edit'], hide: ['red', 'Remove'] };
const show = (value: unknown) => (value === undefined ? '—' : value === null ? 'null' : typeof value === 'object' ? JSON.stringify(value, null, 2) : String(value));
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

const removal = (change: ContentChange) => change.area === 'store_pack_force_delete'
  ? `This pack definition is deleted when published, and ${unopenedText(change.before)} are removed from the clubs that own them (no coins are refunded). Items already opened from it stay.`
  : change.area === 'store_pack_delete' ? 'This pack definition is deleted when published, with its purchase counters. No club owns an unopened copy.'
  : change.area === 'rules' ? 'This rule is removed when published.' : 'This entry is removed from published availability. Existing club items are kept.';

export function Diff({ change }: { change: ContentChange }) {
  if (change.operation === 'hide') return <div className="change-removal"><p className="dim">{removal(change)}</p><details><summary>View removed value</summary><pre>{show(change.before)}</pre></details></div>;
  const insert = change.operation === 'insert';
  const { before, after } = reviewValues(change);
  if (!after) return <div className="diff"><div className="diff-row"><span className="diff-key">Value</span><pre className="diff-before">{show(change.before)}</pre><span className="dim" aria-hidden="true">→</span><pre className="diff-after">{show(change.after)}</pre></div></div>;
  const fields = [...new Set([...Object.keys(after), ...Object.keys(before)])].filter((field) => insert || !same(before[field], after[field]));
  if (!fields.length) return <span className="dim">No field changes.</span>;
  return (
    <div className="diff">
      <div className="diff-labels" aria-hidden="true"><span>Field</span><span>Before</span><span /><span>After</span></div>
      {fields.map((field) => (
        <div key={field} className="diff-row">
          <span className="mono diff-key">{field}</span>
          <pre className="diff-before">{insert ? '—' : show(before[field])}</pre><span className="dim" aria-hidden="true">→</span>
          <pre className="diff-after">{show(after[field])}</pre>
        </div>
      ))}
    </div>
  );
}

export function ChangeList({ changes, onRemove, busy }: { changes: ContentChange[]; onRemove?: (change: ContentChange) => void; busy?: boolean }) {
  const sbcName = useSbcNames(changes.some((change) => change.area === 'sbc_live'));
  const byArea = new Map<string, ContentChange[]>();
  for (const change of changes) byArea.set(change.area, [...(byArea.get(change.area) ?? []), change]);
  return (
    <>
      {[...byArea].map(([area, list]) => (
        <Section key={area} title={areaLabel(area)} actions={<span className="dim">{list.length} change{list.length === 1 ? '' : 's'}</span>}>
          <ul className="change-list">
            {list.map((change) => {
              const [tone, label] = OPERATION[change.operation] ?? ['neutral', change.operation];
              return (
                <li key={change.id}>
                  <div className="change-head">
                    <b className={change.area === 'sbc_set' || change.area === 'sbc_live' ? undefined : 'mono'}>{changeTitle(change)}</b>
                    <Badge tone={tone}>{label}</Badge>
                    {onRemove && change.area === 'player_card' && Number(change.key.resourceId) > 0 && (() => {
                      const base = (change.after as { base?: number } | undefined)?.base;
                      const address = change.operation === 'insert' && base ? `${href('cards', base, 'new')}?draft=${change.key.resourceId}` : href('cards', Number(change.key.resourceId), 'edit');
                      return <LinkButton size="sm" variant="staged" href={address}>Edit staged card</LinkButton>;
                    })()}
                    {onRemove && <Button size="sm" variant="ghost" disabled={busy} onClick={() => onRemove(change)} title="Drop this change; the row keeps its live value">Remove</Button>}
                  </div>
                  {change.area === 'sbc_live'
                    ? <ul className="pending-fields">{fieldChanges(change, 500, sbcName).map((line) => <li key={line}>{line}</li>)}</ul>
                    : <Diff change={change} />}
                </li>
              );
            })}
          </ul>
        </Section>
      ))}
    </>
  );
}
