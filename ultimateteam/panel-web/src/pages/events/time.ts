import { scheduleZone, ukLocal, ukInstant } from '../schedule/time';
export const eventZone = scheduleZone;
export const localDate = ukLocal;
export const utcDate = ukInstant;
export function timingStatus(row: { enabled: boolean; starts_at: string; ends_at: string }, now: string) {
  return !row.enabled ? 'Disabled' : Date.parse(now) < Date.parse(row.starts_at) ? 'Upcoming'
    : Date.parse(now) >= Date.parse(row.ends_at) ? 'Ended' : 'Active';
}
export const displayDate = (value: string) => new Intl.DateTimeFormat('en-GB', { timeZone: eventZone, day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(new Date(value));
