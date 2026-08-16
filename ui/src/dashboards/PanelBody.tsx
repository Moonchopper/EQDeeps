import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as echarts from "echarts";
import {
  api,
  type ContextTimeline,
  type FightInfo,
  type QuerySource,
  type QueryResult,
  type QueryRow,
} from "../api";
import { CHART_SERIES_LIMIT, fmtNum, fmtRate, fmtSpan, OTHER_COLOR, SERIES_COLORS } from "../format";
import { MapPanel } from "../maps/MapPanel";
import {
  buildSpec,
  METRIC_LABELS,
  panelBucketSeconds,
  panelWindowSeconds,
  RATE_METRICS,
  type PanelDef,
} from "./model";
import {
  filterTree,
  heatColor,
  Highlight,
  meterStyle,
  NAME_SORT,
  SharePct,
  SortHeader,
  sortTree,
  TableSearch,
  type SortState,
} from "./tableTools";
import { colorPoolFor, ENTITY_POOL, type EntityColors } from "../colors";
import { LookupLink, lookupKindFor } from "../lookup/LookupLink";
import { ITEM_EMPHASIS, SERIES_EMPHASIS, useChartLink, useRowLink } from "../highlight";
import {
  attachNearestLineHover,
  attachWheelZoom,
  bucketAlignedWindow,
  offsetTooltip,
  heldAxisMax,
  fightBandsKey,
  type HoverLine,
} from "../chartInteractions";
import type { ChartSettings } from "../timeControls";
import type { TimeFrame } from "../timeFrame";
import { fightMarkArea } from "../fightOverlay";
import { contextMarkArea, type ContextMode } from "../contextOverlay";
import { GRID, chartInk, chartTheme } from "../chartTheme";

export interface PanelContext {
  sessionId: string;
  /** The open log's character — resolves `ownerOnly` panels. */
  character: string;
  frame: TimeFrame;
  /** For the fight bands drawn behind time charts. */
  fights: FightInfo[];
  /** Mob-name size on those bands; 0 hides them. */
  fightLabelPx: number;
  /** Where the character was and what level they were; null until it lands. */
  context: ContextTimeline | null;
  /** The installation the open log is from — map choices are kept per install. */
  install?: string;
  /** Which lanes of that the strip shows, if any. */
  contextMode: ContextMode;
  /** Measure framed ranges over played time rather than wall clock. */
  playedTimeOnly: boolean;
  /** Length of the whole log, for sizing buckets when the range is "fit". */
  logSpanSeconds: number;
  /** Wall clock while scrolling; null when the window should sit still. */
  scrollNowMs: number | null;
  /** Promote a zoomed window to the app-wide time range. */
  onAdoptRange: (beginMs: number, endMs: number) => void;
  refreshKey: number;
  petRollup: boolean;
  colors: EntityColors;
}

function fmtMetric(metric: string, value: number): string {
  if (RATE_METRICS.has(metric)) return fmtRate(value);
  if (metric === "hits" || metric === "deaths" || metric === "casts" ||
      metric === "interrupts" || metric === "fizzles" ||
      metric === "xpGains" || metric === "aaPoints" ||
      metric === "factionNet" || metric === "factionUps" ||
      metric === "factionDowns" || metric === "factionCapped" ||
      metric === "loots" || metric === "considers" || metric === "conLevel") {
    return String(Math.round(value));
  }
  // Durations, not counts: "1.2K" seconds is not something anyone reads as a
  // length of time, and neither is "4380m" once a parse runs into days.
  if (metric === "stanceSeconds" || metric === "activeSeconds" || metric === "raidSeconds") {
    return fmtSpan(value);
  }
  if (metric === "xpPercent" || metric === "xpPerHour") {
    // Level-progress points, not a ratio: show two decimals (gains are tiny).
    return value.toFixed(2);
  }
  return fmtNum(value);
}

function usePanelQuery(
  panel: PanelDef,
  ctx: PanelContext,
  settings: ChartSettings,
): QueryResult | null | "no-selection" {
  const [result, setResult] = useState<QueryResult | null>(null);
  const spec = buildSpec(
    panel, ctx.frame, ctx.petRollup, settings, ctx.logSpanSeconds, ctx.character,
    ctx.playedTimeOnly);
  const specKey = JSON.stringify(spec);

  useEffect(() => {
    let cancelled = false;
    api
      .query(ctx.sessionId, JSON.parse(specKey))
      .then((r) => !cancelled && setResult(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [ctx.sessionId, specKey, ctx.refreshKey]);

  return result;
}

export function PanelBody({
  panel,
  ctx,
  settings,
}: {
  panel: PanelDef;
  ctx: PanelContext;
  /**
   * The time frame every panel here reports over — app-wide, or this panel's
   * override. Not just the charts: a total is as much a reading of a time
   * frame as a line is.
   */
  settings: ChartSettings;
}) {
  switch (panel.viz) {
    case "table":
      return <TablePanel panel={panel} ctx={ctx} settings={settings} />;
    case "line":
      return <LinePanel panel={panel} ctx={ctx} settings={settings} />;
    case "bar":
      return <BarPanel panel={panel} ctx={ctx} settings={settings} />;
    case "droprate":
      return <DropRatePanel panel={panel} ctx={ctx} settings={settings} />;
    // No query runs for this one — it reads a folder on disk, not the log.
    case "map":
      return <MapPanel context={ctx.context} install={ctx.install} pinned={panel.mapZone} />;
    default:
      return <TilePanel panel={panel} ctx={ctx} settings={settings} />;
  }
}

/**
 * Keeps the expander open on rows the search opened for the user, without
 * taking the expander away from them: the auto-opened paths are merged INTO
 * the expanded set rather than OR'd with it at render time, so a row opened by
 * a search can still be closed by hand.
 */
function useAutoExpand(
  autoOpen: Set<string>,
  setExpanded: React.Dispatch<React.SetStateAction<Set<string>>>,
) {
  useEffect(() => {
    if (autoOpen.size === 0) {
      return;
    }
    setExpanded((current) => {
      const next = new Set(current);
      for (const path of autoOpen) {
        next.add(path);
      }
      return next.size === current.size ? current : next;
    });
  }, [autoOpen, setExpanded]);
}

function toggle(set: Set<string>, path: string): Set<string> {
  const next = new Set(set);
  if (!next.delete(path)) {
    next.add(path);
  }
  return next;
}

// ---- table -----------------------------------------------------------------

/**
 * The metric a table draws its meter bars from. "total" where the source has
 * one; otherwise the first non-rate column, which is the same metric the
 * server ranked the rows by — so the bars agree with the order they arrive in.
 */
function barMetricFor(panel: PanelDef): string | null {
  if (panel.metrics.includes("total")) return "total";
  return panel.metrics.find((m) => !RATE_METRICS.has(m)) ?? null;
}

function TablePanel({ panel, ctx, settings }: { panel: PanelDef; ctx: PanelContext; settings: ChartSettings }) {
  const result = usePanelQuery(panel, ctx, settings);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<SortState | null>(null);

  const rows = result && result !== "no-selection" ? result.rows : EMPTY_ROWS;
  const filtered = useMemo(() => filterTree(rows, query), [rows, query]);
  // Relevance is the ordering while searching, unless a column was clicked:
  // an explicit sort is an instruction and outranks the match score.
  const view = useMemo(() => sortTree(filtered.rows, sort), [filtered.rows, sort]);
  useAutoExpand(filtered.autoOpen, setExpanded);
  // Above the early returns, where the hooks have to be. It depends on the
  // panel rather than the response, so there is nothing to wait for.
  const pool = colorPoolFor(panel.source, panel.groupBy[0]);
  const rowLink = useRowLink(pool);

  if (result === "no-selection") return <div className="empty">Select a fight</div>;
  if (!result) return <div className="empty">Loading…</div>;

  const barMetric = barMetricFor(panel);
  const maxBar = barMetric
    ? rows.reduce((max, r) => Math.max(max, r.metrics[barMetric] ?? 0), 0)
    : 0;
  // Rows naming the opposing side don't claim: a table of mob names would
  // otherwise spend the player palette on mobs. They still show a color when
  // the entity already has one. Every other grouping claims within its own
  // pool, where there is nothing to spend but its own slots.
  const claimsColor = !(panel.groupBy[0] === "target" && pool === ENTITY_POOL);

  const renderRow = (
    row: QueryRow,
    depth: number,
    path: string,
    parentValue: number,
    maxSibling: number,
  ): JSX.Element[] => {
    const hasChildren = (row.children?.length ?? 0) > 0;
    const isOpen = expanded.has(path);
    const value = barMetric ? row.metrics[barMetric] ?? 0 : 0;
    let rowStyle: React.CSSProperties | undefined;
    let chip: JSX.Element | null = null;
    let share: JSX.Element | null = null;
    if (depth === 0) {
      // Identity, not rank: the entity keeps its color across every panel.
      const color = claimsColor
        ? ctx.colors.claim(row.key, pool)
        : ctx.colors.lookup(row.key, pool);
      if (barMetric && maxBar > 0) {
        rowStyle = meterStyle(color, (value / maxBar) * 100);
        chip = <span className="color-chip" style={{ background: color }} />;
      }
    } else if (barMetric && maxSibling > 0) {
      // LENGTH is the share of the parent, so a breakdown's bars fill the row
      // exactly once between them. Scaling to the biggest sibling instead drew
      // the largest slice full-width whatever it was worth — a 64.7% child
      // reading as a full bar with "64.7%" printed on it, the length and the
      // number disagreeing in the same breath.
      //
      // HUE still ranks within the breakdown, which is a different question and
      // so does not repeat the length: biggest reads green, the tail red, even
      // where every slice is small. A parent summing to zero has no share to
      // take, and falls back to the ranking for both.
      const rank = value / maxSibling;
      const fill = parentValue > 0 ? value / parentValue : rank;
      rowStyle = meterStyle(heatColor(rank), fill * 100, HEAT_ALPHA);
      if (parentValue > 0) {
        share = (
          <SharePct
            pct={(value / parentValue) * 100}
            title={`${fmtMetric(barMetric, value)} of ${fmtMetric(barMetric, parentValue)} ${
              METRIC_LABELS[barMetric] ?? barMetric
            }`}
          />
        );
      }
    }

    // Only top-level rows name an entity the rest of the app knows; a child is
    // a spell or an item under it, which is a different pool.
    const link = depth === 0 ? rowLink(row.key) : null;
    // What this row's name is a name *of* decides whether it gets a lookup
    // door — the mob at the top, the item under it, never a player.
    const lookupKind = lookupKindFor(panel.source, panel.groupBy[depth]);

    const out = [
      <tr
        key={path}
        className={
          `${depth > 0 ? "child-row" : ""} ${
            depth === 0 && row.key === ctx.character ? "self-row" : ""
          } ${link?.className ?? ""}`.trim() || undefined
        }
        style={rowStyle}
        onMouseEnter={link?.onMouseEnter}
        onMouseLeave={link?.onMouseLeave}
      >
        <td style={{ paddingLeft: depth * 16 + 8 }}>
          {hasChildren ? (
            <button className="expander" onClick={() => setExpanded(toggle(expanded, path))}>
              {isOpen ? "▾" : "▸"}
            </button>
          ) : (
            <span className="expander-spacer" />
          )}
          {chip}
          <Highlight text={row.label} hit={filtered.hits.get(path)} />
          {lookupKind && <LookupLink kind={lookupKind} name={row.label} install={ctx.install} />}
          {share}
        </td>
        {panel.metrics.map((m) => (
          <td key={m} className="num">
            {fmtMetric(m, row.metrics[m] ?? 0)}
          </td>
        ))}
      </tr>,
    ];
    if (hasChildren && isOpen) {
      const children = row.children!;
      const maxChild = barMetric ? maxOf(children, barMetric) : 0;
      for (const child of children) {
        out.push(...renderRow(child, depth + 1, `${path}/${child.key}`, value, maxChild));
      }
    }
    return out;
  };

  return (
    <div className="table-panel">
      <TableSearch
        value={query}
        onChange={setQuery}
        placeholder="Filter rows…"
        shown={filtered.rows.length}
        total={filtered.totalRows}
      />
      <div className="table-scroll">
        <table>
          <thead>
            <tr>
              <SortHeader label="Name" sortKey={NAME_SORT} sort={sort} onSort={setSort} />
              {panel.metrics.map((m) => (
                <SortHeader
                  key={m}
                  label={METRIC_LABELS[m] ?? m}
                  sortKey={m}
                  sort={sort}
                  onSort={setSort}
                  numeric
                />
              ))}
            </tr>
          </thead>
          <tbody>{view.flatMap((row) => renderRow(row, 0, row.key, 0, 0))}</tbody>
        </table>
        {view.length === 0 && <div className="empty">No rows match “{query}”</div>}
      </div>
    </div>
  );
}

/** Stable identity, so the filter memo doesn't rebuild on every render. */
const EMPTY_ROWS: QueryRow[] = [];

/**
 * Heat rows carry their meaning in the hue, not just the length, so they sit a
 * little stronger than the entity-colored rows above them — where the tint is
 * a secondary cue on top of a name and a color chip.
 */
// The heat ramp carries magnitude, so it runs warmer than the entity tint —
// but not past 30%, which put --muted-raised at 3.91:1 over the olive stop.
const HEAT_ALPHA = 0.3;

function maxOf(rows: QueryRow[], metric: string): number {
  return rows.reduce((max, r) => Math.max(max, r.metrics[metric] ?? 0), 0);
}

// ---- line ------------------------------------------------------------------

const BREAK_MS = 30_000;

/** Series name carrying the fight bands; kept out of the legend by name. */
const FIGHT_BANDS = "__fights";

/** And for the zone/level strip. */
const CONTEXT_STRIP = "__context";

// Ceiling on a zero-filled line before it stops being worth drawing. 20k
// points is still smooth at any panel size; a whole multi-day log at a
// 1-second bucket is two orders of magnitude past that.
const MAX_FILLED_POINTS = 20_000;

// Damage, healing and tanking read as rates — damage *per second* is the unit
// people expect off a DPS chart. XP %, faction standing and coin are amounts:
// dividing them by the bucket width produces a per-second figure that rounds
// to zero and tells the reader nothing.
const RATE_SOURCES = new Set<QuerySource>(["damage", "healing", "tanking"]);

// fmtNum rounds to whole numbers below 1K, which suits damage but erases XP %
// and coin — values that legitimately sit under 1.
function fmtLineValue(value: number): string {
  return Math.abs(value) < 10 ? Number(value.toFixed(2)).toString() : fmtNum(value);
}

function LinePanel({
  panel,
  ctx,
  settings,
}: {
  panel: PanelDef;
  ctx: PanelContext;
  settings: ChartSettings;
}) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const { windowBuckets, spanSec } = settings;
  const result = usePanelQuery(panel, ctx, settings);
  const [isZoomed, setIsZoomed] = useState(false);
  // The bucket the server actually aggregated at. Everything downstream —
  // step size, window length, alignment — has to use this and not the panel's
  // nominal width, or the chart walks a grid the data is not on.
  const bucketSeconds = panelBucketSeconds(panel, ctx.frame, settings, ctx.logSpanSeconds);
  // Scaled with the bucket, so a long range keeps the same shape of smoothing
  // instead of quietly falling back to one raw bucket.
  const smoothingSec = panelWindowSeconds(panel, ctx.frame, settings, ctx.logSpanSeconds);
  const bandsKey = fightBandsKey(ctx.fights, bucketSeconds);
  // See DpsChart: "fit" and an active zoom both mean the viewport is not the
  // clock's to move.
  const scrollWindow: [number, number] | null =
    ctx.scrollNowMs !== null && spanSec !== "fit" && !isZoomed
      ? bucketAlignedWindow(ctx.scrollNowMs, spanSec + smoothingSec, bucketSeconds)
      : null;
  const suppressZoomEventRef = useRef(false);
  const extentRef = useRef<[number, number] | null>(null);
  const zoomRangeRef = useRef<[number, number] | null>(null);
  // What is plotted, for the nearest-line hover; refilled with the series.
  const hoverLines = useRef<HoverLine[]>([]);
  const hoverRef = useRef<{ reapply: () => void } | null>(null);
  // Identity + scope, so the ceiling survives a remount but not a real change
  // of what is being plotted.
  const axisKey = `${panel.id}|${JSON.stringify(ctx.frame)}|${spanSec}|${windowBuckets}`;

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
    const chart = echarts.init(divRef.current, chartTheme());
    chartRef.current = chart;
    chart.on("datazoom", (params: unknown) => {
      if (suppressZoomEventRef.current) {
        return;
      }
      const p = params as { start?: number; end?: number; batch?: { start?: number; end?: number }[] };
      const window = p.batch?.[0] ?? p;
      setIsZoomed(!(window.start === 0 && window.end === 100));
      const dz = (chart.getOption() as { dataZoom?: { startValue?: number; endValue?: number }[] })
        .dataZoom?.[0];
      if (typeof dz?.startValue === "number" && typeof dz?.endValue === "number") {
        zoomRangeRef.current = [dz.startValue, dz.endValue];
      }
    });
    chart.getZr().on("dblclick", resetZoom);
    const detachWheelZoom = attachWheelZoom(chart, { left: 48, right: 10 }, () => extentRef.current);
    const hover = attachNearestLineHover(chart, () => hoverLines.current);
    hoverRef.current = hover;
    const observer = new ResizeObserver(() => chart.resize());
    observer.observe(divRef.current);
    return () => {
      observer.disconnect();
      hoverRef.current = null;
      hover.detach();
      detachWheelZoom();
      chart.dispose();
      chartRef.current = null;
    };
  }, [resetZoom]);

  // After the effect above, which is what creates the chart it attaches to.
  const pool = colorPoolFor(panel.source, panel.groupBy[0]);
  const linkKeys = useChartLink(chartRef, pool);

  useEffect(() => {
    if (!chartRef.current) return;
    if (!result || result === "no-selection") {
      chartRef.current.clear();
      return;
    }

    const ranked = [...result.rows].sort((a, b) => (b.metrics.total ?? 0) - (a.metrics.total ?? 0));
    // Eight is the series cap, not the palette length: past it a chart
    // folds into "Other" rather than inventing a ninth hue.
    const top = ranked.slice(0, CHART_SERIES_LIMIT);
    const rest = ranked.slice(CHART_SERIES_LIMIT);

    const allSeconds = new Set<number>();
    for (const row of ranked) {
      for (const p of row.series ?? []) {
        allSeconds.add(new Date(p.bucketStart).getTime());
      }
    }
    const timeline = [...allSeconds].sort((a, b) => a - b);

    const step = Math.max(1, bucketSeconds) * 1000;
    // Scrolling with the wall clock: the window IS [now - span, now], so the
    // segment is that window rather than whatever the data happens to cover.
    // Buckets with nothing in them read as zero, so quiet time draws as a line
    // along the floor that keeps moving — and the rolling mean decays into it
    // instead of freezing at the last value. Bounded by the span, so the point
    // count cannot run away no matter how stale the log is.
    const segments: [number, number][] = [];
    if (scrollWindow) {
      segments.push(scrollWindow);
    } else if (timeline.length > 0) {
      const first = timeline[0];
      const last = timeline[timeline.length - 1];
      if ((last - first) / step + 1 <= MAX_FILLED_POINTS) {
        segments.push([first, last]);
      } else {
        const breakMs = Math.max(BREAK_MS, step * 2);
        for (const t of timeline) {
          const previous = segments[segments.length - 1];
          if (previous && t - previous[1] <= breakMs) {
            previous[1] = t;
          } else {
            segments.push([t, t]);
          }
        }
      }
    }

    extentRef.current =
      segments.length > 0 ? [segments[0][0], segments[segments.length - 1][1]] : null;

    // Rate sources average to a per-second figure; amount sources stay in
    // their own units as a rolling mean per bucket.
    const perBucket = RATE_SOURCES.has(panel.source) ? Math.max(1, bucketSeconds) : 1;
    const ringBuckets = Math.max(1, Math.round(smoothingSec / Math.max(1, bucketSeconds)));
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
          if (ring.length > ringBuckets) {
            sum -= ring.shift()!;
          }
          points.push([t, sum / (ring.length * perBucket)]);
        }
        points.push([end + step / 2, null]);
      }
      return points;
    };

    // What the labels on this chart stand for, so hovering a line can say who
    // it is. Rebuilt with the series, since the ranking decides both.
    linkKeys.current = {
      series: new Map(top.map((row) => [row.label, row.key])),
      items: [],
    };

    const series: echarts.SeriesOption[] = top.map((row) => ({
      name: row.label,
      type: "line",
      showSymbol: false,
      lineStyle: { width: 2 },
      color: ctx.colors.claim(row.key, pool),
      data: smoothed([row]),
      connectNulls: false,
      // No triggerLineEvent: hover is the nearest line to the pointer, not
      // the stroke under it — see attachNearestLineHover.
      ...SERIES_EMPHASIS,
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
        ...SERIES_EMPHASIS,
      });
    }
    hoverLines.current = series.map((s) => ({
      name: s.name as string,
      data: s.data as [number, number | null][],
    }));


    // Axis top from what is actually plotted, held steady by stableAxisMax.
    let dataMax = 0;
    let dataMin = 0;
    for (const s of series) {
      for (const point of (s.data as [number, number | null][] | undefined) ?? []) {
        if (point[1] === null) continue;
        dataMax = Math.max(dataMax, point[1]);
        dataMin = Math.min(dataMin, point[1]);
      }
    }

    const axisTop = heldAxisMax(axisKey, dataMax);

    // A floor as steady as the ceiling. Only the sources that go below zero
    // reach for one — faction standing is the only one that does, and it does
    // constantly — but left to ECharts that edge is recomputed every render,
    // so a stabilised top sat above a bottom that wandered. Same rule, same
    // reasons, run on the depth instead of the height.
    const axisFloor = dataMin < 0 ? -heldAxisMax(`${axisKey}|floor`, -dataMin) : 0;


    // A fixed span pins the axis to [latest − span, latest]: constant width,
    // sliding right edge, so the chart doesn't rescale as points arrive. The
    // right edge is the newest data point rather than wall clock, so replayed
    // logs behave. Zooming takes over until it is reset.
    let axisMin: number | null = null;
    let axisMax: number | null = null;
    if (spanSec !== "fit" && segments.length > 0 && !isZoomed) {
      axisMax = scrollWindow ? scrollWindow[1] : segments[segments.length - 1][1];
      axisMin = axisMax - spanSec * 1000;
    }

    // Fight bands behind the line — same backdrop the DPS chart gets, so an
    // XP trough reads as "between pulls" rather than an unexplained gap.
    const plotHeight = (divRef.current?.clientHeight ?? 0) - 26 - 24; // grid top/bottom
    const plotWidth = (divRef.current?.clientWidth ?? 0) - 48 - 10; // grid left/right
    const markArea = extentRef.current
      ? fightMarkArea(
          ctx.fights,
          axisMin ?? extentRef.current[0],
          axisMax ?? extentRef.current[1],
          plotHeight,
          plotWidth,
          ctx.fightLabelPx,
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


    // The context strip along the top: where the character was and what level
    // they were. Drawn after the fight bands and hanging from the axis top, so
    // it stacks above them rather than tinting the same pixels twice.
    const contextArea = extentRef.current
      ? contextMarkArea(
          ctx.context,
          ctx.contextMode,
          axisMin ?? extentRef.current[0],
          axisMax ?? extentRef.current[1],
          axisTop,
          axisFloor,
          plotWidth,
        )
      : undefined;
    if (contextArea) {
      series.push({
        name: CONTEXT_STRIP,
        type: "line",
        data: [],
        silent: true,
        markArea: contextArea,
      } as echarts.SeriesOption);
    }

    chartRef.current.setOption(
      {
        backgroundColor: "transparent",
        animation: false,
        grid: GRID.line,
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
          data: top.map((row) => row.label).concat(rest.length > 0 ? [`Other (${rest.length})`] : []),
        },
        tooltip: {
          trigger: "axis",
          position: offsetTooltip,
          // On the canvas, not in the DOM — see DpsChart for the measurement.
          renderMode: "richText",
          // The crosshair is the theme's; this panel used to leave it at
          // ECharts' default while DpsChart styled its own.
          valueFormatter: (v: unknown) => (typeof v === "number" ? fmtLineValue(v) : "—"),
        },
        xAxis: {
          type: "time",
          min: axisMin,
          max: axisMax,
        },
        yAxis: {
          type: "value",
          // Faction standing genuinely goes negative, so zero is only the
          // floor when the data says it is.
          min: axisFloor,
          max: axisTop,
          axisLabel: { formatter: (v: number) => fmtLineValue(v) },
        },
        series,
      },
      { replaceMerge: ["series"] },
    );
    // Replacing the series dropped their emphasis; put the hover back.
    linkKeys.reapply();
    hoverRef.current?.reapply();

    chartRef.current.dispatchAction({
      type: "takeGlobalCursor",
      key: "dataZoomSelect",
      dataZoomSelectActive: true,
    });
  }, [result, panel.source, bucketSeconds, smoothingSec, spanSec, isZoomed, bandsKey, ctx.fightLabelPx, ctx.scrollNowMs]);

  if (result === "no-selection") return <div className="empty">Select a fight</div>;
  return (
    <div className="chart-wrap">
      <div ref={divRef} className="chart" />
      {isZoomed && (
        <span className="zoom-actions">
          <button
            className="zoom-reset"
            onClick={() => {
              const range = zoomRangeRef.current;
              if (range) ctx.onAdoptRange(range[0], range[1]);
            }}
            title="Make this zoomed window the time range every panel reports over"
          >
            ⤢ set as time range
          </button>
          <button
            className="zoom-reset"
            onClick={resetZoom}
            title="Back to the full view (or double-click the chart)"
          >
            ↺ reset zoom
          </button>
        </span>
      )}
    </div>
  );
}

// ---- bar -------------------------------------------------------------------

function BarPanel({ panel, ctx, settings }: { panel: PanelDef; ctx: PanelContext; settings: ChartSettings }) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const result = usePanelQuery(panel, ctx, settings);
  const metric = panel.primaryMetric;

  useEffect(() => {
    if (!divRef.current) return;
    const chart = echarts.init(divRef.current, chartTheme());
    chartRef.current = chart;
    const observer = new ResizeObserver(() => chart.resize());
    observer.observe(divRef.current);
    return () => {
      observer.disconnect();
      chart.dispose();
      chartRef.current = null;
    };
  }, []);

  // After the effect above, which is what creates the chart it attaches to.
  const linkKeys = useChartLink(chartRef, colorPoolFor(panel.source, panel.groupBy[0]));

  useEffect(() => {
    if (!chartRef.current) return;
    if (!result || result === "no-selection") {
      chartRef.current.clear();
      return;
    }

    const ranked = [...result.rows]
      .sort((a, b) => (b.metrics[metric] ?? 0) - (a.metrics[metric] ?? 0))
      .slice(0, 12);

    // One series of many entities: here a bar is identified by where it sits,
    // not by the name of the series it belongs to.
    linkKeys.current = { series: new Map(), items: ranked.map((r) => r.key) };

    chartRef.current.setOption(
      {
        backgroundColor: "transparent",
        animation: false,
        grid: GRID.ability,
        tooltip: {
          position: offsetTooltip,
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
          axisLabel: { color: chartInk().ink2, fontSize: 11, width: 130, overflow: "truncate" },
        },
        series: [
          {
            type: "bar",
            data: ranked.map((r) => r.metrics[metric] ?? 0),
            barWidth: 13,
            itemStyle: { color: SERIES_COLORS[0] },
            ...ITEM_EMPHASIS,
            label: {
              show: true,
              position: "right",
              color: chartInk().muted,
              fontSize: 11,
              formatter: (p: { value: unknown }) => fmtMetric(metric, Number(p.value)),
            },
          },
        ],
      },
      { replaceMerge: ["series"] },
    );
    linkKeys.reapply(); // replacing the series dropped their emphasis
  }, [result, metric]);

  if (result === "no-selection") return <div className="empty">Select a fight</div>;
  return <div ref={divRef} className="chart" />;
}

// ---- tile ------------------------------------------------------------------

function TilePanel({ panel, ctx, settings }: { panel: PanelDef; ctx: PanelContext; settings: ChartSettings }) {
  const result = usePanelQuery(panel, ctx, settings);
  if (result === "no-selection") return <div className="empty">Select a fight</div>;
  const value = result?.totals[panel.primaryMetric] ?? 0;
  return (
    <div className="tile-body">
      <span className="tile-value">{result ? fmtMetric(panel.primaryMetric, value) : "…"}</span>
      <span className="tile-label">{METRIC_LABELS[panel.primaryMetric] ?? panel.primaryMetric}</span>
    </div>
  );
}

// ---- drop rate -------------------------------------------------------------

/**
 * Loot per kill, by mob.
 *
 * A drop rate needs a denominator the loot source does not carry: how many of
 * that mob died. That number lives in the death source, so this panel runs two
 * queries over the same scope and joins them on the mob's name — the one place
 * in the app where a panel is more than a single query, and the reason it is a
 * viz of its own rather than a table with extra columns.
 *
 * The join is case-insensitive because the two sources disagree on the case of
 * an NPC's leading article: the loot grammar keeps the corpse's name verbatim
 * ("a bandit") while the death grammar normalizes it ("A bandit").
 */
interface DropColumn {
  key: string;
  label: string;
  /** Blank where the column has no meaning at that depth. */
  format: (row: QueryRow, depth: number) => string;
}

const DROP_COLUMNS: DropColumn[] = [
  {
    key: "kills",
    label: "Kills",
    format: (row, depth) => (depth > 0 ? "" : String(Math.round(row.metrics.kills ?? 0))),
  },
  {
    key: "loots",
    label: "Drops",
    format: (row) => String(Math.round(row.metrics.loots ?? 0)),
  },
  {
    key: "dropRate",
    label: "Per kill",
    format: (row) => (row.metrics.kills === 0 ? "—" : fmtRate(row.metrics.dropRate ?? 0)),
  },
];

function useKillCounts(
  panel: PanelDef,
  ctx: PanelContext,
  settings: ChartSettings,
): Map<string, number> | null {
  const [kills, setKills] = useState<Map<string, number> | null>(null);
  // Same scope as the loot query, so the rate is over one window of time. Pet
  // rollup stays off: it merges owners with pets, and these keys are mob names.
  const spec = buildSpec(
    {
      ...panel,
      viz: "table",
      source: "deaths",
      groupBy: ["player"],
      metrics: ["deaths"],
      excludeFlags: [],
      playerFilter: [],
      spellFilter: [],
      ownerOnly: false, // rows are mob names here, not players
    },
    ctx.frame,
    false,
    settings,
    ctx.logSpanSeconds,
    "",
    ctx.playedTimeOnly,
  );
  const specKey = JSON.stringify(spec);

  useEffect(() => {
    let cancelled = false;
    api
      .query(ctx.sessionId, JSON.parse(specKey))
      .then((r) => {
        if (!cancelled) {
          setKills(
            new Map(r.rows.map((row) => [row.key.toLowerCase(), row.metrics.deaths ?? 0])),
          );
        }
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [ctx.sessionId, specKey, ctx.refreshKey]);

  return kills;
}

function DropRatePanel({
  panel,
  ctx,
  settings,
}: {
  panel: PanelDef;
  ctx: PanelContext;
  settings: ChartSettings;
}) {
  const result = usePanelQuery(panel, ctx, settings);
  const kills = useKillCounts(panel, ctx, settings);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<SortState | null>(null);

  const rows = useMemo(() => {
    if (!result || result === "no-selection" || !kills) {
      return EMPTY_ROWS;
    }
    const out: QueryRow[] = [];
    for (const mob of result.rows) {
      const drops = mob.metrics.loots ?? 0;
      if (drops <= 0) {
        continue; // a coin-only corpse is not a drop table
      }
      const killCount = kills.get(mob.key.toLowerCase()) ?? 0;
      const children = (mob.children ?? [])
        .filter((item) => (item.metrics.loots ?? 0) > 0)
        .map((item) => ({
          ...item,
          children: undefined,
          metrics: {
            ...item.metrics,
            kills: killCount,
            dropRate: killCount > 0 ? ((item.metrics.loots ?? 0) / killCount) * 100 : 0,
          },
        }));
      out.push({
        ...mob,
        children,
        metrics: {
          ...mob.metrics,
          kills: killCount,
          dropRate: killCount > 0 ? (drops / killCount) * 100 : 0,
        },
      });
    }
    return out;
  }, [result, kills]);

  const filtered = useMemo(() => filterTree(rows, query), [rows, query]);
  const view = useMemo(() => sortTree(filtered.rows, sort), [filtered.rows, sort]);
  useAutoExpand(filtered.autoOpen, setExpanded);
  const pool = colorPoolFor(panel.source, panel.groupBy[0]);
  const rowLink = useRowLink(pool);

  if (panel.source !== "loot") {
    return <div className="empty">Drop rates need the loot source</div>;
  }
  if (result === "no-selection") return <div className="empty">Select a fight</div>;
  if (!result || !kills) return <div className="empty">Loading…</div>;

  const maxDrops = maxOf(rows, "loots");

  const renderRow = (
    row: QueryRow,
    depth: number,
    path: string,
    parentDrops: number,
    maxSibling: number,
  ): JSX.Element[] => {
    const hasChildren = (row.children?.length ?? 0) > 0;
    const isOpen = expanded.has(path);
    const drops = row.metrics.loots ?? 0;
    let rowStyle: React.CSSProperties | undefined;
    let chip: JSX.Element | null = null;
    if (depth === 0) {
      // Mobs, in loot's own pool — nothing else claims there, so looking up
      // would leave every row gray.
      const color = ctx.colors.claim(row.key, pool);
      rowStyle = meterStyle(color, maxDrops > 0 ? (drops / maxDrops) * 100 : 0);
      chip = <span className="color-chip" style={{ background: color }} />;
    } else if (maxSibling > 0) {
      // Same reading as the items table: length is this item's share of what
      // the mob dropped, so a mob's breakdown fills its row exactly once. Hue
      // ranks within the mob — kills are fixed inside one, so ranking by drops
      // and by drop rate are the same ordering and the tint reads as either.
      const rank = drops / maxSibling;
      const fill = parentDrops > 0 ? drops / parentDrops : rank;
      rowStyle = meterStyle(heatColor(rank), fill * 100, HEAT_ALPHA);
    }

    const link = depth === 0 ? rowLink(row.key) : null;

    const out = [
      <tr
        key={path}
        className={
          `${depth > 0 ? "child-row" : ""} ${
            depth === 0 && row.key === ctx.character ? "self-row" : ""
          } ${link?.className ?? ""}`.trim() || undefined
        }
        style={rowStyle}
        onMouseEnter={link?.onMouseEnter}
        onMouseLeave={link?.onMouseLeave}
        title={
          depth > 0 && (row.metrics.kills ?? 0) > 0
            ? `${Math.round(drops)} in ${Math.round(row.metrics.kills ?? 0)} kills`
            : undefined
        }
      >
        <td style={{ paddingLeft: depth * 16 + 8 }}>
          {hasChildren ? (
            <button className="expander" onClick={() => setExpanded(toggle(expanded, path))}>
              {isOpen ? "▾" : "▸"}
            </button>
          ) : (
            <span className="expander-spacer" />
          )}
          {chip}
          <Highlight text={row.label} hit={filtered.hits.get(path)} />
          <LookupLink kind={depth === 0 ? "npc" : "item"} name={row.label} install={ctx.install} />
        </td>
        {DROP_COLUMNS.map((c) => (
          <td key={c.key} className="num">
            {c.format(row, depth)}
          </td>
        ))}
      </tr>,
    ];
    if (hasChildren && isOpen) {
      const children = row.children!;
      const maxChild = maxOf(children, "loots");
      for (const child of children) {
        out.push(...renderRow(child, depth + 1, `${path}/${child.key}`, drops, maxChild));
      }
    }
    return out;
  };

  return (
    <div className="table-panel">
      <TableSearch
        value={query}
        onChange={setQuery}
        placeholder="Filter mobs or items…"
        shown={filtered.rows.length}
        total={filtered.totalRows}
      />
      <div className="table-scroll">
        <table>
          <thead>
            <tr>
              <SortHeader label="Mob" sortKey={NAME_SORT} sort={sort} onSort={setSort} />
              {DROP_COLUMNS.map((c) => (
                <SortHeader
                  key={c.key}
                  label={c.label}
                  sortKey={c.key}
                  sort={sort}
                  onSort={setSort}
                  numeric
                />
              ))}
            </tr>
          </thead>
          <tbody>{view.flatMap((row) => renderRow(row, 0, row.key, 0, 0))}</tbody>
        </table>
        {view.length === 0 && (
          <div className="empty">
            {rows.length === 0 ? "No item drops in range" : `No rows match “${query}”`}
          </div>
        )}
      </div>
    </div>
  );
}
