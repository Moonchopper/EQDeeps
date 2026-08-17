import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { MapLabel, ZoneMap } from "../api";
import { colorCache } from "./mapColors";

/**
 * World space maps straight onto screen space: +X is east and runs right, +Y is
 * south and runs down. North is therefore up, with no axis flipped.
 *
 * <p>This contradicts the folklore that EverQuest maps are drawn negated, so it
 * was settled against the files rather than taken on trust. The test only works
 * on zones that share an **outdoor border** — every zone has its own origin, so
 * a door between two interiors says nothing about which is north of which,
 * which is why Northern and Southern Felwithe appear to disagree and are not
 * evidence.</p>
 *
 * <p>On the borders it is unambiguous. East-west: East Commonlands puts West
 * Commonlands to its west, West Freeport puts East Freeport to its east, and so
 * on — 5 of 5 with no flip, 0 of 5 with. North-south: South Qeynos puts North
 * Qeynos north, and North Qeynos puts South Qeynos south, each confirming the
 * other. Getting this wrong mirrors every zone, so it is one constant with its
 * evidence attached rather than a setting for the user to fight with.</p>
 */
const FLIP_X = 1;
const FLIP_Y = 1;

/** Labels below this scale are noise; the zone name and exits still show. */
const LABEL_SCALE = 0.55;

const MIN_SCALE = 0.02;
const MAX_SCALE = 40;

export interface MapView {
  scale: number;
  x: number;
  y: number;
}

/**
 * A point to draw over the map — a spawn point, in **map** coordinates
 * (the caller has already turned the game's into the file's).
 */
export interface MapMarker {
  x: number;
  y: number;
  /** What stands here; drawn beside the point when there are few enough to read. */
  label: string;
  /** A pinned mob's colour. Without one the point is part of the roster: small and quiet. */
  color?: string;
  /** Drawn larger and brighter, and named — the one being pointed at. */
  lit?: boolean;
  /** Stepped back further still — everything else while something is lit. */
  dim?: boolean;
}

interface Props {
  map: ZoneMap;
  /** Layer indices to draw. */
  layers: number[];
  /** Draw the file's own colours instead of lifting them for a dark page. */
  trueColors?: boolean;
  /** Inclusive world-Z window, for zones stacked in floors. */
  zRange?: [number, number] | null;
  /** A label the user is pointing at elsewhere — drawn picked out. */
  highlight?: string | null;
  /** Points to draw over the map — a mob's spawn points, from the Bestiary. */
  markers?: MapMarker[];
  /** Clicking an exit label. The argument is the destination as written. */
  onTravel?: (destination: string) => void;
}

interface Placed {
  label: MapLabel;
  sx: number;
  sy: number;
  width: number;
}

function isExit(text: string): boolean {
  return /^(to|from)\s/i.test(text);
}

export function MapCanvas({
  map,
  layers,
  trueColors = false,
  zRange = null,
  highlight = null,
  markers = [],
  onTravel,
}: Props) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<MapView>({ scale: 1, x: 0, y: 0 });
  const placedRef = useRef<Placed[]>([]);
  const frameRef = useRef(0);
  const dragRef = useRef<{ x: number; y: number } | null>(null);

  const [size, setSize] = useState({ w: 0, h: 0 });
  const [hover, setHover] = useState<string | null>(null);
  const [, forceDraw] = useState(0);

  // Fit the zone whenever a different one is shown. Deliberately keyed on the
  // zone rather than on the map object: toggling a layer must not throw away
  // the pan and zoom the user has set up.
  useLayoutEffect(() => {
    const { bounds } = map;
    if (size.w === 0 || size.h === 0 || bounds.minX > bounds.maxX) {
      return;
    }

    const w = Math.max(1, bounds.maxX - bounds.minX);
    const h = Math.max(1, bounds.maxY - bounds.minY);
    const scale = Math.min(size.w / w, size.h / h) * 0.92;

    viewRef.current = {
      scale,
      x: size.w / 2 - FLIP_X * ((bounds.minX + bounds.maxX) / 2) * scale,
      y: size.h / 2 - FLIP_Y * ((bounds.minY + bounds.maxY) / 2) * scale,
    };
    forceDraw((n) => n + 1);
  }, [map.shortName, map.set, size.w, size.h]);

  useEffect(() => {
    const wrap = wrapRef.current;
    if (!wrap) {
      return;
    }

    const observer = new ResizeObserver(() => {
      setSize({ w: wrap.clientWidth, h: wrap.clientHeight });
    });
    observer.observe(wrap);
    setSize({ w: wrap.clientWidth, h: wrap.clientHeight });

    return () => observer.disconnect();
  }, []);

  const draw = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas || size.w === 0) {
      return;
    }

    const ctx = canvas.getContext("2d");
    if (!ctx) {
      return;
    }

    const dpr = window.devicePixelRatio || 1;
    if (canvas.width !== size.w * dpr || canvas.height !== size.h * dpr) {
      canvas.width = size.w * dpr;
      canvas.height = size.h * dpr;
    }

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, size.w, size.h);

    const view = viewRef.current;
    const color = colorCache(!trueColors);
    const show = new Set(layers);
    const [zMin, zMax] = zRange ?? [-Infinity, Infinity];

    ctx.lineWidth = 1;
    ctx.lineCap = "round";

    for (const layer of map.layers) {
      if (!show.has(layer.index)) {
        continue;
      }

      // One path per colour. This is the whole reason the wire format groups
      // by colour: 26,000 segments in six paths rather than 26,000 style
      // changes is the difference between smooth panning and a slideshow.
      for (const stroke of layer.strokes) {
        ctx.strokeStyle = color(stroke.r, stroke.g, stroke.b);
        ctx.beginPath();

        const s = stroke.segments;
        for (let i = 0; i < s.length; i += 6) {
          const z1 = s[i + 2];
          const z2 = s[i + 5];
          if ((z1 < zMin && z2 < zMin) || (z1 > zMax && z2 > zMax)) {
            continue;
          }

          ctx.moveTo(FLIP_X * s[i] * view.scale + view.x, FLIP_Y * s[i + 1] * view.scale + view.y);
          ctx.lineTo(
            FLIP_X * s[i + 3] * view.scale + view.x,
            FLIP_Y * s[i + 4] * view.scale + view.y,
          );
        }

        ctx.stroke();
      }
    }

    // ---- labels ----------------------------------------------------------
    const placed: Placed[] = [];
    ctx.font = "12px ui-sans-serif, system-ui, sans-serif";
    ctx.textBaseline = "middle";

    for (const layer of map.layers) {
      if (!show.has(layer.index)) {
        continue;
      }

      for (const label of layer.labels) {
        if (label.z < zMin || label.z > zMax) {
          continue;
        }

        const exit = isExit(label.text);

        // Exits always show. They are the reason to open a zone map you do not
        // know, and hiding them until zoomed in hides the answer.
        if (!exit && view.scale < LABEL_SCALE) {
          continue;
        }

        const sx = FLIP_X * label.x * view.scale + view.x;
        const sy = FLIP_Y * label.y * view.scale + view.y;
        if (sx < -80 || sy < -20 || sx > size.w + 80 || sy > size.h + 20) {
          continue;
        }

        const lit = highlight !== null && label.text === highlight;
        const width = ctx.measureText(label.text).width;

        // Cheap overlap rejection: a dense zone has hundreds of labels on top
        // of each other and drawing them all produces a smear, not a map.
        const clash = placed.some(
          (p) => Math.abs(p.sy - sy) < 12 && Math.abs(p.sx - sx) < (p.width + width) / 2 + 6,
        );
        if (clash && !lit && !exit) {
          continue;
        }

        ctx.fillStyle = exit ? "#e8963c" : lit ? "#f1ece3" : "#c5bdae";

        if (exit || lit) {
          // A halo, so a label over dense geometry stays readable without a
          // filled box hiding the drawing underneath it.
          ctx.lineWidth = 3;
          ctx.strokeStyle = "rgba(15,13,11,0.85)";
          ctx.strokeText(label.text, sx + 5, sy);
          ctx.lineWidth = 1;
        }

        ctx.fillText(label.text, sx + 5, sy);

        ctx.beginPath();
        ctx.arc(sx, sy, exit || lit ? 3 : 2, 0, Math.PI * 2);
        ctx.fill();

        placed.push({ label, sx, sy, width });
      }
    }

    // ---- markers ---------------------------------------------------------
    // Spawn points, over everything. Three ranks, drawn quiet to loud so the
    // loud ones land on top: the zone's whole roster as small muted dots
    // (dimmer still while something else is lit), pinned mobs as filled
    // discs in their own colour with a dark halo so they read on dense
    // geometry, and the one being pointed at larger, brighter and named.
    // Pinned points are named while there are few enough to read; past that
    // the colour is the answer and the header's chips name the mobs.
    if (markers.length > 0) {
      // A mob with a dozen spawn points needs its name once, not twelve times
      // along the corridor; a handful can each carry it. The colour and the
      // header's chips do the rest for pins.
      const perLabel = new Map<string, number>();
      for (const m of markers) {
        if (m.lit || m.color) perLabel.set(m.label, (perLabel.get(m.label) ?? 0) + 1);
      }
      const named = new Set<string>();
      const nameOnce = (m: MapMarker) => {
        if ((perLabel.get(m.label) ?? 0) <= 3) return true;
        if (named.has(m.label)) return false;
        named.add(m.label);
        return true;
      };
      const rank = (m: MapMarker) => (m.lit ? 2 : m.color ? 1 : 0);
      const ordered = [...markers].sort((a, b) => rank(a) - rank(b));
      for (const m of ordered) {
        const sx = FLIP_X * m.x * view.scale + view.x;
        const sy = FLIP_Y * m.y * view.scale + view.y;
        if (sx < -20 || sy < -20 || sx > size.w + 20 || sy > size.h + 20) {
          continue;
        }

        if (!m.lit && !m.color) {
          ctx.beginPath();
          ctx.arc(sx, sy, m.dim ? 2 : 2.5, 0, Math.PI * 2);
          ctx.fillStyle = m.dim ? "rgba(197,189,174,0.22)" : "rgba(197,189,174,0.5)";
          ctx.fill();
          continue;
        }

        const r = m.lit ? 7 : 5;
        const fill = m.lit ? "#f1ece3" : m.color!;
        ctx.globalAlpha = m.dim ? 0.35 : 1;
        ctx.beginPath();
        ctx.arc(sx, sy, r + 2, 0, Math.PI * 2);
        ctx.fillStyle = "rgba(15,13,11,0.9)";
        ctx.fill();
        ctx.beginPath();
        ctx.arc(sx, sy, r, 0, Math.PI * 2);
        ctx.fillStyle = fill;
        ctx.fill();

        if (!m.dim && nameOnce(m)) {
          ctx.lineWidth = 3;
          ctx.strokeStyle = "rgba(15,13,11,0.85)";
          ctx.strokeText(m.label, sx + r + 4, sy);
          ctx.lineWidth = 1;
          ctx.fillStyle = fill;
          ctx.fillText(m.label, sx + r + 4, sy);
        }
        ctx.globalAlpha = 1;
      }
    }

    placedRef.current = placed;
  }, [map, layers, trueColors, zRange, highlight, markers, size]);

  useEffect(() => {
    cancelAnimationFrame(frameRef.current);
    frameRef.current = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(frameRef.current);
  });

  const labelAt = (px: number, py: number): Placed | null => {
    for (let i = placedRef.current.length - 1; i >= 0; i--) {
      const p = placedRef.current[i];
      if (px >= p.sx - 4 && px <= p.sx + p.width + 8 && Math.abs(py - p.sy) <= 8) {
        return p;
      }
    }
    return null;
  };

  const onWheel = (e: React.WheelEvent<HTMLCanvasElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const px = e.clientX - rect.left;
    const py = e.clientY - rect.top;
    const view = viewRef.current;

    const factor = Math.exp(-e.deltaY * 0.0015);
    const scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, view.scale * factor));
    const applied = scale / view.scale;

    // Zoom about the pointer, so the thing under the cursor stays under it.
    viewRef.current = {
      scale,
      x: px - (px - view.x) * applied,
      y: py - (py - view.y) * applied,
    };
    forceDraw((n) => n + 1);
  };

  const onPointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    // Only the primary button pans; the right button is the way out to the
    // World view and must not start a drag on its way there.
    if (e.button !== 0) {
      return;
    }
    e.currentTarget.setPointerCapture(e.pointerId);
    dragRef.current = { x: e.clientX, y: e.clientY };
  };

  const onPointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const drag = dragRef.current;

    if (drag) {
      const view = viewRef.current;
      viewRef.current = {
        scale: view.scale,
        x: view.x + (e.clientX - drag.x),
        y: view.y + (e.clientY - drag.y),
      };
      dragRef.current = { x: e.clientX, y: e.clientY };
      forceDraw((n) => n + 1);
      return;
    }

    const rect = e.currentTarget.getBoundingClientRect();
    const hit = labelAt(e.clientX - rect.left, e.clientY - rect.top);
    const next = hit && isExit(hit.label.text) ? hit.label.text : null;
    if (next !== hover) {
      setHover(next);
    }
  };

  const onPointerUp = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const drag = dragRef.current;
    dragRef.current = null;

    // A click, not the end of a drag.
    if (!drag || !onTravel) {
      return;
    }

    const rect = e.currentTarget.getBoundingClientRect();
    const hit = labelAt(e.clientX - rect.left, e.clientY - rect.top);
    if (hit && isExit(hit.label.text)) {
      onTravel(hit.label.text);
    }
  };

  return (
    <div className="map-canvas-wrap" ref={wrapRef}>
      <canvas
        ref={canvasRef}
        className="map-canvas"
        style={{
          width: size.w,
          height: size.h,
          cursor: hover ? "pointer" : dragRef.current ? "grabbing" : "grab",
        }}
        onWheel={onWheel}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerLeave={() => {
          dragRef.current = null;
          setHover(null);
        }}
      />
    </div>
  );
}
