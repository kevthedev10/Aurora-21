// Ctrl K: jump to a page, a player, a card or a pack (plan step 3.4). Players and packs are filtered in the browser from
// their list replies; cards use the server's own card search. Nothing here changes anything.
import * as Radix from '@radix-ui/react-dialog';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { ModalSurface } from '../ui/ModalSurface';
import { useAssets } from '../api/assets';
import { api } from '../api/client';
import type { CardsReply, PackCatalogReply, PlayersReply } from '../api/types';
import { FutCard } from '../ui/FutCard';
import { searchText } from '../ui/search';
import { PAGES } from './routes';
import { href } from './router';
import styles from './CommandPalette.module.css';

interface Item { id: string; group: 'Pages' | 'Players' | 'Cards' | 'Packs'; label: string; detail?: string | undefined; to: string; lead?: ReactNode }

const matches = (query: string, ...values: (string | number | null | undefined)[]) =>
  values.some((value) => value != null && searchText(value).includes(searchText(query)));

function useDebounced<T>(value: T, ms: number): T {
  const [current, setCurrent] = useState(value);
  useEffect(() => {
    const timer = window.setTimeout(() => setCurrent(value), ms);
    return () => window.clearTimeout(timer);
  }, [value, ms]);
  return current;
}

export function CommandPalette({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const [text, setText] = useState('');
  const [active, setActive] = useState(0);
  const list = useRef<HTMLUListElement>(null);
  const query = text.trim().toLowerCase();
  const cardQuery = useDebounced(query, 250);
  const assets = useAssets().data;
  const players = useQuery({ queryKey: ['players'], queryFn: () => api<PlayersReply>('players'), enabled: open });
  const packs = useQuery({ queryKey: ['packs-catalog'], queryFn: () => api<PackCatalogReply>('packs/catalog'), enabled: open });
  const cards = useQuery({
    queryKey: ['cards-search', cardQuery],
    queryFn: () => api<CardsReply>(`cards?${new URLSearchParams({ q: cardQuery, limit: '8' })}`),
    enabled: open && cardQuery.length >= 2,
  });

  const items = useMemo<Item[]>(() => {
    const list: Item[] = PAGES.filter((page) => page.group && (!query || matches(query, page.label, page.group)))
      .map((page) => ({ id: `page-${page.id}`, group: 'Pages', label: page.label, detail: page.group, to: href(page.path) }));
    if (query) {
      for (const player of (players.data?.players ?? []).filter((row) => matches(query, row.name, row.id, row.discordId, row.club?.name)).slice(0, 8)) {
        list.push({ id: `player-${player.id}`, group: 'Players', label: player.name ?? `Persona ${player.id}`, detail: `persona ${player.id}${player.online ? ' · online' : ''}`, to: href('players', player.id) });
      }
      if (query === cardQuery) {
        for (const card of cards.data?.cards ?? []) {
          list.push({ id: `card-${card.cardKey}`, group: 'Cards', label: `${card.name ?? 'Card'} ${card.rating ?? ''}`, detail: [card.position, card.special].filter(Boolean).join(' · '),
            to: href('cards', card.cardKey), lead: <FutCard art={card.art} assets={assets} size="xs" /> });
        }
      }
      for (const pack of (packs.data?.packs ?? []).filter((row) => matches(query, row.description, row.id)).slice(0, 6)) {
        list.push({ id: `pack-${pack.id}`, group: 'Packs', label: pack.description ?? `Pack ${pack.id}`, detail: `#${pack.id}`, to: href('packs', 'catalogue', pack.id) });
      }
    }
    return list;
  }, [query, cardQuery, players.data, cards.data, packs.data, assets]);

  useEffect(() => setActive(0), [query]);
  useEffect(() => { if (!open) setText(''); }, [open]);
  useEffect(() => setActive((index) => Math.max(0, Math.min(index, items.length - 1))), [items.length]);
  useEffect(() => {
    if (open) list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView?.({ block: 'nearest' });
  }, [active, open]);

  const go = (item: Item | undefined) => {
    if (!item) return;
    onOpenChange(false);
    window.location.hash = item.to;
  };
  const onKey = (event: KeyboardEvent) => {
    if (event.key === 'ArrowDown') { event.preventDefault(); setActive((index) => Math.max(0, Math.min(items.length - 1, index + 1))); }
    else if (event.key === 'ArrowUp') { event.preventDefault(); setActive((index) => Math.max(0, index - 1)); }
    else if (event.key === 'Enter') { event.preventDefault(); go(items[active]); }
  };
  const searching = query.length >= 2 && (players.isFetching || cards.isFetching || packs.isFetching || query !== cardQuery);

  return (
    <ModalSurface open={open} onOpenChange={onOpenChange} overlayClass={styles.overlay}
      contentClass={styles.panel} centered>
          <Radix.Title className="visually-hidden">Go to</Radix.Title>
          <input
            className={styles.input}
            role="combobox"
            aria-expanded="true"
            aria-controls="palette-list"
            aria-activedescendant={items[active] ? `palette-${items[active].id}` : undefined}
            aria-label="Search pages, players, cards and packs"
            placeholder="Search pages, players, cards and packs"
            value={text}
            onChange={(event) => setText(event.target.value)}
            onKeyDown={onKey}
            autoFocus
          />
          <ul ref={list} className={styles.list} id="palette-list" role="listbox" aria-label="Results">
            {items.map((item, index) => (
              <li key={item.id} role="presentation">
                {(index === 0 || items[index - 1]?.group !== item.group) && <div className={styles.group} role="presentation">{item.group}</div>}
                <div
                  id={`palette-${item.id}`}
                  role="option"
                  aria-selected={index === active}
                  className={styles.option}
                  onMouseMove={() => setActive(index)}
                  onClick={() => go(item)}
                >
                  {item.lead}
                  <span className={styles.label}>{item.label}</span>
                  {item.detail && <span className={styles.detail}>{item.detail}</span>}
                </div>
              </li>
            ))}
          </ul>
          {!items.length && <p className={styles.empty}>{searching ? 'Searching…' : 'Nothing matches.'}</p>}
          <div className={styles.foot}><span>↑ ↓ to move</span><span>Enter to open</span><span>Esc to close</span></div>
    </ModalSurface>
  );
}
