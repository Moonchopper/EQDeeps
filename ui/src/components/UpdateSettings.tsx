import { useEffect, useRef, useState } from "react";
import type { UpdateMode, UpdateState } from "../api";

const MODES: { value: UpdateMode; label: string; hint: string }[] = [
  { value: "ask", label: "Ask me each time", hint: "Tell me about a release and wait for an answer" },
  { value: "auto", label: "Update automatically", hint: "Install every release without asking" },
  { value: "manual", label: "Never check", hint: "No update checks at all; I'll look myself" },
];

/**
 * Update preferences, hung off the version number in the session bar. This is
 * the only way back for someone who chose "don't ask again" — a standing
 * decline the user can't find and reverse is just a broken app, so both the
 * mode and an explicit "check now" live here.
 */
export function UpdateSettings({
  state,
  onSetMode,
  onCheckNow,
}: {
  state: UpdateState;
  onSetMode: (mode: UpdateMode) => void;
  onCheckNow: () => void;
}) {
  const [open, setOpen] = useState(false);
  const wrapper = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      if (!wrapper.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, [open]);

  return (
    <span className="update-settings" ref={wrapper}>
      {/* The version alone read as static text; the gear is what tells people
          there is anything to adjust here. */}
      <button
        className="link-btn version-btn"
        onClick={() => setOpen((v) => !v)}
        title="Update preferences"
        aria-label="Update preferences"
        aria-expanded={open}
      >
        v{state.version}
        <span className="gear-icon" aria-hidden="true">
          ⚙
        </span>
      </button>
      {open && (
        <div className="update-menu">
          <div className="update-menu-title">Updates</div>
          {MODES.map((mode) => (
            <label key={mode.value} className="update-menu-row" title={mode.hint}>
              <input
                type="radio"
                name="update-mode"
                checked={state.mode === mode.value}
                onChange={() => onSetMode(mode.value)}
              />
              {mode.label}
            </label>
          ))}
          <div className="update-menu-foot">
            <button
              className="mini-btn"
              onClick={onCheckNow}
              disabled={state.stage === "checking"}
            >
              {state.stage === "checking" ? "Checking…" : "Check now"}
            </button>
            {!state.canSelfInstall && (
              <span className="update-menu-note">
                This copy can't install updates itself — it will link you to the
                release instead.
              </span>
            )}
            {state.error && <span className="update-menu-note">{state.error}</span>}
          </div>
        </div>
      )}
    </span>
  );
}
