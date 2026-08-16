/**
 * Where to read more about a thing the log named — an item, a mob, a spell —
 * on the web, without leaving the app for a search box (issues #51, #62).
 *
 * <p>Nothing here is data about the game; it is a table of <b>where the data
 * lives</b>. The client ships no item or NPC database (see
 * `docs/domain/eq-client-files.md`), and the log names things but never
 * numbers them — on EverQuest Legends a linked item arrives in the log as
 * plain text, no `\x12` payload — so the app can only hand a <i>name</i> to a
 * site that knows the rest. Every provider is therefore a URL template over a
 * name (or an id, when a later step has learned one), and the popover that
 * uses them opens a real browser tab (ADR-009: the window only shows the app;
 * `target="_blank"` goes to the default browser).</p>
 *
 * <p><b>Worlds.</b> Which sites are right depends on which game the log came
 * from: EverQuest Legends has its own community databases whose item ids
 * match the client's, a Project 1999 character wants the P99 wiki, and live
 * wants Allakhazam. A world is a named, ordered list of providers; the first
 * that can build a URL for a kind is the default. Worlds are guessed from the
 * install the session names and can be overridden per install
 * (`lookupSettings.ts`), which is the room left for servers with other eras
 * unlocked: a new world is a new entry here and nothing else changes.</p>
 */

/** The kinds of thing a name can be looked up as. */
export type LookupKind = "item" | "npc" | "spell" | "zone";

/** What is being looked up: a display name, and an id when one is known. */
export interface LookupRef {
  kind: LookupKind;
  name: string;
  /** The game's own id for the thing, when a source has supplied one (item ids from the client's loot-filter file, later). */
  id?: number;
}

export interface LookupProvider {
  id: string;
  /** Short name for menus. */
  name: string;
  /** Home page, for the settings hint. */
  home: string;
  /** A URL for the reference, or undefined when this site cannot address it (no id, unsupported kind). */
  url: (ref: LookupRef) => string | undefined;
}

export interface LookupWorld {
  id: string;
  name: string;
  /** One line on which servers this is for. */
  hint: string;
  /** In order of preference; the first that yields a URL is the default. */
  providers: LookupProvider[];
}

/**
 * The name a site should be asked about. Legends decorates upgraded items
 * with a rank (`Fine Steel Rapier +2`) and exalted ones with a tag
 * (`Guise of the Deceiver (Exaltation)`); reference sites list the base
 * item. Loot lines keep a corpse's article (`a bandit`) which no wiki page
 * title carries.
 */
export function lookupName(name: string, kind: LookupKind): string {
  let s = name.trim();
  if (kind === "item") {
    s = s.replace(/\s\+\d+$/, "").replace(/\s\(Exaltation\)$/i, "");
  }
  if (kind === "npc") {
    s = s.replace(/^(a|an|the)\s+/i, "");
  }
  return s;
}

const enc = encodeURIComponent;

/** MediaWiki's search box: an exact title lands on the page, anything else on results. */
function mediaWiki(id: string, name: string, base: string): LookupProvider {
  return {
    id,
    name,
    home: base,
    url: (ref) => `${base}/index.php?search=${enc(lookupName(ref.name, ref.kind))}`,
  };
}

const eqlWiki = mediaWiki("eqlwiki", "EQ Legends Wiki", "https://eqlwiki.com");
const p99Wiki = mediaWiki("p99wiki", "Project 1999 Wiki", "https://wiki.project1999.com");

const gnollGuard: LookupProvider = {
  id: "gnollguard",
  name: "Gnoll Guard",
  home: "https://www.gnollguard.com",
  // The site has per-item pages at /items/<Name>, but a miss is a bare 404;
  // its search shows near matches (the +N variants) instead, so it is the
  // safer door for a name the app cannot verify.
  url: (ref) => `https://www.gnollguard.com/search?q=${enc(lookupName(ref.name, ref.kind))}`,
};

const eqlBase: LookupProvider = {
  id: "eqlbase",
  name: "EQLBase",
  home: "https://eqlbase.com",
  // Pages are addressed by id and the site's item ids are the game's own —
  // checked against the client's loot-filter file, 407 of 478 names match
  // exactly and the rest differ only in case. Its search is client-side and
  // ignores a query string, so a bare name has no door here.
  url: (ref) => {
    if (ref.id === undefined) return undefined;
    if (ref.kind === "item") return `https://eqlbase.com/items/${ref.id}/`;
    if (ref.kind === "npc") return `https://eqlbase.com/npcs/${ref.id}/`;
    if (ref.kind === "spell") return `https://eqlbase.com/spells/${ref.id}/`;
    return undefined;
  },
};

const allakhazam: LookupProvider = {
  id: "allakhazam",
  name: "Allakhazam",
  home: "https://everquest.allakhazam.com",
  url: (ref) => `https://everquest.allakhazam.com/search.html?q=${enc(lookupName(ref.name, ref.kind))}`,
};

const eqResource: LookupProvider = {
  id: "eqresource",
  name: "EQResource",
  home: "https://eqresource.com",
  url: (ref) =>
    ref.kind === "item" && ref.id !== undefined
      ? `https://items.eqresource.com/items.php?id=${ref.id}`
      : undefined,
};

const lucy: LookupProvider = {
  id: "lucy",
  name: "Lucy",
  home: "https://lucy.allakhazam.com",
  url: (ref) =>
    ref.kind === "item" && ref.id !== undefined
      ? `https://lucy.allakhazam.com/item.html?id=${ref.id}`
      : undefined,
};

export const LOOKUP_WORLDS: LookupWorld[] = [
  {
    id: "legends",
    name: "EverQuest Legends",
    hint: "The Legends community sites — EQL Wiki, Gnoll Guard, EQLBase.",
    providers: [eqlWiki, gnollGuard, eqlBase, allakhazam],
  },
  {
    id: "classic",
    name: "Classic emulator",
    hint: "Project 1999 and other classic-era servers — the P99 wiki first.",
    providers: [p99Wiki, allakhazam],
  },
  {
    id: "live",
    name: "Live and progression",
    hint: "Daybreak's live and TLP servers — Allakhazam, EQResource, Lucy.",
    providers: [allakhazam, eqResource, lucy],
  },
];

export const DEFAULT_WORLD_ID = "live";

export function worldById(id: string | undefined): LookupWorld {
  return LOOKUP_WORLDS.find((w) => w.id === id) ?? LOOKUP_WORLDS.find((w) => w.id === DEFAULT_WORLD_ID)!;
}

/**
 * The world an install most likely is, from its folder name as the server
 * reports it on the session. Legends installs under "EverQuest Legends";
 * everything else is treated as live until the user says otherwise — a
 * Project 1999 client lives in a folder named whatever the player called it,
 * so there is nothing to guess from.
 */
export function guessWorldId(install: string | undefined): string {
  if (install && /legends/i.test(install)) return "legends";
  return DEFAULT_WORLD_ID;
}

/**
 * Whether a name is a thing at all. The query engine's stand-ins — "coin" for
 * a coin drop, "Unknown" for a corpse the loot line did not name — are labels
 * for the absence of one, and a door beside them would open on nothing.
 */
export function isLookupable(ref: LookupRef): boolean {
  const name = lookupName(ref.name, ref.kind);
  if (name.length === 0) return false;
  if (ref.kind === "item" && /^coin$/i.test(name)) return false;
  if (ref.kind === "npc" && /^unknown$/i.test(name)) return false;
  return true;
}

/** Every link a world can offer for a reference, default first; empty when no site can address it. */
export function linksFor(world: LookupWorld, ref: LookupRef): { provider: LookupProvider; url: string }[] {
  const out: { provider: LookupProvider; url: string }[] = [];
  if (!isLookupable(ref)) return out;
  for (const provider of world.providers) {
    const url = provider.url(ref);
    if (url) out.push({ provider, url });
  }
  return out;
}
