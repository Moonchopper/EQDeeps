import { useState } from "react";
import type { Dimension, QuerySource } from "../api";
import { DIMENSIONS, METRIC_LABELS, VALIDITY_FLAGS, type PanelDef } from "./model";

interface Props {
  panel: PanelDef;
  onSave: (panel: PanelDef) => void;
  onCancel: () => void;
}

const SOURCES: QuerySource[] =
  ["damage", "healing", "tanking", "casts", "deaths", "experience", "faction", "loot", "considers"];

/**
 * The query editor — every panel is metric × dimensions × filters × scope ×
 * bucketing, and this form is the whole model. Canned views are just presets
 * of the same fields.
 */
export function QueryBuilder({ panel, onSave, onCancel }: Props) {
  const [draft, setDraft] = useState<PanelDef>({ ...panel });
  const set = <K extends keyof PanelDef>(key: K, value: PanelDef[K]) =>
    setDraft((d) => ({ ...d, [key]: value }));

  const toggleList = (key: "metrics" | "excludeFlags", value: string) => {
    const list = draft[key];
    set(key, list.includes(value) ? list.filter((v) => v !== value) : [...list, value]);
  };

  const parseNames = (text: string) =>
    text
      .split(",")
      .map((s) => s.trim())
      .filter((s) => s.length > 0);

  const groupBySecondary = draft.groupBy[1] ?? "";

  return (
    <div className="modal-backdrop" onClick={onCancel}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-title">Edit panel</div>

        <div className="form-grid">
          <label>Title</label>
          <input value={draft.title} onChange={(e) => set("title", e.target.value)} />

          <label>Show as</label>
          <div className="radio-row">
            {(["table", "line", "bar", "tile"] as const).map((v) => (
              <label key={v}>
                <input
                  type="radio"
                  checked={draft.viz === v}
                  onChange={() => set("viz", v)}
                />
                {v}
              </label>
            ))}
          </div>

          <label>Source</label>
          <select
            value={draft.source}
            onChange={(e) => set("source", e.target.value as QuerySource)}
          >
            {SOURCES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>

          <label>Scope</label>
          <div className="radio-row">
            <label>
              <input
                type="radio"
                checked={draft.scopeMode === "selection"}
                onChange={() => set("scopeMode", "selection")}
              />
              selected fights
            </label>
            <label>
              <input
                type="radio"
                checked={draft.scopeMode === "all"}
                onChange={() => set("scopeMode", "all")}
              />
              all fights
            </label>
            <label>
              <input
                type="radio"
                checked={draft.scopeMode === "recent"}
                onChange={() => set("scopeMode", "recent")}
              />
              last
              <input
                className="num-input"
                type="number"
                min={5}
                value={draft.lastSeconds}
                onChange={(e) => set("lastSeconds", Math.max(5, Number(e.target.value) || 60))}
              />
              s
            </label>
          </div>

          {draft.scopeMode !== "recent" && (
            <>
              <label>Trim</label>
              <div className="radio-row">
                skip first
                <input
                  className="num-input"
                  type="number"
                  min={0}
                  value={draft.skipFirstSeconds}
                  onChange={(e) => set("skipFirstSeconds", Math.max(0, Number(e.target.value) || 0))}
                />
                s, max
                <input
                  className="num-input"
                  type="number"
                  min={0}
                  value={draft.maxSeconds ?? ""}
                  placeholder="∞"
                  onChange={(e) =>
                    set("maxSeconds", e.target.value === "" ? null : Math.max(1, Number(e.target.value) || 1))
                  }
                />
                s
              </div>
            </>
          )}

          <label>Group by</label>
          <div className="radio-row">
            <select
              value={draft.groupBy[0]}
              onChange={(e) => {
                const primary = e.target.value as Dimension;
                set("groupBy", groupBySecondary ? [primary, groupBySecondary as Dimension] : [primary]);
              }}
            >
              {DIMENSIONS.map((d) => (
                <option key={d.value} value={d.value}>
                  {d.label}
                </option>
              ))}
            </select>
            then
            <select
              value={groupBySecondary}
              onChange={(e) => {
                const secondary = e.target.value;
                set(
                  "groupBy",
                  secondary ? [draft.groupBy[0], secondary as Dimension] : [draft.groupBy[0]],
                );
              }}
            >
              <option value="">(nothing)</option>
              {DIMENSIONS.filter((d) => d.value !== draft.groupBy[0]).map((d) => (
                <option key={d.value} value={d.value}>
                  {d.label}
                </option>
              ))}
            </select>
          </div>

          {draft.viz === "table" ? (
            <>
              <label>Columns</label>
              <div className="check-grid">
                {Object.entries(METRIC_LABELS).map(([metric, label]) => (
                  <label key={metric}>
                    <input
                      type="checkbox"
                      checked={draft.metrics.includes(metric)}
                      onChange={() => toggleList("metrics", metric)}
                    />
                    {label}
                  </label>
                ))}
              </div>
            </>
          ) : draft.viz !== "line" ? (
            <>
              <label>Metric</label>
              <select
                value={draft.primaryMetric}
                onChange={(e) => set("primaryMetric", e.target.value)}
              >
                {Object.entries(METRIC_LABELS).map(([metric, label]) => (
                  <option key={metric} value={metric}>
                    {label}
                  </option>
                ))}
              </select>
            </>
          ) : (
            <>
              <label>Smoothing</label>
              <div className="radio-row">
                bucket
                <input
                  className="num-input"
                  type="number"
                  min={1}
                  value={draft.bucketSeconds}
                  onChange={(e) => set("bucketSeconds", Math.max(1, Number(e.target.value) || 1))}
                />
                s, rolling window
                <input
                  className="num-input"
                  type="number"
                  min={1}
                  value={draft.windowSec}
                  onChange={(e) => set("windowSec", Math.max(1, Number(e.target.value) || 1))}
                />
                s
              </div>
            </>
          )}

          <label>Exclude</label>
          <div className="check-grid">
            {VALIDITY_FLAGS.map((f) => (
              <label key={f.value}>
                <input
                  type="checkbox"
                  checked={draft.excludeFlags.includes(f.value)}
                  onChange={() => toggleList("excludeFlags", f.value)}
                />
                {f.label}
              </label>
            ))}
          </div>

          <label>Players</label>
          <input
            placeholder="all — or comma-separated names"
            defaultValue={draft.playerFilter.join(", ")}
            onBlur={(e) => set("playerFilter", parseNames(e.target.value))}
          />

          <label>Abilities</label>
          <input
            placeholder="all — or comma-separated spell/skill names"
            defaultValue={draft.spellFilter.join(", ")}
            onBlur={(e) => set("spellFilter", parseNames(e.target.value))}
          />
        </div>

        <div className="modal-actions">
          <button className="btn" onClick={onCancel}>
            Cancel
          </button>
          <button className="btn primary" onClick={() => onSave(draft)}>
            Save panel
          </button>
        </div>
      </div>
    </div>
  );
}
