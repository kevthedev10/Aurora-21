import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import { Button } from '../../ui/Button';
import { Field } from '../../ui/Field';
import styles from './PortraitUpload.module.css';

export type PreparedHead = { png: string; rgba: string };
type Framing = { zoom: number; x: number; y: number; fade: boolean };
const defaultFraming: Framing = { zoom: 100, x: 0, y: 0, fade: false };

export function fileAsHead(file: File, framing: Framing = defaultFraming): Promise<{ png: string; rgba: string }> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    const reader = new FileReader();
    image.onload = () => {
      try {
        const canvas = document.createElement('canvas');
        canvas.width = canvas.height = 180;
        const context = canvas.getContext('2d');
        if (!context) throw new Error('The browser could not prepare this image.');
        if (!image.naturalWidth || !image.naturalHeight) throw new Error('The image has no readable dimensions.');
        if (image.naturalWidth * image.naturalHeight > 40_000_000) throw new Error('Choose an image smaller than 40 megapixels.');
        const source = document.createElement('canvas');
        const sourceScale = Math.min(1, 2048 / Math.max(image.naturalWidth, image.naturalHeight));
        source.width = Math.max(1, Math.round(image.naturalWidth * sourceScale));
        source.height = Math.max(1, Math.round(image.naturalHeight * sourceScale));
        const sourceContext = source.getContext('2d');
        if (!sourceContext) throw new Error('The browser could not prepare this image.');
        sourceContext.drawImage(image, 0, 0, source.width, source.height);
        const sourcePixels = sourceContext.getImageData(0, 0, source.width, source.height).data;
        let left = source.width, top = source.height, right = -1, bottom = -1;
        for (let y = 0; y < source.height; y++) for (let x = 0; x < source.width; x++) {
          if ((sourcePixels[(y * source.width + x) * 4 + 3] ?? 0) <= 8) continue;
          left = Math.min(left, x); right = Math.max(right, x); top = Math.min(top, y); bottom = Math.max(bottom, y);
        }
        if (right < left) { left = top = 0; right = source.width - 1; bottom = source.height - 1; }
        const visibleWidth = right - left + 1, visibleHeight = bottom - top + 1;
        // PromoFit v4's visible-subject fit: 162 × 170, centre x=90, bottom y=165.
        const scale = Math.min(162 / visibleWidth, 170 / visibleHeight) * framing.zoom / 100;
        const width = visibleWidth * scale, height = visibleHeight * scale;
        context.imageSmoothingEnabled = true; context.imageSmoothingQuality = 'high';
        context.drawImage(source, left, top, visibleWidth, visibleHeight, 90 - width / 2 + framing.x, 165 - height + framing.y, width, height);
        const pixels = context.getImageData(0, 0, 180, 180).data;
        if (framing.fade) {
          for (let y = 135; y < 180; y++) {
            const t = (y - 135) / 44, alpha = 1 - t * t * (3 - 2 * t);
            for (let x = 0; x < 180; x++) pixels[(y * 180 + x) * 4 + 3] = Math.round(pixels[(y * 180 + x) * 4 + 3]! * alpha);
          }
          context.putImageData(new ImageData(pixels, 180, 180), 0, 0);
        }
        let binary = '';
        for (let offset = 0; offset < pixels.length; offset += 8192)
          binary += String.fromCharCode(...pixels.subarray(offset, offset + 8192));
        const png = canvas.toDataURL('image/png').split(',', 2)[1];
        if (!png) throw new Error('The browser could not encode this image.');
        resolve({ png, rgba: btoa(binary) });
      } catch (error) { reject(error); }
    };
    image.onerror = () => reject(new Error('The image could not be read.'));
    reader.onerror = () => reject(new Error('The image file could not be read.'));
    reader.onload = () => { if (typeof reader.result === 'string') image.src = reader.result; else reject(new Error('The image file could not be read.')); };
    reader.readAsDataURL(file);
  });
}


export function PortraitUpload({ uploading, onUpload, onPreview, onPreparing }: {
  uploading: boolean;
  onUpload: (head: PreparedHead, label: string) => Promise<boolean>;
  onPreview: (url: string | null) => void;
  onPreparing: (preparing: boolean) => void;
}) {
  const [file, setFile] = useState<File | null>(null);
  const [label, setLabel] = useState('');
  const [framing, setFraming] = useState(defaultFraming);
  const [ready, setReady] = useState<{ head: PreparedHead; url: string; framing: Framing } | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    onPreparing(!!file); onPreview(null); setReady(null); setError(null);
    if (!file) return;
    let cancelled = false;
    const timer = setTimeout(() => {
      void (async () => {
        try {
          const head = await fileAsHead(file, framing);
          const reply = await api<{ png: string }>('heads/preview', { method: 'POST', body: { rgba: head.rgba } });
          if (cancelled) return;
          const url = `data:image/png;base64,${reply.png}`;
          setReady({ head, url, framing }); onPreview(url);
        } catch (caught) { if (!cancelled) setError(caught instanceof Error ? caught.message : String(caught)); }
      })();
    }, 200);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [file, framing, onPreview, onPreparing]);
  const current = ready?.framing === framing ? ready : null;
  const clear = () => { setFile(null); onPreview(null); onPreparing(false); };
  return <div className={styles.editor}>
    <div className="form-row">
      <Field label="Upload label"><input value={label} maxLength={80} onChange={(event) => setLabel(event.target.value)} /></Field>
      <Field label="Upload a portrait" hint="Use a transparent PNG cutout. Preview and adjust it before applying."><input type="file" accept="image/png,image/webp,image/jpeg" onChange={(event) => {
        const chosen = event.target.files?.[0]; event.target.value = '';
        if (!chosen) return;
        if (chosen.type && !chosen.type.startsWith('image/')) { setError('Choose an image file.'); return; }
        setFraming(defaultFraming); setFile(chosen);
      }} /></Field>
    </div>
    {file && <div className={styles.workspace}>
      <div className={styles.texture}>
        {current ? <img src={current.url} width={180} height={180} alt="Decoded FIFA 21 portrait texture" /> : error ? <p>Preview unavailable</p> : <p role="status">Preparing game texture…</p>}
        <span>Game texture preview · 180 × 180 DXT5</span>
      </div>
      <div className={styles.controls}>
        <p className="dim">The card preview uses these same compressed pixels. Its layout is an approximation of FIFA’s rendering.</p>
        {([['zoom', 'Portrait size', 50, 200], ['x', 'Horizontal position', -90, 90], ['y', 'Vertical position', -90, 90]] as const).map(([key, title, min, max]) =>
          <Field key={key} label={`${title}: ${framing[key]}${key === 'zoom' ? '%' : ' px'}`}><input aria-label={title} type="range" min={min} max={max} value={framing[key]} onChange={(event) => setFraming((value) => ({ ...value, [key]: Number(event.target.value) }))} /></Field>)}
        <label className={styles.fade}><input type="checkbox" checked={framing.fade} onChange={(event) => setFraming((value) => ({ ...value, fade: event.target.checked }))} /> Fade lower edge</label>
        <div className="row-actions">
          <Button variant="primary" disabled={!current || uploading} onClick={() => { if (current) void onUpload(current.head, label.trim() || file.name.slice(0, 80)).then((saved) => { if (saved) clear(); }); }}>{uploading ? 'Uploading…' : 'Use portrait'}</Button>
          <Button variant="ghost" disabled={uploading} onClick={clear}>Discard portrait</Button>
          <Button size="sm" variant="ghost" disabled={uploading} onClick={() => setFraming({ ...defaultFraming })}>Reset framing</Button>
        </div>
      </div>
    </div>}
    {error && <p className="error-box" role="alert">{error}</p>}
  </div>;
}
