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
}

export type QuerySource =
  "damage" | "healing" | "tanking" | "casts" | "deaths" | "experience" | "faction" | "loot" |
  "considers";
export type Dimension = "player" | "target" | "spell" | "damageType" | "character";

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
  "cast" | "song" | "interrupt" | "fizzle" | "ability" | "buff" | "fade" | "death" | "resist";

/** One timeline mark: instants have no `end`; buff spans run [start, end]. */
export interface TimelineItem {
  actor: string;
  kind: TimelineItemKind;
  label: string;
  start: string;
  end?: string;
  startsBefore?: boolean;
  endsAfter?: boolean;
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
