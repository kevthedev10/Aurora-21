// FIFA 21 cards drawn from the downloaded images (the classic panel's js/core/fut-card.js, ported). Every image falls
// back: a missing frame draws a shade in the card type's colours, a missing special image shows the base player, a
// missing badge or flag is left out. Without any images FutCard renders nothing and pages keep their text views.
import { useState, type CSSProperties, type ReactNode } from 'react';
import type { AssetsReply, CardArt } from '../api/assets';
import { cx } from './cx';
import styles from './FutCard.module.css';

type Band = 'gold' | 'silver' | 'bronze';
/** The colour band FIFA picks from the rating (65/75 cuts). */
export const band = (rating: number): Band => (rating >= 75 ? 'gold' : rating >= 65 ? 'silver' : 'bronze');

// Text colour per frame, measured against the real frames: base frames and the white Icon frame take dark text; other
// special frames take light, gold, silver or bronze (TOTW keeps its band). Keys: "<rareflag>_<band>", "<rareflag>", band.
const INK: Record<string, string> = {
  bronze: '#2b1a0c', silver: '#14181c', gold: '#2a2006', '12': '#3a2e10',
  '3_gold': '#ecd27e', '3_silver': '#dde2e7', '3_bronze': '#d9a877', '5': '#f2d36b', '11': '#f2d36b',
};
const DARK_INK = new Set(['bronze', 'silver', 'gold', '12']);
// The drawn shade when a frame image is missing (top, bottom).
const SHADE: Record<string, [string, string]> = {
  bronze: ['#c09565', '#7a5634'], silver: ['#dfe4ea', '#98a2ad'], gold: ['#ecd27e', '#b08f3c'],
  '3': ['#34373b', '#111214'], '4': ['#6a4a9c', '#2c1c48'], '5': ['#23458c', '#0c1b3f'], '6': ['#3f7896', '#173646'],
  '7': ['#7a2232', '#2e0b13'], '8': ['#962424', '#2d0a0a'], '9': ['#cf4f86', '#5e1a3a'], '10': ['#2f7355', '#113526'],
  '11': ['#1d56a8', '#0a2552'], '12': ['#f4f0e4', '#cdbd8c'],
};
const DEFAULT_SPECIAL: [string, string] = ['#2a2f36', '#101316'];

/** The frame file for a card: its rarity's frames ("<rareflag>_<name>.png"), preferring its colour band. */
export function frameFor(rareflag: number, rating: number, frames: string[]): string | null {
  const own = frames.filter((name) => name.startsWith(`${rareflag}_`));
  const tier = band(rating);
  return own.find((name) => name.includes(`_${tier}`)) ?? (own.length === 1 ? own[0] : own.find((name) => name.includes('_gold')) ?? own[0]) ?? null;
}

/** Ink, glow and shade for a card: exported for tests. */
export function cardLook(rareflag: number, rating: number) {
  const tier = band(rating);
  const key = rareflag >= 3 ? String(rareflag) : tier;
  const inkKey = [`${rareflag}_${tier}`, key].find((candidate) => INK[candidate]);
  const ink = inkKey ? INK[inkKey]! : rareflag >= 3 ? '#f7f1e3' : INK.gold!;
  // Light text gets a soft shadow: several special frames (Beckham, Showdown) are pale behind the rating.
  const glow = inkKey && DARK_INK.has(inkKey) ? 'none' : '0 0.4cqw 1.2cqw rgb(0 0 0 / 0.55)';
  const [top, bottom] = SHADE[key] ?? DEFAULT_SPECIAL;
  return { ink, glow, top, bottom };
}

/** An image that tries each source in turn and disappears when none loads. */
function FallbackImage({ className, sources }: { className: string | undefined; sources: (string | null)[] }) {
  const list = sources.filter((source): source is string => !!source);
  const [index, setIndex] = useState(0);
  const source = list[index];
  if (!source) return null;
  return <img className={className ?? ''} src={source} alt="" loading="lazy" decoding="async" draggable={false} onError={() => setIndex(index + 1)} />;
}

export interface FaceStat { label: string; value: number }

export function FutCard({ art, assets, size = 'sm', stats, onClick, title, portraitUrl }: {
  art: CardArt | null | undefined;
  assets: AssetsReply | null | undefined;
  size?: 'xs' | 'thumbnail' | 'sm' | 'catalogue' | 'lg';
  /** The six face stats in FIFA's order; shown at the large size. */
  stats?: FaceStat[];
  onClick?: () => void;
  title?: string;
  /** A selected uploaded head in the card editor takes priority over downloaded art. */
  portraitUrl?: string | null;
}) {
  if (!art || !assets) return null;
  const base = assets.baseUrl ?? '/panel/assets/';
  const rareflag = Number(art.rareflag) || 0;
  const rating = Number(art.rating) || 0;
  const frame = frameFor(rareflag, rating, assets.frames ?? []);
  const { ink, glow, top, bottom } = cardLook(rareflag, rating);
  const name = art.cardName ?? ((art.name ?? '').replace(/\bNeymar Jr\.?$/i, 'Neymar').trim().split(/\s+/).at(-1) ?? '');
  const label = `${name || 'Player'}, ${art.rating ?? ''} ${art.position ?? ''}`.trim();
  const differs = art.resourceId != null && Number(art.resourceId) !== Number(art.assetId);
  const baseFace = !!art.headResourceId && Number(art.headResourceId) === Number(art.assetId);
  const selectedFace = !!art.headResourceId || !!art.customHeadId;
  const style = { '--fc-ink': ink, '--fc-glow': glow, '--fc-top': top, '--fc-bottom': bottom } as CSSProperties;
  const parts: ReactNode = (
    <>
      {frame && <img className={styles.frame} src={`${base}cards/hd/${frame}`} alt="" decoding="async" draggable={false} />}
      <FallbackImage key={`face-${art.resourceId}-${art.portraitId}-${art.headResourceId}-${art.customHeadId}-${art.defaultPortraitUrl ?? ''}-${portraitUrl ?? ''}`} className={styles.face} sources={[
        portraitUrl ?? (art.customHeadId ? `/panel/heads/${art.customHeadId}.png?texture=2` : null),
        !selectedFace ? art.defaultPortraitUrl ?? null : null,
        !baseFace && art.portraitId ? `${base}dynamic/p${art.portraitId}.png` : null,
        !selectedFace && differs ? `${base}dynamic/p${art.resourceId}.png` : null,
        art.assetId ? `${base}players/${art.assetId}.png` : null,
      ]} />
      {size !== 'thumbnail' && <span className={styles.rating}>{art.rating ?? ''}</span>}
      {size !== 'xs' && size !== 'thumbnail' && (
        <>
          <span className={styles.pos}>{art.position ?? ''}</span>
          {Number(art.nationId) > 0 && <FallbackImage key={`nation-${art.nationId}`} className={styles.nation} sources={[`${base}nations/${art.nationId}.png`]} />}
          {Number(art.teamId) > 0 && <FallbackImage key={`club-${art.teamId}`} className={styles.club} sources={[`${base}clubs/${art.teamId}.png`]} />}
          {/* Long names shrink to fit the name bar instead of losing their end. */}
          <span className={styles.name} style={{ fontSize: `${Math.min(8, 110 / Math.max(1, name.length)).toFixed(2)}cqw` }}>{name}</span>
        </>
      )}
      {(size === 'lg' || size === 'catalogue') && stats?.length ? (
        <span className={styles.stats}>
          {stats.map((stat) => <span key={stat.label}><b>{stat.value}</b><small>{stat.label}</small></span>)}
        </span>
      ) : null}
      {(size === 'lg' || size === 'catalogue') && (art.skillMoves || art.weakFoot || art.workRateAttack || art.workRateDefense) ? <span className={styles.details}>
        {art.skillMoves ? <span title="Skill moves"><b>{art.skillMoves}★</b></span> : null}
        {art.weakFoot ? <span title="Weak foot"><b>{art.weakFoot}★</b></span> : null}
        {(art.workRateAttack || art.workRateDefense) && <span title="Work rate: attacking / defending"><b>{art.workRateAttack?.charAt(0).toUpperCase() || '–'}/{art.workRateDefense?.charAt(0).toUpperCase() || '–'}</b></span>}
      </span> : null}
    </>
  );
  const className = cx(styles.card, styles[size], !frame && styles.shade);
  return onClick ? (
    <button type="button" className={className} style={style} title={title ?? label} aria-label={label} onClick={onClick}>{parts}</button>
  ) : (
    <div className={className} style={style} title={title ?? label} role="img" aria-label={label}>{parts}</div>
  );
}

export const CardGrid = ({ children, large = false }: { children: ReactNode; large?: boolean }) => <div className={cx(styles.grid, large && styles.catalogueGrid)}>{children}</div>;
export const CardRow = ({ children }: { children: ReactNode }) => <div className={styles.row}>{children}</div>;

export function LeagueLogo({ leagueId, assets }: { leagueId: number | null | undefined; assets: AssetsReply | null | undefined }) {
  if (!assets || !(Number(leagueId) > 0)) return null;
  return <FallbackImage className={styles.league} sources={[`${assets.baseUrl ?? '/panel/assets/'}leagues/${leagueId}.png`]} />;
}
