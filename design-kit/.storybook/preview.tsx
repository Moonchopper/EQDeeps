import type { Preview } from "@storybook/react";
import "../src/styles/index.css";

/**
 * Every story renders inside .eqd-page — the kit's dark page ground, ink
 * colour and UI font. tokens.css deliberately doesn't style a bare `body`
 * (see the comment there), so Storybook's own preview iframe needs this
 * decorator to look like the app rather than a naked white/default page.
 */
const preview: Preview = {
  parameters: {
    backgrounds: { disable: true },
    layout: "centered",
  },
  decorators: [
    (Story) => (
      <div className="eqd-page" style={{ minHeight: "100vh", padding: 24 }}>
        <Story />
      </div>
    ),
  ],
};

export default preview;
