import { useEffect, useMemo, useRef, useState } from "react";
import {
  api,
  type DiscoveredLog,
  type FightInfo,
  type SessionInfo,
  type UpdateMode,
  type UpdateState,
} from "./api";
import { createLiveConnection, type BackfillEvent, type TickEvent } from "./live";
import { describeAge, SessionBar } from "./components/SessionBar";
import { UpdateNotice, type UpdateChoice } from "./components/UpdateNotice";
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
import { defaultPanel, newDashboard, newId, type DashboardDef } from "./dashboards/model";
import {
  SUMMARY_VIEW,
  cloneForCustomizing,
  standardViews,
  stripStandardViews,
  summaryTrendPanels,
} from "./dashboards/standardViews";
import { PanelBody, type PanelContext } from "./dashboards/PanelBody";
import { DEFAULT_CHART_SETTINGS, type ChartSettings } from "./timeControls";
import { DEFAULT_LABEL_PX } from "./fightOverlay";
import {
  DEFAULT_FRAME,
  frameFromFights,
  framedFightIds,
  fightsInFrame,
  isLive,
  type TimeFrame,
} from "./timeFrame";

/** Update polling: rare when nothing is happening, brisk while it is. */
const IDLE_POLL_MS = 15_000;
const ACTIVE_POLL_MS = 700;

/**
 * The default dashboard (feature F7): fight list + summary + DPS chart + live
 * meter + deaths, all scoped to the fight selection, live-updating via the hub.
 */
export default function App() {
  const [sessions, setSessions] = useState<SessionInfo[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [fights, setFights] = useState<FightInfo[]>([]);
  // The one time frame the whole app reports over. A live tail by default;
  // picking fights turns it into the fixed range they span. There is no
  // separate "follow live" flag — a live frame *is* following.
  const [frame, setFrame] = useState<TimeFrame>(DEFAULT_FRAME);
  const [tick, setTick] = useState<TickEvent | null>(null);
  const [backfill, setBackfill] = useState<BackfillEvent | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [excludeDs, setExcludeDs] = useState(false);
  const [petRollup, setPetRollup] = useState(() => localStorage.getItem("eqdeeps.petRollup") !== "off");
  // Window/span for every chart in the app. One value, one place — panels have
  // no window/span of their own to disagree with it.
  const [chartDefaults, setChartDefaults] = useState<ChartSettings>(() => {
    try {
      const stored = localStorage.getItem("eqdeeps.chartDefaults");
      return stored ? { ...DEFAULT_CHART_SETTINGS, ...JSON.parse(stored) } : DEFAULT_CHART_SETTINGS;
    } catch {
      return DEFAULT_CHART_SETTINGS;
    }
  });
  // Size of the mob names on the fight bands; 0 hides them. App-wide for the
  // same reason window and span are: it is how you read a chart, not a
  // property of any one of them.
  const [fightLabelPx, setFightLabelPx] = useState<number>(() => {
    const stored = Number(localStorage.getItem("eqdeeps.fightLabelPx"));
    return Number.isFinite(stored) && stored >= 0 ? stored : DEFAULT_LABEL_PX;
  });
  const [discovered, setDiscovered] = useState<DiscoveredLog[]>([]);
  const [update, setUpdate] = useState<UpdateState | null>(null);
  const [showUpdateNotice, setShowUpdateNotice] = useState(false);
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
  const standard = useMemo(() => standardViews(), []);
  const summaryTrends = useMemo(() => summaryTrendPanels(), []);

  function selectStdView(id: string) {
    setStdView(id);
    setView("overview");
    localStorage.setItem("eqdeeps.stdView", id);
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

  /** Release a fixed range without disturbing the window/span settings. */
  function backToLive() {
    setFrame({ kind: "live", spanSec: chartDefaults.spanSec });
  }

  /** One control for "put it back how it started". */
  function resetToDefaults() {
    setFrame(DEFAULT_FRAME);
    setChartDefaults(DEFAULT_CHART_SETTINGS);
    localStorage.setItem("eqdeeps.chartDefaults", JSON.stringify(DEFAULT_CHART_SETTINGS));
    updateFightLabelPx(DEFAULT_LABEL_PX);
  }

  function updateFightLabelPx(px: number) {
    setFightLabelPx(px);
    localStorage.setItem("eqdeeps.fightLabelPx", String(px));
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

  const lastRefresh = useRef(0);
  function bumpRefreshThrottled() {
    const now = Date.now();
    if (now - lastRefresh.current > 1000) {
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

  function refreshDiscovered() {
    api
      .discoverLogs()
      .then(setDiscovered)
      .catch(() => setDiscovered([]));
  }

  // The bundled demo log is listed by the server with source "sample"; it gets
  // its own affordances everywhere so it never reads as one of the player's logs.
  const sampleLog = discovered.find((d) => d.source === "sample");
  const realLogs = discovered.filter((d) => d.source !== "sample");

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
    setFrame({ kind: "live", spanSec: chartDefaults.spanSec }); // a new log starts live
    await live.subscribe(id);
    await refreshFights(id);
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
      setFrame(DEFAULT_FRAME);
      setTick(null);
      if (list.length > 0) {
        await activate(list[0].id);
      }
    }
  }

  // The server decides whether to ask, applying the user's standing answers
  // (F22); the SPA just renders the question when it says so.
  useEffect(() => {
    if (update?.promptRequired) {
      setShowUpdateNotice(true);
    }
  }, [update]);

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
    try {
      const next = await api.checkForUpdate();
      setUpdate(next);
      // Silence would read as a broken button, so say something either way.
      // When there IS an update the consent dialog opens on its own.
      if (!next.promptRequired) {
        setCheckNote(next.restartRequired ? "update ready" : "up to date");
      }
    } catch {
      setCheckNote("check failed");
    }
  }

  // Clear the transient check result a few seconds after it appears.
  useEffect(() => {
    if (!checkNote) return;
    const timer = window.setTimeout(() => setCheckNote(null), 4000);
    return () => window.clearTimeout(timer);
  }, [checkNote]);

  return (
    <div className="app">
      {showUpdateNotice && update?.latestVersion && (
        <UpdateNotice state={update} onChoice={answerUpdate} />
      )}
      <SessionBar
        sessions={sessions}
        activeId={activeId}
        backfill={backfill}
        discovered={discovered}
        update={update}
        onShowUpdatePrompt={() => setShowUpdateNotice(true)}
        onApplyUpdate={applyUpdateNow}
        onSetUpdateMode={setUpdateMode}
        onCheckForUpdate={checkForUpdateNow}
        checkNote={checkNote}
        petRollup={petRollup}
        onTogglePetRollup={togglePetRollup}
        chartDefaults={chartDefaults}
        onChartDefaults={updateChartDefaults}
        frame={frame}
        fights={fights}
        onResetDefaults={resetToDefaults}
        fightLabelPx={fightLabelPx}
        onFightLabelPx={updateFightLabelPx}
        onOpen={openLog}
        onRefreshDiscovered={refreshDiscovered}
        onActivate={activate}
        onClose={closeSession}
        error={error}
      />
      {activeId ? (
        <>
          {/* Two levels, because there are two kinds of thing here. Overview is
              a section of standard views that ship with the app; everything to
              the right of the divider is a dashboard the user built and owns. */}
          <nav className="view-tabs">
            <button
              className={"view-tab" + (view === "overview" ? " on" : "")}
              onClick={() => setView("overview")}
            >
              Overview
            </button>
            {dashboards.length > 0 && <span className="view-tab-divider" />}
            {dashboards.map((d) => (
              <button
                key={d.id}
                className={"view-tab" + (view === d.id ? " on" : "")}
                onClick={() => setView(d.id)}
                onDoubleClick={() => renameDashboard(d.id)}
                title="Double-click to rename"
              >
                {d.name}
              </button>
            ))}
            <button className="view-tab add" onClick={addDashboard} title="New dashboard">
              +
            </button>
            {view !== "overview" && (
              <span className="view-tab-actions">
                <button className="mini-btn" onClick={() => exportDashboard(view)}>
                  export
                </button>
                <label className="mini-btn" title="Import a dashboard JSON">
                  import
                  <input
                    type="file"
                    accept=".json"
                    style={{ display: "none" }}
                    onChange={(e) => {
                      const file = e.target.files?.[0];
                      if (file) importDashboard(file);
                      e.target.value = "";
                    }}
                  />
                </label>
                <button className="mini-btn" onClick={() => deleteDashboard(view)}>
                  delete
                </button>
              </span>
            )}
          </nav>
          {view === "overview" && (
            <nav className="sub-tabs">
              <button
                className={"sub-tab" + (stdView === SUMMARY_VIEW ? " on" : "")}
                onClick={() => selectStdView(SUMMARY_VIEW)}
              >
                Summary
              </button>
              {standard.map((d) => (
                <button
                  key={d.id}
                  className={"sub-tab" + (stdView === d.id ? " on" : "")}
                  onClick={() => selectStdView(d.id)}
                >
                  {d.name}
                </button>
              ))}
            </nav>
          )}
          {(() => {
            // Every panel on this screen shares one context; building it once
            // keeps the three call sites from drifting apart.
            const panelCtx: PanelContext = {
              sessionId: activeId,
              frame,
              fights,
              fightLabelPx,
              refreshKey,
              petRollup,
              colors: entityColors,
            };
            return (
          <main className="dashboard">
            <FightList
              fights={fights}
              selected={framedFightIds(frame)}
              live={isLive(frame)}
              onSelect={selectFights}
              onReset={backToLive}
            />
            {/* Three cases: a standard view, the hand-built Summary that
                Overview opens on, or one of the user's own dashboards. */}
            {view === "overview" && stdView !== SUMMARY_VIEW ? (
              (() => {
                const std = standard.find((d) => d.id === stdView);
                return std ? (
                  <DashboardView
                    dashboard={std}
                    ctx={panelCtx}
                    chartDefaults={chartDefaults}
                    onChange={() => undefined}
                    readOnly
                    onCustomize={() => customizeStandardView(std.id)}
                  />
                ) : (
                  <div className="empty">View not found</div>
                );
              })()
            ) : view === "overview" ? (
              <div className="dashboard-main">
                <SelectionStats
                  sessionId={activeId}
                  character={sessions.find((s) => s.id === activeId)?.character ?? ""}
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
                      refreshKey={refreshKey}
                      petRollup={petRollup}
                      colors={entityColors}
                      chartDefaults={chartDefaults}
                    />
                    <AbilityChart
                      sessionId={activeId}
                      frame={frame}
                      refreshKey={refreshKey}
                      petRollup={petRollup}
                      colors={entityColors}
                    />
                    {summaryTrends.map((p) => (
                      <div key={p.id} className="panel chart-panel">
                        <div className="panel-title">
                          <span className="panel-name">{p.title}</span>
                        </div>
                        <PanelBody
                          panel={p}
                          ctx={panelCtx}
                          settings={chartDefaults}
                        />
                      </div>
                    ))}
                    <TimelineChart
                      sessionId={activeId}
                      frame={frame}
                      refreshKey={refreshKey}
                      character={sessions.find((s) => s.id === activeId)?.character ?? ""}
                      fights={fights}
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
          </main>
            );
          })()}
        </>
      ) : (
        <main className="welcome">
          <h1>No log open</h1>
          {realLogs.length > 0 ? (
            <>
              <p>Recent and detected EverQuest logs — click one to start:</p>
              <div className="discovered-list">
                {realLogs.map((d) => (
                  <div key={d.path} className="discovered-item">
                    <button className="discovered-row" onClick={() => openLog(d.path)}>
                      <span className="discovered-name">
                        {d.character} <span className="subtle">@{d.server}</span>
                      </span>
                      <span className="discovered-meta">
                        last written {describeAge(d.lastWriteTime)} · {(d.sizeBytes / 1048576).toFixed(1)} MB ·{" "}
                        {d.source}
                      </span>
                      <span className="discovered-path">{d.path}</span>
                    </button>
                    {/* Only "recent" rows can be forgotten: the others come from
                        scanning the install, so they would reappear immediately. */}
                    {d.source === "recent" && (
                      <button
                        className="discovered-forget"
                        title="Remove from this list (the log file is not deleted)"
                        aria-label={`Remove ${d.character} from recent logs`}
                        onClick={() => forgetLog(d.path)}
                      >
                        ✕
                      </button>
                    )}
                  </div>
                ))}
              </div>
              <p className="subtle">
                Logging must be on in game (<code>/log</code>). You can also paste any log path above.
                Logs marked <em>recent</em> can be removed from this list with ✕.
              </p>
            </>
          ) : (
            <p>
              Open your EverQuest log file above (for example{" "}
              <code>C:\EverQuest\Logs\eqlog_Yourname_server.txt</code>). Historical fights load
              immediately; while the game runs, everything updates live. If EverQuest is running,
              press ↻ to re-scan for its log files.
            </p>
          )}
          {sampleLog && (
            <div className="sample-callout">
              <p className="subtle">
                {realLogs.length > 0
                  ? "Or just look around first:"
                  : "No log handy? Look around with demo data:"}
              </p>
              <button
                className="discovered-row sample-row"
                onClick={() => openLog(sampleLog.path)}
              >
                <span className="discovered-name">
                  <span className="sample-badge">sample</span> {sampleLog.character}{" "}
                  <span className="subtle">@{sampleLog.server}</span> — not your data
                </span>
                <span className="discovered-meta">
                  two days of real gameplay bundled with EQDeeps · {(sampleLog.sizeBytes / 1048576).toFixed(1)} MB
                </span>
              </button>
            </div>
          )}
        </main>
      )}
    </div>
  );
}
