import { createContext, useMemo, type ReactNode } from "react";

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
  // Memoised, and it matters more than it looks: a context value that is a
  // fresh object each render re-renders every consumer on every App render,
  // straight through the fight rows' memo — 7,900 lookup doors on an
  // 8,000-fight log, on every tab switch and every live tick. Measured on
  // that log: a Combat tab switch was 500–800 ms of long tasks; with this
  // line it is 0–180 ms, and each view issues half the queries it did,
  // because the panels stop re-rendering along with everything else.
  const value = useMemo(() => ({ install, sessionId }), [install, sessionId]);
  return <LookupScopeContext.Provider value={value}>{children}</LookupScopeContext.Provider>;
}
