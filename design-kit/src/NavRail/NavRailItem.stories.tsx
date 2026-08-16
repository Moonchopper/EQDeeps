import type { Meta, StoryObj } from "@storybook/react";
import { IconChartLine, IconPlus } from "@tabler/icons-react";
import { NavRailItem } from "./NavRailItem";

const meta: Meta<typeof NavRailItem> = {
  title: "NavRail/NavRailItem",
  component: NavRailItem,
  args: { label: "Summary", icon: IconChartLine },
};
export default meta;

type Story = StoryObj<typeof NavRailItem>;

export const Default: Story = {
  render: (args) => (
    <div className="nav-rail" style={{ height: "auto" }}>
      <NavRailItem {...args} />
    </div>
  ),
};

export const Active: Story = {
  args: { active: true },
  render: (args) => (
    <div className="nav-rail" style={{ height: "auto" }}>
      <NavRailItem {...args} />
    </div>
  ),
};

export const Collapsed: Story = {
  args: { collapsed: true, description: "What happened in the fight" },
  render: (args) => (
    <div className="nav-rail collapsed" style={{ height: "auto" }}>
      <NavRailItem {...args} />
    </div>
  ),
};

export const AddVariant: Story = {
  args: { label: "New", icon: IconPlus, variant: "add" },
  render: (args) => (
    <div className="nav-rail" style={{ height: "auto" }}>
      <NavRailItem {...args} />
    </div>
  ),
};
