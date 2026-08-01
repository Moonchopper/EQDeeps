import { useEffect, useMemo, useRef, useState } from "react";
import { api, type FightInfo, type SessionInfo } from "./api";
import { createLiveConnection, type BackfillEvent, type TickEvent } from "./live";
import { SessionBar } from "./components/SessionBar";
import { FightList } from "./components/FightList";
import { SummaryTable } from "./components/SummaryTable";
import { DpsChart } from "./components/DpsChart";
import { LiveMeter, makeColorAssigner } from "./components/LiveMeter";
import { DeathLog } from "./components/DeathLog";

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
  const [error, setError] = useState<string | null>(null);

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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

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
        onOpen={openLog}
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
            <div className="dashboard-row">
              <SummaryTable
                sessionId={activeId}
                fightIds={selected}
                refreshKey={refreshKey}
                excludeDamageShields={excludeDs}
                onToggleDamageShields={setExcludeDs}
              />
              <LiveMeter tick={tick} colorFor={colorFor} />
            </div>
            <div className="dashboard-row">
              <DpsChart sessionId={activeId} fightIds={selected} refreshKey={refreshKey} />
              <DeathLog sessionId={activeId} fightIds={selected} refreshKey={refreshKey} />
            </div>
          </div>
        </main>
      ) : (
        <main className="welcome">
          <h1>No log open</h1>
          <p>
            Open your EverQuest log file above (for example{" "}
            <code>C:\EverQuest\Logs\eqlog_Yourname_server.txt</code>). Historical fights load
            immediately; while the game runs, everything updates live.
          </p>
        </main>
      )}
    </div>
  );
}
