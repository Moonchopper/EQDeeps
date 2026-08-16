import { useContext, useEffect, useState } from "react";
import { api } from "../api";
import { guessWorldId, worldById, type LookupWorld } from "./providers";
import { LookupScopeContext } from "./LookupScope";

/**
 * Which world's reference sites a log should open (see `providers.ts`),
 * remembered per installation the same way map choices are (`mapSettings.ts`):
 * the sites that know about a Legends item are a fact about the game the log
 * came from, and one machine can hold a Legends install beside a live one and
 * a P99 client. Nothing is written until the user picks; until then the world
 * is guessed from the install's name on every read, so a Legends log opens
 * Legends sites out of the box and a guess that later improves is not frozen
 * into a file.
 *
 * <p>Lives in the `ui-settings` document — the store slot allowed since the
 * document store was built and, until this, empty. Nested under `lookup` so
 * the next preference that belongs with the game rather than the machine has
 * a sibling to sit beside instead of a new store key.</p>
 */
export interface LookupSettings {
  /** The world for logs whose install is unknown (a log copied out of its game folder). */
  world?: string;
  /** Per-install choices, keyed by the install's folder name. */
  installs?: Record<string, { world?: string }>;
  /**
   * The site a plain click opens, per world (provider id by world id). Kept
   * per world rather than per install because it is a preference about the
   * sites, not about the game: someone who likes Gnoll Guard likes it for
   * every Legends install they have.
   */
  defaults?: Record<string, string>;
}

/** Whether the Bestiary may reach a reference site at all (ADR-020). */
export interface ReferenceSettings {
  enabled?: boolean;
}

export interface UiSettingsDocument {
  lookup?: LookupSettings;
  reference?: ReferenceSettings;
  /* Other UI preferences may join later; unknown fields are carried through the read-modify-write below. */
  [key: string]: unknown;
}

const KEY = "ui-settings";

let cached: UiSettingsDocument | null = null;
let loading: Promise<UiSettingsDocument> | null = null;
const listeners = new Set<() => void>();

function load(): Promise<UiSettingsDocument> {
  if (cached) return Promise.resolve(cached);
  loading ??= api
    .getStore<UiSettingsDocument>(KEY)
    .then((doc) => {
      cached = doc ?? {};
      return cached;
    })
    .catch(() => {
      cached = {};
      return cached;
    });
  return loading;
}

function notify(): void {
  for (const l of listeners) l();
}

/** The world chosen for an install (or the flat fallback), else undefined when nothing was ever chosen. */
export function chosenWorldId(settings: LookupSettings | undefined, install?: string): string | undefined {
  return (install ? settings?.installs?.[install]?.world : undefined) ?? settings?.world;
}

/**
 * Remembers a world for an install (or forgets it with null). Read-modify-write
 * against the stored document, not React state: `ui-settings` is shared with
 * whatever else comes to live there, and a blind PUT of one tenant's view
 * would drop the others.
 */
export async function rememberWorld(worldId: string | null, install?: string): Promise<void> {
  const current = await api.getStore<UiSettingsDocument>(KEY).catch(() => null);
  const next: UiSettingsDocument = { ...(current ?? {}) };
  const lookup: LookupSettings = { ...(next.lookup ?? {}), installs: { ...(next.lookup?.installs ?? {}) } };
  if (install) {
    if (worldId) {
      lookup.installs![install] = { world: worldId };
    } else {
      delete lookup.installs![install];
    }
  } else if (worldId) {
    lookup.world = worldId;
  } else {
    delete lookup.world;
  }
  next.lookup = lookup;
  cached = next;
  notify();
  await api.putStore(KEY, next);
}

/**
 * Whether mob details may be fetched. On by default — the Bestiary is worth
 * having and the request carries nothing about the player (ADR-020) — but a
 * switch, because "no cloud" is a promise this app makes and a promise with
 * no off switch is a slogan. Off means the view asks nothing of anyone.
 */
export function useReferenceEnabled(): { enabled: boolean; ready: boolean } {
  const [, bump] = useState(0);
  useEffect(() => {
    const l = () => bump((n) => n + 1);
    listeners.add(l);
    if (!cached) void load().then(l);
    return () => {
      listeners.delete(l);
    };
  }, []);
  return { enabled: cached?.reference?.enabled !== false, ready: cached !== null };
}

/** Remembers whether mob details may be fetched. Read-modify-write, as above. */
export async function rememberReferenceEnabled(enabled: boolean): Promise<void> {
  const current = await api.getStore<UiSettingsDocument>(KEY).catch(() => null);
  const next: UiSettingsDocument = { ...(current ?? {}), reference: { enabled } };
  cached = next;
  notify();
  await api.putStore(KEY, next);
}

/** The site a plain click opens in a world, if the user has said; else undefined and the world's first is used. */
export function preferredProviderId(settings: LookupSettings | undefined, worldId: string): string | undefined {
  return settings?.defaults?.[worldId];
}

/** Remembers which site a plain click opens in a world (or forgets it with null). Read-modify-write, as above. */
export async function rememberDefaultProvider(worldId: string, providerId: string | null): Promise<void> {
  const current = await api.getStore<UiSettingsDocument>(KEY).catch(() => null);
  const next: UiSettingsDocument = { ...(current ?? {}) };
  const lookup: LookupSettings = { ...(next.lookup ?? {}), defaults: { ...(next.lookup?.defaults ?? {}) } };
  if (providerId) {
    lookup.defaults![worldId] = providerId;
  } else {
    delete lookup.defaults![worldId];
  }
  next.lookup = lookup;
  cached = next;
  notify();
  await api.putStore(KEY, next);
}

/**
 * The world an install's links should use, and whether that came from the
 * user or a guess — the settings row says which, so "why is this opening the
 * P99 wiki" has an answer on the screen. Also the site a plain click opens
 * in that world, when the user has picked one.
 */
export function useLookupWorld(installOverride?: string): {
  world: LookupWorld;
  chosen: boolean;
  ready: boolean;
  install?: string;
  /** The user's preferred site for this world, if any. */
  preferredId?: string;
} {
  // The install comes from the nearest LookupScope unless a caller knows better.
  const scoped = useContext(LookupScopeContext);
  const install = installOverride ?? scoped.install;
  const [, bump] = useState(0);
  useEffect(() => {
    const l = () => bump((n) => n + 1);
    listeners.add(l);
    if (!cached) void load().then(l);
    return () => {
      listeners.delete(l);
    };
  }, []);
  const chosen = chosenWorldId(cached?.lookup, install);
  const world = worldById(chosen ?? guessWorldId(install));
  return {
    world,
    chosen: chosen !== undefined,
    ready: cached !== null,
    install,
    preferredId: preferredProviderId(cached?.lookup, world.id),
  };
}
