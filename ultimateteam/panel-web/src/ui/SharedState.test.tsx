import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Tabs } from './Tabs';
import { ToastProvider, useToast } from './Toast';
import { usePanelMotion } from './Motion';

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('shared UI state', () => {
  it('preserves form state when a controlled tab changes', async () => {
    function Example() {
      const [value, setValue] = useState('one');
      const [note, setNote] = useState('');
      return <Tabs label="Review" value={value} onChange={setValue}
        items={[{ value: 'one', label: 'One' }, { value: 'two', label: 'Two' }]}>
        <input aria-label="Note" value={note} onChange={(event) => setNote(event.target.value)} />
      </Tabs>;
    }
    render(<Example />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Note' }), { target: { value: 'unsaved' } });
    fireEvent.mouseDown(screen.getByRole('tab', { name: 'Two' }), { button: 0, ctrlKey: false });
    expect(screen.getByRole('tab', { name: 'Two' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('textbox', { name: 'Note' })).toHaveProperty('value', 'unsaved');
    expect(screen.getAllByRole('tabpanel')).toHaveLength(1);
  });

  it('dismisses a notification and cancels its auto-dismiss timer', async () => {
    const schedule = vi.spyOn(window, 'setTimeout');
    const cancel = vi.spyOn(window, 'clearTimeout');
    function Example() {
      const toast = useToast();
      return <button onClick={() => toast('Change staged', 'staged')}>Notify</button>;
    }
    render(<ToastProvider><Example /></ToastProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Notify' }));
    const call = schedule.mock.calls.findIndex((args) => args[1] === 4000);
    expect(call).toBeGreaterThanOrEqual(0);
    const timer = schedule.mock.results[call]?.value;
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(cancel).toHaveBeenCalledWith(timer);
    await waitFor(() => expect(screen.queryByText('Change staged')).toBeNull());
  });

  it('responds to reduced-motion preference changes without reloading', () => {
    let matches = false;
    let notify: (() => void) | undefined;
    vi.stubGlobal('matchMedia', vi.fn((media: string) => ({
      get matches() { return matches; }, media,
      addEventListener: (_name: string, listener: () => void) => { notify = listener; },
      removeEventListener: vi.fn(),
    })));
    function Probe() {
      const { reduced, transition } = usePanelMotion();
      return <output>{`${reduced}:${transition.duration}`}</output>;
    }
    render(<Probe />);
    expect(screen.getByText('false:0.18')).toBeTruthy();
    act(() => { matches = true; notify?.(); });
    expect(screen.getByText('true:0')).toBeTruthy();
  });
});
