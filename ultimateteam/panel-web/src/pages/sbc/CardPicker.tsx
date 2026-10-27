// Picks one card by name, for a locked player, a placeholder's league and nation, or a player reward.
import { useEffect, useState } from 'react';
import { useAssets } from '../../api/assets';
import { useCard, useCardSearch } from '../../api/sbc';
import type { CardRow } from '../../api/types';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { FutCard } from '../../ui/FutCard';

export function CardPicker({ open, title, description, onPick, onClose }: {
  open: boolean;
  title: string;
  description?: string | undefined;
  onPick: (card: CardRow) => void;
  onClose: () => void;
}) {
  const [text, setText] = useState('');
  const [query, setQuery] = useState('');
  useEffect(() => {
    const timer = window.setTimeout(() => setQuery(text.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [text]);
  const search = useCardSearch(query);
  const assets = useAssets();
  const cards = search.data ?? [];
  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next) onClose(); }} title={title} description={description} wide
      footer={<Button variant="ghost" onClick={onClose}>Cancel</Button>}>
      <div className="stack">
        <Field label="Player name" hint="Type at least two letters.">
          <input type="search" autoFocus value={text} placeholder="Mbappé, Van Dijk…" onChange={(event) => setText(event.target.value)} />
        </Field>
        {query.length >= 2 && !search.isPending && !cards.length && <p className="dim">No card matches “{query}”.</p>}
        <ul className="sbc-card-results" aria-busy={search.isFetching}>
          {cards.map((card) => (
            <li key={card.cardKey}>
              <button type="button" onClick={() => onPick(card)} aria-label={`${card.name ?? 'Card'} ${card.rating ?? ''} ${card.position ?? ''}`}>
                <FutCard art={card.art} assets={assets.data} size="xs" />
                <span className="sbc-card-results-name">{card.name ?? `Card ${card.resourceId}`}</span>
                <span className="dim">{card.rating} · {card.position}{card.special ? ` · ${card.special}` : ''}</span>
              </button>
            </li>
          ))}
        </ul>
      </div>
    </Dialog>
  );
}

/** A card shown by resource id: its face when the art is downloaded, else its name, rating and position. */
export function CardChip({ resourceId, size = 'xs' }: { resourceId: number; size?: 'xs' | 'thumbnail' }) {
  const card = useCard(resourceId);
  const assets = useAssets();
  const row = card.data;
  return (
    <span className="sbc-card-chip" title={row ? `${row.name} ${row.rating} ${row.position}` : `Card ${resourceId}`}>
      {row?.art && assets.data?.available ? <FutCard art={row.art} assets={assets.data} size={size} /> : null}
      <span>
        <strong>{row?.name ?? (card.isPending ? 'Loading…' : `Card ${resourceId}`)}</strong>
        {row && <small className="dim">{row.rating} · {row.position}</small>}
      </span>
    </span>
  );
}
