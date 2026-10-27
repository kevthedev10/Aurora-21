import * as Radix from '@radix-ui/react-tabs';
import { useId, type ReactNode } from 'react';
import { LayoutGroup, motion } from 'motion/react';
import { usePanelMotion } from './Motion';
import styles from './Tabs.module.css';

export interface TabItem<T extends string> {
  value: T;
  label: ReactNode;
  count?: number | undefined;
}

/** Tabs whose value lives in the URL or the page's state; only the active panel renders. */
export function Tabs<T extends string>({ label, items, value, onChange, children }: {
  label: string;
  items: TabItem<T>[];
  value: T;
  onChange: (value: T) => void;
  children: ReactNode;
}) {
  const id = useId();
  const { reduced, transition } = usePanelMotion();
  return (
    <Radix.Root value={value} onValueChange={(next) => onChange(next as T)} activationMode="manual">
      <LayoutGroup id={id}>
      <Radix.List className={styles.list} aria-label={label}>
        {items.map((item) => (
          <Radix.Trigger key={item.value} value={item.value} className={styles.tab}>
            {item.label}
            {item.count !== undefined && <span className={styles.count}>{item.count.toLocaleString('en-GB')}</span>}
            {item.value === value && <motion.span className={styles.indicator} aria-hidden="true"
              {...(!reduced && { layoutId: 'active-tab' })} transition={transition} />}
          </Radix.Trigger>
        ))}
      </Radix.List>
      </LayoutGroup>
      <Radix.Content value={value} className={styles.panel} asChild>
        <motion.div initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={transition}>
          {children}
        </motion.div>
      </Radix.Content>
    </Radix.Root>
  );
}
