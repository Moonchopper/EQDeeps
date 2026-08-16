import type { Meta, StoryObj } from "@storybook/react";
import { Modal } from "./Modal";
import { Button } from "../Button/Button";
import { FormRow } from "../Form/FormRow";
import { TextInput } from "../Form/TextInput";
import { Select } from "../Form/Select";
import { RadioRow } from "../Form/RadioRow";

const meta: Meta<typeof Modal> = {
  title: "Modal/Modal",
  component: Modal,
  parameters: {
    // The backdrop is position:fixed and covers the viewport by design — let
    // it render at full bleed rather than clipped inside a small card cell.
    layout: "fullscreen",
  },
};
export default meta;

type Story = StoryObj<typeof Modal>;

export const Default: Story = {
  render: () => (
    <Modal
      title="Delete Dashboard"
      actions={
        <>
          <Button>Cancel</Button>
          <Button variant="primary">Delete</Button>
        </>
      }
    >
      <p style={{ margin: 0, color: "var(--ink-2)" }}>
        "Raid Overview" and its 6 panels will be removed. This can't be undone.
      </p>
    </Modal>
  ),
};

/** A dialog built from the kit's own form controls — the New Query pattern. */
export const FormModal: Story = {
  render: () => (
    <Modal
      title="New Query"
      actions={
        <>
          <Button>Cancel</Button>
          <Button variant="primary">Add to Dashboard</Button>
        </>
      }
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
        <FormRow label="Name">
          <TextInput defaultValue="Top Healers" />
        </FormRow>
        <FormRow label="Metric">
          <Select
            options={[
              { value: "healing", label: "Healing" },
              { value: "damage", label: "Damage" },
            ]}
            defaultValue="healing"
          />
        </FormRow>
        <FormRow label="Shown as">
          <RadioRow
            name="viz"
            options={[
              { value: "table", label: "Table" },
              { value: "chart", label: "Chart" },
            ]}
            value="table"
          />
        </FormRow>
      </div>
    </Modal>
  ),
};
