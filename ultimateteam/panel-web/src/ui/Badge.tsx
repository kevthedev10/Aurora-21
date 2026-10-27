import type { ReactNode } from 'react';
import { cx } from './cx';
import styles from './Badge.module.css';

export type Tone = 'neutral' | 'pitch' | 'staged' | 'flare' | 'red' | 'coin';

export function Badge({ tone = 'neutral', href, title, children }: { tone?: Tone; href?: string; title?: string; children: ReactNode }) {
  const className = cx(styles.badge, tone !== 'neutral' && styles[tone]);
  return href ? <a className={className} href={href} title={title}>{children}</a> : <span className={className} title={title}>{children}</span>;
}

/** Marks anything waiting in the content changeset: it does not reach the game until Publish. */
export const StagedBadge = ({ children = 'Staged' }: { children?: ReactNode }) => (
  <Badge tone="staged" title="Waiting under Changes until it is published">{children}</Badge>
);

/** A service or player state: up (green), down (red) or not reported (grey). */
export function StatusDot({ state, children }: { state: boolean | null | undefined; children: ReactNode }) {
  const name = state === true ? 'up' : state === false ? 'down' : 'not reported';
  return (
    <span className={cx(styles.dot, state === true && styles.up, state === false && styles.down)} title={name}>
      {children}
    </span>
  );
}
