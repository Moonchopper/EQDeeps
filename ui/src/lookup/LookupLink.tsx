import { useEffect, useRef, useState, type MouseEvent } from "react";
import { createPortal } from "react-dom";
import { IconExternalLink } from "@tabler/icons-react";
import { linksFor, lookupName, type LookupKind } from "./providers";
import { useLookupWorld } from "./lookupSettings";

interface Props {
  kind: LookupKind;
  name: string;
  /** The game's id for the thing, when known — unlocks the id-addressed sites. */
  id?: number;
  /** The install the log is from, which decides the world (see `providers.ts`). */
  install?: string;
}

/**
 * The little "look this up" door beside a name (issues #51, #62): an arrow
 * that shows when the row is pointed at, and on click a menu of the reference
 * sites that can say more — the world's first choice on top, the rest under
 * it. Every entry is a real link opening a new tab, which the shell hands to
 * the default browser (ADR-009); the app itself never navigates.
 *
 * <p>A menu rather than a straight jump: the sites disagree about coverage
 * (one has the quest, another the drop rate, and on Legends the id-addressed
 * one is right only once an id is known), and the person clicking knows which
 * they were after. One extra click; no guessing on their behalf.</p>
 *
 * <p>Renders nothing when no site can address the reference, so a column
 * never carries a dead arrow.</p>
 */
export function LookupLink({ kind, name, id, install }: Props) {
  const { world } = useLookupWorld(install);
  const [open, setOpen] = useState<{ x: number; y: number } | null>(null);
  const button = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);

  const links = linksFor(world, { kind, name, id });

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

  const toggle = (e: MouseEvent) => {
    // The row underneath has its own click and hover behaviour; this is not that.
    e.stopPropagation();
    e.preventDefault();
    if (open) {
      setOpen(null);
      return;
    }
    const r = button.current!.getBoundingClientRect();
    // Below and left-aligned to the arrow; nudged back inside the viewport when
    // the row sits near the right edge, where a menu would otherwise clip.
    const width = 240;
    const x = Math.min(r.left, window.innerWidth - width - 8);
    setOpen({ x: Math.max(8, x), y: r.bottom + 4 });
  };

  const shown = lookupName(name, kind);
  const kindLabel = kind === "npc" ? "mob" : kind;

  return (
    <>
      <button
        ref={button}
        type="button"
        className={"lookup-btn" + (open ? " on" : "")}
        title={`Look up this ${kindLabel}`}
        aria-label={`Look up ${shown}`}
        aria-haspopup="menu"
        aria-expanded={open !== null}
        onClick={toggle}
      >
        <IconExternalLink size={12} stroke={2} aria-hidden />
      </button>
      {open &&
        createPortal(
          <div
            ref={menu}
            className="lookup-menu"
            role="menu"
            style={{ left: open.x, top: open.y }}
            onClick={(e) => e.stopPropagation()}
          >
            <div className="lookup-head">
              <span className="lookup-name">{shown}</span>
              <span className="lookup-kind">{kindLabel}</span>
            </div>
            {links.map(({ provider, url }, i) => (
              <a
                key={provider.id}
                className={"lookup-item" + (i === 0 ? " default" : "")}
                role="menuitem"
                href={url}
                target="_blank"
                rel="noreferrer"
                onClick={() => setOpen(null)}
              >
                <span>{provider.name}</span>
                <IconExternalLink size={12} stroke={2} aria-hidden />
              </a>
            ))}
            <div className="lookup-foot">Sites for {world.name} — change in Settings</div>
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
