import * as Radix from '@radix-ui/react-dialog';
import { useState, type ReactNode } from 'react';
import { Button, type ButtonVariant } from './Button';
import { cx } from './cx';
import styles from './Dialog.module.css';
import { ModalSurface } from './ModalSurface';

export function Dialog({ open, onOpenChange, title, description, wide, footer, children }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  description?: ReactNode;
  wide?: boolean;
  footer?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <ModalSurface open={open} onOpenChange={onOpenChange} overlayClass={styles.overlay}
      contentClass={cx(styles.content, wide && styles.wide)} centered described={Boolean(description)}>
          <Radix.Title className={styles.title}>{title}</Radix.Title>
          {description && <Radix.Description className={styles.description}>{description}</Radix.Description>}
          {children && <div className={styles.body}>{children}</div>}
          {footer && <div className={styles.footer}>{footer}</div>}
          <Radix.Close asChild>
            <Button variant="ghost" size="sm" className={styles.close} aria-label="Close">✕</Button>
          </Radix.Close>
    </ModalSurface>
  );
}

/**
 * Asks before an action and runs it: the button stays busy while it runs, and a refusal is shown in the dialog
 * rather than closing it. The confirm label names the action ("Block player", "Stage hide"), never "OK".
 */
export function ConfirmDialog({ open, onOpenChange, title, text, confirmLabel, variant = 'primary', onConfirm, children }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  text?: ReactNode;
  confirmLabel: string;
  variant?: ButtonVariant;
  onConfirm: () => Promise<unknown> | unknown;
  children?: ReactNode;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      await onConfirm();
      onOpenChange(false);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog
      open={open}
      onOpenChange={(next) => { if (!busy) { setError(null); onOpenChange(next); } }}
      title={title}
      description={text}
      footer={<>
        <Button variant="ghost" onClick={() => onOpenChange(false)} disabled={busy}>Cancel</Button>
        <Button variant={variant} onClick={run} disabled={busy}>{busy ? 'Working…' : confirmLabel}</Button>
      </>}
    >
      {children}
      {error && <p className={styles.error} role="alert">{error}</p>}
    </Dialog>
  );
}
