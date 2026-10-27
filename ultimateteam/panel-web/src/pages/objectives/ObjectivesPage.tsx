import { AvailabilityDialog, ScheduledSection } from '../schedule/AvailabilitySections';
// Objectives and the Season Pass, like SBCs and the store: the Objectives tab is the catalogue (every objective group, gift and
// the welcome group), Live objectives the ordered list of the groups the game shows, each switched on or off, Season Pass the
// default season and the custom seasons (SeasonPass.tsx). Everything is rules/objectives.json, staged and published under Changes.
// Address: #/objectives, #/objectives/live, #/objectives/season,
// #/objectives/group/<id>, #/objectives/new/<normal|gift>.
import { useEffect, useState } from 'react';
import { useChangeset } from '../../api/content';
import { catalogueGroups, liveListOf, stagedObjectiveChanges, useObjectiveActions, useObjectives, withStaged, type ObjectiveLiveEntry, type ObjectiveRulesDoc, type ObjectivesReply } from '../../api/objectives';
import { href } from '../../app/router';
import { Badge, StagedBadge } from '../../ui/Badge';
import { Button, LinkButton } from '../../ui/Button';
import { num } from '../../ui/format';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section } from '../../ui/Layout';
import { Tabs } from '../../ui/Tabs';
import { useToast } from '../../ui/Toast';
import { GroupEditor, type GroupKind } from './GroupEditor';
import { SeasonPassTab } from './SeasonPass';
import '../modes/modes.css';
import '../packs/listing.css';
import './objectives.css';

const TABS = ['catalogue', 'live', 'season'] as const;
type Tab = (typeof TABS)[number];
const LABELS: Record<Tab, string> = { catalogue: 'Objectives', live: 'Live objectives', season: 'Season Pass' };
const KIND_LABEL: Record<GroupKind, string> = { welcome: 'Welcome', normal: 'Objective group', gift: 'Gift' };

export function ObjectivesPage({ rest }: { rest: string[] }) {
  const objectives = useObjectives();
  const changeset = useChangeset();
  if (objectives.isPending) return <><PageHeader title="Objectives" /><Loading /></>;
  if (objectives.isError) return <><PageHeader title="Objectives" /><ErrorPanel what="The objectives" error={objectives.error} onRetry={() => void objectives.refetch()} /></>;
  const reply = objectives.data;
  const staged = stagedObjectiveChanges(changeset.data?.changeset?.changes, reply.settingsFile);
  const doc = withStaged(reply.rules, staged);
  if (rest[0] === 'new' && (rest[1] === 'normal' || rest[1] === 'gift')) return <GroupEditor key={`new-${rest[1]}`} reply={reply} doc={doc} groupId={null} newKind={rest[1]} />;
  if (rest[0] === 'group') {
    const id = Number(rest[1]);
    if (!catalogueGroups(doc).some((group) => group.id === id)) return <><PageHeader title="Objectives" /><EmptyState title="Group not found" action={<a href={href('objectives')}>All objectives</a>} /></>;
    return <GroupEditor key={id} reply={reply} doc={doc} groupId={id} newKind={null} />;
  }
  const tab: Tab = (TABS as readonly string[]).includes(rest[0] ?? '') ? rest[0] as Tab : 'catalogue';
  const live = liveListOf(doc);
  return (
    <>
      <PageHeader title="Objectives" description="Objective groups and gifts, the Live objectives the game shows, and the Season Pass. A group is in the game only while it is on Live objectives and switched on. Save definitions immediately; use Live objectives or scheduled drops for availability."
        actions={<><LinkButton href={href('objectives', 'new', 'gift')}>New gift</LinkButton><LinkButton variant="primary" href={href('objectives', 'new', 'normal')}>New objective group</LinkButton></>} />
      {staged.length > 0 && <p className="notice">Objectives have {staged.length} staged edit{staged.length === 1 ? '' : 's'}. <a href={href('changes')}>Review Changes</a> to publish them.</p>}
      <Tabs label="Objectives" value={tab} onChange={(next) => { window.location.hash = next === 'catalogue' ? href('objectives') : href('objectives', next); }}
        items={TABS.map((value) => ({ value, label: LABELS[value], ...(value === 'live' && { count: live.filter((entry) => entry.enabled).length }) }))}>
        {tab === 'live' ? <LiveTab reply={reply} doc={doc} />
          : tab === 'season' ? <SeasonPassTab reply={reply} doc={doc} staged={staged} />
          : <Catalogue reply={reply} doc={doc} />}
      </Tabs>
    </>
  );
}

function Catalogue({ reply, doc }: { reply: ObjectivesReply; doc: ObjectiveRulesDoc }) {
  const actions = useObjectiveActions(reply.settingsFile);
  const toast = useToast();
  const live = new Map(liveListOf(doc).map((entry) => [entry.group, entry.enabled]));
  const started = new Map(reply.started.map((entry) => [entry.group, entry.at]));
  const published = new Map(catalogueGroups(reply.rules).map((group) => [group.id, JSON.stringify(groupOf(reply.rules, group.id))]));
  const remove = (id: number, kind: GroupKind) => {
    const promise = kind === 'gift'
      ? actions.mutateAsync({ path: ['gifts'], value: (doc.gifts ?? []).filter((gift) => gift.id !== id), shown: doc.gifts ?? [] })
      : actions.mutateAsync({ path: ['grindGroups'], value: doc.grindGroups.filter((group) => group.id !== id), shown: doc.grindGroups });
    promise.then(() => toast('Removal saved. Clubs keep what they claimed; the id is never reused.', 'staged'), (error: unknown) => toast(error instanceof Error ? error.message : String(error), 'error'));
  };
  const sections: [GroupKind, string, string][] = [
    ['welcome', 'Welcome', 'The group every new club sees first.'],
    ['normal', 'Objective groups', 'Objectives clubs complete by playing, with a reward for each and one for the whole group.'],
    ['gift', 'Gifts', 'One-off gifts, claimable once while their window is open.'],
  ];
  return <>
    {sections.map(([kind, title, text]) => {
      const groups = catalogueGroups(doc).filter((group) => group.kind === kind);
      return (
        <section key={kind} className="objective-section" aria-label={title}>
          <h3 className="objective-section-head">{title}<small className="dim">{text}</small></h3>
          {!groups.length ? <p className="dim">None yet.</p> : (
            <ul className="objective-grid">
              {groups.map((group) => {
                const listed = live.has(group.id);
                const on = live.get(group.id) === true;
                const isStaged = published.get(group.id) !== JSON.stringify(groupOf(doc, group.id));
                const count = kind === 'normal' ? doc.grindGroups.find((entry) => entry.id === group.id)?.objectives.length ?? 0 : 1;
                const since = started.get(group.id);
                return (
                  <li key={group.id} className={on ? 'objective-card' : 'objective-card objective-card-off'}>
                    <div className="objective-card-body">
                      <strong>{group.title}</strong>
                      <small className="dim">{group.subTitle}</small>
                      <small className="dim">{count} objective{count === 1 ? '' : 's'}{since ? ` · counting since ${new Date(since).toLocaleDateString('en-GB')}` : ''}</small>
                      <span className="objective-tags">
                        {isStaged && <StagedBadge />}
                        {on ? <Badge tone="pitch">Live</Badge> : listed ? <Badge tone="red">Off</Badge> : <Badge>Not live</Badge>}
                        <Badge>{KIND_LABEL[kind]}</Badge>
                      </span>
                    </div>
                    <div className="objective-card-actions">
                      <LinkButton size="sm" href={href('objectives', 'group', group.id)}>Edit</LinkButton>
                      {kind !== 'welcome' && <Button size="sm" variant="danger" disabled={listed || actions.isPending}
                        title={listed ? 'Take it off Live objectives first' : undefined} onClick={() => remove(group.id, kind)}>Remove</Button>}
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </section>
      );
    })}
  </>;
}

const groupOf = (doc: ObjectiveRulesDoc, id: number) => doc.welcomeGroup.id === id ? doc.welcomeGroup
  : doc.grindGroups.find((group) => group.id === id) ?? (doc.gifts ?? []).find((gift) => gift.id === id);

function LiveTab({ reply, doc }: { reply: ObjectivesReply; doc: ObjectiveRulesDoc }) {
  const actions = useObjectiveActions(reply.settingsFile);
  const toast = useToast();
  const start = liveListOf(doc);
  const startKey = JSON.stringify(start);
  const [listing, setListing] = useState<ObjectiveLiveEntry[]>(start);
  const [adding, setAdding] = useState(false);
  useEffect(() => { setListing(JSON.parse(startKey) as ObjectiveLiveEntry[]); }, [startKey]);
  const groups = new Map(catalogueGroups(doc).map((group) => [group.id, group]));
  const available = catalogueGroups(doc).filter((group) => !listing.some((entry) => entry.group === group.id && entry.enabled));
  const dirty = JSON.stringify(listing) !== startKey;
  const move = (index: number, by: number) => setListing((current) => {
    const next = [...current];
    const [moved] = next.splice(index, 1);
    next.splice(index + by, 0, moved!);
    return next;
  });
  const stage = () => actions.mutate({ path: ['live'], value: listing, shown: doc.live ?? null }, {
    onSuccess: () => toast('Live objectives saved. A group listed for the first time counts from now.', 'staged'),
    onError: (error) => toast(error.message, 'error'),
  });
  return (
    <><Section title="Live objectives" text="The groups the game shows, in this order. New releases require start and end dates (a gift also needs its own window open). A group's objectives count from when it first went live."
      actions={<Button size="sm" onClick={() => setAdding(true)} disabled={!available.length} aria-label="Add a group to Live objectives">+ Add group</Button>}>
      {!listing.some(entry => entry.enabled) ? <EmptyState title="No objectives listed" text="Add a group to show it in the game." /> : (
        <ol className="listing-rows">
          {listing.map((entry, index) => {
            if (!entry.enabled) return null;
            const group = groups.get(entry.group);
            const name = group?.title ?? `Group ${entry.group}`;
            return (
              <li key={entry.group} className="listing-row objective-live-row">
                <span className="objective-live-order">{index + 1}</span>
                <div className="listing-name">
                  <strong>{name}</strong>
                  <span className="dim listing-detail">{group ? `${KIND_LABEL[group.kind]} · ${group.subTitle}` : 'Not in the catalogue'}</span>
                </div>
                <div className="row-actions">
                  <Button size="sm" variant="ghost" disabled={index === 0} aria-label={`Move ${name} up`} onClick={() => move(index, -1)}>↑</Button>
                  <Button size="sm" variant="ghost" disabled={index === listing.length - 1} aria-label={`Move ${name} down`} onClick={() => move(index, 1)}>↓</Button>
                </div>
                <Badge tone="pitch">Live</Badge>
                <Button size="sm" variant="ghost" aria-label={`Remove ${name} from Live objectives`}
                  onClick={() => setListing((current) => current.filter((candidate) => candidate.group !== entry.group))}>Remove</Button>
              </li>
            );
          })}
        </ol>
      )}
      <div className="row-actions mt">
        <Button variant="staged" onClick={stage} disabled={!dirty || actions.isPending}>Save Live objectives</Button>
        <Button variant="ghost" onClick={() => setListing(start)} disabled={!dirty}>Reset</Button>
        <span className="dim">{num(listing.length)} listed · {num(listing.filter((entry) => entry.enabled).length)} in the game</span>
      </div>
    </Section>
    <ScheduledSection kind="objective" options={catalogueGroups(doc).map(group => ({ value: String(group.id), label: group.title }))} />
    {adding && <AvailabilityDialog kind="objective" options={available.map(group => ({ value: String(group.id), label: group.title }))} onClose={() => setAdding(false)} />}
    </>
  );
}
