// The panel API client: same origin, JSON both ways. The server's access checks (OperatorPanel.Refusal) need
// Content-Type: application/json on every write, even one without a body.

/** A refused or failed call: the server's `{error}` text, with the status and body for callers that need more
 *  (for example `428 {code:'needsConfirm'}` before a club edit on a signed-in player). */
export class ApiError extends Error {
  readonly status: number;
  readonly body: unknown;

  constructor(message: string, status: number, body: unknown) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
  }
}

/** Fires when any call answers 401, so the app can show sign-in again. */
export const sessionEvents = new EventTarget();
export const destinationEvents = new EventTarget();
const destinationKey = 'aurora21.panel.content.destination';
let destination: number | null = null;
// Normal edits save live; an old session's drop selection must not capture new edits.
try { sessionStorage.removeItem(destinationKey); } catch { /* Storage may be disabled. */ }
export const getContentDestination = () => destination;
export function setContentDestination(value: number | null) {
  if (value === destination) return;
  destination = value;
  try { if (value === null) sessionStorage.removeItem(destinationKey); else sessionStorage.setItem(destinationKey, String(value)); } catch { /* Keep the in-memory selection. */ }
  destinationEvents.dispatchEvent(new Event('change'));
}
export const subscribeContentDestination = (listener: () => void) => {
  destinationEvents.addEventListener('change', listener);
  return () => destinationEvents.removeEventListener('change', listener);
};
export function destinationPath(path: string): string {
  if (destination === null || !/^(content\/changeset(?:[/?]|$)|content\/history\/\d+\/revert(?:[?]|$)|promos(?:[/?]|$)|packs\/(?:catalog|lineup|tokens-lineup|simulate|odds)(?:[/?]|$))/.test(path)) return path;
  const query = new URLSearchParams(path.split('?')[1]);
  if (query.has('dropId')) return path;
  return `${path}${path.includes('?') ? '&' : '?'}dropId=${destination}`;
}

export interface ApiOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  signal?: AbortSignal;
  /** Legacy Changes recovery must retain a draft until reviewed. */
  staged?: boolean;
}

const errorText = (body: unknown): string | null =>
  typeof body === 'object' && body !== null && 'error' in body && typeof body.error === 'string' && body.error ? body.error : null;

export async function api<T>(path: string, options: ApiOptions = {}): Promise<T> {
  const method = options.method ?? 'GET';
  let scopedPath = destinationPath(path);
  const query = new URLSearchParams(scopedPath.split('?')[1]);
  const normalSave = method === 'PUT' && /^content\/changeset\/changes(?:[?]|$)/.test(scopedPath)
    || ['POST', 'PUT', 'DELETE'].includes(method) && /^packs\/catalog(?:\/\d+(?:\/delete)?)?(?:[?]|$)/.test(scopedPath)
    || method === 'PUT' && /^packs\/(?:lineup|tokens-lineup)(?:[?]|$)/.test(scopedPath);
  if (normalSave && !options.staged && !query.get('dropId')) scopedPath += `${scopedPath.includes('?') ? '&' : '?'}apply=1`;
  if (method !== 'GET' && /^packs\/catalog\/\d+\/delete(?:[?]|$)/.test(scopedPath) && new URLSearchParams(scopedPath.split('?')[1]).has('dropId')) {
    throw new ApiError('Pack deletion cannot be scheduled. Select Immediate changes to review a deletion.', 422, { error: 'Pack deletion cannot be scheduled.' });
  }
  const init: RequestInit = { method, credentials: 'same-origin' };
  if (method !== 'GET') init.headers = { 'Content-Type': 'application/json' };
  if (options.body !== undefined) init.body = JSON.stringify(options.body);
  if (options.signal) init.signal = options.signal;
  const response = await fetch(`/panel/api/${scopedPath}`, init);
  let body: unknown = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }
  if (response.status === 401) {
    sessionEvents.dispatchEvent(new Event('signedout'));
    throw new ApiError('Signed out', 401, body);
  }
  if (!response.ok) throw new ApiError(errorText(body) ?? `${response.status} ${response.statusText}`.trim(), response.status, body);
  return body as T;
}
