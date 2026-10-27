import { describe, expect, it } from 'vitest';
import { changeTitle, fieldChanges, keyLabel } from './changes';

describe('change summaries', () => {
  it('names the row', () => {
    expect(keyLabel({ file: 'market', path: ['maintenanceIntervalSeconds'] })).toBe('market › maintenanceIntervalSeconds');
    expect(keyLabel({ packId: 7 })).toBe('pack 7');
    expect(keyLabel({ resourceId: 158023 })).toBe('card 158023');
  });

  it('lists changed fields only, with a cap', () => {
    const change = { id: 1, area: 'store_pack', key: { packId: 7 }, operation: 'update',
      before: { pack: 1, price: 7500, size: 12, name: 'Gold', bio: 'x' }, after: { pack: 1, price: 9000, size: 11, name: 'Gold+', bio: 'y' } };
    expect(fieldChanges(change, 2)).toEqual(['price: 7500 → 9000', 'size: 12 → 11', 'and 2 more']);
    expect(fieldChanges({ ...change, before: { value: 30 }, after: { value: 31 } })).toEqual(['value: 30 → 31']);
    expect(fieldChanges({ ...change, before: 30, after: 31 })).toEqual(['30 → 31']);
    expect(fieldChanges({ ...change, operation: 'hide' })).toEqual(['hidden from the game']);
    expect(fieldChanges({ ...change, before: null, after: { price: 1 } })).toEqual(['null → {"price":1}']);
    expect(fieldChanges({ id: 2, area: 'player_card', key: { resourceId: 158023 }, operation: 'update',
      before: { card: { rating: 93, position: 'RW', traits: [1] } }, after: { card: { rating: 94, position: 'RW', traits: [1, 2] } } }))
      .toEqual(['card.rating: 93 → 94', 'card.traits: [1] → [1,2]']);
  });

  it('summarises the live SBC list and an SBC removal', () => {
    const listing = (...entries: [string, boolean][]) => ({ listing: entries.map(([setKey, enabled]) => ({ setKey, enabled })) });
    expect(fieldChanges({ id: 5, area: 'sbc_live', key: { scope: 'live' }, operation: 'update',
      before: listing(['UPG_GOLD', true], ['UPG_SILVER', true]), after: listing(['UPG_GOLD', false], ['UP_82', true]) }))
      .toEqual(['UP_82 listed, on', 'UPG_GOLD switched off', 'UPG_SILVER taken off Live SBCs']);
    const names: Record<string, string> = { UP_82: '82+ Player Pick', UPG_GOLD: 'Gold Upgrade', UPG_SILVER: 'Silver Upgrade' };
    expect(fieldChanges({ id: 5, area: 'sbc_live', key: { scope: 'live' }, operation: 'update',
      before: listing(['UPG_GOLD', true], ['UPG_SILVER', true]), after: listing(['UPG_GOLD', false], ['UP_82', true]) }, 3, (key) => names[key] ?? key))
      .toEqual(['82+ Player Pick listed, on', 'Gold Upgrade switched off', 'Silver Upgrade taken off Live SBCs']);
    expect(changeTitle({ id: 7, area: 'sbc_set', key: { setKey: 'FND_LETS_GET_STARTED' }, operation: 'update', before: null,
      after: { definition: { name: "Let's Get Started" } } })).toBe("Let's Get Started");
    expect(changeTitle({ id: 8, area: 'sbc_live', key: { scope: 'live' }, operation: 'update', before: null, after: null })).toBe('Live SBCs');
    expect(fieldChanges({ id: 6, area: 'sbc_set', key: { setKey: 'MY_SET' }, operation: 'hide', before: {}, after: null }))
      .toEqual(['removed from the catalogue, or a bundled SBC reverted to its sbc.json definition']);
  });

  it('reviews nested pack and promo fields without hiding changes inside a JSON row', () => {
    expect(fieldChanges({ id: 3, area: 'store_pack', key: { packId: 7 }, operation: 'update',
      before: { revision: 'old', pack: { name: 'Gold', price: 7500, size: 12 } },
      after: { revision: 'old', pack: { name: 'Gold', price: 9000, size: 12 } } }))
      .toEqual(['pack.price: 7500 → 9000']);
    expect(fieldChanges({ id: 4, area: 'promo', key: { code: 'totw_1' }, operation: 'update',
      before: { promo: { name: 'TOTW 1', draw_weight: 1 }, buckets: [1] },
      after: { promo: { name: 'TOTW 1', draw_weight: 2 }, buckets: [1] } }))
      .toEqual(['promo.draw_weight: 1 → 2']);
  });
});
