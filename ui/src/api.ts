// REST client + types mirroring the backend's JSON shapes (camelCase,
// string enums — see QuerySpecJson / ServerApp.ConfigureJson).

export interface SessionInfo {
  id: string;
  path: string;
  character: string;
  server: string;
  /**
   * The installation the log belongs to — "EverQuest Legends", "EverQuest" —
   * or absent when the log is not under a Logs folder. Map choices and the
   * era are kept per install.
   */
  install?: string;
  backfillComplete: boolean;
  recordCount: number;
  fightCount: number;
  unrecognizedLines: number;
  malformedLines: number;
  /** Records this open restored from the log cache rather than re-parsing. */
  restoredRecords: number;
  /** Stance switches by this character; 0 hides the Stances view. */
  stanceSwitches: number;
}

export interface FightInfo {
  id: number;
  name: string;
  beginTime: string;
  lastDamageTime: string;
  dead: boolean;
  closed: boolean;
  damageTotal: number;
  tankingTotal: number;
  tauntCount: number;
  groupIndex: number;
  /** Instance difficulty; absent in the open world (and in a tier-0 instance). */
  difficulty?: number;
  /**
   * Learned health for this mob at this zone and difficulty (F25). Absent
   * until enough of them have been killed. Against `damageTotal` it says
   * whether this fight was a whole kill or a share of one.
   */
  estimatedHealth?: number;
  /**
   * This session's own character and their pets, out of `damageTotal`. The
   * per-fight series any cross-window comparison has to be built from — totals
   * over windows of different lengths are not comparable at all.
   */
  characterDamage: number;
}

export type MobHealthConfidence = "low" | "medium" | "high";

/**
 * What a mob is worth in one place at one difficulty (F25), learned from the
 * damage it took to kill. `health` is the median damage-to-kill and is biased
 * high — the killing blow overshoots — so `floor` and `ceiling` travel with it
 * rather than the one number being shown alone.
 */
export interface MobHealthEstimate {
  mob: string;
  zone: string;
  /** Absent in the open world. */
  difficulty?: number;
  /** The server's word for the tier ("Awakened", "Fused"). */
  tierName?: string;
  health: number;
  /** 10th percentile of the kills behind it. */
  floor: number;
  /** 90th percentile. */
  ceiling: number;
  samples: number;
  /** Those that survived the merged-fight filter — what the estimate used. */
  cleanSamples: number;
  confidence: MobHealthConfidence;
  lastKilled: string;
}

export interface MobHealthReport {
  server: string;
  mobs: MobHealthEstimate[];
  kills: number;
  /** Whether any of it came from an instance — decides if tier columns mean anything. */
  instanced: boolean;
}

export type MobAttackConfidence = "low" | "medium" | "high";

/** One of a mob's attacks, on its own. */
export interface MobAttackSkill {
  skill: string;
  /** Spells have attempts but no swing accounting — nothing about them can be dodged. */
  spell: boolean;
  swings: number;
  landed: number;
  total: number;
  avgHit: number;
  medianHit: number;
  floor: number;
  ceiling: number;
  maxHit: number;
  minHit: number;
  hitRate: number;
  missRate: number;
  avoidRate: number;
}

/**
 * What it costs to stand in front of one mob, in one place, at one difficulty,
 * at one defender level (F26).
 *
 * The level is in the key because how hard a mob hits is a fact about a
 * pairing, not about the mob: pooling a level-40's incoming damage with a
 * level-60's would produce an average describing neither.
 *
 * The hit-size figures — `avgHit`, `medianHit`, `floor`/`ceiling`, `maxHit`,
 * `minHit` — are **melee only**. Averaging a 15-point damage shield tick with a
 * 200-point backstab gives a number describing neither, so spells and shields
 * live in `spellTotal` and in `skills` where they read as themselves. The
 * p10–p90 band is shown rather than collapsed: unlike mob health, that spread
 * is the mob's real damage range rather than doubt about it.
 */
export interface MobAttackEstimate {
  mob: string;
  zone: string;
  /** Absent in the open world. */
  difficulty?: number;
  tierName?: string;
  /** Absent when the log never established who was being hit. */
  defenderLevel?: number;
  fights: number;
  /** Melee attempts the log accounted for. Ripostes are not among them — the log drops them. */
  swings: number;
  /** Everything that did damage, spells and shields included. */
  landed: number;
  /** Every point inflicted, on the same footing as `landed`. */
  total: number;
  /** The landed swings the headline figures are computed from. */
  meleeHits: number;
  meleeTotal: number;
  /** The rest of `total`: spells, DoTs and damage shields. */
  spellTotal: number;
  avgHit: number;
  medianHit: number;
  floor: number;
  ceiling: number;
  maxHit: number;
  minHit: number;
  hitRate: number;
  missRate: number;
  dodgeRate: number;
  parryRate: number;
  blockRate: number;
  absorbRate: number;
  /** Who the evidence came from, capped — this key pools every defender at one level. */
  defenders: string[];
  skills: MobAttackSkill[];
  confidence: MobAttackConfidence;
  firstSeen: string;
  lastSeen: string;
}

export interface MobAttackReport {
  server: string;
  character: string;
  /** Absent when the log has not established it — which rows are "mine" then has no answer. */
  characterLevel?: number;
  mobs: MobAttackEstimate[];
  landed: number;
  instanced: boolean;
}

/** How a swing turned out. Avoided outcomes carry a zero amount. */
export type HitOutcome =
  "melee" | "directDamage" | "damageOverTime" | "damageShield" | "other" |
  "miss" | "dodge" | "parry" | "block" | "invulnerable" | "absorb";

/** One item as the server's registry knows it (F29). Nulls are omitted on the wire. */
export interface ItemRecord {
  name: string;
  /** The game's own id, when a client file (loot filters, inventory dump) has supplied it. */
  id?: number;
  iconId?: number;
  firstSeen?: string;
  lastSeen?: string;
  /** Flags as the server prints them, e.g. "LootFilter, Looted". */
  sources: string;
  looted: number;
  sold: number;
  bought: number;
}

export interface ItemReport {
  server: string;
  items: ItemRecord[];
  /** How many rows carry a game id. */
  numbered: number;
}

export type ItemMentionKind = "chat" | "looted" | "sold" | "bought";

export interface ItemMention {
  at: string;
  kind: ItemMentionKind;
  item: string;
  id?: number;
  /** Looter, seller ("You"), buyer ("You") or chat sender. */
  who: string;
  /** The corpse, the merchant, or the chat channel. */
  where?: string;
  /** The chat line, for chat mentions. */
  text?: string;
  quantity: number;
}

export interface ItemMentionsResult {
  mentions: ItemMention[];
  /** How many fell in the scope before the limit. */
  total: number;
  /** How many names the chat scanner knew — zero explains an empty chat column. */
  knownNames: number;
}

/** What the reference layer can answer right now, and why not when it cannot. */
export interface ReferenceStatus {
  available: boolean;
  source: string;
  homeUrl: string;
  names: number;
  listings: number;
  refreshedUtc?: string;
  error?: string;
}

/** One NPC as the reference site lists it. */
export interface NpcListing {
  name: string;
  level?: number;
  id: number;
  url: string;
}

/**
 * One name in a browse. A reference site lists the same mob once per zone it
 * stands in — "a ghoul" is 33 listings, seven of them level 13, alike but for
 * where they stand — so a name is one row here, its level span is stated, and
 * `levels` carries one listing per distinct level (ADR-020).
 */
export interface NpcBrowseRow {
  name: string;
  minLevel?: number;
  maxLevel?: number;
  /** How many listings the site carries under this name, before collapsing. */
  listings: number;
  levels: NpcListing[];
}

export interface NpcSearchResult {
  source: string;
  npcs: NpcBrowseRow[];
  error?: string;
}

export interface NpcLootLine {
  itemId: number;
  item: string;
  dropPercent: number;
  iconId: number;
  damage?: string;
}

export interface NpcSpawnZone {
  shortName: string;
  longName: string;
  spawnPoints: number;
  locations: number[][];
}

/** Everything the site lists about one NPC; every field may be absent. */
export interface NpcDetail {
  id: number;
  name: string;
  level?: number;
  maxLevel?: number;
  hp?: number;
  ac?: number;
  race?: string;
  class?: string;
  faction?: string;
  respawnSeconds?: number;
  minDamage?: number;
  maxDamage?: number;
  specials: string[];
  loot: NpcLootLine[];
  zones: NpcSpawnZone[];
}

export interface NpcDetailResult {
  source: string;
  url: string;
  detail: NpcDetail;
}

export interface NpcLookupResult {
  source: string;
  listing: NpcListing;
  /** True when a /consider level picked this listing out of several. */
  exact: boolean;
  observedLevels: number[];
  detail?: NpcDetail;
}

export interface IncomingHit {
  at: string;
  attacker: string;
  defender: string;
  /** Set when the defender is a pet. */
  defenderOwner?: string;
  skill: string;
  outcome: HitOutcome;
  amount: number;
  modifiers: string;
  spell: boolean;
  fight?: string;
}

/**
 * The tail of the incoming-damage stream. Deliberately not a QuerySpec: the
 * sequence is the information, and no aggregation keeps it.
 */
export interface IncomingHitsResult {
  rangeBegin?: string;
  rangeEnd?: string;
  hits: IncomingHit[];
  /** How many fell in the scope before the tail was taken. */
  total: number;
  dataVersion: number;
}

export type QuerySource =
  "damage" | "healing" | "tanking" | "casts" | "deaths" | "experience" | "faction" | "loot" |
  "considers";
export type Dimension = "player" | "target" | "spell" | "damageType" | "character" | "stance";

export interface QueryFilter {
  dim?: Dimension;
  values?: string[];
  flag?: "damageShield" | "bane" | "headshot" | "assassinate" | "finishingBlow" | "slayUndead";
  exclude?: boolean;
}

export interface QuerySpec {
  source: QuerySource;
  scope: {
    fightIds?: number[];
    timeRanges?: { begin: string; end: string }[];
    lastSeconds?: number;
    skipFirstSeconds?: number;
    maxSeconds?: number;
    /**
     * Cut the time between play sessions out of a range before measuring it.
     * Only bites on a scope that is a stretch of wall clock — fights carry
     * their own ends, so a scope made of them never held the night anyway.
     */
    playedTimeOnly?: boolean;
  };
  groupBy: Dimension[];
  metrics?: string[];
  filters?: QueryFilter[];
  bucketSeconds?: number;
  petRollup?: boolean;
}

export interface SeriesPoint {
  bucketStart: string;
  value: number;
}

export interface QueryRow {
  key: string;
  label: string;
  metrics: Record<string, number>;
  children?: QueryRow[];
  series?: SeriesPoint[];
}

export interface QueryResult {
  rows: QueryRow[];
  totals: Record<string, number>;
  raidSeconds: number;
  dataVersion: number;
}

export type TimelineItemKind =
  "cast" | "song" | "interrupt" | "fizzle" | "ability" | "buff" | "fade" | "death" | "resist" |
  "stance";

/** One timeline mark: instants have no `end`; buff spans run [start, end]. */
export interface TimelineItem {
  actor: string;
  kind: TimelineItemKind;
  label: string;
  start: string;
  end?: string;
  startsBefore?: boolean;
  endsAfter?: boolean;
  /** What this cast landed, when it could be paired. Absent when unknown. */
  amount?: number;
  effect?: "none" | "damage" | "heal";
}

export interface TimelineResult {
  rangeBegin?: string;
  rangeEnd?: string;
  items: TimelineItem[];
  dataVersion: number;
}

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new Error(`${response.status} ${await response.text()}`);
  }
  return (await response.json()) as T;
}

// ---- zone maps (F27) -------------------------------------------------------

/** How a zone's display name was arrived at. Only the first two are verifiable. */
export type ZoneNameSource = "name" | "graph" | "curated";

/**
 * How a zone's era was arrived at: from the band its client zone id falls in,
 * or set by hand where the band is known to be wrong (issue #57).
 */
export type ZoneEraSource = "id" | "curated";

/** One expansion, in release order. `id` is what the table and the API use. */
export interface ZoneEra {
  id: string;
  /** Full title — "The Ruins of Kunark". */
  name: string;
  /** What players call it — "Kunark". */
  short: string;
  year: number;
}

export interface MapCatalogEntry {
  shortName: string;
  /** Absent when the zone table does not know this map's name. */
  displayName?: string;
  nameSource?: ZoneNameSource;
  /**
   * The earliest expansion the place exists in, as a `ZoneEra.id`. Absent
   * when the table cannot say — which means shown under every era filter,
   * never hidden.
   */
  era?: string;
  eraSource?: ZoneEraSource;
  /** Map sets holding this zone, best first — usually "default", "brewalls". */
  sets: string[];
}

export interface MapCatalog {
  found: boolean;
  /** Where the app looked, reported even when it found nothing. */
  roots: string[];
  zones: MapCatalogEntry[];
  /** The folder the user nominated, if they have. */
  userRoot?: string;
}

/**
 * Every segment of one colour, packed as [x1,y1,z1, x2,y2,z2, …].
 *
 * Flat and pre-grouped because that is what the canvas wants: one path per
 * colour rather than a style change per segment. See ADR-016.
 */
export interface MapStrokes {
  r: number;
  g: number;
  b: number;
  segments: number[];
}

export interface MapLabel {
  x: number;
  y: number;
  z: number;
  r: number;
  g: number;
  b: number;
  size: number;
  text: string;
}

export interface MapLayer {
  index: number;
  strokes: MapStrokes[];
  labels: MapLabel[];
  segments: number;
}

export interface MapBounds {
  minX: number;
  minY: number;
  minZ: number;
  maxX: number;
  maxY: number;
  maxZ: number;
}

export interface ZoneMap {
  shortName: string;
  displayName?: string;
  nameSource?: ZoneNameSource;
  era?: string;
  eraSource?: ZoneEraSource;
  set: string;
  sets: string[];
  bounds: MapBounds;
  layers: MapLayer[];
}

export interface ZoneGraphNode {
  /** The place's representative map — the first of `maps`. */
  shortName: string;
  displayName?: string;
  /** Every map that draws this place; two when a revamp sits beside its original. */
  maps: string[];
  degree: number;
  /** Earliest expansion the zone exists in; absent when unknown (and so always shown). */
  era?: string;
  eraSource?: ZoneEraSource;
}

export interface ZoneGraphEdge {
  from: string;
  to: string;
}

export interface ZoneGraph {
  zones: ZoneGraphNode[];
  edges: ZoneGraphEdge[];
  /** Every expansion in release order, so era codes can be compared. */
  eras: ZoneEra[];
  /**
   * Map files read from disk to build this graph, and those whose labels came
   * from the label cache; the first is zero once the cache is warm. Optional
   * because the view derives sub-graphs that carry neither.
   */
  mapsRead?: number;
  mapsRemembered?: number;
}

export interface ZoneRouteStep {
  shortName: string;
  displayName?: string;
  /** The label the previous zone used for this exit — "(Boat)" and the like. */
  via?: string;
}

/**
 * `found` is a flag rather than a null route because the server drops nulls
 * when serializing, and an empty route would read as "you are already there".
 */
export interface ZoneRoute {
  found: boolean;
  route: ZoneRouteStep[];
}

export interface DiscoveredLog {
  path: string;
  character: string;
  server: string;
  lastWriteTime: string;
  sizeBytes: number;
  source: string;
}

/** One equipped item, with whatever is socketed into it. */

/**
 * A stretch over which one thing stayed true — the zone the character was in,
 * or the level they were. Both are step functions over the log, clipped to the
 * play sessions so nothing is drawn across the night.
 */
export interface ContextSpan {
  range: { begin: string; end: string };
  label: string;
}

export interface ContextTimeline {
  zones: ContextSpan[];
  levels: ContextSpan[];
}

export interface VersionInfo {
  version: string;
  updateAvailable: boolean;
  latestVersion?: string;
  releaseUrl?: string;
  releaseNotes?: string;
}

export type UpdateStage =
  | "idle"
  | "checking"
  | "available"
  | "downloading"
  | "staged"
  | "failed";

/** Standing policy: ask each time (default), update silently, or never check. */
export type UpdateMode = "ask" | "auto" | "manual";

/**
 * How long a "no" lasts. "once" is forgotten at restart, "release" waits for
 * something newer than what was offered, "currentVersion" stays quiet until
 * the user is running a different build.
 */
export type DeferScope = "once" | "release" | "currentVersion";

export interface UpdateState {
  version: string;
  stage: UpdateStage;
  mode: UpdateMode;
  latestVersion?: string;
  releaseNotes?: string;
  releaseUrl?: string;
  downloadPercent: number;
  downloadedBytes: number;
  /** Total size of the update, known from the app cast before the first byte. */
  downloadSizeBytes: number;
  /** The server wants the consent dialog shown right now. */
  promptRequired: boolean;
  /** An installer is staged; it lands on next launch, or on "Restart now". */
  restartRequired: boolean;
  /** Applying would raise a UAC prompt, so it needs an explicit click. */
  requiresElevation: boolean;
  /** False for portable and source builds: they can only link to the release. */
  canSelfInstall: boolean;
  /** When the last check completed — proof a manual check actually ran. */
  lastCheckedUtc?: string;
  error?: string;
}

export const api = {
  listSessions: (): Promise<SessionInfo[]> => fetch("/api/sessions").then((r) => json(r)),

  getVersion: (): Promise<VersionInfo> => fetch("/api/version").then((r) => json(r)),

  getUpdateState: (): Promise<UpdateState> => fetch("/api/update/state").then((r) => json(r)),

  /** Explicit "check for updates" — overrides every standing decline. */
  checkForUpdate: (): Promise<UpdateState> =>
    fetch("/api/update/check", { method: "POST" }).then((r) => json(r)),

  /**
   * Consent given: download in the background and stage for install.
   * `applyWhenReady` means "update now" — install and relaunch as soon as the
   * download lands, rather than waiting for the user to close the app.
   */
  stageUpdate: (applyWhenReady = false): Promise<UpdateState> =>
    fetch("/api/update/stage", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ applyWhenReady }),
    }).then((r) => json(r)),

  deferUpdate: (scope: DeferScope): Promise<UpdateState> =>
    fetch("/api/update/defer", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ scope }),
    }).then((r) => json(r)),

  setUpdateMode: (mode: UpdateMode): Promise<UpdateState> =>
    fetch("/api/update/mode", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ mode }),
    }).then((r) => json(r)),

  /** Close and reinstall now. The app exits; the installer brings it back. */
  applyUpdate: (): Promise<void> =>
    fetch("/api/update/apply", { method: "POST" }).then(() => undefined),

  discoverLogs: (): Promise<DiscoveredLog[]> => fetch("/api/logs/discovered").then((r) => json(r)),

  /** Drop a log from the recently-opened list. The file itself is untouched. */
  forgetRecentLog: (path: string): Promise<void> =>
    fetch(`/api/logs/recent?path=${encodeURIComponent(path)}`, { method: "DELETE" }).then(
      () => undefined,
    ),

  openSession: (path: string): Promise<SessionInfo> =>
    fetch("/api/sessions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path }),
    }).then((r) => json(r)),

  closeSession: (id: string): Promise<void> =>
    fetch(`/api/sessions/${id}`, { method: "DELETE" }).then(() => undefined),

  getSession: (id: string): Promise<SessionInfo> =>
    fetch(`/api/sessions/${id}`).then((r) => json(r)),

  getFights: (id: string): Promise<FightInfo[]> =>
    fetch(`/api/sessions/${id}/fights`).then((r) => json(r)),

  /**
   * Learned mob health for this session's *server* — the session only says
   * which world is being asked about. Every log ever opened against it has
   * contributed.
   */
  getMobs: (id: string): Promise<MobHealthReport> =>
    fetch(`/api/sessions/${id}/mobs`).then((r) => json(r)),

  /**
   * Learned attack profiles for this session's server (F26). Server-wide like
   * mob health, but the rows are per defender level, so the session also says
   * which of them are this character's.
   */
  getAttacks: (id: string): Promise<MobAttackReport> =>
    fetch(`/api/sessions/${id}/attacks`).then((r) => json(r)),

  // ---- NPC reference (F30) --------------------------------------------------
  // Someone else's data about the game, fetched by our server on demand and
  // cached there (ADR-020). Every call can answer "nothing", and the app is
  // unaffected when it does.

  referenceStatus: (): Promise<ReferenceStatus> =>
    fetch("/api/reference/status").then((r) => json(r)),

  searchNpcs: (q: string, limit = 60): Promise<NpcSearchResult> =>
    fetch(`/api/reference/npcs?q=${encodeURIComponent(q)}&limit=${limit}`).then((r) => json(r)),

  npcDetail: async (id: number): Promise<NpcDetailResult | null> => {
    const response = await fetch(`/api/reference/npcs/${id}`);
    if (response.status === 204 || !response.ok) return null;
    return (await response.json()) as NpcDetailResult;
  },

  /** A name the log met, matched to a listing using this session's /consider levels. */
  lookupNpc: async (sessionId: string, name: string): Promise<NpcLookupResult | null> => {
    const response = await fetch(
      `/api/sessions/${sessionId}/npcs/lookup?name=${encodeURIComponent(name)}`,
    );
    if (response.status === 204 || !response.ok) return null;
    return (await response.json()) as NpcLookupResult;
  },

  // ---- item registry (F29) --------------------------------------------------

  /** Everything the server's registry has named, with ids where a client file supplied one. */
  getItems: (id: string): Promise<ItemReport> =>
    fetch(`/api/sessions/${id}/items`).then((r) => json(r)),

  /** One name, resolved to what the registry knows — null when nothing is. */
  resolveItem: async (id: string, name: string): Promise<ItemRecord | null> => {
    const response = await fetch(`/api/sessions/${id}/items/resolve?name=${encodeURIComponent(name)}`);
    if (response.status === 204 || !response.ok) return null;
    return (await response.json()) as ItemRecord;
  },

  /** The item feed over a scope: looted, sold, bought, named in chat — newest first. */
  itemMentions: (
    id: string,
    scope: QuerySpec["scope"],
    options: { limit?: number } = {},
  ): Promise<ItemMentionsResult> =>
    fetch(`/api/sessions/${id}/items/mentions`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ scope, ...options }),
    }).then((r) => json(r)),

  /** The raw incoming stream over a scope, newest last. */
  hits: (
    id: string,
    scope: QuerySpec["scope"],
    options: { limit?: number; ownerOnly?: boolean } = {},
  ): Promise<IncomingHitsResult> =>
    fetch(`/api/sessions/${id}/hits`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ scope, ...options }),
    }).then((r) => json(r)),

  getContext: (id: string): Promise<ContextTimeline> =>
    fetch(`/api/sessions/${id}/context`).then((r) => json(r)),

  timeline: (id: string, scope: QuerySpec["scope"]): Promise<TimelineResult> =>
    fetch(`/api/sessions/${id}/timeline`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ scope }),
    }).then((r) => json(r)),

  query: (id: string, spec: QuerySpec): Promise<QueryResult> =>
    fetch(`/api/sessions/${id}/query`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(spec),
    }).then((r) => json(r)),

  getStore: async <T>(key: string): Promise<T | null> => {
    const response = await fetch(`/api/store/${key}`);
    if (response.status === 204 || !response.ok) {
      return null;
    }
    return (await response.json()) as T;
  },

  putStore: (key: string, document: unknown): Promise<void> =>
    fetch(`/api/store/${key}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(document),
    }).then(() => undefined),

  // ---- zone maps (F27) ----------------------------------------------------

  mapCatalog: (): Promise<MapCatalog> => fetch("/api/maps").then((r) => json(r)),

  /**
   * Point the app at a maps folder, or pass null to clear it and go back to
   * discovery. Rejects with the server's reason, which is written to be shown
   * next to the box the path was typed into.
   */
  setMapRoot: async (path: string | null): Promise<MapCatalog> => {
    const response = await fetch("/api/maps/root", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path }),
    });

    if (!response.ok) {
      const body = (await response.json().catch(() => null)) as { error?: string } | null;
      throw new Error(body?.error ?? `${response.status}`);
    }

    return (await response.json()) as MapCatalog;
  },

  /**
   * Which maps could be the zone a log line named. Plural on purpose: a
   * revamped zone shares its display name with the classic one.
   */
  resolveZone: (zone: string): Promise<{ zone: string; shortNames: string[] }> =>
    fetch(`/api/maps/resolve?zone=${encodeURIComponent(zone)}`).then((r) => json(r)),

  zoneMap: (shortName: string, set?: string): Promise<ZoneMap> =>
    fetch(
      `/api/maps/${encodeURIComponent(shortName)}` +
        (set ? `?set=${encodeURIComponent(set)}` : ""),
    ).then((r) => json(r)),

  /** The whole world. First call reads every map's labels and takes seconds. */
  zoneGraph: (): Promise<ZoneGraph> => fetch("/api/maps/graph").then((r) => json(r)),

  /**
   * `era` is the expansion the player says their server has reached; the
   * route then avoids zones from later ones. Omit it for the whole world.
   */
  zoneRoute: (from: string, to: string, era?: string): Promise<ZoneRoute> =>
    fetch(
      `/api/maps/route?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}` +
        (era ? `&era=${encodeURIComponent(era)}` : ""),
    ).then((r) => json(r)),
};
