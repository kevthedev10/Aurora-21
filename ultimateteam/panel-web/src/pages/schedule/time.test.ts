import { describe, expect, it } from 'vitest';
import { calendarDays, contentDateInstant, contentTime, dateWindowProblem, shiftDay, ukInstant, ukLocal } from './time';

describe('UK scheduling clock', () => {
  it('uses 7pm UK time in both summer and winter', () => {
    expect(contentDateInstant('2026-07-01')).toBe('2026-07-01T18:00:00.000Z');
    expect(contentDateInstant('2026-12-01')).toBe('2026-12-01T19:00:00.000Z');
    expect(ukInstant(contentTime('2026-07-01'))).toBe('2026-07-01T18:00:00.000Z');
    expect(ukInstant(contentTime('2026-12-01'))).toBe('2026-12-01T19:00:00.000Z');
    expect(ukLocal('2026-07-01T18:00:00Z')).toBe('2026-07-01T19:00');
  });
  it('requires real dates and a positive bounded date window', () => {
    expect(dateWindowProblem('2026-10-01', '2026-10-28')).toBeNull();
    expect(dateWindowProblem('2026-10-01', '2026-10-01')).toBe('End date must be after start date.');
    expect(dateWindowProblem('2026-10-02', '2026-10-01')).toBe('End date must be after start date.');
    expect(() => contentDateInstant('2026-02-30')).toThrow('valid date');
    expect(() => contentDateInstant('')).toThrow('Choose a date');
  });
  it('rejects missing and ambiguous times at UK clock changes', () => {
    expect(() => ukInstant('2026-03-29T01:30')).toThrow('does not exist');
    expect(() => ukInstant('2026-10-25T01:30')).toThrow('occurs twice');
    expect(() => ukInstant('2026-02-30T19:00')).toThrow('valid date');
  });
  it('starts month and week calendars on Monday without using browser timezone', () => {
    expect(calendarDays('2026-10-07', 'week')).toEqual(['2026-10-05', '2026-10-06', '2026-10-07', '2026-10-08', '2026-10-09', '2026-10-10', '2026-10-11']);
    const month = calendarDays('2026-10-07', 'month');
    expect(month).toHaveLength(42);
    expect(month[0]).toBe('2026-09-28');
    expect(shiftDay('2026-12-31', 1)).toBe('2027-01-01');
  });
});
