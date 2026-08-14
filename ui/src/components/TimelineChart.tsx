import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as echarts from "echarts";
import { api, type FightInfo, type TimelineItem, type TimelineResult } from "../api";
import { attachWheelZoom, offsetTooltip } from "../chartInteractions";
import { OTHER_COLOR, fmtNum } from "../format";
import { frameScope, type TimeFrame } from "../timeFrame";
import { GRID, chartInk, chartTheme } from "../chartTheme";

interface Props {
  sessionId: string;
  frame: TimeFrame;
  refreshKey: number;
  character: string;
  fights: FightInfo[];
  /** Promote a zoomed window to the app-wide time range. */
  onAdoptRange: (beginMs: number, endMs: number) => void;
}

/**
 * Mark styling, keyed by kind — except casts, which split by what they did so
 * a large mark is never ambiguous between a big hit and a big heal. Four
 * categorical hues (buffs, damage casts, abilities, heal casts), validated
 * together against the panel surface rather than eyeballed; the "nothing
 * happened / ended" marks (interrupt, fizzle, fade, resist) share a neutral
 * gray and rely on their symbol + the legend, and deaths wear the reserved
 * status red with an ✕.
 */
const DEATH_X =
  "path://M2,0 L5,3 L8,0 L10,2 L7,5 L10,8 L8,10 L5,7 L2,10 L0,8 L3,5 L0,2 Z";

const SPAN_COLOR = "#0671d1"; // buffs — SERIES_COLORS slot 4

/**
 * Stances get their own hue and their own lane. A stance is not a buff you
 * happened to cast — it is the state everything else in the lane happened
 * under, so reading it as a separate band under your own marks is the point:
 * the switch is where one band ends and the next begins.
 */
const STANCE_COLOR = "#a2991b"; // SERIES_COLORS slot 5

/**
 * Magnitude rides on SIZE, because hue is already spent on what a mark is.
 * Area is the readable channel for "how big", so the radius follows a square
 * root — a 4× hit must not look 16× larger. The floor is the base size rather
 * than zero: a small hit still has to be visible and hoverable, which costs
 * strict area-proportionality at the bottom of the scale and is worth it.
 */
const MARK_MIN_PX = 9;
const MARK_MAX_PX = 22;

function markSize(item: TimelineItem, peakDamage: number, peakHeal: number): number {
  const peak = item.effect === "heal" ? peakHeal : peakDamage;
  if (!item.amount || peak <= 0) {
    return MARK_MIN_PX;
  }

  const scaled = Math.sqrt(Math.min(item.amount, peak) / peak);
  return MARK_MIN_PX + (MARK_MAX_PX - MARK_MIN_PX) * scaled;
}

/** Casts split on effect; everything else is its bare kind. */
function markKey(item: TimelineItem): string {
  return (item.kind === "cast" || item.kind === "song") && item.effect === "heal"
    ? `${item.kind}:heal`
    : item.kind;
}

const INSTANT_KINDS: {
  key: string;
  name: string;
  color: string;
  symbol: string;
  rotate?: number;
}[] = [
  { key: "cast", name: "casts", color: "#ba5003", symbol: "triangle" },
  { key: "cast:heal", name: "heal casts", color: "#9280f6", symbol: "triangle" },
  { key: "song", name: "songs", color: "#ba5003", symbol: "diamond" },
  { key: "song:heal", name: "heal songs", color: "#9280f6", symbol: "diamond" },
  { key: "ability", name: "abilities", color: "#00814e", symbol: "rect" },
  { key: "interrupt", name: "interrupts", color: OTHER_COLOR, symbol: "triangle", rotate: 180 },
  { key: "fizzle", name: "fizzles", color: OTHER_COLOR, symbol: "emptyCircle" },
  { key: "fade", name: "fades", color: OTHER_COLOR, symbol: "emptyDiamond" },
  { key: "resist", name: "resists", color: OTHER_COLOR, symbol: "pin" },
  { key: "death", name: "deaths", color: "#e56386", symbol: DEATH_X },
];

const ROW_HEIGHT = 20;

/**
 * Floor on how often the timeline refetches. Every other panel can follow the
 * live tick because its query is small; this one returns every cast, ability
 * and death in the window.
 */
const TIMELINE_MIN_REFRESH_MS = 5000;

interface Row {
  /** Actor name shown on the axis — only on the actor's first row. */
  label: string;
  instants: TimelineItem[];
  spans: TimelineItem[];
  /** Held stances; their own band, never mixed with buff spans. */
  stances: TimelineItem[];
}

function emptyRow(fill: Partial<Row>): Row {
  return { label: "", instants: [], spans: [], stances: [], ...fill };
}

/**
 * One lane block per actor: a row of instant marks, then as many span rows as
 * concurrent buffs need (greedy interval packing). Log owner first, then other
 * players, then NPCs (anything sharing a fight name).
 */
/**
 * Lanes drawn at once. The chart sizes itself at ROW_HEIGHT per lane, so this
 * is a height cap as much as a legibility one: 185 actors — which a 24-hour
 * range really does produce — is a 3,700px canvas inside a 300px panel, and
 * nobody reads a timeline that tall anyway.
 */
const MAX_LANES = 24;

function buildRows(
  items: TimelineItem[],
  character: string,
  npcNames: Set<string>,
): { rows: Row[]; omitted: number } {
  const byActor = new Map<string, TimelineItem[]>();
  for (const item of items) {
    const list = byActor.get(item.actor);
    if (list) {
      list.push(item);
    } else {
      byActor.set(item.actor, [item]);
    }
  }

  const actors = [...byActor.keys()].sort((a, b) => {
    const rank = (name: string) =>
      name.toLowerCase() === character.toLowerCase() ? 0 : npcNames.has(name) ? 2 : 1;
    return rank(a) - rank(b) || a.localeCompare(b);
  });

  // Which actors survive the cap is decided by how much they did; the ORDER
  // they are drawn in stays you-then-players-then-NPCs, so the reading order
  // never shuffles. Dropping by activity beats dropping by alphabet.
  const kept = new Set(
    [...actors]
      .sort((a, b) => (byActor.get(b)?.length ?? 0) - (byActor.get(a)?.length ?? 0))
      .slice(0, MAX_LANES),
  );
  const omitted = actors.length - kept.size;

  const rows: Row[] = [];
  for (const actor of actors) {
    if (!kept.has(actor)) {
      continue;
    }

    const all = byActor.get(actor)!;
    const instants = all.filter((i) => !i.end);
    const byStart = (a: TimelineItem, b: TimelineItem) => a.start.localeCompare(b.start);
    const stances = all.filter((i) => i.end && i.kind === "stance").sort(byStart);
    const spans = all.filter((i) => i.end && i.kind !== "stance").sort(byStart);

    const actorRows: Row[] = [];
    if (instants.length > 0) {
      actorRows.push(emptyRow({ instants }));
    }
    // Stances tile the timeline without overlapping, so however many switches
    // there were they are one band — and it gets a row of its own, because the
    // state you fought in is not one more thing you cast.
    if (stances.length > 0) {
      actorRows.push(emptyRow({ stances }));
    }

    // Buff rows start below whatever the two rows above claimed.
    const offset = actorRows.length;
    const rowEnds: number[] = [];
    for (const span of spans) {
      const start = new Date(span.start).getTime();
      const slot = rowEnds.findIndex((end) => start >= end + 1000);
      if (slot >= 0) {
        rowEnds[slot] = new Date(span.end!).getTime();
        actorRows[offset + slot].spans.push(span);
      } else {
        rowEnds.push(new Date(span.end!).getTime());
        actorRows.push(emptyRow({ spans: [span] }));
      }
    }
    if (actorRows.length > 0) {
      actorRows[0].label = actor;
      rows.push(...actorRows);
    }
  }

  return { rows, omitted };
}

function fmtTime(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour12: false });
}

function spanTooltip(item: TimelineItem): string {
  const seconds = Math.round((new Date(item.end!).getTime() - new Date(item.start).getTime()) / 1000);
  const from = item.startsBefore ? "before selection" : fmtTime(item.start);
  const to = item.endsAfter ? "beyond selection" : fmtTime(item.end!);
  return (
    `<b>${item.label}</b><br/>buff on ${item.actor}<br/>` +
    `${from} → ${to} · ${seconds}s shown`
  );
}

function stanceTooltip(item: TimelineItem): string {
  const seconds = Math.round((new Date(item.end!).getTime() - new Date(item.start).getTime()) / 1000);
  const from = item.startsBefore ? "before selection" : fmtTime(item.start);
  const to = item.endsAfter ? "beyond selection" : fmtTime(item.end!);
  return `<b>${item.label} stance</b><br/>${from} → ${to} · ${seconds}s shown`;
}

function instantTooltip(item: TimelineItem, kindName: string): string {
  // The size says "big"; the tooltip has to say how big, since a size read off
  // a scale with no axis is an ordering, not a number.
  const landed = item.amount
    ? ` · ${fmtNum(item.amount)} ${item.effect === "heal" ? "healed" : "damage"}`
    : "";
  return `<b>${item.label}</b><br/>${kindName.replace(/s$/, "")} · ${item.actor} · ${fmtTime(item.start)}${landed}`;
}

/**
 * Gantt-style event timeline (the seed of the event/annotation system): per
 * PC/NPC lanes with instant casts, abilities, deaths, and resists as marks,
 * plus buff spans derived from the owner's cast → "worn off" pairs. Spell-DB
 * integration will add received buffs and true durations later.
 */
export function TimelineChart({
  sessionId,
  frame,
  refreshKey,
  character,
  fights,
  onAdoptRange,
}: Props) {
  // Ask by scope, not by enumerating the frame's fights. At a 24-hour range
  // that list is 1,300 ids and 55 KB of request body describing a window the
  // server can derive from 79 bytes — and it churns every time a fight is
  // added, refetching for a reason that has nothing to do with the window.
  const scopeKey = JSON.stringify(frameScope(frame));
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const [result, setResult] = useState<TimelineResult | null>(null);
  const [isZoomed, setIsZoomed] = useState(false);
  const suppressZoomEventRef = useRef(false);
  const extentRef = useRef<[number, number] | null>(null);
  const zoomRangeRef = useRef<[number, number] | null>(null);

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
      if (suppressZoomEventRef.current) return;
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
    const detachWheelZoom = attachWheelZoom(chart, { left: 100, right: 12 }, () => extentRef.current);
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
    resetZoom();
  }, [scopeKey, resetZoom]);

  // A new window is answered at once; a live tick is not. refreshKey bumps
  // about once a second, and at a long range this query is ~1 MB and ~800 ms,
  // so following it 1:1 means never finishing one before starting the next
  // and redrawing thousands of marks in between. The timeline is a detail
  // view — a few seconds stale costs nothing.
  const lastFetchRef = useRef(0);
  const lastScopeRef = useRef(scopeKey);
  if (lastScopeRef.current !== scopeKey) {
    lastScopeRef.current = scopeKey;
    lastFetchRef.current = 0; // a window the user just chose is not "recent"
  }

  useEffect(() => {
    let cancelled = false;
    let timer: number | undefined;
    const run = () => {
      lastFetchRef.current = Date.now();
      api
        .timeline(sessionId, JSON.parse(scopeKey))
        .then((r) => !cancelled && setResult(r))
        .catch(() => undefined);
    };

    const since = Date.now() - lastFetchRef.current;
    if (since >= TIMELINE_MIN_REFRESH_MS) {
      run();
    } else {
      timer = window.setTimeout(run, TIMELINE_MIN_REFRESH_MS - since);
    }

    return () => {
      cancelled = true;
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [sessionId, scopeKey, refreshKey]);

  // Keyed on the count and the newest name, NOT the array. The fights array is
  // replaced on every hub push, and depending on it rebuilt this Set, which
  // rebuilt every row, which redrew the whole timeline several times a second
  // — regardless of how rarely the data behind it was refetched. Fights only
  // ever append, so a new name can only arrive with a new fight.
  const npcKey = `${fights.length}|${fights[fights.length - 1]?.name ?? ""}`;
  const npcNames = useMemo(
    () => new Set(fights.map((f) => f.name)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [npcKey],
  );
  const { rows, omitted } = useMemo(
    () =>
      result ? buildRows(result.items, character, npcNames) : { rows: [], omitted: 0 },
    [result, character, npcNames],
  );

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;
    if (!result || rows.length === 0) {
      chart.clear();
      return;
    }

    const rangeBegin = result.rangeBegin ? new Date(result.rangeBegin).getTime() : null;
    const rangeEnd = result.rangeEnd ? new Date(result.rangeEnd).getTime() : null;
    extentRef.current = rangeBegin !== null && rangeEnd !== null ? [rangeBegin, rangeEnd] : null;

    const labels = rows.map((r) => r.label);

    const spanData: { value: [number, number, number]; item: TimelineItem }[] = [];
    const stanceData: { value: [number, number, number]; item: TimelineItem }[] = [];
    const instantData = new Map<string, { value: [number, number]; item: TimelineItem }[]>();
    rows.forEach((row, rowIndex) => {
      for (const span of row.spans) {
        spanData.push({
          value: [new Date(span.start).getTime(), new Date(span.end!).getTime(), rowIndex],
          item: span,
        });
      }
      for (const stance of row.stances) {
        stanceData.push({
          value: [new Date(stance.start).getTime(), new Date(stance.end!).getTime(), rowIndex],
          item: stance,
        });
      }
      for (const instant of row.instants) {
        const key = markKey(instant);
        let list = instantData.get(key);
        if (!list) {
          list = [];
          instantData.set(key, list);
        }
        list.push({ value: [new Date(instant.start).getTime(), rowIndex], item: instant });
      }
    });

    // Frame-wide peaks: the scale must not be re-derived per lane, or two
    // equal-looking marks could be 400 and 5,000.
    let peakDamage = 0;
    let peakHeal = 0;
    for (const row of rows) {
      for (const instant of row.instants) {
        if (!instant.amount) continue;
        if (instant.effect === "heal") {
          peakHeal = Math.max(peakHeal, instant.amount);
        } else if (instant.effect === "damage") {
          peakDamage = Math.max(peakDamage, instant.amount);
        }
      }
    }

    const series: echarts.SeriesOption[] = [
      {
        name: "buffs",
        type: "custom",
        color: SPAN_COLOR,
        renderItem: (params, apiArg) => {
          const start = apiArg.coord([apiArg.value(0), apiArg.value(2)]);
          const end = apiArg.coord([apiArg.value(1), apiArg.value(2)]);
          const coords = (params as unknown as {
            coordSys: { x: number; y: number; width: number; height: number };
          }).coordSys;
          const barHeight = 12;
          const rect = echarts.graphic.clipRectByRect(
            {
              x: start[0],
              y: start[1] - barHeight / 2,
              width: Math.max(end[0] - start[0], 2),
              height: barHeight,
            },
            coords,
          );
          return (
            rect && {
              type: "rect",
              shape: { ...rect, r: 3 },
              style: { fill: SPAN_COLOR, opacity: 0.85 },
            }
          );
        },
        encode: { x: [0, 1], y: 2 },
        data: spanData,
        tooltip: {
          formatter: (p: unknown) =>
            spanTooltip((p as { data: { item: TimelineItem } }).data.item),
        },
      },
      {
        name: "stances",
        type: "custom",
        color: STANCE_COLOR,
        renderItem: (params, apiArg) => {
          const start = apiArg.coord([apiArg.value(0), apiArg.value(2)]);
          const end = apiArg.coord([apiArg.value(1), apiArg.value(2)]);
          const coords = (params as unknown as {
            coordSys: { x: number; y: number; width: number; height: number };
          }).coordSys;
          const barHeight = 14;
          const rect = echarts.graphic.clipRectByRect(
            {
              x: start[0],
              y: start[1] - barHeight / 2,
              width: Math.max(end[0] - start[0], 2),
              height: barHeight,
            },
            coords,
          );
          if (!rect) {
            return;
          }

          const item = stanceData[(params as { dataIndex: number }).dataIndex]?.item;
          // A band nobody can name is just a colour. It gets its name when
          // there is room for it; the tooltip answers when there isn't.
          const text = rect.width >= 52 ? item?.label ?? "" : "";
          return {
            type: "rect",
            shape: { ...rect, r: 3 },
            style: { fill: STANCE_COLOR, opacity: 0.85 },
            textContent: text
              ? { style: { text, fill: chartInk().onMark, fontSize: 10, fontWeight: 600 } }
              : undefined,
            textConfig: { position: "inside" },
          };
        },
        encode: { x: [0, 1], y: 2 },
        data: stanceData,
        tooltip: {
          formatter: (p: unknown) =>
            stanceTooltip((p as { data: { item: TimelineItem } }).data.item),
        },
      },
      ...INSTANT_KINDS.filter((k) => instantData.has(k.key)).map(
        (k): echarts.SeriesOption => ({
          name: k.name,
          type: "scatter",
          color: k.color,
          symbol: k.symbol,
          // One scale per measure across the whole frame, so a mark of a given
          // size means the same number in every lane. Damage and healing are
          // scaled apart — 5,000 of each is not the same quantity.
          symbolSize: (_v: unknown, p: unknown) =>
            markSize((p as { data: { item: TimelineItem } }).data.item, peakDamage, peakHeal),
          symbolRotate: k.rotate,
          data: instantData.get(k.key)!,
          tooltip: {
            formatter: (p: unknown) =>
              instantTooltip((p as { data: { item: TimelineItem } }).data.item, k.name),
          },
        }),
      ),
    ];

    chart.setOption(
      {
        backgroundColor: "transparent",
        animation: false,
        grid: GRID.timeline,
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
        },
        tooltip: {
          trigger: "item",
          position: offsetTooltip,
        },
        xAxis: {
          type: "time",
          min: rangeBegin,
          max: rangeEnd,
          axisLabel: { color: chartInk().muted, fontSize: 11 },
          // Lanes here are categories, not values, so without vertical rules
          // there is nothing to read a mark's time against but the axis at the
          // bottom — which is far away by the time you are on the fourth lane.
          splitLine: { show: true, lineStyle: { color: "#2c2c2a" } },
        },
        yAxis: {
          type: "category",
          data: rows.map((_, i) => String(i)),
          inverse: true,
          axisLine: { show: false },
          axisTick: { show: false },
          axisLabel: {
            color: "#c3c2b7",
            fontSize: 11,
            width: 92,
            overflow: "truncate" as const,
            formatter: (value: string) => labels[Number(value)] ?? "",
          },
          // Banding, not lines: a mark far from the axis needs its row carried
          // across to the name, and alternating washes do that without adding
          // rules that compete with the vertical time grid. Both steps are
          // barely-there on purpose — the events are the subject.
          splitArea: {
            show: true,
            areaStyle: { color: ["rgba(255,255,255,0.022)", "rgba(255,255,255,0.055)"] },
          },
          splitLine: { show: false },
        },
        series,
      },
      { replaceMerge: ["series"] },
    );
    chart.resize();

    chart.dispatchAction({
      type: "takeGlobalCursor",
      key: "dataZoomSelect",
      dataZoomSelectActive: true,
    });
  }, [result, rows]);

  const height = Math.max(200, rows.length * ROW_HEIGHT + 76);

  return (
    <div className="panel chart-panel">
      <div className="panel-title">
        <span>Timeline</span>
        <span className="title-controls">
          {/* Silent truncation would read as "these are all the actors". */}
          {omitted > 0 && (
            <span
              className="subtle"
              title="Only the busiest lanes are drawn — narrow the time range to see the rest"
            >
              +{omitted} more
            </span>
          )}
          <span
            className="subtle"
            title={
              "Casts, abilities, deaths, and resists per PC/NPC, plus buff bars paired from your " +
              "casts and their 'worn off' messages. Buffs without a logged wear-off (and buffs cast " +
              "on you by others) need the spell database and aren't drawn yet."
            }
          >
            what's this?
          </span>
        </span>
      </div>
      {result && rows.length === 0 && (
        <div className="empty">No spell or ability activity in this selection</div>
      )}
      <div className="chart-wrap">
        <div ref={divRef} className="chart" style={{ height }} />
        {isZoomed && (
          <span className="zoom-actions">
            <button
              className="zoom-reset"
              onClick={() => {
                const range = zoomRangeRef.current;
                if (range) onAdoptRange(range[0], range[1]);
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
    </div>
  );
}
