import type { Meta, StoryObj } from "@storybook/react";
import { Panel } from "../Panel/Panel";
import { EmptyState } from "./EmptyState";

const meta: Meta<typeof EmptyState> = {
  title: "EmptyState/EmptyState",
  component: EmptyState,
};
export default meta;

type Story = StoryObj<typeof EmptyState>;

export const Empty: Story = {
  args: { variant: "empty", message: "No combat recorded in this range." },
  render: (args) => (
    <div style={{ width: 340 }}>
      <Panel title="Damage Done">
        <EmptyState {...args} />
      </Panel>
    </div>
  ),
};

export const Loading: Story = {
  args: { variant: "loading" },
  render: (args) => (
    <div style={{ width: 340 }}>
      <Panel title="Damage Done">
        <EmptyState {...args} />
      </Panel>
    </div>
  ),
};

export const Error: Story = {
  args: { variant: "error", message: "Couldn't reach the server. Retry in a moment." },
  render: (args) => (
    <div style={{ width: 340 }}>
      <Panel title="Damage Done">
        <EmptyState {...args} />
      </Panel>
    </div>
  ),
};
