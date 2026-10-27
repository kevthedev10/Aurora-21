type Values = Record<string, unknown>;
const formulas: [string, number][][] = [
  [['acceleration', .45], ['sprint_speed', .55]],
  [['positioning', .05], ['finishing', .45], ['shot_power', .2], ['long_shots', .2], ['volleys', .05], ['penalties', .05]],
  [['vision', .2], ['crossing', .2], ['free_kick_accuracy', .05], ['short_passing', .35], ['long_passing', .15], ['curve', .05]],
  [['agility', .1], ['balance', .05], ['ball_control', .35], ['dribbling', .5]],
  [['interceptions', .2], ['heading_accuracy', .1], ['marking', .3], ['standing_tackle', .3], ['sliding_tackle', .1]],
  [['jumping', .05], ['stamina', .25], ['strength', .5], ['aggression', .2]],
];
const keeperFields = ['gk_diving', 'gk_handling', 'gk_kicking', 'gk_reflexes', 'gk_speed', 'gk_positioning'];
const valid = (value: unknown): number | null => {
  if (value === null || value === undefined || value === '') return null;
  const number = Number(value);
  return Number.isInteger(number) && number >= 1 && number <= 99 ? number : null;
};
export function faceValue(card: Values, slot: number): number | null {
  if (card.position === 'GK') return valid(card[keeperFields[slot]!]);
  const terms = formulas[slot]!;
  if (terms.some(([key]) => valid(card[key]) === null)) return null;
  return Math.round(terms.reduce((sum, [key, weight]) => sum + Number(card[key]) * weight, 0));
}
/** Detailed edits recalculate their face slot. Face edits shift contributing attributes equally,
 * then distribute any residual after clamping; unrelated fields and relative differences stay intact. */
export function updateStats(current: Values, key: string, value: unknown): Values {
  const next = { ...current, [key]: value };
  if (key === 'position') {
    for (let index = 0; index < 6; index++) {
      const calculated = faceValue(next, index);
      if (calculated !== null) next[`face_slot_${index}`] = calculated;
    }
    return next;
  }
  if (valid(value) === null) return next;
  const slot = key.startsWith('face_slot_') ? Number(key.slice(10)) : -1;
  if (slot >= 0 && slot < 6) {
    const target = Number(value);
    if (current.position === 'GK') { next[keeperFields[slot]!] = target; return next; }
    const terms = formulas[slot]!;
    // Fill missing contributors with the requested aggregate rather than treating null as zero.
    for (const [field] of terms) next[field] = valid(current[field]) ?? target;
    for (let pass = 0; pass < 8; pass++) {
      const average = terms.reduce((sum, [field, weight]) => sum + Number(next[field]) * weight, 0);
      const delta = target - average;
      if (Math.round(average) === target) break;
      const movable = terms.filter(([field]) => delta > 0 ? Number(next[field]) < 99 : Number(next[field]) > 1);
      const totalWeight = movable.reduce((sum, [, weight]) => sum + weight, 0);
      if (!totalWeight) break;
      let changed = false;
      for (const [field] of movable) {
        const adjusted = Math.max(1, Math.min(99, Math.round(Number(next[field]) + delta / totalWeight)));
        changed ||= adjusted !== next[field]; next[field] = adjusted;
      }
      if (!changed) {
        const [field] = [...movable].sort((a, b) => b[1] - a[1])[0]!;
        next[field] = Number(next[field]) + Math.sign(delta);
      }
    }
    next[key] = faceValue(next, slot) ?? target;
  } else {
    const affected = current.position === 'GK' ? keeperFields.indexOf(key) : formulas.findIndex((terms) => terms.some(([field]) => field === key));
    if (affected >= 0) {
      const calculated = faceValue(next, affected);
      if (calculated !== null) next[`face_slot_${affected}`] = calculated;
    }
  }
  return next;
}
