// A challenge's or set's rewards: packs (by id), player picks (random or fixed), coins, XP, a player card or a manager league
// card, as SbcService pays them. A player pick is a PACK reward naming a pick pack, made under Packs / Picks.
import { useState } from 'react';
import { href } from '../../app/router';
import type { SbcReply, SbcReward } from '../../api/sbc';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { CardChip, CardPicker } from './CardPicker';

// PICK is the editor's name for a PACK reward that pays a player pick pack (random, or fixed: chosen cards).
const TYPES: [string, string][] = [['PACK', 'Pack'], ['PICK', 'Player pick'], ['COINS', 'Coins'], ['XP', 'XP'], ['PLAYER', 'Player card'],
  ['MANAGER_LEAGUE', 'Manager league card']];

/** "2× Gold Pack", "5,000 coins", "Pelé", for a set's summary. */
export function rewardText(data: SbcReply, entry: SbcReward): string {
  const attributes = entry.attributes ?? {};
  switch (entry.type.toUpperCase()) {
    case 'PACK': {
      const id = Number(attributes.packId ?? 0);
      const count = Number(attributes.count ?? 1);
      return `${count > 1 ? `${count}× ` : ''}${data.packs.find((pack) => pack.id === id)?.name ?? `Pack ${id}`}`;
    }
    case 'COINS': return `${num(Number(entry.value ?? 0))} coins`;
    case 'XP': return `${num(Number(entry.value ?? 0))} XP`;
    case 'PLAYER': return String(attributes.name ?? `Card ${attributes.resourceId}`);
    case 'MANAGER_LEAGUE': return String(attributes.name ?? `Manager league ${attributes.league}`);
    default: return entry.type;
  }
}

export function RewardsEditor({ data, entries, onChange }: { data: SbcReply; entries: SbcReward[]; onChange: (entries: SbcReward[]) => void }) {
  const [picking, setPicking] = useState<number | null>(null);
  const set = (index: number, entry: SbcReward) => onChange(entries.map((current, at) => at === index ? entry : current));
  const pack = (id: number, count = 1): SbcReward => ({ type: 'PACK', attributes: { packId: id, count, name: data.packs.find((entry) => entry.id === id)?.name ?? `Pack ${id}` } });
  const league = (id: number): SbcReward => ({ type: 'MANAGER_LEAGUE', attributes: { league: id, name: data.managerLeagues.find((entry) => entry.league === id)?.name ?? `League ${id}` } });
  const fresh = (type: string): SbcReward => type === 'PACK' ? pack(data.packs.find((option) => !option.pick)?.id ?? 0)
    : type === 'PICK' ? pack(data.packs.find((option) => option.pick)?.id ?? 0)
    : type === 'PLAYER' ? { type, attributes: {} }
    : type === 'MANAGER_LEAGUE' ? league(data.managerLeagues[0]?.league ?? 0)
    : { type, value: type === 'XP' ? 100 : 1000 };
  const packs = [...data.packs].sort((a, b) => a.name.localeCompare(b.name, 'en') || a.id - b.id);
  const isPick = (id: number) => !!data.packs.find((option) => option.id === id)?.pick;
  
  return (
    <div className="sbc-rewards">
      {!entries.length && <p className="dim">No rewards.</p>}
      <ul className="sbc-rule-list">
        {entries.map((entry, index) => {
          const attributes = entry.attributes ?? {};
          const packId = Number(attributes.packId ?? 0);
          const type = entry.type.toUpperCase() === 'PACK' && isPick(packId) ? 'PICK' : entry.type.toUpperCase();
          const resourceId = Number(attributes.resourceId ?? 0);
          return (
            <li key={index} className="sbc-rule">
              <div className="sbc-rule-fields">
                <Field label="Reward">
                  <select value={type} onChange={(event) => { set(index, fresh(event.target.value)); if (event.target.value === 'PLAYER') setPicking(index); }}>
                    {TYPES.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
                  </select>
                </Field>
                {type === 'PACK' && <>
                  <Field label="Pack">
                    <select value={packId} onChange={(event) => set(index, pack(Number(event.target.value), Number(attributes.count ?? 1)))}>
                      {!data.packs.some((option) => option.id === packId) && <option value={packId}>{packId ? `Pack ${packId} (not in the catalogue)` : 'Choose a pack'}</option>}
                      {packs.filter((option) => !option.pick).map((option) => <option key={option.id} value={option.id}>{option.name} · #{option.id}{option.enabled ? '' : ' (not sold)'}</option>)}
                    </select>
                  </Field>
                  <Field label="Count">
                    <input type="number" min={1} max={10} value={Number(attributes.count ?? 1)}
                      onChange={(event) => set(index, pack(packId, Math.max(1, Math.min(10, Number(event.target.value) || 1))))} />
                  </Field>
                </>}
                {type === 'PICK' && <>
                  <Field label="Player pick" hint={<a href={`${href('packs', 'catalogue', 'new')}?type=picks`}>Make a new pick (random or fixed) under Packs / Picks</a>}>
                    <select value={packId} onChange={(event) => set(index, pack(Number(event.target.value), Number(attributes.count ?? 1)))}>
                      <optgroup label="Random player picks">
                        {packs.filter((option) => option.pick && !option.fixed).map((option) => <option key={option.id} value={option.id}>{option.name} · #{option.id}</option>)}
                      </optgroup>
                      <optgroup label="Fixed player picks (chosen cards)">
                        {packs.filter((option) => option.fixed).map((option) => <option key={option.id} value={option.id}>{option.name} · #{option.id}</option>)}
                      </optgroup>
                    </select>
                  </Field>
                  <Field label="Count">
                    <input type="number" min={1} max={10} value={Number(attributes.count ?? 1)}
                      onChange={(event) => set(index, pack(packId, Math.max(1, Math.min(10, Number(event.target.value) || 1))))} />
                  </Field>
                </>}
                {(type === 'COINS' || type === 'XP') && (
                  <Field label="Amount">
                    <input type="number" min={1} max={10_000_000} value={Number(entry.value ?? 0)}
                      onChange={(event) => set(index, { type, value: Math.max(1, Number(event.target.value) || 1) })} />
                  </Field>
                )}
                {type === 'MANAGER_LEAGUE' && (
                  <Field label="League">
                    <select value={Number(attributes.league ?? 0)} onChange={(event) => set(index, league(Number(event.target.value)))}>
                      {data.managerLeagues.map((option) => <option key={option.league} value={option.league}>{option.name}</option>)}
                    </select>
                  </Field>
                )}
                {type === 'PLAYER' && (
                  <div className="sbc-control-group sbc-reward-card">
                    <span className="sbc-control-label">Card</span>
                    <div className="sbc-control-box">
                      {resourceId || attributes.name ? <CardChip resourceId={resourceId} /> : <span className="dim">No card chosen</span>}
                      <Button size="sm" onClick={() => setPicking(index)}>{resourceId ? 'Change…' : 'Choose card…'}</Button>
                    </div>
                    {attributes.name && <span className="sbc-control-hint">Authored as {String(attributes.name)} {String(attributes.rating ?? '')} {String(attributes.position ?? '')}</span>}
                  </div>
                )}
              </div>
              <Button size="sm" variant="ghost" className="sbc-rule-remove" aria-label="Remove reward" onClick={() => onChange(entries.filter((_, at) => at !== index))}>Remove</Button>
            </li>
          );
        })}
      </ul>
      <Button size="sm" onClick={() => onChange([...entries, fresh('PACK')])}>Add reward</Button>
      {/* A picked card is paid exactly (resourceId, no name): SbcService matches authored names only when there is one. */}
      <CardPicker open={picking !== null} title="Choose the reward card" onClose={() => setPicking(null)}
        onPick={(card) => { if (picking !== null && card.resourceId) set(picking, { type: 'PLAYER', attributes: { resourceId: card.resourceId } }); setPicking(null); }} />
    </div>
  );
}
