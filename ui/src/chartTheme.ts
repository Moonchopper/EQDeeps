import * as echarts from "echarts";

import { OTHER_COLOR, SERIES_COLORS } from "./format";

/**
 * The one place ECharts is told what the app looks like.
 *
 * Before this file existed, every chart set its own axis colours, tooltip
 * chrome and legend text as hex literals — 66 of them across five files — and
 * none of them read the CSS tokens, so the charts and the DOM drifted apart
 * every time the palette moved. Worse, no chart set a root `textStyle` at all,
 * which meant every axis label, legend entry, tooltip body and bar label
 * rendered in ECharts' stock `sans-serif` while the surrounding DOM rendered
 * the app font. Two typefaces on every panel, in the shipping build.
 *
 * The theme is built by READING the custom properties off `:root` rather than
 * restating them. That is the whole point: `styles.css` stays the single source
 * of truth, and a future theme swap moves the charts with it for free. The
 * fallbacks exist only so a chart still renders if this ever runs before the
 * stylesheet has landed; they are not a second palette to maintain.
 */
export const CHART_THEME = "eqdeeps";

/** Read a `:root` custom property, falling back if the stylesheet isn't up yet. */
function token(name: string, fallback: string): string {
  if (typeof window === "undefined") {
    return fallback;
  }
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

/**
 * Grid rectangles, exported because each one used to be restated as arithmetic
 * in two or three other places — the fight-band label height, the context
 * strip's label-fit test and the wheel-zoom padding all recompute the same
 * numbers, and nothing asserted they agreed. Change the rect here and the
 * dependents follow instead of silently drifting.
 */
export const GRID = {
  /** DpsChart: room on the left for y labels, on top for the scroll legend. */
  dps: { left: 52, right: 12, top: 30, bottom: 40 },
  /** PanelBody's LinePanel: tighter, no legend row of its own. */
  line: { left: 48, right: 10, top: 26, bottom: 24 },
  /** TimelineChart: a wide left gutter for the actor names. */
  timeline: { left: 100, right: 12, top: 30, bottom: 26 },
  /** AbilityChart: horizontal bars, labels on the right. */
  ability: { left: 8, right: 56, top: 6, bottom: 6, containLabel: true },
} as const;

/**
 * Concrete token values, for the few places a component has to hand ECharts a
 * colour itself — a per-series label, a mark line, an overlay tint.
 *
 * This exists because ECharts renders to a canvas and therefore cannot resolve
 * `var(--ink-2)`: passing a CSS variable there fails silently and the text lands
 * black. Anything that needs a token inside a chart option reads it from here.
 */
export function chartInk() {
  return {
    ink: token("--ink", "#f1ece3"),
    ink2: token("--ink-2", "#c5bdae"),
    muted: token("--muted", "#968e7e"),
    grid: token("--grid", "#3a342e"),
    baseline: token("--baseline", "#605851"),
    surface: token("--surface", "#26211c"),
    surface2: token("--surface-2", "#2a2520"),
    accent: token("--accent", "#e8963c"),
  };
}

function buildTheme() {
  const ink = token("--ink", "#f1ece3");
  const ink2 = token("--ink-2", "#c5bdae");
  const muted = token("--muted", "#968e7e");
  const grid = token("--grid", "#3a342e");
  const baseline = token("--baseline", "#605851");
  const border = token("--border", "#4d453d");
  const surface2 = token("--surface-2", "#2a2520");
  const fontFamily = token("--font-ui", 'system-ui, -apple-system, "Segoe UI", sans-serif');
  const shadow = token("--shadow-overlay", "0 14px 34px -10px rgba(4, 3, 2, 0.72)");

  /* Axes share everything except which of them draws a line and which draws a
     grid: the value axis carries the split lines and no spine, the time and
     category axes carry the spine and no split lines. Two systems of rules
     across one plot is one too many. */
  const axisLabel = { color: muted, fontSize: 11, hideOverlap: true };
  const spine = { show: true, lineStyle: { color: baseline } };
  const noTicks = { show: false };

  return {
    color: [...SERIES_COLORS],
    backgroundColor: "transparent",
    /* The root textStyle every chart was missing. */
    textStyle: { fontFamily, fontSize: 11, color: ink2 },

    line: {
      /* Splines, not polylines — but monotone ones. A plain smoothed line
         overshoots between samples: it will draw a DPS peak that never happened
         and dip below zero coming off a burst. `smoothMonotone: "x"` pins the
         curve inside the data it was given, which is the only version of this
         a parser is allowed to ship. */
      smooth: 0.35,
      smoothMonotone: "x",
      showSymbol: false,
      lineStyle: { width: 1.5, cap: "round", join: "round" },
      symbol: "circle",
      symbolSize: 7,
      /* No areaStyle, anywhere. Under one series a gradient fill is the best
         part of the look; under eight it destroys exactly the per-second
         comparison these charts exist for. Panels that want a fill opt in
         explicitly rather than inheriting one. */
    },

    bar: {
      /* Rounded on the leading end only, so the bar stays anchored to its
         baseline, and capped: past about half the bar's own width a dome reads
         as larger than the value it represents. */
      itemStyle: { borderRadius: [0, 3, 3, 0] },
    },

    valueAxis: {
      axisLine: { show: false },
      axisTick: noTicks,
      axisLabel,
      splitLine: { show: true, lineStyle: { color: grid } },
    },
    categoryAxis: {
      axisLine: spine,
      axisTick: noTicks,
      axisLabel,
      splitLine: { show: false },
      splitArea: { show: false },
    },
    timeAxis: {
      axisLine: spine,
      axisTick: noTicks,
      axisLabel,
      splitLine: { show: false },
    },
    logAxis: {
      axisLine: { show: false },
      axisTick: noTicks,
      axisLabel,
      splitLine: { show: true, lineStyle: { color: grid } },
    },

    legend: {
      /* Label ink is always a text token, never the series colour: the swatch
         beside it already carries identity, and a 11px legend entry painted in
         a 3:1 mark colour is unreadable. */
      textStyle: { color: ink2, fontSize: 11 },
      inactiveColor: muted,
      icon: "roundRect",
      itemWidth: 9,
      itemHeight: 9,
      itemGap: 12,
      /* The scroll legend's page arrows defaulted to ECharts' #2f4554, which is
         invisible on any of this app's surfaces. */
      pageIconColor: ink2,
      pageIconInactiveColor: muted,
      pageTextStyle: { color: muted, fontSize: 11 },
    },

    tooltip: {
      backgroundColor: surface2,
      borderColor: border,
      borderWidth: 1,
      borderRadius: 10,
      padding: [7, 10],
      textStyle: { color: ink, fontSize: 12 },
      /* One crosshair for the whole app. DpsChart styled its own and the
         standard-view line panels left ECharts' default, so two charts on one
         screen disagreed about what a crosshair looks like. */
      axisPointer: { type: "line", lineStyle: { color: baseline, width: 1 } },
      extraCssText: `box-shadow: ${shadow};`,
      transitionDuration: 0,
      confine: true,
    },
  };
}

let registered = false;

/**
 * Register the theme once and return its name, for `echarts.init(el, name)`.
 *
 * Called from inside each chart's effect rather than at module scope, so the
 * stylesheet is guaranteed to have landed before the tokens are read.
 */
export function chartTheme(): string {
  if (!registered) {
    echarts.registerTheme(CHART_THEME, buildTheme());
    registered = true;
  }
  return CHART_THEME;
}

/** The dashed neutral series that folds away everything past the chart cap. */
export const OTHER_SERIES_COLOR = OTHER_COLOR;
