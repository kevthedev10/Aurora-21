// Reply shapes, written from ultimateteam/panel-api-contract.md and the server's OperatorPanel*.cs. Every field is
// optional: the running API can be older than this page, and a missing field is shown as "not reported".

export interface SessionReply {
  ok?: boolean;
  mode?: string;
}

export interface DashboardReply {
  services?: {
    host?: string;
    database?: string;
    list?: { key?: string; label?: string; port?: number; up?: boolean }[];
    supervised?: boolean;
    futAlive?: boolean;
    blazeAlive?: boolean;
    blaze?: boolean | null;
    fut?: boolean | null;
    blazeAccess?: { maintenance?: boolean; bypassCount?: number; maxPlayers?: number; blockedCount?: number } | null;
  };
  online?: { count?: number; players?: { id?: number; name?: string; club?: string | null; remote?: string; since?: string }[] };
  totals?: { players?: number; clubs?: number; players_today?: number; logins_today?: number; players_week?: number; coins?: number; points?: number; items?: number };
  recent?: { id?: number; name?: string; club?: string | null; remote?: string; at?: string; ended?: string | null }[];
  clubs?: { persona_id?: number; club_name?: string; club_abbr?: string; coins?: number; points?: number; items?: number; new_items?: number; squads?: number | null; updated_at?: string }[];
  days?: { day?: string; players?: number; logins?: number }[];
  supervisor?: string[];
  snapshots?: { key?: string; process?: string; status?: string; updatedAt?: string; bytes?: number }[];
  launch?: { identity?: boolean; playRoles?: number; discordBot?: boolean };
  startedAt?: string;
  drafts?: { current?: boolean; count?: number; prizePending?: number };
  identity?: { collisions?: unknown[] };
  debug?: { active?: boolean; unknown?: boolean } | null;
}

/** GET content/changeset: the open changeset, if any (OperatorPanel.Content.cs ChangesetView). */
export interface ChangesetReply {
  areas?: string[];
  open?: boolean;
  pending?: number;
  changeset?: Changeset | null;
}

/** GET players: one row per persona that has signed in or has a club (OperatorPanel.Players.cs PlayerRow). */
export interface PlayerRow {
  id: number;
  discordId?: string | null;
  name?: string | null;
  club?: { name?: string | null; abbr?: string | null; coins?: number; points?: number; items?: number } | null;
  logins?: number;
  lastLogin?: string | null;
  online?: boolean;
  access?: { blocked?: boolean; maintenanceBypass?: boolean; note?: string | null } | null;
}

export interface PlayersReply {
  players?: PlayerRow[];
}

/** GET cards: one grantable card (OperatorPanel.Cards.cs CardView). */
export interface CardRow {
  forcedUntradeable?: boolean;
  cardKey: string;
  resourceId?: number;
  assetId?: number;
  name?: string | null;
  rating?: number;
  rareflag?: number;
  special?: string | null;
  position?: string | null;
  skillMoves?: number;
  weakFoot?: number;
  workRateAttack?: string | null;
  workRateDefense?: string | null;
  /** The six face stats in FIFA order (attrs, or gkAttrs for a goalkeeper). */
  attributes?: number[];
  nation?: string | null;
  teamId?: number | null;
  leagueId?: number | null;
  /** False for grant-only cards that packs never drop. */
  inPacks?: boolean;
  art?: import('./assets').CardArt | null;
}

export interface CardsReply {
  buckets?: CardBucket[] | null;
  total?: number;
  offset?: number;
  limit?: number;
  poolSize?: number;
  positions?: string[];
  attrs?: string[];
  gkAttrs?: string[];
  /** Special rareflag to its name. */
  flags?: Record<string, string>;
  grant?: { available?: boolean; reason?: string | null; countMax?: number };
  cards?: CardRow[];
}

/** GET cards/items?tab=: one grantable consumable, club customisation item or manager (OperatorPanel.Items.cs). */
export interface ItemRow {
  /** <pool>:<resourceId>, or vanity:<subtype>:<asset> for a My Stadium item: what grantItem takes. */
  key: string;
  type: string;
  typeName: string;
  name: string;
  /** A My Stadium item's localisation key, when it has one. */
  nameKey?: string | null;
  rating: number;
  rareflag: number;
  resourceId: number;
  team?: string | null;
  league?: string | null;
  nation?: string | null;
  forcedUntradeable: boolean;
  countMax: number;
}

export interface ItemsReply {
  tab: string;
  types: { value: string; label: string; count: number }[];
  items: ItemRow[];
  total: number;
}

export interface CardBucket {
  code: string; name: string; weight: number; enabled: boolean;
  count: number; total: number; offset: number; limit: number; cards: CardRow[];
}

/** GET cards/specials: each special card type with how many cards it has. */
export interface SpecialsReply {
  types?: { flag: number; name?: string; available?: number; inPacks?: number }[];
  poolSize?: number;
}

/** GET cards/editor/{resourceId}: the complete value required by the player_card content area. */
export interface CardEditorReply {
  resourceId: number;
  assetId?: number;
  name?: string | null;
  cardTypeId: number;
  grantOnly?: boolean;
  art?: import('./assets').CardArt | null;
  card: Record<string, string | number | number[] | null>;
  options: {
    positions: string[];
    feet: string[];
    workRates: string[];
    cardTypes: { id: number; name: string; level?: string | null; rareflag: number }[];
    traits: { id: number; name: string; appliesTo?: 'goalkeeper' | 'outfield' | 'both' }[];
  };
  revisionSize?: number;
}

/** GET cards/images/{assetId}: available portraits for every card of the same player. */
export interface CardImagesReply {
  assetId: number;
  basePhoto?: string | null;
  cards: { resourceId: number; name?: string; rating?: number; rarity?: string; url?: string | null }[];
  uploads: { headId: number; label: string; createdAt?: string; createdBy?: string | null; url: string }[];
}

/** GET packs/catalog (OperatorPanel.PackCatalog.cs). */
export interface CatalogPack {
  unavailableReason?: string | null;
  /** The pack's tile art, named by its store category (bronze, silver, gold, special). */
  art?: string | null;
  /** Whether the published store lineup sells it. */
  inStore?: boolean;
  /** The published rules that award this pack, as file.path; empty when no reward names it. */
  usedBy?: string[] | null;
  /** Set when what the pack draws comes from somewhere the editor does not control; naming and selling stay editable. */
  viewOnlyReason?: string | null;
  id: number;
  description?: string;
  blurb?: string;
  group?: string;
  coins?: number;
  points?: number;
  items?: number;
  rare?: number;
  /** Always present in the store: cannot be hidden. */
  locked?: boolean;
  grantOnly?: boolean;
  config?: Record<string, unknown>;
}

/** A pack on the store listing: sold when switched on; untradeable null keeps the server's store default. */
export interface ListingEntry {
  price?: number;
  packId: number;
  enabled: boolean;
  untradeable: boolean | null;
}

export interface PackCatalogReply {
  /** An md5 of the store packs; every save sends it back and is refused (409) if the catalogue changed meanwhile. */
  revision?: string;
  groups?: string[];
  publicPackIds?: number[];
  /** The store listing (migration 132): listed packs, each switched on or off, with a purchase's tradeability. */
  listing?: ListingEntry[];
  tokenListing?: ListingEntry[];
  packs?: CatalogPack[];
  promos?: PromoOption[];
  pickLimits?: { minChoices: number; maxChoices: number; maxTokensPerGroup: number };
  /** The values a new pack or pick starts its odds table with (rules/pack_odds.json). */
  oddsTemplate?: { icon: number; pickTotw: number };
  /** The My Stadium item types a My Stadium pack slot can draw, with how many items a store pack may give. */
  vanityTypes?: VanityType[];
}

export interface VanityType { subtype: number; name: string; items: number }

export interface PromoOption { code: string; name: string; category: string }

/** One staged content change; before and after are the row's values, null for an insert or a hide. */
export interface ContentChange {
  id: number;
  area: string;
  key: Record<string, unknown>;
  operation: string;
  before?: unknown;
  after?: unknown;
  updatedAt?: string | null;
}

export interface Changeset {
  id: number;
  status?: string;
  note?: string | null;
  createdBy?: string | null;
  createdAt?: string | null;
  publishedAt?: string | null;
  revertsChangesetId?: number | null;
  changes?: ContentChange[];
}

/** GET packs: the store packs with their contents, used to pick a gift (OperatorPanel.Packs.cs PackOverviewAsync). */
export interface PackOverview {
  id: number;
  name?: string;
  category?: string;
  items?: number;
  rare?: number;
  ratingMin?: number | null;
  ratingMax?: number | null;
  coins?: number;
  points?: number;
  blurb?: string;
  enabled?: boolean;
}

export interface PacksReply {
  packs?: { packs?: PackOverview[] };
  grantable?: { id: number; name?: string }[];
  pileCap?: { value?: number } | null;
}
