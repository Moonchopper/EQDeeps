import * as signalR from "@microsoft/signalr";
import type { FightInfo, QueryResult } from "./api";

export interface BackfillEvent {
  sessionId: string;
  bytesProcessed: number;
  totalBytes: number;
  complete: boolean;
}

export interface FightsEvent {
  sessionId: string;
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
