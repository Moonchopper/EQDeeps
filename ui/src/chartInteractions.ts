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
