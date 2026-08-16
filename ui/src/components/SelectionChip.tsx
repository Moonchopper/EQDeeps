import { IconPin, IconPinned, IconX } from "@tabler/icons-react";
import { useSelection, useSelectionActions } from "../highlight";

/**
 * The standing selection, made visible: who is lit, whether it is pinned, and
 * the way out. It sits in the header because the selection is app-wide state
 * with no panel of its own — a row you clicked on Summary is still lit on
 * Healing, and the chip is what says so, on the one strip every view shares.
 *
 * Absent when nothing is selected. A permanent "nothing selected" slot would
 * be furniture on the ninety-percent path.
 */
export function SelectionChip({ colorFor }: { colorFor: (key: string, pool: string) => string }) {
  const selection = useSelection();
  const { setPinned, clear } = useSelectionActions();
  if (!selection) {
    return null;
  }
  const { key, pool } = selection.target;
  const Pin = selection.pinned ? IconPinned : IconPin;
  return (
    <span
      className={"selection-chip" + (selection.pinned ? " pinned" : "")}
      title={
        selection.pinned
          ? `${key} stays lit on every view until unpinned`
          : `${key} stays lit on this view; pin it to keep it on every view`
      }
    >
      <span className="color-chip" style={{ background: colorFor(key, pool) }} />
      <span className="selection-name">{key}</span>
      <button
        className="selection-btn"
        onClick={() => setPinned(!selection.pinned)}
        title={selection.pinned ? "Unpin — keep only on this view" : "Pin across every view"}
        aria-label={selection.pinned ? "Unpin" : "Pin across every view"}
        aria-pressed={selection.pinned}
      >
        <Pin size={14} stroke={1.75} />
      </button>
      <button
        className="selection-btn"
        onClick={clear}
        title="Clear the selection"
        aria-label="Clear the selection"
      >
        <IconX size={14} stroke={1.75} />
      </button>
    </span>
  );
}
