import type { Dimension, QuerySource } from "./api";
import { OTHER_COLOR, SERIES_COLORS } from "./format";

/**
 * Per-session entity→color registry: the mechanism behind "color follows the
 * entity, never its rank" ACROSS panels. The first entity to claim gets slot 1,
 * and keeps it for the session — in the DPS lines, the meter bars, the stacked
 * ability segments, and the table row tints alike. Entities beyond the eight
 * validated slots are neutral gray rather than cycled hues (a cycled 9th hue
 * would collide with a real series inside a single chart).
 *
 * Every ranked consumer (meter ticks, charts, summary rows) claims in
 * total-descending order over the same data, so the prominent actors converge
 * on the same slots regardless of which panel renders first.
 *
 * Slots are handed out per POOL. There used to be one pool for everything, and
 * on any real log the players and their pets claimed all eight of its slots
 * before you ever opened a tab grouped by something else — so stances,
 * factions and items arrived to an exhausted palette and rendered entirely
 * gray. Pools fix that without weakening the guarantee that made a single
 * registry attractive: color still follows the entity everywhere it appears.
 * A stance and a player were never going to share a chart, so there was never
 * anything for them to collide over.
 */
export interface EntityColors {
  /** Assign-or-return: gives the entity a palette slot in `pool` if one is free. */
  claim(key: string, pool?: string): string;

  /** Return the entity's color if it has one; neutral gray otherwise. */
  lookup(key: string, pool?: string): string;
}

/** The pool whose keys are people — players, their pets, NPCs. */
export const ENTITY_POOL = "entity";

/**
 * Sources whose row keys name a person. These share {@link ENTITY_POOL}, which
 * is what carries a player's color from the damage table to the healing chart
 * to the live meter.
 */
const ENTITY_SOURCES = new Set<QuerySource>([
  "damage",
  "healing",
  "tanking",
  "casts",
  "deaths",
]);

/**
 * Which pool a panel's row keys draw from, given what it is grouped by.
 *
 * `player`/`target`/`character` name a person only on the combat sources. On
 * the progression sources the very same dimensions name something else
 * entirely — a faction, a corpse, a conned mob — so they get a pool of their
 * own rather than competing for the palette that players are using.
 */
export function colorPoolFor(source: QuerySource, dim: Dimension | undefined): string {
  if (dim === "spell" || dim === "damageType" || dim === "stance") {
    return dim;
  }

  return ENTITY_SOURCES.has(source) ? ENTITY_POOL : source;
}

export function createEntityColors(): EntityColors {
  const pools = new Map<string, Map<string, string>>();
  const slots = (pool: string) => {
    let assigned = pools.get(pool);
    if (!assigned) {
      assigned = new Map();
      pools.set(pool, assigned);
    }
    return assigned;
  };

  return {
    claim(key: string, pool = ENTITY_POOL): string {
      const assigned = slots(pool);
      const existing = assigned.get(key);
      if (existing) {
        return existing;
      }
      if (assigned.size < SERIES_COLORS.length) {
        const color = SERIES_COLORS[assigned.size];
        assigned.set(key, color);
        return color;
      }
      return OTHER_COLOR;
    },
    lookup(key: string, pool = ENTITY_POOL): string {
      return slots(pool).get(key) ?? OTHER_COLOR;
    },
  };
}
