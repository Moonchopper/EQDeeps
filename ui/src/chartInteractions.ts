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

interface ZrMouseEvent {
  offsetX: number;
  event?: { button?: number; preventDefault?: () => void };
}

/**
 * Press-and-hold middle mouse button to scrub the time axis left/right.
 * Pixel movement converts to time through the axis, and the window shifts via
 * a dataZoom action — so scrubbing behaves exactly like a zoom (the reset pill
 * appears, live-follow pauses) and composes with drag-zoom and wheel-zoom.
 * Returns a detach function.
 */
export function attachMiddleScrub(
  chart: echarts.ECharts,
  pad: { left: number; right: number },
): () => void {
  const zr = chart.getZr();
  let lastX: number | null = null;

  const onDown = (e: ZrMouseEvent) => {
    if (e.event?.button === 1) {
      lastX = e.offsetX;
      e.event.preventDefault?.(); // stop the browser's middle-click autoscroll
    }
  };

  const onMove = (e: ZrMouseEvent) => {
    if (lastX === null) {
      return;
    }
    const dx = e.offsetX - lastX;
    if (dx === 0) {
      return;
    }
    lastX = e.offsetX;

    const leftPx = pad.left;
    const rightPx = chart.getWidth() - pad.right;
    const startValue = chart.convertFromPixel({ xAxisIndex: 0 }, leftPx);
    const endValue = chart.convertFromPixel({ xAxisIndex: 0 }, rightPx);
    if (!Number.isFinite(startValue) || !Number.isFinite(endValue) || endValue <= startValue) {
      return;
    }

    // Content follows the cursor: dragging right shifts the window earlier.
    const timePerPixel = (endValue - startValue) / (rightPx - leftPx);
    const delta = dx * timePerPixel;
    chart.dispatchAction({
      type: "dataZoom",
      dataZoomIndex: 0,
      startValue: startValue - delta,
      endValue: endValue - delta,
    });
  };

  const onUp = () => {
    lastX = null;
  };

  zr.on("mousedown", onDown);
  zr.on("mousemove", onMove);
  zr.on("mouseup", onUp);
  zr.on("globalout", onUp);
  return () => {
    zr.off("mousedown", onDown);
    zr.off("mousemove", onMove);
    zr.off("mouseup", onUp);
    zr.off("globalout", onUp);
  };
}
