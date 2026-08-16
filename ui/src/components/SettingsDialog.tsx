import { useEffect, type ReactNode } from "react";
import type { UpdateMode, UpdateState } from "../api";
import { LABEL_SIZE_CHOICES } from "../fightOverlay";
import { CONTEXT_MODES, type ContextMode } from "../contextOverlay";
import { LOOKUP_WORLDS } from "../lookup/providers";
import { rememberWorld, useLookupWorld } from "../lookup/lookupSettings";

interface Props {
  onClose: () => void;
  density: "comfortable" | "compact";
  onDensity: (compact: boolean) => void;
  petRollup: boolean;
  onPetRollup: (on: boolean) => void;
  /** Fight overlay: -1 off, 0 bands only, otherwise the name size in px. */
  fightLabelPx: number;
  onFightLabelPx: (px: number) => void;
  contextMode: ContextMode;
  onContextMode: (mode: ContextMode) => void;
  playedTimeOnly: boolean;
  onPlayedTimeOnly: (on: boolean) => void;
  /** Whether charts follow the wall clock through quiet time. */
  liveScroll: boolean;
  onLiveScroll: (on: boolean) => void;
  update: UpdateState | null;
  onSetUpdateMode: (mode: UpdateMode) => void;
  onCheckForUpdate: () => void;
  /** Transient result of a manual check, e.g. "up to date". */
  checkNote: string | null;
  /** The install the open log is from; the reference-site choice is kept per install. */
  install?: string;
}

const UPDATE_MODES: { value: UpdateMode; label: string; hint: string }[] = [
  { value: "ask", label: "Ask me each time", hint: "Tell me about a release and wait for an answer" },
  { value: "auto", label: "Update automatically", hint: "Install every release without asking" },
  { value: "manual", label: "Never check", hint: "No update checks at all; I'll look myself" },
];

/**
 * The one place a set-once preference lives (ADR-017). These used to be
 * scattered across the header — two checkboxes by the log picker, four
 * selects inside the time controls, an update menu hung off the version
 * number — where a thing you decide once sat beside the range you change all
 * night, and the row had run out of width. Sections scale where a toolbar
 * does not: the next preference gets a row here, not a header slot.
 *
 * Nothing here is new state. Every control writes straight through to the
 * same handlers the header used, so a change shows on the charts behind the
 * dialog as it is made — there is no Apply, and nothing to lose by closing.
 */
export function SettingsDialog({
  onClose,
  density,
  onDensity,
  petRollup,
  onPetRollup,
  fightLabelPx,
  onFightLabelPx,
  contextMode,
  onContextMode,
  playedTimeOnly,
  onPlayedTimeOnly,
  liveScroll,
  onLiveScroll,
  update,
  onSetUpdateMode,
  onCheckForUpdate,
  checkNote,
  install,
}: Props) {
  // The one preference here that is not lifted through App: it is a fact
  // about the game the log came from rather than about this machine, so it
  // lives with the map choices' kind of storage (per install, in the document
  // store) and the row talks to that store directly. Still write-through: a
  // change is on every lookup menu as it is made.
  const lookup = useLookupWorld(install);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div
        className="modal settings"
        role="dialog"
        aria-modal="true"
        aria-labelledby="settings-title"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="modal-title settings-title">
          <span id="settings-title">Settings</span>
          <button className="mini-btn" onClick={onClose} aria-label="Close settings">
            close
          </button>
        </div>

        <Section title="Display">
          <Row
            label="Compact rows"
            hint="Tighter table rows — about four more on screen, at the cost of some legibility"
          >
            <input
              type="checkbox"
              checked={density === "compact"}
              onChange={(e) => onDensity(e.target.checked)}
            />
          </Row>
          <Row
            label="Pets → owners"
            hint="Merge each pet's damage and healing into its owner's rows everywhere — a query-time switch, nothing reparses"
          >
            <input
              type="checkbox"
              checked={petRollup}
              onChange={(e) => onPetRollup(e.target.checked)}
            />
          </Row>
        </Section>

        <Section title="Reference sites">
          <Row
            label="Look things up on"
            hint={
              (install ? `For logs from "${install}"` : "For logs whose game install is not known") +
              (lookup.chosen ? "" : " — guessed from the install; pick one to make it stick") +
              ". The arrow beside an item or mob opens these sites."
            }
          >
            <select
              value={lookup.world.id}
              onChange={(e) => void rememberWorld(e.target.value, install)}
              disabled={!lookup.ready}
            >
              {LOOKUP_WORLDS.map((w) => (
                <option key={w.id} value={w.id} title={w.hint}>
                  {w.name}
                </option>
              ))}
            </select>
          </Row>
        </Section>

        <Section title="Charts">
          <Row
            label="Fight overlay"
            hint="Shaded bands behind every time chart showing which mob each stretch was against, with or without the mob's name at this size"
          >
            <select value={fightLabelPx} onChange={(e) => onFightLabelPx(Number(e.target.value))}>
              {LABEL_SIZE_CHOICES.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.label}
                </option>
              ))}
            </select>
          </Row>
          <Row
            label="Context strip"
            hint="A strip above every time chart showing which zone the character was in and what level they were"
          >
            <select
              value={contextMode}
              onChange={(e) => onContextMode(e.target.value as ContextMode)}
            >
              {CONTEXT_MODES.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.label}
                </option>
              ))}
            </select>
          </Row>
          <Row
            label="Hours counted"
            hint="What the hours in a rate or a duration are counted from. Played cuts the gaps between play sessions out of the range, so a rate over a window that includes a night is not divided by the night."
          >
            <select
              value={playedTimeOnly ? "played" : "clock"}
              onChange={(e) => onPlayedTimeOnly(e.target.value === "played")}
            >
              <option value="clock">wall clock</option>
              <option value="played">played</option>
            </select>
          </Row>
          <Row
            label="Scroll with the clock"
            hint="Keep the charts moving when the log goes quiet, drawing the idle time as zero. Off pins them to the newest record."
          >
            <input
              type="checkbox"
              checked={liveScroll}
              onChange={(e) => onLiveScroll(e.target.checked)}
            />
          </Row>
        </Section>

        {update && (
          <Section title={`Updates · v${update.version}`}>
            {/* This is the only way back for someone who chose "don't ask
                again" — a standing decline the user can't find and reverse is
                just a broken app, so both the mode and an explicit check live
                here. */}
            {UPDATE_MODES.map((mode) => (
              <Row key={mode.value} label={mode.label} hint={mode.hint}>
                <input
                  type="radio"
                  name="update-mode"
                  checked={update.mode === mode.value}
                  onChange={() => onSetUpdateMode(mode.value)}
                />
              </Row>
            ))}
            <div className="settings-foot">
              <button
                className="mini-btn"
                onClick={onCheckForUpdate}
                disabled={update.stage === "checking"}
              >
                {update.stage === "checking" ? "Checking…" : "Check now"}
              </button>
              {checkNote && <span className="settings-note">{checkNote}</span>}
              {!update.canSelfInstall && (
                <span className="settings-note">
                  This copy can't install updates itself — it will link you to the release
                  instead.
                </span>
              )}
              {update.error && <span className="settings-note">{update.error}</span>}
            </div>
          </Section>
        )}
      </div>
    </div>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="settings-section">
      <h3 className="settings-heading">{title}</h3>
      {children}
    </section>
  );
}

/**
 * One preference: the control, its name, and the sentence that used to be a
 * hover title. Written out because a preference you set once is one you have
 * to understand once, and a tooltip is the wrong place for a sentence.
 */
function Row({ label, hint, children }: { label: string; hint: string; children: ReactNode }) {
  return (
    <label className="settings-row">
      <span className="settings-control">{children}</span>
      <span className="settings-text">
        <span className="settings-label">{label}</span>
        <span className="settings-hint">{hint}</span>
      </span>
    </label>
  );
}
