import { useEffect, useState } from "react";
import { api, type QueryResult, type QueryRow, type QuerySource, type QuerySpec } from "../api";
import { fmtNum, fmtRate } from "../format";
import { defaultPanel, type PanelDef } from "../dashboards/model";
import { meterStyle } from "../dashboards/tableTools";
import { ENTITY_POOL, type EntityColors } from "../colors";
import { useRowLink } from "../highlight";
import { frameScope, type TimeFrame } from "../timeFrame";

interface Column {
  metric: string;
  header: string;
  format: (v: number) => string;
}

const COLUMNS: Record<string, Column[]> = {
  damage: [
    { metric: "total", header: "Total", format: fmtNum },
    { metric: "dps", header: "DPS", format: fmtNum },
    { metric: "sdps", header: "SDPS", format: fmtNum },
    { metric: "percentOfTotal", header: "%", format: fmtRate },
    { metric: "critRate", header: "Crit%", format: fmtRate },
    { metric: "twincastRate", header: "TC%", format: fmtRate },
    { metric: "hits", header: "Hits", format: (v) => String(v) },
    { metric: "maxHit", header: "Max", format: fmtNum },
  ],
  healing: [
    { metric: "total", header: "Healed", format: fmtNum },
    { metric: "dps", header: "HPS", format: fmtNum },
    { metric: "percentOfTotal", header: "%", format: fmtRate },
    { metric: "overhealRate", header: "Over%", format: fmtRate },
    { metric: "critRate", header: "Crit%", format: fmtRate },
    { metric: "hits", header: "Heals", format: (v) => String(v) },
    { metric: "maxHit", header: "Max", format: fmtNum },
  ],
  tanking: [
    { metric: "total", header: "Taken", format: fmtNum },
    { metric: "dps", header: "DTPS", format: fmtNum },
    { metric: "percentOfTotal", header: "%", format: fmtRate },
    { metric: "meleeAttempts", header: "Attempts", format: (v) => String(v) },
    { metric: "undefendedRate", header: "Undef%", format: fmtRate },
    { metric: "maxHit", header: "Max", format: fmtNum },
  ],
};

interface Props {
  sessionId: string;
  frame: TimeFrame;
  refreshKey: number;
  excludeDamageShields: boolean;
  onToggleDamageShields: (exclude: boolean) => void;
  petRollup: boolean;
  onOpenInBuilder?: (seed: PanelDef) => void;
  colors: EntityColors;
}

/**
 * The classic summaries as canned queries over the selection — the same specs
 * a power user can build by hand. Rows expand player → spell/skill.
 */
export function SummaryTable({
  sessionId,
  frame,
  refreshKey,
  excludeDamageShields,
  onToggleDamageShields,
  petRollup,
  onOpenInBuilder,
  colors,
}: Props) {
  const [source, setSource] = useState<QuerySource>("damage");
  const [rowsBy, setRowsBy] = useState<"player" | "target">("player");
  const [result, setResult] = useState<QueryResult | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const spec: QuerySpec = {
      source,
      scope: frameScope(frame),
      // By-target rows show how the numbers differ per mob across an
      // aggregate selection; drill-down inverts (target → player).
      groupBy: rowsBy === "player" ? ["player", "spell"] : ["target", "player"],
      metrics: [
        ...new Set([...COLUMNS[source].map((c) => c.metric), "total", "activeSeconds"]),
      ],
      filters:
        source === "damage" && excludeDamageShields
          ? [{ flag: "damageShield", exclude: true }]
          : [],
      petRollup,
    };
    let cancelled = false;
    api
      .query(sessionId, spec)
      .then((r) => {
        if (!cancelled) {
          setResult(r);
          setError(null);
        }
      })
      .catch((e) => !cancelled && setError(String(e)));
    return () => {
      cancelled = true;
    };
  }, [sessionId, source, rowsBy, JSON.stringify(frame), refreshKey, excludeDamageShields, petRollup]);

  const columns = COLUMNS[source];
  // Rows are people either way round — players, or the mobs they fought — so
  // both readings link against the same pool the colors come from.
  const rowLink = useRowLink(ENTITY_POOL);

  // Meter-style row tint: the same entity color the charts use, at low alpha,
  // sized by the row's share of the top total.
  const maxTotal = result?.rows.reduce((max, r) => Math.max(max, r.metrics.total ?? 0), 0) ?? 0;

  const renderRow = (row: QueryRow, depth: number, path: string): JSX.Element[] => {
    const hasChildren = (row.children?.length ?? 0) > 0;
    const isExpanded = expanded.has(path);
    let rowStyle: React.CSSProperties | undefined;
    let chip: JSX.Element | null = null;
    if (depth === 0 && maxTotal > 0) {
      const color = rowsBy === "player" ? colors.claim(row.key) : colors.lookup(row.key);
      rowStyle = meterStyle(color, ((row.metrics.total ?? 0) / maxTotal) * 100);
      chip = <span className="color-chip" style={{ background: color }} />;
    }

    // Only the top level names an entity: a child row is that player's spell,
    // which is a different kind of thing in a different pool.
    const link = depth === 0 ? rowLink(row.key) : null;

    const out: JSX.Element[] = [
      <tr
        key={path}
        className={`${depth > 0 ? "child-row" : ""} ${link?.className ?? ""}`.trim() || undefined}
        style={rowStyle}
        onMouseEnter={link?.onMouseEnter}
        onMouseLeave={link?.onMouseLeave}
      >
        <td style={{ paddingLeft: depth * 18 + 8 }}>
          {hasChildren ? (
            <button
              className="expander"
              onClick={() => {
                const next = new Set(expanded);
                if (isExpanded) {
                  next.delete(path);
                } else {
                  next.add(path);
                }
                setExpanded(next);
              }}
            >
              {isExpanded ? "▾" : "▸"}
            </button>
          ) : (
            <span className="expander-spacer" />
          )}
          {chip}
          {row.label}
        </td>
        {columns.map((c) => (
          <td key={c.metric} className="num">
            {c.format(row.metrics[c.metric] ?? 0)}
          </td>
        ))}
      </tr>,
    ];
    if (hasChildren && isExpanded) {
      for (const child of row.children!) {
        out.push(...renderRow(child, depth + 1, `${path}/${child.key}`));
      }
    }
    return out;
  };

  return (
    <div className="panel summary">
      <div className="panel-title">
        <span className="tabs">
          {(["damage", "healing", "tanking"] as QuerySource[]).map((s) => (
            <button
              key={s}
              className={"tab" + (s === source ? " on" : "")}
              onClick={() => setSource(s)}
            >
              {s[0].toUpperCase() + s.slice(1)}
            </button>
          ))}
        </span>
        <span className="title-controls">
          <span className="tabs">
            <button
              className={"tab small" + (rowsBy === "player" ? " on" : "")}
              onClick={() => setRowsBy("player")}
              title="Rows are players; expand for spells"
            >
              by player
            </button>
            <button
              className={"tab small" + (rowsBy === "target" ? " on" : "")}
              onClick={() => setRowsBy("target")}
              title="Rows are mobs; expand for who did what to them"
            >
              by target
            </button>
          </span>
          {source === "damage" && (
            <label className="toggle">
              <input
                type="checkbox"
                checked={excludeDamageShields}
                onChange={(e) => onToggleDamageShields(e.target.checked)}
              />
              exclude DS
            </label>
          )}
          {onOpenInBuilder && (
            <button
              className="mini-btn"
              title="This view is a query — open a copy in the dashboard builder"
              onClick={() =>
                onOpenInBuilder({
                  ...defaultPanel(),
                  title: `${source[0].toUpperCase() + source.slice(1)} summary`,
                  source,
                  groupBy: rowsBy === "player" ? ["player", "spell"] : ["target", "player"],
                  excludeFlags:
                    source === "damage" && excludeDamageShields ? ["damageShield"] : [],
                })
              }
            >
              edit as panel
            </button>
          )}
        </span>
      </div>
      {error && <div className="error">{error}</div>}
      {result ? (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                {columns.map((c) => (
                  <th key={c.metric} className="num">
                    {c.header}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>{result.rows.flatMap((row) => renderRow(row, 0, row.key))}</tbody>
          </table>
        </div>
      ) : (
        <div className="empty">Loading…</div>
      )}
    </div>
  );
}
