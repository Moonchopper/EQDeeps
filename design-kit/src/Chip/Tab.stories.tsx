import type { Meta, StoryObj } from "@storybook/react";
import { Tab } from "./Tab";

const meta: Meta<typeof Tab> = {
  title: "Chip/Tab",
  component: Tab,
  args: { children: "Summary" },
};
export default meta;

type Story = StoryObj<typeof Tab>;

export const Default: Story = {};

export const Selected: Story = {
  args: { active: true },
};

export const Small: Story = {
  args: { size: "small", children: "Compact" },
};

/** A tab strip — one selected, the rest quiet. */
export const Strip: Story = {
  render: () => (
    <div className="tabs">
      <Tab active>Summary</Tab>
      <Tab>Healing</Tab>
      <Tab>Tanking</Tab>
      <Tab>Loot</Tab>
    </div>
  ),
};
