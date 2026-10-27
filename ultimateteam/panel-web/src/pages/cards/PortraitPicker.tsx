import { useId, useState } from 'react';
import type { CardImagesReply } from '../../api/types';

/** Native portrait radios supply keyboard navigation without a duplicate selector. */
export function PortraitPicker({ images, sourceResourceId, creating, value, ownImage, onChange }: {
  images: CardImagesReply; sourceResourceId: number; creating: boolean; value: string;
  ownImage: string | null; onChange: (value: string) => void;
}) {
  const name = useId();
  const offerBase = !creating && sourceResourceId !== images.assetId && !!images.basePhoto;
  const choices = [
    { value: 'own', label: creating ? 'Default player head' : 'This card’s own head', url: ownImage, note: 'No portrait override' },
    ...(offerBase ? [{ value: `card:${images.assetId}`, label: 'Base player head', url: images.basePhoto ?? null, note: 'Standard player photo' }] : []),
    ...images.cards.filter((head) => head.url && head.resourceId !== sourceResourceId && (!offerBase || head.resourceId !== images.assetId)).map((head) => ({
      value: `card:${head.resourceId}`, label: `${head.name ?? 'Card'} ${head.rating ?? ''} · ${head.rarity || head.resourceId}`,
      url: head.url ?? null, note: `Card ${head.resourceId}`,
    })),
    ...images.uploads.map((head) => ({ value: `upload:${head.headId}`, label: head.label, url: head.url, note: 'Uploaded portrait' })),
  ];
  if (!choices.some(choice => choice.value === value)) choices.push({ value, label: 'Selected portrait', url: null, note: 'Selected portrait is not currently available' });
  return <div className="stack">
    <fieldset className="portrait-options"><legend>Available images</legend>
      <div className="portrait-grid">{choices.map((choice) => <label className={`portrait-option${choice.value === value ? ' selected' : ''}`} key={choice.value}>
        <input type="radio" name={name} value={choice.value} checked={value === choice.value} onChange={() => onChange(choice.value)} />
        <PortraitImage key={choice.url ?? choice.value} url={choice.url} />
        <b>{choice.label}</b><small className="dim">{choice.note}</small>
      </label>)}</div>
    </fieldset>
  </div>;
}

function PortraitImage({ url }: { url: string | null }) {
  const [failed, setFailed] = useState(false);
  return url && !failed ? <img src={url} alt="" loading="lazy" decoding="async" onError={() => setFailed(true)} />
    : <span className="portrait-missing">Preview unavailable</span>;
}
