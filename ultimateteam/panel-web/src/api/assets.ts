import { useQuery } from '@tanstack/react-query';
import { api } from './client';

/** GET assets: the downloaded card images the server serves under /panel/assets/ (OperatorPanel.Assets.cs). */
export interface AssetsReply {
  available?: boolean;
  baseUrl?: string;
  categories?: Record<string, number>;
  frames?: string[];
}

/** The card art fields the server adds to cards, inventory items and pack draws (`art`). */
export interface CardArt {
  resourceId?: number | null;
  assetId?: number | null;
  /** The special-card image id (dynamic art); null or 0 for none. */
  portraitId?: number | null;
  /** An explicit existing-image selection; equal to assetId for the base face. */
  headResourceId?: number | null;
  /** Preview decoded from this card's bundled native DDS override. */
  defaultPortraitUrl?: string | null;
  /** Published uploaded portrait, served by the private panel. */
  customHeadId?: number | null;
  rareflag?: number | null;
  rating?: number | null;
  position?: string | null;
  skillMoves?: number | null;
  weakFoot?: number | null;
  workRateAttack?: string | null;
  workRateDefense?: string | null;
  name?: string | null;
  cardName?: string | null;
  teamId?: number | null;
  nationId?: number | null;
  leagueId?: number | null;
}

/** The image summary, or null when the server has no images (pages then keep their text views). */
export function useAssets() {
  return useQuery({
    queryKey: ['assets'],
    queryFn: async () => {
      const reply = await api<AssetsReply>('assets').catch(() => null);
      return reply?.available ? reply : null;
    },
    // A running download keeps adding images.
    staleTime: 60_000,
  });
}
