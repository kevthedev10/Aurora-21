import { api } from '../../api/client';
import type { CardRow, CardsReply } from '../../api/types';

/** Resolve the draft's membership, including cards not yet in the published promo. */
export async function loadMembers(ids: number[], signal: AbortSignal): Promise<CardRow[]> {
  const rows: CardRow[] = [];
  for (let offset = 0; offset < ids.length; offset += 100) {
    const batch = ids.slice(offset, offset + 100);
    const reply = await api<CardsReply>(`cards?resourceIds=${batch.join(',')}&limit=200`, { signal });
    const found = new Map(reply.cards?.map(card => [card.resourceId, card]));
    rows.push(...batch.map(id => found.get(id) ?? { cardKey: `native:${id}`, resourceId: id, name: `Card ${id}` }));
  }
  return rows;
}
export function filterMembers(cards: CardRow[], query: string) {
  const fold = (value: string) => value.normalize('NFKD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
  const needle = fold(query.trim());
  return cards.filter(card => fold(`${card.name ?? ''} ${card.resourceId} ${card.rating ?? ''} ${card.position ?? ''} ${card.special ?? ''}`).includes(needle));
}
