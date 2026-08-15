import { api } from "../api";

/**
 * The user's corrections to what the app worked out for itself (F27):
 * which map to draw for a given zone, and where the maps live.
 *
 * <p>These are corrections rather than preferences, which is why they live in
 * the document store beside dashboards instead of in a cache. The zone table
 * ships 264 hand-checked and deduced rows and is knowingly incomplete and
 * knowingly fallible — ADR-016 promises the user an escape hatch, and this is
 * it. A choice made here outranks anything the table says.</p>
 */
export interface MapSettings {
  /** Set by the server when the user nominates a maps folder. */
  root?: string;
  /** Normalized zone name → map short name. */
  chosen?: Record<string, string>;
}

/**
 * Strips an instance's difficulty suffix — "The Estate of Unrest 4 (Refined)"
 * is the same geometry as the open-world zone.
 *
 * <p>Mirrors <c>InstanceZone.Parse</c>: the number is capped at two digits and
 * the tier word at letters, so a zone legitimately ending in a parenthetical is
 * not mistaken for an instance.</p>
 */
function stripInstance(zone: string): string {
  const match = /^(.+?) (\d{1,2}) \(([A-Za-z][A-Za-z ]*)\)$/.exec(zone.trim());
  return match ? match[1] : zone.trim();
}

/**
 * The key a zone is remembered under.
 *
 * <p>Deliberately the same rule as `ZoneTable.Normalize` on the server —
 * lowercase, drop everything but letters and digits, drop a leading "the" — so
 * that a choice made against "The Ocean of Tears" is found again when
 * something asks about "Ocean of Tears". If the two ever disagree, overrides
 * silently stop applying, which is why they are written out identically rather
 * than one calling the other.</p>
 */
export function zoneKey(zone: string): string {
  const bare = stripInstance(zone)
    .toLowerCase()
    .replace(/[^a-z0-9]/g, "");

  return bare.startsWith("the") && bare.length > 3 ? bare.slice(3) : bare;
}

export async function loadMapSettings(): Promise<MapSettings> {
  return (await api.getStore<MapSettings>("map-settings")) ?? {};
}

/** The map the user chose for this zone, if they chose one. */
export function chosenFor(settings: MapSettings, zone: string): string | undefined {
  return settings.chosen?.[zoneKey(zone)];
}

/**
 * Remembers a choice, or forgets it when `shortName` is null.
 *
 * <p>Read-modify-write against the live document rather than against React
 * state: the server writes `root` into the same document, and a blind PUT of
 * local state would drop whichever the other side set most recently. Returns
 * the merged result so the caller can hold it without re-reading.</p>
 */
export async function rememberMap(
  zone: string,
  shortName: string | null,
): Promise<MapSettings> {
  const current = await loadMapSettings();
  const chosen = { ...(current.chosen ?? {}) };
  const key = zoneKey(zone);

  if (shortName) {
    chosen[key] = shortName;
  } else {
    delete chosen[key];
  }

  const next: MapSettings = { ...current, chosen };
  await api.putStore("map-settings", next);
  return next;
}
