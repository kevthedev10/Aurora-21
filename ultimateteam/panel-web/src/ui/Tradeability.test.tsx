import { expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { Tradeability } from './Tradeability';
it('allows selecting untradeable on ordinary gifts', () => {
  const changed = vi.fn(); render(<Tradeability value={false} onChange={changed} />);
  fireEvent.click(screen.getByLabelText('Untradeable')); expect(changed).toHaveBeenCalledWith(true);
});
it('locks Developer Cards as untradeable even with a false requested value', () => {
  const changed = vi.fn(); render(<Tradeability value={false} onChange={changed} forced />);
  const checkbox = screen.getByLabelText('Untradeable') as HTMLInputElement;
  expect(checkbox.checked).toBe(true); expect(checkbox.disabled).toBe(true);
  expect(screen.getByText('Developer Cards are always untradeable.')).toBeTruthy();
  fireEvent.click(checkbox); expect(changed).not.toHaveBeenCalled();
});
