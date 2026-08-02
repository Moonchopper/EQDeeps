# ADR-010: Installer & auto-update

Status: accepted (2026-08-02). Scope: feature F22. Supersedes the "update
check" and "one self-contained single-file exe" bullets of ADR-008; the
notify-only behaviour described there survives as the portable-build fallback.

## Context

Through v0.3.2 EQDeeps shipped as a portable zip containing one ~76 MB
single-file exe, and its update story was a link: the session bar showed
"vX.Y.Z available ↗" and the user downloaded and re-extracted by hand. Now that
releases are Authenticode-signed (docs/release-signing.md) there is a trust
anchor good enough to install code automatically, and the owner wanted real
auto-updating with explicit per-update consent.

## Decision

**Inno Setup for installation, NetSparkle for updating.**

The alternative seriously considered was Velopack, which is the more modern
framework and would have brought delta updates — a real advantage at this
artifact size. It was rejected on install-location control: its `Setup.exe` is
one-click with no directory picker (offering only a `--installto` command-line
flag, a nonstarter for end users), and its official answer to that gap, the
`--msi` wizard added in velopack#606, defaults to a per-machine install under
Program Files that Velopack's own updater documents as unsupported. Pinning the
MSI to `--instLocation perUser` removes the location dialog entirely, so the
wizard and working auto-update were mutually exclusive.

Inno Setup has no such conflict: it installs per-user by default (no UAC, and
the install directory stays writable so updates apply silently), while still
offering a real directory page and an "install for all users" choice for
anyone who wants one. The cost accepted is that there are no delta updates —
each update re-downloads the full installer.

### Shape

- **Installer** (`installer/EQDeeps.iss`): per-user by default under
  `%LocalAppData%\Programs\EQDeeps` (`PrivilegesRequired=lowest` with
  `PrivilegesRequiredOverridesAllowed=dialog`), directory page on, Start Menu
  icon always and desktop icon opt-in, license page, Windows 10+ / x64. `AppId`
  is fixed forever: it is what makes an update land in the directory the user
  originally chose. Uninstalling deliberately leaves `%AppData%\EQDeeps`
  (sessions, preferences, MRU) alone.
- **Payload is a folder, not a single-file exe.** An installer wants an
  application directory, and single-file publishing would re-extract ~180 MB to
  temp on every launch for no remaining benefit.
- **Portable zip still ships.** It has no uninstaller, so `UpdateService`
  detects that (`unins000.exe` absent) and degrades to exactly the old
  notify-only behaviour rather than pretending it can install.

### Consent model (the point of the feature)

`UpdatePreferences.Decide` is a pure function over persisted answers, so the
whole matrix is unit-tested without a network or a clock. Four levers, because
"no" means four different things:

| Lever | Lasts until | Stored as |
| --- | --- | --- |
| Not right now | app restart, or a manual check | in-memory set, never persisted |
| Skip this version | a release newer than the one offered | `SkippedVersions[]` |
| Don't ask again for vX | the user is running something other than vX | `MutedOnVersion` |
| Update automatically | changed in the dialog | `Mode = Auto` |

Two properties fall out of keying the levers this way, and both are
deliberate: skipping one release never hides the next, and the mute expires by
itself the moment the user ends up on a different build — so there is no way to
permanently silence updates by accident. An explicit "check for updates"
overrides every standing decline, or a user who once muted prompts could never
find an update again without editing JSON.

Default is `Ask`. Preferences live server-side in
`%AppData%\EQDeeps\update-prefs.json`, not `localStorage`, because the update
loop must honour them with no UI attached.

### Staging and applying

Downloads are staged and **applied on exit by default**, never mid-session — a
combat parser that restarts itself during a raid is worse than one that updates
a day late. `UserInteractionMode.DownloadNoInstall` keeps NetSparkle from
installing on its own; the staged installer is recorded in
`pending-update.json` so an interrupted run resumes without re-downloading.
"Update & restart now" is available for users who want it immediately; it
applies an already-staged installer directly, so it works offline and never
re-downloads what is already on disk.

Applying is a two-part handshake, and both halves are required: the handoff
script waits for our PID to exit before it can replace our files, so applying
must also shut the app down. Omitting that shipped once — the script waited its
two minutes and gave up, making "restart to update" look inert. `UpdateApplyTests`
now pins it.

A long-running session re-checks every **two hours**. EQDeeps is commonly left
open for days, so that loop is the only thing that tells such a session a
release exists; six hours was long enough for a whole raid night to pass. The
request is a ~2 KB app cast fetch, so checking more often costs nothing.

Because Inno Setup cannot replace a running exe, `UpdateInstaller` writes a
throwaway batch script that waits for our PID to exit, runs the installer
silently, optionally relaunches, and deletes itself. Applying on exit is
skipped when the install directory is not user-writable (a machine-wide
install): a UAC prompt appearing after the user closed the app reads as
malware, so that case waits for an explicit "Restart to update" click.

### Trust

Two independent gates, both required:

1. **Ed25519** (NetSparkle, `SecurityMode.Strict`) over both the app cast and
   the installer, verified against a public key compiled into the assembly.
   Signed in CI from `SPARKLE_PRIVATE_KEY`.
2. **Authenticode** via `WinVerifyTrust` immediately before execution, with the
   signer subject required to match `CN=Austin Culbertson`. Reading the
   certificate alone would not do — that succeeds on tampered files; the chain
   has to be walked.

Compromising the update host alone therefore breaks neither. A build with the
placeholder public key still in place refuses to self-install and says so in
the log rather than staging something unverifiable.

The app cast is published as a release asset, so
`releases/latest/download/appcast.xml` is a stable URL with no server to run.
It is generated *after* the GitHub release exists so it can carry the generated
release notes, which are what the consent dialog shows as bullets.

## Consequences

- Updates re-download the full installer (~76 MB). The mitigation, if it starts
  to matter, is publishing framework-dependent and having Inno bootstrap the
  .NET 8 Desktop Runtime + WebView2, which would cut the payload to roughly
  20 MB. Deliberately left as a follow-up rather than folded into this change.
- Existing portable-zip users are not migrated automatically; they keep getting
  notified and switch by running the installer once.
- `SPARKLE_PRIVATE_KEY` is now release-critical. Losing it means no existing
  install can verify a new release, and recovery requires shipping a new public
  key by some other channel. See docs/release-signing.md.
- The app's outbound surface is unchanged in kind — still only GitHub, still
  silent on failure, still no telemetry — but it now downloads and executes
  code, which is why both signature gates are non-optional.

## Verification

`UpdateConsentTests` pin all four levers, including the two expiry properties
above; `UpdatePreferenceStoreTests` and `PendingUpdateStoreTests` cover
persistence, corrupt files, and markers whose installer has been cleaned out of
temp. The release workflow fails the build if either signature is missing
rather than shipping an installer every client would refuse.
