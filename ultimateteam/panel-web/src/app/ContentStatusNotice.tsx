import { useState } from 'react';
import { ApiError } from '../api/client';
import { useChangeset, useContentBusy, useContentStatus, useReloadContent, type ContentReport } from '../api/content';
import { Button, LinkButton } from '../ui/Button';
import { ConfirmDialog } from '../ui/Dialog';
import { Reveal } from '../ui/Reveal';
import { useToast } from '../ui/Toast';
import { href } from './router';

/** SQL edits stay unloaded until an operator explicitly reviews and reloads them. */
export function ContentStatusNotice() {
  const status = useContentStatus();
  const pending = useChangeset();
  const reload = useReloadContent();
  const busy = useContentBusy();
  const toast = useToast();
  const [confirming, setConfirming] = useState(false);
  const [report, setReport] = useState<ContentReport | null>(null);
  const unsupported = status.error instanceof ApiError && status.error.status === 404;
  const changed = status.data?.reloadRequired === true;
  return <>
    {status.isError && !unsupported && <div className="content-warning" role="alert">
      <b>Content status could not be checked.</b> {status.error.message}
      <Button size="sm" variant="ghost" onClick={() => void status.refetch()} disabled={status.isFetching}>Check again</Button>
    </div>}
    <Reveal show={changed}>
      <section className="content-warning" aria-label="External content changes">
        <b>Database content changed outside the loaded revision.</b>
        <p>The game still uses revision {status.data?.revision}. Review your pending edits before loading the database changes.</p>
        <details><summary>Changed tables ({status.data?.tables?.length ?? 0})</summary>
          <ul>{status.data?.tables?.map((table) => <li key={table.table}><span className="mono">{table.table}</span> · {table.changedAt}</li>)}</ul>
        </details>
        <div className="row-actions mt">
          <LinkButton href={href('changes')}>Review pending changes</LinkButton>
          <Button onClick={() => setConfirming(true)} disabled={busy || status.isError || pending.isPending || pending.isError}>Reload content</Button>
        </div>
      </section>
    </Reveal>
    <Reveal show={report !== null}>
      <section className="content-warning" aria-label="Content reload result">
        <b>Content revision {report?.revision ?? '?'} loaded.</b>
        {report?.warnings?.map((text) => <p key={text}>{text}</p>)}
        {report?.notes?.map((text) => <p className="dim" key={text}>{text}</p>)}
        <Button size="sm" variant="ghost" onClick={() => setReport(null)}>Dismiss reload result</Button>
      </section>
    </Reveal>
    <ConfirmDialog open={confirming} onOpenChange={setConfirming} title="Reload database content?"
      text={`The next request for every club uses the new content. Draft screens may change. ${pending.data?.pending ?? 0} pending edit(s) stay staged; reload does not publish them. Validate them again afterwards.`}
      confirmLabel="Reload content" onConfirm={() => reload.mutateAsync().then((reply) => {
        setReport(reply); toast(`Content revision ${reply.revision ?? '?'} loaded.`);
      })} />
  </>;
}
