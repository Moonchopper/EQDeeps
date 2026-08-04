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
  /**
   * This session's own character and their pets, out of `damageTotal`. The
   * per-fight series any cross-window comparison has to be built from — totals
   * over windows of different lengths are not comparable at all.
   */
  characterDamage: number;
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
export interface GearItem {
  location: string;
  /** Which of the repeated slots this is: Ear#1 vs Ear#2. */
  occurrence: number;
  slotKey: string;
  /** Verbatim, upgrade level included: "Short Sword of the Ykesha +5". */
  name: string;
  /** The same name without the upgrade level, so "+2 → +5" reads as one item. */
  baseName: string;
  plus: number;
  itemId: number;
  augments: GearItem[];
}

export interface KeyRingEntry {
  category: string;
  name: string;
  itemId: number;
}

/** What one /outputfile inventory run proved about the player's gear. */
export interface GearSnapshot {
  character: string;
  server: string;
  /** When the dump was written — the instant this gear starts applying. */
  capturedAt: string;
  equipped: GearItem[];
  keyRing: KeyRingEntry[];
  /** Sum of upgrade levels. A progression marker, not a power rating. */
  upgradeScore: number;
  hash: string;
}

export type GearChangeKind =
  | "equipped"
  | "removed"
  | "upgraded"
  | "replaced"
  | "reaugmented";

export interface GearSlotChange {
  slotKey: string;
  location: string;
  kind: GearChangeKind;
  before?: GearItem;
  after?: GearItem;
}

/**
 * A gear change, dated at the snapshot that proved it. It happened somewhere
 * between `previousAt` and `at` — that window is the honest extent of what is
 * known, and the UI should not pretend otherwise.
 */
export interface GearChange {
  at: string;
  previousAt: string;
  slots: GearSlotChange[];
  upgradeScoreDelta: number;
}

export interface GearStatus {
  hasSnapshot: boolean;
  capturedAt?: string;
  /** Fights since the last snapshot — how far the gear could have drifted unseen. */
  fightsSince: number;
  /** Exactly where the dump is expected, for when the command seemed to do nothing. */
  expectedPath: string;
  command: string;
}

export interface GearReport {
  snapshots: GearSnapshot[];
  changes: GearChange[];
  status: GearStatus;
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

  getGear: (id: string): Promise<GearReport> =>
    fetch(`/api/sessions/${id}/gear`).then((r) => json(r)),

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
