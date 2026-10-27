// Create or edit a pack or pick (plan 5.3). There is one kind of pack: a new one is unassigned until the Store lineup sells it
// or a reward names it. A pack stays a pack and a pick stays a pick. Every pack has a complete odds table (pack odds v3);
// a view-only pack (its own draw rules) can only change how it is named, sold and shown. Save stages the fields that
// change; Test contents draws one pack from the edited values without changing anything.
import { useState, type FormEvent } from 'react';
import { useAssets } from '../../api/assets';
import { ApiError } from '../../api/client';
import { useCatalogue, usageOf, usePackActions, USAGE_LABEL, type Catalogue, type CatalogueEntry, type DrawnItem } from '../../api/packs';
import { href } from '../../app/router';
import { Button, LinkButton } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { CardRow, FutCard } from '../../ui/FutCard';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section } from '../../ui/Layout';
import { SwitchRow } from '../../ui/Switch';
import { useToast } from '../../ui/Toast';
import { PickGroupsEditor, type PickGroup } from './PickGroupsEditor';
import { artLabel } from './CatalogueTab';
import { PromoSourceFields, oddsCategories, oddsIssue, usePromoAvailability, type DrawSettings } from './PromoSourceFields';
import { DEFAULT_MIXED, MixedContentsEditor, validateMixedContents, type MixedContents } from './MixedContentsEditor';
import { DEFAULT_VANITY, VanityContentsEditor, cleanVanityPack, validateVanityPack, type VanityPack } from './VanityContentsEditor';

type Kind = 'text' | 'group' | 'integer' | 'chance' | 'boolean';
type Mode = 'players' | 'picks' | 'mixed' | 'vanity';
interface Spec { key: string; label: string; kind: Kind; hint?: string }

/** The pack fields the server accepts (OperatorPanel.PackCatalog.cs ReadPackFields), grouped for the form. */
const GROUPS: { title: string; fields: Spec[] }[] = [
  { title: 'Details', fields: [
    { key: 'name', label: 'Name', kind: 'text' },
    { key: 'bio', label: 'Description', kind: 'text' },
    { key: 'store_group', label: 'Pack art', kind: 'group', hint: 'The tile art in the store and in My Packs.' },
    { key: 'purchase_limit', label: 'Purchase limit per club', kind: 'integer', hint: '0 means unlimited. Counts successful store purchases across restarts; gifts do not count.' },
    { key: 'community_limit', label: 'Community limit', kind: 'integer', hint: 'Shared across all clubs. 0 means unlimited. Setting this clears the per-club limit. Counts purchases since this feature was added; gifts do not count.' },
  ] },
  { title: 'Contents', fields: [
    { key: 'size', label: 'Items per pack', kind: 'integer' },
    { key: 'rare_slots', label: 'Rare slots', kind: 'integer' },
    { key: 'gold_weight', label: 'Gold weight', kind: 'integer' },
    { key: 'silver_weight', label: 'Silver weight', kind: 'integer' },
    { key: 'bronze_weight', label: 'Bronze weight', kind: 'integer' },
    { key: 'min_rating', label: 'Minimum rating', kind: 'integer', hint: 'Empty for none' },
    { key: 'max_rating', label: 'Maximum rating', kind: 'integer', hint: 'Empty for none' },
    { key: 'guaranteed_specials', label: 'Guaranteed specials', kind: 'integer' },
    { key: 'manager_chance', label: 'Manager chance', kind: 'chance', hint: '0 to 1' },
    { key: 'chem_style_chance', label: 'Chemistry style chance', kind: 'chance', hint: '0 to 1' },
  ] },
  { title: 'Flags', fields: [
    { key: 'promo_only', label: 'Specials only', kind: 'boolean', hint: 'Every player slot must be a special card.' },
    { key: 'admin_only', label: 'Grant only', kind: 'boolean', hint: 'Never sold: it cannot be added to the Store lineup. Rewards and panel grants can still give it.' },
  ] },
];
const SPECS = GROUPS.flatMap((group) => group.fields);
/** What a view-only pack may still change (OperatorPanel.PackCatalog.cs ViewOnlyEditableFields): naming, selling and art. */
const VIEW_ONLY_EDITABLE = new Set(['name', 'bio', 'store_group', 'purchase_limit', 'community_limit', 'admin_only']);
const VIEW_ONLY_LOCKED = new Set(SPECS.map((spec) => spec.key).filter((key) => !VIEW_ONLY_EDITABLE.has(key)));
const DEFAULTS: Record<string, unknown> = { name: 'New Pack', bio: '', store_group: 'special', size: 12,
  gold_weight: 100, silver_weight: 0, bronze_weight: 0, rare_slots: 1 };

const asText = (value: unknown) => (value === undefined || value === null ? '' : String(value));

/** Form values to the fields to send: for an edit only what differs from the published values; for a new pack every set field. */
export function packFields(values: Record<string, string | boolean>, original: Record<string, unknown>, isNew: boolean,
  omit: ReadonlySet<string> = new Set()): Record<string, unknown> | string {
  const out: Record<string, unknown> = {};
  for (const spec of SPECS.filter((candidate) => !omit.has(candidate.key))) {
    const raw = values[spec.key];
    let value: unknown;
    if (spec.kind === 'boolean') value = raw === true;
    else if (spec.kind === 'text' || spec.kind === 'group') value = String(raw ?? '');
    else {
      const text = String(raw ?? '').trim();
      if (!text) continue;
      const number = Number(text);
      if (!Number.isFinite(number) || number < 0 || (spec.kind === 'integer' && !Number.isInteger(number)) || (spec.kind === 'chance' && number > 1)) {
        return `${spec.label}: ${spec.kind === 'chance' ? 'a number from 0 to 1' : 'a whole number of 0 or more'}.`;
      }
      value = number;
    }
    // A missing text field reads as empty and a missing flag as off, so an untouched field never counts as a change.
    const before = spec.kind === 'boolean' ? original[spec.key] === true : spec.kind === 'text' || spec.kind === 'group' ? asText(original[spec.key]) : original[spec.key];
    if (isNew || JSON.stringify(value) !== JSON.stringify(before)) out[spec.key] = value;
  }
  if (Number(out.purchase_limit ?? original.purchase_limit ?? 0) > 0 && Number(out.community_limit ?? original.community_limit ?? 0) > 0)
    return 'Choose either a per-club purchase limit or a community limit, not both.';
  if (Number(out.community_limit ?? 0) > 999999) return 'Community limit: use 999999 or less.';
  return out;
}

const listHref = (picks: boolean) => `${href('packs', 'catalogue')}${picks ? '?type=picks' : ''}`;

export function PackEditor({ packId, picksOnly = false }: { packId: number | null; picksOnly?: boolean }) {
  const catalogue = useCatalogue();
  const crumbs = <a href={href('packs', 'catalogue')}>Packs</a>;
  if (catalogue.isPending) return <><PageHeader crumbs={crumbs} title="Loading…" /><Loading /></>;
  if (catalogue.isError || !catalogue.data) return <><PageHeader crumbs={crumbs} title="Pack" /><ErrorPanel what="The pack catalogue" error={catalogue.error} onRetry={() => void catalogue.refetch()} /></>;
  const pack = packId === null ? null : catalogue.data.packs.find((candidate) => candidate.id === packId);
  if (packId !== null && !pack) return <><PageHeader crumbs={crumbs} title={`Pack ${packId}`} /><EmptyState title="No such pack" action={<LinkButton href={href('packs', 'catalogue')}>Back to the list</LinkButton>} /></>;
  const defaults = picksOnly ? { ...DEFAULTS, name: 'New Player Pick', size: 1, rare_slots: 1,
    player_picks: [{ count: 1, choice_count: 3, min_rating: 82 }] } : DEFAULTS;
  return <Form key={`${picksOnly}-${packId ?? 'new'}`} picksOnly={picksOnly} packId={packId} pack={pack ?? null} original={pack?.config ?? defaults} catalogue={catalogue.data} />;
}

/** "Used in Store + Rewards: rivals_rewards.promotion.1…" for an existing pack. */
function Usage({ pack }: { pack: CatalogueEntry }) {
  const usage = usageOf(pack);
  return <p className="dim">Used in: <b>{USAGE_LABEL[usage]}</b>{pack.usedBy?.length ? ` · named by ${pack.usedBy.slice(0, 3).join(', ')}${pack.usedBy.length > 3 ? ` and ${pack.usedBy.length - 3} more` : ''}` : ''}
    {usage === 'unassigned' ? ' · switch it on in the Store lineup to sell it, or name it in a reward.' : ''}</p>;
}

function Form({ packId, pack, original, catalogue, picksOnly }: { packId: number | null; pack: CatalogueEntry | null; original: Record<string, unknown>; catalogue: Catalogue; picksOnly: boolean }) {
  const { groups, revision } = catalogue;
  const toast = useToast();
  const assets = useAssets().data;
  const actions = usePackActions();
  const viewOnly = pack?.viewOnlyReason ?? null;
  const [values, setValues] = useState<Record<string, string | boolean>>(() =>
    Object.fromEntries(SPECS.map((spec) => [spec.key, spec.kind === 'boolean' ? original[spec.key] === true : asText(original[spec.key])])));
  const [error, setError] = useState<string | null>(null);
  const [drawn, setDrawn] = useState<DrawnItem[] | null>(null);
  const originalGroups: PickGroup[] = Array.isArray(original.player_picks) ? original.player_picks as PickGroup[]
    : Number(original.player_pick_count ?? 0) > 0 ? [{ count: 1, choice_count: Number(original.player_pick_count), min_rating: Number(original.min_rating ?? 0),
      max_rating: Number(original.max_rating ?? 99), promo_only: original.promo_only === true }] : [];
  const originalMixed = original.mixed_contents as MixedContents | undefined;
  const originalVanity = original.vanity_pack as VanityPack | undefined;
  const isVanity = (originalVanity?.slots?.length ?? 0) > 0;
  const [mode, setMode] = useState<Mode>(isVanity ? 'vanity' : originalMixed?.tier ? 'mixed' : originalGroups.length ? 'picks' : 'players');
  const [mixed, setMixed] = useState<MixedContents>(originalMixed?.tier ? originalMixed : DEFAULT_MIXED);
  const [vanity, setVanity] = useState<VanityPack>(isVanity ? originalVanity! : DEFAULT_VANITY);
  const vanityTypes = catalogue.vanityTypes;
  const [pickGroups, setPickGroups] = useState<PickGroup[]>(originalGroups);
  const [bonusCount, setBonusCount] = useState(Number(original.bonus_player_count ?? 0));
  const [bonusRating, setBonusRating] = useState(Number(original.bonus_min_rating ?? 0));
  const [bonusSpecials, setBonusSpecials] = useState(original.bonus_promo_only === true);
  const originalSource: DrawSettings = { include_general_pool: original.include_general_pool !== false,
    promo_codes: (original.promo_codes as string[] | undefined) ?? [], category_chances: (original.category_chances as Record<string, number> | undefined) ?? {} };
  const [source, setSource] = useState<DrawSettings>(originalSource);
  const specialRequirement = values.promo_only === true ? 'all' : Number(values.guaranteed_specials) > 0 ? 'minimum' : 'none';
  const set = (key: string, value: string | boolean) => setValues((current) => ({ ...current, [key]: value,
    ...(key === 'purchase_limit' && Number(value) > 0 ? { community_limit: '0' } : {}),
    ...(key === 'community_limit' && Number(value) > 0 ? { purchase_limit: '0' } : {}),
  }));
  const isNew = packId === null;
  // A definition is a pack or a pick from its creation, matching the list filter it is found under.
  const pickDefinition = isNew ? picksOnly : originalGroups.length > 0;
  // Upgraded specials only drop in silver and bronze slots: the row appears once the pack has any.
  const lowerTiers = mode === 'mixed' ? mixed.tier !== 'gold' : Number(values.silver_weight) > 0 || Number(values.bronze_weight) > 0;
  const upgraded = mode !== 'picks' && lowerTiers;
  const template = mode === 'picks' || pickDefinition
    ? { totw: catalogue.oddsTemplate.pickTotw, icon: catalogue.oddsTemplate.icon }
    : { icon: catalogue.oddsTemplate.icon };
  const { active } = usePromoAvailability();
  const oddsProblem = (settings: DrawSettings, withUpgraded: boolean, specialsOnly: boolean) =>
    oddsIssue(settings, oddsCategories(settings, catalogue.promos, active, withUpgraded), specialsOnly);
  const anyOddsProblem = () => viewOnly || mode === 'vanity' ? null : oddsProblem(source, upgraded, mode === 'players' && specialRequirement === 'all')
    ?? (mode === 'picks' ? pickGroups.map(group => oddsProblem({ ...source, ...group }, false, group.promo_only === true)).find(Boolean) ?? null : null);
  const fieldsFor = (all: boolean): Record<string, unknown> | string => {
    const oddsError = anyOddsProblem();
    if (oddsError) return oddsError;
    const fields = packFields(values, original, all, viewOnly ? VIEW_ONLY_LOCKED : undefined);
    if (typeof fields === 'string' || viewOnly) return fields;
    const put = (key: string, value: unknown, before: unknown = original[key]) => {
      if (all || JSON.stringify(value) !== JSON.stringify(before)) fields[key] = value;
    };
    for (const key of ['include_general_pool', 'promo_codes', 'category_chances'] as const) put(key, source[key], originalSource[key]);
    // Players and picks never replace a player with a legacy manager/consumable slot.
    // Mixed packs use their explicit item ranges instead of these legacy chances.
    put('manager_chance', 0, original.manager_chance ?? 0);
    put('chem_style_chance', 0, original.chem_style_chance ?? 0);
    if (mode === 'players' && specialRequirement === 'all') {
      put('rare_slots', Number(values.size));
      put('guaranteed_specials', 0);
    } else if (mode === 'players' && Number(values.guaranteed_specials) > Number(values.rare_slots)) {
      return 'Guaranteed special players cannot exceed the rare slots.';
    }
    if (mode === 'mixed') {
      const issue = validateMixedContents(mixed, Number(values.size), Number(values.rare_slots));
      if (issue) return issue;
      put('mixed_contents', mixed);
      for (const tier of ['gold', 'silver', 'bronze']) put(`${tier}_weight`, tier === mixed.tier ? 100 : 0);
      put('guaranteed_specials', 0, original.guaranteed_specials ?? 0);
      put('promo_only', false, original.promo_only === true);
    } else if (originalMixed?.tier) put('mixed_contents', {});
    if (mode === 'vanity') {
      const issue = validateVanityPack(vanity, vanityTypes);
      if (issue) return issue;
      const spec = cleanVanityPack(vanity);
      // One item per slot; the player-pack fields stay as they are and are not used.
      put('vanity_pack', spec, isVanity ? cleanVanityPack(originalVanity!) : undefined);
      put('size', spec.slots.length);
    } else if (isVanity) put('vanity_pack', {});
    if (mode === 'picks') {
      if (!pickGroups.length) return 'Add at least one pick group.';
      if (!Number.isInteger(bonusCount) || bonusCount < 0 || !Number.isInteger(bonusRating) || bonusRating < 0 || bonusRating > 99) return 'Bonus count and rating must be valid whole numbers.';
      put('player_picks', pickGroups, Array.isArray(original.player_picks) ? original.player_picks : []);
      if (Number(original.player_pick_count ?? 0) > 0) put('player_pick_count', 0);
      put('size', pickGroups.reduce((total, group) => total + (group.count ?? 1), bonusCount));
      put('rare_slots', 0); put('guaranteed_specials', 0);
      put('bonus_player_count', bonusCount, original.bonus_player_count ?? 0);
      put('bonus_min_rating', bonusRating, original.bonus_min_rating ?? 0);
      put('bonus_promo_only', bonusSpecials, original.bonus_promo_only === true);
    }
    return fields;
  };

  const save = (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    const fields = fieldsFor(isNew);
    if (typeof fields === 'string') { setError(fields); return; }
    if (!Object.keys(fields).length) { setError('Nothing changed.'); return; }
    actions.stage.mutate({ id: packId, revision, pack: fields }, {
      onSuccess: () => {
        toast(isNew ? `New ${mode === 'picks' ? 'pick' : 'pack'} saved.` : 'Pack saved.', 'done');
        window.location.hash = listHref(mode === 'picks');
      },
      onError: (caught) => setError(caught instanceof ApiError && caught.status === 409 ? `${caught.message} Your values are kept; reload the page to see the other change.` : caught.message),
    });
  };
  const test = () => {
    setError(null);
    const fields = fieldsFor(true);
    if (typeof fields === 'string') { setError(fields); return; }
    actions.test.mutate({ packId, pack: fields }, { onSuccess: (reply) => setDrawn(reply.items ?? []), onError: (caught) => setError(caught.message) });
  };
  const shownGroups = GROUPS.filter((group) => !viewOnly || group.title !== 'Contents');

  return (
    <>
      <PageHeader crumbs={<a href={listHref(pickDefinition)}>Packs</a>} title={isNew ? picksOnly ? 'Create player pick' : 'Create pack' : `Edit ${pack?.description ?? `pack ${packId}`}`}
        description="Save the definition immediately. Control store availability through the store listing or a scheduled drop." />
      <form onSubmit={save} className="stack">
        {isNew ? <p className="dim">A new {picksOnly ? 'pick' : 'pack'} is unassigned. Set its price and tradeability when adding it to either store lineup, or name it in a reward.</p>
          : pack && <Usage pack={pack} />}
        {viewOnly && <p className="notice" role="status">View only: {viewOnly} You can still change its name, art and limits, and sell or award it.</p>}
        {!viewOnly && <Section title="Pack type">
          <Field label="Contents type"><select value={mode} onChange={(event) => {
            setMode(event.target.value as Mode);
          }}>{pickDefinition ? <option value="picks">Player picks</option> : <><option value="players">Players</option><option value="mixed">Mixed items</option>
            {vanityTypes.length > 0 && <option value="vanity">My Stadium items</option>}</>}</select></Field>
          {pack?.unavailableReason && <p className="notice" role="status">Published definition cannot be enabled yet: {pack.unavailableReason} Test contents checks your edited values.</p>}
        </Section>}
        {shownGroups.map((group) => (
          (mode === 'picks' || mode === 'vanity') && group.title === 'Contents' ? null :
          <Section key={group.title} title={group.title}>
            {group.title === 'Flags' ? group.fields.filter((spec) => !['promo_only', 'enabled', 'store_listed'].includes(spec.key)).map((spec) => (
              <SwitchRow key={spec.key} label={spec.label} text={spec.hint} checked={values[spec.key] === true} onChange={(checked) => set(spec.key, checked)} />
            )) : (
              <div className="form-row">
                {group.fields.filter((spec) => !['manager_chance', 'chem_style_chance', 'guaranteed_specials'].includes(spec.key)
                  && (spec.key !== 'rare_slots' || specialRequirement !== 'all' || mode !== 'players')
                  && (mode !== 'mixed' || group.title !== 'Contents' || ['size', 'rare_slots'].includes(spec.key))).map((spec) => (
                  <Field key={spec.key} label={spec.label} hint={spec.hint}>
                    {spec.kind === 'group' ? (
                      <select value={String(values[spec.key])} onChange={(event) => set(spec.key, event.target.value)}>
                        {!values[spec.key] && <option value="">Automatic</option>}
                        {groups.map((option) => <option key={option} value={option}>{artLabel(option)}</option>)}
                      </select>
                    ) : (
                      <input value={String(values[spec.key])} inputMode={spec.kind === 'text' ? undefined : 'decimal'} onChange={(event) => set(spec.key, event.target.value)} />
                    )}
                  </Field>
                ))}
              </div>
            )}
          </Section>
        ))}
        {!viewOnly && mode === 'mixed' && <Section title="Mixed item ranges"><MixedContentsEditor value={mixed} onChange={setMixed} /></Section>}
        {!viewOnly && mode === 'vanity' && <Section title="My Stadium items"><VanityContentsEditor value={vanity} types={vanityTypes} onChange={setVanity} /></Section>}
        {!viewOnly && mode === 'players' && <Section title="Special players" text="The odds table sets the chance of each special category. Choose a requirement here only when specials must be guaranteed.">
          <div className="form-row">
            <Field label="Special player requirement"><select value={specialRequirement} onChange={(event) => {
              const next = event.target.value;
              setValues(current => ({ ...current, promo_only: next === 'all',
                guaranteed_specials: next === 'minimum' ? String(Math.max(1, Number(current.guaranteed_specials) || 0)) : '0' }));
            }}>
              <option value="none">No guaranteed specials</option>
              <option value="minimum">Guarantee a number</option>
              <option value="all">All players special</option>
            </select></Field>
            {specialRequirement === 'minimum' && <Field label="Guaranteed special players" hint="Must fit within the rare slots.">
              <input type="number" min="1" max={Number(values.rare_slots)} value={String(values.guaranteed_specials)} onChange={event => set('guaranteed_specials', event.target.value)} />
            </Field>}
          </div>
          {specialRequirement === 'all' && <p className="dim">All {String(values.size)} player slots are special. Opening fails if the eligible special pool cannot fill them.</p>}
        </Section>}
        {!viewOnly && mode !== 'vanity' && <Section title="Promo sources and odds"><PromoSourceFields value={source} onChange={setSource} promos={catalogue.promos}
          upgraded={upgraded} specialsOnly={mode === 'players' && specialRequirement === 'all'} template={template} /></Section>}
        {!viewOnly && mode === 'picks' && <>
          <Section title="Player picks" text="Opening the pack grants tokens. Each token offers one player from its saved choices.">
            <PickGroupsEditor groups={pickGroups} onChange={setPickGroups} catalogue={catalogue} source={source} />
            <p>This pack contains {pickGroups.reduce((total, group) => total + (group.count ?? 1), 0)} pick tokens and {bonusCount} bonus players.</p>
          </Section>
          <Section title="Bonus players">
            <div className="form-row">
              <Field label="Bonus player count"><input type="number" min="0" max="100" value={bonusCount} onChange={(event) => setBonusCount(Number(event.target.value))} /></Field>
              <Field label="Bonus minimum rating"><input type="number" min="0" max="99" value={bonusRating} onChange={(event) => setBonusRating(Number(event.target.value))} /></Field>
            </div>
            <SwitchRow label="Bonus specials only" checked={bonusSpecials} onChange={setBonusSpecials} />
          </Section>
        </>}
        {error && <p className="error-box" role="alert">{error}</p>}
        <div className="row-actions">
          <Button variant="staged" type="submit" disabled={actions.stage.isPending || !!anyOddsProblem()}>Save changes</Button>
          {!viewOnly && <Button onClick={test} disabled={actions.test.isPending || !!anyOddsProblem()}>{actions.test.isPending ? 'Drawing…' : 'Test contents'}</Button>}
          <LinkButton variant="ghost" href={listHref(pickDefinition)}>Cancel</LinkButton>
        </div>
      </form>
      {drawn && (
        <Section title="Test draw" text={mode === 'vanity' ? `${drawn.length} items drawn for a club that owns none of them; nothing changed.` : mode === 'picks' ? 'Sample choices for each pick, followed by any bonus players; nothing changed.' : `${drawn.length} items drawn from these values; nothing changed.`}>
          <DrawnCards items={drawn} assets={assets} />
        </Section>
      )}
    </>
  );
}

export function DrawnCards({ items, assets }: { items: DrawnItem[]; assets: ReturnType<typeof useAssets>['data'] }) {
  if (items.some((item) => item.pickIndex !== undefined)) {
    const picks = [...new Set(items.flatMap((item) => item.pickIndex === undefined ? [] : [item.pickIndex]))];
    const bonus = items.filter((item) => item.pickIndex === undefined);
    return <div className="stack">{picks.map((index) => <div key={index}>
      <h3>Pick {index + 1}: choose 1 of {items.filter((item) => item.pickIndex === index).length}</h3>
      <PlayerCards items={items.filter((item) => item.pickIndex === index)} assets={assets} />
    </div>)}{bonus.length > 0 && <div><h3>Bonus players</h3><PlayerCards items={bonus} assets={assets} /></div>}</div>;
  }
  return <PlayerCards items={items} assets={assets} />;
}

function PlayerCards({ items, assets }: { items: DrawnItem[]; assets: ReturnType<typeof useAssets>['data'] }) {
  if (assets && items.every((item) => item.art)) {
    return <CardRow>{items.map((item, index) => <FutCard key={index} art={item.art} assets={assets} title={`${item.name} ${item.rating}`} />)}</CardRow>;
  }
  return <ol className="drawn-list">{items.map((item, index) => <li key={index}>{item.name ?? 'Item'} <span className="dim">{item.rating ?? ''}</span></li>)}</ol>;
}
