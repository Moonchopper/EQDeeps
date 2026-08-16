import {
  useContext,
  useEffect,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type MouseEvent,
  type RefObject,
} from "react";
import { createPortal } from "react-dom";
import { IconExternalLink, IconStar, IconStarFilled } from "@tabler/icons-react";
import { api } from "../api";
import { defaultLinkFor, linksFor, lookupName, type LookupKind } from "./providers";
import { rememberDefaultProvider, useLookupWorld } from "./lookupSettings";
import { LookupScopeContext } from "./LookupScope";

interface Props {
  kind: LookupKind;
  name: string;
  /** The game's id for the thing, when known — unlocks the id-addressed sites. */
  id?: number;
  /** The install the log is from, when the caller knows better than the enclosing LookupScope. */
  install?: string;
  /**
   * Render as a span rather than a button: for a door inside something that
   * is already a button (a fight row), where a nested button is invalid HTML
   * and the browsers disagree about which one a click means.
   */
  inline?: boolean;
}

/**
 * Item ids resolved so far, per session and name key, shared by every door
 * on the page: a hover over a column of two hundred items asks about each
 * once, and the answer is there for the click.
 */
const resolved = new Map<string, number | null>();
const resolving = new Map<string, Promise<number | null>>();

function resolveId(sessionId: string, name: string): Promise<number | null> {
  const key = `${sessionId}|${lookupName(name, "item").toLowerCase()}`;
  const known = resolved.get(key);
  if (known !== undefined) return Promise.resolve(known);
  let pending = resolving.get(key);
  if (!pending) {
    pending = api
      .resolveItem(sessionId, name)
      .then((record) => record?.id ?? null)
      .catch(() => null)
      .then((value) => {
        // A miss is not remembered: the registry learns ids as the player
        // loots and sets filters, and the next hover should see that.
        if (value !== null) resolved.set(key, value);
        resolving.delete(key);
        return value;
      });
    resolving.set(key, pending);
  }
  return pending;
}

/**
 * The little "look this up" door beside a name (issues #51, #62): an arrow
 * that shows when the row is pointed at. A plain click opens the site you
 * usually want, straight away, in a real browser tab (the shell hands new
 * windows to the default browser, ADR-009; the app itself never navigates).
 * A right-click opens the menu of every site for this log's world, each a
 * link, each with a star to make it the one a plain click opens from then on
 * — per world, so the choice made on one Legends item holds for every mob
 * and item in every Legends log.
 *
 * <p>Two gestures rather than one, at the owner's ask once the menu was in
 * hand: the site you use is the same one nearly every time, and a click on
 * the door followed by a click on the wiki is the alt-tab this was meant to
 * remove. The menu is still there for the exception (one site has the
 * quest, another the drop rate) and for saying which is which.</p>
 *
 * <p>An item's id is not known to the caller — the log never numbers items
 * — so the door asks the session's registry (F29) for the name on hover,
 * ahead of the click, and again on open; the answer is cached across doors.
 * The id-addressed sites (EQLBase) can then be the default and a click lands
 * on the exact item page; for an item nobody has numbered the click falls
 * through to the first site that takes a name. Nothing is asked for a row
 * that is never pointed at.</p>
 *
 * <p>Renders nothing when no site can address the reference, so a column
 * never carries a dead arrow.</p>
 */
export function LookupLink({ kind, name, id, install, inline = false }: Props) {
  const { world, preferredId } = useLookupWorld(install);
  const { sessionId } = useContext(LookupScopeContext);
  const [open, setOpen] = useState<{ x: number; y: number } | null>(null);
  const [resolvedId, setResolvedId] = useState<number | undefined>(undefined);
  const button = useRef<HTMLElement>(null);
  const menu = useRef<HTMLDivElement>(null);

  const effectiveId = id ?? resolvedId;
  const ref = { kind, name, id: effectiveId };
  const links = linksFor(world, ref);
  const primary = defaultLinkFor(world, ref, preferredId);

  // Ask for the id ahead of need: on hover, and on open. Whichever comes first.
  const wantId = kind === "item" && effectiveId === undefined && !!sessionId;
  const prefetch = () => {
    if (!wantId) return;
    void resolveId(sessionId!, name).then((value) => {
      if (value !== null) setResolvedId(value);
    });
  };
  useEffect(() => {
    if (!open) return;
    const onDown = (e: Event) => {
      const t = e.target as Node;
      if (menu.current?.contains(t) || button.current?.contains(t)) return;
      setOpen(null);
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(null);
    // Any scroll moves the anchor out from under a fixed menu; close rather than drift.
    const onScroll = () => setOpen(null);
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    document.addEventListener("scroll", onScroll, true);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("scroll", onScroll, true);
    };
  }, [open]);

  if (links.length === 0) return null;

  const stop = (e: MouseEvent | ReactKeyboardEvent) => {
    // The row underneath has its own click and hover behaviour; this is not that.
    e.stopPropagation();
    e.preventDefault();
  };

  const openMenu = () => {
    prefetch();
    const r = button.current!.getBoundingClientRect();
    // Below and left-aligned to the arrow; nudged back inside the viewport when
    // the row sits near the right edge, where a menu would otherwise clip.
    const width = 260;
    const x = Math.min(r.left, window.innerWidth - width - 8);
    setOpen({ x: Math.max(8, x), y: r.bottom + 4 });
  };

  /** Plain click: straight to the usual site. Synchronous, so a browser's popup rules see a user gesture. */
  const go = (e: MouseEvent | ReactKeyboardEvent) => {
    stop(e);
    if (open) {
      setOpen(null);
      return;
    }
    if (primary) {
      window.open(primary.url, "_blank", "noopener,noreferrer");
    } else {
      openMenu();
    }
  };

  /** Right-click: the menu of every site, and the stars. */
  const more = (e: MouseEvent | ReactKeyboardEvent) => {
    stop(e);
    if (open) {
      setOpen(null);
    } else {
      openMenu();
    }
  };

  const shown = lookupName(name, kind);
  const kindLabel = kind === "npc" ? "mob" : kind;

  const doorProps = {
    className: "lookup-btn" + (open ? " on" : ""),
    title: primary
      ? `Open on ${primary.provider.name} · right-click for other sites`
      : `Look up this ${kindLabel}`,
    "aria-label": `Look up ${shown}`,
    "aria-haspopup": "menu" as const,
    "aria-expanded": open !== null,
    onClick: go,
    onContextMenu: more,
    onMouseEnter: prefetch,
    onFocus: prefetch,
  };
  const glyph = <IconExternalLink size={12} stroke={2} aria-hidden />;
  const onKey = (e: ReactKeyboardEvent) => {
    // Enter goes; Shift+Enter, Space and the context-menu key open the menu.
    if (e.key === "Enter" && !e.shiftKey) go(e);
    else if (e.key === "Enter" || e.key === " " || e.key === "ContextMenu") more(e);
  };

  return (
    <>
      {inline ? (
        <span
          ref={button as RefObject<HTMLSpanElement>}
          role="button"
          tabIndex={0}
          onKeyDown={onKey}
          {...doorProps}
        >
          {glyph}
        </span>
      ) : (
        <button ref={button as RefObject<HTMLButtonElement>} type="button" onKeyDown={onKey} {...doorProps}>
          {glyph}
        </button>
      )}
      {open &&
        createPortal(
          <div
            ref={menu}
            className="lookup-menu"
            role="menu"
            style={{ left: open.x, top: open.y }}
            onClick={(e) => e.stopPropagation()}
            onContextMenu={(e) => e.preventDefault()}
          >
            <div className="lookup-head">
              <span className="lookup-name">{shown}</span>
              <span className="lookup-kind">{kindLabel}</span>
            </div>
            {links.map(({ provider, url }) => {
              const isDefault = primary?.provider.id === provider.id;
              const isPreferred = preferredId === provider.id;
              return (
                <div key={provider.id} className={"lookup-item" + (isDefault ? " default" : "")} role="none">
                  <a
                    role="menuitem"
                    href={url}
                    target="_blank"
                    rel="noreferrer"
                    onClick={() => setOpen(null)}
                    title={`Open ${shown} on ${provider.name}`}
                  >
                    <span>{provider.name}</span>
                    {isDefault && <span className="lookup-tag">click opens</span>}
                    <IconExternalLink size={12} stroke={2} aria-hidden />
                  </a>
                  <button
                    type="button"
                    className={"lookup-star" + (isPreferred ? " on" : "")}
                    title={
                      isPreferred
                        ? `A plain click opens ${provider.name} for ${world.name} — click to forget`
                        : `Make ${provider.name} what a plain click opens for ${world.name}`
                    }
                    aria-label={`Make ${provider.name} the default site`}
                    aria-pressed={isPreferred}
                    onClick={(e) => {
                      e.stopPropagation();
                      void rememberDefaultProvider(world.id, isPreferred ? null : provider.id);
                    }}
                  >
                    {isPreferred ? (
                      <IconStarFilled size={12} aria-hidden />
                    ) : (
                      <IconStar size={12} stroke={2} aria-hidden />
                    )}
                  </button>
                </div>
              );
            })}
            <div className="lookup-foot">
              Click the arrow to open the starred site · right-click for this menu · sites for {world.name}
            </div>
          </div>,
          document.body,
        )}
    </>
  );
}

/**
 * The lookup kind a table dimension names, or null when the rows are players
 * (their own characters, whom no wiki lists). Loot's `spell` column is the
 * item looted — the query engine puns the two (see `QueryEngine`'s loot
 * dimension resolution) — and `target` is the far side of whatever the source
 * is: the mob hit, healed through, or looted from.
 */
export function lookupKindFor(source: string, dimension: string | undefined): LookupKind | null {
  if (dimension === "target") return "npc";
  // Spells are a kind the providers know, but a damage table's `spell` column
  // is as often "Kick" or "Bash" as a spell, and an arrow beside every melee
  // row is noise; spell lookup waits for a surface that lists real spells.
  if (dimension === "spell" && source === "loot") return "item";
  return null;
}
