import type { ReactNode } from "react";

export interface EqdPageProps {
  children?: ReactNode;
}

/**
 * The kit's page-ground wrapper (see styles/tokens.css's .eqd-page rules):
 * dark page background, ink colour, UI font. Exported — not because it's one
 * of the kit's nine component groups, but so the design-sync preview
 * pipeline can wrap every compiled story in it via `.design-sync/config.json`'s
 * `provider`, mirroring what `.storybook/preview.tsx`'s decorator does for the
 * real Storybook reference render. The decorator bundle esbuild pass can't
 * process the woff2 asset the CSS chain pulls in (its loader map is
 * hard-coded upstream); `cfg.provider` sidesteps that entirely, so both
 * sides of the compare oracle end up wrapped the same way. Kept out of the
 * synced roster via `componentSrcMap: {"EqdPage": null}` — it's kit
 * plumbing, not a component someone would build a design with.
 */
export function EqdPage({ children }: EqdPageProps) {
  return (
    <div className="eqd-page" style={{ minHeight: "100%", padding: 24 }}>
      {children}
    </div>
  );
}
