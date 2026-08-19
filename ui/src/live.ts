import * as signalR from "@microsoft/signalr";
import type { FightInfo, QueryResult } from "./api";

export interface BackfillEvent {
  sessionId: string;
  bytesProcessed: number;
  totalBytes: number;
  complete: boolean;
}

/**
 * A live push of fights. `full` means `fights` is the whole list; otherwise it
 * is only the fights changed after `baseVersion`, to merge by id into a list
 * held at that version or later. The server sends deltas because a raid's
 * worth of closed fights should not travel every time the open one takes a
 * hit — measured at 2 MB a second on an 8,000-fight log.
 */
export interface FightsEvent {
  sessionId: string;
  version: number;
  baseVersion: number;
  full: boolean;
  fights: FightInfo[];
}

export interface TickEvent {
  sessionId: string;
  fightIds: number[];
  result: QueryResult;
}

export interface LiveHandlers {
  onBackfill?: (e: BackfillEvent) => void;
  onFights?: (e: FightsEvent) => void;
  onTick?: (e: TickEvent) => void;
  /** The hub gave up reconnecting — the server has likely exited. */
  onConnectionLost?: () => void;
}

/** One hub connection for the app; per-session subscriptions via groups. */
export function createLiveConnection(handlers: LiveHandlers) {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/live")
    .withAutomaticReconnect()
    .build();

  connection.on("backfill", (e: BackfillEvent) => handlers.onBackfill?.(e));
  connection.on("fights", (e: FightsEvent) => handlers.onFights?.(e));
  connection.on("tick", (e: TickEvent) => handlers.onTick?.(e));
  connection.onclose(() => handlers.onConnectionLost?.());

  // Tell the server this was a genuine close (tab/window closed or navigated
  // away) as opposed to the browser discarding or freezing the tab — the
  // server only ties its lifetime to deliberate closes. Back/forward-cache
  // stashing sets `persisted` and is not a close.
  window.addEventListener("pagehide", (e) => {
    if (!e.persisted) {
      navigator.sendBeacon("/api/ui/goodbye");
    }
  });

  const subscribed = new Set<string>();

  return {
    async start() {
      await connection.start();
      for (const id of subscribed) {
        await connection.invoke("Subscribe", id);
      }
    },
    async subscribe(sessionId: string) {
      subscribed.add(sessionId);
      if (connection.state === signalR.HubConnectionState.Connected) {
        await connection.invoke("Subscribe", sessionId);
      }
    },
    async unsubscribe(sessionId: string) {
      subscribed.delete(sessionId);
      if (connection.state === signalR.HubConnectionState.Connected) {
        await connection.invoke("Unsubscribe", sessionId);
      }
    },
    connection,
  };
}

export type LiveConnection = ReturnType<typeof createLiveConnection>;
