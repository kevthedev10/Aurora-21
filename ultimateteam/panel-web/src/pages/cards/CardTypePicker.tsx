import { useEffect, useId, useRef, useState } from 'react';
import type { AssetsReply } from '../../api/assets';
import type { CardEditorReply } from '../../api/types';
import { band, frameFor } from '../../ui/FutCard';

type CardType = CardEditorReply['options']['cardTypes'][number];
export function typeForRating(types: CardType[], selected: number | null, rating: number): CardType | undefined {
  const type = types.find((item) => item.id === selected);
  if (!type) return type;
  if (type.rareflag === 3 && rating < 65) return types.find((item) => item.rareflag === 1 && item.level?.toLowerCase() === 'bronze') ?? type;
  return types.find((item) => item.rareflag === type.rareflag && item.level?.toLowerCase() === band(rating)) ?? type;
}
export const isIconType = (type: CardType | undefined) => type?.rareflag === 12 || type?.rareflag === 84;
export const allowedCardTypes = (types: CardType[], icon: boolean, rating = 99) => types.filter((type) =>
  ![89, 92, 93, 94].includes(type.rareflag) && !/icon\s*swaps/i.test(type.name) && isIconType(type) === icon
  && (type.rareflag !== 3 || rating >= 65));
export function CardTypePicker({ types, value, rating, assets, onChange, icon = false }: {
  types: CardType[]; value: number | null; rating: number; assets?: AssetsReply | null | undefined; onChange: (id: number) => void; icon?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const id = useId();
  const opener = useRef<HTMLButtonElement>(null);
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false); };
    document.addEventListener('pointerdown', outside);
    return () => document.removeEventListener('pointerdown', outside);
  }, [open]);
  const selected = typeForRating(types, value, rating);
  const allowed = allowedCardTypes(types, icon, rating);
  const choices = allowed.filter((type, index) => allowed.findIndex((item) => item.rareflag === type.rareflag) === index)
    .map((type) => typeForRating(types, type.id, rating)!);
  const label = (type: CardType) => type.rareflag === 0 ? 'Non-rare' : type.rareflag === 1 ? 'Rare' : type.name.replace(/\b(?:gold|silver|bronze)\b/gi, '').replace(/\s+/g, ' ').trim();
  const close = () => { setOpen(false); opener.current?.focus(); };
  return <div className="card-type-picker" ref={root} onBlur={(event) => {
    if (event.relatedTarget && !event.currentTarget.contains(event.relatedTarget as Node)) setOpen(false);
  }}
    onKeyDown={(event) => { if (event.key === 'Escape') { event.preventDefault(); close(); } }}>
    <span className="dim" id={`${id}-label`}>Card type</span>
    <button type="button" ref={opener} aria-label="Choose card type" aria-expanded={open} aria-controls={id} className="card-type-trigger" onClick={() => setOpen(!open)}>
      {selected && <TypeFrame type={selected} rating={rating} assets={assets} />}
      <span>{selected ? label(selected) : 'Choose a type'}<small className="dim">{selected && selected.rareflag <= 1 ? `${band(rating)} · based on rating` : 'Special card'}</small></span><span aria-hidden="true">▾</span>
    </button>
    {open && <div className="card-type-menu" id={id}>
      <label>Find a card type<input type="search" value={search} onChange={(event) => setSearch(event.target.value)} /></label>
      <fieldset className="card-type-options" role="radiogroup" aria-label="Card types" onKeyDown={(event) => {
        if (!['ArrowDown', 'ArrowRight', 'ArrowUp', 'ArrowLeft'].includes(event.key)) return;
        const buttons = [...event.currentTarget.querySelectorAll<HTMLButtonElement>('button[role="radio"]')];
        const index = buttons.indexOf(event.target as HTMLButtonElement);
        if (index < 0 || !buttons.length) return;
        event.preventDefault();
        buttons[(index + (['ArrowDown', 'ArrowRight'].includes(event.key) ? 1 : -1) + buttons.length) % buttons.length]?.click();
      }}><legend>Card types</legend>
        {choices.filter((type) => label(type).toLowerCase().includes(search.toLowerCase())).map((type) => <button type="button" role="radio" aria-checked={selected?.id === type.id}
          aria-label={label(type)} key={type.id} className={`card-type-option${selected?.id === type.id ? ' selected' : ''}`}
          onClick={() => { onChange(type.id); close(); }}>
          <span className="card-type-check" aria-hidden="true">{selected?.id === type.id ? '●' : '○'}</span>
          <TypeFrame type={type} rating={rating} assets={assets} /><span>{label(type)}</span>
        </button>)}
      </fieldset>
    </div>}
  </div>;
}
function TypeFrame({ type, rating, assets }: { type: CardType; rating: number; assets?: AssetsReply | null | undefined }) {
  const frame = frameFor(type.rareflag, type.rareflag === 3 ? Math.max(65, rating) : rating, assets?.frames ?? []);
  return frame ? <img className="card-type-frame" src={`${assets?.baseUrl ?? '/panel/assets/'}cards/hd/${frame}`} alt="" />
    : <span className={`card-type-frame card-type-placeholder ${band(rating)}`} aria-hidden="true">{type.rareflag <= 1 ? type.rareflag === 0 ? 'C' : 'R' : '★'}</span>;
}
