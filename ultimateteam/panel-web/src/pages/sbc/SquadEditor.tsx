// A challenge's squad: its formation, drawn on a pitch, and its locked slots (FIFA's bricks). An open slot is filled by
// the player; a locked slot stays empty, holds a set card, or holds a retail placeholder (a card of a league and nation the
// server picks). The Players in squad requirement follows the open slots (SbcSetArea refuses a mismatch).
import { useState } from 'react';
import { useAssets } from '../../api/assets';
import type { SbcBricks, SbcReply } from '../../api/sbc';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { SwitchRow } from '../../ui/Switch';
import { CardChip, CardPicker } from './CardPicker';
import { formationName } from './SbcSettingsTab';

/** Where each FIFA position sits on the pitch, in percent from the left and the top (attack at the top). */
const SPOTS: Record<string, [number, number]> = {
  GK: [50, 90], SW: [50, 79], RWB: [89, 60], RB: [86, 72], RCB: [66, 76], CB: [50, 76], LCB: [34, 76], LB: [14, 72], LWB: [11, 60],
  RDM: [64, 61], CDM: [50, 61], LDM: [36, 61], RM: [86, 45], RCM: [67, 49], CM: [50, 49], LCM: [33, 49], LM: [14, 45],
  RAM: [69, 34], CAM: [50, 34], LAM: [31, 34], RF: [68, 20], CF: [50, 22], LF: [32, 20], RW: [84, 21], RS: [62, 11], ST: [50, 11],
  LS: [38, 11], LW: [16, 21],
};

type SlotKind = 'open' | 'locked' | 'player' | 'placeholder';

const kindOf = (bricks: SbcBricks | undefined, index: number): SlotKind => {
  if (!bricks || bricks.freeIndices.includes(index)) return 'open';
  if (bricks.players?.[String(index)]) return 'player';
  if (bricks.dummies?.[String(index)]) return 'placeholder';
  return 'locked';
};

/** The bricks with one slot changed, or null when no slot is locked any more. */
function withSlot(bricks: SbcBricks, index: number, kind: SlotKind, value?: number | { league: number; nation: number }): SbcBricks {
  const key = String(index);
  const players = { ...(bricks.players ?? {}) };
  const dummies = { ...(bricks.dummies ?? {}) };
  delete players[key];
  delete dummies[key];
  if (kind === 'player' && typeof value === 'number') players[key] = value;
  if (kind === 'placeholder' && typeof value === 'object') dummies[key] = value;
  const free = new Set(bricks.freeIndices);
  if (kind === 'open') free.add(index); else free.delete(index);
  return {
    freeIndices: [...free].sort((a, b) => a - b),
    ...(Object.keys(players).length && { players }),
    ...(Object.keys(dummies).length && { dummies }),
  };
}

export function SquadEditor({ data, formation, bricks, defaultFormation, onChange }: {
  data: SbcReply;
  formation: string | undefined;
  bricks: SbcBricks | undefined;
  defaultFormation: string;
  onChange: (change: { formation?: string | undefined; bricks?: SbcBricks | undefined }) => void;
}) {
  const [editing, setEditing] = useState<number | null>(null);
  const assets = useAssets();
  const base = assets.data?.baseUrl ?? '/panel/assets/';
  const shown = data.formations.find((entry) => entry.id === (formation || defaultFormation)) ?? data.formations[0];
  const slots = shown?.slots ?? [];
  const open = bricks ? bricks.freeIndices.length : data.squadSize;
  const lockAll = (on: boolean) => onChange({ bricks: on ? { freeIndices: slots.map((_, index) => index).filter((index) => index > 0) } : undefined });

  return (
    <div className="sbc-squad">
      <div className="sbc-squad-controls">
        <Field label="Formation" hint={formation ? 'The formation FIFA shows for this challenge.' : `None set: the default, ${formationName(defaultFormation)}.`}>
          <select value={formation ?? ''} onChange={(event) => onChange({ formation: event.target.value || undefined })}>
            <option value="">Default ({formationName(defaultFormation)})</option>
            {data.formations.map((entry) => <option key={entry.id} value={entry.id}>{formationName(entry.id)}</option>)}
          </select>
        </Field>
        <SwitchRow label="Locked slots" checked={!!bricks} onChange={lockAll}
          text="Some slots come locked: empty, holding a set card, or a placeholder card of a league and nation. Click a slot to change it." />
        <p className="sbc-squad-count"><strong>{open}</strong> open slot{open === 1 ? '' : 's'}: the player places {open} card{open === 1 ? '' : 's'}.</p>
        <ul className="sbc-squad-legend" aria-label="Slot kinds">
          <li><span className="sbc-slot-dot sbc-slot-open" />Open</li>
          <li><span className="sbc-slot-dot sbc-slot-locked" />Locked, empty</li>
          <li><span className="sbc-slot-dot sbc-slot-player" />Locked card</li>
          <li><span className="sbc-slot-dot sbc-slot-placeholder" />Placeholder</li>
        </ul>
      </div>
      <div className="sbc-pitch" role="group" aria-label={`Squad in ${formationName(shown?.id ?? defaultFormation)}`}>
        <div className="sbc-pitch-lines" aria-hidden="true"><span /><span /><span /></div>
        {slots.map((position, index) => {
          const name = data.positions[position] ?? `#${position}`;
          const [left, top] = SPOTS[name] ?? [50, 50];
          const kind = kindOf(bricks, index);
          const player = bricks?.players?.[String(index)];
          const dummy = bricks?.dummies?.[String(index)];
          return (
            <button key={index} type="button" className={`sbc-slot sbc-slot-${kind}`} style={{ left: `${left}%`, top: `${top}%` }}
              disabled={!bricks} onClick={() => setEditing(index)} aria-label={`Slot ${index + 1}, ${name}: ${kind}`}>
              {player ? <CardChip resourceId={player} />
                : dummy ? <span className="sbc-slot-marks">
                    <img src={`${base}leagues/${dummy.league}.png`} alt={`League ${dummy.league}`} />
                    <img src={`${base}nations/${dummy.nation}.png`} alt={`Nation ${dummy.nation}`} />
                  </span>
                : <span className="sbc-slot-icon" aria-hidden="true">{kind === 'locked'
                    ? <svg viewBox="0 0 24 24"><path d="M7 11V8a5 5 0 0 1 10 0v3" /><rect x="5" y="11" width="14" height="10" rx="2" /></svg>
                    : <svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14" /></svg>}</span>}
              <span className="sbc-slot-name">{name}</span>
            </button>
          );
        })}
      </div>
      {editing !== null && bricks && <SlotDialog index={editing} position={data.positions[slots[editing] ?? 0] ?? ''} bricks={bricks} squadSize={slots.length}
        onClose={() => setEditing(null)} onApply={(next) => { onChange({ bricks: next }); setEditing(null); }} />}
    </div>
  );
}

function SlotDialog({ index, position, bricks, squadSize, onApply, onClose }: {
  index: number; position: string; bricks: SbcBricks; squadSize: number; onApply: (bricks: SbcBricks) => void; onClose: () => void;
}) {
  const [kind, setKind] = useState<SlotKind>(kindOf(bricks, index));
  const [player, setPlayer] = useState<number | undefined>(bricks.players?.[String(index)]);
  const [dummy, setDummy] = useState(bricks.dummies?.[String(index)] ?? { league: 0, nation: 0 });
  const [picking, setPicking] = useState<'player' | 'placeholder' | null>(null);
  const next = withSlot(bricks, index, kind, kind === 'player' ? player : kind === 'placeholder' ? dummy : undefined);
  const problem = next.freeIndices.length === 0 ? 'Leave at least one slot open.'
    : next.freeIndices.length >= squadSize ? 'Lock at least one slot, or switch locked slots off.'
    : kind === 'player' && !player ? 'Choose the card.'
    : kind === 'placeholder' && (!(dummy.league > 0) || !(dummy.nation > 0)) ? 'Give the placeholder a league and a nation.' : null;
  const options: [SlotKind, string, string][] = [
    ['open', 'Open', 'The player places a card here.'],
    ['locked', 'Locked, empty', 'Nobody can place a card here.'],
    ['player', 'Locked card', 'A set card sits here and counts toward chemistry.'],
    ['placeholder', 'Placeholder', 'The server puts a card of this league and nation here (retail Foundations).'],
  ];
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose(); }} title={`Slot ${index + 1} · ${position}`}
      footer={<>
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={!!problem} onClick={() => onApply(next)}>Apply</Button>
      </>}>
      <div className="stack">
        <div className="sbc-choice" role="radiogroup" aria-label="Slot">
          {options.map(([value, label, text]) => (
            <label key={value} className={kind === value ? 'sbc-choice-on' : undefined}>
              <input type="radio" name="slot-kind" value={value} checked={kind === value} onChange={() => setKind(value)} />
              <span><strong>{label}</strong><small className="dim">{text}</small></span>
            </label>
          ))}
        </div>
        {kind === 'player' && (
          <div className="sbc-inline">
            {player ? <CardChip resourceId={player} size="thumbnail" /> : <span className="dim">No card chosen.</span>}
            <Button size="sm" onClick={() => setPicking('player')}>{player ? 'Change card…' : 'Choose card…'}</Button>
          </div>
        )}
        {kind === 'placeholder' && (
          <div className="sbc-form">
            <Field label="League id"><input type="number" min={1} value={dummy.league || ''} onChange={(event) => setDummy({ ...dummy, league: Number(event.target.value) })} /></Field>
            <Field label="Nation id"><input type="number" min={1} value={dummy.nation || ''} onChange={(event) => setDummy({ ...dummy, nation: Number(event.target.value) })} /></Field>
            <div className="sbc-inline"><Button size="sm" onClick={() => setPicking('placeholder')}>Copy from a card…</Button></div>
          </div>
        )}
        {problem && <p className="dim" role="status">{problem}</p>}
      </div>
      <CardPicker open={picking !== null} title={picking === 'player' ? 'Choose the locked card' : 'Copy a league and nation'}
        description={picking === 'placeholder' ? 'The placeholder takes this card’s league and nation; the server picks the card it shows.' : undefined}
        onClose={() => setPicking(null)} onPick={(card) => {
          if (picking === 'player') setPlayer(card.resourceId);
          else setDummy({ league: Number(card.leagueId ?? card.art?.leagueId ?? 0), nation: Number(card.art?.nationId ?? 0) });
          setPicking(null);
        }} />
    </Dialog>
  );
}
