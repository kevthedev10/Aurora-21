import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { useAssets } from '../../api/assets';
import { api } from '../../api/client';
import { changesetKey, contentActionKey, useChangeset, useContentBusy, useContentStatus } from '../../api/content';
import type { CardEditorReply, CardImagesReply, ChangesetReply } from '../../api/types';
import { href } from '../../app/router';
import { Button, LinkButton } from '../../ui/Button';
import { Field } from '../../ui/Field';
import { FutCard } from '../../ui/FutCard';
import { EmptyState, ErrorPanel, Loading, PageHeader, Section } from '../../ui/Layout';
import { Badge } from '../../ui/Badge';
import { ConfirmDialog } from '../../ui/Dialog';
import { Reveal } from '../../ui/Reveal';
import { StatBar } from '../../ui/StatBar';
import { useToast } from '../../ui/Toast';
import { PortraitPicker } from './PortraitPicker';
import { PortraitUpload, type PreparedHead } from './PortraitUpload';
export { fileAsHead } from './PortraitUpload';
import { allowedCardTypes, CardTypePicker, isIconType, typeForRating } from './CardTypePicker';
import { updateStats } from './cardStats';

type CardValue = CardEditorReply['card'];
type NumericGroup = { title: string; fields: [string, string][] };

const GROUPS: NumericGroup[] = [
  { title: 'Overall and face stats', fields: [
    ['rating', 'Rating'], ['face_slot_0', 'Pace / Diving'], ['face_slot_1', 'Shooting / Handling'],
    ['face_slot_2', 'Passing / Kicking'], ['face_slot_3', 'Dribbling / Reflexes'],
    ['face_slot_4', 'Defending / Speed'], ['face_slot_5', 'Physical / Positioning'],
  ] },
  { title: 'Attacking', fields: [
    ['acceleration', 'Acceleration'], ['sprint_speed', 'Sprint speed'], ['positioning', 'Positioning'],
    ['finishing', 'Finishing'], ['shot_power', 'Shot power'], ['long_shots', 'Long shots'],
    ['volleys', 'Volleys'], ['penalties', 'Penalties'],
  ] },
  { title: 'Passing and dribbling', fields: [
    ['vision', 'Vision'], ['crossing', 'Crossing'], ['free_kick_accuracy', 'Free kick accuracy'],
    ['short_passing', 'Short passing'], ['long_passing', 'Long passing'], ['curve', 'Curve'],
    ['agility', 'Agility'], ['balance', 'Balance'], ['reactions', 'Reactions'],
    ['ball_control', 'Ball control'], ['dribbling', 'Dribbling'], ['composure', 'Composure'],
  ] },
  { title: 'Defending and physical', fields: [
    ['interceptions', 'Interceptions'], ['heading_accuracy', 'Heading accuracy'], ['marking', 'Defensive awareness'],
    ['standing_tackle', 'Standing tackle'], ['sliding_tackle', 'Sliding tackle'], ['jumping', 'Jumping'],
    ['stamina', 'Stamina'], ['strength', 'Strength'], ['aggression', 'Aggression'],
  ] },
  { title: 'Goalkeeping', fields: [
    ['gk_diving', 'Diving'], ['gk_handling', 'Handling'], ['gk_kicking', 'Kicking'],
    ['gk_reflexes', 'Reflexes'], ['gk_speed', 'Speed'], ['gk_positioning', 'Positioning'],
  ] },
];

const numberFields = GROUPS.flatMap((group) => group.fields.map(([key]) => key));
function groupsForPosition(position: unknown): NumericGroup[] {
  const groups: NumericGroup[] = position === 'GK' ? GROUPS[4]!.fields.map(([key, label]) => ({ title: label, fields: [[key, `${label} attribute`]] })) : [
    { title: 'Pace', fields: GROUPS[1]!.fields.slice(0, 2) },
    { title: 'Shooting', fields: GROUPS[1]!.fields.slice(2) },
    { title: 'Passing', fields: GROUPS[2]!.fields.slice(0, 6) },
    { title: 'Dribbling', fields: GROUPS[2]!.fields.slice(6) },
    { title: 'Defending', fields: GROUPS[3]!.fields.slice(0, 5) },
    { title: 'Physical', fields: GROUPS[3]!.fields.slice(5) },
  ];
  return groups.map((group, index) => ({ ...group, fields: [[`face_slot_${index}`, `${group.title} (card)`], ...group.fields] }));
}
const eligibleTrait = (trait: CardEditorReply['options']['traits'][number], position: unknown) =>
  trait.appliesTo === 'both' || (trait.appliesTo ?? 'outfield') === (position === 'GK' ? 'goalkeeper' : 'outfield');
const textFields = ['position', 'preferred_foot', 'work_rate_attack', 'work_rate_defense'] as const;

/** Keep the complete server value: a player_card edit must name every field, including null heads and traits. */
export function cardForStage(original: CardValue, draft: Record<string, unknown>): CardValue | string {
  const value: CardValue = structuredClone(original);
  for (const key of numberFields) {
    if (String(draft[key] ?? '') === String(original[key] ?? '')) continue;
    const text = String(draft[key] ?? '').trim();
    const number = Number(text);
    if (!text || !Number.isInteger(number) || number < 1 || number > 99) return `${key.replaceAll('_', ' ')} must be a whole number from 1 to 99.`;
    value[key] = number;
  }
  for (const key of ['weak_foot', 'skill_moves']) {
    if (String(draft[key] ?? '') === String(original[key] ?? '')) continue;
    const text = String(draft[key] ?? '').trim();
    const number = Number(text);
    if (!text || !Number.isInteger(number) || number < 1 || number > 5) return `${key.replaceAll('_', ' ')} must be a whole number from 1 to 5.`;
    value[key] = number;
  }
  for (const key of textFields) {
    if (draft[key] === original[key]) continue;
    value[key] = String(draft[key] ?? '');
  }
  for (const key of ['head_resource_id', 'custom_head_id']) {
    if (draft[key] === original[key]) continue;
    const id = Number(draft[key]);
    if (draft[key] !== null && (!Number.isSafeInteger(id) || id <= 0)) return 'A player head must be an existing portrait id or the card’s own head.';
    value[key] = draft[key] === null ? null : id;
  }
  if (!Array.isArray(draft.traits) || draft.traits.some((id) => !Number.isSafeInteger(id) || Number(id) <= 0)) return 'Traits must be existing visible trait ids.';
  value.traits = [...new Set(draft.traits as number[])];
  return value;
}

export const headChoice = (card: Record<string, unknown>) => card.custom_head_id ? `upload:${card.custom_head_id}` : card.head_resource_id ? `card:${card.head_resource_id}` : 'own';


export function CardEditor({ resourceId, creating, draftResourceId }: { resourceId: number; creating: boolean; draftResourceId?: number }) {
  const client = useQueryClient();
  const toast = useToast();
  const assets = useAssets().data;
  const [publishedId, setPublishedId] = useState<number | null>(null);
  const editorId = publishedId ?? resourceId;
  const newDefinition = creating && publishedId === null;
  const editor = useQuery({ queryKey: ['card-editor', editorId], queryFn: () => api<CardEditorReply>(`cards/editor/${editorId}`) });
  const imageKey = ['card-images', editor.data?.assetId];
  const images = useQuery({ queryKey: imageKey, queryFn: () => api<CardImagesReply>(`cards/images/${editor.data!.assetId}`), enabled: !!editor.data?.assetId });
  const changes = useChangeset();
  const status = useContentStatus();
  const busy = useContentBusy();
  // A retry or a second save updates the same new definition, rather than allocating another card.
  const allocated = useRef<number | null>(draftResourceId ?? null);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const target = creating ? allocated.current : resourceId;
  const pending = changes.data?.changeset?.status === 'published' ? undefined : changes.data?.changeset?.changes?.find((change) => change.area === 'player_card' && Number(change.key.resourceId) === target);
  const after = pending?.after as { base?: number; cardTypeId?: number; card?: CardValue } | undefined;
  const [draft, setDraft] = useState<Record<string, unknown> | null>(null);
  const [draftSource, setDraftSource] = useState<CardValue | null>(null);
  const [cardType, setCardType] = useState<number | null>(null);
  const [baseline, setBaseline] = useState('');
  const [saved, setSaved] = useState('');
  const [portraitPreview, setPortraitPreview] = useState<string | null>(null);
  const [preparingPortrait, setPreparingPortrait] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const [staged, setStaged] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [leaving, setLeaving] = useState<string | null>(null);
  const reference = JSON.stringify([editor.data?.card, editor.data?.cardTypeId, pending?.id, pending?.after]);
  const signature = (card: Record<string, unknown>, type: number | null) => JSON.stringify([card, typeForRating(editor.data?.options.cardTypes ?? [], type, Number(card.rating))?.id ?? type]);
  const reset = () => {
    if (!editor.data) return;
    const card = structuredClone(after?.card ?? editor.data.card);
    const type = after?.cardTypeId ?? editor.data.cardTypeId;
    setDraftSource(structuredClone(editor.data.card));
    setDraft(card); setCardType(type); setSaved(signature(card, type)); setBaseline(reference); setError(null); setStaged(false);
  };
  // Compare local edits to the values this form loaded, not a newer background response.
  const normalized = editor.data && draft ? cardForStage(draftSource ?? editor.data.card, draft) : null;
  const dirty = !!draft && (typeof normalized === 'string' || signature(normalized ?? draft, cardType) !== saved);
  const stale = !!baseline && baseline !== reference;
  useEffect(() => {
    if (editor.data && !editor.isError && !changes.isPending && !changes.isError && !busy
      && (!draft || (!dirty && stale && !editor.isFetching && !changes.isFetching))) reset();
  }, [editor.data, editor.isError, editor.isFetching, changes.isPending, changes.isError, changes.isFetching, busy, draft, dirty, stale, reference]);
  useEffect(() => {
    if (!dirty) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  const stage = useMutation({
    mutationKey: contentActionKey,
    mutationFn: async ({ card, type }: { card: CardValue; type: number | null }) => {
      if (creating && allocated.current === null)
        allocated.current = (await api<{ resourceId: number }>('cards/new', { method: 'POST', body: { baseResourceId: resourceId } })).resourceId;
      const reply = await api<ChangesetReply>('content/changeset/changes', {
        method: 'PUT', body: { area: 'player_card', key: { resourceId: creating ? allocated.current : resourceId }, operation: newDefinition ? 'insert' : 'update',
          after: newDefinition ? { base: resourceId, cardTypeId: type, card } : { card, cardTypeId: type } },
      });
      return { reply, card, type };
    },
    onSuccess: ({ reply, card, type }) => {
      const published = reply.changeset?.status === 'published';
      if (published) {
        const id = creating ? allocated.current! : resourceId;
        const live = { ...editor.data!, resourceId: id, card: structuredClone(card), cardTypeId: type };
        client.setQueryData(['card-editor', id], live);
        client.setQueryData(changesetKey, { ...reply, open: false, pending: 0, changeset: null });
        if (creating) setPublishedId(id);
        setDraftSource(structuredClone(card));
        setBaseline(JSON.stringify([card, type, undefined, undefined]));
        void client.invalidateQueries({ queryKey: ['cards'] });
      } else {
        client.setQueryData(changesetKey, reply);
        const stagedChange = reply.changeset?.changes?.find((change) => change.area === 'player_card' && Number(change.key.resourceId) === (creating ? allocated.current : resourceId));
        setBaseline(JSON.stringify([editor.data?.card, editor.data?.cardTypeId, stagedChange?.id, stagedChange?.after]));
      }
      setSaved(signature(card, type)); setStaged(true);
      toast(published ? 'Card saved.' : 'Card saved for review.', published ? 'done' : 'staged');
      if (creating && allocated.current && mounted.current) window.location.replace(published
        ? href('cards', allocated.current, 'edit') : `${href('cards', resourceId, 'new')}?draft=${allocated.current}`);
    },
    onError: (caught) => { setError(caught.message); void client.invalidateQueries({ queryKey: changesetKey }); },
  });
  const update = (key: string, value: unknown) => {
    if (key === 'rating' && Number(value) >= 1 && Number(value) < 65 && editor.data) {
      const previous = editor.data.options.cardTypes.find((type) => type.id === cardType);
      if (previous?.rareflag === 3) setCardType(typeForRating(editor.data.options.cardTypes, cardType, Number(value))?.id ?? cardType);
    }
    setDraft((current) => ({ ...updateStats(current ?? {}, key, value), ...(key === 'position' && Array.isArray(current?.traits) ? {
      traits: current.traits.filter((id) => editor.data?.options.traits.some((trait) => trait.id === id && eligibleTrait(trait, value))),
    } : {}) })); setStaged(false); setError(null);
  };
  const chooseHead = (choice: string) => {
    const [kind, id] = choice.split(':');
    setDraft((current) => ({ ...current, head_resource_id: kind === 'card' ? Number(id) : null, custom_head_id: kind === 'upload' ? Number(id) : null }));
    setStaged(false); setError(null);
  };
  const upload = async (encoded: PreparedHead, label: string) => {
    if (!editor.data?.assetId) return false;
    setError(null); setUploading(true);
    try {
      const result = await api<CardImagesReply['uploads'][number]>('heads', { method: 'POST', body: { assetId: editor.data.assetId, label, ...encoded } });
      // Keep the successful upload available even if refreshing the options subsequently fails.
      client.setQueryData<CardImagesReply>(imageKey, (previous) => ({ assetId: editor.data!.assetId!, basePhoto: previous?.basePhoto ?? null,
        cards: previous?.cards ?? [], uploads: [result, ...(previous?.uploads ?? []).filter((head) => head.headId !== result.headId)] }));
      chooseHead(`upload:${result.headId}`);
      toast('Portrait uploaded and selected. Save the card to use it.', 'staged');
      return true;
    } catch (caught) { setError(caught instanceof Error ? caught.message : String(caught)); return false; }
    finally { setUploading(false); }
  };
  const save = (event: FormEvent) => {
    event.preventDefault(); setError(null);
    if (!editor.data || !draft || busy || uploading || preparingPortrait) return;
    if (stale) { setError('The published or pending card changed. Reload its values before saving.'); return; }
    const card = cardForStage(editor.data.card, draft);
    if (typeof card === 'string') { setError(card); return; }
    const icon = isIconType(editor.data.options.cardTypes.find((type) => type.id === editor.data!.cardTypeId));
    if (icon && Number(card.rating) < 85) { setError('Icon overall must be at least 85.'); return; }
    const selected = typeForRating(editor.data.options.cardTypes, cardType, Number(card.rating));
    if (creating && !allowedCardTypes(editor.data.options.cardTypes, icon, Number(card.rating)).some((type) => type.id === selected?.id)) { setError('Choose an available card type for this player.'); return; }
    const allowed = { position: editor.data.options.positions, preferred_foot: editor.data.options.feet,
      work_rate_attack: editor.data.options.workRates, work_rate_defense: editor.data.options.workRates };
    for (const key of textFields) if (card[key] !== editor.data.card[key] && !allowed[key].includes(String(card[key]))) { setError(`Choose an available ${key.replaceAll('_', ' ')}.`); return; }
    if (!newDefinition && JSON.stringify(card) === JSON.stringify(editor.data.card) && selected?.id === editor.data.cardTypeId) { setError('To drop a pending edit, remove it under Changes. Otherwise change at least one field.'); return; }
    stage.mutate({ card, type: typeForRating(editor.data.options.cardTypes, cardType, Number(card.rating))?.id ?? cardType });
  };
  const navigate = (address: string) => { if (dirty) setLeaving(address); else window.location.hash = address; };
  const back = (event: React.MouseEvent<HTMLAnchorElement>) => { if (dirty) { event.preventDefault(); setLeaving(href('cards')); } };
  const crumbs = <a href={href('cards')} onClick={back}>Cards</a>;
  if (editor.isPending || changes.isPending) return <><PageHeader crumbs={crumbs} title="Loading card editor…" /><Loading /></>;
  if ((editor.isError && !editor.data) || !editor.data) return <><PageHeader crumbs={crumbs} title="Card editor" /><ErrorPanel what="The card" error={editor.error} onRetry={() => void editor.refetch()} /></>;
  if (changes.isError && !changes.data) return <><PageHeader crumbs={crumbs} title="Card editor" /><ErrorPanel what="Pending changes" error={changes.error} onRetry={() => void changes.refetch()} /></>;
  if (newDefinition && draftResourceId && (!pending || pending.operation !== 'insert' || after?.base !== resourceId)) return <>
    <PageHeader crumbs={crumbs} title="New card" /><EmptyState title="This new card is no longer pending" text="It may have been published or removed. Start another version to create a different card."
      action={<div className="row-actions"><LinkButton href={href('cards', draftResourceId)}>Look for the published card</LinkButton><LinkButton href={href('cards', resourceId, 'new')}>Start another card</LinkButton></div>} />
  </>;
  if (!draft) return <Loading />;

  const source = editor.data;
  const iconPlayer = isIconType(source.options.cardTypes.find((option) => option.id === source.cardTypeId));
  const type = typeForRating(source.options.cardTypes, cardType, Number(draft.rating));
  const faceLabels = draft.position === 'GK' ? ['DIV', 'HAN', 'KIC', 'REF', 'SPD', 'POS'] : ['PAC', 'SHO', 'PAS', 'DRI', 'DEF', 'PHY'];
  const face = [0, 1, 2, 3, 4, 5].map((index) => ({ label: faceLabels[index]!, value: Number(draft[`face_slot_${index}`]) || 0 }));
  const selectedHead = images.data?.uploads.find((head) => head.headId === Number(draft.custom_head_id));
  const previewArt = { ...source.art, resourceId: creating ? allocated.current ?? source.assetId ?? resourceId : source.resourceId,
    headResourceId: Number(draft.head_resource_id) || null,
    defaultPortraitUrl: !creating && headChoice(draft) === 'own' ? source.art?.defaultPortraitUrl ?? null : null,
    assetId: source.assetId ?? null, name: source.name ?? null, rating: Number(draft.rating), position: String(draft.position), rareflag: type?.rareflag ?? source.art?.rareflag ?? 0,
    skillMoves: Number(draft.skill_moves) || null, weakFoot: Number(draft.weak_foot) || null,
    workRateAttack: String(draft.work_rate_attack ?? ''), workRateDefense: String(draft.work_rate_defense ?? ''),
    portraitId: Number(draft.head_resource_id) || (!creating && source.card.head_resource_id === null && source.card.custom_head_id === null ? source.art?.portraitId ?? null : null),
    customHeadId: Number(draft.custom_head_id) || null };
  const ownImage = creating ? images.data?.basePhoto : source.art?.defaultPortraitUrl
    ?? images.data?.cards.find((head) => head.resourceId === resourceId)?.url
    ?? (source.card.head_resource_id === null && source.card.custom_head_id === null && source.art?.portraitId
      ? `/panel/assets/dynamic/p${source.art.portraitId}.png` : images.data?.basePhoto);
  const disabled = stage.isPending || uploading;
  const external = status.data?.reloadRequired === true;
  const minimumRating = iconPlayer ? 85 : 1;
  const stageBlocked = busy || uploading || preparingPortrait || stale || external || editor.isError || changes.isError || Number(draft.rating) < minimumRating || (!dirty && !(newDefinition && !pending && !staged));

  return <>
    <PageHeader crumbs={crumbs} title={`${newDefinition ? 'New card from' : 'Edit'} ${source.name ?? resourceId}`}
      description="Save card definitions immediately. A new card becomes available through a promo group or scheduled drop. Uploaded portraits are saved as options; save the card to select one."
      actions={<LinkButton href={href('cards')} onClick={back}>Back to cards</LinkButton>} />
    {editor.isError && <ErrorPanel what="The latest card values" error={editor.error} onRetry={() => void editor.refetch()} />}
    {changes.isError && <ErrorPanel what="The latest pending edits" error={changes.error} onRetry={() => void changes.refetch()} />}
    <Reveal show={staged && !dirty}><Section className="card-editor-staged" title="Card saved" text={pending ? "The card is pending review." : "The card definition is saved. Availability is controlled by promo groups and scheduled drops."}>
      {pending && <LinkButton variant="staged" href={href('changes')}>Review in Changes</LinkButton>}</Section></Reveal>
    {pending && <p className="notice">This card has a pending {pending.operation === 'insert' ? 'creation' : 'edit'}. Further changes in this form need saving again. <a href={href('changes')} onClick={(event) => { if (dirty) { event.preventDefault(); setLeaving(href('changes')); } }}>Review pending card</a></p>}
    {stale && <p className="notice" role="alert">Published or pending values changed while this form was open. Your typed values are kept. Reload the latest values before saving.</p>}
    {external && <p className="notice">Reload the external database changes above before saving this card.</p>}
    <div className="card-editor-layout">
      <aside className="card-editor-preview" aria-label="Card preview">
        <div className="row-actions"><b>Preview</b><Badge tone={dirty ? 'flare' : pending ? 'staged' : 'neutral'}>{dirty ? 'Unsaved changes' : pending ? 'Staged values' : 'Published values'}</Badge></div>
        {assets ? <FutCard art={previewArt} assets={assets} size="lg" portraitUrl={portraitPreview ?? selectedHead?.url ?? (Number(draft.head_resource_id) === source.assetId ? images.data?.basePhoto ?? null : null)} stats={face} /> : <p className="dim">Card art is not installed. Ratings, position and face stats are shown below.</p>}
        <dl className="facts"><div><dt>Rating / position</dt><dd>{String(draft.rating ?? '–')} · {String(draft.position ?? '–')}</dd></div>
          <div><dt>Card type</dt><dd>{type?.name ?? '–'}</dd></div>{allocated.current && <div><dt>New resource id</dt><dd className="mono">{allocated.current}</dd></div>}</dl>
        {!assets && face.map((stat) => <StatBar key={stat.label} {...stat} />)}
        <Button variant="staged" aria-label="Save previewed card" form="card-edit-form" type="submit" disabled={stageBlocked}>{stage.isPending ? 'Saving…' : newDefinition ? 'Save new card' : 'Save changes'}</Button>
        <Button size="sm" variant="ghost" disabled={disabled} onClick={() => setResetting(true)}>Reload form values</Button>
      </aside>
      <form id="card-edit-form" className="stack" onSubmit={save}>
        <fieldset disabled={disabled} className="card-editor-fields">
        <Section title="Card type" text={creating ? 'Create another definition for this player, using an existing card as the starting point.' : 'Choose the card type to save alongside this card’s attributes.'}>
          <CardTypePicker types={source.options.cardTypes} value={cardType} rating={Number(draft.rating)} assets={assets} icon={iconPlayer}
            onChange={(id) => { setCardType(id); setStaged(false); setError(null); }} />
        </Section>
        <Section title="Card identity">
          <div className="form-row">
            <Field label="Overall" error={iconPlayer && Number(draft.rating) < 85 ? 'Icons require an overall of at least 85.' : null}><input type="number" min={minimumRating} max={99} value={String(draft.rating ?? '')} onChange={(event) => update('rating', event.target.value)} /></Field>
            <Field label="Position"><select value={String(draft.position ?? '')} onChange={(event) => update('position', event.target.value)}>{source.options.positions.map((item) => <option key={item}>{item}</option>)}</select></Field>
            <Field label="Preferred foot"><select value={String(draft.preferred_foot ?? '')} onChange={(event) => update('preferred_foot', event.target.value)}>{!draft.preferred_foot && <option value="">Not set</option>}{source.options.feet.map((item) => <option key={item}>{item}</option>)}</select></Field>
            <Field label="Weak foot"><input type="number" min={1} max={5} value={String(draft.weak_foot ?? '')} onChange={(event) => update('weak_foot', event.target.value)} /></Field>
            <Field label="Skill moves"><input type="number" min={1} max={5} value={String(draft.skill_moves ?? '')} onChange={(event) => update('skill_moves', event.target.value)} /></Field>
            <Field label="Attacking work rate"><select value={String(draft.work_rate_attack ?? '')} onChange={(event) => update('work_rate_attack', event.target.value)}>{!draft.work_rate_attack && <option value="">Not set</option>}{source.options.workRates.map((item) => <option key={item}>{item}</option>)}</select></Field>
            <Field label="Defensive work rate"><select value={String(draft.work_rate_defense ?? '')} onChange={(event) => update('work_rate_defense', event.target.value)}>{!draft.work_rate_defense && <option value="">Not set</option>}{source.options.workRates.map((item) => <option key={item}>{item}</option>)}</select></Field>
          </div>
        </Section>
        <p className="dim">Detailed attributes recalculate their card stat. Editing a card stat shifts its contributing attributes within 1–99. Reactions and composure do not affect dribbling.</p>
        {groupsForPosition(draft.position).map((group, index) => <AttributeSection key={group.title} title={group.title} initiallyOpen
          score={<div className="face-stat-score"><Field label={`${group.title} (card)`}><input type="number" min={1} max={99}
            value={String(draft[`face_slot_${index}`] ?? '')} onChange={(event) => update(`face_slot_${index}`, event.target.value)} /></Field></div>}>
          <div className="form-row">{group.fields.slice(1).map(([key, fieldLabel]) => <Field key={key} label={fieldLabel} hint={draft[key] == null ? 'Not set; leaving it blank preserves it.' : undefined}>
            <input type="number" min={1} max={99} value={String(draft[key] ?? '')} onChange={(event) => update(key, event.target.value)} /></Field>)}</div>
        </AttributeSection>)}
        <Section title="Traits" text="Traits match the selected position; shared traits are available to both. Hidden CPU traits stay unchanged.">
          {source.options.traits.length ? <div className="card-editor-traits">{source.options.traits.filter((trait) => eligibleTrait(trait, draft.position)).map((trait) => <label key={trait.id}>
            <input type="checkbox" checked={Array.isArray(draft.traits) && draft.traits.includes(trait.id)} onChange={(event) => {
              const current = Array.isArray(draft.traits) ? draft.traits as number[] : [];
              update('traits', event.target.checked ? [...current, trait.id] : current.filter((id) => id !== trait.id));
            }} /> {trait.name}</label>)}</div> : <p className="dim">No visible traits are offered by this server.</p>}
        </Section>
        <Section title="Card Image" text="Choose an existing image of this player, or upload another.">
          {images.isError && <ErrorPanel what="Head options" error={images.error} onRetry={() => void images.refetch()} />}
          {images.isPending ? <Loading lines={2} /> : images.data && <PortraitPicker images={images.data} sourceResourceId={resourceId} creating={creating} value={headChoice(draft)} ownImage={ownImage ?? null} onChange={chooseHead} />}
          <PortraitUpload uploading={uploading} onUpload={upload} onPreview={setPortraitPreview} onPreparing={setPreparingPortrait} />
          {selectedHead && <p className="dim">Uploaded head selected: {selectedHead.label}. Save the card to apply it.</p>}
        </Section>
        </fieldset>
        <Reveal show={error !== null}>{error && <p className="error-box" role="alert">{error}</p>}</Reveal>
        <div className="row-actions"><Button variant="staged" type="submit" disabled={stageBlocked}>{stage.isPending ? 'Saving…' : newDefinition ? 'Save new card' : 'Save changes'}</Button>
          <Button variant="ghost" disabled={disabled} onClick={() => navigate(href('cards'))}>Cancel</Button></div>
      </form>
    </div>
    <ConfirmDialog open={resetting} onOpenChange={setResetting} title="Reload this form’s values?" text="Your unsaved changes are replaced by the latest pending values, or published values if no edit is pending. This does not remove a staged edit."
      confirmLabel="Reload form values" onConfirm={reset} />
    <ConfirmDialog open={leaving !== null} onOpenChange={(open) => { if (!open) setLeaving(null); }} title="Leave without saving?" text="Your unsaved form values will be lost. Saved changes are kept."
      confirmLabel="Leave card editor" onConfirm={() => { if (leaving) window.location.hash = leaving; }} />
  </>;
}

function AttributeSection({ title, initiallyOpen, children, score }: { title: string; initiallyOpen: boolean; children: ReactNode; score: ReactNode }) {
  const [open, setOpen] = useState(initiallyOpen);
  const id = useId();
  return <Section title={title} actions={<div className="stat-section-actions">{score}<Button size="sm" variant="ghost" aria-expanded={open} aria-controls={id} aria-label={`${open ? 'Collapse' : 'Expand'} ${title}`} onClick={() => setOpen(!open)}>{open ? 'Collapse' : 'Expand'}</Button></div>}>
    <Reveal show={open} id={id}>{children}</Reveal>
  </Section>;
}
