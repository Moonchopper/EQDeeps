import { api } from "../api";

/**
 * What a player has said about the world on one installation of the game:
 * which drawing a place opens on, and how far its world is unlocked.
 */
export interface InstallMapSettings {
  /** Normalized zone name → map short name. */
  chosen?: Record<string, string>;
  /**
   * The expansion this install's server has reached, as a `ZoneEra.id`, or
   * absent for "any". Chosen by the player and never inferred: nothing in a
   * log or a map file says what a server has unlocked (issue #57).
   */
  era?: string;
}

/**
 * The user's corrections to what the app worked out for itself (F27):
 * which map to draw for a given zone, how far the world is unlocked, and
 * where the maps live.
 *
 * <p>These are corrections rather than preferences, which is why they live in
 * the document store beside dashboards instead of in a cache. The zone table
 * ships 268 hand-checked and deduced rows and is knowingly incomplete and
 * knowingly fallible — ADR-016 promises the user an escape hatch, and this is
 * it. A choice made here outranks anything the table says.</p>
 *
 * <p><b>Keyed by installation.</b> Which drawing of Freeport is right and how
 * far the world is unlocked are facts about the game a log comes from — an
 * EverQuest Legends install runs a classic world with the old Freeport, live
 * has the revamps and everything, a Project 1999 client its own era — and one
 * machine can hold several. The shard in the log's file name ("qeynos") is a
 * finer cut than that: every server on one install shares its client and its
 * era, so the install is the key, named by its folder as the server reports
 * it on the session. The choices live under `installs[<install>]`, and the
 * flat `chosen` and `era` at the top are the layer underneath: read as the
 * fallback for any install, written to only when none is known (a log copied
 * out of its game folder names no install). That is also the migration — a
 * file from before there were installs keeps working as-is, and new choices
 * go under the install without touching it.</p>
 */
export interface MapSettings extends InstallMapSettings {
  /** Set by the server when the user nominates a maps folder. Machine-level: it is where the files are. */
  root?: string;
  /** Per-install choices, keyed by the install's folder name. */
  installs?: Record<string, InstallMapSettings>;
  /**
   * The install most recently written to, so the Map tab opened with no log
   * still shows the world the way you last set it rather than a blank slate.
   */
  lastInstall?: string;
}

/**
 * Strips an instance's difficulty suffix — "The Estate of Unrest 4 (Refined)"
 * is the same geometry as the open-world zone.
 *
 * <p>Mirrors <c>InstanceZone.Parse</c>: the number is capped at two digits and
 * the tier word at letters, so a zone legitimately ending in a parenthetical is
 * not mistaken for an instance.</p>
 */
export function stripInstance(zone: string): string {
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

/**
 * The install a read or write is about: the one given, else the one last
 * written to, else none — in which case the flat machine-level fields serve.
 */
function scopeOf(settings: MapSettings, install?: string): string | undefined {
  return install || settings.lastInstall || undefined;
}

/** The map the user chose for this zone, if they chose one — on this install, else anywhere. */
export function chosenFor(settings: MapSettings, zone: string, install?: string): string | undefined {
  const key = zoneKey(zone);
  const scope = scopeOf(settings, install);
  return (scope ? settings.installs?.[scope]?.chosen?.[key] : undefined) ?? settings.chosen?.[key];
}

/** The era the user set — for this install, else the machine-level one. */
export function eraFor(settings: MapSettings, install?: string): string | undefined {
  const scope = scopeOf(settings, install);
  return (scope ? settings.installs?.[scope]?.era : undefined) ?? settings.era;
}

/**
 * Applies a change to the right layer and returns the new document.
 *
 * <p>With an install, the change lands under that install and it becomes
 * the last install; without one, under the flat fields. A forget is applied
 * to both layers, because a binding that came from the layer underneath would
 * otherwise still show through — "forget" has to mean gone.</p>
 */
function apply(
  current: MapSettings,
  install: string | undefined,
  change: (layer: InstallMapSettings) => void,
  forget: boolean,
): MapSettings {
  const next: MapSettings = { ...current, chosen: { ...(current.chosen ?? {}) } };
  const scope = scopeOf(current, install);

  if (scope) {
    const installs = { ...(next.installs ?? {}) };
    const layer: InstallMapSettings = {
      ...(installs[scope] ?? {}),
      chosen: { ...(installs[scope]?.chosen ?? {}) },
    };
    change(layer);
    installs[scope] = layer;
    next.installs = installs;
    next.lastInstall = scope;
    if (forget) {
      change(next);
    }
  } else {
    change(next);
  }

  return next;
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
  install?: string,
): Promise<MapSettings> {
  const key = zoneKey(zone);
  const next = apply(
    await loadMapSettings(),
    install,
    (layer) => {
      layer.chosen ??= {};
      if (shortName) {
        layer.chosen[key] = shortName;
      } else {
        delete layer.chosen[key];
      }
    },
    shortName === null,
  );

  await api.putStore("map-settings", next);
  return next;
}

/**
 * Remembers which expansion this install's world has reached, or forgets it
 * when null. Read-modify-write for the same reason as `rememberMap`.
 */
export async function rememberEra(era: string | null, install?: string): Promise<MapSettings> {
  const next = apply(
    await loadMapSettings(),
    install,
    (layer) => {
      if (era) {
        layer.era = era;
      } else {
        delete layer.era;
      }
    },
    era === null,
  );

  await api.putStore("map-settings", next);
  return next;
}
