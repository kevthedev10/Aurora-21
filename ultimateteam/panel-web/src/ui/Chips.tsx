import type { ReactNode } from 'react';
import styles from './Chips.module.css';

export interface ChipOption<T extends string> {
  value: T;
  label: ReactNode;
  count?: number | undefined;
}

/** A single choice from a few options (a pile, a view): pressed buttons in a labelled group. */
export function Chips<T extends string>({ label, options, value, onChange }: {
  label: string;
  options: ChipOption<T>[];
  value: T | null;
  onChange: (value: T) => void;
}) {
  return (
    <div className={styles.chips} role="group" aria-label={label}>
      {options.map((option) => (
        <button key={option.value} type="button" className={styles.chip} aria-pressed={option.value === value} onClick={() => onChange(option.value)}>
          {option.label}
          {option.count !== undefined && <span className={styles.count}>{option.count.toLocaleString('en-GB')}</span>}
        </button>
      ))}
    </div>
  );
}
