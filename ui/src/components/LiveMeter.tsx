import type { QueryRow } from "../api";
import type { TickEvent } from "../live";
import { fmtNum, fmtRate } from "../format";
import { useRowLink } from "../highlight";

interface Props {
  tick: TickEvent | null;
  colorFor: (key: string) => string;
  petRollup: boolean;
}

/**
 * The live meter: horizontal bars per player over the current fight(s), fed by
 * hub ticks. Text stays in ink tokens; the colored bar carries identity.
 * The server pushes rolled-up rows whose children carry the actor breakdown,
 * so the pets→owners toggle un-rolls client-side without a round trip.
 */
export function LiveMeter({ tick, colorFor, petRollup }: Props) {
  const rowLink = useRowLink();
  let rows: QueryRow[] = tick?.result.rows ?? [];
  if (!petRollup) {
    rows = rows
      .flatMap((row) =>
        row.label.endsWith(" +Pets") && row.children ? row.children : [row],
      )
      .sort((a, b) => (b.metrics.total ?? 0) - (a.metrics.total ?? 0));
  }
  const max = rows.length > 0 ? Math.max(...rows.map((r) => r.metrics.total ?? 0)) : 0;

  return (
    <div className="panel live-meter">
      <div className="panel-title">
        <span>Live meter</span>
        {tick && <span className="subtle">fight #{tick.fightIds.join(", #")}</span>}
      </div>
      {rows.length === 0 ? (
        <div className="empty">Waiting for combat…</div>
      ) : (
        <div className="meter-rows">
          {rows.map((row) => {
            const link = rowLink(row.key);
            return (
              <div
                key={row.key}
                className={`meter-row ${link.className ?? ""}`.trim()}
                onMouseEnter={link.onMouseEnter}
                onMouseLeave={link.onMouseLeave}
                onClick={link.onClick}
              >
                <div
                  className="meter-bar"
                  style={{
                    width: max > 0 ? `${((row.metrics.total ?? 0) / max) * 100}%` : "0%",
                    background: colorFor(row.key),
                  }}
                />
                <span className="meter-name">{row.label}</span>
                <span className="meter-nums">
                  {fmtNum(row.metrics.total ?? 0)} · {fmtNum(row.metrics.dps ?? 0)} dps ·{" "}
                  {fmtRate(row.metrics.percentOfTotal ?? 0)}
                </span>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
