import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { usePanelMotion } from './Motion';
import { cx } from './cx';
import styles from './Toast.module.css';

/** done: an immediate change was applied. staged: waiting under Changes. error: refused or failed. */
export type ToastTone = 'done' | 'staged' | 'error';

interface Item { id: number; tone: ToastTone; message: ReactNode }

const Context = createContext<(message: ReactNode, tone?: ToastTone) => void>(() => undefined);

export const useToast = () => useContext(Context);

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<Item[]>([]);
  const next = useRef(1);
  const timers = useRef(new Map<number, number>());
  const { reduced, transition } = usePanelMotion();
  useEffect(() => () => {
    for (const timer of timers.current.values()) window.clearTimeout(timer);
    timers.current.clear();
  }, []);
  const dismiss = useCallback((id: number) => {
    window.clearTimeout(timers.current.get(id));
    timers.current.delete(id);
    setItems((current) => current.filter((item) => item.id !== id));
  }, []);
  const show = useCallback((message: ReactNode, tone: ToastTone = 'done') => {
    const id = next.current++;
    setItems((current) => [...current.slice(-3), { id, tone, message }]);
    timers.current.set(id, window.setTimeout(() => dismiss(id), tone === 'error' ? 8000 : 4000));
  }, [dismiss]);
  const value = useMemo(() => show, [show]);
  return (
    <Context.Provider value={value}>
      {children}
      <div className={styles.region} role="status" aria-live="polite">
        <AnimatePresence initial={false}>
        {items.map((item) => (
          <motion.div key={item.id} layout={!reduced} initial={{ opacity: 0, y: reduced ? 0 : 12 }}
            animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: reduced ? 0 : 8 }} transition={transition}
            className={cx(styles.toast, item.tone !== 'done' && styles[item.tone])}>
            <div className={styles.message}>{item.message}</div>
            <button type="button" className={styles.dismiss} aria-label="Dismiss" onClick={() => dismiss(item.id)}>×</button>
          </motion.div>
        ))}
        </AnimatePresence>
      </div>
    </Context.Provider>
  );
}
