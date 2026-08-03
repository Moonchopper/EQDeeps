import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as echarts from "echarts";
import { api, type FightInfo, type TimelineItem, type TimelineResult } from "../api";
import { attachWheelZoom, offsetTooltip } from "../chartInteractions";
import { fmtNum } from "../format";
import { fightsInFrame, type TimeFrame } from "../timeFrame";

interface Props {
  sessionId: string;
  frame: TimeFrame;
  refreshKey: number;
  character: string;
  fights: FightInfo[];
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

const SPAN_COLOR = "#3987e5"; // buffs

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
  { key: "cast", name: "casts", color: "#d95926", symbol: "triangle" },
  { key: "cast:heal", name: "heal casts", color: "#9163d9", symbol: "triangle" },
  { key: "song", name: "songs", color: "#d95926", symbol: "diamond" },
  { key: "song:heal", name: "heal songs", color: "#9163d9", symbol: "diamond" },
  { key: "ability", name: "abilities", color: "#199e70", symbol: "rect" },
  { key: "interrupt", name: "interrupts", color: "#898781", symbol: "triangle", rotate: 180 },
  { key: "fizzle", name: "fizzles", color: "#898781", symbol: "emptyCircle" },
  { key: "fade", name: "fades", color: "#898781", symbol: "emptyDiamond" },
  { key: "resist", name: "resists", color: "#898781", symbol: "pin" },
  { key: "death", name: "deaths", color: "#d03b3b", symbol: DEATH_X },
];

const ROW_HEIGHT = 20;

interface Row {
  /** Actor name shown on the axis — only on the actor's first row. */
  label: string;
  instants: TimelineItem[];
  spans: TimelineItem[];
}

/**
 * One lane block per actor: a row of instant marks, then as many span rows as
 * concurrent buffs need (greedy interval packing). Log owner first, then other
 * players, then NPCs (anything sharing a fight name).
 */
function buildRows(items: TimelineItem[], character: string, npcNames: Set<string>): Row[] {
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

  const rows: Row[] = [];
  for (const actor of actors) {
    const all = byActor.get(actor)!;
    const instants = all.filter((i) => !i.end);
    const spans = all.filter((i) => i.end).sort((a, b) => a.start.localeCompare(b.start));

    const actorRows: Row[] = [];
    if (instants.length > 0) {
      actorRows.push({ label: "", instants, spans: [] });
    }
    const rowEnds: number[] = [];
    for (const span of spans) {
      const start = new Date(span.start).getTime();
      const slot = rowEnds.findIndex((end) => start >= end + 1000);
      if (slot >= 0) {
        rowEnds[slot] = new Date(span.end!).getTime();
        actorRows[instants.length > 0 ? slot + 1 : slot].spans.push(span);
      } else {
        rowEnds.push(new Date(span.end!).getTime());
        actorRows.push({ label: "", instants: [], spans: [span] });
      }
    }
    if (actorRows.length > 0) {
      actorRows[0].label = actor;
      rows.push(...actorRows);
    }
  }

  return rows;
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
export function TimelineChart({ sessionId, frame, refreshKey, character, fights }: Props) {
  // The timeline draws per-combatant lanes, so it is inherently fight-shaped:
  // it takes the fights the frame covers rather than the frame itself.
  const fightIds = fightsInFrame(frame, fights);
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const [result, setResult] = useState<TimelineResult | null>(null);
  const [isZoomed, setIsZoomed] = useState(false);
  const suppressZoomEventRef = useRef(false);
  const extentRef = useRef<[number, number] | null>(null);
  const selectionKey = fightIds.join(",");

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
      if (suppressZoomEventRef.current) return;
      const p = params as { start?: number; end?: number; batch?: { start?: number; end?: number }[] };
      const window = p.batch?.[0] ?? p;
      setIsZoomed(!(window.start === 0 && window.end === 100));
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
  }, [selectionKey, resetZoom]);

  useEffect(() => {
    if (fightIds.length === 0) {
      setResult(null);
      chartRef.current?.clear();
      return;
    }
    let cancelled = false;
    api
      .timeline(sessionId, { fightIds })
      .then((r) => !cancelled && setResult(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, selectionKey, refreshKey]);

  const npcNames = useMemo(() => new Set(fights.map((f) => f.name)), [fights]);
  const rows = useMemo(
    () => (result ? buildRows(result.items, character, npcNames) : []),
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
    const instantData = new Map<string, { value: [number, number]; item: TimelineItem }[]>();
    rows.forEach((row, rowIndex) => {
      for (const span of row.spans) {
        spanData.push({
          value: [new Date(span.start).getTime(), new Date(span.end!).getTime(), rowIndex],
          item: span,
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
        grid: { left: 100, right: 12, top: 30, bottom: 26 },
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
          textStyle: { color: "#c3c2b7", fontSize: 11 },
          inactiveColor: "#52514e",
        },
        tooltip: {
          trigger: "item",
          position: offsetTooltip,
          backgroundColor: "#232322",
          borderColor: "rgba(255,255,255,0.10)",
          textStyle: { color: "#ffffff", fontSize: 12 },
        },
        xAxis: {
          type: "time",
          min: rangeBegin,
          max: rangeEnd,
          axisLine: { lineStyle: { color: "#383835" } },
          axisLabel: { color: "#898781", fontSize: 11 },
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
      {fightIds.length === 0 && <div className="empty">Select a fight</div>}
      {fightIds.length > 0 && result && rows.length === 0 && (
        <div className="empty">No spell or ability activity in this selection</div>
      )}
      <div className="chart-wrap">
        <div ref={divRef} className="chart" style={{ height }} />
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
