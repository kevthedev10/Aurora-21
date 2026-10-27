import * as Radix from '@radix-ui/react-dialog';
import { AnimatePresence, motion } from 'motion/react';
import { useRef, type ReactNode } from 'react';
import { usePanelMotion } from './Motion';

/** Shared presence and focus handling for dialogs without a Radix.Trigger (opened by page actions). */
export function ModalSurface({ open, onOpenChange, overlayClass, contentClass, centered = false,
  drawer = false, described = false, children }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  overlayClass: string | undefined;
  contentClass: string | undefined;
  centered?: boolean;
  drawer?: boolean;
  described?: boolean;
  children: ReactNode;
}) {
  const { reduced, transition } = usePanelMotion();
  const opener = useRef<HTMLElement | null>(null);
  const currentOpen = useRef(open);
  currentOpen.current = open;
  const hidden = reduced ? { opacity: 0 } : drawer ? { opacity: 0, x: 32 } : { opacity: 0, y: 12 };
  return (
    <Radix.Root open={open} onOpenChange={onOpenChange}>
      <Radix.Portal forceMount>
        <AnimatePresence>
          {open && <Radix.Overlay key="overlay" forceMount asChild>
            <motion.div className={overlayClass} initial={{ opacity: 0 }} animate={{ opacity: 1 }}
              exit={{ opacity: 0 }} transition={transition} />
          </Radix.Overlay>}
          {open && <Radix.Content key="content" forceMount asChild
            {...(!described && { 'aria-describedby': undefined })}
            onOpenAutoFocus={() => {
              if (document.activeElement instanceof HTMLElement) opener.current = document.activeElement;
            }}
            onCloseAutoFocus={(event) => {
              event.preventDefault();
              // An interrupted exit must not steal focus from a reopened surface.
              const otherDialogFocused = document.activeElement instanceof HTMLElement
                && document.activeElement.closest('[role="dialog"]') !== null;
              if (!currentOpen.current && !otherDialogFocused) {
                if (opener.current?.isConnected) opener.current.focus();
                else document.getElementById('panel-main')?.focus();
              }
            }}>
            <motion.div className={contentClass} {...(centered && { style: { x: '-50%' } })}
              initial={hidden} animate={{ opacity: 1, ...(drawer ? { x: 0 } : { y: 0 }) }}
              exit={hidden} transition={transition}>
              {children}
            </motion.div>
          </Radix.Content>}
        </AnimatePresence>
      </Radix.Portal>
    </Radix.Root>
  );
}
