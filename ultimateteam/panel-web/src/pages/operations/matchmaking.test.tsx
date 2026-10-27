import { afterEach, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MatchmakingPage } from './MatchmakingPage';

const player = (name: string, state: string, gameState?: number) => ({ name, discordId: name, state, mode: 'FUT_Rivals', gameState, gameId: gameState == null ? null : 42 });
const show = (players: ReturnType<typeof player>[]) => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ snapshot: { current: true, available: true, updatedAt: new Date().toISOString() }, players, counts: { games: 1, online: players.length } }), { headers: { 'Content-Type': 'application/json' } })));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><MatchmakingPage /></QueryClientProvider>);
  return client;
};
const section = (name: string) => screen.getByRole('heading', { name }).closest('section')!;
afterEach(() => vi.unstubAllGlobals());

it('keeps connection checks and pregame players separate from playing and completed games', async () => {
  show([player('Waiting user', 'searching'), player('Checking', 'match', 3), player('Setting up', 'match', 130), player('Playing user', 'match', 131), player('Finished', 'match', 4), player('Unreported', 'match'), player('Online user', 'online')]);
  await screen.findByText('Checking');
  expect(within(section('Searching')).getByText('Waiting user')).toBeTruthy();
  expect(within(section('Pre-match')).getByText('Checking')).toBeTruthy();
  expect(within(section('Pre-match')).getByText('Setting up')).toBeTruthy();
  expect(within(section('In-match')).queryByText('Checking')).toBeNull();
  expect(within(section('In-match')).getByText('Playing user')).toBeTruthy();
  expect(within(section('In-match')).getByText('Division Rivals')).toBeTruthy();
  expect(within(section('Post-match')).getByText('Finished')).toBeTruthy();
  expect(within(section('Matches without a reported phase')).getByText('Unreported')).toBeTruthy();
  expect(within(section('Other online players')).getByText('Online user')).toBeTruthy();
});

it('pages searches and resets paging when filtering', async () => {
  show(Array.from({ length: 23 }, (_, index) => player(`Searcher ${index}`, 'searching')));
  await screen.findByText('Searcher 0');
  expect(within(section('Searching')).getAllByRole('row')).toHaveLength(11);
  fireEvent.click(within(section('Searching')).getByRole('button', { name: 'Next' }));
  expect(screen.queryByText('Searcher 0')).toBeNull();
  fireEvent.change(screen.getByLabelText('Find a player'), { target: { value: 'Searcher 0' } });
  expect(screen.getByText('Searcher 0')).toBeTruthy();
  expect(within(section('Searching')).getAllByRole('row')).toHaveLength(2);
  fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
  expect(within(section('Searching')).getAllByRole('row')).toHaveLength(11);
});

it('retains the last snapshot after a failed refresh and allows pausing polling', async () => {
  const client = show([player('Keep visible', 'searching')]);
  await screen.findByText('Keep visible');
  fireEvent.click(screen.getByRole('button', { name: 'Pause auto-refresh' }));
  expect(screen.getByRole('button', { name: 'Resume auto-refresh' })).toBeTruthy();
  vi.stubGlobal('fetch', vi.fn(async () => new Response('{"error":"Offline"}', { status: 500 })));
  await act(async () => { await client.invalidateQueries({ queryKey: ['matchmaking'] }); });
  await screen.findByText('Refresh failed');
  expect(screen.getByText('Keep visible')).toBeTruthy();
  expect(screen.getByText(/displayed snapshot is not current/)).toBeTruthy();
});
