import { describe, expect, it } from 'vitest';
import { ago, num } from './format';

describe('format', () => {
  it('formats numbers and marks missing ones', () => {
    expect(num(1250000)).toBe('1,250,000');
    expect(num(null)).toBe('–');
    expect(num(Number.NaN)).toBe('–');
  });

  it('reads ISO and SQLite UTC times', () => {
    const now = Date.parse('2026-09-29T12:00:00Z');
    expect(ago('2026-09-29T11:57:00Z', now)).toBe('3 min ago');
    expect(ago('2026-09-29 09:00:00', now)).toBe('3 h ago');
    expect(ago('2026-09-25T12:00:00Z', now)).toBe('4 days ago');
    expect(ago('nonsense', now)).toBe('–');
  });
});
