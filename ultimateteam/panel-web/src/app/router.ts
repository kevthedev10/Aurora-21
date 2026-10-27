// A hash router: #/<page>/<more>?<query>. The server serves one shell at /panel/, so pages need no routes.
import { useMemo, useSyncExternalStore } from 'react';

export interface Route {
  /** The path parts after "#/", for example ["players", "123"]. Empty for the home page. */
  parts: string[];
  params: URLSearchParams;
}

export function parseHash(hash: string): Route {
  const [path = '', query = ''] = hash.replace(/^#\/?/, '').split('?', 2);
  return { parts: path.split('/').filter(Boolean).map(decodeURIComponent), params: new URLSearchParams(query) };
}

const subscribe = (notify: () => void) => {
  window.addEventListener('hashchange', notify);
  return () => window.removeEventListener('hashchange', notify);
};

export function useRoute(): Route {
  const hash = useSyncExternalStore(subscribe, () => window.location.hash);
  return useMemo(() => parseHash(hash), [hash]);
}

export const href = (...parts: (string | number)[]) => `#/${parts.map((part) => encodeURIComponent(String(part))).join('/')}`;
