import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { href } from '../../app/router';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { ago } from '../../ui/format';
import { DataTable } from '../../ui/DataTable';
import { Badge } from '../../ui/Badge';
import './operations-review.css';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section } from '../../ui/Layout';

interface LogsReply { pinned?: string[]; files?: { name: string; size?: number; mtime?: string }[] }
interface TailReply { name?: string; size?: number; mtime?: string; truncated?: boolean; lines?: string[] }
const validFile = (name: string) => /^[A-Za-z0-9._-]{1,128}$/.test(name);

export function LogsPage({ file }: { file?: string }) {
  const [fileSearch, setFileSearch] = useState('');
  const [follow, setFollow] = useState(true);
  const list = useQuery({ queryKey: ['logs'], queryFn: ({ signal }) => api<LogsReply>('logs', { signal }), refetchInterval: follow ? 10_000 : false, retry: false });
  const selected = file && validFile(file) ? file : !file ? list.data?.pinned?.[0] ?? list.data?.files?.[0]?.name : undefined;
  const [filter, setFilter] = useState('');
  const [debounced, setDebounced] = useState('');
  const exists = Boolean(selected && list.data?.files?.some(entry => entry.name === selected));
  useEffect(() => { const timer = window.setTimeout(() => setDebounced(filter.trim()), 300); return () => window.clearTimeout(timer); }, [filter]);
  const tail = useQuery({
    queryKey: ['log-tail', selected, debounced],
    queryFn: ({ signal }) => api<TailReply>(`logs/${encodeURIComponent(selected!)}?lines=500&q=${encodeURIComponent(debounced)}`, { signal }),
    enabled: exists, refetchInterval: follow ? 3000 : false, retry: false,
  });
  return <div className="operations-review logs-review">
    <PageHeader title="Logs" description="Server logs, limited to the latest 500 matching lines per view." actions={<><Button onClick={() => { setFollow(!follow); if (!follow) { void list.refetch(); if (exists) void tail.refetch(); } }}>{follow ? 'Pause follow' : 'Resume follow'}</Button><Button onClick={() => { void list.refetch(); if (exists) void tail.refetch(); }} disabled={list.isFetching || tail.isFetching}>Refresh</Button></>} />
    {list.isError && <ErrorPanel what="The log file list" error={list.error} onRetry={() => void list.refetch()} />}
    {file && !validFile(file) ? <EmptyState title="Invalid log filename" /> : list.isPending ? <Loading /> : !list.data ? null : <div className="logs-layout">
      <Section title="Files">
        <Field label="Find a log file"><input type="search" value={fileSearch} onChange={event => setFileSearch(event.target.value)} placeholder="Filename…" /></Field>
        <DataTable key={fileSearch} pageSize={10} fixedLayout rows={(list.data.files ?? []).filter(row => row.name.toLowerCase().includes(fileSearch.trim().toLowerCase()))} rowKey={(row) => row.name} columns={[
          { key: 'name', header: 'File', width: 190, sort: row => row.name, cell: (row) => <a href={href('logs', row.name)} aria-current={row.name === selected ? 'page' : undefined}>{row.name}</a> },
          { key: 'size', header: 'Size', width: 90, sort: row => row.size, cell: row => row.size == null ? '–' : `${(row.size / 1024).toFixed(1)} KB` },
          { key: 'time', header: 'Updated', width: 110, sort: row => row.mtime, cell: (row) => ago(row.mtime) },
        ]} empty={<EmptyState title={fileSearch ? 'No files match' : 'No log files'} />} />
      </Section>
      <Section title={selected ?? 'Log tail'} actions={exists && <Badge tone={tail.isError ? 'red' : follow ? 'pitch' : 'staged'}>{tail.isError ? 'Refresh failed' : follow ? 'Following every 3 seconds' : 'Follow paused'}</Badge>}>
        {exists && <div className="logs-tools"><Field label="Filter lines"><input type="search" maxLength={200} value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="Text to find" /></Field><Button variant="ghost" disabled={!filter} onClick={() => { setFilter(''); setDebounced(''); }}>Clear filter</Button></div>}
        {tail.isError && exists && <ErrorPanel what="The log tail" error={tail.error} onRetry={() => void tail.refetch()} />}
        {!selected ? <EmptyState title="No log file selected" /> : !exists ? <EmptyState title="No such log file" /> : tail.isPending ? <Loading /> : tail.data && <>
          {tail.isError && <p className="notice">Showing the last loaded lines; this view is not current.</p>}
          {tail.data.truncated && <p className="notice">Only the last 4 MB of this file was searched.</p>}
          <p className="dim">File updated {ago(tail.data.mtime)} · {tail.data.lines?.length ?? 0} of up to 500 matching lines · View loaded {new Date(tail.dataUpdatedAt).toLocaleString('en-GB')}</p>
          <pre className="log-tail" aria-label="Log lines" tabIndex={0}>{tail.data.lines?.join('\n') || 'No matching lines.'}</pre>
        </>}
      </Section>
    </div>}
  </div>;
}
