import { useEffect, useMemo, useRef, useState } from "react";
import {
  type ContextTimeline,
  api,
  type DiscoveredLog,
  type FightInfo,
  type MobAttackReport,
  type MobHealthReport,
  type SessionInfo,
  type UpdateMode,
  type UpdateState,
} from "./api";
import { createLiveConnection, type BackfillEvent, type TickEvent } from "./live";
import { IncomingPanel } from "./components/IncomingPanel";
import { SessionBar } from "./components/SessionBar";
import { UpdateNotice, type UpdateChoice } from "./components/UpdateNotice";
import { SettingsDialog } from "./components/SettingsDialog";
import { LogPicker, LogsDialog } from "./components/LogPicker";
import { BestiaryPanel } from "./components/BestiaryPanel";
import { Trail } from "./components/Trail";
import { screenKey, type BestiaryTarget, type Crumb, type MapTarget, type Screen } from "./trail";
import { useReferenceEnabled } from "./lookup/lookupSettings";
import { LookupScope } from "./lookup/LookupScope";
import { LookupMenuHost } from "./lookup/lookupMenu";
import { NavRail } from "./components/NavRail";
import { SelectionChip } from "./components/SelectionChip";
import { useSelectionActions } from "./highlight";
import { FightList } from "./components/FightList";
import { SummaryTable } from "./components/SummaryTable";
import { DpsChart } from "./components/DpsChart";
import { LiveMeter } from "./components/LiveMeter";
import { createEntityColors } from "./colors";
import { DeathLog } from "./components/DeathLog";
import { SelectionStats } from "./components/SelectionStats";
import { AbilityChart } from "./components/AbilityChart";
import { TimelineChart } from "./components/TimelineChart";
import { DashboardView } from "./dashboards/DashboardView";
import {
  CONTEXT_MODES,
  DEFAULT_CONTEXT_MODE,
  type ContextMode,
} from "./contextOverlay";
import { defaultPanel, newDashboard, newId, type DashboardDef } from "./dashboards/model";
import { MapView } from "./maps/MapView";
import {
  HITS_VIEW,
  MAPS_VIEW,
  BESTIARY_VIEW,
  STANCES_VIEW_ID,
  SUMMARY_VIEW,
  cloneForCustomizing,
  standardViews,
  stripStandardViews,
  summaryTrendPanels,
} from "./dashboards/standardViews";
import { PanelBody, type PanelContext } from "./dashboards/PanelBody";
import { isFramedView } from "./dashboards/railGroups";
import { DEFAULT_CHART_SETTINGS, type ChartSettings } from "./timeControls";
import { DEFAULT_LABEL_PX } from "./fightOverlay";
import {
  DEFAULT_FRAME,
  frameFromFights,
  frameFromRange,
  framedFightIds,
  fightsInFrame,
  frameSpanSeconds,
  isLive,
  type TimeFrame,
} from "./timeFrame";
import { queryBucketSeconds } from "./chartInteractions";

/**
 * How stale the log can get before wall-clock scrolling stops making sense.
 * Opening a log written days ago with the charts still chasing the clock would
 * show a window of pure zeros — the data is there, just hours to the left. An
 * hour is generous enough for any AFK and short enough that reviewing an
 * archive pins to the data instead.
 */
const LIVE_LOG_GRACE_MS = 60 * 60 * 1000;

/** Update polling: rare when nothing is happening, brisk while it is. */
const IDLE_POLL_MS = 15_000;
const ACTIVE_POLL_MS = 700;

/** How long the active session must go without a hit before an update prompt may interrupt. */
const UPDATE_PROMPT_QUIET_MS = 2 * 60_000;
/** How often a waiting prompt asks again whether it is quiet yet. */
const UPDATE_PROMPT_RECHECK_MS = 15_000;

/**
 * Whether the active session is between fights: nothing open, and the last
 * hit landed long enough ago that "between pulls" is a fair reading. An
 * empty list is quiet — no log, or a log with no fights, is not a fight.
 */
function combatQuiet(fights: FightInfo[], nowMs: number): boolean {
  let last = 0;
  for (const f of fights) {
    if (!f.closed) return false;
    const t = new Date(f.lastDamageTime).getTime();
    if (t > last) last = t;
  }
  return nowMs - last >= UPDATE_PROMPT_QUIET_MS;
}

/**
 * The default dashboard (feature F7): fight list + summary + DPS chart + live
 * meter + deaths, all scoped to the fight selection, live-updating via the hub.
 */
export default function App() {
  const [sessions, setSessions] = useState<SessionInfo[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [fights, setFights] = useState<FightInfo[]>([]);
  // Learned mob health for the active session's SERVER (F25). Null until the
  // first fetch lands, which the panel distinguishes from "nothing learned".
  const [mobs, setMobs] = useState<MobHealthReport | null>(null);
  const [attacks, setAttacks] = useState<MobAttackReport | null>(null);
  const [context, setContext] = useState<ContextTimeline | null>(null);
  // The one time frame the whole app reports over. A live tail by default;
  // picking fights turns it into the fixed range they span. There is no
  // separate "follow live" flag — a live frame *is* following.
  const [frame, setFrame] = useState<TimeFrame>(DEFAULT_FRAME);
  const [tick, setTick] = useState<TickEvent | null>(null);
  const [backfill, setBackfill] = useState<BackfillEvent | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [excludeDs, setExcludeDs] = useState(false);
  const [petRollup, setPetRollup] = useState(() => localStorage.getItem("eqdeeps.petRollup") !== "off");
  /**
   * Row density. Comfortable is the default and compact is the opt-in, which is
   * the way round it has to be: this audience is 35-55, plays at night, and the
   * recurring thread on the EverQuest interface forums is literally "font and
   * everything too small to read". Trading four visible rows for legibility is
   * the right default; anyone who wants the rows back can say so once.
   */
  const [density, setDensity] = useState<"comfortable" | "compact">(() =>
    localStorage.getItem("eqdeeps.density") === "compact" ? "compact" : "comfortable",
  );
  // Window/span for every chart in the app. One value, one place — panels have
  // no window/span of their own to disagree with it.
  const [chartDefaults, setChartDefaults] = useState<ChartSettings>(() => {
    try {
      const stored = localStorage.getItem("eqdeeps.chartDefaults");
      if (!stored) return DEFAULT_CHART_SETTINGS;
      // The window used to persist as seconds under `windowSec`. The top bar
      // that wrote it sits at the 1-second bucket, so its number was already
      // the bucket count this now stores — carry it straight across rather than
      // resetting everyone's window on upgrade.
      const { windowSec, ...rest } = JSON.parse(stored) as Partial<ChartSettings> & {
        windowSec?: number;
      };
      return {
        ...DEFAULT_CHART_SETTINGS,
        ...(typeof windowSec === "number" ? { windowBuckets: windowSec } : {}),
        ...rest,
      };
    } catch {
      return DEFAULT_CHART_SETTINGS;
    }
  });
  // Size of the mob names on the fight bands; 0 hides them. App-wide for the
  // same reason window and span are: it is how you read a chart, not a
  // property of any one of them.
  // Which lanes the context strip shows. Its own control rather than a mode of
  // the fight overlay: they answer different questions and a reader wants the
  // bands without the strip as often as the other way round.
  // Whether a framed range is measured over the hours played or the hours it
  // spans. Default is the calendar: that is what the range literally says, and
  // quietly redefining what every existing dashboard reports is not something
  // a version bump should do behind the reader's back.
  const [playedTimeOnly, setPlayedTimeOnly] = useState(
    () => localStorage.getItem("eqdeeps.playedTimeOnly") === "on",
  );

  const [contextMode, setContextMode] = useState<ContextMode>(() => {
    const stored = localStorage.getItem("eqdeeps.contextMode");
    return CONTEXT_MODES.some((m) => m.value === stored)
      ? (stored as ContextMode)
      : DEFAULT_CONTEXT_MODE;
  });

  const [fightLabelPx, setFightLabelPx] = useState<number>(() => {
    const stored = Number(localStorage.getItem("eqdeeps.fightLabelPx"));
    return Number.isFinite(stored) && stored >= 0 ? stored : DEFAULT_LABEL_PX;
  });
  // Wall-clock scrolling. The server anchors a trailing window to the newest
  // RECORD, so with the log quiet the picture freezes — which reads as "the
  // chart broke" rather than "nothing is happening". With this on the charts
  // keep advancing and the quiet time draws as the zero it is.
  const [liveScroll, setLiveScroll] = useState(
    () => localStorage.getItem("eqdeeps.liveScroll") !== "off",
  );
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [fightsCollapsed, setFightsCollapsed] = useState(
    () => localStorage.getItem("eqdeeps.fightsCollapsed") === "on",
  );
  const [discovered, setDiscovered] = useState<DiscoveredLog[]>([]);
  const [update, setUpdate] = useState<UpdateState | null>(null);
  const [showUpdateNotice, setShowUpdateNotice] = useState(false);
  // Whether the Bestiary may ask a reference site anything at all (ADR-020).
  const { enabled: referenceEnabled } = useReferenceEnabled();
  const [showSettings, setShowSettings] = useState(false);
  const [showLogs, setShowLogs] = useState(false);
  // The rail collapses to icons. Two pieces of state because the Map is a
  // special case: it brings its own left column (the zone list), so on Map
  // the rail starts collapsed whatever the standing preference, and a toggle
  // there is an override for this visit rather than a change of preference.
  // Leaving Map drops the override; the preference is what it was.
  const [railPref, setRailPref] = useState(
    () => localStorage.getItem("eqdeeps.railCollapsed") === "on",
  );
  const [railOnMap, setRailOnMap] = useState<boolean | null>(null);
  const [checkNote, setCheckNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dashboards, setDashboards] = useState<DashboardDef[]>([]);
  const [view, setView] = useState<string>("overview"); // "overview" | dashboard id
  // Which standard view the Overview section is showing. Sticky across
  // restarts: reopening on the tab you were last reading is the whole point
  // of these being views rather than dashboards you navigate to.
  const [stdView, setStdView] = useState<string>(
    () => localStorage.getItem("eqdeeps.stdView") ?? SUMMARY_VIEW,
  );
  const activeSession = sessions.find((s) => s.id === activeId);
  const character = activeSession?.character ?? "";
  // Stances only earn their tab on a log that has them — see STANCES_VIEW_ID.
  // Backfill can turn this on partway through, which is why it is derived on
  // every render rather than latched when the session opens.
  const hasStances = (activeSession?.stanceSwitches ?? 0) > 0;
  const standard = useMemo(
    () => standardViews().filter((d) => d.id !== STANCES_VIEW_ID || hasStances),
    [hasStances],
  );
  // Null means "show the hand-built Summary" — including when the remembered
  // sub-tab is a view this log doesn't have, which is what happens when you
  // switch from a stance-using character to one who has never held one.
  const activeStdView = standard.find((d) => d.id === stdView) ?? null;
  // Which rail entry Overview is actually showing. Not the same as `stdView`:
  // a remembered view this log doesn't have falls back to Summary, and the
  // rail has to light up what is on screen rather than what was last clicked.
  const effectiveStdView =
    activeStdView ||
    stdView === BESTIARY_VIEW ||
    stdView === HITS_VIEW ||
    stdView === MAPS_VIEW
      ? stdView
      : SUMMARY_VIEW;
  // A selection made on one view is that view's, unless it was pinned:
  // leaving the view — or the character — lets it go (see highlight.tsx).
  const { clearUnlessPinned } = useSelectionActions();
  useEffect(() => {
    clearUnlessPinned();
  }, [view, effectiveStdView, activeId, clearUnlessPinned]);

  const onMap = view === "overview" && effectiveStdView === MAPS_VIEW;
  const railCollapsed = onMap ? (railOnMap ?? true) : railPref;
  function toggleRail() {
    if (onMap) {
      setRailOnMap(!railCollapsed);
      return;
    }
    const next = !railPref;
    setRailPref(next);
    localStorage.setItem("eqdeeps.railCollapsed", next ? "on" : "off");
  }
  useEffect(() => {
    if (!onMap) setRailOnMap(null);
  }, [onMap]);

  // Scrolling needs a live tail AND a log that is still being written; see
  // LIVE_LOG_GRACE_MS.
  const newestRecordMs = fights.length
    ? new Date(fights[fights.length - 1].lastDamageTime).getTime()
    : 0;
  // How much log there is, so a "fit" range can size its buckets against
  // something real instead of asking for a point per second across days.
  const logSpanSeconds = fights.length
    ? Math.max(1, Math.round((newestRecordMs - new Date(fights[0].beginTime).getTime()) / 1000))
    : 0;
  const scrolling =
    liveScroll && isLive(frame) && newestRecordMs > 0 && nowMs - newestRecordMs < LIVE_LOG_GRACE_MS;

  /*
   * How often every panel refetches: once per bucket, backing off in step with
   * how far out you are looking.
   *
   * A chart cannot change faster than its bucket closes. At a 24-hour range
   * that bucket is a minute, so a second of new data moves nothing anyone can
   * see, and refetching nine panels to find that out costs a megabyte. Short
   * ranges bucket at a second and keep refreshing at a second, so live play is
   * exactly as responsive as it ever was.
   *
   * The ceiling is not about the charts — live scrolling advances their
   * viewport every second with no fetch at all, so the view never looks
   * frozen, and the live meter runs straight off the hub tick and is never
   * throttled at all. It is for the tables and tiles, which have no bucket to
   * hide behind and would otherwise sit on five-minute-old totals at the
   * longest ranges.
   */
  const MAX_REFRESH_MS = 30_000;
  const refreshIntervalMs = Math.min(
    MAX_REFRESH_MS,
    Math.max(
      1000,
      queryBucketSeconds(1, frameSpanSeconds(frame, chartDefaults.spanSec, logSpanSeconds)) * 1000,
    ),
  );
  const summaryTrends = useMemo(() => summaryTrendPanels(), []);

  function selectStdView(id: string) {
    setStdView(id);
    setView("overview");
    localStorage.setItem("eqdeeps.stdView", id);
  }

  // ---- the Bestiary ↔ Map trail (see trail.ts) --------------------------
  // The targets are consumed by the view they name and re-fire on `seq`, so
  // the same mob can be asked for twice; the crumbs are the way back. Both
  // are cleared when the rail is used — a trail is one train of thought.
  const [crumbs, setCrumbs] = useState<Crumb[]>([]);
  const [bestiaryTarget, setBestiaryTarget] = useState<BestiaryTarget | null>(null);
  const [mapTarget, setMapTarget] = useState<MapTarget | null>(null);
  const trailSeq = useRef(0);

  function selectFromRail(id: string) {
    setCrumbs([]);
    setBestiaryTarget(null);
    setMapTarget(null);
    selectStdView(id);
  }

  /** From a mob page to the zone it stands in, leaving the mob behind as a crumb. */
  function showOnMap(target: Omit<MapTarget, "seq">, from: Crumb) {
    setCrumbs((c) => [...c, from]);
    setMapTarget({ ...target, seq: ++trailSeq.current });
    selectStdView(MAPS_VIEW);
  }

  /** From a zone to a mob that stands there, leaving the zone behind as a crumb. */
  function openMob(target: Omit<BestiaryTarget, "seq">, from: Crumb) {
    setCrumbs((c) => [...c, from]);
    setBestiaryTarget({ ...target, seq: ++trailSeq.current });
    selectStdView(BESTIARY_VIEW);
  }

  // ---- back / forward over screens (see trail.ts: Screen) ----------------
  // A browser-style history of where you have been: rail views, the mob
  // open in the Bestiary, the zone and mode on the Map. Views report their
  // screen as it settles; a report that matches the current entry is not a
  // move, and one that arrives while a back/forward is being applied is the
  // view settling into it, not a new place. Kept in a ref as well as state
  // so reports and moves read the latest without a render between.
  const [hist, setHist] = useState<{ items: Screen[]; index: number }>({ items: [], index: -1 });
  const histRef = useRef(hist);
  const applying = useRef<Screen | null>(null);
  const applyTimer = useRef<number | undefined>(undefined);

  function reportScreen(s: Screen) {
    const h = histRef.current;
    const cur = h.items[h.index];
    if (cur && screenKey(cur) === screenKey(s)) return;
    if (applying.current) {
      if (screenKey(applying.current) === screenKey(s)) applying.current = null;
      return;
    }
    const items = [...h.items.slice(0, h.index + 1), s].slice(-100);
    histRef.current = { items, index: items.length - 1 };
    setHist(histRef.current);
  }

  const canBack = hist.index > 0;
  const canForward = hist.index < hist.items.length - 1;

  function goHistory(delta: number) {
    const h = histRef.current;
    const next = h.index + delta;
    const target = h.items[next];
    if (!target) return;
    histRef.current = { ...h, index: next };
    setHist(histRef.current);
    applying.current = target;
    // The trail is the hop-and-return affordance; once the history is being
    // walked it is the way back, and a crumb pointing at the screen you are
    // now on would only be noise.
    setCrumbs([]);
    // A view that never settles on exactly this screen — a mob the index no
    // longer has — must not leave every later report ignored.
    window.clearTimeout(applyTimer.current);
    applyTimer.current = window.setTimeout(() => (applying.current = null), 1500);

    setView(target.view);
    if (target.view === "overview") {
      setStdView(target.stdView);
      localStorage.setItem("eqdeeps.stdView", target.stdView);
    }
    if (target.stdView === BESTIARY_VIEW) {
      setBestiaryTarget({ name: target.mob?.name ?? "", id: target.mob?.id, seq: ++trailSeq.current });
    }
    if (target.stdView === MAPS_VIEW && target.zone) {
      setMapTarget({ ...target.zone, seq: ++trailSeq.current });
    }
  }
  const goBack = () => goHistory(-1);
  const goForward = () => goHistory(1);

  // The rail views that carry no place inside them report themselves; the
  // Bestiary and the Map report with their mob or zone from inside.
  useEffect(() => {
    if (view === "overview" && (stdView === BESTIARY_VIEW || stdView === MAPS_VIEW)) return;
    reportScreen({ view, stdView: effectiveStdView });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, effectiveStdView, stdView]);

  // Mouse side buttons and Alt+arrows, as a browser has them. Buttons 3 and
  // 4 are what every mouse with thumb buttons sends; there is no page
  // history for the shell to fight over, so this is the only thing they do.
  useEffect(() => {
    const onMouse = (e: MouseEvent) => {
      if (e.button === 3) {
        e.preventDefault();
        goBack();
      } else if (e.button === 4) {
        e.preventDefault();
        goForward();
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (!e.altKey || e.ctrlKey || e.metaKey) return;
      if (e.key === "ArrowLeft") {
        e.preventDefault();
        goBack();
      } else if (e.key === "ArrowRight") {
        e.preventDefault();
        goForward();
      }
    };
    window.addEventListener("mouseup", onMouse);
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("mouseup", onMouse);
      window.removeEventListener("keydown", onKey);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /** Back along the trail to a crumb, dropping it and everything after it. */
  function backTo(index: number) {
    const crumb = crumbs[index];
    setCrumbs(crumbs.slice(0, index));
    if (crumb.view === "map" && crumb.map) {
      setMapTarget({ ...crumb.map, seq: ++trailSeq.current });
      selectStdView(MAPS_VIEW);
    } else if (crumb.view === "bestiary" && crumb.bestiary) {
      setBestiaryTarget({ ...crumb.bestiary, seq: ++trailSeq.current });
      selectStdView(BESTIARY_VIEW);
    }
  }

  function updateChartDefaults(next: ChartSettings) {
    setChartDefaults(next);
    localStorage.setItem("eqdeeps.chartDefaults", JSON.stringify(next));
    // Changing the span is a statement about the live window, so it also
    // releases a fixed range — otherwise the control would appear to do
    // nothing while a fight was framed.
    if (next.spanSec !== chartDefaults.spanSec) {
      setFrame({ kind: "live", spanSec: next.spanSec });
    }
  }

  /** Frame the fights the list just handed us; an empty pick returns to live. */
  function selectFights(ids: number[]) {
    setFrame(frameFromFights(fights, ids) ?? { kind: "live", spanSec: chartDefaults.spanSec });
  }

  /**
   * Adopt a window a chart was zoomed into as the app's time range. This is
   * the way to frame something that is not a pull — a wipe, a lull, the two
   * minutes either side of a death — without hunting for it in the fight list.
   */
  function adoptRange(beginMs: number, endMs: number) {
    setFrame(frameFromRange(beginMs, endMs));
  }

  /** Release a fixed range without disturbing the window/span settings. */
  function backToLive() {
    setFrame({ kind: "live", spanSec: chartDefaults.spanSec });
  }

  /** One control for "put it back how it started". */
  // The time state only: the fight overlay used to be reset here too, from
  // when it sat in the same header group; it is a preference now.
  function resetToDefaults() {
    setFrame(DEFAULT_FRAME);
    setChartDefaults(DEFAULT_CHART_SETTINGS);
    localStorage.setItem("eqdeeps.chartDefaults", JSON.stringify(DEFAULT_CHART_SETTINGS));
  }

  function updateFightLabelPx(px: number) {
    setFightLabelPx(px);
    localStorage.setItem("eqdeeps.fightLabelPx", String(px));
  }

  function updatePlayedTimeOnly(on: boolean) {
    setPlayedTimeOnly(on);
    localStorage.setItem("eqdeeps.playedTimeOnly", on ? "on" : "off");
  }

  function updateContextMode(mode: ContextMode) {
    setContextMode(mode);
    localStorage.setItem("eqdeeps.contextMode", mode);
  }

  function toggleLiveScroll(on: boolean) {
    setLiveScroll(on);
    localStorage.setItem("eqdeeps.liveScroll", on ? "on" : "off");
    setNowMs(Date.now()); // catch up immediately rather than at the next tick
  }

  function toggleFightsCollapsed() {
    setFightsCollapsed((on) => {
      localStorage.setItem("eqdeeps.fightsCollapsed", on ? "off" : "on");
      return !on;
    });
  }

  // On the root rather than in a context: density is one number that a dozen
  // unrelated rules need, and threading it through props would touch every
  // component to change a padding.
  useEffect(() => {
    document.documentElement.dataset.density = density;
  }, [density]);

  function toggleDensity(compact: boolean) {
    const next = compact ? "compact" : "comfortable";
    setDensity(next);
    localStorage.setItem("eqdeeps.density", next);
  }

  function togglePetRollup(on: boolean) {
    setPetRollup(on);
    localStorage.setItem("eqdeeps.petRollup", on ? "on" : "off");
  }

  // ---- dashboards: load the user's own, save debounced ---------------------
  const saveTimer = useRef<number | undefined>(undefined);
  useEffect(() => {
    api
      .getStore<{ dashboards?: DashboardDef[]; hiddenPresets?: string[] }>("dashboards")
      .then((doc) => {
        // Migration off the provisioned-presets model: the standard views are
        // rendered from code now, so their stored copies (and the hidden list
        // that tracked deleted ones) are dropped on first load.
        const { dashboards: mine, changed } = stripStandardViews(doc?.dashboards ?? []);
        setDashboards(mine);
        if (changed || doc?.hiddenPresets) {
          api.putStore("dashboards", { dashboards: mine }).catch(() => undefined);
        }
      })
      .catch(() => undefined);
  }, []);

  function updateDashboards(next: DashboardDef[]) {
    setDashboards(next);
    window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(() => {
      api.putStore("dashboards", { dashboards: next }).catch(() => undefined);
    }, 800);
  }

  /** "Customize a copy" on a standard view: clone it into the user's own set. */
  function customizeStandardView(id: string) {
    const source = standard.find((d) => d.id === id);
    if (!source) return;
    const copy = cloneForCustomizing(source);
    updateDashboards([...dashboards, copy]);
    setView(copy.id);
  }

  function addDashboard() {
    const name = window.prompt("Dashboard name", `Dashboard ${dashboards.length + 1}`);
    if (!name) return;
    const dashboard = newDashboard(name);
    updateDashboards([...dashboards, dashboard]);
    setView(dashboard.id);
  }

  function renameDashboard(id: string) {
    const current = dashboards.find((d) => d.id === id);
    const name = window.prompt("Rename dashboard", current?.name ?? "");
    if (!name || !current) return;
    updateDashboards(dashboards.map((d) => (d.id === id ? { ...d, name } : d)));
  }

  function deleteDashboard(id: string) {
    const current = dashboards.find((d) => d.id === id);
    if (!current || !window.confirm(`Delete dashboard "${current.name}"?`)) return;
    updateDashboards(dashboards.filter((d) => d.id !== id));
    setView("overview");
  }

  function exportDashboard(id: string) {
    const dashboard = dashboards.find((d) => d.id === id);
    if (!dashboard) return;
    const blob = new Blob([JSON.stringify({ eqdeepsDashboard: dashboard }, null, 2)], {
      type: "application/json",
    });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `${dashboard.name.replace(/[^\w-]+/g, "_")}.eqdeeps.json`;
    a.click();
    URL.revokeObjectURL(a.href);
  }

  function importDashboard(file: File) {
    file.text().then((text) => {
      try {
        const parsed = JSON.parse(text) as { eqdeepsDashboard?: DashboardDef };
        const dashboard = parsed.eqdeepsDashboard;
        if (!dashboard?.panels || !dashboard.name) {
          setError("Not an EQDeeps dashboard file");
          return;
        }
        const imported = { ...dashboard, id: newId("d") };
        updateDashboards([...dashboards, imported]);
        setView(imported.id);
      } catch {
        setError("Not an EQDeeps dashboard file");
      }
    });
  }

  /** "Edit this view" on canned panels: seed a panel in a dashboard (F4/F6). */
  function openInBuilder(seed: ReturnType<typeof defaultPanel>) {
    let target = dashboards.find((d) => d.name === "My panels");
    let next = dashboards;
    if (!target) {
      target = newDashboard("My panels");
      next = [...dashboards, target];
    }
    updateDashboards(
      next.map((d) => (d.id === target!.id ? { ...d, panels: [...d.panels, seed] } : d)),
    );
    setView(target.id);
  }

  const activeIdRef = useRef(activeId);
  activeIdRef.current = activeId;
  // One entity→color registry per session: charts, meter, and table tints all
  // read the same assignment, so "orange" means the same player everywhere.
  const entityColors = useMemo(() => createEntityColors(), [activeId]);

  const live = useMemo(
    () =>
      createLiveConnection({
        onBackfill: (e) => {
          if (e.sessionId === activeIdRef.current) {
            setBackfill(e);
            if (e.complete) {
              refreshFights(e.sessionId);
            }
          }
        },
        onFights: (e) => {
          if (e.sessionId === activeIdRef.current) {
            setFights(e.fights);
            bumpRefreshThrottled();
          }
        },
        // A live frame needs no nudging: its scope is the trailing window of
        // the record stream, so new records move it on the server side.
        onTick: (e) => {
          if (e.sessionId === activeIdRef.current) {
            setTick(e);
            bumpRefreshThrottled();
          }
        },
        onConnectionLost: () =>
          setError("Lost connection to the EQDeeps server — relaunch EQDeeps.Server.exe and refresh this page."),
      }),
    [],
  );

  /**
   * The context strip, on its own slow beat.
   *
   * Zones and levels change minutes apart at best, and building them walks the
   * whole record stream — refetching with the panels every second would make
   * the cheapest thing on screen the most expensive thing on the server. Once
   * on open, once when the backfill lands, and every half minute after that is
   * ample for a band that spans hours.
   */
  useEffect(() => {
    if (!activeId) {
      return;
    }

    let cancelled = false;
    const load = () =>
      api
        .getContext(activeId)
        .then((next) => !cancelled && setContext(next))
        .catch(() => undefined);

    load();
    const timer = window.setInterval(load, 30_000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [activeId, backfill?.complete]);

  const lastRefresh = useRef(0);
  // Read through a ref: the live connection is built once and closes over the
  // first render's function, so the value has to be reachable rather than
  // captured.
  const refreshIntervalRef = useRef(1000);
  refreshIntervalRef.current = refreshIntervalMs;

  function bumpRefreshThrottled() {
    const now = Date.now();
    if (now - lastRefresh.current > refreshIntervalRef.current) {
      lastRefresh.current = now;
      setRefreshKey((k) => k + 1);
    }
  }

  useEffect(() => {
    live.start().catch((e) => setError(String(e)));
    api
      .listSessions()
      .then((list) => {
        setSessions(list);
        if (list.length > 0) {
          activate(list[0].id);
        }
      })
      .catch((e) => setError(String(e)));
    refreshDiscovered();
    // The server checks for updates on its own schedule and drives download
    // progress; polling keeps the pill and the consent prompt in step with it.
    const pollUpdate = () => api.getUpdateState().then(setUpdate).catch(() => undefined);
    pollUpdate();
    const updateTimer = window.setInterval(pollUpdate, IDLE_POLL_MS);
    return () => window.clearInterval(updateTimer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // One clock for every scrolling chart, ticking only while the feature is on
  // and the frame is a live tail — a fixed range has nothing to scroll toward.
  useEffect(() => {
    if (!liveScroll || !isLive(frame)) return;
    const timer = window.setInterval(() => setNowMs(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [liveScroll, frame]);

  // The mob index grows as things die, and the server banks kills on its own
  // beat rather than pushing them. Polling only while the tab is open is
  // enough: nobody watches a health estimate tick, and off the tab there is
  // nothing to keep current. The fetch on open covers the common case where a
  // player looks once.
  useEffect(() => {
    // The Bestiary reads it for its "what you measured" column, and the Map
    // for "mobs you have killed here".
    if (!activeId || view !== "overview" || (stdView !== BESTIARY_VIEW && stdView !== MAPS_VIEW))
      return;
    let cancelled = false;
    const load = () =>
      api
        .getMobs(activeId)
        .then((report) => !cancelled && setMobs(report))
        .catch(() => undefined);
    load();
    const timer = window.setInterval(load, 15000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [activeId, view, stdView]);

  // Attack profiles are banked on the same server-side tick as kills, and read
  // the same way: poll while the tab is open, and not at all off it. The raw
  // feed beside them refreshes on its own faster beat inside the panel — the
  // profiles are an evening's evidence and do not move in fifteen seconds.
  // The Bestiary reads them too, for what a listed mob actually hit you for.
  useEffect(() => {
    if (!activeId || view !== "overview" || (stdView !== HITS_VIEW && stdView !== BESTIARY_VIEW)) return;
    let cancelled = false;
    const load = () =>
      api
        .getAttacks(activeId)
        .then((report) => !cancelled && setAttacks(report))
        .catch(() => undefined);
    load();
    const timer = window.setInterval(load, 15000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [activeId, view, stdView]);

  // While something is actually happening, poll fast enough for the progress
  // bar to move. At the idle interval a download finishes between two polls,
  // so the update appears to do nothing and then be done.
  useEffect(() => {
    const busy = update?.stage === "downloading" || update?.stage === "checking";
    if (!busy) return;
    const timer = window.setInterval(
      () => api.getUpdateState().then(setUpdate).catch(() => undefined),
      ACTIVE_POLL_MS,
    );
    return () => window.clearInterval(timer);
  }, [update?.stage]);

  // Watch for the log's first stance switch, which is what reveals the Stances
  // tab. It can arrive at any point — a backfill still reading, or the player
  // pressing the hotkey right now — so this rides the ordinary refresh beat
  // and then stops for good: once a log has stances it cannot stop having them.
  useEffect(() => {
    if (!activeId || hasStances) return;
    let cancelled = false;
    api
      .getSession(activeId)
      .then((info) => {
        if (!cancelled) {
          setSessions((list) => list.map((s) => (s.id === info.id ? info : s)));
        }
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [activeId, hasStances, refreshKey]);

  function refreshDiscovered() {
    api
      .discoverLogs()
      .then(setDiscovered)
      .catch(() => setDiscovered([]));
  }

  async function refreshFights(id: string) {
    try {
      const list = await api.getFights(id);
      setFights(list);
      setRefreshKey((k) => k + 1);
    } catch (e) {
      setError(String(e));
    }
  }

  async function activate(id: string) {
    setActiveId(id);
    setTick(null);
    setBackfill(null);
    setMobs(null);
    setAttacks(null);
    setContext(null);
    setCrumbs([]); // another character's world; the trail was this one's
    // The history is deliberately kept: the screens are places in the app,
    // not in the log, and the first log opens on start-up — resetting here
    // threw away the screen the app opened on.
    setFrame({ kind: "live", spanSec: chartDefaults.spanSec }); // a new log starts live
    await live.subscribe(id);
    await refreshFights(id);
    api.getMobs(id).then(setMobs).catch(() => undefined);
  }

  async function openLog(path: string) {
    try {
      setError(null);
      const info = await api.openSession(path);
      setSessions(await api.listSessions());
      await activate(info.id);
    } catch (e) {
      setError(String(e));
    }
  }

  // Remove a log from the recently-opened list. The row disappears at once —
  // waiting on a round trip for a delete the user just asked for reads as a
  // stall — and comes back if the server refuses.
  async function forgetLog(path: string) {
    setDiscovered((list) => list.filter((d) => d.path !== path));
    try {
      await api.forgetRecentLog(path);
    } catch (e) {
      setError(String(e));
      refreshDiscovered();
    }
  }

  async function closeSession(id: string) {
    await api.closeSession(id);
    await live.unsubscribe(id);
    const list = await api.listSessions();
    setSessions(list);
    if (activeId === id) {
      setActiveId(null);
      setFights([]);
      setMobs(null);
      setAttacks(null);
      setFrame(DEFAULT_FRAME);
      setTick(null);
      if (list.length > 0) {
        await activate(list[0].id);
      }
    }
  }

  // The server decides whether to ask, applying the user's standing answers
  // (F22); the SPA renders the question when it says so — but not in the
  // middle of a fight. A prompt found by the background check waits for the
  // active session to go quiet (no fight open, nothing hit for a couple of
  // minutes); one the user asked for, by clicking "check for updates", shows
  // at once, since that click was the request. Re-checked on every fight
  // push and on a slow timer, so the wait ends when the fighting does.
  const manualCheck = useRef(false);
  useEffect(() => {
    if (!update?.promptRequired || showUpdateNotice) return;
    if (manualCheck.current) {
      manualCheck.current = false;
      setShowUpdateNotice(true);
      return;
    }
    if (combatQuiet(fights, Date.now())) {
      setShowUpdateNotice(true);
      return;
    }
    const timer = window.setInterval(() => {
      if (combatQuiet(fights, Date.now())) setShowUpdateNotice(true);
    }, UPDATE_PROMPT_RECHECK_MS);
    return () => window.clearInterval(timer);
  }, [update, fights, showUpdateNotice]);

  async function answerUpdate(choice: UpdateChoice) {
    setShowUpdateNotice(false);
    try {
      if (choice.kind === "defer") {
        setUpdate(await api.deferUpdate(choice.scope));
        return;
      }

      // "Always" first, so a failure partway still leaves the standing
      // preference the user just expressed.
      if (choice.always) {
        setUpdate(await api.setUpdateMode("auto"));
      }

      setUpdate(await api.stageUpdate(choice.now));
    } catch {
      // A failed consent call is not worth a red banner over the app; the
      // next poll re-reads the real state from the server.
    }
  }

  async function applyUpdateNow() {
    try {
      await api.applyUpdate();
    } catch {
      // The app is exiting underneath us — a rejected fetch here is expected.
    }
  }

  async function setUpdateMode(mode: UpdateMode) {
    try {
      setUpdate(await api.setUpdateMode(mode));
    } catch {
      // Next poll re-reads the authoritative state.
    }
  }

  // An explicit check clears every standing decline server-side, so this is
  // also the way back for someone who chose "don't ask again".
  async function checkForUpdateNow() {
    setCheckNote(null);
    // The user asked, so the answer does not wait for a quiet moment.
    manualCheck.current = true;
    try {
      const next = await api.checkForUpdate();
      setUpdate(next);
      // Silence would read as a broken button, so say something either way.
      // When there IS an update the consent dialog opens on its own.
      if (!next.promptRequired) {
        manualCheck.current = false;
        setCheckNote(next.restartRequired ? "update ready" : "up to date");
      }
    } catch {
      manualCheck.current = false;
      setCheckNote("check failed");
    }
  }

  // Clear the transient check result a few seconds after it appears.
  useEffect(() => {
    if (!checkNote) return;
    const timer = window.setTimeout(() => setCheckNote(null), 4000);
    return () => window.clearTimeout(timer);
  }, [checkNote]);

  // Built into a variable so the provider can wrap it without re-indenting
  // three hundred lines: the install decides which reference sites every
  // lookup door in the tree offers, and it is known here and nowhere below.
  const tree = (
    <div className="app">
      {showUpdateNotice && update?.latestVersion && (
        <UpdateNotice state={update} onChoice={answerUpdate} />
      )}
      {showSettings && (
        <SettingsDialog
          onClose={() => setShowSettings(false)}
          density={density}
          onDensity={toggleDensity}
          petRollup={petRollup}
          onPetRollup={togglePetRollup}
          fightLabelPx={fightLabelPx}
          onFightLabelPx={updateFightLabelPx}
          contextMode={contextMode}
          onContextMode={updateContextMode}
          playedTimeOnly={playedTimeOnly}
          onPlayedTimeOnly={updatePlayedTimeOnly}
          liveScroll={liveScroll}
          onLiveScroll={toggleLiveScroll}
          update={update}
          onSetUpdateMode={setUpdateMode}
          onCheckForUpdate={checkForUpdateNow}
          checkNote={checkNote}
        />
      )}
      {showLogs && (
        <LogsDialog
          onClose={() => setShowLogs(false)}
          onRescan={refreshDiscovered}
          discovered={discovered}
          sessions={sessions}
          onOpen={(path) => {
            setShowLogs(false);
            openLog(path);
          }}
          onActivate={(id) => {
            setShowLogs(false);
            activate(id);
          }}
          onForget={forgetLog}
        />
      )}
      <SessionBar
        sessions={sessions}
        history={{ canBack, canForward, onBack: goBack, onForward: goForward }}
        colorFor={(key, pool) => entityColors.claim(key, pool)}
        activeId={activeId}
        backfill={backfill}
        discovered={discovered}
        update={update}
        onShowUpdatePrompt={() => setShowUpdateNotice(true)}
        onApplyUpdate={applyUpdateNow}
        chartDefaults={chartDefaults}
        onChartDefaults={updateChartDefaults}
        frame={frame}
        fights={fights}
        onResetDefaults={resetToDefaults}
        framed={Boolean(activeId) && (view !== "overview" || isFramedView(effectiveStdView))}
        onAbsoluteRange={adoptRange}
        onOpenLogs={() => setShowLogs(true)}
        onActivate={activate}
        onClose={closeSession}
        error={error}
      />
      {activeId ? (
        <>
          {(() => {
            // Every panel on this screen shares one context; building it once
            // keeps the three call sites from drifting apart.
            const panelCtx: PanelContext = {
              sessionId: activeId,
              character,
              frame,
              fights,
              fightLabelPx,
              context,
              install: sessions.find((s) => s.id === activeId)?.install,
              contextMode,
              playedTimeOnly,
              refreshKey,
              petRollup,
              colors: entityColors,
              // null when there is nothing to scroll toward, so every chart
              // makes the same call without repeating the condition.
              scrollNowMs: scrolling ? nowMs : null,
              onAdoptRange: adoptRange,
              logSpanSeconds,
            };
            // The fight list scopes a parse, so it shows where the time frame
            // applies — which the view's rail group decides (ADR-017). The
            // World views read a server-wide index or a folder on disk, and a
            // pane whose every click changed nothing would be furniture.
            const showFights = view !== "overview" || isFramedView(effectiveStdView);
            return (
          <main
            className={
              "dashboard" +
              (!showFights ? " fights-hidden" : fightsCollapsed ? " fights-collapsed" : "") +
              (railCollapsed ? " rail-collapsed" : "")
            }
          >
            <NavRail
              standard={standard}
              dashboards={dashboards}
              view={view}
              activeStdView={effectiveStdView}
              onSelectStdView={selectFromRail}
              onSelectDashboard={setView}
              onRenameDashboard={renameDashboard}
              onAddDashboard={addDashboard}
              onExportDashboard={exportDashboard}
              onImportDashboard={importDashboard}
              onDeleteDashboard={deleteDashboard}
              onOpenLogs={() => setShowLogs(true)}
              onOpenSettings={() => setShowSettings(true)}
              update={update}
              onCheckForUpdate={checkForUpdateNow}
              checkNote={checkNote}
              collapsed={railCollapsed}
              onToggleCollapsed={toggleRail}
            />
            {/* Three cases: a standard view, the hand-built Summary that
                Overview opens on, or one of the user's own dashboards. The
                Bestiary, Incoming and Map are checked first — they are rail
                entries but not dashboards, so the standard-view lookup
                resolves them to nothing. */}
            {view === "overview" && stdView === BESTIARY_VIEW ? (
              <div className="trail-host">
                <Trail crumbs={crumbs} onBack={backTo} />
                <BestiaryPanel
                  sessionId={activeId}
                  mobs={mobs}
                  attacks={attacks}
                  enabled={referenceEnabled}
                  target={bestiaryTarget}
                  onShowOnMap={showOnMap}
                  onScreen={(mob) => reportScreen({ view: "overview", stdView: BESTIARY_VIEW, mob })}
                />
              </div>
            ) : view === "overview" && stdView === HITS_VIEW ? (
              <IncomingPanel
                attacks={attacks}
                sessionId={activeId}
                frame={frame}
                server={sessions.find((s) => s.id === activeId)?.server ?? ""}
              />
            ) : view === "overview" && stdView === MAPS_VIEW ? (
              // The last zone the log named is where the character is now.
              // `hasLog` says one is coming: the zone timeline is built after
              // the backfill, so without it the Map view cannot tell "nobody is
              // playing" from "wait a moment" and settles on an unrelated zone.
              <div className="trail-host">
                <Trail crumbs={crumbs} onBack={backTo} />
                <MapView
                  currentZone={context?.zones?.[context.zones.length - 1]?.label}
                  currentLevel={levelOf(context?.levels?.[context.levels.length - 1]?.label)}
                  install={sessions.find((s) => s.id === activeId)?.install}
                  hasLog={Boolean(activeId)}
                  mobs={mobs}
                  referenceEnabled={referenceEnabled}
                  target={mapTarget}
                  onOpenMob={openMob}
                  onScreen={(zone) => reportScreen({ view: "overview", stdView: MAPS_VIEW, zone })}
                />
              </div>
            ) : view === "overview" && activeStdView ? (
              <DashboardView
                dashboard={activeStdView}
                ctx={panelCtx}
                chartDefaults={chartDefaults}
                onChange={() => undefined}
                readOnly
                onCustomize={() => customizeStandardView(activeStdView.id)}
              />
            ) : view === "overview" ? (
              <div className="dashboard-main">
                <SelectionStats
                  sessionId={activeId}
                  character={character}
                  frame={frame}
                  fightCount={fightsInFrame(frame, fights).length}
                  refreshKey={refreshKey}
                  petRollup={petRollup}
                />
                {/* Charts own the wide column and stack; tables live in a
                    narrow rail. Trends are time-series, so width is
                    resolution — and a rail keeps a one-row damage table from
                    claiming half the page the way an equal-height row did. */}
                <div className="summary-body">
                  <div className="summary-charts">
                    <DpsChart
                      sessionId={activeId}
                      frame={frame}
                      fights={fights}
                      fightLabelPx={fightLabelPx}
                      context={context}
                      contextMode={contextMode}
                      refreshKey={refreshKey}
                      petRollup={petRollup}
                      colors={entityColors}
                      chartDefaults={chartDefaults}
                      scrollNowMs={panelCtx.scrollNowMs}
                      onAdoptRange={adoptRange}
                      logSpanSeconds={logSpanSeconds}
                    />
                    {/* Healing and damage taken abreast, under the DPS chart:
                        output, upkeep and what came back, all on one axis. */}
                    <div className="summary-pair">
                      {summaryTrends.map((p) => (
                        <div key={p.id} className="panel chart-panel">
                          <div className="panel-title">
                            <span className="panel-name">{p.title}</span>
                            <span className="panel-controls">
                              <SelectionChip
                                colorFor={(k, pl) => entityColors.claim(k, pl)}
                                compact
                              />
                            </span>
                          </div>
                          <PanelBody panel={p} ctx={panelCtx} settings={chartDefaults} />
                        </div>
                      ))}
                    </div>
                    <TimelineChart
                      sessionId={activeId}
                      frame={frame}
                      refreshKey={refreshKey}
                      character={character}
                      fights={fights}
                      onAdoptRange={adoptRange}
                    />
                  </div>
                  <div className="summary-rail">
                    <SummaryTable
                      sessionId={activeId}
                      frame={frame}
                      refreshKey={refreshKey}
                      excludeDamageShields={excludeDs}
                      onToggleDamageShields={setExcludeDs}
                      petRollup={petRollup}
                      onOpenInBuilder={openInBuilder}
                      colors={entityColors}
                      character={character}
                    />
                    <AbilityChart
                      sessionId={activeId}
                      frame={frame}
                      refreshKey={refreshKey}
                      petRollup={petRollup}
                      colors={entityColors}
                    />
                    <LiveMeter tick={tick} colorFor={entityColors.claim} petRollup={petRollup} />
                    <DeathLog sessionId={activeId} frame={frame} refreshKey={refreshKey} />
                  </div>
                </div>
              </div>
            ) : (
              (() => {
                const dashboard = dashboards.find((d) => d.id === view);
                return dashboard ? (
                  <DashboardView
                    dashboard={dashboard}
                    ctx={panelCtx}
                    chartDefaults={chartDefaults}
                    onChange={(next) =>
                      updateDashboards(dashboards.map((d) => (d.id === next.id ? next : d)))
                    }
                  />
                ) : (
                  <div className="empty">Dashboard not found</div>
                );
              })()
            )}
            {showFights && (
              <FightList
                fights={fights}
                selected={framedFightIds(frame)}
                live={isLive(frame)}
                onSelect={selectFights}
                onReset={backToLive}
                collapsed={fightsCollapsed}
                onToggleCollapsed={toggleFightsCollapsed}
              />
            )}
          </main>
            );
          })()}
        </>
      ) : (
        <main className="welcome">
          <h1>No log open</h1>
          <p className="subtle">
            Click a log to start. Historical fights load at once; while the game runs, everything
            updates live. Logging must be on in game (<code>/log</code>).
          </p>
          <div className="welcome-actions">
            <button className="mini-btn" onClick={refreshDiscovered} title="Re-scan for log files">
              ↻ rescan
            </button>
          </div>
          <LogPicker
            discovered={discovered}
            sessions={sessions}
            onOpen={openLog}
            onActivate={activate}
            onForget={forgetLog}
          />
        </main>
      )}
    </div>
  );
  return (
    <LookupScope install={activeSession?.install} sessionId={activeId || undefined}>
      {tree}
      <LookupMenuHost />
    </LookupScope>
  );
}

/** A level span's label ("42") as a number, or undefined for none — the log's last word on the character's level. */
function levelOf(label: string | undefined): number | undefined {
  const n = label === undefined ? NaN : Number(label);
  return Number.isFinite(n) ? n : undefined;
}
