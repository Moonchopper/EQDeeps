import type { Meta, StoryObj } from "@storybook/react";
import { FormRow } from "./FormRow";
import { TextInput } from "./TextInput";
import { Select } from "./Select";

const meta: Meta<typeof FormRow> = {
  title: "Form/FormRow",
  component: FormRow,
};
export default meta;

type Story = StoryObj<typeof FormRow>;

export const Default: Story = {
  render: () => (
    <div style={{ width: 320 }}>
      <FormRow label="Name">
        <TextInput defaultValue="Raid Overview" />
      </FormRow>
    </div>
  ),
};

/** A few rows stacked — every label still lines up without a shared grid ancestor. */
export const Stack: Story = {
  render: () => (
    <div style={{ width: 340, display: "flex", flexDirection: "column", gap: 10 }}>
      <FormRow label="Name">
        <TextInput defaultValue="Raid Overview" />
      </FormRow>
      <FormRow label="Metric">
        <Select
          options={[
            { value: "damage", label: "Damage" },
            { value: "healing", label: "Healing" },
          ]}
          defaultValue="damage"
        />
      </FormRow>
      <FormRow label="Limit">
        <TextInput numeric defaultValue="10" />
      </FormRow>
    </div>
  ),
};
