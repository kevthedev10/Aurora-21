import { afterEach, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { setContentDestination } from './client';
import { usePackActions } from './packs';

afterEach(() => { setContentDestination(null); vi.unstubAllGlobals(); });
it('routes the actual pack editor, lineups, hide and previews to the selected drop', async () => {
  const fetch = vi.fn(async (_url: string, _init?: RequestInit) => new Response('{}', { status: 200 }));
  vi.stubGlobal('fetch', fetch);
  setContentDestination(42);
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  const { result } = renderHook(usePackActions, { wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider> });
  await act(async () => {
    await result.current.stage.mutateAsync({ id: null, revision: 'r1', pack: { name: 'Promo pack' } });
    await result.current.stage.mutateAsync({ id: 3, revision: 'r1', pack: { name: 'Renamed' } });
    await result.current.lineup.mutateAsync({ revision: 'r1', listing: [{ packId: 3, enabled: true, untradeable: null }] });
    await result.current.tokenLineup.mutateAsync({ revision: 'r1', listing: [{ packId: 3, enabled: true, untradeable: null }] });
    await result.current.hide.mutateAsync({ id: 3, revision: 'r1' });
    await result.current.test.mutateAsync({ packId: 3, pack: {} });
    await result.current.simulate.mutateAsync({ packId: 3, rolls: 1 });
  });
  expect(fetch.mock.calls.map(call => call[0])).toEqual([
    '/panel/api/packs/catalog?dropId=42', '/panel/api/packs/catalog/3?dropId=42',
    '/panel/api/packs/lineup?dropId=42', '/panel/api/packs/tokens-lineup?dropId=42',
    '/panel/api/packs/catalog/3?dropId=42', '/panel/api/packs/catalog/test?dropId=42', '/panel/api/packs/simulate?dropId=42',
  ]);
  client.clear();
});
