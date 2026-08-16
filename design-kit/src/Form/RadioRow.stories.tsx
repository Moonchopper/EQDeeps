import { useState } from "react";
import type { Meta, StoryObj } from "@storybook/react";
import { RadioRow } from "./RadioRow";

const OPTIONS = [
  { value: "damage", label: "Damage" },
  { value: "healing", label: "Healing" },
  { value: "threat", label: "Threat" },
];

const meta: Meta<typeof RadioRow> = {
  title: "Form/RadioRow",
  component: RadioRow,
  args: { name: "metric", options: OPTIONS },
};
export default meta;

type Story = StoryObj<typeof RadioRow>;

function Interactive() {
  const [value, setValue] = useState("healing");
  return <RadioRow name="metric" options={OPTIONS} value={value} onChange={setValue} />;
}

export const Default: Story = {
  render: () => <Interactive />,
};

export const Disabled: Story = {
  args: { value: "damage", disabled: true },
};
