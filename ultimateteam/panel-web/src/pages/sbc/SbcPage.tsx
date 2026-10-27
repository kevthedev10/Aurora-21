import { Field } from '../../ui/Field';
import { AvailabilityDialog, ScheduledSection } from '../schedule/AvailabilitySections';
// Squad Building Challenges, like Packs / Picks: the SBCs tab is the catalogue (content area sbc_set; add a set, set its
// requirements, rewards, game tab and badge, edit any set), and Live SBCs is the list of sets the game shows (sbc_live,
// migration 141), each switched on or off. Settings is rules/sbc.json (SbcSettingsTab). Changes are staged and published
// under Changes; the set editor is SbcEditor. Address: #/sbc, #/sbc/live, #/sbc/settings, #/sbc/new[/<key to copy>],
// #/sbc/<setKey>.
import { useEffect, useMemo, useState } from 'react';
import { useChangeset } from '../../api/content';
import { badgeUrl, useSbc, useSbcActions, useSbcLiveActions, type SbcLiveEntry, type SbcReply, type SbcSetDef, type SbcSetRow } from '../../api/sbc';
import { href } from '../../app/router';
import { Badge, StagedBadge } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section } from '../../ui/Layout';
import { Tabs } from '../../ui/Tabs';
import { useToast } from '../../ui/Toast';
import { SbcEditor } from './SbcEditor';
import { SbcSettingsTab } from './SbcSettingsTab';
import '../packs/listing.css';
import './sbc.css';

const go = (path?: string) => { window.location.hash = path ? href('sbc', path) : href('sbc'); };
type Changes = { area: string; key: Record<string, unknown>; operation: string; after?: unknown }[] | undefined;

/** The definition staged for a set, if any, else null. */
function staged(changes: Changes, key: string) {
  const change = changes?.find((entry) => entry.area === 'sbc_set' && entry.key.setKey === key);
  if (!change) return null;
  return { operation: change.operation, definition: (change.after as { definition?: SbcSetDef } | null)?.definition ?? null };
}

/** The live list staged under Changes, if any. */
const stagedLive = (changes: Changes) =>
  (changes?.find((entry) => entry.area === 'sbc_live')?.after as { listing?: SbcLiveEntry[] } | null | undefined)?.listing ?? null;

/** Sets staged as new that are not published yet: they can be listed in the same publish. */
const stagedNew = (data: SbcReply, changes: Changes) => (changes ?? []).filter((change) => change.area === 'sbc_set' && change.operation === 'insert'
  && !data.sets.some((set) => set.key === change.key.setKey)).map((change) => ({ key: String(change.key.setKey), definition: (change.after as { definition?: SbcSetDef } | null)?.definition }));

export function SbcPage({ rest }: { rest: string[] }) {
  const sbc = useSbc();
  const changeset = useChangeset();
  const changes = changeset.data?.changeset?.changes;
  if (sbc.isPending) return <><PageHeader title="SBCs" /><Loading /></>;
  if (sbc.isError) return <><PageHeader title="SBCs" /><ErrorPanel what="The SBC list" error={sbc.error} onRetry={() => void sbc.refetch()} /></>;
  const data = sbc.data;
  if (rest[0] === 'new') return <SbcEditor key={rest[1] ?? 'new'} data={data} existing={null} pending={null} copy={data.sets.find((set) => set.key === rest[1])} staged={stagedNew(data, changes)} />;
  if (rest[0] && rest[0] !== 'live' && rest[0] !== 'settings') {
    const row = data.sets.find((set) => set.key === rest[0]);
    if (!row) return <><PageHeader title="SBCs" /><EmptyState title="SBC not found" text="It may have been staged as new; publish it first." action={<a href={href('sbc')}>All SBCs</a>} /></>;
    return <SbcEditor key={row.key} data={data} existing={row} pending={staged(changes, row.key)} staged={stagedNew(data, changes)} />;
  }
  const tab = rest[0] === 'live' ? 'live' : rest[0] === 'settings' ? 'settings' : 'catalogue';
  return (
    <>
      <PageHeader title="SBCs" description="Every Squad Building Challenge, and the Live SBCs list of the ones the game shows. A set is in the game only when it is on Live SBCs and switched on. Save definitions immediately; use Live SBCs or scheduled drops for availability."
        actions={<LinkButton variant="primary" href={href('sbc', 'new')}>New SBC</LinkButton>} />
      <Tabs label="SBCs" value={tab} onChange={(next) => go(next === 'catalogue' ? undefined : next)}
        items={[{ value: 'catalogue', label: 'SBCs', count: data.sets.length }, { value: 'live', label: 'Live SBCs', count: data.live.filter((entry) => entry.enabled).length },
          { value: 'settings', label: 'Settings' }]}>
        {tab === 'live' ? <LiveTab data={data} changes={changes} /> : tab === 'settings' ? <SbcSettingsTab data={data} changes={changes} /> : <SbcList data={data} changes={changes} />}
      </Tabs>
    </>
  );
}

/** Rows grouped as FIFA's SBC screen groups them: one section per game tab, in tab order, then any unknown tab. */
function byTab<T>(data: SbcReply, rows: T[], tabOf: (row: T) => number) {
  const groups = data.tabs.map((tab) => ({ id: tab.id, name: tab.name, rows: rows.filter((row) => tabOf(row) === tab.id) }));
  const known = new Set(data.tabs.map((tab) => tab.id));
  for (const row of rows) {
    const id = tabOf(row);
    if (known.has(id)) continue;
    const group = groups.find((entry) => entry.id === id) ?? groups[groups.push({ id, name: `Tab ${id}`, rows: [] }) - 1]!;
    group.rows.push(row);
  }
  return groups.filter((group) => group.rows.length > 0);
}

function SbcList({ data, changes }: { data: SbcReply; changes: Changes }) {
  const [tab, setTab] = useState<number | 'all'>('all');
  const [query, setQuery] = useState('');
  const actions = useSbcActions();
  const toast = useToast();
  const newOnes = stagedNew(data, changes);
  const shown = data.sets.filter((set) => (tab === 'all' || set.definition.tabId === tab)
    && `${set.key} ${set.definition.name}`.toLowerCase().includes(query.toLowerCase()));
  const remove = (row: SbcSetRow) => actions.mutate({ key: row.key, operation: 'hide' }, {
    onSuccess: () => toast(`${row.definition.name} ${row.origin === 'panel' ? 'removal' : 'revert'} saved.`, 'done'),
    onError: (error) => toast(error.message, 'error'),
  });
  return (
    <>
      <div className="sbc-toolbar">
        <Field label="Find an SBC"><input type="search" placeholder="Name or key…" value={query} onChange={(event) => setQuery(event.target.value)} /></Field>
        <div className="sbc-tabs" role="group" aria-label="Game SBC tab">
          <button type="button" aria-pressed={tab === 'all'} onClick={() => setTab('all')}>All</button>
          {data.tabs.map((entry) => <button key={entry.id} type="button" aria-pressed={tab === entry.id} onClick={() => setTab(entry.id)}>{entry.name}</button>)}
        </div>
      </div>
      {newOnes.length > 0 && <p className="sbc-note"><StagedBadge>New</StagedBadge> {newOnes.map((entry) => entry.definition?.name || entry.key).join(', ')} staged; they appear here once published. Add them to Live SBCs to show them in the game.</p>}
      {!shown.length ? <EmptyState title="No SBCs" text="Nothing matches this tab or search." /> : byTab(data, shown, (row) => (staged(changes, row.key)?.definition ?? row.definition).tabId).map((group) => (
        <section key={group.id} className="sbc-group" aria-label={group.name}>
          <h3 className="sbc-group-head">
            <img src={badgeUrl(`tab-${group.id}`)} alt="" />
            <span>{group.name}</span>
            <small className="dim">{group.rows.filter((row) => row.listed && row.enabled).length} live of {group.rows.length}</small>
          </h3>
          <ul className="sbc-grid">
            {group.rows.map((row) => {
              const pending = staged(changes, row.key);
              const def = pending?.definition ?? row.definition;
              const live = row.listed && row.enabled;
              return (
                <li key={row.key} className={live ? 'sbc-card' : 'sbc-card sbc-card-hidden'}>
                  <img className="sbc-card-badge" src={badgeUrl(def.badge || row.key)} alt="" onError={(event) => { event.currentTarget.src = badgeUrl(`tab-${def.tabId}`); }} />
                  <div className="sbc-card-body">
                    <strong title={row.key}>{def.name || row.key}</strong>
                    <small>{def.challenges.length} challenge{def.challenges.length === 1 ? '' : 's'}{def.repeatableDaily ? ' · daily' : def.repeatable ? ' · repeatable' : ''}</small>
                    <span className="sbc-card-tags">
                      {pending && <StagedBadge>{pending.operation === 'hide' ? (row.origin === 'panel' ? 'Removal staged' : 'Revert staged') : 'Staged'}</StagedBadge>}
                      {live ? <Badge tone="pitch">Live</Badge> : row.listed ? <Badge tone="red" title="On Live SBCs but switched off">Off</Badge>
                        : <Badge title="Not on Live SBCs">Not live</Badge>}
                      {row.origin === 'panel' ? <Badge>Added here</Badge> : row.edited ? <Badge>Edited</Badge> : null}
                    </span>
                  </div>
                  <div className="sbc-card-actions">
                    <LinkButton size="sm" href={href('sbc', row.key)}>Edit</LinkButton>
                    <LinkButton size="sm" variant="ghost" href={href('sbc', 'new', row.key)}>Duplicate</LinkButton>
                    {row.origin === 'panel' && <Button size="sm" variant="danger" disabled={actions.isPending || row.listed}
                      title={row.listed ? 'Take it off Live SBCs first' : undefined} onClick={() => remove(row)}>Remove</Button>}
                    {row.origin === 'bundled' && row.edited && <Button size="sm" variant="ghost" disabled={actions.isPending}
                      title="Go back to the bundled sbc.json definition" onClick={() => remove(row)}>Revert</Button>}
                  </div>
                </li>
              );
            })}
          </ul>
        </section>
      ))}
    </>
  );
}

const sameListing = (a: SbcLiveEntry[], b: SbcLiveEntry[]) => {
  const sorted = (list: SbcLiveEntry[]) => JSON.stringify([...list].sort((x, y) => x.setKey.localeCompare(y.setKey, 'en')));
  return sorted(a) === sorted(b);
};

function LiveTab({ data, changes }: { data: SbcReply; changes: Changes }) {
  const toast = useToast();
  const actions = useSbcLiveActions();
  const pending = stagedLive(changes);
  const start = pending ?? data.live;
  const startKey = JSON.stringify(start);
  const [listing, setListing] = useState<SbcLiveEntry[]>(start);
  const [adding, setAdding] = useState(false);
  useEffect(() => { setListing(JSON.parse(startKey) as SbcLiveEntry[]); }, [startKey]);
  const catalogue = useMemo(() => [
    ...data.sets.map((row) => ({ key: row.key, definition: staged(changes, row.key)?.definition ?? row.definition, isNew: false })),
    ...stagedNew(data, changes).filter((entry) => entry.definition).map((entry) => ({ key: entry.key, definition: entry.definition!, isNew: true })),
  ], [data, changes]);
  const order = new Map(catalogue.map((entry, index) => [entry.key, index]));
  const byKey = new Map(catalogue.map((entry) => [entry.key, entry]));
  const listed = new Set(listing.filter(entry => entry.enabled).map((entry) => entry.setKey));
  const available = catalogue.filter((entry) => !listed.has(entry.key));
  const sorted = listing.filter(entry => entry.enabled).sort((a, b) => (order.get(a.setKey) ?? 1e9) - (order.get(b.setKey) ?? 1e9));
  const dirty = !sameListing(listing, start);
  const stage = () => actions.mutate(listing, {
    onSuccess: () => toast('Live SBCs saved.', 'done'),
    onError: (error) => toast(error.message, 'error'),
  });
  return (
    <><Section title="Live SBCs" text="The SBCs the game shows. New releases require start and end dates; inactive SBCs stay in the catalogue with their definitions. Clubs keep their progress either way."
      actions={<>
        {pending && <StagedBadge>Live SBCs are staged: changes here update them</StagedBadge>}
        <Button size="sm" onClick={() => setAdding(true)} disabled={!available.length} aria-label="Add an SBC to Live SBCs">+ Add SBC</Button>
      </>}>
      {!sorted.length ? <EmptyState title="No SBCs listed" text="Add an SBC to show it in the game." /> : byTab(data, sorted, (entry) => byKey.get(entry.setKey)?.definition.tabId ?? -1).map((group) => (
        <section key={group.id} className="sbc-group" aria-label={group.name}>
          <h3 className="sbc-group-head">
            <img src={badgeUrl(`tab-${group.id}`)} alt="" />
            <span>{group.name}</span>
            <small className="dim">{group.rows.filter((entry) => entry.enabled).length} on of {group.rows.length}</small>
          </h3>
          <ul className="listing-rows">
            {group.rows.map((entry) => {
              const set = byKey.get(entry.setKey);
              const name = set?.definition.name || entry.setKey;
              const count = set?.definition.challenges.length ?? 0;
              return (
                <li key={entry.setKey} className="listing-row sbc-live-row">
                  <img className="sbc-live-badge" src={badgeUrl(set?.definition.badge || entry.setKey)} alt=""
                    onError={(event) => { event.currentTarget.src = badgeUrl(`tab-${set?.definition.tabId ?? 0}`); }} />
                  <div className="listing-name">
                    <strong title={entry.setKey}>{name}</strong>
                    <span className="dim listing-detail">{set ? `${count} challenge${count === 1 ? '' : 's'}${set.definition.repeatableDaily ? ' · daily' : set.definition.repeatable ? ' · repeatable' : ''}` : 'Not in the catalogue'}
                      {set?.isNew ? ' · staged as new' : ''}</span>
                  </div>
                  <Badge tone="pitch">Live</Badge>
                  <Button size="sm" variant="ghost" aria-label={`Remove ${name} from Live SBCs`}
                    onClick={() => setListing((current) => current.filter((candidate) => candidate.setKey !== entry.setKey))}>Remove</Button>
                </li>
              );
            })}
          </ul>
        </section>
      ))}
      <div className="row-actions mt">
        <Button variant="staged" onClick={stage} disabled={!dirty || actions.isPending}>Save Live SBCs</Button>
        <Button variant="ghost" onClick={() => setListing(start)} disabled={!dirty}>Reset</Button>
        <span className="dim">{num(sorted.length)} listed · {num(sorted.filter((entry) => entry.enabled).length)} in the game</span>
      </div>
    </Section>
    <ScheduledSection kind="sbc" options={catalogue.map(entry => ({ value: entry.key, label: entry.definition.name || entry.key }))} />
    {adding && <AvailabilityDialog kind="sbc" options={available.map(entry => ({ value: entry.key, label: entry.definition.name || entry.key }))} onClose={() => setAdding(false)} />}
    </>
  );
}
