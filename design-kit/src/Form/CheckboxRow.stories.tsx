import { useState } from "react";
import type { Meta, StoryObj } from "@storybook/react";
import { CheckboxRow } from "./CheckboxRow";

const meta: Meta<typeof CheckboxRow> = {
  title: "Form/CheckboxRow",
  component: CheckboxRow,
};
export default meta;

type Story = StoryObj<typeof CheckboxRow>;

export const Unchecked: Story = {
  args: { label: "Include damage shields", checked: false },
};

export const Checked: Story = {
  args: { label: "Include damage shields", checked: true },
};

/** Several checkboxes arranged in the kit's filter grid (`.check-grid`). */
export const Grid: Story = {
  render: () => {
    const [values, setValues] = useState<Record<string, boolean>>({
      bane: true,
      shield: false,
      headshot: true,
      backstab: false,
      riposte: false,
      crit: true,
    });
    const labels: [string, string][] = [
      ["bane", "Bane damage"],
      ["shield", "Damage shields"],
      ["headshot", "Headshots"],
      ["backstab", "Backstabs"],
      ["riposte", "Ripostes"],
      ["crit", "Critical hits"],
    ];
    return (
      <div className="check-grid" style={{ width: 420 }}>
        {labels.map(([key, label]) => (
          <CheckboxRow
            key={key}
            label={label}
            checked={values[key]}
            onChange={(v) => setValues((prev) => ({ ...prev, [key]: v }))}
          />
        ))}
      </div>
    );
  },
};
