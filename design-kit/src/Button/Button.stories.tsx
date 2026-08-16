import type { Meta, StoryObj } from "@storybook/react";
import { Button } from "./Button";

const meta: Meta<typeof Button> = {
  title: "Button/Button",
  component: Button,
  args: {
    children: "Save",
  },
};
export default meta;

type Story = StoryObj<typeof Button>;

export const Default: Story = {
  args: { children: "Cancel" },
};

export const Primary: Story = {
  args: { variant: "primary", children: "Save Dashboard" },
};

export const Disabled: Story = {
  args: { variant: "primary", children: "Save Dashboard", disabled: true },
};

/** The one committing action beside its exit — the pairing every modal ends on. */
export const ActionRow: Story = {
  render: () => (
    <div style={{ display: "flex", justifyContent: "flex-end", gap: 8 }}>
      <Button>Cancel</Button>
      <Button variant="primary">Save Dashboard</Button>
    </div>
  ),
};
