import { afterEach, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SchedulePage } from './SchedulePage';
import type { ScheduleReply } from '../../api/schedule';
afterEach(() => { vi.unstubAllGlobals(); window.location.hash = ''; });
it('hides deletion for live campaigns and confirms deletion of only the selected upcoming campaign', async () => {
  const data: ScheduleReply = { now: '2026-10-07T10:00:00Z', campaigns: [
    { id: 1, name: 'Active', startsAt: '2026-10-01T18:00:00Z', endsAt: '2026-10-10T18:00:00Z', version: 1 },
    { id: 2, name: 'Upcoming', startsAt: '2026-10-11T18:00:00Z', endsAt: '2026-10-18T18:00:00Z', version: 4 },
  ], drops: [], legacy: [] };
  const writes: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method === 'DELETE') {
      writes.push(url); expect(JSON.parse(String(init.body))).toEqual({ version: 4 });
      data.campaigns = data.campaigns.filter(campaign => campaign.id !== 2);
    }
    return new Response(JSON.stringify(data), { status: 200 });
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><SchedulePage /></QueryClientProvider>);
  await screen.findByRole('heading', { name: 'Active', level: 3 });
  expect(screen.queryByRole('button', { name: 'Delete campaign' })).toBeNull();
  fireEvent.change(screen.getByLabelText('Campaign to view'), { target: { value: '2' } });
  fireEvent.click(screen.getByRole('button', { name: 'Delete campaign' }));
  expect(writes).toEqual([]);
  const confirmation = within(screen.getByRole('dialog', { name: 'Delete Upcoming?' }));
  fireEvent.click(confirmation.getByRole('button', { name: 'Delete campaign' }));
  await waitFor(() => expect(writes).toEqual(['/panel/api/content/campaigns/2']));
  await screen.findByRole('heading', { name: 'Active', level: 3 });
  expect(screen.queryByRole('dialog')).toBeNull();
  expect(screen.queryByRole('option', { name: 'Upcoming' })).toBeNull();
  client.clear();
});
