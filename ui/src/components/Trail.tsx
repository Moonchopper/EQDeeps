import { IconChevronRight, IconBook2, IconMap2, IconSwords } from "@tabler/icons-react";
import type { Crumb } from "../trail";

/** What the crumb's own title says it goes back to (F31 added the third case). */
function trailTitle(c: Crumb): string {
  if (c.view === "map") return `Back to the map of ${c.label}`;
  if (c.view === "raid-targets") return `Back to ${c.label}`;
  return `Back to the Bestiary page for ${c.label}`;
}

/**
 * The way back along a trail: every place you hopped from, in order, each a
 * click to return. Renders nothing until there is somewhere to go back to,
 * so a view opened from the rail looks exactly as it always did.
 */
export function Trail({ crumbs, onBack }: { crumbs: Crumb[]; onBack: (index: number) => void }) {
  if (crumbs.length === 0) return null;
  return (
    <nav className="trail" aria-label="Back along the trail">
      {crumbs.map((c, i) => (
        <span key={i} className="trail-step">
          <button className="trail-crumb" onClick={() => onBack(i)} title={trailTitle(c)}>
            {c.view === "map" ? (
              <IconMap2 size={13} stroke={1.8} aria-hidden />
            ) : c.view === "raid-targets" ? (
              <IconSwords size={13} stroke={1.8} aria-hidden />
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
