import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Dialog } from './Dialog';
import { Drawer } from './Drawer';

function Example({ drawer = false }: { drawer?: boolean }) {
  const [open, setOpen] = useState(false);
  const Surface = drawer ? Drawer : Dialog;
  return <>
    <button onClick={() => setOpen(true)}>Open</button>
    <Surface open={open} onOpenChange={setOpen} title="Review">
      <label>Note<input /></label>
    </Surface>
  </>;
}

describe('animated modal surfaces', () => {
  for (const drawer of [false, true]) {
    it(`focuses ${drawer ? 'drawer' : 'dialog'} content and restores the opener after Escape`, async () => {
      render(<Example drawer={drawer} />);
      const opener = screen.getByRole('button', { name: 'Open' });
      opener.focus();
      fireEvent.click(opener);
      const note = await screen.findByRole('textbox', { name: 'Note' });
      // Drawers place their close button before the body; dialogs place it after the body.
      const firstControl = drawer ? screen.getByRole('button', { name: 'Close' }) : note;
      await waitFor(() => expect(document.activeElement).toBe(firstControl));
      note.focus();
      opener.focus();
      await waitFor(() => expect(document.activeElement).toBe(note));
      fireEvent.keyDown(note, { key: 'Escape' });
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(opener));
      fireEvent.click(opener);
      fireEvent.click(await screen.findByRole('button', { name: 'Close' }));
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(opener));
    });
  }

  it('preserves an in-progress form when a close animation is interrupted by reopening', async () => {
    const change = () => undefined;
    const { rerender } = render(<Dialog open onOpenChange={change} title="Review"><input aria-label="Note" /></Dialog>);
    const input = await screen.findByRole('textbox', { name: 'Note' });
    fireEvent.change(input, { target: { value: 'keep this' } });
    rerender(<Dialog open={false} onOpenChange={change} title="Review"><input aria-label="Note" /></Dialog>);
    rerender(<Dialog open onOpenChange={change} title="Review"><input aria-label="Note" /></Dialog>);
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Note' })).toHaveProperty('value', 'keep this'));
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
  });
});
