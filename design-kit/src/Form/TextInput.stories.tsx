import type { Meta, StoryObj } from "@storybook/react";
import { FormRow } from "./FormRow";
import { TextInput } from "./TextInput";

const meta: Meta<typeof TextInput> = {
  title: "Form/TextInput",
  component: TextInput,
};
export default meta;

type Story = StoryObj<typeof TextInput>;

export const Default: Story = {
  render: () => (
    <div style={{ width: 300 }}>
      <FormRow label="Name">
        <TextInput defaultValue="Raid Overview" />
      </FormRow>
    </div>
  ),
};

/** The narrow numeric treatment — a filter value, not a full-width form field. */
export const Numeric: Story = {
  render: () => <TextInput numeric defaultValue="10" />,
};

export const Disabled: Story = {
  render: () => (
    <div style={{ width: 300 }}>
      <FormRow label="Name">
        <TextInput defaultValue="Locked" disabled />
      </FormRow>
    </div>
  ),
};
