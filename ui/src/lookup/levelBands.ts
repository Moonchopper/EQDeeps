/** A slice of the level range the reference index is browsed by. */
export interface LevelBand {
  label: string;
  min: number;
  max?: number;
}

/**
 * The bands the index is browsed by — the Bestiary's chips and the World's.
 * Uneven on purpose: the world is.
 */
export const LEVEL_BANDS: LevelBand[] = [
  { label: "1–9", min: 1, max: 9 },
  { label: "10–19", min: 10, max: 19 },
  { label: "20–29", min: 20, max: 29 },
  { label: "30–39", min: 30, max: 39 },
  { label: "40–49", min: 40, max: 49 },
  { label: "50–59", min: 50, max: 59 },
  { label: "60+", min: 60 },
];

/** The band a level falls in, or undefined for no level. */
export function bandOf(level: number | undefined): LevelBand | undefined {
  if (level === undefined || !Number.isFinite(level)) return undefined;
  return LEVEL_BANDS.find((b) => level >= b.min && (b.max === undefined || level <= b.max));
}

/** "L5–14", or "L40" when the ends meet, or "" with no low end. */
export function levelSpan(low: number | undefined, high: number | undefined): string {
  if (low === undefined) return "";
  return high !== undefined && high !== low ? `L${low}–${high}` : `L${low}`;
}
