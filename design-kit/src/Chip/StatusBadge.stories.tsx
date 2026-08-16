import type { Meta, StoryObj } from "@storybook/react";
import { StatusBadge } from "./StatusBadge";

const meta: Meta<typeof StatusBadge> = {
  title: "Chip/StatusBadge",
  component: StatusBadge,
};
export default meta;

type Story = StoryObj<typeof StatusBadge>;

export const Sample: Story = {
  args: { variant: "sample", children: "Sample" },
};

export const Update: Story = {
  args: { variant: "update", children: "v0.13.0 available" },
};

export const UpdateQuiet: Story = {
  args: { variant: "update-quiet", children: "Up to date" },
};

export const UpdateProgress: Story = {
  args: { variant: "update-progress", progress: 62, children: "Downloading… 62%" },
};

export const UpdateFailed: Story = {
  args: { variant: "update-failed", children: "Update failed — retry" },
};

export const Live: Story = {
  args: { variant: "live", children: "Live" },
};
