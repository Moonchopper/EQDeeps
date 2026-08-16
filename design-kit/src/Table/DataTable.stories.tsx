import { useState } from "react";
import type { Meta, StoryObj } from "@storybook/react";
import { DataTable, type DataTableColumn } from "./DataTable";
import { SERIES_COLORS } from "../tokens/colors";

interface Row {
  id: string;
  name: string;
  className: string;
  damage: number;
  dps: number;
  share: number;
  color: string;
  pet?: { id: string; name: string; damage: number; dps: number };
}

const ROWS: Row[] = [
  { id: "aeliana", name: "Aeliana", className: "Wizard", damage: 88420, dps: 412, share: 41, color: SERIES_COLORS[0] },
  {
    id: "grimjaw",
    name: "Grimjaw",
    className: "Necromancer",
    damage: 51200,
    dps: 238,
    share: 24,
    color: SERIES_COLORS[1],
    pet: { id: "grimjaw-pet", name: "Thornclaw (pet)", damage: 18300, dps: 84 },
  },
  { id: "sylvara", name: "Sylvara", className: "Ranger", damage: 43860, dps: 204, share: 20, color: SERIES_COLORS[2] },
  { id: "brannoc", name: "Brannoc", className: "Warrior", damage: 31400, dps: 146, share: 15, color: SERIES_COLORS[3] },
];

const COLUMNS: DataTableColumn<Row>[] = [
  {
    key: "name",
    header: "Name",
    render: (r) => (
      <span style={{ display: "inline-flex", alignItems: "center" }}>
        <span className="color-chip" style={{ background: r.color }} />
        {r.name}
      </span>
    ),
  },
  { key: "class", header: "Class", render: (r) => r.className },
  { key: "damage", header: "Damage", align: "right", sortable: true, render: (r) => r.damage.toLocaleString() },
  { key: "dps", header: "DPS", align: "right", sortable: true, render: (r) => r.dps.toLocaleString() },
  { key: "share", header: "Share", align: "right", render: (r) => `${r.share}%` },
];

const meta: Meta<typeof DataTable> = {
  title: "Table/DataTable",
  component: DataTable,
};
export default meta;

type Story = StoryObj<typeof DataTable>;

function SortableTable() {
  const [sortKey, setSortKey] = useState("damage");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("desc");
  const sorted = [...ROWS].sort((a, b) => {
    const av = (a as any)[sortKey] ?? 0;
    const bv = (b as any)[sortKey] ?? 0;
    return sortDir === "asc" ? av - bv : bv - av;
  });
  return (
    <DataTable
      columns={COLUMNS}
      rows={sorted}
      rowKey={(r) => r.id}
      sortKey={sortKey}
      sortDir={sortDir}
      onSort={(key) => {
        if (key === sortKey) setSortDir((d) => (d === "asc" ? "desc" : "asc"));
        else {
          setSortKey(key);
          setSortDir("desc");
        }
      }}
    />
  );
}

export const Default: Story = {
  render: () => (
    <div style={{ width: 460 }}>
      <SortableTable />
    </div>
  ),
};

export const WithChildRows: Story = {
  render: () => (
    <div style={{ width: 460 }}>
      <DataTable
        columns={COLUMNS}
        rows={ROWS}
        rowKey={(r) => r.id}
        childRows={(r) =>
          r.pet
            ? [
                {
                  id: r.pet.id,
                  name: r.pet.name,
                  className: "",
                  damage: r.pet.damage,
                  dps: r.pet.dps,
                  share: 0,
                  color: r.color,
                },
              ]
            : undefined
        }
      />
    </div>
  ),
};

export const SelectedAndLinked: Story = {
  render: () => (
    <div style={{ width: 460 }}>
      <DataTable
        columns={COLUMNS}
        rows={ROWS}
        rowKey={(r) => r.id}
        selectedKey="aeliana"
        linkedKeys={["grimjaw"]}
        onRowClick={() => {}}
      />
    </div>
  ),
};
