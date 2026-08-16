import type { Meta, StoryObj } from "@storybook/react";
import { MiniButton } from "./MiniButton";

const meta: Meta<typeof MiniButton> = {
  title: "Chip/MiniButton",
  component: MiniButton,
  args: { children: "export" },
};
export default meta;

type Story = StoryObj<typeof MiniButton>;

export const Default: Story = {};

export const Active: Story = {
  args: { active: true, children: "follow live" },
};

export const Disabled: Story = {
  args: { disabled: true, children: "delete" },
};

/** A row of toolbar actions, the pattern this control ships in. */
export const Row: Story = {
  render: () => (
    <div style={{ display: "flex", gap: 6 }}>
      <MiniButton>export</MiniButton>
      <MiniButton>import</MiniButton>
      <MiniButton active>follow live</MiniButton>
      <MiniButton disabled>delete</MiniButton>
    </div>
  ),
};
