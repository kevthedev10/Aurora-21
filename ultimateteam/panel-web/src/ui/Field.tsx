import { useId, cloneElement, isValidElement, type ReactElement, type ReactNode } from 'react';
import { cx } from './cx';
import styles from './Field.module.css';

interface FieldProps {
  label: ReactNode;
  hint?: ReactNode;
  error?: string | null;
  inline?: boolean;
  /** One input, select or textarea; it gets the label's id and the hint/error description. */
  children: ReactElement<{ id?: string; 'aria-describedby'?: string; 'aria-invalid'?: boolean }>;
}

export function Field({ label, hint, error, inline, children }: FieldProps) {
  const id = useId();
  const noteId = `${id}-note`;
  const control = isValidElement(children)
    ? cloneElement(children, { id, ...((hint || error) && { 'aria-describedby': noteId }), ...(error && { 'aria-invalid': true }) })
    : children;
  return (
    <div className={cx(styles.field, error && styles.invalid, inline && styles.inline)}>
      <label className={styles.label} htmlFor={id}>{label}</label>
      {control}
      {error ? <span id={noteId} className={styles.error}>{error}</span> : hint ? <span id={noteId} className={styles.hint}>{hint}</span> : null}
    </div>
  );
}
