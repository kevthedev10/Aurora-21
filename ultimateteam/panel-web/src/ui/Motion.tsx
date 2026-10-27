import { MotionConfig, type Transition } from 'motion/react';
import type { ReactNode } from 'react';
import { useMediaQuery } from './useMediaQuery';

export const panelTransition: Transition = { duration: 0.18, ease: [0.22, 1, 0.36, 1] };

export function PanelMotion({ children }: { children: ReactNode }) {
  return <MotionConfig reducedMotion="user" transition={panelTransition}>{children}</MotionConfig>;
}

/** Apply the preference immediately, including changes while the panel is open. */
export function usePanelMotion() {
  const reduced = useMediaQuery('(prefers-reduced-motion: reduce)');
  return { reduced, transition: reduced ? { duration: 0 } : panelTransition };
}
