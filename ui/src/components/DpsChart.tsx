import { useCallback, useEffect, useRef, useState } from "react";
import * as echarts from "echarts";
import { api, type FightInfo, type QueryResult, type QueryRow } from "../api";
import { fmtNum, OTHER_COLOR } from "../format";
import type { EntityColors } from "../colors";
import { attachWheelZoom, bucketAlignedWindow, offsetTooltip } from "../chartInteractions";
import {
  fmtDuration,
  spanChoices,
  windowChoices,
  type ChartSettings,
  type Span,
} from "../timeControls";
import { frameScope, isLive, type TimeFrame } from "../timeFrame";
import { fightMarkArea } from "../fightOverlay";

interface Props {
  sessionId: string;
  frame: TimeFrame;
  /** For the fight bands drawn behind the line. */
  fights: FightInfo[];
  /** Mob-name size on the bands; 0 hides them. */
  fightLabelPx: number;
  /** Wall clock while scrolling; null when the window should sit still. */
  scrollNowMs: number | null;
  refreshKey: number;
  petRollup: boolean;
  colors: EntityColors;
  chartDefaults: ChartSettings;
}

// Shared with the standard views' time panels so the two can't drift. This
// chart is bucketed at 1 s, which is the ladder's base unit.
const WINDOW_CHOICES = windowChoices(1);
const SPAN_CHOICES = spanChoices(1);

/** Gaps longer than this are dead time between pulls — the line breaks. */
const BREAK_MS = 30_000;

/** Series name carrying the fight bands; kept out of the legend by name. */
const FIGHT_BANDS = "__fights";

/**
 * DPS over time with a user-adjustable rolling window. Seconds with no landed
 * damage inside a combat segment count as zero rather than leaving holes, so
 * swing cadence doesn't shred the line; true dead time (> 30 s of raid-wide
 * silence) still breaks it. Top 8 players by total with the rest folded into
 * "Other"; colors follow the entity for the life of the selection, never its
 * rank.
 */
export function DpsChart({
  sessionId,
  frame,
  fights,
  fightLabelPx,
  scrollNowMs,
  refreshKey,
  petRollup,
  colors,
  chartDefaults,
}: Props) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const [windowSec, setWindowSec] = useState(chartDefaults.windowSec);
  const [span, setSpan] = useState<Span>(chartDefaults.spanSec);
  const [result, setResult] = useState<QueryResult | null>(null);
  const frameKey = JSON.stringify(frame);

  // The top-bar control is the parent: this chart is not special, so a change
  // there pushes down here exactly as it does to every standard-view panel.
  useEffect(() => {
    setWindowSec(chartDefaults.windowSec);
    setSpan(chartDefaults.spanSec);
  }, [chartDefaults.windowSec, chartDefaults.spanSec]);

  const effectiveSpan = span === "fit" ? 60 : span;

  // Drag-to-zoom: while the user is zoomed, the sliding span viewport pauses
  // (it would yank the axis out from under the selection). A visible "reset
  // zoom" pill appears whenever the view is non-default; double-click is the
  // shortcut for the same reset.
  const [isZoomed, setIsZoomed] = useState(false);
  const suppressZoomEventRef = useRef(false);
  const extentRef = useRef<[number, number] | null>(null);

  // "fit" means show everything there is, which cannot also mean "and keep
  // sliding past it", so scrolling only applies to a fixed span. Zooming
  // suspends it too — the viewport belongs to the user until they reset.
  const scrollWindow: [number, number] | null =
    scrollNowMs !== null && span !== "fit" && !isZoomed
      ? bucketAlignedWindow(scrollNowMs, span + windowSec, 1)
      : null;

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
      // A dispatch back to the full range (wheel-zoom-out hitting the data
      // extent) is a reset, not a zoom.
      const p = params as { start?: number; end?: number; batch?: { start?: number; end?: number }[] };
      const window = p.batch?.[0] ?? p;
      setIsZoomed(!(window.start === 0 && window.end === 100));
    });
    chart.getZr().on("dblclick", resetZoom);
    const detachWheelZoom = attachWheelZoom(chart, { left: 52, right: 12 }, () => extentRef.current);
    const onResize = () => chart.resize();
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("resize", onResize);
      detachWheelZoom();
      chart.dispose();
      chartRef.current = null;
    };
  }, [resetZoom]);

  useEffect(() => {
    resetZoom(); // new frame: fresh viewport
  }, [frameKey, resetZoom]);

  useEffect(() => {
    let cancelled = false;
    api
      .query(sessionId, {
        source: "damage",
        // The frame is the scope. The extra windowSec of lookback warms up the
        // rolling mean so the left edge of the viewport is already smoothed.
        scope: frameScope(frame, windowSec),
        groupBy: ["player"],
        metrics: ["total"],
        bucketSeconds: 1,
        petRollup,
      })
      .then((r) => !cancelled && setResult(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, frameKey, refreshKey, windowSec, petRollup]);

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
    if (scrollWindow) {
      // Scrolling with the wall clock: the window IS [now - span, now], so
      // that is the segment. Seconds with nothing in them read as zero, so
      // quiet time draws as a line along the floor that keeps moving and the
      // rolling mean decays into it rather than freezing at its last value.
      segments.push(scrollWindow);
    } else {
      for (const t of timeline) {
        const last = segments[segments.length - 1];
        if (last && t - last[1] <= BREAK_MS) {
          last[1] = t;
        } else {
          segments.push([t, t]);
        }
      }
    }

    extentRef.current =
      segments.length > 0 ? [segments[0][0], segments[segments.length - 1][1]] : null;

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
      color: colors.claim(row.key),
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

    // A fixed span pins the axis to [latest − span, latest]: constant width,
    // sliding right edge — no rescaling as points arrive. The right edge is
    // the newest data second (not wall clock), so replayed logs behave too.
    let axisMin: number | null = null;
    let axisMax: number | null = null;
    if (span !== "fit" && segments.length > 0 && !isZoomed) {
      // While scrolling the right edge is the clock; otherwise it is the
      // newest record, so a replayed or finished log still behaves.
      axisMax = scrollWindow ? scrollWindow[1] : segments[segments.length - 1][1];
      axisMin = axisMax - effectiveSpan * 1000;
    }

    // Fight bands behind the line: which mob each stretch of output was
    // against, so a trough reads as "between pulls" instead of just a gap.
    const plotHeight = (divRef.current?.clientHeight ?? 0) - 30 - 40; // grid top/bottom
    const plotWidth = (divRef.current?.clientWidth ?? 0) - 52 - 12; // grid left/right
    const markArea = extentRef.current
      ? fightMarkArea(
          fights,
          axisMin ?? extentRef.current[0],
          axisMax ?? extentRef.current[1],
          plotHeight,
          plotWidth,
          fightLabelPx,
        )
      : undefined;
    if (markArea) {
      series.push({
        name: FIGHT_BANDS,
        type: "line",
        data: [],
        silent: true,
        markArea,
      } as echarts.SeriesOption);
    }

    chartRef.current.setOption(
      {
        backgroundColor: "transparent",
        animation: false,
        grid: { left: 52, right: 12, top: 30, bottom: 40 },
        // The zoom brush lives inside the toolbox's dataZoom feature, and
        // ECharts skips feature creation entirely when show:false — so the
        // toolbox is rendered but parked off-canvas, and the select cursor is
        // armed below so a plain click-drag zooms into a time range.
        toolbox: {
          show: true,
          top: -1000,
          feature: { dataZoom: { yAxisIndex: "none", filterMode: "none" } },
        },
        // Wheel zoom is handled by attachWheelZoom (extent-clamped, resets
        // cleanly at full extent); the inside component just holds the window.
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
          // The bands ride on their own series; it has no line to toggle, so
          // naming the real series keeps it out of the legend.
          data: top.map((row) => row.label).concat(rest.length > 0 ? [`Other (${rest.length})`] : []),
          textStyle: { color: "#c3c2b7", fontSize: 11 },
          inactiveColor: "#52514e",
        },
        tooltip: {
          trigger: "axis",
          axisPointer: { type: "line", lineStyle: { color: "#52514e" } },
          position: offsetTooltip,
          backgroundColor: "#232322",
          borderColor: "rgba(255,255,255,0.10)",
          textStyle: { color: "#ffffff", fontSize: 12 },
          valueFormatter: (v: unknown) => (typeof v === "number" ? fmtNum(v) : "—"),
        },
        xAxis: {
          type: "time",
          min: axisMin,
          max: axisMax,
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

    chartRef.current.dispatchAction({
      type: "takeGlobalCursor",
      key: "dataZoomSelect",
      dataZoomSelectActive: true,
    });
  }, [result, windowSec, span, colors, isZoomed, fights, fightLabelPx, scrollNowMs]);

  return (
    <div className="panel chart-panel">
      <div className="panel-title">
        <span>Damage per second</span>
        <span className="title-controls">
          {/* The scope tabs are gone: the app has one time frame now, set by
              the fight list or the top bar, and this chart reads it like
              everything else. What is left is how to read it. */}
          <span className="subtle">{isLive(frame) ? "live" : "framed"}</span>
          <label className="toggle" title="Rolling average window — 1 s is raw landed damage">
            window
            <select
              className="panel-select"
              value={windowSec}
              onChange={(e) => setWindowSec(Number(e.target.value))}
            >
              {WINDOW_CHOICES.map((w) => (
                <option key={w} value={w}>
                  {w === 1 ? "raw (1s)" : fmtDuration(w)}
                </option>
              ))}
            </select>
          </label>
          <label
            className="toggle"
            title="Time viewport — a fixed span slides with the newest data instead of rescaling"
          >
            span
            <select
              className="panel-select"
              value={String(span)}
              onChange={(e) => setSpan(e.target.value === "fit" ? "fit" : Number(e.target.value))}
            >
              {SPAN_CHOICES.map((s) => (
                <option key={String(s.value)} value={String(s.value)}>
                  {s.label}
                </option>
              ))}
            </select>
          </label>
        </span>
      </div>
      <div className="chart-wrap">
        <div ref={divRef} className="chart" />
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
    </div>
  );
}
