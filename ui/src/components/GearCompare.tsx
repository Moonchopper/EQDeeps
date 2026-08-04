import { useEffect, useMemo, useRef } from "react";
import * as echarts from "echarts";
import type { FightInfo } from "../api";
import { fmtNum, SERIES_COLORS } from "../format";

/**
 * Comparing gear sets means comparing windows of wildly different size — 36
 * minutes against two. There is no single honest way to do that, so this
 * offers the three that are each honest about something different, and says
 * which is which rather than picking one and hoping.
 */
export type CompareMode = "distribution" | "sequence" | "elapsed";

export const COMPARE_MODES: { value: CompareMode; label: string; blurb: string }[] = [
  {
    value: "distribution",
    label: "spread",
    blurb:
      "Every fight in the set as one point of DPS, drawn as its spread. Time is off the axis entirely, which is what lets unequal windows be compared at all — but a set with 15 fights is a thinner claim than one with 97, so the counts are on the labels.",
  },
  {
    value: "sequence",
    label: "by fight",
    blurb:
      "DPS per fight, numbered from the start of each set. Every set begins at fight 1 so they overlay; a set worn longer simply runs further right.",
  },
  {
    value: "elapsed",
    label: "by clock",
    blurb:
      "DPS over time, each set restarted at 0:00 and clipped to the shortest one so the visible window is identical. Familiar to read, but the clipped part of a long set is discarded and what remains is its opening, not a fair sample of it.",
  },
];

export interface CompareSet {
  key: string;
  label: string;
  begin: string;
  end: string;
}

interface Props {
  sets: CompareSet[];
  fights: FightInfo[];
  mode: CompareMode;
  /** Restrict every set to mobs that appear in all of them. */
  commonTargets: boolean;
}

/** Server rule: a fight's duration is inclusive of its final second. */
function seconds(fight: FightInfo): number {
  return Math.max(
    1,
    (new Date(fight.lastDamageTime).getTime() - new Date(fight.beginTime).getTime()) / 1000 + 1,
  );
}

function dps(fight: FightInfo): number {
  return fight.characterDamage / seconds(fight);
}

function quantile(sorted: number[], q: number): number {
  if (sorted.length === 0) return 0;
  const pos = (sorted.length - 1) * q;
  const base = Math.floor(pos);
  const rest = pos - base;
  return sorted[base + 1] !== undefined
    ? sorted[base] + rest * (sorted[base + 1] - sorted[base])
    : sorted[base];
}

/**
 * DPS per fight for each set, with fights that landed no damage dropped — a
 * pull someone else killed is not a zero for this character, it is an absence
 * of evidence, and averaging it in would punish the gear for it.
 */
function fightsOf(set: CompareSet, fights: FightInfo[]): FightInfo[] {
  return fights.filter(
    (f) =>
      f.lastDamageTime >= set.begin && f.beginTime < set.end && f.characterDamage > 0,
  );
}

export function GearCompare({ sets, fights, mode, commonTargets }: Props) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  /** Per-set fights, after the optional like-for-like filter. */
  const series = useMemo(() => {
    const raw = sets.map((set) => ({ set, fights: fightsOf(set, fights) }));
    if (!commonTargets || raw.length < 2) {
      return raw;
    }

    // Mobs every set actually fought. Content is a bigger lever on DPS than
    // gear is, so this is the difference between a comparison and a coincidence.
    let shared = new Set<string>(raw[0].fights.map((f) => f.name));
    for (const entry of raw.slice(1)) {
      const names = new Set<string>(entry.fights.map((f) => f.name));
      shared = new Set<string>([...shared].filter((n) => names.has(n)));
    }

    return raw.map((entry) => ({
      set: entry.set,
      fights: entry.fights.filter((f) => shared.has(f.name)),
    }));
  }, [sets, fights, commonTargets]);

  const empty = series.every((s) => s.fights.length === 0);

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

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;

    if (empty) {
      chart.clear();
      return;
    }

    // The container is often laid out after the chart was created — a tab
    // switch, or data arriving into a panel that had none. ECharts keeps
    // whatever size it saw at init, so measure again before drawing.
    chart.resize();

    const axis = {
      axisLine: { lineStyle: { color: "#383835" } },
      axisLabel: { color: "#898781", fontSize: 11 },
    };
    const common = {
      backgroundColor: "transparent",
      animation: false,
      grid: { left: 56, right: 14, top: 28, bottom: 34 },
      tooltip: {
        backgroundColor: "#232322",
        borderColor: "rgba(255,255,255,0.10)",
        textStyle: { color: "#ffffff", fontSize: 12 },
      },
      yAxis: {
        type: "value",
        name: "DPS",
        nameTextStyle: { color: "#898781" },
        min: 0,
        ...axis,
        splitLine: { lineStyle: { color: "#2c2c2a" } },
        axisLabel: { color: "#898781", fontSize: 11, formatter: (v: number) => fmtNum(v) },
      },
    };

    if (mode === "distribution") {
      const boxes = series.map((entry) => {
        const values = entry.fights.map(dps).sort((a, b) => a - b);
        return [
          values[0] ?? 0,
          quantile(values, 0.25),
          quantile(values, 0.5),
          quantile(values, 0.75),
          values[values.length - 1] ?? 0,
        ];
      });

      chart.setOption(
        {
          ...common,
          xAxis: {
            type: "category",
            // The sample size rides on the label: a 15-fight box and a
            // 97-fight box look equally solid otherwise.
            data: series.map((e) => `${e.set.label}  n=${e.fights.length}`),
            ...axis,
          },
          series: [
            {
              type: "boxplot",
              data: boxes,
              itemStyle: { color: "rgba(245,197,66,0.18)", borderColor: "#c9a227" },
              tooltip: {
                formatter: (p: { name: string; data: number[] }) =>
                  `${p.name}<br/>max ${fmtNum(p.data[5] ?? p.data[4])}<br/>` +
                  `upper ${fmtNum(p.data[4])}<br/>median ${fmtNum(p.data[3])}<br/>` +
                  `lower ${fmtNum(p.data[2])}<br/>min ${fmtNum(p.data[1])}`,
              },
            },
          ],
        },
        { replaceMerge: ["series", "xAxis"] },
      );
      return;
    }

    // Both line modes: one series per set, differing only in what x means.
    const clipTo =
      mode === "elapsed"
        ? Math.min(
            ...series
              .filter((e) => e.fights.length > 0)
              .map((e) => {
                const start = new Date(e.set.begin).getTime();
                const last = e.fights[e.fights.length - 1];
                return (new Date(last.lastDamageTime).getTime() - start) / 1000;
              }),
          )
        : 0;

    chart.setOption(
      {
        ...common,
        legend: {
          top: 0,
          data: series.map((e) => e.set.label),
          textStyle: { color: "#c3c2b7", fontSize: 11 },
          inactiveColor: "#52514e",
        },
        tooltip: { ...common.tooltip, trigger: "axis" },
        xAxis: {
          type: "value",
          name: mode === "sequence" ? "fight #" : "elapsed (s)",
          nameLocation: "middle",
          nameGap: 22,
          nameTextStyle: { color: "#898781" },
          min: mode === "sequence" ? 1 : 0,
          max: mode === "elapsed" ? Math.max(1, clipTo) : undefined,
          ...axis,
          splitLine: { show: false },
        },
        series: series.map((entry, i) => ({
          name: entry.set.label,
          type: "line",
          showSymbol: entry.fights.length <= 40,
          symbolSize: 4,
          lineStyle: { width: 1.5 },
          color: SERIES_COLORS[i % SERIES_COLORS.length],
          data: entry.fights
            .map((f, n) => {
              const x =
                mode === "sequence"
                  ? n + 1
                  : (new Date(f.beginTime).getTime() - new Date(entry.set.begin).getTime()) / 1000;
              return [x, Math.round(dps(f))];
            })
            .filter(([x]) => mode === "sequence" || x <= clipTo),
        })),
      },
      { replaceMerge: ["series", "xAxis"] },
    );
  }, [series, mode, empty]);

  // The canvas is mounted unconditionally and the empty states sit over it.
  // Swapping it out for a message instead would unmount the ref the one-time
  // init effect reads, and nothing would ever re-create the chart when data
  // finally arrived — which is exactly how this shipped blank the first time.
  const message =
    sets.length === 0
      ? "Pick two or more sets to compare."
      : empty
        ? commonTargets
          ? "No mob was fought in every selected set — turn off like-for-like, or pick sets with overlapping content."
          : "No fights with your own damage in these sets yet."
        : null;

  return (
    <div className="gear-compare-stage">
      <div ref={divRef} className="chart gear-compare-chart" />
      {message !== null && <div className="empty gear-compare-empty">{message}</div>}
    </div>
  );
}
