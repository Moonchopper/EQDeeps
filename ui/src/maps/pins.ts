import type { NpcPlace } from "../api";
import { SERIES_COLORS } from "../format";

/**
 * A mob the player has pinned to the maps (F30 × F27): drawn as its spawn
 * points on whichever zone is open and as a ring on every zone it stands in
 * on the world, in one colour across both, until unpinned.
 *
 * <p>Places come from the browse row — known from the listing ids alone, so
 * pinning costs no fetch. Spawn points are per zone and are filled in lazily
 * the first time that zone is drawn (one cached shard read), because a name
 * like "a ghoul" stands in nine zones and nobody is looking at eight of
 * them.</p>
 */
export interface PinnedMob {
  name: string;
  places: NpcPlace[];
  /** Spawn points by map short name, [x, y, z] as the site gives them; absent until that zone has been drawn. */
  points: Record<string, number[][]>;
}

const KEY = "eqdeeps.pinnedMobs";

/** The colour a pin draws in: its slot in the shared palette, so it reads the same on both maps. */
export function pinColor(index: number): string {
  return SERIES_COLORS[index % SERIES_COLORS.length];
}

export function loadPins(): PinnedMob[] {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as unknown;
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (p): p is PinnedMob =>
        typeof p === "object" && p !== null && typeof (p as PinnedMob).name === "string" && Array.isArray((p as PinnedMob).places),
    ).map((p) => ({ ...p, points: p.points ?? {} }));
  } catch {
    return [];
  }
}

export function savePins(pins: PinnedMob[]): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(pins));
  } catch {
    // Storage full or disabled: the pins live for the session and that is all.
  }
}

/** Whether a pin stands in a zone, by any of the map short names that draw the place. */
export function pinStandsIn(pin: PinnedMob, shortName: string): NpcPlace | undefined {
  return pin.places.find((p) => p.shortName === shortName || p.maps.includes(shortName));
}

/** Every map short name a pin's zones are drawn under. */
export function pinZones(pin: PinnedMob): string[] {
  const out = new Set<string>();
  for (const p of pin.places) {
    for (const m of p.maps) out.add(m);
    if (p.shortName) out.add(p.shortName);
  }
  return [...out];
}
