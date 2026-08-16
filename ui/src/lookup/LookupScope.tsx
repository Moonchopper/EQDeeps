import { createContext, type ReactNode } from "react";

/**
 * The install the open log is from, for everything under it that offers a
 * lookup door. Set once by App for the active session rather than threaded
 * through every table, row and dialog that names a mob — a fight row deep in
 * a memoised list has no business carrying a prop about game installs, and a
 * context update reaches it through the memo anyway.
 */
export const LookupInstallContext = createContext<string | undefined>(undefined);

export function LookupScope({ install, children }: { install?: string; children: ReactNode }) {
  return <LookupInstallContext.Provider value={install}>{children}</LookupInstallContext.Provider>;
}
