import { useEffect, useState } from "react";
import { api, type QueryResult } from "../api";
import { frameScope, type TimeFrame } from "../timeFrame";

interface Props {
  sessionId: string;
  frame: TimeFrame;
  refreshKey: number;
}

/** Deaths inside the current time frame: victim → killer with counts. */
export function DeathLog({ sessionId, frame, refreshKey }: Props) {
  const [result, setResult] = useState<QueryResult | null>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .query(sessionId, {
        source: "deaths",
        scope: frameScope(frame),
        groupBy: ["player", "target"],
        metrics: ["deaths"],
      })
      .then((r) => !cancelled && setResult(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, JSON.stringify(frame), refreshKey]);

  const rows = result?.rows ?? [];
  return (
    <div className="panel death-log">
      <div className="panel-title">
        <span>Deaths</span>
      </div>
      {rows.length === 0 ? (
        <div className="empty">No deaths</div>
      ) : (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Victim</th>
                <th>Killed by</th>
                <th className="num">Count</th>
              </tr>
            </thead>
            <tbody>
              {rows.flatMap((victim) =>
                (victim.children ?? [{ key: "?", label: "Unknown", metrics: victim.metrics }]).map(
                  (killer) => (
                    <tr key={`${victim.key}/${killer.key}`}>
                      <td>{victim.label}</td>
                      <td>{killer.label}</td>
                      <td className="num">{killer.metrics.deaths ?? 0}</td>
                    </tr>
                  ),
                ),
              )}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
