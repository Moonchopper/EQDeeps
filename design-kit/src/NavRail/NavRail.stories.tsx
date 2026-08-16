import { useState } from "react";
import type { Meta, StoryObj } from "@storybook/react";
import {
  IconChartLine,
  IconDiamond,
  IconFileText,
  IconHeart,
  IconMap2,
  IconSettings,
  IconShield,
  IconSkull,
  IconTargetArrow,
  IconTrendingUp,
} from "@tabler/icons-react";
import { NavRail, type NavRailGroupData } from "./NavRail";

const GROUPS: NavRailGroupData[] = [
  {
    key: "combat",
    heading: "Combat",
    entries: [
      { key: "summary", label: "Summary", icon: IconChartLine },
      { key: "healing", label: "Healing", icon: IconHeart },
      { key: "tanking", label: "Tanking", icon: IconShield },
      { key: "incoming", label: "Incoming", icon: IconTargetArrow },
    ],
  },
  {
    key: "character",
    heading: "Character",
    entries: [
      { key: "experience", label: "Experience", icon: IconTrendingUp },
      { key: "loot", label: "Loot", icon: IconDiamond },
    ],
  },
  {
    key: "world",
    heading: "World",
    entries: [
      { key: "mobs", label: "Mobs", icon: IconSkull },
      { key: "map", label: "Map", icon: IconMap2 },
    ],
  },
];

const meta: Meta<typeof NavRail> = {
  title: "NavRail/NavRail",
  component: NavRail,
};
export default meta;

type Story = StoryObj<typeof NavRail>;

function Interactive({ startCollapsed = false }: { startCollapsed?: boolean }) {
  const [active, setActive] = useState("tanking");
  const [collapsed, setCollapsed] = useState(startCollapsed);
  return (
    <div style={{ height: 420 }}>
      <NavRail
        groups={GROUPS}
        activeKey={active}
        collapsed={collapsed}
        onSelect={setActive}
        onToggleCollapsed={() => setCollapsed((c) => !c)}
        footer={
          <button className="rail-tab">
            <IconSettings size={16} stroke={1.75} className="rail-icon" />
            <span className="rail-label">Settings</span>
          </button>
        }
      />
    </div>
  );
}

export const Default: Story = {
  render: () => <Interactive />,
};

export const Collapsed: Story = {
  render: () => <Interactive startCollapsed />,
};

/** The utility cluster at the foot — Logs and Settings, apart from where you are in the rail. */
export const WithFooter: Story = {
  render: () => (
    <div style={{ height: 420 }}>
      <NavRail
        groups={GROUPS}
        activeKey="summary"
        footer={
          <>
            <button className="rail-tab">
              <IconFileText size={16} stroke={1.75} className="rail-icon" />
              <span className="rail-label">Logs</span>
            </button>
            <button className="rail-tab">
              <IconSettings size={16} stroke={1.75} className="rail-icon" />
              <span className="rail-label">Settings</span>
            </button>
          </>
        }
      />
    </div>
  ),
};
