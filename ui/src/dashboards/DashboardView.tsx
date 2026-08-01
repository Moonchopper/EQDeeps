import React, { useState } from "react";
import { GridLayout, useContainerWidth, type Layout, type LayoutItem } from "react-grid-layout";
import { defaultPanel, newId, type DashboardDef, type PanelDef } from "./model";
import { PanelBody, type PanelContext } from "./PanelBody";
import { QueryBuilder } from "./QueryBuilder";

interface Props {
  dashboard: DashboardDef;
  ctx: PanelContext;
  onChange: (dashboard: DashboardDef) => void;
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

/** One custom dashboard: a drag/resize grid of query panels. */
export function DashboardView({ dashboard, ctx, onChange }: Props) {
  const [editing, setEditing] = useState<PanelDef | null>(null);
  const { containerRef, width, mounted } = useContainerWidth();

  const layout: Layout = dashboard.panels.map((p) => rectFor(dashboard, p.id));

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
        <button className="btn" onClick={() => setEditing(defaultPanel())}>
          + Add panel
        </button>
        <span className="subtle">drag panels by their title bar · resize from the corner</span>
      </div>

      {dashboard.panels.length === 0 && (
        <div className="empty">This dashboard is empty — add a panel to start.</div>
      )}

      {mounted && (
        <GridLayout
          width={width}
          layout={layout}
          gridConfig={{ cols: COLS, rowHeight: 36, margin: [10, 10] }}
          dragConfig={{ handle: ".panel-drag" }}
          onLayoutChange={(next: Layout) =>
            onChange({
              ...dashboard,
              layout: next.map((l) => ({ i: l.i, x: l.x, y: l.y, w: l.w, h: l.h })),
            })
          }
        >
          {dashboard.panels.map((panel) => (
            <div key={panel.id} className="panel grid-panel">
              <div className="panel-title panel-drag">
                <span className="panel-name">{panel.title}</span>
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
              </div>
              <PanelBody panel={panel} ctx={ctx} />
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
