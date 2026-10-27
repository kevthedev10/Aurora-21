import { describe, expect, it } from 'vitest';
import { mergeCatalogue, usageOf } from './packs';

describe('mergeCatalogue', () => {
  it('lays staged pack and lineup changes over the published catalogue', () => {
    const catalog = { revision: 'abc', groups: ['gold'], publicPackIds: [1, 2],
      packs: [{ id: 1, config: { name: 'Gold', price: 7500 } }, { id: 2, locked: true, config: { name: 'Bronze' } }] };
    const open = { open: true, pending: 3, changeset: { id: 4, changes: [
      { id: 1, area: 'store_pack', key: { packId: 1 }, operation: 'update', after: { pack: { price: 9000 } } },
      { id: 2, area: 'store_pack', key: { packId: 9 }, operation: 'insert', after: { pack: { name: 'New Pack', size: 3 } } },
      { id: 3, area: 'store_lineup', key: { scope: 'public' }, operation: 'update', after: { publicPackIds: [1, 9] } },
    ] } };
    const merged = mergeCatalogue(catalog, open);
    expect(merged.packs.map((pack) => [pack.id, pack.description, pack.pending, pack.coins, pack.items])).toEqual([
      [1, 'Gold', 'Edit', 9000, 12], [2, 'Bronze', undefined, 0, 12], [9, 'New Pack', 'New', 0, 3],
    ]);
    expect(merged.lineup).toEqual([1, 9]);
    expect(merged.lineupPending).toBe(true);
    expect(merged.publicPackIds).toEqual([1, 2]);
  });

  it('shows a staged new pack as unassigned with its chosen art, and where published packs are used', () => {
    const catalog = { revision: 'abc', packs: [{ id: 515, inStore: false, usedBy: ['rivals_rewards.promotion.1.packs[0].packId'], config: { name: 'Pick' } },
      { id: 1, inStore: true, usedBy: ['sbc.rewardPacks.gold'], config: { name: 'Gold' } }, { id: 2, inStore: true, usedBy: [], config: { name: 'Silver' } }] };
    const open = { open: true, pending: 1, changeset: { id: 4, changes: [
      { id: 1, area: 'store_pack', key: { packId: 564 }, operation: 'insert', after: { revision: 'abc', pack: { name: 'New', store_group: 'gold' } } },
    ] } };
    expect(mergeCatalogue(catalog, open).packs.map((pack) => [pack.id, usageOf(pack), pack.art, pack.pending])).toEqual([
      [1, 'store-rewards', undefined, undefined], [2, 'store', undefined, undefined], [515, 'rewards', undefined, undefined], [564, 'unassigned', 'gold', 'New'],
    ]);
  });

  it('marks a pack with a staged delete or force delete as pending Delete', () => {
    const catalog = { revision: 'abc', packs: [{ id: 1, config: { name: 'Gold' } }, { id: 2, config: { name: 'Silver' } }] };
    const open = { open: true, pending: 2, changeset: { id: 4, changes: [
      { id: 1, area: 'store_pack_delete', key: { packId: 1 }, operation: 'hide' },
      { id: 2, area: 'store_pack_force_delete', key: { packId: 2 }, operation: 'hide' },
    ] } };
    expect(mergeCatalogue(catalog, open).packs.map((pack) => [pack.id, pack.pending])).toEqual([[1, 'Delete'], [2, 'Delete']]);
  });
});
