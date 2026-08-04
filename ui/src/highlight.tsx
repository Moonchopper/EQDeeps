import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import type * as echarts from "echarts";
import { ENTITY_POOL } from "./colors";

/**
 * Linked highlighting: point at one thing and every other reading of the same
 * entity lights up — the DPS line, the summary row, the meter bar, the stacked
 * segment inside an ability.
 *
 * It answers the question the color registry raised but could not close. Color
 * already follows the entity everywhere (see colors.ts), which is what lets
 * someone say "the orange line is also the second row" — but only after they
 * have found orange twice, on a screen where eight players are eight hues and
 * two of them are close. Hovering states the identity outright instead of
 * asking the eye to match it.
 *
 * The identity is the same one the palette is keyed by: a row key inside a
 * POOL. Pools matter here for the reason they matter there — an item and a
 * player can share a name, and a loot table lighting up because a mob is called
 * "Sonic Bat" would be a coincidence presented as a fact.
 *
 * Hover is a pointer position, not a selection. It is deliberately not
 * persisted, not synced to the URL, and not part of any panel definition:
 * nothing about the app's state should differ because the mouse came to rest
 * somewhere.
 */
export interface HoverTarget {
  key: string;
  pool: string;
}

const HoveredContext = createContext<HoverTarget | null>(null);
const SetHoveredContext = createContext<(target: HoverTarget | null) => void>(() => undefined);

/**
 * Two contexts rather than one object, because the two halves have opposite
 * update patterns: the setter never changes, so components that only emit
 * hover (chart wrappers) don't re-render when the pointer moves, while the
 * ones that respond to it do.
 */
export function HighlightProvider({ children }: { children: ReactNode }) {
  const [hovered, setState] = useState<HoverTarget | null>(null);

  // Re-stating the current hover is a no-op, not a re-render. Charts echo the
  // highlight they were just given (see useChartLink), so this is what keeps
  // "point at a row, light the line, which reports the row" from being a loop
  // rather than a round trip.
  const setHovered = useCallback((next: HoverTarget | null) => {
    setState((previous) =>
      previous === next ||
      (previous !== null && next !== null && previous.key === next.key && previous.pool === next.pool)
        ? previous
        : next,
    );
  }, []);

  return (
    <SetHoveredContext.Provider value={setHovered}>
      <HoveredContext.Provider value={hovered}>{children}</HoveredContext.Provider>
    </SetHoveredContext.Provider>
  );
}

export function useHovered(): HoverTarget | null {
  return useContext(HoveredContext);
}

export function useSetHovered(): (target: HoverTarget | null) => void {
  return useContext(SetHoveredContext);
}

/** True when `key` in `pool` is what the pointer is on. */
export function isHovered(hovered: HoverTarget | null, key: string, pool: string): boolean {
  return hovered !== null && hovered.key === key && hovered.pool === pool;
}

/**
 * Mouse handlers making a row a hover source, plus the class that makes it a
 * target. One hook for both directions because every row is both: hovering the
 * meter lights the table, and hovering the table lights the meter.
 */
export function useRowLink(pool: string = ENTITY_POOL) {
  const hovered = useHovered();
  const setHovered = useSetHovered();

  return useCallback(
    (key: string) => ({
      className: isHovered(hovered, key, pool) ? "linked" : undefined,
      onMouseEnter: () => setHovered({ key, pool }),
      onMouseLeave: () => setHovered(null),
    }),
    [hovered, pool, setHovered],
  );
}

/**
 * What a chart has drawn, in terms the rest of the app can match against.
 * Charts label series for people to read — "Raider21 +Pets", an ability name —
 * so the key that identifies the entity has to be carried alongside.
 */
export interface ChartKeys {
  /** Series name → entity key, for line and stacked-bar charts. */
  series: Map<string, string>;
  /** Entity key per data index, for single-series category charts. */
  items: string[];
}

/**
 * Wires a chart into the highlight: hovering it publishes what is under the
 * cursor, and hovering anything else emphasises the matching series or bar.
 *
 * Returns a ref the chart fills in as it draws. It is a ref rather than a
 * dependency so that a hover costs a `dispatchAction` and nothing more —
 * rebuilding the option on every pointer move would re-run the smoothing and
 * repaint the fight bands for a highlight ECharts can apply on its own.
 *
 * CALL THIS AFTER the effect that creates the chart. Effects run in the order
 * they are declared, and this one reads `chartRef.current` on mount.
 */
export function useChartLink(
  chartRef: React.MutableRefObject<echarts.ECharts | null>,
  pool: string = ENTITY_POOL,
): React.MutableRefObject<ChartKeys> {
  const keys = useRef<ChartKeys>({ series: new Map(), items: [] });
  const hovered = useHovered();
  const setHovered = useSetHovered();
  // True only while we are the ones dispatching. ECharts emits action events
  // synchronously from dispatchAction, so this reliably tells the highlight we
  // asked for apart from the one the user caused.
  const echoing = useRef(false);

  // Chart → app.
  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) {
      return;
    }

    const enter = (params: {
      componentType?: string;
      seriesName?: string;
      name?: string;
      dataIndex?: number;
    }) => {
      // A legend entry names its series; a bar in a category chart names only
      // itself, so the key comes from where it sits.
      const byIndex =
        params.componentType === "series" &&
        keys.current.items.length > 0 &&
        typeof params.dataIndex === "number"
          ? keys.current.items[params.dataIndex]
          : undefined;
      const name = params.componentType === "legend" ? params.name : params.seriesName;
      const key = byIndex ?? (name ? keys.current.series.get(name) : undefined);
      // Folded "Other (n)" series and the fight-band overlays name no one:
      // clearing is more honest than leaving the last entity lit.
      setHovered(key ? { key, pool } : null);
    };
    const leave = () => setHovered(null);

    // The legend is the easiest thing on a chart to point at, and the only one
    // ECharts keeps to itself: legend entries emit no mouse events. What they
    // do emit is the highlight their own hoverLink dispatches, which names the
    // series — so that action is the legend's hover event in all but name.
    // Action events are typed loosely — they carry whatever the action's
    // payload was, so the shape is asserted here rather than declared.
    const highlighted = (...args: unknown[]) => {
      if (echoing.current) {
        return;
      }
      const { seriesName } = (args[0] ?? {}) as { seriesName?: string };
      const key = seriesName ? keys.current.series.get(seriesName) : undefined;
      if (key) {
        setHovered({ key, pool });
      }
    };
    const downplayed = () => {
      if (!echoing.current) {
        setHovered(null);
      }
    };

    const zr = chart.getZr();
    chart.on("mouseover", enter);
    chart.on("mouseout", leave);
    chart.on("highlight", highlighted);
    chart.on("downplay", downplayed);
    // The pointer leaving the canvas entirely fires no mouseout on any element.
    zr.on("globalout", leave);

    return () => {
      if (chart.isDisposed()) {
        return; // the chart's own effect disposed it first
      }
      chart.off("mouseover", enter);
      chart.off("mouseout", leave);
      chart.off("highlight", highlighted);
      chart.off("downplay", downplayed);
      zr.off("globalout", leave);
    };
  }, [chartRef, pool, setHovered]);

  // App → chart.
  useEffect(() => {
    const chart = chartRef.current;
    if (!chart || chart.isDisposed()) {
      return;
    }

    echoing.current = true;
    try {
      chart.dispatchAction({ type: "downplay" });
      if (!hovered || hovered.pool !== pool) {
        return;
      }

      for (const [name, key] of keys.current.series) {
        if (key === hovered.key) {
          chart.dispatchAction({ type: "highlight", seriesName: name });
          return;
        }
      }
      const index = keys.current.items.indexOf(hovered.key);
      if (index >= 0) {
        chart.dispatchAction({ type: "highlight", seriesIndex: 0, dataIndex: index });
      }
    } finally {
      echoing.current = false;
    }
  }, [chartRef, hovered, pool]);

  return keys;
}

/**
 * Emphasis for a series that shares its chart with others: the hovered one
 * keeps full strength and the rest fade, which is the only treatment that
 * works when eight lines cross. `blurScope: "coordinateSystem"` keeps a fade
 * inside the chart it started in.
 */
/**
 * Emphasis for a series that shares its chart with others: the hovered one
 * keeps full strength and the rest fade, which is the only treatment that
 * works when eight lines cross.
 *
 * The fade is 0.25 rather than the 0.1 ECharts would apply by default, because
 * the other lines are the comparison that makes the highlighted one worth
 * reading — vanish them and the chart answers "how much" without "compared to
 * what". Focus dims the fight bands and gear marks along with them, since they
 * are series too; they stay legible at the reduced opacity, and an override on
 * those series does not lift it (a markArea takes the blur from its parent
 * whatever its own blur state says).
 */
export const SERIES_EMPHASIS = {
  emphasis: { focus: "series", lineStyle: { width: 3 } },
  blur: { lineStyle: { opacity: 0.25 } },
  blurScope: "coordinateSystem",
} as const;

/** The same, for bars: they don't overlap, so opacity carries it alone. */
export const BAR_EMPHASIS = {
  emphasis: { focus: "series" },
  blur: { itemStyle: { opacity: 0.15 } },
  blurScope: "coordinateSystem",
} as const;

/**
 * Single-series category charts, where the bars are the entities rather than
 * the series: focus is per item, so hovering one mob fades its neighbours.
 */
export const ITEM_EMPHASIS = {
  emphasis: { focus: "self" },
  blur: { itemStyle: { opacity: 0.15 } },
  blurScope: "coordinateSystem",
} as const;
