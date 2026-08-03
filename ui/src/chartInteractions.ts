import type * as echarts from "echarts";

/**
 * Tooltip placement that never sits on the hovered values: diagonally offset
 * from the cursor (above-right), flipping left/below near the viewport edges,
 * so the crosshair point and the lines around it stay visible.
 */
export function offsetTooltip(
  point: [number, number],
  _params: unknown,
  _dom: unknown,
  _rect: unknown,
  size: { contentSize: [number, number]; viewSize: [number, number] },
): [number, number] {
  const [x, y] = point;
  const [contentW, contentH] = size.contentSize;
  const [viewW] = size.viewSize;
  const gap = 28;

  let px = x + gap;
  if (px + contentW > viewW - 4) {
    px = x - contentW - gap;
  }
  if (px < 4) {
    px = 4;
  }

  let py = y - contentH - gap;
  if (py < 4) {
    py = y + gap;
  }

  return [px, py];
}

interface ZrWheelEvent {
  offsetX: number;
  wheelDelta: number;
  event?: { preventDefault?: () => void };
}

/**
 * Wheel zoom for time charts, replacing the built-in handler: zooms around
 * the cursor with absolute time values, clamped to the data extent supplied
 * by getExtent so the view can't wander into empty space. Zooming back out
 * to the full extent dispatches a true reset (0–100%) so the chart's
 * zoomed-state tracking settles back to "default view".
 * Returns a detach function.
 */
export function attachWheelZoom(
  chart: echarts.ECharts,
  pad: { left: number; right: number },
  getExtent: () => [number, number] | null,
): () => void {
  const zr = chart.getZr();

  const onWheel = (e: ZrWheelEvent) => {
    const delta = e.wheelDelta;
    if (!delta) {
      return;
    }

    const leftPx = pad.left;
    const rightPx = chart.getWidth() - pad.right;
    const start = chart.convertFromPixel({ xAxisIndex: 0 }, leftPx);
    const end = chart.convertFromPixel({ xAxisIndex: 0 }, rightPx);
    if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) {
      return;
    }

    e.event?.preventDefault?.();
    const extent = getExtent();

    // Zoom around the cursor; wheel up zooms in.
    const factor = delta > 0 ? 1 / 1.3 : 1.3;
    const cursor = chart.convertFromPixel({ xAxisIndex: 0 }, e.offsetX);
    let newStart = cursor - (cursor - start) * factor;
    let newEnd = cursor + (end - cursor) * factor;
    if (extent) {
      newStart = Math.max(newStart, extent[0]);
      newEnd = Math.min(newEnd, extent[1]);
      if (newStart <= extent[0] && newEnd >= extent[1]) {
        // Fully zoomed out again — issue a real reset.
        chart.dispatchAction({ type: "dataZoom", dataZoomIndex: 0, start: 0, end: 100 });
        return;
      }
    }
    if (newEnd - newStart < 1000) {
      return; // don't zoom tighter than one second of log time
    }
    chart.dispatchAction({
      type: "dataZoom",
      dataZoomIndex: 0,
      startValue: newStart,
      endValue: newEnd,
    });
  };

  zr.on("mousewheel", onWheel);
  return () => {
    zr.off("mousewheel", onWheel);
  };
}

/**
 * A [start, end] window ending at `nowMs`, with BOTH ends snapped down to the
 * bucket grid.
 *
 * This alignment is load-bearing, not tidiness. Smoothing walks the window in
 * bucket-sized steps and looks each timestamp up in a map keyed by the
 * server's bucket starts, which are whole seconds. Start from an unaligned
 * `Date.now()` and every single lookup misses, so a chart full of data draws
 * as a flat line of zeros. The server floors in local time and every real UTC
 * offset is a whole number of minutes, so flooring epoch milliseconds lands on
 * the same grid.
 */
export function bucketAlignedWindow(
  nowMs: number,
  lengthSec: number,
  bucketSeconds: number,
): [number, number] {
  const step = Math.max(1, bucketSeconds) * 1000;
  const end = Math.floor(nowMs / step) * step;
  return [end - Math.ceil((lengthSec * 1000) / step) * step, end];
}

/** Round up to a "nice" axis top: 1, 2, 2.5 or 5 times a power of ten. */
export function niceCeil(value: number): number {
  if (!(value > 0)) {
    return 1;
  }

  const magnitude = 10 ** Math.floor(Math.log10(value));
  for (const step of [1, 2, 2.5, 5]) {
    if (value <= step * magnitude) {
      return step * magnitude;
    }
  }

  return 10 * magnitude;
}

/**
 * A y-axis top that holds still.
 *
 * Auto-scaling recomputes the top every render, so a spike entering or leaving
 * the window rescales everything under it and the whole line appears to jump —
 * motion that means nothing, on top of motion that does. Three rules, in
 * ascending order of how much they actually buy:
 *
 *  1. Snap to a nice step, so small changes in the peak land on one number.
 *  2. Grow at once, shrink only once the data fits in half the axis.
 *  3. Hold the top through idle instead of collapsing to it.
 *
 * Rule 3 is the one that matters and it is not obvious. An all-zero window —
 * which live scrolling produces constantly — would otherwise drop the axis to
 * nothing, so the first hit after downtime throws it back up by whatever
 * factor the fight is worth. Measured over 93 hours of a real log at
 * one-second renders: 213 axis changes with nice-stepping alone and 30 of them
 * a 4x leap or worse; rule 2 cut the count to 188 but left all 30 leaps
 * untouched; rule 3 brought the leaps down to 17.
 *
 * The cost is deliberate and small: the axis averages ~1.4x the height it
 * strictly needs. A scale you can read across is worth more than a full one.
 */
export function stableAxisMax(dataMax: number, held: number): number {
  if (dataMax <= 0) {
    return held || 1; // idle: keep the scale the fight established
  }

  const target = niceCeil(dataMax);
  if (target >= held) {
    return target;
  }

  return target <= held / 2 ? target : held;
}

/**
 * Axis ceilings, held OUTSIDE the component.
 *
 * A ref resets when the component remounts, and a remount takes the ceiling
 * with it — the next draw starts from zero and snaps to whatever the current
 * data needs, which is the jump the hysteresis exists to prevent. Since the
 * ceiling describes the data rather than the mounted instance, it lives here
 * and is keyed by chart identity plus scope: a genuine change of scope still
 * forgets it, a remount does not.
 */
const axisCeilings = new Map<string, number>();

/** The stabilised ceiling for `key`, advanced by this render's data. */
export function heldAxisMax(key: string, dataMax: number): number {
  const next = stableAxisMax(dataMax, axisCeilings.get(key) ?? 0);
  axisCeilings.set(key, next);
  // Bounded: one entry per chart per scope, and scopes are few.
  if (axisCeilings.size > 200) {
    axisCeilings.clear();
  }

  return next;
}
