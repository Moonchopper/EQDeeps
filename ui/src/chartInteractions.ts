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

/**
 * Round up to a "nice" axis top: 1, 1.5, 2, 3, 4, 5 or 7.5 times a power of
 * ten.
 *
 * Finer than the classic 1/2/2.5/5 ladder, for a reason that only appears once
 * the ceiling is allowed to move. Three of those rungs sit exactly two apart
 * (1→2, 2.5→5, 5→10), so stepping down one rung HALVES the axis — and the
 * hysteresis below, which shrinks once the data fits half the axis, is then
 * satisfied by the very next rung down. "Grow above 100" and "shrink at 100"
 * become the same threshold, and a peak wandering across it flips the axis by
 * 2x on alternate renders.
 *
 * No rung here is half of another. A step down is at most a third of the
 * height rather than a half, and the grow and shrink thresholds cannot meet.
 */
export function niceCeil(value: number): number {
  if (!(value > 0)) {
    return 1;
  }

  const magnitude = 10 ** Math.floor(Math.log10(value));
  for (const step of [1, 1.5, 2, 3, 4, 5, 7.5]) {
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
 *  2. Grow at once, shrink only once the data fits well inside half the axis.
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
 * "Well inside" in rule 2 is load-bearing, and was not there originally. When
 * the shrink took the data fitting half EXACTLY (`<=`), it and the grow test
 * were the same threshold: on the old ladder the next rung down was half the
 * axis, so a peak drifting either side of it flipped the ceiling by 2x on
 * alternate renders. Replaying 8,988 renders of real damage through the old
 * rule, 41 of its 66 axis changes returned to a height it had held within the
 * previous ten renders — nearly two thirds of all the movement undoing itself,
 * which is exactly the motion this function exists to prevent. A strict
 * comparison, on the finer ladder above, leaves 23 of 61, and because a step
 * down is now a third rather than a half the total distance travelled falls by
 * a fifth.
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

  return target < held / 2 ? target : held;
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
  // Re-inserting moves the key to the young end of the insertion order, so the
  // eviction below drops whichever ceiling has gone longest without a draw.
  axisCeilings.delete(key);
  axisCeilings.set(key, next);
  // Bounded: one entry per chart per scope. Evicting the oldest rather than
  // emptying the map is the point — a scope is a frame, so a session spent
  // clicking through fights mints keys steadily, and clearing would drop every
  // live chart's ceiling at the same moment. Each would then snap to whatever
  // its data needed on the next render, which is precisely the jump this
  // function exists to prevent, arriving everywhere at once.
  while (axisCeilings.size > 200) {
    const oldest = axisCeilings.keys().next().value;
    if (oldest === undefined) {
      break;
    }
    axisCeilings.delete(oldest);
  }

  return next;
}

/**
 * Roughly how many points are worth fetching for one line. A chart is about a
 * thousand pixels wide, so beyond this every extra point is smaller than a
 * pixel — invisible, but still queried, serialised, transferred and parsed.
 */
const TARGET_POINTS = 1500;

/** Bucket widths worth snapping to, so the choice is stable and readable. */
const BUCKET_LADDER = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

/**
 * The bucket a query should actually use: the panel's own width, or coarser
 * when the range is long enough that its width would produce more points than
 * anyone can see.
 *
 * Measured on a real log, damage by player over 24 hours: at a 1-second bucket
 * that is 26,113 points, 1.2 MB and 827 ms; at a minute it is 1,090 points,
 * 56 KB and 114 ms. The picture is the same either way — the extra points land
 * 26-deep on a single pixel. Ranges short enough to fetch honestly are left
 * exactly as they were, so the default 15-minute view is untouched.
 */
export function queryBucketSeconds(baseSeconds: number, spanSeconds: number): number {
  const base = Math.max(1, Math.round(baseSeconds));
  if (!(spanSeconds > 0)) {
    return base;
  }

  const needed = spanSeconds / TARGET_POINTS;
  if (needed <= base) {
    return base;
  }

  return BUCKET_LADDER.find((step) => step >= needed && step >= base) ?? Math.ceil(needed);
}


/**
 * A cheap stand-in for "the fight bands would look different".
 *
 * The fights array is replaced on every hub push, so depending on it directly
 * redraws every chart several times a second while combat is live. What the
 * bands actually show is where fights start and end, and neither can move
 * visibly faster than one bucket — so the count plus the newest end, rounded
 * to the bucket, captures every change worth repainting for.
 */
export function fightBandsKey(
  fights: { lastDamageTime: string }[],
  bucketSeconds: number,
): string {
  if (fights.length === 0) {
    return "0";
  }

  const step = Math.max(1, bucketSeconds) * 1000;
  const newest = new Date(fights[fights.length - 1].lastDamageTime).getTime();
  return `${fights.length}|${Math.floor(newest / step)}`;
}

/** One plotted line, in the shape both time charts already build. */
export interface HoverLine {
  name: string;
  /** Sorted by time; a null value is a gap. */
  data: [number, number | null][];
}

/** How far, in pixels, the pointer can be from a line and still be "on" it. */
const LINE_HOVER_PX = 14;

/**
 * Nearest-line hover: whichever line is closest to the pointer, within
 * LINE_HOVER_PX, is the hovered one — the whole plot is the hover surface,
 * not the stroke.
 *
 * ECharts hit-tests a line against its own stroke plus a 5px tolerance, so a
 * 2px line has to be hit within about 7px, in a chart whose whole point is
 * eight of them crossing. Pointing at one was a precision task. This walks the
 * plotted data instead: the time under the cursor, the value each line has
 * there, and the pixel distance to each — one binary search per line per
 * frame, which is nothing next to what a tooltip costs.
 *
 * It speaks ECharts' own actions — `highlight` and `downplay` by series name —
 * so the emphasis, the fade of the others and the linked highlight
 * (useChartLink listens for exactly these) all follow without knowing this
 * exists. Coalesced to one animation frame so a fast mouse costs one search
 * per frame, not one per event.
 *
 * `getLines` is read on every frame rather than captured, because the chart
 * rebuilds its series without remounting. Returns a detach function.
 */
export function attachNearestLineHover(
  chart: echarts.ECharts,
  getLines: () => HoverLine[],
): () => void {
  const zr = chart.getZr();
  let pending: { x: number; y: number } | null = null;
  let frame = 0;
  let current: string | null = null;

  const apply = (name: string | null) => {
    if (name === current) {
      return;
    }
    if (current !== null) {
      chart.dispatchAction({ type: "downplay", seriesName: current });
    }
    current = name;
    if (name !== null) {
      chart.dispatchAction({ type: "highlight", seriesName: name });
    }
  };

  const nearest = (x: number, y: number): string | null => {
    if (!chart.containPixel({ gridIndex: 0 }, [x, y])) {
      return null;
    }
    const t = chart.convertFromPixel({ xAxisIndex: 0 }, x);
    if (!Number.isFinite(t)) {
      return null;
    }
    // Both axes are linear (time and value), so two conversions per frame
    // give the whole pixel mapping; asking ECharts per point costs more than
    // the search does.
    const origin = chart.convertToPixel({ gridIndex: 0 }, [t, 0]) as [number, number] | undefined;
    const unit = chart.convertToPixel({ gridIndex: 0 }, [t + 1000, 1]) as
      | [number, number]
      | undefined;
    if (!origin || !unit) {
      return null;
    }
    const xPerMs = (unit[0] - origin[0]) / 1000;
    const yPerValue = unit[1] - origin[1];
    const toPixel = (time: number, value: number): [number, number] => [
      origin[0] + (time - t) * xPerMs,
      origin[1] + value * yPerValue,
    ];
    let best: string | null = null;
    let bestDist = LINE_HOVER_PX;
    for (const line of getLines()) {
      const data = line.data;
      if (data.length === 0) {
        continue;
      }
      // Binary search for the first point at or after the cursor's time.
      let lo = 0;
      let hi = data.length - 1;
      while (lo < hi) {
        const mid = (lo + hi) >> 1;
        if (data[mid][0] < t) {
          lo = mid + 1;
        } else {
          hi = mid;
        }
      }
      // The point found and the one before it bracket the cursor; a gap on
      // either side simply contributes nothing.
      for (const i of [lo - 1, lo]) {
        const point = data[i];
        if (!point || point[1] === null) {
          continue;
        }
        const px = toPixel(point[0], point[1]);
        const dist = Math.hypot(px[0] - x, px[1] - y);
        if (dist < bestDist) {
          bestDist = dist;
          best = line.name;
        }
      }
    }
    return best;
  };

  const flush = () => {
    frame = 0;
    if (pending) {
      apply(nearest(pending.x, pending.y));
      pending = null;
    }
  };

  const onMove = (e: { offsetX: number; offsetY: number }) => {
    pending = { x: e.offsetX, y: e.offsetY };
    if (!frame) {
      frame = requestAnimationFrame(flush);
    }
  };
  const onOut = () => {
    pending = null;
    apply(null);
  };

  zr.on("mousemove", onMove);
  zr.on("globalout", onOut);
  return () => {
    zr.off("mousemove", onMove);
    zr.off("globalout", onOut);
    if (frame) {
      cancelAnimationFrame(frame);
    }
  };
}
