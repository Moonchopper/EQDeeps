import { useEffect, useMemo, useRef, useState } from "react";
import { api, type DiscoveredLog, type FightInfo, type SessionInfo } from "./api";
import { createLiveConnection, type BackfillEvent, type TickEvent } from "./live";
import { describeAge, SessionBar } from "./components/SessionBar";
import { FightList } from "./components/FightList";
import { SummaryTable } from "./components/SummaryTable";
import { DpsChart } from "./components/DpsChart";
import { LiveMeter, makeColorAssigner } from "./components/LiveMeter";
import { DeathLog } from "./components/DeathLog";
import { SelectionStats } from "./components/SelectionStats";
import { AbilityChart } from "./components/AbilityChart";

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
  const [error, setError] = useState<string | null>(null);

  function togglePetRollup(on: boolean) {
    setPetRollup(on);
    localStorage.setItem("eqdeeps.petRollup", on ? "on" : "off");
  }

  const activeIdRef = useRef(activeId);
  activeIdRef.current = activeId;
  const followRef = useRef(followLive);
  followRef.current = followLive;
  const colorFor = useMemo(() => makeColorAssigner(), [activeId]);

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

  return (
    <div className="app">
      <SessionBar
        sessions={sessions}
        activeId={activeId}
        backfill={backfill}
        discovered={discovered}
        petRollup={petRollup}
        onTogglePetRollup={togglePetRollup}
        onOpen={openLog}
        onRefreshDiscovered={refreshDiscovered}
        onActivate={activate}
        onClose={closeSession}
        error={error}
      />
      {activeId ? (
        <main className="dashboard">
          <FightList
            fights={fights}
            selected={selected}
            followLive={followLive}
            onSelect={setSelected}
            onFollowLive={setFollowLive}
          />
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
              />
              <div className="panel-stack">
                <LiveMeter tick={tick} colorFor={colorFor} petRollup={petRollup} />
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
              />
              <AbilityChart
                sessionId={activeId}
                fightIds={selected}
                refreshKey={refreshKey}
                character={sessions.find((s) => s.id === activeId)?.character ?? ""}
                petRollup={petRollup}
              />
            </div>
          </div>
        </main>
      ) : (
        <main className="welcome">
          <h1>No log open</h1>
          {discovered.length > 0 ? (
            <>
              <p>Found these EverQuest logs on this machine — click one to start:</p>
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
