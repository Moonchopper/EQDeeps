import { useEffect, useRef, useState } from "react";
import * as echarts from "echarts";
import { api, type QueryResult, type QueryRow } from "../api";
import { fmtNum, OTHER_COLOR, SERIES_COLORS } from "../format";

interface Props {
  sessionId: string;
  fightIds: number[];
  refreshKey: number;
}

const WINDOW_CHOICES = [1, 3, 5, 10, 30, 60];

/** Gaps longer than this are dead time between pulls — the line breaks. */
const BREAK_MS = 30_000;

/**
 * DPS over time with a user-adjustable rolling window (default 5 s — the
 * standard "current burst" number). Seconds with no landed damage inside a
 * combat segment count as zero rather than leaving holes, so swing cadence
 * doesn't shred the line; true dead time (> 30 s of raid-wide silence) still
 * breaks it. Top 8 players by total with the rest folded into "Other";
 * colors follow the entity for the life of the selection, never its rank.
 */
export function DpsChart({ sessionId, fightIds, refreshKey }: Props) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const colorMapRef = useRef<Map<string, string>>(new Map());
  const [windowSec, setWindowSec] = useState(5);
  const [result, setResult] = useState<QueryResult | null>(null);
  const selectionKey = fightIds.join(",");

  useEffect(() => {
    if (!divRef.current) return;
    const chart = echarts.init(divRef.current);
    chartRef.current = chart;
    const onResize = () => chart.resize();
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("resize", onResize);
      chart.dispose();
      chartRef.current = null;
    };
  }, []);

  // New selection = new chart context: reset the entity→color assignment.
  useEffect(() => {
    colorMapRef.current = new Map();
  }, [selectionKey]);

  useEffect(() => {
    if (fightIds.length === 0) {
      setResult(null);
      chartRef.current?.clear();
      return;
    }
    let cancelled = false;
    api
      .query(sessionId, {
        source: "damage",
        scope: { fightIds },
        groupBy: ["player"],
        metrics: ["total"],
        bucketSeconds: 1,
      })
      .then((r) => !cancelled && setResult(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, selectionKey, refreshKey]);

  useEffect(() => {
    if (!chartRef.current) return;
    if (!result) {
      chartRef.current.clear();
      return;
    }

    const ranked = [...result.rows].sort(
      (a, b) => (b.metrics.total ?? 0) - (a.metrics.total ?? 0),
    );
    const top = ranked.slice(0, 8);
    const rest = ranked.slice(8);

    const colors = colorMapRef.current;
    for (const row of top) {
      if (!colors.has(row.key)) {
        colors.set(row.key, SERIES_COLORS[colors.size % SERIES_COLORS.length]);
      }
    }

    const secondsOf = (rows: QueryRow[]) => {
      const bySecond = new Map<number, number>();
      for (const row of rows) {
        for (const p of row.series ?? []) {
          const t = new Date(p.bucketStart).getTime();
          bySecond.set(t, (bySecond.get(t) ?? 0) + p.value);
        }
      }
      return bySecond;
    };

    // Timeline segments come from raid-wide activity: within a segment every
    // series is dense (zero-filled), so windows and cadence behave.
    const allSeconds = new Set<number>();
    for (const row of ranked) {
      for (const p of row.series ?? []) {
        allSeconds.add(new Date(p.bucketStart).getTime());
      }
    }
    const timeline = [...allSeconds].sort((a, b) => a - b);
    const segments: [number, number][] = [];
    for (const t of timeline) {
      const last = segments[segments.length - 1];
      if (last && t - last[1] <= BREAK_MS) {
        last[1] = t;
      } else {
        segments.push([t, t]);
      }
    }

    const smoothed = (bySecond: Map<number, number>) => {
      const points: [number, number | null][] = [];
      for (const [start, end] of segments) {
        // Rolling mean over the last `windowSec` seconds; early seconds of a
        // segment divide by elapsed time so ramp-up isn't artificially low.
        const ring: number[] = [];
        let sum = 0;
        for (let t = start; t <= end; t += 1000) {
          const raw = bySecond.get(t) ?? 0;
          ring.push(raw);
          sum += raw;
          if (ring.length > windowSec) {
            sum -= ring.shift()!;
          }
          points.push([t, sum / ring.length]);
        }
        points.push([end + 500, null]); // break before the next segment
      }
      return points;
    };

    const series: echarts.SeriesOption[] = top.map((row) => ({
      name: row.label,
      type: "line",
      showSymbol: false,
      lineStyle: { width: 2 },
      itemStyle: { color: colors.get(row.key) },
      color: colors.get(row.key),
      data: smoothed(secondsOf([row])),
      connectNulls: false,
    }));
    if (rest.length > 0) {
      series.push({
        name: `Other (${rest.length})`,
        type: "line",
        showSymbol: false,
        lineStyle: { width: 2, type: "dashed" },
        color: OTHER_COLOR,
        data: smoothed(secondsOf(rest)),
        connectNulls: false,
      });
    }

    chartRef.current.setOption(
      {
        backgroundColor: "transparent",
        animation: false,
        grid: { left: 52, right: 12, top: 30, bottom: 40 },
        legend: {
          type: "scroll",
          top: 0,
          textStyle: { color: "#c3c2b7", fontSize: 11 },
          inactiveColor: "#52514e",
        },
        tooltip: {
          trigger: "axis",
          axisPointer: { type: "line", lineStyle: { color: "#52514e" } },
          backgroundColor: "#232322",
          borderColor: "rgba(255,255,255,0.10)",
          textStyle: { color: "#ffffff", fontSize: 12 },
          valueFormatter: (v: unknown) => (typeof v === "number" ? fmtNum(v) : "—"),
        },
        xAxis: {
          type: "time",
          axisLine: { lineStyle: { color: "#383835" } },
          axisLabel: { color: "#898781", fontSize: 11 },
          splitLine: { show: false },
        },
        yAxis: {
          type: "value",
          axisLabel: {
            color: "#898781",
            fontSize: 11,
            formatter: (v: number) => fmtNum(v),
          },
          splitLine: { lineStyle: { color: "#2c2c2a" } },
        },
        series,
      },
      { replaceMerge: ["series"] },
    );
  }, [result, windowSec]);

  return (
    <div className="panel chart-panel">
      <div className="panel-title">
        <span>Damage per second</span>
        <label className="toggle" title="Rolling average window — 1 s is raw landed damage">
          window
          <select
            className="panel-select"
            value={windowSec}
            onChange={(e) => setWindowSec(Number(e.target.value))}
          >
            {WINDOW_CHOICES.map((w) => (
              <option key={w} value={w}>
                {w === 1 ? "raw (1s)" : `${w}s`}
              </option>
            ))}
          </select>
        </label>
      </div>
      {fightIds.length === 0 && <div className="empty">Select a fight</div>}
      <div ref={divRef} className="chart" />
    </div>
  );
}
