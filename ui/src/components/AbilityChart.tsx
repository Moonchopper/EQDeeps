import { useEffect, useRef, useState } from "react";
import * as echarts from "echarts";
import { api, type QueryResult } from "../api";
import { fmtNum, fmtRate } from "../format";

interface Props {
  sessionId: string;
  fightIds: number[];
  refreshKey: number;
  character: string;
}

const BAR_COLOR = "#3987e5";
const MAX_BARS = 14;

/**
 * Damage broken down by ability/skill for one actor (or everyone), as
 * horizontal bars. "DPS" here is contribution: the ability's damage over the
 * actor's active time, so bars sum to the actor's overall DPS — per-ability
 * active-time rates would make rare procs look absurd. One hue on purpose:
 * the category axis carries identity, so color has no job to do.
 */
export function AbilityChart({ sessionId, fightIds, refreshKey, character }: Props) {
  const divRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const [players, setPlayers] = useState<QueryResult | null>(null);
  const [player, setPlayer] = useState<string>(character);
  const [mode, setMode] = useState<"dps" | "total">("dps");
  const [abilities, setAbilities] = useState<QueryResult | null>(null);
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

  // Actor list for the picker (pets appear as their own actors).
  useEffect(() => {
    if (fightIds.length === 0) {
      setPlayers(null);
      return;
    }
    let cancelled = false;
    api
      .query(sessionId, {
        source: "damage",
        scope: { fightIds },
        groupBy: ["player"],
        metrics: ["total", "dps", "activeSeconds"],
        petRollup: false,
      })
      .then((r) => {
        if (cancelled) return;
        setPlayers(r);
        setPlayer((current) => {
          if (current && r.rows.some((row) => row.key === current)) return current;
          return r.rows.some((row) => row.key === character) ? character : "";
        });
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, selectionKey, refreshKey, character]);

  useEffect(() => {
    if (fightIds.length === 0) {
      setAbilities(null);
      chartRef.current?.clear();
      return;
    }
    let cancelled = false;
    api
      .query(sessionId, {
        source: "damage",
        scope: { fightIds },
        groupBy: ["spell"],
        metrics: ["total", "hits"],
        filters: player ? [{ dim: "player", values: [player] }] : [],
        petRollup: false,
      })
      .then((r) => !cancelled && setAbilities(r))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [sessionId, selectionKey, refreshKey, player]);

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

    const ranked = [...abilities.rows].sort(
      (a, b) => (b.metrics.total ?? 0) - (a.metrics.total ?? 0),
    );
    const top = ranked.slice(0, MAX_BARS);
    const rest = ranked.slice(MAX_BARS);
    const grand = ranked.reduce((sum, r) => sum + (r.metrics.total ?? 0), 0);

    const entries = top.map((r) => ({
      label: r.label,
      total: r.metrics.total ?? 0,
      hits: r.metrics.hits ?? 0,
    }));
    if (rest.length > 0) {
      entries.push({
        label: `Other (${rest.length})`,
        total: rest.reduce((sum, r) => sum + (r.metrics.total ?? 0), 0),
        hits: rest.reduce((sum, r) => sum + (r.metrics.hits ?? 0), 0),
      });
    }

    const value = (total: number) => (mode === "dps" ? (denom > 0 ? total / denom : 0) : total);

    chartRef.current.setOption(
      {
        backgroundColor: "transparent",
        animation: false,
        grid: { left: 8, right: 64, top: 6, bottom: 6, containLabel: true },
        tooltip: {
          backgroundColor: "#232322",
          borderColor: "rgba(255,255,255,0.10)",
          textStyle: { color: "#ffffff", fontSize: 12 },
          formatter: (params: unknown) => {
            const p = params as { dataIndex: number };
            const e = entries[p.dataIndex];
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
          show: false,
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
        series: [
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
              formatter: (p: { value: number | string }) => fmtNum(Number(p.value)),
            },
          },
        ],
      },
      { replaceMerge: ["series"] },
    );
  }, [abilities, players, player, mode]);

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
                {r.key}
              </option>
            ))}
          </select>
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
      {fightIds.length === 0 && <div className="empty">Select a fight</div>}
      <div ref={divRef} className="chart" />
    </div>
  );
}
