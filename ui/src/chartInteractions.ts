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
  event?: { shiftKey?: boolean; preventDefault?: () => void };
}

/**
 * Wheel navigation for time charts, replacing the built-in wheel zoom (whose
 * modifier handling can't make zoom and pan exclusive): plain wheel zooms
 * around the cursor, shift+wheel scrubs left/right. Both move the dataZoom
 * window with absolute time values, clamped to the data extent supplied by
 * getExtent, so the view can't wander into empty space.
 * Zooming back out to the full extent dispatches a true reset (0–100%) so the
 * chart's zoomed-state tracking can settle back to "default view".
 * Returns a detach function.
 */
export function attachWheelNavigation(
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

    if (e.event?.shiftKey) {
      // Scrub: a fifth of the window per notch; wheel down moves later.
      let shift = (end - start) * 0.2 * (delta > 0 ? -1 : 1);
      if (extent) {
        shift = shift > 0 ? Math.min(shift, extent[1] - end) : Math.max(shift, extent[0] - start);
      }
      if (shift === 0) {
        return;
      }
      chart.dispatchAction({
        type: "dataZoom",
        dataZoomIndex: 0,
        startValue: start + shift,
        endValue: end + shift,
      });
      return;
    }

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
