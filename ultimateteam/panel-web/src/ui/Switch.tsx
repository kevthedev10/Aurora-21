import { useId, type ReactNode } from 'react';
import { cx } from './cx';
import styles from './Switch.module.css';

/** An on/off setting with its explanation; danger turns red when on (a block). */
export function SwitchRow({ label, text, checked, onChange, danger, disabled }: {
  label: string;
  text?: ReactNode;
  checked: boolean;
  onChange: (checked: boolean) => void;
  danger?: boolean;
  disabled?: boolean;
}) {
  const id = useId();
  return (
    <div className={styles.row}>
      <label className={styles.text} htmlFor={id}><b>{label}</b>{text && <span>{text}</span>}</label>
      <span className={cx(styles.switch, danger && styles.danger)}>
        <input id={id} type="checkbox" role="switch" checked={checked} disabled={disabled} onChange={(event) => onChange(event.target.checked)} />
        <span className={styles.track} aria-hidden="true" />
      </span>
    </div>
  );
}
