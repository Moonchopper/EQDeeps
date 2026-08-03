import type { QueryRow } from "../api";
import { fmtRate } from "../format";
import { fuzzyMatch, type FuzzyHit } from "../fuzzy";

/**
 * The interactive bits every result table shares: a fuzzy search box, sortable
 * column headers, share-of-parent bars, and match highlighting.
 *
 * All of it runs over the rows already fetched. A query panel returns its whole
 * grouped tree in one response, so filtering and sorting are a re-render rather
 * than a round trip — which is what makes the list keep up with typing.
 */

// ---- filtering -------------------------------------------------------------

export interface FilterResult {
  /** The surviving tree. Identical to the input when the query is empty. */
  rows: QueryRow[];
  /** Path → the match on that row's own label, for highlighting. */
  hits: Map<string, FuzzyHit>;
  /** Paths to expand regardless of user state: their match is a level down. */
  autoOpen: Set<string>;
  /** Top-level row count before filtering, for the "12 of 340" readout. */
  totalRows: number;
  filtered: boolean;
}

/**
 * Keeps a row when its own label matches, or when anything under it does.
 *
 * The two cases behave differently on purpose. Matching the row itself means
 * the row is what was asked for, so its full breakdown stays intact — search
 * an item and you see every mob that dropped it. Matching only a descendant
 * means the row is context, so it narrows to the matching children and opens
 * itself, because the answer is the thing a level down, not the header over it.
 *
 * Rows come back in relevance order; the caller may re-sort by a column.
 */
export function filterTree(rows: QueryRow[], query: string): FilterResult {
  const hits = new Map<string, FuzzyHit>();
  const autoOpen = new Set<string>();
  if (query.trim().length === 0) {
    return { rows, hits, autoOpen, totalRows: rows.length, filtered: false };
  }

  const walk = (row: QueryRow, path: string): { row: QueryRow; score: number } | null => {
    const children = row.children ?? [];
    const kept: QueryRow[] = [];
    let best = 0;
    for (const child of children) {
      const hit = walk(child, `${path}/${child.key}`);
      if (hit) {
        kept.push(hit.row);
        best = Math.max(best, hit.score);
      }
    }

    const own = fuzzyMatch(row.label, query);
    if (own) {
      hits.set(path, own);
      if (kept.length > 0) {
        autoOpen.add(path);
      }
      // A hit on the row itself outranks a hit borrowed from a child.
      return { row, score: own.score + 200 + best };
    }
    if (kept.length === 0) {
      return null;
    }
    autoOpen.add(path);
    return { row: { ...row, children: kept }, score: best };
  };

  const survivors: { row: QueryRow; score: number }[] = [];
  for (const row of rows) {
    const hit = walk(row, row.key);
    if (hit) {
      survivors.push(hit);
    }
  }
  survivors.sort((a, b) => b.score - a.score);
  return {
    rows: survivors.map((s) => s.row),
    hits,
    autoOpen,
    totalRows: rows.length,
    filtered: true,
  };
}

// ---- sorting ---------------------------------------------------------------

export interface SortState {
  key: string;
  dir: "asc" | "desc";
}

/** The sort key for the name column, which is text rather than a metric. */
export const NAME_SORT = "__name";

/**
 * Click cycle: the useful direction first, then the other, then off. "Off" is
 * the server's own ordering, which is already ranked — losing the ability to
 * get back to it would make sorting a one-way door.
 */
export function nextSort(current: SortState | null, key: string): SortState | null {
  const first: "asc" | "desc" = key === NAME_SORT ? "asc" : "desc";
  if (!current || current.key !== key) {
    return { key, dir: first };
  }
  if (current.dir === first) {
    return { key, dir: first === "asc" ? "desc" : "asc" };
  }
  return null;
}

/** Sorts every level of the tree, so an expanded breakdown obeys the header too. */
export function sortTree(rows: QueryRow[], sort: SortState | null): QueryRow[] {
  if (!sort) {
    return rows;
  }
  const sign = sort.dir === "asc" ? 1 : -1;
  const compare = (a: QueryRow, b: QueryRow) =>
    sort.key === NAME_SORT
      ? sign * a.label.localeCompare(b.label, undefined, { numeric: true, sensitivity: "base" })
      : sign * ((a.metrics[sort.key] ?? 0) - (b.metrics[sort.key] ?? 0));

  const apply = (list: QueryRow[]): QueryRow[] =>
    [...list]
      .sort(compare)
      .map((row) => (row.children ? { ...row, children: apply(row.children) } : row));
  return apply(rows);
}

// ---- presentation ----------------------------------------------------------

export function SortHeader({
  label,
  sortKey,
  sort,
  onSort,
  numeric,
}: {
  label: string;
  sortKey: string;
  sort: SortState | null;
  onSort: (next: SortState | null) => void;
  numeric?: boolean;
}) {
  const active = sort?.key === sortKey;
  return (
    <th
      className={(numeric ? "num " : "") + "sortable" + (active ? " sorted" : "")}
      onClick={() => onSort(nextSort(sort, sortKey))}
      title={`Sort by ${label}`}
    >
      {label}
      <span className="sort-caret">{active ? (sort!.dir === "asc" ? "▲" : "▼") : "⇅"}</span>
    </th>
  );
}

export function TableSearch({
  value,
  onChange,
  placeholder,
  shown,
  total,
}: {
  value: string;
  onChange: (next: string) => void;
  placeholder: string;
  shown: number;
  total: number;
}) {
  return (
    <div className="table-search">
      <input
        className="search-input"
        type="search"
        value={value}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        spellCheck={false}
      />
      {value.trim().length > 0 && (
        <span className="search-count">
          {shown} of {total}
        </span>
      )}
    </div>
  );
}

/**
 * The meter fill behind a table row: a tint from the left edge, sized as a
 * share of the biggest value at that level. Top-level rows have always drawn
 * this way; breakdown rows now do too, so an expanded row reads as a
 * distribution at a glance instead of a column of numbers to divide in your
 * head. Alpha is low enough that the row text stays the foreground.
 */
export function meterStyle(color: string, pct: number, alpha = "2e"): React.CSSProperties {
  const width = Math.max(0, Math.min(100, pct)).toFixed(1);
  return {
    background: `linear-gradient(to right, ${color}${alpha} ${width}%, transparent ${width}%)`,
  };
}

/**
 * Red → amber → green across a breakdown, so rank is legible from hue alone
 * before you read a single number.
 *
 * Top-level rows keep their entity color, which is identity and not rank (see
 * colors.ts) — heat is only for the rows under them, where "which of these is
 * the big one" is the whole question and the entity is already named by the
 * parent. The stops are the palette's own danger/gold/live rather than raw
 * red and green: the amber midpoint is what keeps the ramp readable for
 * red-green color blindness, since lightness moves along with hue.
 */
const HEAT_STOPS: readonly (readonly [number, number, number])[] = [
  [0xe6, 0x67, 0x67], // --danger
  [0xf5, 0xc5, 0x42], // --gold
  [0x63, 0xcf, 0x82], // --live
];

/** `t` is 0 (coldest) to 1 (hottest). Returns 6-digit hex, so alpha can be appended. */
export function heatColor(t: number): string {
  const clamped = Number.isFinite(t) ? Math.max(0, Math.min(1, t)) : 0;
  const scaled = clamped * (HEAT_STOPS.length - 1);
  const index = Math.min(HEAT_STOPS.length - 2, Math.floor(scaled));
  const fraction = scaled - index;
  const from = HEAT_STOPS[index];
  const to = HEAT_STOPS[index + 1];
  const channel = (k: number) =>
    Math.round(from[k] + (to[k] - from[k]) * fraction)
      .toString(16)
      .padStart(2, "0");
  return `#${channel(0)}${channel(1)}${channel(2)}`;
}

/** The share a breakdown row holds of its parent, shown where no column carries it. */
export function SharePct({ pct, title }: { pct: number; title?: string }) {
  return (
    <span className="share-pct" title={title}>
      {fmtRate(pct)}
    </span>
  );
}

/** Renders the matched characters bold, so a fuzzy hit shows its reasoning. */
export function Highlight({ text, hit }: { text: string; hit?: FuzzyHit }) {
  if (!hit || hit.positions.length === 0) {
    return <>{text}</>;
  }
  const matched = new Set(hit.positions);
  const parts: JSX.Element[] = [];
  let run = "";
  let runIsMatch = matched.has(0);
  const flush = () => {
    if (run.length === 0) {
      return;
    }
    parts.push(
      runIsMatch ? (
        <mark key={parts.length} className="search-hit">
          {run}
        </mark>
      ) : (
        <span key={parts.length}>{run}</span>
      ),
    );
    run = "";
  };
  for (let i = 0; i < text.length; i++) {
    const isMatch = matched.has(i);
    if (isMatch !== runIsMatch) {
      flush();
      runIsMatch = isMatch;
    }
    run += text[i];
  }
  flush();
  return <>{parts}</>;
}
