import type { Meta, StoryObj } from "@storybook/react";
import { IconHeart, IconShield, IconTargetArrow } from "@tabler/icons-react";
import { NavRailGroup } from "./NavRailGroup";
import { NavRailItem } from "./NavRailItem";

const meta: Meta<typeof NavRailGroup> = {
  title: "NavRail/NavRailGroup",
  component: NavRailGroup,
  args: { heading: "Combat" },
};
export default meta;

type Story = StoryObj<typeof NavRailGroup>;

export const Default: Story = {
  render: (args) => (
    <div className="nav-rail" style={{ height: "auto" }}>
      <NavRailGroup {...args}>
        <NavRailItem label="Healing" icon={IconHeart} />
        <NavRailItem label="Tanking" icon={IconShield} active />
        <NavRailItem label="Incoming" icon={IconTargetArrow} />
      </NavRailGroup>
    </div>
  ),
};

export const Collapsed: Story = {
  args: { collapsed: true },
  render: (args) => (
    <div className="nav-rail collapsed" style={{ height: "auto" }}>
      <NavRailGroup {...args}>
        <NavRailItem label="Healing" icon={IconHeart} collapsed />
        <NavRailItem label="Tanking" icon={IconShield} active collapsed />
        <NavRailItem label="Incoming" icon={IconTargetArrow} collapsed />
      </NavRailGroup>
    </div>
  ),
};
