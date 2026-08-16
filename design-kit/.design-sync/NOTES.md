# design-sync notes — eqdeeps-ui-kit

Repo-specific gotchas a future sync should know before re-deriving them.

- **The decorator-bundle esbuild pass can't load the self-hosted woff2 font.**
  `.storybook/preview.tsx` imports `../src/styles/index.css`, whose `tokens.css`
  `@font-face` references the woff2 by `url()`. The converter's own
  `bundlePreviewDecorators` step (`.ds-sync/lib/source-storybook.mjs`) bundles
  that preview module with a hard-coded esbuild loader map (`.js`/`.json`
  only) that never reads `cfg.storyImports.loaders` — so it fails with
  `No loader is configured for ".woff2" files`. Fix used: export a tiny
  `EqdPage` wrapper component (`src/EqdPage.tsx`, `div.eqd-page`) and set
  `cfg.provider: {"component": "EqdPage"}`, which replaces decorator bundling
  entirely for the converter's previews — the real Storybook reference is
  unaffected (its own Vite pipeline handles the font fine). `EqdPage` is
  excluded from the synced roster via `componentSrcMap: {"EqdPage": null}` —
  it's kit plumbing, not a component a design agent would build with.
- **`cfg.cssEntry` had to be set explicitly.** tsup emits `dist/index.css` as
  a fully separate file (no `import` reference survives in `dist/index.js`
  since `injectStyle` is off), so the converter's auto-detection — which
  looks for a CSS side-effect import in the JS entry — found nothing and
  reported `[CSS_PLACEHOLDER]`. Set `cfg.cssEntry: "dist/index.css"`.
- **Five components render wider than a grid cell**: EmptyState, CheckboxRow,
  FormRow, Panel, DataTable. All `wide`, not `escape` — fixed with
  `cardMode: "column"` in `cfg.overrides` per the `[GRID_OVERFLOW]` remedy.
  Grades carried through the targeted rebuild; no re-grade needed for a
  `wide`→`column` fix.
- **TokenGallery's per-story capture was truncated.** `compare.mjs`'s
  `storyShot()` path always takes a viewport-clipped screenshot
  (`fullPage: false`), unlike the Storybook reference side, which screenshots
  the story root element and gets its full rendered height regardless of
  viewport. Default capture viewport is 900×700; TokenGallery's real content
  was ~2199px tall, so everything from "Chart palette" onward was cropped in
  the preview only — not a real component bug, a capture-viewport mismatch.
  Fixed two ways together: tightened `TokenGallery.tsx`'s section/row spacing
  (marginBottom 32→16, several inner gaps 6-8→4-6) to bring total height
  under the compare tool's 2000px viewport cap, and set
  `cfg.overrides.TokenGallery.viewport: "960x2000"` as a safety margin. A
  `viewport` override moves the grade contract (full rebuild + full compare
  required, not the `.tsx`-only targeted loop) — see the storybook shape's
  rebuild-rules table.
- **`Modal` is an overlay** (`position: fixed` backdrop) — set
  `cfg.overrides.Modal: {"cardMode": "single", "viewport": "480x320"}`
  proactively before validate ever flagged it, since the pattern is
  well-known from ADR-015 (backdrop + scrim over the page). Confirmed correct
  once graded — no `[PORTAL?]` warn fired, so no story escaped the card.

## Known render warns

None outstanding — `package-validate.mjs` and the final compare pass are
clean with zero warnings after the fixes above.

## Re-sync risks

- **`EqdPage` and the `cfg.provider` wiring are load-bearing for the
  converter's preview fidelity**, not just a style nicety. If a future sync
  drops or renames `EqdPage`, previews silently lose the dark page ground —
  they'd still "render" (no `[RENDER]` failure) but every card would go back
  to browser-default background/font, which the render check does not catch
  (only a human glance at the sheets would).
- **The font is embedded as a base64 `data:` URI in `dist/index.css`**
  (`tsup.config.ts`'s `.woff2: "dataurl"` loader), not shipped as a separate
  file. This keeps the package self-contained but means `dist/index.css` is
  ~73 KB even though the source rules are tiny — expected, not a bug, but
  worth knowing before "why is this CSS file so big" comes up again.
- **`TokenGallery`'s spacing was tuned to fit under a 2000px capture cap**,
  not for its own sake. If new token sections are added to the gallery (a
  new scale, more swatches), total height grows again and the same
  truncation can recur — check the raw `_ds.png` capture against the `_sb.png`
  one for full-height parity after any TokenGallery edit, don't just trust
  the thumbnail sheet (it visually compresses both sides to the same width,
  which hides a height mismatch at a glance).
- **No remote-fetched assets anywhere in this kit's stories** (icons are all
  bundled Tabler React components, no CDN images) — so the `[ASSETS_BLOCKED]`
  sandboxed-shell canary never had anything to catch here. If a future story
  adds a remote image, re-verify from a shell with real network egress
  before trusting that story's grade.
- **This is a fresh kit with no prior EQDeeps `ui/` dependency at build
  time** — `design-kit/` never imports from `ui/`, only visually mirrors it
  (tokens, class names, the font file) by intentional, one-time copy. A
  future visual-language change in `ui/src/styles.css` or
  `docs/architecture/adr-015-visual-language.md` does NOT propagate here
  automatically; re-porting is a manual, deliberate act, same as the
  original port was.
