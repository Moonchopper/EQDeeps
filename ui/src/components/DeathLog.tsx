import { useEffect, useState } from "react";
import { api, type QueryResult } from "../api";
import { useRowLink } from "../highlight";
import { frameScope, type TimeFrame } from "../timeFrame";
import { LookupLink } from "../lookup/LookupLink";
import { looksLikeNpc } from "../lookup/providers";

interface Props {
  sessionId: string;
  frame: TimeFrame;
  refreshKey: number;
}

/** Deaths inside the current time frame: victim → killer with counts. */
export function DeathLog({ sessionId, frame, refreshKey }: Props) {
  const [result, setResult] = useState<QueryResult | null>(null);
  // A row is a victim/killer pair, so it names two entities. The victim is the
  // one the row is about — and the one whose line on the DPS chart is about to
  // stop, which is the connection worth being able to make.
  const rowLink = useRowLink();

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
                    <tr
                      key={`${victim.key}/${killer.key}`}
                      className={rowLink(victim.key).className}
                      onMouseEnter={rowLink(victim.key).onMouseEnter}
                      onMouseLeave={rowLink(victim.key).onMouseLeave}
                      onClick={rowLink(victim.key).onClick}
                    >
                      <td>
                        {victim.label}
                        {/* Players and mobs share both columns. A name shaped
                            like a mob's gets a door; so does whatever killed a
                            player, since that is a mob even when its name is
                            one word — while a mob's killer is the raid. */}
                        {looksLikeNpc(victim.label) && <LookupLink kind="npc" name={victim.label} />}
                      </td>
                      <td>
                        {killer.label}
                        {(looksLikeNpc(killer.label) || !looksLikeNpc(victim.label)) && (
                          <LookupLink kind="npc" name={killer.label} />
                        )}
                      </td>
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
