import type { AnchorHTMLAttributes, ButtonHTMLAttributes } from 'react';
import { cx } from './cx';
import styles from './Button.module.css';

/** primary: the page's main action. staged: an edit that waits for review (content). danger: removes or blocks. */
export type ButtonVariant = 'default' | 'primary' | 'staged' | 'danger' | 'ghost';

interface Look {
  variant?: ButtonVariant;
  size?: 'md' | 'sm';
}

const look = ({ variant = 'default', size = 'md' }: Look, extra?: string) =>
  cx(styles.button, variant !== 'default' && styles[variant], size === 'sm' && styles.sm, extra);

export function Button({ variant, size, className, type = 'button', ...rest }: Look & ButtonHTMLAttributes<HTMLButtonElement>) {
  return <button type={type} className={look({ ...(variant && { variant }), ...(size && { size }) }, className)} {...rest} />;
}

export function LinkButton({ variant, size, className, ...rest }: Look & AnchorHTMLAttributes<HTMLAnchorElement>) {
  return <a className={look({ ...(variant && { variant }), ...(size && { size }) }, className)} {...rest} />;
}
