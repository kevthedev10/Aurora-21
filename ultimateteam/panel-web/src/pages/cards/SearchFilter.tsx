import { useEffect, useId, useRef, useState } from 'react';

export interface FilterOption { value: string; label: string; image?: string | undefined; leagueId?: number }

/** Searchable native-button options: arrows, Enter and Escape also work without a mouse. */
export function SearchFilter({ label, value, options, onChange, searchable = true, className = '' }: {
  label: string; value: string; options: FilterOption[]; onChange: (option: FilterOption | null) => void; searchable?: boolean; className?: string;
}) {
  const id = useId();
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const [active, setActive] = useState(0);
  const menu = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (open) menu.current?.querySelector<HTMLElement>('input, [aria-selected="true"], [role="option"]')?.focus();
  }, [open]);
  const selected = options.find(option => option.value === value);
  const matches = options.filter(option => option.label.toLocaleLowerCase().includes(search.toLocaleLowerCase())).slice(0, 100);
  const choose = (option: FilterOption | null) => { onChange(option); setSearch(''); setOpen(false); setActive(0); document.getElementById(id)?.focus(); };
  return <div className={`catalogue-filter ${className}`} onBlur={event => {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) { setOpen(false); setSearch(''); }
  }}>
    <label htmlFor={id}>{label}</label>
    <button id={id} className="filter-trigger" type="button" aria-haspopup="listbox" aria-expanded={open} aria-controls={open ? `${id}-options` : undefined}
      onClick={() => setOpen(!open)} onKeyDown={event => {
        if (event.key === 'ArrowDown') { event.preventDefault(); setOpen(true); }
        if (event.key === 'Escape') setOpen(false);
      }}>
      {selected?.image && <img src={selected.image} alt="" />}<span className="filter-label" title={selected?.label}>{selected?.label ?? (value ? `Selected ${value}` : 'Any')}</span><span aria-hidden="true">⌄</span>
    </button>
    {open && <div ref={menu} className="filter-menu" onKeyDown={event => {
      if (event.key === 'Escape') { setOpen(false); document.getElementById(id)?.focus(); }
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        const buttons = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="option"]'));
        const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
        const next = Math.max(0, Math.min(buttons.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1)));
        setActive(next); buttons[next]?.focus();
      }
      if (event.key === 'Enter' && event.target instanceof HTMLInputElement) { event.preventDefault(); choose(matches[Math.max(0, active - 1)] ?? null); }
    }}>
      {searchable && <input autoFocus type="search" aria-label={`Search ${label.toLowerCase()}`} placeholder={`Search ${label.toLowerCase()}…`}
        value={search} onChange={event => { setSearch(event.target.value); setActive(1); }} />}
      <div id={`${id}-options`} role="listbox" aria-label={label}>
        <button type="button" role="option" aria-selected={!value} onClick={() => choose(null)}>Any</button>
        {matches.map(option => <button type="button" role="option" key={option.value} aria-selected={value === option.value}
          onClick={() => choose(option)}>{option.image && <img src={option.image} alt="" loading="lazy" />}<span>{option.label}</span></button>)}
        {!matches.length && <p className="dim">No matches</p>}
      </div>
      {options.length > 100 && matches.length === 100 && <small className="dim">Type to narrow the results.</small>}
    </div>}
  </div>;
}
