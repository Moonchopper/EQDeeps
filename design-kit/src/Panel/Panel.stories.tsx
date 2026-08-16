import type { Meta, StoryObj } from "@storybook/react";
import { Panel } from "./Panel";

const meta: Meta<typeof Panel> = {
  title: "Panel/Panel",
  component: Panel,
  args: {
    title: "Damage Done",
  },
};
export default meta;

type Story = StoryObj<typeof Panel>;

export const Default: Story = {
  render: (args) => (
    <div style={{ width: 360 }}>
      <Panel {...args}>
        <p style={{ margin: 0 }}>
          214,880 total damage across 3m 42s of combat — 971 DPS, led by Aeliana at 41% of the raid
          total.
        </p>
      </Panel>
    </div>
  ),
};

export const NoTitle: Story = {
  args: { title: undefined },
  render: (args) => (
    <div style={{ width: 320 }}>
      <Panel {...args}>
        <p style={{ margin: 0 }}>A bare surface — no header rule, just the elevated ground.</p>
      </Panel>
    </div>
  ),
};

export const WithTitleActions: Story = {
  render: (args) => (
    <div style={{ width: 380 }}>
      <Panel
        {...args}
        titleActions={
          <span style={{ fontSize: "var(--fs-tiny)", color: "var(--muted)" }}>Last 15 minutes</span>
        }
      >
        <p style={{ margin: 0 }}>A time-range note riding the title bar, beside the panel's name.</p>
      </Panel>
    </div>
  ),
};

/** Edge-to-edge content — a table doesn't want the default body padding. */
export const EdgeToEdgeContent: Story = {
  args: { title: "Recent Deaths" },
  render: (args) => (
    <div style={{ width: 380 }}>
      <Panel {...args}>
        <div style={{ margin: "calc(var(--sp-5) * -1)" }}>
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th className="num">Fatal Hit</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Grimjaw</td>
                <td className="num">4,120</td>
              </tr>
              <tr>
                <td>Sylvara</td>
                <td className="num">3,860</td>
              </tr>
            </tbody>
          </table>
        </div>
      </Panel>
    </div>
  ),
};
