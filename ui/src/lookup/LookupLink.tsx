import { useState, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent } from "react";
import { IconExternalLink } from "@tabler/icons-react";
import { looksLikeNpc, lookupName, type LookupKind, type LookupRef } from "./providers";
import { useLookupActions, useLookupMenuOpenFor } from "./lookupMenu";

interface Props {
  kind: LookupKind;
  name: string;
  /** The game's id for the thing, when known — unlocks the id-addressed sites. */
  id?: number;
  /**
   * Render as a span rather than a button: for a door inside something that
   * is already a button (a fight row), where a nested button is invalid HTML
   * and the browsers disagree about which one a click means.
   */
  inline?: boolean;
}

/**
 * The little "look this up" door beside a name (issues #51, #62): an arrow
 * that shows when the row is pointed at. A plain click opens the site you
 * usually want, straight away, in a real browser tab (the shell hands new
 * windows to the default browser, ADR-009; the app itself never navigates).
 * A right-click opens the menu of every site for this log's world — see
 * `lookupMenu.tsx`, which owns the menu, the stars, and the item-id cache;
 * this is only a trigger for it, the same one a chart label is.
 *
 * <p>Two gestures rather than one, at the owner's ask once the menu was in
 * hand: the site you use is the same one nearly every time, and a click on
 * the door followed by a click on the wiki is the alt-tab this was meant to
 * remove. The menu is still there for the exception (one site has the
 * quest, another the drop rate) and for saying which is which.</p>
 *
 * <p>An item's id is not known to the caller — the log never numbers items
 * — so the door asks the session's registry (F29) for the name on hover,
 * ahead of the click, through the shared cache. Nothing is asked for a row
 * that is never pointed at.</p>
 *
 * <p>Renders nothing when no site can address the reference, so a column
 * never carries a dead arrow.</p>
 */
export function LookupLink({ kind, name, id, inline = false }: Props) {
  const { go, menu, prefetch, canLookup } = useLookupActions();
  const ref: LookupRef = { kind, name, id };
  const open = useLookupMenuOpenFor(ref);
  // Re-render once an id lands, so a click that follows can use it.
  const [, gotId] = useState(0);

  if (!canLookup(ref)) return null;

  const stop = (e: MouseEvent | ReactKeyboardEvent) => {
    // The row underneath has its own click and hover behaviour; this is not that.
    e.stopPropagation();
    e.preventDefault();
  };
  const at = (e: MouseEvent | ReactKeyboardEvent) => {
    const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
    return { x: r.left, y: r.bottom + 4 };
  };
  const onGo = (e: MouseEvent | ReactKeyboardEvent) => {
    stop(e);
    go(ref, at(e));
  };
  const onMore = (e: MouseEvent | ReactKeyboardEvent) => {
    stop(e);
    menu(ref, at(e));
  };
  const onKey = (e: ReactKeyboardEvent) => {
    // Enter goes; Shift+Enter, Space and the context-menu key open the menu.
    if (e.key === "Enter" && !e.shiftKey) onGo(e);
    else if (e.key === "Enter" || e.key === " " || e.key === "ContextMenu") onMore(e);
  };
  const warm = () => prefetch(ref, () => gotId((n) => n + 1));

  const shown = lookupName(name, kind);
  const kindLabel = kind === "npc" ? "mob" : kind;
  const doorProps = {
    className: "lookup-btn" + (open ? " on" : ""),
    title: `Look up this ${kindLabel} · right-click for other sites`,
    "aria-label": `Look up ${shown}`,
    "aria-haspopup": "menu" as const,
    "aria-expanded": open,
    onClick: onGo,
    onContextMenu: onMore,
    onMouseEnter: warm,
    onFocus: warm,
    onKeyDown: onKey,
  };
  const glyph = <IconExternalLink size={12} stroke={2} aria-hidden />;

  return inline ? (
    <span role="button" tabIndex={0} {...doorProps}>
      {glyph}
    </span>
  ) : (
    <button type="button" {...doorProps}>
      {glyph}
    </button>
  );
}

/**
 * The lookup kind a table row names, from its dimension and its value, or
 * null when it names a player (their own characters, whom no wiki lists).
 * The dimension says what the column is *for*; the value settles what a
 * particular row is, because several sources put mobs and players in one
 * column: the death source's `player` is the victim, which is as often a
 * mob as a character; healing's `target` is an ally. Loot's `spell` column
 * is the item looted — the query engine puns the two.
 */
export function lookupKindFor(source: string, dimension: string | undefined, label: string): LookupKind | null {
  switch (dimension) {
    case "spell":
      // Spells are a kind the providers know, but a damage table's `spell`
      // column is as often "Kick" or "Bash" as a spell, and an arrow beside
      // every melee row is noise; spell lookup waits for a surface that
      // lists real spells.
      return source === "loot" ? "item" : null;
    case "target":
      // The far side of the source: the mob hit or looted from — or, for
      // healing, the ally healed, and for deaths, the killer, who is a mob
      // only when the name says so (a mob's killer is the raid).
      return source === "healing" || source === "deaths" ? (looksLikeNpc(label) ? "npc" : null) : "npc";
    case "player":
    case "character":
      // A player's name is one word; a value with an article or a second
      // word is a mob in a player-shaped column (a death's victim, say).
      return looksLikeNpc(label) ? "npc" : null;
    default:
      return null;
  }
}
