import * as Radix from '@radix-ui/react-dialog';
import type { ReactNode } from 'react';
import { Button } from './Button';
import styles from './Drawer.module.css';
import { ModalSurface } from './ModalSurface';

/** A panel from the right edge for a look at something without leaving the page (focus is trapped and returned). */
export function Drawer({ open, onOpenChange, title, description, footer, children }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  description?: ReactNode;
  footer?: ReactNode;
  children: ReactNode;
}) {
  return (
    <ModalSurface open={open} onOpenChange={onOpenChange} overlayClass={styles.overlay}
      contentClass={styles.panel} drawer described={Boolean(description)}>
          <div className={styles.head}>
            <div>
              <Radix.Title className={styles.title}>{title}</Radix.Title>
              {description && <Radix.Description className={styles.description}>{description}</Radix.Description>}
            </div>
            <Radix.Close asChild><Button variant="ghost" size="sm" aria-label="Close">✕</Button></Radix.Close>
          </div>
          <div className={styles.body}>{children}</div>
          {footer && <div className={styles.foot}>{footer}</div>}
    </ModalSurface>
  );
}
