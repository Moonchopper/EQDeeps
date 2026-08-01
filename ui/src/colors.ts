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
 */
export interface EntityColors {
  /** Assign-or-return: gives the entity a palette slot if one is free. */
  claim(key: string): string;

  /** Return the entity's color if it has one; neutral gray otherwise. */
  lookup(key: string): string;
}

export function createEntityColors(): EntityColors {
  const assigned = new Map<string, string>();
  return {
    claim(key: string): string {
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
    lookup(key: string): string {
      return assigned.get(key) ?? OTHER_COLOR;
    },
  };
}
