import { AnimatePresence, motion, useIsPresent } from 'motion/react';
import type { ReactNode } from 'react';
import { usePanelMotion } from './Motion';

/** Expand feedback and history without animating each row in a long list. */
export function Reveal({ show, children, id }: { show: boolean; children: ReactNode; id?: string }) {
  return <AnimatePresence initial={false}>
    {show && <RevealBody {...(id && { id })}>{children}</RevealBody>}
  </AnimatePresence>;
}

function RevealBody({ children, id }: { children: ReactNode; id?: string }) {
  const { transition } = usePanelMotion();
  const present = useIsPresent();
  return <motion.div {...(id && { id })} inert={!present} aria-hidden={!present || undefined} initial={{ height: 0, opacity: 0 }}
      animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }}
      transition={transition} style={{ overflow: 'hidden' }}>{children}</motion.div>;
}
