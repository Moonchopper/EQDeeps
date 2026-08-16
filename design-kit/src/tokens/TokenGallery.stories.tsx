import type { Meta, StoryObj } from "@storybook/react";
import { TokenGallery } from "./TokenGallery";

const meta: Meta<typeof TokenGallery> = {
  title: "Tokens/TokenGallery",
  component: TokenGallery,
};
export default meta;

type Story = StoryObj<typeof TokenGallery>;

/** Every token in the kit, browsable on one page. */
export const Default: Story = {};
