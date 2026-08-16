import { useState } from "react";
import type { Meta, StoryObj } from "@storybook/react";
import { SearchInput } from "./SearchInput";

const meta: Meta<typeof SearchInput> = {
  title: "Form/SearchInput",
  component: SearchInput,
};
export default meta;

type Story = StoryObj<typeof SearchInput>;

function Interactive() {
  const [value, setValue] = useState("");
  return (
    <div style={{ width: 260 }}>
      <SearchInput value={value} onChange={setValue} count={{ shown: 12, total: 340 }} />
    </div>
  );
}

export const Default: Story = {
  render: () => <Interactive />,
};

export const WithResultsCount: Story = {
  render: () => (
    <div style={{ width: 260 }}>
      <SearchInput value="grim" onChange={() => {}} count={{ shown: 1, total: 340 }} />
    </div>
  ),
};
