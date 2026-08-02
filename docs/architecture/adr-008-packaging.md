# ADR-008: Packaging & distribution

Status: accepted (2026-08-01), partly superseded. Scope: phase 8 — feature F14.
The launch behavior below (browser-tab UI) is superseded by ADR-009 (windowed
shell); it survives as the fallback/`--browser` mode. The **single-file exe**
and **update check** bullets are superseded by ADR-010 (installer +
auto-update): the payload is now a folder installed by Inno Setup, and the
notify-only update check survives only in the portable build.

## Design

- **One self-contained single-file exe.** `dotnet publish -r win-x64
  --self-contained -p:PublishSingleFile=true` with the built SPA **embedded in
  the assembly** (`GenerateEmbeddedFilesManifest` + `ManifestEmbeddedFileProvider`),
  so the artifact is literally `EQDeeps.Server.exe` — no loose wwwroot, no
  installer, no .NET on the target machine. Dev builds keep serving the
  physical `wwwroot`; builds without a UI degrade to API-only. ~93 MB
  (self-contained runtime); trimming was rejected for now — SignalR and
  reflection-heavy JSON make `PublishTrimmed` risky for marginal gain.
- **Launch behavior** (Program.cs): if an EQDeeps instance already answers on
  the default port, open the browser to it and exit (double-click twice ≠ two
  servers); if the port is taken by something else, rebind to a dynamic port;
  then print the URL and open the default browser. Flags: `--no-browser`,
  `--no-update-check`, standard `--urls`. Tray icon deferred — it drags in a
  Windows UI framework for little benefit over the console+browser pairing.
- **Update check** (the app's only outbound call, per the system overview):
  a background GET of the GitHub latest-release API on startup, compared
  against the assembly version (prerelease/build metadata ignored). Result is
  exposed at `/api/version`; the session bar shows the running version and an
  "vX.Y.Z available ↗" link to the release page. No download, no auto-install,
  silent on failure — offline machines behave identically.
  *(Superseded by ADR-010: installed builds now stage and apply updates with
  per-update consent. This notify-only path is what the portable zip still
  does, since it has nothing to install into.)*
- **Versioning & CI**: SemVer via `<Version>` (default 0.1.0), overridden from
  the git tag in CI. `ci.yml` builds SPA + runs the full test suite on
  push/PR; `release.yml` fires on `v*` tags — test, publish win-x64, zip,
  create the GitHub release with generated notes. `scripts/publish.ps1`
  reproduces the artifact locally.

## Verification

The published exe, copied *alone* into an empty directory, was run and
verified: health + version endpoints respond and the embedded SPA (index +
hashed assets) serves — the F14 acceptance criterion ("a machine without the
.NET SDK runs the app from the published artifact") holds by construction,
since the runtime ships inside the file. `UpdateCheckerTests` pin the tag
comparison semantics; the integration suite covers `/api/version`.
