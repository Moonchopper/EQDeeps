# ADR-009: Windowed shell

Status: accepted (2026-08-01). Scope: post-phase-8 UX — supersedes the launch
behavior in ADR-008 (browser-tab UI).

## Context

Shipping "an exe that opens your browser" made EQDeeps feel like a
client/server pair rather than one application: the UI lived in a tab next to
unrelated sites, carried browser chrome, had no taskbar identity of its own,
and the server's lifetime had to be inferred from tab-close heuristics
(pagehide beacons, memory-saver false positives — see ADR-008 and the
ClientTracker). The backend/SPA split itself is fine; only the presentation
needed to change.

## Design

- **WebView2 shell window.** The exe is now a WinExe: it starts Kestrel
  exactly as before, then hosts the SPA in its own WinForms window via
  WebView2 (`AppWindow`), the Chromium engine preinstalled with Windows 10/11
  — nothing bundled, no Electron. The REST/SignalR architecture, SPA build,
  and publish pipeline are untouched; the message loop runs on a dedicated
  STA thread while the host keeps the main thread.
- **Lifetime = window.** Closing the window stops the host (`--stay-alive`
  opts out). The ClientTracker goodbye heuristic remains, but only governs
  browser mode; in window mode it is inert. A stopping host (Ctrl+C from an
  attached console) closes the window symmetrically.
- **Single instance focuses the window.** A second launch that finds a
  running instance POSTs `/api/ui/focus` (WindowBridge); the running shell
  restores/activates its window. 404 (browser mode, headless, older build)
  falls back to opening a browser tab, as before.
- **Graceful degradation.** `--browser` forces the old default-browser mode;
  a missing WebView2 runtime (stripped-down installs) or a failed WebView2
  initialization degrades to browser mode automatically, handing lifetime
  back to the tab monitor. `--no-browser` stays headless.
- **No console box, still scriptable.** WinExe removes the console window on
  double-click; `AttachConsole(ATTACH_PARENT_PROCESS)` keeps `dotnet run` and
  terminal launches printing the URL/version banner.
- **Window placement persists** in `%AppData%\EQDeeps\window.json` (beside
  the DocumentStore documents), validated against current screens before
  restore. WebView2 profile data (cache-like, machine-local) lives under
  `%LocalAppData%\EQDeeps\WebView2`.
- **The window only shows the app.** New-window requests and navigations off
  the localhost origin (release notes, docs) open in the user's real browser.
- **Costs accepted (Windows-first is a locked decision):** the server project
  targets `net8.0-windows` (Server.Tests follows), and the self-contained exe
  grows to ~182 MB with the WinForms runtime pack (was ~93 MB). The WebView2
  package's unconditional WPF assembly reference is stripped in
  `Directory.Build.targets` to avoid an MSB3277 WindowsBase conflict. The
  WebView2 SDK is BSD-licensed (attribution added to NOTICE).
- **Door opened, not walked through:** a native shell makes an always-on-top
  compact meter overlay possible later; a tray icon (rejected in ADR-008 for
  dragging in a UI framework) is now nearly free if `--stay-alive` grows a UI.

## Verification

Smoke-tested against the published single-file artifact: the window opens
titled "EQDeeps" with `/api/health` ok and WebView2 processes running;
`POST /api/ui/focus` returns 204; a second exe launch leaves one process and
focuses the window; closing the window exits the process within the shutdown
grace and writes `window.json`; `--no-browser` runs headless (no window,
focus returns 404). Full test suite passes under `net8.0-windows`.
