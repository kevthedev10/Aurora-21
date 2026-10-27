const numbers = new Intl.NumberFormat('en-GB');

/** 1,250,000; "–" for a value the API did not report. */
export const num = (value: number | null | undefined) => (typeof value === 'number' && Number.isFinite(value) ? numbers.format(value) : '–');

/** "3 min ago", from an ISO time (or SQLite's zone-less UTC); "–" when missing or unreadable. */
export function ago(value: string | null | undefined, now = Date.now()): string {
  if (!value) return '–';
  const time = Date.parse(/[zZ]|[+-]\d\d:?\d\d$/.test(value) ? value : `${value.replace(' ', 'T')}Z`);
  if (Number.isNaN(time)) return '–';
  const seconds = Math.round((now - time) / 1000);
  if (seconds < 45) return 'just now';
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 36) return `${hours} h ago`;
  return `${Math.round(hours / 24)} days ago`;
}
