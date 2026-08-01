import { useEffect, useState } from "react";
import { api, type QueryResult, type QueryRow, type QuerySource, type QuerySpec } from "../api";
import { fmtNum, fmtRate } from "../format";

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
  fightIds: number[];
  refreshKey: number;
  excludeDamageShields: boolean;
  onToggleDamageShields: (exclude: boolean) => void;
}

/**
 * The classic summaries as canned queries over the selection — the same specs
 * a power user can build by hand. Rows expand player → spell/skill.
 */
export function SummaryTable({
  sessionId,
  fightIds,
  refreshKey,
  excludeDamageShields,
  onToggleDamageShields,
}: Props) {
  const [source, setSource] = useState<QuerySource>("damage");
  const [result, setResult] = useState<QueryResult | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (fightIds.length === 0) {
      setResult(null);
      return;
    }
    const spec: QuerySpec = {
      source,
      scope: { fightIds },
      groupBy: ["player", "spell"],
      metrics: [
        ...new Set([...COLUMNS[source].map((c) => c.metric), "total", "activeSeconds"]),
      ],
      filters:
        source === "damage" && excludeDamageShields
          ? [{ flag: "damageShield", exclude: true }]
          : [],
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
  }, [sessionId, source, fightIds.join(","), refreshKey, excludeDamageShields]);

  const columns = COLUMNS[source];

  const renderRow = (row: QueryRow, depth: number, path: string): JSX.Element[] => {
    const hasChildren = (row.children?.length ?? 0) > 0;
    const isExpanded = expanded.has(path);
    const out: JSX.Element[] = [
      <tr key={path} className={depth > 0 ? "child-row" : undefined}>
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
      </div>
      {error && <div className="error">{error}</div>}
      {fightIds.length === 0 ? (
        <div className="empty">Select a fight</div>
      ) : result ? (
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
