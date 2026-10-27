import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { ConfirmDialog } from './Dialog';

describe('ConfirmDialog', () => {
  it('keeps the dialog open and shows the refusal when the action fails', async () => {
    const onOpenChange = vi.fn();
    render(<ConfirmDialog open onOpenChange={onOpenChange} title="Reset Division Rivals?" confirmLabel="Reset Division Rivals" variant="danger"
      onConfirm={() => Promise.reject(new Error('The club is in a match.'))} />);
    fireEvent.click(screen.getByRole('button', { name: 'Reset Division Rivals' }));
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'The club is in a match.');
    expect(onOpenChange).not.toHaveBeenCalledWith(false);
  });

  it('closes after the action succeeds', async () => {
    const onOpenChange = vi.fn();
    const onConfirm = vi.fn().mockResolvedValue(undefined);
    render(<ConfirmDialog open onOpenChange={onOpenChange} title="Block Northbank?" confirmLabel="Block player" variant="danger" onConfirm={onConfirm} />);
    fireEvent.click(screen.getByRole('button', { name: 'Block player' }));
    await waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false));
    expect(onConfirm).toHaveBeenCalledOnce();
  });
});
