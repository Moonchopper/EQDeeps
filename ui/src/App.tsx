import { useEffect, useMemo, useRef, useState } from "react";
import { api, type DiscoveredLog, type FightInfo, type SessionInfo, type VersionInfo } from "./api";
import { createLiveConnection, type BackfillEvent, type TickEvent } from "./live";
import { describeAge, SessionBar } from "./components/SessionBar";
import { markAnnounced, shouldAnnounce, UpdateNotice } from "./components/UpdateNotice";
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
import { PRESET_IDS, presetDashboards, reconcilePresets } from "./dashboards/presets";

/**
 * The default dashboard (feature F7): fight list + summary + DPS chart + live
 * meter + deaths, all scoped to the fight selection, live-updating via the hub.
 */
export default function App() {
  const [sessions, setSessions] = useState<SessionInfo[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [fights, setFights] = useState<FightInfo[]>([]);
  const [selected, setSelected] = useState<number[]>([]);
  const [followLive, setFollowLive] = useState(true);
  const [tick, setTick] = useState<TickEvent | null>(null);
  const [backfill, setBackfill] = useState<BackfillEvent | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [excludeDs, setExcludeDs] = useState(false);
  const [petRollup, setPetRollup] = useState(() => localStorage.getItem("eqdeeps.petRollup") !== "off");
  const [discovered, setDiscovered] = useState<DiscoveredLog[]>([]);
  const [version, setVersion] = useState<VersionInfo | null>(null);
  const [showUpdateNotice, setShowUpdateNotice] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dashboards, setDashboards] = useState<DashboardDef[]>([]);
  const [hiddenPresets, setHiddenPresets] = useState<string[]>([]);
  const [view, setView] = useState<string>("overview"); // "overview" | dashboard id

  function togglePetRollup(on: boolean) {
    setPetRollup(on);
    localStorage.setItem("eqdeeps.petRollup", on ? "on" : "off");
  }

  // ---- dashboards: load + reconcile presets once, save debounced -----------
  const saveTimer = useRef<number | undefined>(undefined);
  useEffect(() => {
    api
      .getStore<{ dashboards?: DashboardDef[]; hiddenPresets?: string[] }>("dashboards")
      .then((doc) => {
        const hidden = doc?.hiddenPresets ?? [];
        const { dashboards: reconciled, changed } = reconcilePresets(doc?.dashboards ?? [], hidden);
        setHiddenPresets(hidden);
        setDashboards(reconciled);
        if (changed) {
          api.putStore("dashboards", { dashboards: reconciled, hiddenPresets: hidden })
            .catch(() => undefined);
        }
      })
      .catch(() => undefined);
  }, []);

  /** Reset the built-in dashboards to pristine and unhide any deleted ones. Idempotent. */
  function restorePresets() {
    const pristine = presetDashboards();
    const withoutPresets = dashboards.filter((d) => !PRESET_IDS.has(d.id));
    updateDashboards([...pristine, ...withoutPresets], []);
  }

  function updateDashboards(next: DashboardDef[], nextHidden: string[] = hiddenPresets) {
    setDashboards(next);
    setHiddenPresets(nextHidden);
    window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(() => {
      api.putStore("dashboards", { dashboards: next, hiddenPresets: nextHidden })
        .catch(() => undefined);
    }, 800);
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
    // A deleted preset is remembered as hidden so it stays deleted across
    // restarts instead of being re-provisioned.
    const nextHidden = PRESET_IDS.has(id) ? [...hiddenPresets, id] : hiddenPresets;
    updateDashboards(dashboards.filter((d) => d.id !== id), nextHidden);
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
  const followRef = useRef(followLive);
  followRef.current = followLive;
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
        onTick: (e) => {
          if (e.sessionId === activeIdRef.current) {
            setTick(e);
            if (followRef.current) {
              setSelected((prev) => {
                const same =
                  prev.length === e.fightIds.length && prev.every((id, i) => id === e.fightIds[i]);
                return same ? prev : e.fightIds;
              });
            }
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
    // Update-check results land a moment after startup; poll twice.
    api.getVersion().then(setVersion).catch(() => undefined);
    const versionTimer = window.setTimeout(
      () => api.getVersion().then(setVersion).catch(() => undefined),
      15_000,
    );
    return () => window.clearTimeout(versionTimer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

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
      if (followRef.current && list.length > 0) {
        setSelected([list[list.length - 1].id]);
      }
      setRefreshKey((k) => k + 1);
    } catch (e) {
      setError(String(e));
    }
  }

  async function activate(id: string) {
    setActiveId(id);
    setTick(null);
    setBackfill(null);
    setSelected([]);
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

  async function closeSession(id: string) {
    await api.closeSession(id);
    await live.unsubscribe(id);
    const list = await api.listSessions();
    setSessions(list);
    if (activeId === id) {
      setActiveId(null);
      setFights([]);
      setSelected([]);
      setTick(null);
      if (list.length > 0) {
        await activate(list[0].id);
      }
    }
  }

  // Announce a new release once per version; the gold pill in the session bar
  // stays as the persistent reminder after dismissal.
  useEffect(() => {
    if (version && shouldAnnounce(version)) {
      setShowUpdateNotice(true);
    }
  }, [version]);

  function dismissUpdateNotice() {
    if (version) {
      markAnnounced(version);
    }
    setShowUpdateNotice(false);
  }

  return (
    <div className="app">
      {showUpdateNotice && version && (
        <UpdateNotice version={version} onDismiss={dismissUpdateNotice} />
      )}
      <SessionBar
        sessions={sessions}
        activeId={activeId}
        backfill={backfill}
        discovered={discovered}
        version={version}
        petRollup={petRollup}
        onTogglePetRollup={togglePetRollup}
        onOpen={openLog}
        onRefreshDiscovered={refreshDiscovered}
        onActivate={activate}
        onClose={closeSession}
        error={error}
      />
      {activeId ? (
        <>
          <nav className="view-tabs">
            <button
              className={"view-tab" + (view === "overview" ? " on" : "")}
              onClick={() => setView("overview")}
            >
              Overview
            </button>
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
            <button
              className="mini-btn"
              onClick={restorePresets}
              title="Reset the built-in Raid DPS / Healing / Tanking / Right now dashboards to their defaults"
            >
              restore presets
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
          <main className="dashboard">
            <FightList
              fights={fights}
              selected={selected}
              followLive={followLive}
              onSelect={setSelected}
              onFollowLive={setFollowLive}
            />
            {view === "overview" ? (
              <div className="dashboard-main">
                <SelectionStats
                  sessionId={activeId}
                  character={sessions.find((s) => s.id === activeId)?.character ?? ""}
                  fightIds={selected}
                  refreshKey={refreshKey}
                  petRollup={petRollup}
                />
                <div className="dashboard-row">
                  <SummaryTable
                    sessionId={activeId}
                    fightIds={selected}
                    refreshKey={refreshKey}
                    excludeDamageShields={excludeDs}
                    onToggleDamageShields={setExcludeDs}
                    petRollup={petRollup}
                    onOpenInBuilder={openInBuilder}
                    colors={entityColors}
                  />
                  <div className="panel-stack">
                    <LiveMeter tick={tick} colorFor={entityColors.claim} petRollup={petRollup} />
                    <DeathLog sessionId={activeId} fightIds={selected} refreshKey={refreshKey} />
                  </div>
                </div>
                <div className="dashboard-row halves">
                  <DpsChart
                    sessionId={activeId}
                    fightIds={selected}
                    refreshKey={refreshKey}
                    followLive={followLive}
                    petRollup={petRollup}
                    colors={entityColors}
                  />
                  <AbilityChart
                    sessionId={activeId}
                    fightIds={selected}
                    refreshKey={refreshKey}
                    character={sessions.find((s) => s.id === activeId)?.character ?? ""}
                    petRollup={petRollup}
                    colors={entityColors}
                  />
                </div>
                <TimelineChart
                  sessionId={activeId}
                  fightIds={selected}
                  refreshKey={refreshKey}
                  character={sessions.find((s) => s.id === activeId)?.character ?? ""}
                  fights={fights}
                />
              </div>
            ) : (
              (() => {
                const dashboard = dashboards.find((d) => d.id === view);
                return dashboard ? (
                  <DashboardView
                    dashboard={dashboard}
                    ctx={{ sessionId: activeId, fightIds: selected, refreshKey, petRollup, colors: entityColors }}
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
        </>
      ) : (
        <main className="welcome">
          <h1>No log open</h1>
          {discovered.length > 0 ? (
            <>
              <p>Recent and detected EverQuest logs — click one to start:</p>
              <div className="discovered-list">
                {discovered.map((d) => (
                  <button key={d.path} className="discovered-row" onClick={() => openLog(d.path)}>
                    <span className="discovered-name">
                      {d.character} <span className="subtle">@{d.server}</span>
                    </span>
                    <span className="discovered-meta">
                      last written {describeAge(d.lastWriteTime)} · {(d.sizeBytes / 1048576).toFixed(1)} MB ·{" "}
                      {d.source}
                    </span>
                    <span className="discovered-path">{d.path}</span>
                  </button>
                ))}
              </div>
              <p className="subtle">
                Logging must be on in game (<code>/log</code>). You can also paste any log path above.
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
        </main>
      )}
    </div>
  );
}
