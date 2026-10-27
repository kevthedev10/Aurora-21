// Rules (plan 5.4): every game rule is a database row (file, path, value) that the server reads. Edits, removals, new
// keys and imports from the bundled files are staged and published under Changes. Address: #/rules/<file>?q=&offset=.
import { useCallback, useEffect, useState } from 'react';
import { motion } from 'motion/react';
import { useChangeset, useChangesetActions, useContentBusy } from '../../api/content';
import { changedRule, sameRuleKey, parseJson, parsePath, pathText, RULES_PAGE, useRuleActions, useRuleFiles, useRulePage, type RulePath, type RuleRow } from '../../api/rules';
import type { ContentChange } from '../../api/types';
import { href } from '../../app/router';
import { Badge, StagedBadge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { ConfirmDialog, Dialog } from '../../ui/Dialog';
import { Field } from '../../ui/Field';
import { num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section } from '../../ui/Layout';
import { useToast } from '../../ui/Toast';
import { usePanelMotion } from '../../ui/Motion';
import { Reveal } from '../../ui/Reveal';
import './rules.css';

const shown = (value: unknown) => { const text = JSON.stringify(value); return text && text.length > 160 ? `${text.slice(0, 159)}…` : text ?? '—'; };
const descriptions: Record<string, [string, string]> = {
  draft: ['Draft', 'Formations, player selection and entry item settings.'], cards: ['Cards', 'Card attributes, prices and item behaviour.'],
  club: ['Club', 'Club and squad rules.'], squad_battles: ['Squad Battles', 'Competition and match settings.'], seasons: ['Modes & seasons', 'Season and competitive mode settings.'],
  onboarding: ['Onboarding', 'New club setup and starter rewards.'],
  market: ['Transfer market', 'Listing, pricing and market settings.'], rivals_rewards: ['Rivals settlement', 'Delay before weekly rewards settle.'],
  sqbt_rewards: ['Squad Battles settlement', 'Delay before weekly rewards settle.'], sqbt_totw: ['Squad Battles opponents', 'Featured opponent settings.'], staff: ['Staff', 'Staff membership and prize eligibility.'],
  pack_odds: ['Pack defaults & reward copies', 'Starting odds for newly created packs and picks, and red reward card revisions.'], tradeability: ['Tradeability', 'Restrictions by reward source and pack.'],
};
/** Also omit these files when connected to a server from before the curated browser. */
const DEDICATED = new Set(['sbc', 'objectives', 'champions_rewards', 'draft']);
const fileTitle = (file: string) => descriptions[file]?.[0] ?? file.replaceAll('_', ' ');
const valueType = (value: unknown) => value === null ? 'Null' : Array.isArray(value) ? 'List' : typeof value === 'boolean' ? 'Toggle' : typeof value === 'number' ? 'Number' : typeof value === 'string' ? 'Text' : 'Object';

export function RulesPage({ rest, params }: { rest: string[]; params: URLSearchParams }) {
  const comments = params.get('comments') === '1';
  const files = useRuleFiles(comments);
  const list = (files.data?.files ?? []).filter((entry) => !DEDICATED.has(entry.file));
  const file = list.some((entry) => entry.file === rest[0]) ? rest[0] : list[0]?.file;
  const q = params.get('q') ?? '';
  const offset = Math.max(0, Number(params.get('offset')) || 0);
  const [importing, setImporting] = useState(false);
  const [fileSearch, setFileSearch] = useState('');
  const [advanced, setAdvanced] = useState(false);
  const busy = useContentBusy();
  const { reduced, transition } = usePanelMotion();
  const go = useCallback((next: string | undefined, query: string, from: number, documentation = comments) => {
    if (!next) return;
    const qs = new URLSearchParams({ ...(query && { q: query }), ...(from && { offset: String(from) }), ...(documentation && { comments: '1' }) }).toString();
    window.location.hash = `${href('rules', next)}${qs ? `?${qs}` : ''}`;
  }, [comments]);
  if (files.isSuccess && rest[0] && !list.some(entry => entry.file === rest[0])) return <><PageHeader title="Rules" /><EmptyState title="Rule file not found" text="Choose a file from the available rules." action={<a href={href('rules')}>All rules</a>} /></>;

  return (
    <>
      <PageHeader title="Rules" description="Gameplay and economy settings without a dedicated editor. Saving applies edits immediately."
        actions={<Button onClick={() => setImporting(true)} disabled={!file || busy}>Import from files</Button>} />
      {files.isPending ? <Loading /> : files.isError ? <ErrorPanel what="The rule files" error={files.error} onRetry={() => void files.refetch()} /> :
        !list.length ? <EmptyState title="No rule files available" text="The server has no rule files to show." /> : (
          <div className="rules-layout rules-review">
            <nav className="file-list" aria-label="Rule files">
              <Field label="Find a rule file"><input type="search" placeholder="Search sections…" value={fileSearch} onChange={e => setFileSearch(e.target.value)} /></Field>
              {list.filter(entry => `${entry.file} ${fileTitle(entry.file)}`.toLowerCase().includes(fileSearch.toLowerCase())).map((entry) => (
                <motion.a key={entry.file} href={href('rules', entry.file)} aria-current={entry.file === file ? 'page' : undefined} whileHover={reduced ? {} : { x: 2 }} transition={transition}>
                  <span><strong>{fileTitle(entry.file)}</strong><small>{entry.file}.json</small></span><span className="dim">{num(entry.rules)}</span>
                </motion.a>
              ))}
            </nav>
            <div className="stack">
              {file && <RuleTable key={file} file={file} q={q} offset={Number.isSafeInteger(offset) && offset <= 1_000_000 ? offset : 0} comments={comments} go={go} />}
              <div><Button variant="ghost" aria-expanded={advanced} aria-controls="advanced-rule-editor" onClick={() => setAdvanced(!advanced)}>Advanced editing</Button></div>
              <Reveal show={advanced} id="advanced-rule-editor">{file && <AnyLocation key={file} file={file} />}</Reveal>
            </div>
          </div>
        )}
      {importing && file && <ImportDialog file={file} onClose={() => setImporting(false)} />}
    </>
  );
}

function RuleTable({ file, q, offset, comments, go }: { file: string; q: string; offset: number; comments: boolean; go: (file: string, q: string, offset: number, comments?: boolean) => void }) {
  const toast = useToast();
  const page = useRulePage(file, q, offset, comments);
  const open = useChangeset();
  const actions = useRuleActions();
  const changeActions = useChangesetActions();
  const busy = useContentBusy();
  const [text, setText] = useState(q);
  const [editing, setEditing] = useState<RuleRow | null>(null);
  const [removing, setRemoving] = useState<RuleRow | null>(null);
  useEffect(() => setText(q), [q]);
  useEffect(() => {
    const timer = window.setTimeout(() => { if (text.trim() !== q) go(file, text.trim(), 0); }, 350);
    return () => window.clearTimeout(timer);
  }, [text, q, file, go]);
  const staged = (open.data?.changeset?.changes ?? []).filter((change) => change.area === 'rules');
  const stagedFor = (path: RulePath) => staged.find((change) => sameRuleKey(change.key, file, path));
  const total = page.data?.total ?? 0;
  const rows = page.data?.rules ?? [];
  useEffect(() => {
    if (page.isSuccess && !page.isPlaceholderData && offset > 0 && offset >= total) go(file, q, total ? Math.floor((total - 1) / RULES_PAGE) * RULES_PAGE : 0);
  }, [page.isSuccess, page.isPlaceholderData, offset, total, file, q, go]);
  const additions = staged.filter(c => c.key.file === file && c.operation === 'insert');

  return (
    <Section title={fileTitle(file)} text={descriptions[file]?.[1] ?? ''} className="rules-section" actions={<span className="dim">{num(total)} rule{total === 1 ? '' : 's'}{q ? ' matching' : ''}</span>}>
      <div className="rules-toolbar"><Field label="Search rules"><input type="search" placeholder="Search paths and values…" value={text} onChange={(event) => setText(event.target.value)} /></Field>
        <Button variant="ghost" disabled={!text && !q} onClick={() => { setText(''); go(file, '', 0); }}>Clear search</Button>
        <label className="rules-documentation"><input type="checkbox" checked={comments} onChange={e => go(file, q, 0, e.target.checked)} />Show documentation</label>
      </div>
      {open.isError && <ErrorPanel what="Pending edits" error={open.error} onRetry={() => void open.refetch()} />}
      {!!staged.filter(c => c.key.file === file).length && <p className="notice">This section has pending edits. <a href={href('changes')}>Review Changes</a> to validate and publish them.</p>}
      {page.isPending ? <Loading /> : page.isError ? <ErrorPanel what="These rules" error={page.error} onRetry={() => void page.refetch()} /> :
        !rows.length ? <EmptyState title={q ? 'No rule matches' : 'This file has no rules'} /> : (
          <ul className="rule-list" aria-busy={page.isFetching}>
            {rows.map((row) => {
              const change = stagedFor(row.path);
              return (
                <li key={JSON.stringify(row.path)}>
                  <span className="mono rule-path" title={JSON.stringify(row.path)}>{pathText(row.path)}</span>
                  <div className="rule-value-group"><Badge>{typeof row.path[0] === 'string' && row.path[0].startsWith('_') ? 'Documentation' : valueType(row.value)}</Badge><span className="mono rule-value" title={JSON.stringify(row.value)}>{shown(row.value)}</span>
                    {change && <StagedBadge>{change.operation === 'hide' ? 'Staged removal' : `Staged → ${shown((change.after as { value?: unknown } | null)?.value)}`}</StagedBadge>}
                  </div>
                  <span className="row-actions">
                    <Button size="sm" disabled={!open.data || busy || page.isPlaceholderData || change?.operation === 'hide'} onClick={() => setEditing(row)}>Edit</Button>
                    {change ? <Button size="sm" variant="ghost" disabled={busy} onClick={() => changeActions.remove.mutate(change.id)}>Undo</Button>
                      : <Button size="sm" variant="ghost" disabled={!open.data || busy || page.isPlaceholderData} onClick={() => setRemoving(row)}>Remove</Button>}
                  </span>
                </li>
              );
            })}
          </ul>
        )}
      {total > RULES_PAGE && (
        <div className="pager">
          <span className="dim">{num(Math.min(total, offset + 1))}–{num(Math.min(total, offset + rows.length))} of {num(total)}</span>
          <span className="row-actions">
            <Button size="sm" disabled={offset === 0} onClick={() => go(file, q, Math.max(0, offset - RULES_PAGE))}>Previous</Button>
            <Button size="sm" disabled={offset + RULES_PAGE >= total} onClick={() => go(file, q, offset + RULES_PAGE)}>Next</Button>
          </span>
        </div>
      )}
      {changeActions.remove.isError && <p className="error-box" role="alert">{changeActions.remove.error.message}</p>}
      {!!additions.length && <div className="rule-additions"><h3>Pending additions</h3><ul>{additions.map(change => <li key={change.id}><span className="mono">{pathText(change.key.path as RulePath)}</span><StagedBadge>{shown((change.after as { value: unknown }).value)}</StagedBadge><Button size="sm" disabled={busy} onClick={() => setEditing({ path: change.key.path as RulePath, value: (change.after as { value: unknown }).value })}>Edit</Button><Button size="sm" disabled={busy} onClick={() => changeActions.remove.mutate(change.id)}>Undo</Button></li>)}</ul></div>}
      {editing && <EditDialog file={file} row={editing} pending={stagedFor(editing.path)} onClose={() => setEditing(null)} />}
      <ConfirmDialog open={!!removing} onOpenChange={(openNow) => { if (!openNow) setRemoving(null); }} title="Remove this rule?"
        text={removing ? `${pathText(removing.path)} is removed immediately. The server refuses invalid rules.` : ''}
        confirmLabel="Remove rule" variant="staged"
        onConfirm={() => removing && actions.stage.mutateAsync({ file, path: removing.path, operation: 'hide' }).then(() => toast('Rule removed.', 'staged'))} />
    </Section>
  );
}

function EditDialog({ file, row, pending, onClose }: { file: string; row: RuleRow; pending: ContentChange | undefined; onClose: () => void }) {
  const toast = useToast();
  const actions = useRuleActions();
  const [baseline] = useState({ live: pending?.operation === 'insert' ? null : { value: row.value }, pending: changedRule(pending) });
  const [initial] = useState(pending?.after ? (pending.after as { value: unknown }).value : row.value);
  const [json, setJson] = useState(initial === null || typeof initial === 'object');
  const [text, setText] = useState(typeof initial === 'string' ? initial : JSON.stringify(initial, null, 2) ?? 'null');
  const [error, setError] = useState<string | null>(null);
  const save = () => {
    const parsed = !json && typeof initial === 'string' ? { ok: true as const, value: text } : parseJson(text);
    if (!parsed.ok) { setError('That is not valid JSON.'); return; }
    setError(null);
    actions.stage.mutate({ file, path: row.path, operation: pending?.operation === 'insert' ? 'insert' : 'update', value: parsed.value, baseline }, {
      onSuccess: () => { toast('Rule saved.', 'done'); onClose(); },
      onError: (caught) => setError(caught.message),
    });
  };
  return (
    <Dialog open onOpenChange={(openNow) => { if (!openNow && !actions.stage.isPending) onClose(); }} wide title={`${fileTitle(file)} › ${pathText(row.path)}`}
      description="The server checks this value before saving it immediately."
      footer={<><Button variant="ghost" disabled={actions.stage.isPending} onClick={onClose}>Cancel</Button><Button variant="staged" onClick={save} disabled={actions.stage.isPending}>Save changes</Button></>}>
      {pending && <p className="notice">{pending.operation === 'insert' ? 'New rule, waiting to publish.' : <>Editing your pending value. Published value: <span className="mono">{shown(row.value)}</span></>}</p>}
      <fieldset className="rule-editor-fields" disabled={actions.stage.isPending}>
      {!json && typeof initial === 'boolean' ? <Field label="Value" error={error}><select value={text} onChange={e => setText(e.target.value)}><option value="true">Enabled</option><option value="false">Disabled</option></select></Field>
        : !json && typeof initial === 'number' ? <Field label="Value" error={error}><input type="number" step="any" value={text} onChange={e => setText(e.target.value)} /></Field>
        : !json && typeof initial === 'string' ? <Field label="Value" hint="Text, without JSON quotes." error={error}><textarea rows={Math.min(10, Math.max(3, text.split('\n').length))} value={text} onChange={e => setText(e.target.value)} /></Field>
        : <Field label="Value" error={error} hint='JSON: a number, true/false, "text", a list, an object or null.'><textarea className="code" rows={10} spellCheck={false} value={text} onChange={e => setText(e.target.value)} /></Field>}
      <div className="row-actions">{!json ? <Button size="sm" onClick={() => { if (typeof initial === 'string') setText(JSON.stringify(text)); setJson(true); setError(null); }}>Edit as JSON</Button>
        : <Button size="sm" onClick={() => { const parsed = parseJson(text); if (parsed.ok) { setText(JSON.stringify(parsed.value, null, 2)); setError(null); } else setError('That is not valid JSON.'); }}>Format JSON</Button>}
        <Badge>{json ? 'JSON' : valueType(initial)}</Badge></div>
      </fieldset>
    </Dialog>
  );
}

function AnyLocation({ file }: { file: string }) {
  const toast = useToast();
  const actions = useRuleActions();
  const [path, setPath] = useState('');
  const [operation, setOperation] = useState<'update' | 'insert' | 'hide'>('update');
  const [value, setValue] = useState('');
  const [error, setError] = useState<string | null>(null);
  const stage = () => {
    setError(null);
    const parsedPath = parsePath(path);
    if (!parsedPath) { setError('The path is a JSON list of keys (text) and positions (whole numbers), for example ["priceBounds", 2].'); return; }
    let parsedValue: unknown = null;
    if (operation !== 'hide') {
      const parsed = parseJson(value);
      if (!parsed.ok) { setError('The value is not valid JSON.'); return; }
      parsedValue = parsed.value;
    }
    actions.stage.mutate({ file, path: parsedPath, operation, value: parsedValue }, {
      onSuccess: () => { toast('Rule saved.', 'done'); setPath(''); setValue(''); },
      onError: (caught) => setError(caught.message),
    });
  };
  return (
    <Section title="Edit any location" text="Add a key, append to a list (its position is the list's length), replace a whole list or object, or remove one.">
      <div className="stack">
        <Field label="Path (JSON list)"><input className="mono" spellCheck={false} placeholder='["priceBounds", "maximumByRating", 2, "maximum"]' value={path} onChange={(event) => setPath(event.target.value)} /></Field>
        <Field label="Change">
          <select value={operation} onChange={(event) => setOperation(event.target.value as typeof operation)}>
            <option value="update">Change the value</option>
            <option value="insert">Add (a new key, or list position = length)</option>
            <option value="hide">Remove</option>
          </select>
        </Field>
        {operation !== 'hide' && <Field label="Value (JSON)"><textarea className="code" rows={4} spellCheck={false} placeholder="50" value={value} onChange={(event) => setValue(event.target.value)} /></Field>}
        {error && <p className="error" role="alert">{error}</p>}
        <div><Button variant="staged" onClick={stage} disabled={actions.stage.isPending}>Save changes</Button></div>
      </div>
    </Section>
  );
}

function ImportDialog({ file, onClose }: { file: string; onClose: () => void }) {
  const toast = useToast();
  const actions = useRuleActions();
  const pending = useChangeset();
  const affected = (pending.data?.changeset?.changes ?? []).filter(c => c.area === 'rules');
  const blocked = affected.some(c => c.key.file === file);
  const [error, setError] = useState<string | null>(null);
  const run = (only: string | null) => actions.importFiles.mutate(only, {
    onSuccess: (reply) => {
      const staged = Number(reply.staged) || 0;
      toast(staged ? `Staged ${num(staged)} difference${staged === 1 ? '' : 's'} from the files: review them under Changes.` : 'The database already matches the files.', staged ? 'staged' : 'done');
      onClose();
      if (staged) window.location.hash = href('changes');
    },
    onError: (caught) => setError(caught.message),
  });
  return (
    <Dialog open onOpenChange={(openNow) => { if (!openNow && !actions.importFiles.isPending) onClose(); }} title="Import from the rules files?"
      description="Compares the bundled rules/*.json files with the database and stages every difference as a pending change. Nothing reaches the game until you publish it; values changed only here in the panel show up as differences too."
      footer={<>
        <Button variant="ghost" disabled={actions.importFiles.isPending} onClick={onClose}>Cancel</Button>
        <Button onClick={() => run(file)} disabled={actions.importFiles.isPending || !pending.data || blocked}>Only {file}</Button>
        <Button variant="staged" onClick={() => run(null)} disabled={actions.importFiles.isPending || !pending.data || !!affected.length}>All files</Button>
      </>}>
      {pending.isError && <ErrorPanel what="Pending changes" error={pending.error} onRetry={() => void pending.refetch()} />}
      {!!affected.length && <p className="notice">There are pending rule edits. <a href={href('changes')}>Review Changes</a> before importing files with those edits.</p>}
      {error && <p className="error" role="alert">{error}</p>}
    </Dialog>
  );
}
