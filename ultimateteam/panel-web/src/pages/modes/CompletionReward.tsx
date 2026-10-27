import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useChangeset, useChangesetActions, useContentBusy } from '../../api/content';
import { useModeActions } from '../../api/modes';
import { isPick, useCatalogue } from '../../api/packs';
import { readRule, ruleChange } from '../../api/rules';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { StagedBadge } from '../../ui/Badge';
import { ErrorPanel, Loading, Section } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';

export type CompletionMode = 'rivals' | 'weekendLeague' | 'squadBattles' | 'draftOnline' | 'draftSinglePlayer';
export interface CompletionRewardValue { coins: number; fifaPoints?: number; xp?: number; packId: number | null; untradeable: boolean }

export function CompletionReward({ mode, title = 'Match completion reward' }: { mode: CompletionMode; title?: string }) {
  const path = ['matchCompletionRewards', mode];
  const live = useQuery({ queryKey: ['rules', 'seasons', 'completion', mode], queryFn: () => readRule('seasons', path) });
  const pending = useChangeset();
  const catalogue = useCatalogue();
  const actions = useModeActions();
  const changes = useChangesetActions();
  const busy = useContentBusy();
  const toast = useToast();
  const [draft, setDraft] = useState<CompletionRewardValue | null>(null);
  if (live.isPending || pending.isPending || catalogue.isPending) return <Loading />;
  if (live.isError) return <ErrorPanel what="The completion reward" error={live.error} onRetry={() => void live.refetch()} />;
  if (pending.isError) return <ErrorPanel what="The pending changes" error={pending.error} onRetry={() => void pending.refetch()} />;
  if (catalogue.isError) return <ErrorPanel what="The pack catalogue" error={catalogue.error} onRetry={() => void catalogue.refetch()} />;
  const change = ruleChange(pending.data!, 'seasons', path);
  const shown = (change ? (change.after as { value: CompletionRewardValue })?.value : live.data?.definition?.value) as CompletionRewardValue | undefined;
  if (!shown) return <Section title={title} text="Completion settings are missing. Update the server to load the mode reward rules." />;
  // A server from before Aurora Tokens per match (2026-10-04) has none: 0.
  const value = { ...shown, fifaPoints: shown.fifaPoints ?? 0, xp: shown.xp ?? 0, ...draft };
  const packs = (catalogue.data?.packs ?? []).filter(pack => pack.pending !== 'Delete' && !isPick(pack.config));
  const whole = (amount: number | undefined) => Number.isSafeInteger(amount) && amount! >= 0;
  const valid = whole(value.coins) && whole(value.fifaPoints) && whole(value.xp) && (value.packId === null || packs.some(pack => pack.id === value.packId));
  const dirty = draft !== null && JSON.stringify(value) !== JSON.stringify({ ...shown, fifaPoints: shown.fifaPoints ?? 0, xp: shown.xp ?? 0 });
  return <Section title={title} text="Flat coins, Aurora Tokens, Season XP and an optional pack for a completed match, whether you win, draw or lose. Difficulty and match statistics do not change this reward. Quits and void matches do not qualify.">
    <div className="mode-settings">
      <Field label="Coins per completed match" error={!Number.isSafeInteger(value.coins) || value.coins < 0 ? 'Enter a whole number from 0.' : null}>
        <input type="number" min={0} step={1} value={Number.isFinite(value.coins) ? value.coins : ''} disabled={busy}
          onChange={event => setDraft({ ...value, coins: /^\d+$/.test(event.target.value) ? Number(event.target.value) : NaN })} />
      </Field>
      <Field label="Aurora Tokens per completed match" hint="0 pays none." error={!whole(value.fifaPoints) ? 'Enter a whole number from 0.' : null}>
        <input type="number" min={0} step={1} value={Number.isFinite(value.fifaPoints) ? value.fifaPoints : ''} disabled={busy}
          onChange={event => setDraft({ ...value, fifaPoints: /^\d+$/.test(event.target.value) ? Number(event.target.value) : NaN })} />
      </Field>
      <Field label="Season XP per completed match" hint="Towards the Season Pass." error={!whole(value.xp) ? 'Enter a whole number from 0.' : null}>
        <input type="number" min={0} step={1} value={Number.isFinite(value.xp) ? value.xp : ''} disabled={busy}
          onChange={event => setDraft({ ...value, xp: /^\d+$/.test(event.target.value) ? Number(event.target.value) : NaN })} />
      </Field>
      <Field label="Pack per completed match">
        <select value={value.packId ?? ''} disabled={busy} onChange={event => setDraft({ ...value, packId: event.target.value ? Number(event.target.value) : null })}>
          <option value="">No pack</option>
          {value.packId !== null && !packs.some(pack => pack.id === value.packId) && <option value={value.packId}>Unavailable pack {value.packId}</option>}
          {packs.map(pack => <option key={pack.id} value={pack.id}>{pack.description ?? `Pack ${pack.id}`}</option>)}
        </select>
      </Field>
      <Field label="Pack tradeability">
        <select value={value.untradeable ? 'untradeable' : 'tradeable'} disabled={busy || value.packId === null}
          onChange={event => setDraft({ ...value, untradeable: event.target.value === 'untradeable' })}>
          <option value="untradeable">Untradeable</option><option value="tradeable">Tradeable</option>
        </select>
      </Field>
    </div>
    <div className="mode-setting-actions">
      {change && <><StagedBadge /><Button size="sm" variant="ghost" disabled={busy} onClick={() => changes.remove.mutate(change.id, { onSuccess: () => setDraft(null), onError: error => toast(String(error), 'error') })}>Undo staged completion reward</Button></>}
      {dirty && <><Button size="sm" variant="ghost" disabled={busy} onClick={() => setDraft(null)}>Discard edits</Button>
        <Button size="sm" variant="staged" disabled={busy || !valid} onClick={() => actions.stageSetting.mutate({ file: 'seasons', path, value, shown }, {
          onSuccess: () => { setDraft(null); toast('Completion reward saved.', 'done'); },
          onError: error => toast(error instanceof Error ? error.message : String(error), 'error'),
        })}>Save completion reward</Button></>}
    </div>
  </Section>;
}
