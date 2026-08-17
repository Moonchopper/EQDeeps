/**
 * The Bestiary ↔ Map trail (F30 × F27): a mob page can open the zone it
 * stands in, a zone can open the mobs that stand there, and each hop leaves a
 * crumb behind so the way back is a click rather than a search. Nothing here
 * is persisted — a trail is one train of thought, and it ends when the rail is
 * used to go somewhere else.
 */

/** What to open on the Bestiary: a listing by id, or failing that a name. */
export interface BestiaryTarget {
  name: string;
  /** A particular listing — this mob, in this zone — when the caller knows one. */
  id?: number;
  /** Bumped on every ask so asking for the same mob twice reopens it. */
  seq: number;
}

/** A mob's spawn points, for drawing on the zone that was opened for it. */
export interface SpawnOverlay {
  mob: string;
  /** [x, y, z] as the site gives them — game coordinates; the canvas negates both axes. */
  points: number[][];
}

/** What to open on the Map: a place by name, on a particular drawing when known. */
export interface MapTarget {
  place: string;
  /** The map short name to open on when the caller knows one; otherwise the place's usual. */
  shortName?: string;
  spawn?: SpawnOverlay;
  seq: number;
}

/** One step back: the view you were on and what to reopen there. */
export interface Crumb {
  view: "bestiary" | "map";
  label: string;
  bestiary?: Omit<BestiaryTarget, "seq">;
  map?: Omit<MapTarget, "seq">;
}
