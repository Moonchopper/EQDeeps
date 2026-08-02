import { useCallback, useEffect, useRef, useState } from "react";
import * as echarts from "echarts";
import { api, type QueryResult, type QueryRow } from "../api";
import { fmtNum, fmtRate, OTHER_COLOR, SERIES_COLORS } from "../format";
import { buildSpec, METRIC_LABELS, RATE_METRICS, type PanelDef } from "./model";
import type { EntityColors } from "../colors";
import { attachWheelNavigation, offsetTooltip } from "../chartInteractions";

export interface PanelContext {
  sessionId: string;
  fightIds: number[];
  refreshKey: number;
  petRollup: boolean;
  colors: EntityColors;
}

function fmtMetric(metric: string, value: number): string {
  if (RATE_METRICS.has(metric)) return fmtRate(value);
  if (metric === "hits" || metric === "deaths" || metric === "casts" ||
      metric === "interrupts" || metric === "fizzles" || metric === "activeSeconds") {
    return String(Math.round(value));
  }
  return fmtNum(value);
}

function usePanelQuery(panel: PanelDef, ctx: PanelContext): QueryResult | null | "no-selection" {
  const [result, setResult] = useState<QueryResult | null>(null);
  const spec = buildSpec(panel, ctx.fightIds, ctx.petRollup);
  const specKey = JSON.stringify(spec);
  const noSelection = panel.scopeMode === "selection" && ctx.fightIds.length === 0;

  useEffect(() => {
    if (noSelection) {
      setResult(null);
      return;
    }
    let cancelled = false;
    api
      .query(ctx.sessionId, JSON.parse(specKey))
      .then((r) => !cancelled && setResult(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [ctx.sessionId, specKey, ctx.refreshKey, noSelection]);

  return noSelection ? "no-selection" : result;
}

export function PanelBody({ panel, ctx }: { panel: PanelDef; ctx: PanelContext }) {
  switch (panel.viz) {
    case "table":
      return <TablePanel panel={panel} ctx={ctx} />;
    case "line":
      return <LinePanel panel={panel} ctx={ctx} />;
    case "bar":
      return <BarPanel panel={panel} ctx={ctx} />;
    default:
      return <TilePanel panel={panel} ctx={ctx} />;
  }
}

// ---- table -----------------------------------------------------------------

function TablePanel({ panel, ctx }: { panel: PanelDef; ctx: PanelContext }) {
  const result = usePanelQuery(panel, ctx);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  if (result === "no-selection") return <div className="empty">Select a fight</div>;
  if (!result) return <div className="empty">Loading…</div>;

  const barMetric = panel.viz === "table" && panel.metrics.includes("total") ? "total" : null;
  const maxBar = barMetric
    ? result.rows.reduce((max, r) => Math.max(max, r.metrics[barMetric] ?? 0), 0)
    : 0;
  const playerRows = panel.groupBy[0] === "player";

  const renderRow = (row: QueryRow, depth: number, path: string): JSX.Element[] => {
    const hasChildren = (row.children?.length ?? 0) > 0;
    const isOpen = expanded.has(path);
    let rowStyle: React.CSSProperties | undefined;
    let chip: JSX.Element | null = null;
    if (depth === 0 && barMetric && maxBar > 0) {
      const color = playerRows ? ctx.colors.claim(row.key) : ctx.colors.lookup(row.key);
      const pct = ((row.metrics[barMetric] ?? 0) / maxBar) * 100;
      rowStyle = {
        background: `linear-gradient(to right, ${color}2e ${pct.toFixed(1)}%, transparent ${pct.toFixed(1)}%)`,
      };
      chip = <span className="color-chip" style={{ background: color }} />;
    }

    const out = [
      <tr key={path} className={depth > 0 ? "child-row" : undefined} style={rowStyle}>
        <td style={{ paddingLeft: depth * 16 + 8 }}>
          {hasChildren ? (
            <button
              className="expander"
              onClick={() => {
                const next = new Set(expanded);
                if (isOpen) {
                  next.delete(path);
                } else {
                  next.add(path);
                }
                setExpanded(next);
              }}
            >
              {isOpen ? "▾" : "▸"}
            </button>
          ) : (
            <span className="expander-spacer" />
          )}
          {chip}
          {row.label}
        </td>
        {panel.metrics.map((m) => (
          <td key={m} className="num">
            {fmtMetric(m, row.metrics[m] ?? 0)}
          </td>
        ))}
      </tr>,
    ];
    if (hasChildren && isOpen) {
      for (const child of row.children!) {
        out.push(...renderRow(child, depth + 1, `${path}/${child.key}`));
      }
    }
    return out;
  };

  return (
    <div className="table-scroll">
      <table>
        <thead>
          <tr>
            <th>Name</th>
            {panel.metrics.map((m) => (
              <th key={m} className="num">
                {METRIC_LABELS[m] ?? m}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>{result.rows.flatMap((row) => renderRow(row, 0, row.key))}</tbody>
      </table>
    </div>
  );
}

// ---- line ------------------------------------------------------------------

const BREAK_MS = 30_000;

function LinePanel({ panel, ctx }: { panel: PanelDef; ctx: PanelContext }) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const result = usePanelQuery(panel, ctx);
  const [isZoomed, setIsZoomed] = useState(false);
  const suppressZoomEventRef = useRef(false);
  const extentRef = useRef<[number, number] | null>(null);

  const resetZoom = useCallback(() => {
    const chart = chartRef.current;
    if (chart) {
      suppressZoomEventRef.current = true;
      chart.dispatchAction({ type: "dataZoom", start: 0, end: 100 });
      suppressZoomEventRef.current = false;
    }
    setIsZoomed(false);
  }, []);

  useEffect(() => {
    if (!divRef.current) return;
    const chart = echarts.init(divRef.current);
    chartRef.current = chart;
    chart.on("datazoom", (params: unknown) => {
      if (suppressZoomEventRef.current) {
        return;
      }
      const p = params as { start?: number; end?: number; batch?: { start?: number; end?: number }[] };
      const window = p.batch?.[0] ?? p;
      setIsZoomed(!(window.start === 0 && window.end === 100));
    });
    chart.getZr().on("dblclick", resetZoom);
    const detachScrub = attachWheelNavigation(chart, { left: 48, right: 10 }, () => extentRef.current);
    const observer = new ResizeObserver(() => chart.resize());
    observer.observe(divRef.current);
    return () => {
      observer.disconnect();
      detachScrub();
      chart.dispose();
      chartRef.current = null;
    };
  }, [resetZoom]);

  useEffect(() => {
    if (!chartRef.current) return;
    if (!result || result === "no-selection") {
      chartRef.current.clear();
      return;
    }

    const ranked = [...result.rows].sort((a, b) => (b.metrics.total ?? 0) - (a.metrics.total ?? 0));
    const top = ranked.slice(0, 8);
    const rest = ranked.slice(8);

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

    extentRef.current =
      segments.length > 0 ? [segments[0][0], segments[segments.length - 1][1]] : null;

    const step = Math.max(1, panel.bucketSeconds) * 1000;
    const windowBuckets = Math.max(1, Math.round(panel.windowSec / Math.max(1, panel.bucketSeconds)));
    const smoothed = (rows: QueryRow[]) => {
      const bySecond = new Map<number, number>();
      for (const row of rows) {
        for (const p of row.series ?? []) {
          const t = new Date(p.bucketStart).getTime();
          bySecond.set(t, (bySecond.get(t) ?? 0) + p.value);
        }
      }
      const points: [number, number | null][] = [];
      for (const [start, end] of segments) {
        const ring: number[] = [];
        let sum = 0;
        for (let t = start; t <= end; t += step) {
          const raw = bySecond.get(t) ?? 0;
          ring.push(raw);
          sum += raw;
          if (ring.length > windowBuckets) {
            sum -= ring.shift()!;
          }
          points.push([t, sum / (ring.length * Math.max(1, panel.bucketSeconds))]);
        }
        points.push([end + step / 2, null]);
      }
      return points;
    };

    const series: echarts.SeriesOption[] = top.map((row) => ({
      name: row.label,
      type: "line",
      showSymbol: false,
      lineStyle: { width: 2 },
      color: ctx.colors.claim(row.key),
      data: smoothed([row]),
      connectNulls: false,
    }));
    if (rest.length > 0) {
      series.push({
        name: `Other (${rest.length})`,
        type: "line",
        showSymbol: false,
        lineStyle: { width: 2, type: "dashed" },
        color: OTHER_COLOR,
        data: smoothed(rest),
        connectNulls: false,
      });
    }

    chartRef.current.setOption(
      {
        backgroundColor: "transparent",
        animation: false,
        grid: { left: 48, right: 10, top: 26, bottom: 24 },
        // Rendered-but-off-canvas toolbox: ECharts only instantiates the zoom
        // brush when the toolbox is shown (see DpsChart).
        toolbox: {
          show: true,
          top: -1000,
          feature: { dataZoom: { yAxisIndex: "none", filterMode: "none" } },
        },
        dataZoom: [
          {
            type: "inside",
            xAxisIndex: 0,
            filterMode: "none",
            zoomOnMouseWheel: false,
            moveOnMouseWheel: false,
            moveOnMouseMove: false,
          },
        ],
        legend: {
          type: "scroll",
          top: 0,
          textStyle: { color: "#c3c2b7", fontSize: 10 },
          inactiveColor: "#52514e",
        },
        tooltip: {
          trigger: "axis",
          position: offsetTooltip,
          backgroundColor: "#232322",
          borderColor: "rgba(255,255,255,0.10)",
          textStyle: { color: "#ffffff", fontSize: 12 },
          valueFormatter: (v: unknown) => (typeof v === "number" ? fmtNum(v) : "—"),
        },
        xAxis: {
          type: "time",
          axisLine: { lineStyle: { color: "#383835" } },
          axisLabel: { color: "#898781", fontSize: 10 },
          splitLine: { show: false },
        },
        yAxis: {
          type: "value",
          axisLabel: { color: "#898781", fontSize: 10, formatter: (v: number) => fmtNum(v) },
          splitLine: { lineStyle: { color: "#2c2c2a" } },
        },
        series,
      },
      { replaceMerge: ["series"] },
    );

    chartRef.current.dispatchAction({
      type: "takeGlobalCursor",
      key: "dataZoomSelect",
      dataZoomSelectActive: true,
    });
  }, [result, panel.windowSec, panel.bucketSeconds]);

  if (result === "no-selection") return <div className="empty">Select a fight</div>;
  return (
    <div className="chart-wrap">
      <div
        ref={divRef}
        className="chart"
        title="Drag to zoom a time range · scroll to zoom · shift+scroll to scrub · double-click to reset"
      />
      {isZoomed && (
        <button
          className="zoom-reset"
          onClick={resetZoom}
          title="Back to the full view (or double-click the chart)"
        >
          ↺ reset zoom
        </button>
      )}
    </div>
  );
}

// ---- bar -------------------------------------------------------------------

function BarPanel({ panel, ctx }: { panel: PanelDef; ctx: PanelContext }) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const result = usePanelQuery(panel, ctx);
  const metric = panel.primaryMetric;

  useEffect(() => {
    if (!divRef.current) return;
    const chart = echarts.init(divRef.current);
    chartRef.current = chart;
    const observer = new ResizeObserver(() => chart.resize());
    observer.observe(divRef.current);
    return () => {
      observer.disconnect();
      chart.dispose();
      chartRef.current = null;
    };
  }, []);

  useEffect(() => {
    if (!chartRef.current) return;
    if (!result || result === "no-selection") {
      chartRef.current.clear();
      return;
    }

    const ranked = [...result.rows]
      .sort((a, b) => (b.metrics[metric] ?? 0) - (a.metrics[metric] ?? 0))
      .slice(0, 12);

    chartRef.current.setOption(
      {
        backgroundColor: "transparent",
        animation: false,
        grid: { left: 8, right: 56, top: 6, bottom: 6, containLabel: true },
        tooltip: {
          position: offsetTooltip,
          backgroundColor: "#232322",
          borderColor: "rgba(255,255,255,0.10)",
          textStyle: { color: "#ffffff", fontSize: 12 },
          valueFormatter: (v: unknown) =>
            typeof v === "number" ? fmtMetric(metric, v) : "—",
        },
        xAxis: { type: "value", show: false },
        yAxis: {
          type: "category",
          inverse: true,
          data: ranked.map((r) => r.label),
          axisLine: { show: false },
          axisTick: { show: false },
          axisLabel: { color: "#c3c2b7", fontSize: 11, width: 130, overflow: "truncate" },
        },
        series: [
          {
            type: "bar",
            data: ranked.map((r) => r.metrics[metric] ?? 0),
            barWidth: 13,
            itemStyle: { color: SERIES_COLORS[0], borderRadius: [0, 4, 4, 0] },
            label: {
              show: true,
              position: "right",
              color: "#898781",
              fontSize: 11,
              formatter: (p: { value: unknown }) => fmtMetric(metric, Number(p.value)),
            },
          },
        ],
      },
      { replaceMerge: ["series"] },
    );
  }, [result, metric]);

  if (result === "no-selection") return <div className="empty">Select a fight</div>;
  return <div ref={divRef} className="chart" />;
}

// ---- tile ------------------------------------------------------------------

function TilePanel({ panel, ctx }: { panel: PanelDef; ctx: PanelContext }) {
  const result = usePanelQuery(panel, ctx);
  if (result === "no-selection") return <div className="empty">Select a fight</div>;
  const value = result?.totals[panel.primaryMetric] ?? 0;
  return (
    <div className="tile-body">
      <span className="tile-value">{result ? fmtMetric(panel.primaryMetric, value) : "…"}</span>
      <span className="tile-label">{METRIC_LABELS[panel.primaryMetric] ?? panel.primaryMetric}</span>
    </div>
  );
}
