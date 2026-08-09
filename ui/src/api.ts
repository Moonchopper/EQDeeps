// REST client + types mirroring the backend's JSON shapes (camelCase,
// string enums — see QuerySpecJson / ServerApp.ConfigureJson).

export interface SessionInfo {
  id: string;
  path: string;
  character: string;
  server: string;
  backfillComplete: boolean;
  recordCount: number;
  fightCount: number;
  unrecognizedLines: number;
  malformedLines: number;
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
};
