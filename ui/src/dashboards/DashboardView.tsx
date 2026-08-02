import React, { useState } from "react";
import { GridLayout, useContainerWidth, type Layout, type LayoutItem } from "react-grid-layout";
import { defaultPanel, newId, type DashboardDef, type PanelDef } from "./model";
import { PanelBody, panelSettings, type PanelContext } from "./PanelBody";
import { QueryBuilder } from "./QueryBuilder";
import { TimeControls, type ChartSettings } from "../timeControls";

interface Props {
  dashboard: DashboardDef;
  ctx: PanelContext;
  onChange: (dashboard: DashboardDef) => void;
  /**
   * Standard views are app furniture: rendered from code, never stored, and
   * not editable in place. Their panels keep the header time controls (those
   * are live view state, not edits) but lose add/edit/duplicate/remove and
   * the drag-resize grid.
   */
  readOnly?: boolean;
  /** Read-only views only: clone this view into a dashboard the user owns. */
  onCustomize?: () => void;
}

const COLS = 12;

function rectFor(dashboard: DashboardDef, panelId: string): LayoutItem {
  const existing = dashboard.layout.find((l) => l.i === panelId);
  if (existing) {
    return { ...existing, minW: 2, minH: 3 };
  }

  // Place new panels below everything else, half width.
  const bottom = dashboard.layout.reduce((max, l) => Math.max(max, l.y + l.h), 0);
  return { i: panelId, x: 0, y: bottom, w: 6, h: 7, minW: 2, minH: 3 };
}

/** A grid of query panels — one custom dashboard, or one standard view. */
export function DashboardView({ dashboard, ctx, onChange, readOnly, onCustomize }: Props) {
  const [editing, setEditing] = useState<PanelDef | null>(null);
  const { containerRef, width, mounted } = useContainerWidth();
  // Header window/span live only for as long as the view is open. They are a
  // way of looking at the data, not a property of it — and on a standard view
  // there is nowhere to persist them to anyway.
  const [chartSettings, setChartSettings] = useState<Record<string, ChartSettings>>({});

  const layout: Layout = dashboard.panels.map((p) => rectFor(dashboard, p.id));
  const timeCharts = dashboard.panels.filter((p) => p.viz === "line");
  const settingsFor = (panel: PanelDef) => chartSettings[panel.id] ?? panelSettings(panel);

  const applyToAll = (from: PanelDef) => {
    const source = settingsFor(from);
    setChartSettings((current) => {
      const next = { ...current };
      for (const panel of timeCharts) {
        next[panel.id] = { ...source };
      }
      return next;
    });
  };

  const savePanel = (panel: PanelDef) => {
    const exists = dashboard.panels.some((p) => p.id === panel.id);
    onChange({
      ...dashboard,
      panels: exists
        ? dashboard.panels.map((p) => (p.id === panel.id ? panel : p))
        : [...dashboard.panels, panel],
    });
    setEditing(null);
  };

  const removePanel = (id: string) =>
    onChange({
      ...dashboard,
      panels: dashboard.panels.filter((p) => p.id !== id),
      layout: dashboard.layout.filter((l) => l.i !== id),
    });

  const duplicatePanel = (panel: PanelDef) => {
    const copy = { ...panel, id: newId("p"), title: panel.title + " (copy)" };
    onChange({ ...dashboard, panels: [...dashboard.panels, copy] });
  };

  return (
    <div className="dashboard-custom" ref={containerRef as React.RefObject<HTMLDivElement>}>
      <div className="dash-toolbar">
        {readOnly ? (
          <>
            <button className="btn" onClick={onCustomize} title="Copy this view into your own dashboard, where you can edit it">
              Customize a copy
            </button>
            <span className="subtle">
              a standard view — the copy is yours to rearrange and edit
            </span>
          </>
        ) : (
          <>
            <button className="btn" onClick={() => setEditing(defaultPanel())}>
              + Add panel
            </button>
            <span className="subtle">drag panels by their title bar · resize from the corner</span>
          </>
        )}
      </div>

      {dashboard.panels.length === 0 && (
        <div className="empty">This dashboard is empty — add a panel to start.</div>
      )}

      {mounted && (
        <GridLayout
          width={width}
          layout={layout}
          gridConfig={{ cols: COLS, rowHeight: 36, margin: [10, 10] }}
          // The header controls live inside the drag handle, so they have to
          // be excluded by hand or every dropdown click starts a drag.
          dragConfig={{ enabled: !readOnly, handle: ".panel-drag", cancel: ".panel-controls" }}
          resizeConfig={{ enabled: !readOnly }}
          onLayoutChange={(next: Layout) =>
            !readOnly &&
            onChange({
              ...dashboard,
              layout: next.map((l) => ({ i: l.i, x: l.x, y: l.y, w: l.w, h: l.h })),
            })
          }
        >
          {dashboard.panels.map((panel) => (
            <div key={panel.id} className="panel grid-panel">
              <div className={"panel-title" + (readOnly ? "" : " panel-drag")}>
                <span className="panel-name">{panel.title}</span>
                <span className="panel-controls">
                  {panel.viz === "line" && (
                    <TimeControls
                      settings={settingsFor(panel)}
                      bucketSeconds={panel.bucketSeconds}
                      onChange={(next) =>
                        setChartSettings((current) => ({ ...current, [panel.id]: next }))
                      }
                      onApplyToAll={timeCharts.length > 1 ? () => applyToAll(panel) : undefined}
                    />
                  )}
                  {!readOnly && (
                    <span className="panel-actions">
                      <button className="mini-btn" title="Edit query" onClick={() => setEditing(panel)}>
                        edit
                      </button>
                      <button className="mini-btn" title="Duplicate" onClick={() => duplicatePanel(panel)}>
                        ⧉
                      </button>
                      <button className="mini-btn" title="Remove" onClick={() => removePanel(panel.id)}>
                        ×
                      </button>
                    </span>
                  )}
                </span>
              </div>
              <PanelBody panel={panel} ctx={ctx} settings={settingsFor(panel)} />
            </div>
          ))}
        </GridLayout>
      )}

      {editing && (
        <QueryBuilder panel={editing} onSave={savePanel} onCancel={() => setEditing(null)} />
      )}
    </div>
  );
}
