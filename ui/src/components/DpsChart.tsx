import { useEffect, useRef } from "react";
import * as echarts from "echarts";
import { api } from "../api";
import { fmtNum, OTHER_COLOR, SERIES_COLORS } from "../format";

interface Props {
  sessionId: string;
  fightIds: number[];
  refreshKey: number;
}

/**
 * DPS over time: per-second landed totals per player, top 8 by total with the
 * rest folded into "Other" (fixed slot order — colors follow the entity for
 * the life of the selection, never its rank). Lines break across dead time
 * instead of drawing over the gap.
 */
export function DpsChart({ sessionId, fightIds, refreshKey }: Props) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const colorMapRef = useRef<Map<string, string>>(new Map());
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
      .then((result) => {
        if (cancelled || !chartRef.current) return;

        const ranked = [...result.rows].sort(
          (a, b) => (b.metrics.total ?? 0) - (a.metrics.total ?? 0),
        );
        const top = ranked.slice(0, 8);
        const rest = ranked.slice(8);

        // Stable color per entity within this selection.
        const colors = colorMapRef.current;
        for (const row of top) {
          if (!colors.has(row.key)) {
            colors.set(row.key, SERIES_COLORS[colors.size % SERIES_COLORS.length]);
          }
        }

        const toPoints = (rows: typeof top) => {
          // Merge (for "Other") and break lines at gaps > 1 s with null points.
          const bySecond = new Map<number, number>();
          for (const row of rows) {
            for (const p of row.series ?? []) {
              const t = new Date(p.bucketStart).getTime();
              bySecond.set(t, (bySecond.get(t) ?? 0) + p.value);
            }
          }
          const times = [...bySecond.keys()].sort((a, b) => a - b);
          const points: [number, number | null][] = [];
          for (let i = 0; i < times.length; i++) {
            if (i > 0 && times[i] - times[i - 1] > 1000) {
              points.push([times[i - 1] + 500, null]);
            }
            points.push([times[i], bySecond.get(times[i])!]);
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
          data: toPoints([row]),
          connectNulls: false,
        }));
        if (rest.length > 0) {
          series.push({
            name: `Other (${rest.length})`,
            type: "line",
            showSymbol: false,
            lineStyle: { width: 2, type: "dashed" },
            color: OTHER_COLOR,
            data: toPoints(rest),
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
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, selectionKey, refreshKey]);

  return (
    <div className="panel chart-panel">
      <div className="panel-title">
        <span>Damage per second</span>
      </div>
      {fightIds.length === 0 && <div className="empty">Select a fight</div>}
      <div ref={divRef} className="chart" />
    </div>
  );
}
