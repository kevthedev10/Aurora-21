export const scheduleZone = 'Europe/London';
const formatter = new Intl.DateTimeFormat('en-GB', { timeZone: scheduleZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
export function ukLocal(value: string): string {
  const fields = Object.fromEntries(formatter.formatToParts(new Date(value)).map(part => [part.type, part.value]));
  return `${fields.year}-${fields.month}-${fields.day}T${fields.hour}:${fields.minute}`;
}
/** Reject both missing and repeated clock-change times rather than guessing an offset. */
export function ukInstant(value: string): string {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value)) throw new Error('Enter a date and time.');
  const wall = Date.parse(`${value}Z`);
  if (!Number.isFinite(wall) || new Date(wall).toISOString().slice(0, 16) !== value) throw new Error('Enter a valid date and time.');
  const candidates = [wall, wall - 3_600_000].filter(instant => ukLocal(new Date(instant).toISOString()) === value);
  if (candidates.length === 0) throw new Error('This UK time does not exist because the clocks change.');
  if (candidates.length > 1) throw new Error('This UK time occurs twice because the clocks change. Choose another time.');
  return new Date(candidates[0]!).toISOString();
}
export const ukDay = (value: string) => ukLocal(value).slice(0, 10);
export const ukDateLabel = (value: string) => ukDay(value).split('-').reverse().join('-');
export const contentTime = (day: string) => `${day}T19:00`;
export function contentDateInstant(day: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(day)) throw new Error('Choose a date.');
  return ukInstant(contentTime(day));
}
export function dateWindowProblem(start: string, end: string): string | null {
  try { contentDateInstant(start); contentDateInstant(end); }
  catch (error) { return error instanceof Error ? error.message : String(error); }
  return end <= start ? 'End date must be after start date.' : null;
}
export function shiftDay(day: string, amount: number): string {
  const date = new Date(`${day}T12:00:00Z`); date.setUTCDate(date.getUTCDate() + amount); return date.toISOString().slice(0, 10);
}
export function calendarDays(day: string, view: 'month' | 'week'): string[] {
  const date = new Date(`${view === 'month' ? `${day.slice(0, 7)}-01` : day}T12:00:00Z`);
  const offset = (date.getUTCDay() + 6) % 7;
  const start = shiftDay(date.toISOString().slice(0, 10), -offset);
  return Array.from({ length: view === 'month' ? 42 : 7 }, (_, index) => shiftDay(start, index));
}
export const ukDisplay = (value: string) => new Intl.DateTimeFormat('en-GB', { timeZone: scheduleZone, day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', timeZoneName: 'short', hourCycle: 'h23' }).format(new Date(value));
