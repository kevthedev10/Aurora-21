import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import { contentActionKey, type ValidationReply } from './content';
import { contentTime, ukInstant } from '../pages/schedule/time';

export interface Campaign { id: number; name: string; startsAt: string | null; endsAt: string | null; version: number }
export type DropStatus = 'draft' | 'scheduled' | 'live' | 'ended' | 'cancelled' | 'failed';
export interface ContentDrop {
  id: number; campaignId: number | null; name: string; startsAt: string; endsAt: string | null;
  status: DropStatus; version: number; changesetId: number | null; error: string | null; inheritEnd: boolean;
  changes: number; items: { id: number; area: string; key: Record<string, unknown>; operation: string; endsAt: string | null; inheritEnd: boolean }[];
  publishedAt: string | null;
}
export interface LegacyEntry { kind: 'promo_event' | 'icon_rotation' | 'season'; code: string; name: string; startsAt: string; endsAt: string; enabled: boolean; rotationType?: 'totw' | 'promo' }
export interface ScheduleReply { now: string; campaigns: Campaign[]; drops: ContentDrop[]; legacy: LegacyEntry[] }
export interface SchedulePreview { at: string; revision: number; promos: { code: string; name: string; generallyAvailable: boolean; cards: number }[]; storePackIds: number[]; sbcKeys: string[]; objectiveGroupIds: number[]; seasonId: number | null }
export interface DropInput { campaignId: number | null; name: string; startDate: string; endDate: string; inheritEnd: false; version?: number }
export const scheduleKey = ['content-schedule'] as const;
export const useSchedule = () => useQuery({ queryKey: scheduleKey, queryFn: ({ signal }) => api<ScheduleReply>('content/schedule', { signal }), refetchInterval: 15_000 });
export const useSchedulePreview = (at: string | null) => useQuery({ queryKey: ['schedule-preview', at], queryFn: ({ signal }) => api<SchedulePreview>(`content/schedule/preview?at=${encodeURIComponent(at!)}`, { signal }), enabled: at !== null });
export function useScheduleActions() {
  const client = useQueryClient();
  const refresh = () => client.invalidateQueries({ queryKey: scheduleKey });
  return {
    campaign: useMutation({ mutationKey: contentActionKey, mutationFn: ({ id, startDate, endDate, ...body }: { id?: number; name: string; startDate: string; endDate: string; version?: number }) => api<Campaign>(`content/campaigns${id ? `/${id}` : ''}`, { method: id ? 'PUT' : 'POST', body: { ...body, startsAt: ukInstant(contentTime(startDate)), endsAt: ukInstant(contentTime(endDate)) } }), onSuccess: refresh }),
    deleteCampaign: useMutation({ mutationKey: contentActionKey, mutationFn: (campaign: Campaign) => api(`content/campaigns/${campaign.id}`, { method: 'DELETE', body: { version: campaign.version } }), onSuccess: refresh }),
    drop: useMutation({ mutationKey: contentActionKey, mutationFn: ({ id, startDate, endDate, ...body }: DropInput & { id?: number }) => api<ContentDrop>(`content/drops${id ? `/${id}` : ''}`, { method: id ? 'PUT' : 'POST', body: { ...body, startsAt: ukInstant(contentTime(startDate)), endsAt: ukInstant(contentTime(endDate)) } }), onSuccess: refresh }),
    validate: useMutation({ mutationKey: contentActionKey, mutationFn: (drop: ContentDrop) => api<ValidationReply>(`content/drops/${drop.id}/validate`, { method: 'POST', body: { version: drop.version } }) }),
    transition: useMutation({ mutationKey: contentActionKey, mutationFn: ({ drop, action }: { drop: ContentDrop; action: 'schedule' | 'cancel' | 'publish' | 'end' }) => api<ContentDrop>(`content/drops/${drop.id}/${action}`, { method: 'POST', body: { version: drop.version } }), onSuccess: refresh }),
  };
}
