import {
  HITS_VIEW,
  MAPS_VIEW,
  BESTIARY_VIEW,
  RAID_TARGETS_VIEW,
  SLAYER_VIEW,
  STANCES_VIEW_ID,
  SUMMARY_VIEW,
} from "./standardViews";

/**
 * How the shipped views are grouped in the nav rail (ADR-017). A group is
 * named for the question its views answer, and it decides the furniture its
 * views get: `framed` says whether the app-wide time frame applies, which is
 * what shows or hides the fight list and the header's time controls.
 *
 * Every entry names a view id: the hand-built Summary, a standard-view
 * dashboard from `standardViews()`, or one of the derived-index views (Mobs,
 * Incoming, Map). The rail resolves the standard ones against what this log
 * actually has — Stances is conditional — so a listed id can render nothing.
 */
export interface RailGroup {
  key: string;
  label: string;
  /** Whether the time frame — fight list, range, window — applies here. */
  framed: boolean;
  ids: string[];
}

export const RAIL_GROUPS: RailGroup[] = [
  {
    // What happened in the fight.
    key: "combat",
    label: "Combat",
    framed: true,
    ids: [
      SUMMARY_VIEW,
      "preset-healing",
      "preset-tanking",
      STANCES_VIEW_ID,
      HITS_VIEW,
    ],
  },
  {
    // What happened to this character over the life of the log.
    key: "character",
    label: "Character",
    framed: true,
    ids: ["preset-experience", "preset-faction", "preset-loot"],
  },
  {
    // What this server's world is worth, learned across every log ever
    // opened on it. Nothing here reports over a time frame: the Bestiary
    // reads a server-wide index, Map reads a folder on disk, Slayer reads a
    // file the game itself wrote, and Raid targets reads the whole log rather
    // than a selection of it (F31 — "have I ever killed this" is a lifetime
    // question). None of the four is a QuerySpec.
    key: "world",
    label: "World",
    framed: false,
    ids: [BESTIARY_VIEW, SLAYER_VIEW, MAPS_VIEW, RAID_TARGETS_VIEW],
  },
];

/**
 * Whether the time frame applies on a shipped view. Unknown ids — a stale
 * remembered view, say — count as framed, because hiding a control the user
 * might need is the worse mistake.
 */
export function isFramedView(id: string): boolean {
  const group = RAIL_GROUPS.find((g) => g.ids.includes(id));
  return group ? group.framed : true;
}
