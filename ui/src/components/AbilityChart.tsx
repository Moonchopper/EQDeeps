import { useEffect, useRef, useState } from "react";
import * as echarts from "echarts";
import { api, type QueryResult, type QueryRow } from "../api";
import { fmtNum, fmtRate, OTHER_COLOR } from "../format";
import type { EntityColors } from "../colors";
import { offsetTooltip } from "../chartInteractions";
import { frameScope, type TimeFrame } from "../timeFrame";

interface Props {
  sessionId: string;
  frame: TimeFrame;
  refreshKey: number;
  petRollup: boolean;
  colors: EntityColors;
}

const BAR_COLOR = "#3987e5";
const SURFACE = "#1a1a19";
const MAX_BARS = 14;
const MAX_STACK_ATTACKERS = 8;

/**
 * Damage broken down by ability/skill for one actor (or everyone), as
 * horizontal bars. "DPS" here is contribution: the ability's damage over the
 * actor's active time, so bars sum to the actor's overall DPS — per-ability
 * active-time rates would make rare procs look absurd.
 *
 * For "everyone", the split toggle subdivides each ability into per-attacker
 * stacked segments: attackers take the categorical slots in damage order (the
 * same ranking the meter uses), everyone past eight folds into a gray Other,
 * and a legend carries identity. Flat mode stays single-hue — there the
 * category axis already names each bar.
 */
export function AbilityChart({ sessionId, frame, refreshKey, petRollup, colors }: Props) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const [players, setPlayers] = useState<QueryResult | null>(null);
  // "" is everyone, which is where this opens: the raid-wide ability mix is
  // the more useful first read, and picking one actor is one click away.
  // A non-empty value is an explicit choice and survives live refreshes.
  const [player, setPlayer] = useState<string>("");
  const [mode, setMode] = useState<"dps" | "total">("dps");
  const [split, setSplit] = useState(true);
  const [abilities, setAbilities] = useState<QueryResult | null>(null);
  const selectionKey = JSON.stringify(frame);
  const splitActive = player === "" && split;

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

  // Actor list for the picker (pets appear as their own actors).
  useEffect(() => {
    let cancelled = false;
    api
      .query(sessionId, {
        source: "damage",
        scope: frameScope(frame),
        groupBy: ["player"],
        metrics: ["total", "dps", "activeSeconds"],
        petRollup,
      })
      .then((r) => {
        if (cancelled) return;
        setPlayers(r);
        // Keep a chosen actor only while they are still in the frame;
        // otherwise fall back to everyone rather than an empty chart.
        setPlayer((current) =>
          current === "" || r.rows.some((row) => row.key === current) ? current : "",
        );
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, selectionKey, refreshKey, petRollup]);

  useEffect(() => {
    let cancelled = false;
    api
      .query(sessionId, {
        source: "damage",
        scope: frameScope(frame),
        groupBy: splitActive ? ["spell", "player"] : ["spell"],
        metrics: ["total", "hits"],
        filters: player ? [{ dim: "player", values: [player] }] : [],
        petRollup,
      })
      .then((r) => !cancelled && setAbilities(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, selectionKey, refreshKey, player, splitActive, petRollup]);

  useEffect(() => {
    if (!chartRef.current) return;
    if (!abilities || abilities.rows.length === 0) {
      chartRef.current.clear();
      return;
    }

    // Denominator for contribution DPS: the actor's active seconds, or the
    // selection's fought time for "everyone".
    const playerRow = players?.rows.find((r) => r.key === player);
    const denom = player
      ? (playerRow?.metrics.activeSeconds ?? 0)
      : (players?.raidSeconds ?? abilities.raidSeconds);
    const value = (total: number) => (mode === "dps" ? (denom > 0 ? total / denom : 0) : total);

    const ranked = [...abilities.rows].sort(
      (a, b) => (b.metrics.total ?? 0) - (a.metrics.total ?? 0),
    );
    const top = ranked.slice(0, MAX_BARS);
    const rest = ranked.slice(MAX_BARS);
    const grand = ranked.reduce((sum, r) => sum + (r.metrics.total ?? 0), 0);

    interface Entry {
      label: string;
      total: number;
      hits: number;
      byAttacker: Map<string, number>;
    }

    const toEntry = (rows: QueryRow[], label: string): Entry => {
      const byAttacker = new Map<string, number>();
      let total = 0;
      let hits = 0;
      for (const row of rows) {
        total += row.metrics.total ?? 0;
        hits += row.metrics.hits ?? 0;
        for (const child of row.children ?? []) {
          byAttacker.set(child.key, (byAttacker.get(child.key) ?? 0) + (child.metrics.total ?? 0));
        }
      }
      return { label, total, hits, byAttacker };
    };

    const entries = top.map((r) => toEntry([r], r.label));
    if (rest.length > 0) {
      entries.push(toEntry(rest, `Other (${rest.length})`));
    }

    let series: echarts.SeriesOption[];
    if (splitActive) {
      // Attacker slots in overall-damage order — the meter's ranking.
      const attackerOrder = (players?.rows ?? []).map((r) => r.key);
      const stackKeys = attackerOrder.slice(0, MAX_STACK_ATTACKERS);
      const folded = attackerOrder.slice(MAX_STACK_ATTACKERS);
      const segment = (key: string) =>
        entries.map((e) => value(e.byAttacker.get(key) ?? 0));
      const foldedSegment = () =>
        entries.map((e) =>
          value(
            [...e.byAttacker.entries()]
              .filter(([k]) => !stackKeys.includes(k))
              .reduce((sum, [, v]) => sum + v, 0),
          ),
        );

      series = stackKeys.map((key) => ({
        name: key,
        type: "bar",
        stack: "dmg",
        barWidth: 13,
        data: segment(key),
        itemStyle: {
          color: colors.claim(key),
          borderColor: SURFACE,
          borderWidth: 1,
        },
      }));
      if (folded.length > 0) {
        series.push({
          name: `Other (${folded.length})`,
          type: "bar",
          stack: "dmg",
          barWidth: 13,
          data: foldedSegment(),
          itemStyle: { color: OTHER_COLOR, borderColor: SURFACE, borderWidth: 1 },
        });
      }
    } else {
      series = [
        {
          type: "bar",
          data: entries.map((e) => value(e.total)),
          barWidth: 13,
          itemStyle: { color: BAR_COLOR, borderRadius: [0, 4, 4, 0] },
          label: {
            show: true,
            position: "right",
            color: "#898781",
            fontSize: 11,
            formatter: (p: { value: unknown }) => fmtNum(Number(p.value)),
          },
        },
      ];
    }

    chartRef.current.setOption(
      {
        backgroundColor: "transparent",
        animation: false,
        grid: {
          left: 8,
          right: splitActive ? 16 : 64,
          top: splitActive ? 28 : 6,
          bottom: 6,
          containLabel: true,
        },
        legend: splitActive
          ? {
              type: "scroll",
              top: 0,
              textStyle: { color: "#c3c2b7", fontSize: 11 },
              inactiveColor: "#52514e",
            }
          : { show: false },
        tooltip: {
          position: offsetTooltip,
          backgroundColor: "#232322",
          borderColor: "rgba(255,255,255,0.10)",
          textStyle: { color: "#ffffff", fontSize: 12 },
          formatter: (params: unknown) => {
            const p = params as { dataIndex: number; seriesName?: string; value?: number };
            const e = entries[p.dataIndex];
            if (splitActive) {
              const segValue = typeof p.value === "number" ? p.value : 0;
              const abilityValue = value(e.total);
              const share = abilityValue > 0 ? (segValue / abilityValue) * 100 : 0;
              return (
                `<b>${p.seriesName}</b> — ${e.label}<br/>` +
                `${fmtNum(segValue)} ${mode === "dps" ? "dps" : "damage"} · ` +
                `${fmtRate(share)} of this ability`
              );
            }
            const share = grand > 0 ? (e.total / grand) * 100 : 0;
            return (
              `<b>${e.label}</b><br/>` +
              `${fmtNum(value(e.total))} ${mode === "dps" ? "dps" : "damage"} · ` +
              `${fmtRate(share)} of total · ${e.hits} hits`
            );
          },
        },
        xAxis: {
          type: "value",
          show: splitActive,
          axisLabel: { color: "#898781", fontSize: 11, formatter: (v: number) => fmtNum(v) },
          splitLine: { lineStyle: { color: "#2c2c2a" } },
        },
        yAxis: {
          type: "category",
          inverse: true,
          data: entries.map((e) => e.label),
          axisLine: { show: false },
          axisTick: { show: false },
          axisLabel: {
            color: "#c3c2b7",
            fontSize: 11,
            width: 150,
            overflow: "truncate",
          },
        },
        series,
      },
      { replaceMerge: ["series"] },
    );
  }, [abilities, players, player, mode, splitActive, colors]);

  return (
    <div className="panel chart-panel">
      <div className="panel-title">
        <span>Damage by ability</span>
        <span className="title-controls">
          <select
            className="panel-select"
            value={player}
            onChange={(e) => setPlayer(e.target.value)}
            title="Whose abilities to break down"
          >
            <option value="">everyone</option>
            {players?.rows.map((r) => (
              <option key={r.key} value={r.key}>
                {r.label}
              </option>
            ))}
          </select>
          {player === "" && (
            <label className="toggle" title="Subdivide each ability by attacker">
              <input type="checkbox" checked={split} onChange={(e) => setSplit(e.target.checked)} />
              split
            </label>
          )}
          <span className="tabs">
            <button
              className={"tab small" + (mode === "dps" ? " on" : "")}
              onClick={() => setMode("dps")}
              title="Ability damage over active time — bars sum to overall DPS"
            >
              dps
            </button>
            <button
              className={"tab small" + (mode === "total" ? " on" : "")}
              onClick={() => setMode("total")}
            >
              total
            </button>
          </span>
        </span>
      </div>
      <div ref={divRef} className="chart" />
    </div>
  );
}
