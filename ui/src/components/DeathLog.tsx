import { useEffect, useState } from "react";
import { api, type QueryResult } from "../api";

interface Props {
  sessionId: string;
  fightIds: number[];
  refreshKey: number;
}

/** Deaths within the selection: victim → killer with counts. */
export function DeathLog({ sessionId, fightIds, refreshKey }: Props) {
  const [result, setResult] = useState<QueryResult | null>(null);

  useEffect(() => {
    if (fightIds.length === 0) {
      setResult(null);
      return;
    }
    let cancelled = false;
    api
      .query(sessionId, {
        source: "deaths",
        scope: { fightIds },
        groupBy: ["player", "target"],
        metrics: ["deaths"],
      })
      .then((r) => !cancelled && setResult(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, fightIds.join(","), refreshKey]);

  const rows = result?.rows ?? [];
  return (
    <div className="panel death-log">
      <div className="panel-title">
        <span>Deaths</span>
      </div>
      {rows.length === 0 ? (
        <div className="empty">{fightIds.length === 0 ? "Select a fight" : "No deaths"}</div>
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
