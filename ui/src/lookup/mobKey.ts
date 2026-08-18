/**
 * The key two mob names are compared under, wherever a name from the log
 * meets a name from the reference site: trimmed, case folded, and with a
 * leading "a", "an" or "the" taken off.
 *
 * The game names its generic mobs with an article and the log repeats it —
 * "An imp protector" — but the site drops it when it likes (a tenth of the
 * lower-case names EQLBase lists have none). The server's index treats the
 * two as one name (`NpcIndex.Normalize`); this is the same rule on this side,
 * so a listing found under "imp protector" still lights up the log's row,
 * its measured table, and its pin. A bare article is a query, not an
 * article, and is left alone.
 */
export function mobKey(name: string): string {
  const k = name.trim().toLowerCase();
  const m = /^(an|a|the)\s+(\S.*)$/.exec(k);
  return m ? m[2] : k;
}

/** Whether two mob names name the same mob, article and case aside. */
export function sameMob(a: string, b: string): boolean {
  return mobKey(a) === mobKey(b);
}
