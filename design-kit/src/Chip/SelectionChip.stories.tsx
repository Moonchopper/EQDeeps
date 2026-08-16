import type { Meta, StoryObj } from "@storybook/react";
import { SelectionChip } from "./SelectionChip";

const meta: Meta<typeof SelectionChip> = {
  title: "Chip/SelectionChip",
  component: SelectionChip,
  args: {
    name: "Aeliana",
    color: "#e56386",
  },
};
export default meta;

type Story = StoryObj<typeof SelectionChip>;

export const Default: Story = {
  args: { onTogglePin: () => {}, onClear: () => {} },
};

export const Pinned: Story = {
  args: { pinned: true, onTogglePin: () => {}, onClear: () => {} },
};

export const Compact: Story = {
  args: { compact: true, onTogglePin: () => {}, onClear: () => {} },
};

export const CompactPinned: Story = {
  args: { compact: true, pinned: true, onTogglePin: () => {}, onClear: () => {} },
};
