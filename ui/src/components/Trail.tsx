import { IconChevronRight, IconBook2, IconMap2 } from "@tabler/icons-react";
import type { Crumb } from "../trail";

/**
 * The way back along a Bestiary ↔ Map trail: every place you hopped from, in
 * order, each a click to return. Renders nothing until there is somewhere to
 * go back to, so a view opened from the rail looks exactly as it always did.
 */
export function Trail({ crumbs, onBack }: { crumbs: Crumb[]; onBack: (index: number) => void }) {
  if (crumbs.length === 0) return null;
  return (
    <nav className="trail" aria-label="Back along the trail">
      {crumbs.map((c, i) => (
        <span key={i} className="trail-step">
          <button
            className="trail-crumb"
            onClick={() => onBack(i)}
            title={`Back to ${c.view === "map" ? "the map of" : "the Bestiary page for"} ${c.label}`}
          >
            {c.view === "map" ? (
              <IconMap2 size={13} stroke={1.8} aria-hidden />
            ) : (
              <IconBook2 size={13} stroke={1.8} aria-hidden />
            )}
            {c.label}
          </button>
          <IconChevronRight size={12} stroke={2} className="trail-sep" aria-hidden />
        </span>
      ))}
      <span className="trail-here subtle">here</span>
    </nav>
  );
}
