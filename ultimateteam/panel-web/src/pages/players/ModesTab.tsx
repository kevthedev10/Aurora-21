// Modes (plan 4.9): this club's Division Rivals, Squad Battles, Aurora Champions and Draft records, with audited resets
// (POST modes/<persona>/<mode>/reset). Settled reward history and spent Weekend League entries survive a reset.
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../api/client';
import { clubKey, MODE_NAMES, type PlayerDetail } from '../../api/players';
import { Button } from '../../ui/Button';
import { DataTable } from '../../ui/DataTable';
import { ConfirmDialog } from '../../ui/Dialog';
import { EmptyState, ErrorPanel, Loading, Section } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';

type Mode = 'rivals' | 'squad-battles' | 'champions' | 'draft';
type Record_ = Record<string, unknown> | null | undefined;

interface ModesReply {
  id?: number;
  name?: string;
  rivals?: Record_;
  squadBattles?: Record_;
  champions?: Record_;
  championEntries?: number;
  draft?: { slot?: number; mode?: string; state?: string; formation?: string | null; wins?: number; losses?: number; completed?: boolean | number; pendingCoinReward?: number }[];
  /** A match is being created for that mode: the player must leave first. */
  pending?: { rivals?: boolean; squadBattles?: boolean; champions?: boolean } | null;
  resetPolicy?: string;
}

const FIELDS: Record<Exclude<Mode, 'draft'>, [string, string][]> = {
  rivals: [['Skill Rating', 'skillRating'], ['Division', 'division'], ['Placed', 'placed'], ['Wins', 'wins'], ['Draws', 'draws'], ['Losses', 'losses'],
    ['Weekly score', 'weeklyScore'], ['Weekly games', 'weeklyGames'], ['Aurora Champions points', 'fcPoints'], ['Form', 'form']],
  'squad-battles': [['Week', 'week'], ['Points', 'points'], ['Games', 'games'], ['Skipped', 'skipped'], ['Featured games', 'specialGames'],
    ['Wins', 'wins'], ['Draws', 'draws'], ['Losses', 'losses']],
  champions: [['Event', 'eventId'], ['Games', 'games'], ['Wins', 'wins'], ['Draws', 'draws'], ['Losses', 'losses'], ['Form', 'form']],
};

const shown = (value: unknown) => (value === null || value === undefined || value === '' ? '–' : Array.isArray(value) ? value.join(' ') : String(value));

export function ModesTab({ detail }: { detail: PlayerDetail }) {
  const toast = useToast();
  const client = useQueryClient();
  const modes = useQuery({ queryKey: ['modes', detail.id], queryFn: () => api<ModesReply>(`modes/${detail.id}`) });
  const [resetting, setResetting] = useState<Mode | null>(null);
  const reset = useMutation({
    mutationFn: (mode: Mode) => api(`modes/${detail.id}/${mode}/reset`, { method: 'POST', body: { confirm: true } }),
    onSuccess: (_reply, mode) => {
      toast(`${MODE_NAMES[mode]} progress reset for ${detail.name ?? 'the club'}.`);
      void client.invalidateQueries({ queryKey: ['modes', detail.id] });
      void client.invalidateQueries({ queryKey: clubKey(detail.id) });
    },
  });

  if (modes.isPending) return <Loading />;
  if (modes.isError) return <ErrorPanel what="The mode records" error={modes.error} onRetry={() => void modes.refetch()} />;
  const data = modes.data;
  const records: [Exclude<Mode, 'draft'>, Record_, boolean | undefined][] = [
    ['rivals', data.rivals, data.pending?.rivals],
    ['squad-battles', data.squadBattles, data.pending?.squadBattles],
    ['champions', data.champions, data.pending?.champions],
  ];

  return (
    <>
      {data.resetPolicy && <p className="dim">{data.resetPolicy}</p>}
      {detail.online && <p className="notice">The player must leave FIFA 21 before resetting competition progress.</p>}
      <div className="two-columns club-section-grid">
        {records.map(([mode, record, pending]) => (
          <Section key={mode} title={MODE_NAMES[mode]} actions={<Button size="sm" variant="danger" disabled={detail.online || reset.isPending} onClick={() => setResetting(mode)}>Reset progress</Button>}>
            {pending && <p className="notice">A pending match record exists for this mode.</p>}
            {record ? (
              <dl className="facts">
                {FIELDS[mode].map(([label, key]) => <div key={key}><dt>{label}</dt><dd>{shown(record[key])}</dd></div>)}
                {mode === 'champions' && <div><dt>Redeemed entries kept</dt><dd>{shown(data.championEntries)}</dd></div>}
              </dl>
            ) : <EmptyState title="No progress recorded" />}
          </Section>
        ))}
        <Section title="FUT Draft" actions={<Button size="sm" variant="danger" disabled={detail.online || reset.isPending} onClick={() => setResetting('draft')}>Reset runs</Button>}>
          <DataTable
            columns={[
              { key: 'slot', header: 'Slot', cell: (run) => shown(run.slot) },
              { key: 'mode', header: 'Mode', cell: (run) => shown(run.mode) },
              { key: 'state', header: 'State', cell: (run) => shown(run.state) },
              { key: 'formation', header: 'Formation', cell: (run) => shown(run.formation) },
              { key: 'record', header: 'W–L', cell: (run) => `${shown(run.wins)}–${shown(run.losses)}` },
              { key: 'completed', header: 'Drafts completed', align: 'end', cell: (run) => shown(run.completed) },
              { key: 'coins', header: 'Pending coins', align: 'end', cell: (run) => shown(run.pendingCoinReward) },
            ]}
            rows={(data.draft ?? []).map((run, index) => ({ ...run, index }))}
            rowKey={(run) => run.index}
            empty={<EmptyState title="No Draft run recorded" />}
          />
        </Section>
      </div>
      <ConfirmDialog
        open={resetting !== null}
        onOpenChange={(open) => { if (!open) setResetting(null); }}
        title={resetting ? `Reset ${MODE_NAMES[resetting]} for ${detail.name ?? 'this club'}?` : ''}
        text={resetting === 'draft'
          ? 'An unfinished paid Draft run and any pending coin reward will be lost.'
          : 'Current progress is cleared. Settled prizes and spent Weekend League entries stay recorded.'}
        confirmLabel={resetting === 'draft' ? 'Reset Draft runs' : 'Reset progress'}
        variant="danger"
        onConfirm={() => (resetting ? reset.mutateAsync(resetting) : undefined)}
      />
    </>
  );
}
