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
  "damage" | "healing" | "tanking" | "casts" | "deaths" | "experience" | "faction";
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
}

export const api = {
  listSessions: (): Promise<SessionInfo[]> => fetch("/api/sessions").then((r) => json(r)),

  getVersion: (): Promise<VersionInfo> => fetch("/api/version").then((r) => json(r)),

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
