import type { Meta, StoryObj } from "@storybook/react";
import { FormRow } from "./FormRow";
import { Select } from "./Select";

const meta: Meta<typeof Select> = {
  title: "Form/Select",
  component: Select,
  args: {
    options: [
      { value: "damage", label: "Damage" },
      { value: "healing", label: "Healing" },
      { value: "threat", label: "Threat" },
    ],
    defaultValue: "damage",
  },
};
export default meta;

type Story = StoryObj<typeof Select>;

export const Default: Story = {
  render: (args) => (
    <div style={{ width: 260 }}>
      <FormRow label="Metric">
        <Select {...args} />
      </FormRow>
    </div>
  ),
};

export const ManyOptions: Story = {
  args: {
    options: [
      { value: "1", label: "Last 5 minutes" },
      { value: "2", label: "Last 15 minutes" },
      { value: "3", label: "Last 30 minutes" },
      { value: "4", label: "Last hour" },
      { value: "5", label: "Last 6 hours" },
      { value: "6", label: "This session" },
    ],
    defaultValue: "2",
  },
  render: (args) => (
    <div style={{ width: 260 }}>
      <FormRow label="Range">
        <Select {...args} />
      </FormRow>
    </div>
  ),
};
