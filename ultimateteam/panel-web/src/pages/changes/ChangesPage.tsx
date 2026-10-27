// Changes (plan 5.1): Pending — the open changeset, validated and published as one — and History, where a published
// changeset can be reverted through the same review. Routes: #/changes and #/changes/history.
import { useEffect, useId, useRef, useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { usePanelMotion } from '../../ui/Motion';
import { rememberBy, rememberedBy, rememberRecovery, recoveryStorageKey, refusal, RestageError, useChangeset, useChangesetActions, useContentBusy, useContentDestination, useContentStatus, useHistory, type PublishReply, type ValidationReply } from '../../api/content';
import { ApiError } from '../../api/client';
import type { Changeset, ContentChange } from '../../api/types';
import { areaLabel, changeTitle, keyLabel } from '../../app/changes';
import { href } from '../../app/router';
import { Badge, StagedBadge } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { ConfirmDialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { ago } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section } from '../../ui/Layout';
import { Tabs } from '../../ui/Tabs';
import { useToast } from '../../ui/Toast';
import { Reveal } from '../../ui/Reveal';
import { ChangeList } from './ChangeList';
import './changes.css';

const plural = (count: number, one: string) => `${count.toLocaleString('en-GB')} ${one}${count === 1 ? '' : 's'}`;
const readRecovery = (): RestageError | null => {
  try {
    const raw: unknown = JSON.parse(sessionStorage.getItem(recoveryStorageKey()) ?? 'null');
    if (!raw || typeof raw !== 'object' || !('area' in raw) || typeof raw.area !== 'string' ||
      !('id' in raw) || typeof raw.id !== 'number' || !('operation' in raw) || typeof raw.operation !== 'string' ||
      !('key' in raw) || !raw.key || typeof raw.key !== 'object' || Array.isArray(raw.key)) return null;
    return new RestageError(new Error('The retained draft has not been restored yet.'), raw as RestageError['change']);
  } catch { return null; }
};

export function ChangesPage({ tab }: { tab: string | undefined }) {
  const active = tab === 'history' ? 'history' : 'pending';
  const changeset = useChangeset();
  const pending = Number(changeset.data?.pending) || 0;
  return (
    <div className="changes-page">
      <PageHeader className="changes-page-header" title="Changes" description="Review pending edits, then publish them together to apply them in the game." />
      <Tabs label="Changes" value={active} onChange={(next) => { window.location.hash = next === 'history' ? href('changes', 'history') : href('changes'); }}
        items={[{ value: 'pending', label: 'Pending', count: pending }, { value: 'history', label: 'History' }]}>
        {active === 'history' ? <History /> : <Pending />}
      </Tabs>
    </div>
  );
}

function Pending() {
  const destination = useContentDestination();
  const toast = useToast();
  const changeset = useChangeset();
  const actions = useChangesetActions();
  const status = useContentStatus();
  const busy = useContentBusy();
  const [checked, setChecked] = useState<{ fingerprint: string; result: ValidationReply } | null>(null);
  const [recovery, setRecovery] = useState<RestageError | null>(readRecovery);
  const [area, setArea] = useState('');
  const [search, setSearch] = useState('');
  const [published, setPublished] = useState<PublishReply | null>(null);
  const [discarding, setDiscarding] = useState<string | null>(null);
  const [droppingRecovery, setDroppingRecovery] = useState(false);
  useEffect(() => { if (recovery) rememberRecovery(recovery.change); }, [recovery]);

  if (changeset.isPending) return <Loading />;
  if (changeset.isError && !changeset.data) {
    if (changeset.error instanceof ApiError && changeset.error.status === 404) {
      return <EmptyState title="No content changes on this server" text="The running panel API predates content changesets: redeploy the server." />;
    }
    return <ErrorPanel what="The pending changes" error={changeset.error} onRetry={() => void changeset.refetch()} />;
  }
  const open = changeset.data?.open ? changeset.data.changeset ?? null : null;
  const changes = open?.changes ?? [];
  const fingerprint = JSON.stringify([open, status.data, changeset.isError, status.isError]);
  const result = checked?.fingerprint === fingerprint ? checked.result : null;
  const setResult = (value: ValidationReply | null) => setChecked(value ? { fingerprint, result: value } : null);
  const monitoringFailed = status.isError && !(status.error instanceof ApiError && status.error.status === 404);
  const blocked = busy || changeset.isError || status.isPending || monitoringFailed || status.data?.reloadRequired === true;
  const visible = changes.filter((change) => (!area || change.area === area) && (!search.trim() ||
    `${areaLabel(change.area)} ${keyLabel(change.key)} ${changeTitle(change)} ${JSON.stringify(change.after)}`.toLowerCase().includes(search.trim().toLowerCase())));

  const remove = (change: ContentChange) => actions.remove.mutate(change.id, {
    onSuccess: () => { setResult(null); toast('Change removed.'); },
    onError: (error) => toast(error.message, 'error'),
  });
  const keep = (change: ContentChange) => actions.restage.mutate(change, {
    onSuccess: () => { setResult(null); toast('Staged again on the live value.', 'staged'); },
    onError: (error) => { if (error instanceof RestageError) setRecovery(error); toast(error.message, 'error'); },
  });

  return (
    <div className="changes-pending">
      {changeset.isError && <ErrorPanel what="The latest pending changes" error={changeset.error} onRetry={() => void changeset.refetch()} />}
      <Reveal show={published !== null}>{published && <PublishedNotice reply={published} onClose={() => setPublished(null)} />}</Reveal>
      {recovery && <Section title="Your value is retained here" text={recovery.message}>
        <p className="dim">This recovery copy is saved in this browser tab. Check Pending before retrying if the connection failed.</p>
        <pre className="changes-recovery">{JSON.stringify({ area: recovery.change.area, key: recovery.change.key, operation: recovery.change.operation, after: recovery.change.after }, null, 2)}</pre>
        <div className="row-actions"><Button disabled={busy} onClick={() => actions.recover.mutate(recovery.change, {
          onSuccess: () => { setRecovery(null); setResult(null); toast('Your value is staged again.', 'staged'); },
          onError: (error) => toast(error.message, 'error'),
        })}>Retry staging retained value</Button>
        <Button variant="ghost" disabled={busy} onClick={() => setDroppingRecovery(true)}>Discard retained value</Button></div>
      </Section>}
      {!changes.length ? (
        <EmptyState title={open ? 'The open changeset is empty' : 'Nothing is pending'}
          text="Staged card, pack, pick, promo, event and rule edits collect here for review."
          action={open ? <Button size="sm" disabled={busy || changeset.isError} onClick={() => actions.discard.mutate(undefined, { onError: (error) => toast(error.message, 'error') })}>Close the empty changeset</Button> : undefined} />
      ) : (
        <>
          <Section className="changes-summary" title="Pending review">
          <div className="changeset-head">
            <div>
              <b>Changeset #{open?.id}</b> · {plural(changes.length, 'change')}, opened {ago(open?.createdAt)}{open?.createdBy ? ` by ${open.createdBy}` : ''}
              {open?.revertsChangesetId ? <> <Badge tone="staged">reverts #{open.revertsChangesetId}</Badge></> : null}
            </div>
            <div className="row-actions">
              <Button variant="danger" disabled={busy || changeset.isError} onClick={() => setDiscarding(fingerprint)}>Discard all</Button>
            </div>
          </div>
          </Section>
          {checked && !result && <p className="notice" role="status">Content or pending edits changed since the last check. Validate again.</p>}
          <Reveal show={result !== null}>{result && <ValidationResult result={result} changes={changes} onKeep={keep} onRemove={remove} recoveryPending={recovery !== null} />}</Reveal>
          <Section title="Review changes" className="changes-filters">
          <div className="change-tools">
            <Field label="Content area"><select value={area} onChange={(event) => setArea(event.target.value)}>
              <option value="">All areas</option>{[...new Set(changes.map((change) => change.area))].map((value) => <option key={value} value={value}>{areaLabel(value)}</option>)}
            </select></Field>
            <Field label="Find pending changes"><input type="search" placeholder="Search items, fields and values…" value={search} onChange={(event) => setSearch(event.target.value)} /></Field>
            <Button variant="ghost" disabled={!area && !search} onClick={() => { setArea(''); setSearch(''); }}>Clear filters</Button>
          </div>
          <p className="dim">Showing {visible.length} of {changes.length} pending changes. Publishing includes every pending change.</p>
          </Section>
          {visible.length ? <ChangeList changes={visible} onRemove={remove} busy={busy || changeset.isError} /> : <EmptyState title="No matching pending changes" text="Clear the search or choose another content area." />}
          {destination ? <Section title="Scheduled drop draft" text="Review this drop in Schedule to validate it, choose its release time or explicitly publish it now."><LinkButton href={href('schedule')}>Review in Schedule</LinkButton></Section> : <PublishForm count={changes.length} fingerprint={fingerprint} blocked={blocked || recovery !== null} result={result}
            onPublished={(reply) => { setResult(null); setPublished(reply); }} onRefused={setResult} />}
          {status.data?.reloadRequired && <p className="notice">Reload the external database changes above, then validate before publishing.</p>}
          {monitoringFailed && <p className="notice">Publishing is paused until the content status check succeeds.</p>}
        </>
      )}
      <ConfirmDialog open={discarding === fingerprint} onOpenChange={(next) => { if (!next) setDiscarding(null); }} title="Discard every pending change?"
        text={`${plural(changes.length, 'change')} will be dropped. The live content does not change.`}
        confirmLabel="Discard all" variant="danger"
        onConfirm={() => {
          if (discarding !== fingerprint) return Promise.reject(new Error('Pending edits changed. Review and confirm again.'));
          return actions.discard.mutateAsync().then(() => { setResult(null); toast('Pending changes discarded.'); });
        }} />
      <ConfirmDialog open={droppingRecovery} onOpenChange={setDroppingRecovery} title="Discard the retained value?"
        text="This removes the recovery copy in this browser tab. The database and any already staged edits are unchanged. Copy it first if you want to keep it."
        confirmLabel="Discard retained value" variant="danger" onConfirm={() => { rememberRecovery(null); setRecovery(null); }} />
    </div>
  );
}

function ValidationResult({ result, changes, onKeep, onRemove, recoveryPending }: { result: ValidationReply; changes: ContentChange[]; onKeep: (change: ContentChange) => void; onRemove: (change: ContentChange) => void; recoveryPending: boolean }) {
  const busy = useContentBusy();
  if (result.ok) {
    return (
      <p className="ready" role="status">
        <b>Ready to publish.</b> {plural(result.changes ?? changes.length, 'change')} apply cleanly, and the content loads the way the server loads it at start-up.
      </p>
    );
  }
  const conflicts = result.conflicts ?? [];
  return (
    <div className="stack">
      {(result.errors ?? []).map((error) => <p key={error} className="error-box" role="alert">{error}</p>)}
      {conflicts.length > 0 && (
        <Section className="changes-conflicts" title="Conflicts" text={'These rows changed after they were edited here. "Keep my value" stages your value again on top of the live row; Remove drops your change.'}>
          <ul className="change-list">
            {conflicts.map((conflict) => {
              const mine = changes.find((change) => change.id === conflict.changeId);
              return (
                <li key={conflict.changeId}>
                  <div className="change-head">
                    <b>{areaLabel(conflict.area)}</b><span className="mono">{keyLabel(conflict.key)}</span>
                    <Button size="sm" disabled={!mine || busy || recoveryPending}
                      onClick={() => mine && onKeep(mine)}>
                      Keep my value
                    </Button>
                    <Button size="sm" variant="ghost" disabled={busy || !mine}
                      onClick={() => mine && onRemove(mine)}>
                      Remove
                    </Button>
                  </div>
                  <div className="diff">
                    <div className="diff-row"><span className="dim">Edited from</span><span className="mono">{JSON.stringify(conflict.expected ?? null)}</span></div>
                    <div className="diff-row"><span className="dim">Live now</span><span className="mono">{JSON.stringify(conflict.live ?? null)}</span></div>
                    <div className="diff-row"><span className="dim">Your staged value</span><span className="mono">{JSON.stringify(mine?.after ?? null)}</span></div>
                  </div>
                </li>
              );
            })}
          </ul>
        </Section>
      )}
    </div>
  );
}

function PublishForm({ count, fingerprint, blocked, result, onPublished, onRefused }: { count: number; fingerprint: string; blocked: boolean; result: ValidationReply | null; onPublished: (reply: PublishReply) => void; onRefused: (result: ValidationReply) => void }) {
  const toast = useToast();
  const actions = useChangesetActions();
  const [note, setNote] = useState('');
  const [by, setBy] = useState(rememberedBy);
  const [confirming, setConfirming] = useState<string | null>(null);
  const latest = useRef(fingerprint);
  latest.current = fingerprint;
  const { reduced, transition } = usePanelMotion();
  const validate = async () => {
    if (blocked) return;
    if (result?.ok === true) { setConfirming(fingerprint); return; }
    const reviewed = fingerprint;
    try {
      const reply = await actions.validate.mutateAsync();
      if (latest.current !== reviewed) return;
      onRefused(reply);
      if (reply.ok === true) setConfirming(reviewed);
    } catch (error) {
      if (latest.current === reviewed) onRefused(refusal(error));
    }
  };
  const state = actions.publish.isPending ? 'publishing' : actions.validate.isPending ? 'checking' : result?.ok === true ? 'ready' : result ? 'error' : 'idle';
  const label = state === 'publishing' ? 'Publishing…' : state === 'checking' ? 'Validating…' : state === 'ready' ? `Publish ${plural(count, 'change')}` : state === 'error' ? 'Retry validation' : 'Validate & publish';
  const publish = () => {
    if (blocked || result?.ok !== true) return Promise.reject(new Error('Publishing requires a successful validation of the current edits.'));
    if (confirming !== fingerprint) return Promise.reject(new Error('Pending edits changed. Review and confirm again.'));
    if (by.trim()) rememberBy(by.trim());
    return actions.publish.mutateAsync({ note: note.trim() || null, by: by.trim() || null })
      .then((reply) => { toast(`Content revision ${reply.content?.revision ?? '?'} is live.`); setNote(''); onPublished(reply); })
      // A refusal shows its reasons above the list; the dialog closes because nothing was published.
      .catch((error: unknown) => { onRefused(refusal(error)); });
  };
  return (
    <Section className="changes-publish" title="Publish" text="Validate every pending edit, then confirm publishing. If any edit fails, nothing is published.">
      <div className="changes-publish-fields">
        <Field label="Note" hint="What this changes and why; shown in the history.">
          <input value={note} maxLength={500} onChange={(event) => setNote(event.target.value)} />
        </Field>
        <Field label="Published by" hint="A label for the history; the panel has no accounts.">
          <input value={by} maxLength={64} onChange={(event) => setBy(event.target.value)} />
        </Field>
        <div><Button className="changes-publish-action" data-state={state} variant="primary" disabled={blocked} onClick={() => void validate()} aria-label={label} aria-busy={state === 'checking' || state === 'publishing'}>
          <AnimatePresence initial={false} mode="wait">
            <motion.span className="changes-publish-state" key={state} initial={{ opacity: 0, y: reduced ? 0 : 6 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: reduced ? 0 : -6 }} transition={transition}>
              <span className="changes-publish-symbol" aria-hidden="true">{state === 'checking' || state === 'publishing' ? <motion.span animate={reduced ? {} : { rotate: 360 }} transition={{ duration: 1, repeat: Infinity, ease: 'linear' }}>◌</motion.span> : state === 'ready' ? '✓' : state === 'error' ? '!' : '↑'}</span>
              <span>{label}</span>
            </motion.span>
          </AnimatePresence>
        </Button></div>
      </div>
      <ConfirmDialog open={confirming === fingerprint && result?.ok === true && (!blocked || actions.publish.isPending)} onOpenChange={(next) => { if (!next) setConfirming(null); }} title={`Publish ${plural(count, 'change')}?`}
        text="Every change applies together, checked the way start-up checks the content; every club uses it from its next request. If anything fails, nothing is published."
        confirmLabel="Publish" onConfirm={publish} />
    </Section>
  );
}

function PublishedNotice({ reply, onClose }: { reply: PublishReply; onClose: () => void }) {
  return (
    <Section title={`Published changeset #${reply.published?.id ?? '?'}`} actions={<Button size="sm" variant="ghost" onClick={onClose}>Dismiss</Button>}
      text={`Content revision ${reply.content?.revision ?? '?'} is live.`}>
      {(reply.content?.warnings ?? []).map((warning) => <p key={warning} className="notice">{warning}</p>)}
      {(reply.content?.notes ?? []).map((line) => <p key={line} className="dim">{line}</p>)}
    </Section>
  );
}

function History() {
  const history = useHistory();
  const pending = useChangeset();
  if (history.isPending) return <Loading />;
  if (history.isError) return <ErrorPanel what="The change history" error={history.error} onRetry={() => void history.refetch()} />;
  const list = history.data.changesets ?? [];
  if (!list.length) return <EmptyState title="Nothing has been published yet" text="Published changesets are listed here, newest first." />;
  return <div className="changes-history"><p className="dim">Latest {plural(list.length, 'published changeset')}, newest first. Up to 50 are shown.</p>
    {(pending.data?.pending ?? 0) > 0 && <p className="notice">Publish or discard pending edits before staging a revert. <a href={href('changes')}>Review Pending</a></p>}
    {pending.isError && <ErrorPanel what="Pending edits" error={pending.error} onRetry={() => void pending.refetch()} />}
    {list.map((changeset) => <PublishedChangeset key={changeset.id} changeset={changeset} />)}</div>;
}

function PublishedChangeset({ changeset }: { changeset: Changeset }) {
  const toast = useToast();
  const actions = useChangesetActions();
  const busy = useContentBusy();
  const pending = useChangeset();
  const detailsId = useId();
  const [open, setOpen] = useState(false);
  const [reverting, setReverting] = useState(false);
  const changes = changeset.changes ?? [];
  const hasNewCard = changes.some((change) => change.area === 'player_card' && change.operation === 'insert');
  const hasPending = (pending.data?.pending ?? 0) > 0;
  return (
    <Section
      title={<>#{changeset.id} {changeset.revertsChangesetId ? <StagedBadge>reverts #{changeset.revertsChangesetId}</StagedBadge> : null}</>}
      actions={<span className="dim" title={changeset.publishedAt ?? ''}>published {ago(changeset.publishedAt)}</span>}
      text={changeset.note || 'No note.'}
    >
      <div className="row-actions">
        <span className="dim">{plural(changes.length, 'change')}{changeset.createdBy ? ` · by ${changeset.createdBy}` : ''}</span>
        <Button size="sm" variant="ghost" onClick={() => setOpen(!open)} aria-expanded={open} aria-controls={detailsId}>{open ? 'Hide changes' : 'Show changes'}</Button>
        <Button size="sm" onClick={() => setReverting(true)} disabled={hasNewCard || busy || hasPending || !pending.data || pending.isError} title={hasNewCard ? 'A new card may already be owned by a club; make it grant-only in the cards rules instead.' : hasPending ? 'Publish or discard pending edits before staging a revert.' : undefined}>Revert</Button>
      </div>
      {hasNewCard && <p className="dim">This changeset added a player card. It cannot be reverted because clubs may own it; use the cards rules to make it grant-only.</p>}
      <Reveal show={open} id={detailsId}><div className="mt"><ChangeList changes={changes} /></div></Reveal>
      <ConfirmDialog open={reverting && !hasPending && !pending.isError && !!pending.data} onOpenChange={setReverting} title={`Revert changeset #${changeset.id}?`}
        text="Its previous values are staged as pending changes (a new row is hidden). Nothing changes in the game until you publish them."
        confirmLabel="Stage the revert" variant="staged"
        onConfirm={() => actions.revert.mutateAsync({ id: changeset.id, by: rememberedBy() || null }).then(() => {
          toast('Revert staged: review it under Pending.', 'staged');
          window.location.hash = href('changes');
        })} />
    </Section>
  );
}
