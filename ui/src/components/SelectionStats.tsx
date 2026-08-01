import { useEffect, useState } from "react";
import { api, type QueryResult } from "../api";
import { fmtNum } from "../format";

interface Props {
  sessionId: string;
  character: string;
  fightIds: number[];
  refreshKey: number;
  petRollup: boolean;
}

function fmtSeconds(total: number): string {
  const s = Math.round(total);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${sec.toString().padStart(2, "0")}s`;
  return `${sec}s`;
}

/**
 * Aggregate headline for the current selection: how long was actually fought,
 * how much damage landed, and the log owner's average DPS/SDPS across all
 * selected fights (active-time denominators — downtime between pulls never
 * dilutes the number). Averages across mixed content are knowingly unadjusted
 * for mob level/mitigation (feature F21 tracks normalizing that).
 */
export function SelectionStats({ sessionId, character, fightIds, refreshKey, petRollup }: Props) {
  const [result, setResult] = useState<QueryResult | null>(null);

  useEffect(() => {
    if (fightIds.length === 0) {
      setResult(null);
      return;
    }
    let cancelled = false;
    api
      .query(sessionId, {
        source: "damage",
        scope: { fightIds },
        groupBy: ["player"],
        metrics: ["total", "dps", "sdps", "activeSeconds"],
        petRollup,
      })
      .then((r) => !cancelled && setResult(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, fightIds.join(","), refreshKey, petRollup]);

  if (fightIds.length === 0 || !result) {
    return null;
  }

  const mine = result.rows.find((r) => r.key === character);
  const raidDps = result.raidSeconds > 0 ? (result.totals["total"] ?? 0) / result.raidSeconds : 0;

  const tiles: { label: string; value: string }[] = [
    { label: fightIds.length === 1 ? "fight" : "fights", value: String(fightIds.length) },
    { label: "fought time", value: fmtSeconds(result.raidSeconds) },
    { label: "total damage", value: fmtNum(result.totals["total"] ?? 0) },
    { label: "raid dps", value: fmtNum(raidDps) },
    { label: `${character} dps`, value: mine ? fmtNum(mine.metrics.dps ?? 0) : "—" },
    { label: `${character} sdps`, value: mine ? fmtNum(mine.metrics.sdps ?? 0) : "—" },
  ];

  return (
    <div className="stats-strip">
      {tiles.map((t) => (
        <div key={t.label} className="stat-tile">
          <span className="stat-value">{t.value}</span>
          <span className="stat-label">{t.label}</span>
        </div>
      ))}
    </div>
  );
}
