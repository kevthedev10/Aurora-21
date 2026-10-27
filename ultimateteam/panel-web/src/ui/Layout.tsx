import type { ReactNode } from 'react';
import { cx } from './cx';
import { ApiError } from '../api/client';
import { Button } from './Button';
import styles from './Layout.module.css';

export function PageHeader({ title, description, crumbs, actions, className }: { title: ReactNode; description?: ReactNode; crumbs?: ReactNode; actions?: ReactNode; className?: string }) {
  return (
    <header className={cx(styles.head, className)}>
      <div className={styles.titles}>
        {crumbs && <p className={styles.crumbs}>{crumbs}</p>}
        <h1 className={styles.title}>{title}</h1>
        {description && <p className={styles.description}>{description}</p>}
      </div>
      {actions && <div className={styles.actions}>{actions}</div>}
    </header>
  );
}

export function Section({ title, text, actions, children, className }: { title?: ReactNode; text?: ReactNode; actions?: ReactNode; children?: ReactNode; className?: string }) {
  return (
    <section className={cx(styles.section, className)}>
      {(title || actions) && (
        <div className={styles.sectionHead}>
          {title && <h2 className={styles.sectionTitle}>{title}</h2>}
          {actions}
        </div>
      )}
      {text && <p className={styles.sectionText}>{text}</p>}
      {children}
    </section>
  );
}

export const Toolbar = ({ children }: { children: ReactNode }) => <div className={styles.toolbar}>{children}</div>;

export const Stats = ({ children }: { children: ReactNode }) => <div className={styles.stats}>{children}</div>;

export function Stat({ label, value, hint }: { label: ReactNode; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className={styles.stat}>
      <div className={styles.statLabel}>{label}</div>
      <div className={styles.statValue}>{value}</div>
      {hint && <div className={styles.statHint}>{hint}</div>}
    </div>
  );
}

export function EmptyState({ title, text, action }: { title: ReactNode; text?: ReactNode; action?: ReactNode }) {
  return (
    <div className={styles.empty}>
      <h3 className={styles.emptyTitle}>{title}</h3>
      {text && <p className={styles.emptyText}>{text}</p>}
      {action}
    </div>
  );
}

/** A failed load: what the server said, and a retry. */
export function ErrorPanel({ error, onRetry, what = 'This' }: { error: unknown; onRetry?: () => void; what?: string }) {
  const message = error instanceof Error ? error.message : String(error);
  const status = error instanceof ApiError ? ` (HTTP ${error.status})` : '';
  return (
    <div className={cx(styles.empty, styles.problem)} role="alert">
      <h3 className={styles.emptyTitle}>{what} could not be loaded</h3>
      <p className={styles.emptyText}>{message}{status}</p>
      {onRetry && <Button size="sm" onClick={onRetry}>Try again</Button>}
    </div>
  );
}

export const Loading = ({ lines = 4, label = 'Loading' }: { lines?: number; label?: string }) => (
  <div className={styles.skeleton} role="status" aria-label={label}>
    {Array.from({ length: lines }, (_, index) => <span key={index} />)}
  </div>
);
