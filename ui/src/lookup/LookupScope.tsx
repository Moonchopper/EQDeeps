import { createContext, type ReactNode } from "react";

/**
 * What every lookup door under it needs to know about the open log: the
 * install it is from (which decides the world, see `providers.ts`) and the
 * session it belongs to (which the item doors ask to resolve a name to the
 * game's id, F29). Set once by App for the active session rather than
 * threaded through every table, row and dialog that names a mob or an item —
 * a fight row deep in a memoised list has no business carrying props about
 * game installs, and a context update reaches it through the memo anyway.
 */
export interface LookupScopeValue {
  install?: string;
  sessionId?: string;
}

export const LookupScopeContext = createContext<LookupScopeValue>({});

export function LookupScope({
  install,
  sessionId,
  children,
}: LookupScopeValue & { children: ReactNode }) {
  return <LookupScopeContext.Provider value={{ install, sessionId }}>{children}</LookupScopeContext.Provider>;
}
