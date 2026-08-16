import { useCallback, useContext, useEffect, useRef, useState, type MutableRefObject, type RefObject } from "react";
import type * as echarts from "echarts";
import { createPortal } from "react-dom";
import { IconExternalLink, IconStar, IconStarFilled } from "@tabler/icons-react";
import { api } from "../api";
import { defaultLinkFor, linksFor, lookupName, type LookupRef } from "./providers";
import { rememberDefaultProvider, useLookupWorld } from "./lookupSettings";
import { LookupScopeContext } from "./LookupScope";

/*
 * One lookup menu for the whole page (issues #51, #62). Every trigger — the
 * arrow beside a name, a chart's axis label, a legend entry — asks this
 * module to open the menu at a point or to go straight to the usual site;
 * nothing renders a menu of its own. That is what lets a canvas label, which
 * has no DOM to hang a popover on, behave exactly like the arrow in a table.
 */

// ---- item ids, resolved once and shared ------------------------------------

/**
 * Item ids resolved so far, per session and name key: a hover over a column
 * of two hundred items asks about each once, and the answer is there for
 * the click. A miss is not remembered — the registry learns ids as the
 * player loots and sets filters, and the next hover should see that.
 */
const resolved = new Map<string, number>();
const resolving = new Map<string, Promise<number | null>>();

function idKey(sessionId: string, name: string): string {
  return `${sessionId}|${lookupName(name, "item").toLowerCase()}`;
}

/** The id if it has already been resolved; no request. */
export function knownItemId(sessionId: string | undefined, name: string): number | undefined {
  return sessionId ? resolved.get(idKey(sessionId, name)) : undefined;
}

/** Resolves an item's id through the session's registry (F29), once per name; null for a stranger. */
export function resolveItemId(sessionId: string, name: string): Promise<number | null> {
  const key = idKey(sessionId, name);
  const known = resolved.get(key);
  if (known !== undefined) return Promise.resolve(known);
  let pending = resolving.get(key);
  if (!pending) {
    pending = api
      .resolveItem(sessionId, name)
      .then((record) => record?.id ?? null)
      .catch(() => null)
      .then((value) => {
        if (value !== null) resolved.set(key, value);
        resolving.delete(key);
        return value;
      });
    resolving.set(key, pending);
  }
  return pending;
}

// ---- the menu's state -------------------------------------------------------

interface MenuState {
  ref: LookupRef;
  x: number;
  y: number;
}

let current: MenuState | null = null;
const listeners = new Set<() => void>();

function set(next: MenuState | null): void {
  current = next;
  for (const l of listeners) l();
}

/**
 * Opens the menu for a reference at a point (viewport coordinates), or moves
 * it there. Positioned below and left-aligned to the point, nudged back
 * inside the viewport when the point sits near the right edge.
 */
export function openLookupMenu(ref: LookupRef, at: { x: number; y: number }): void {
  const width = 260;
  const x = Math.max(8, Math.min(at.x, window.innerWidth - width - 8));
  set({ ref, x, y: at.y });
}

export function closeLookupMenu(): void {
  if (current) set(null);
}

/** Whether the menu is open for this reference (so a trigger can draw itself "on"). */
export function useLookupMenuOpenFor(ref: LookupRef): boolean {
  const [, bump] = useState(0);
  useEffect(() => {
    const l = () => bump((n) => n + 1);
    listeners.add(l);
    return () => {
      listeners.delete(l);
    };
  }, []);
  return current !== null && current.ref.kind === ref.kind && current.ref.name === ref.name;
}

// ---- what a trigger does ---------------------------------------------------

/**
 * The two gestures every trigger offers: `go` opens the usual site in a new
 * tab, synchronously (a browser's popup rules want a user gesture on the
 * stack), falling back to the menu when no site can address the reference;
 * `menu` opens the menu at a point. `prefetch` asks for an item's id ahead of
 * need — on hover, say — so `go` can land on the id-addressed page.
 */
export function useLookupActions(): {
  go: (ref: LookupRef, at: { x: number; y: number }) => void;
  menu: (ref: LookupRef, at: { x: number; y: number }) => void;
  prefetch: (ref: LookupRef, then?: () => void) => void;
  /** Whether any site at all can address the reference in this world. */
  canLookup: (ref: LookupRef) => boolean;
} {
  const { world, preferredId } = useLookupWorld();
  const { sessionId } = useContext(LookupScopeContext);

  const withId = useCallback(
    (ref: LookupRef): LookupRef =>
      ref.kind === "item" && ref.id === undefined ? { ...ref, id: knownItemId(sessionId, ref.name) } : ref,
    [sessionId],
  );

  const prefetch = useCallback(
    (ref: LookupRef, then?: () => void) => {
      if (ref.kind !== "item" || ref.id !== undefined || !sessionId) return;
      if (knownItemId(sessionId, ref.name) !== undefined) return;
      void resolveItemId(sessionId, ref.name).then((value) => {
        if (value !== null) then?.();
      });
    },
    [sessionId],
  );

  const menu = useCallback((ref: LookupRef, at: { x: number; y: number }) => openLookupMenu(ref, at), []);

  const go = useCallback(
    (ref: LookupRef, at: { x: number; y: number }) => {
      if (current) {
        closeLookupMenu();
        return;
      }
      const link = defaultLinkFor(world, withId(ref), preferredId);
      if (link) {
        window.open(link.url, "_blank", "noopener,noreferrer");
      } else {
        openLookupMenu(ref, at);
      }
    },
    [world, preferredId, withId],
  );

  const canLookup = useCallback((ref: LookupRef) => linksFor(world, ref).length > 0, [world]);

  return { go, menu, prefetch, canLookup };
}

// ---- the menu itself --------------------------------------------------------

/**
 * Renders the one menu, wherever it was asked to open. Mount once, inside
 * the LookupScope. Every entry is a real link opening a new tab (the shell
 * hands new windows to the default browser, ADR-009); the star on each makes
 * it what a plain click opens from then on, per world.
 */
export function LookupMenuHost() {
  const [, bump] = useState(0);
  useEffect(() => {
    const l = () => bump((n) => n + 1);
    listeners.add(l);
    return () => {
      listeners.delete(l);
    };
  }, []);

  const { world, preferredId } = useLookupWorld();
  const { sessionId } = useContext(LookupScopeContext);
  const menu = useRef<HTMLDivElement>(null);
  const state = current;

  // An item's id, asked for on open; the id-addressed sites join when it lands.
  const [, gotId] = useState(0);
  useEffect(() => {
    if (!state || state.ref.kind !== "item" || state.ref.id !== undefined || !sessionId) return;
    let cancelled = false;
    void resolveItemId(sessionId, state.ref.name).then((value) => {
      if (!cancelled && value !== null) gotId((n) => n + 1);
    });
    return () => {
      cancelled = true;
    };
  }, [state, sessionId]);

  useEffect(() => {
    if (!state) return;
    const onDown = (e: Event) => {
      if (menu.current?.contains(e.target as Node)) return;
      closeLookupMenu();
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && closeLookupMenu();
    // Any scroll moves the anchor out from under a fixed menu; close rather than drift.
    const onScroll = () => closeLookupMenu();
    // Deferred a tick so the click that opened the menu is not the one that closes it.
    const t = window.setTimeout(() => {
      document.addEventListener("mousedown", onDown);
      document.addEventListener("keydown", onKey);
      document.addEventListener("scroll", onScroll, true);
    }, 0);
    return () => {
      window.clearTimeout(t);
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("scroll", onScroll, true);
    };
  }, [state]);

  if (!state) return null;

  const ref: LookupRef =
    state.ref.kind === "item" && state.ref.id === undefined
      ? { ...state.ref, id: knownItemId(sessionId, state.ref.name) }
      : state.ref;
  const links = linksFor(world, ref);
  const primary = defaultLinkFor(world, ref, preferredId);
  const shown = lookupName(ref.name, ref.kind);
  const kindLabel = ref.kind === "npc" ? "mob" : ref.kind;

  return createPortal(
    <div
      ref={menu as RefObject<HTMLDivElement>}
      className="lookup-menu"
      role="menu"
      style={{ left: state.x, top: state.y }}
      onClick={(e) => e.stopPropagation()}
      onContextMenu={(e) => e.preventDefault()}
    >
      <div className="lookup-head">
        <span className="lookup-name">{shown}</span>
        <span className="lookup-kind">{kindLabel}</span>
      </div>
      {links.length === 0 && <div className="lookup-foot">No site here knows how to find this.</div>}
      {links.map(({ provider, url }) => {
        const isDefault = primary?.provider.id === provider.id;
        const isPreferred = preferredId === provider.id;
        return (
          <div key={provider.id} className={"lookup-item" + (isDefault ? " default" : "")} role="none">
            <a
              role="menuitem"
              href={url}
              target="_blank"
              rel="noreferrer"
              onClick={() => closeLookupMenu()}
              title={`Open ${shown} on ${provider.name}`}
            >
              <span>{provider.name}</span>
              {isDefault && <span className="lookup-tag">click opens</span>}
              <IconExternalLink size={12} stroke={2} aria-hidden />
            </a>
            <button
              type="button"
              className={"lookup-star" + (isPreferred ? " on" : "")}
              title={
                isPreferred
                  ? `A plain click opens ${provider.name} for ${world.name} — click to forget`
                  : `Make ${provider.name} what a plain click opens for ${world.name}`
              }
              aria-label={`Make ${provider.name} the default site`}
              aria-pressed={isPreferred}
              onClick={(e) => {
                e.stopPropagation();
                void rememberDefaultProvider(world.id, isPreferred ? null : provider.id);
              }}
            >
              {isPreferred ? <IconStarFilled size={12} aria-hidden /> : <IconStar size={12} stroke={2} aria-hidden />}
            </button>
          </div>
        );
      })}
      <div className="lookup-foot">
        Click a name or its arrow to open the starred site · right-click for this menu · sites for {world.name}
      </div>
    </div>,
    document.body,
  );
}

// ---- charts -----------------------------------------------------------------

/**
 * Makes a chart's names into lookup triggers, the same two gestures as the
 * arrow: a click on a category-axis label goes to the usual site, a
 * right-click on an axis label, a legend entry or a bar opens the menu. A
 * canvas label has no DOM to hang an arrow on, so the label itself is the
 * door — which is also why the axis needs `triggerEvent: true` in its option,
 * or ECharts never reports the click.
 *
 * <p>`kindOf` says what a name is (a mob, an item, or null for a player, who
 * gets nothing); left-click on a legend entry is left to the selection
 * behaviour in `highlight.tsx`, which owns that gesture on charts.</p>
 *
 * <p>Call this after the effect that creates the chart, like `useChartLink`:
 * it reads `chartRef.current` on mount.</p>
 */
export function useChartLookup(
  chartRef: MutableRefObject<echarts.ECharts | null>,
  kindOf: (name: string) => LookupRef["kind"] | null,
  /** Turns an axis value into the name it stands for, when the axis is indexed and a formatter draws the label. */
  nameOfValue: (value: string) => string = (value) => value,
): void {
  const { go, menu } = useLookupActions();
  const kindOfRef = useRef(kindOf);
  kindOfRef.current = kindOf;
  const nameOfValueRef = useRef(nameOfValue);
  nameOfValueRef.current = nameOfValue;

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;

    // ECharts types the raw event as mouse-or-touch; a touch has no clientX,
    // so the fields are read as maybe-present and default to the corner.
    type Params = {
      componentType?: string;
      value?: unknown;
      name?: string;
      event?: { event?: { clientX?: number; clientY?: number; preventDefault(): void } };
    };
    const nameOf = (p: Params): string | undefined => {
      if (p.componentType === "xAxis" || p.componentType === "yAxis") {
        return typeof p.value === "string" ? nameOfValueRef.current(p.value) : undefined;
      }
      // A legend entry names its series; a bar in a category chart names its category.
      return p.componentType === "legend" || p.componentType === "series" ? p.name : undefined;
    };
    const at = (p: Params) => ({ x: p.event?.event?.clientX ?? 0, y: (p.event?.event?.clientY ?? 0) + 4 });

    const clicked = (p: Params) => {
      if (p.componentType !== "xAxis" && p.componentType !== "yAxis") return;
      const name = nameOf(p);
      const kind = name ? kindOfRef.current(name) : null;
      if (name && kind) go({ kind, name }, at(p));
    };
    const context = (p: Params) => {
      const name = nameOf(p);
      const kind = name ? kindOfRef.current(name) : null;
      if (!name || !kind) return;
      p.event?.event?.preventDefault();
      menu({ kind, name }, at(p));
    };

    // Cast through the loose shape above: ECharts' own param type is a wide
    // union and these handlers only read the fields every member has.
    const onClick = clicked as unknown as (p: unknown) => void;
    const onContext = context as unknown as (p: unknown) => void;
    chart.on("click", onClick);
    chart.on("contextmenu", onContext);
    return () => {
      if (chart.isDisposed()) return;
      chart.off("click", onClick);
      chart.off("contextmenu", onContext);
    };
  }, [chartRef, go, menu]);
}
