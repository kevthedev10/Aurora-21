import { describe, expect, it } from 'vitest';
import { faceValue, updateStats } from './cardStats';
import { editableCard, cardEditorReply } from '../../../test/cardFixtures';
import { typeForRating } from './CardTypePicker';

describe('card stat editing', () => {
  it('uses the supplied formulas, excludes reactions/composure, and maps native field names', () => {
    const card = { ...editableCard, acceleration: 80, sprint_speed: 90, free_kick_accuracy: 90, marking: 90 };
    expect(faceValue(card, 0)).toBe(86);
    expect(faceValue(card, 2)).toBe(71);
    expect(faceValue(card, 4)).toBe(76);
    expect(updateStats(card, 'reactions', 99).face_slot_3).toBe(card.face_slot_3);
    expect(updateStats(card, 'composure', 99).face_slot_3).toBe(card.face_slot_3);
    expect(updateStats(card, 'acceleration', 90).face_slot_0).toBe(90);
  });
  it('updates contributing attributes in reverse and reaches the requested rounded aggregate at bounds', () => {
    for (const slot of [0, 1, 2, 3, 4, 5]) for (const target of [1, 40, 85, 99]) {
      const changed = updateStats({ ...editableCard, acceleration: 1, sprint_speed: 99 }, `face_slot_${slot}`, target);
      expect(faceValue(changed, slot)).toBe(target);
      expect(changed.reactions).toBe((editableCard as Record<string, unknown>).reactions);
      expect(changed.composure).toBe((editableCard as Record<string, unknown>).composure);
    }
  });
  it('links goalkeeper face and detailed stats directly, preserving blank invalid edits', () => {
    const keeper = { ...editableCard, position: 'GK' };
    expect(updateStats(keeper, 'face_slot_0', 92).gk_diving).toBe(92);
    expect(updateStats(keeper, 'gk_handling', 88).face_slot_1).toBe(88);
    expect(updateStats(editableCard, 'acceleration', '').face_slot_0).toBe(editableCard.face_slot_0);
  });
  it('maps standard rarity to the rating band without changing special types', () => {
    const types = cardEditorReply.options.cardTypes;
    expect([64, 65, 74, 75].map((rating) => typeForRating(types, 1, rating)?.id)).toEqual([3, 2, 2, 1]);
    expect(typeForRating(types, 5, 60)?.id).toBe(5);
  });
});
