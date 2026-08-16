import type { ReactNode } from "react";
import { SERIES_COLORS, CHART_SERIES_LIMIT, OTHER_COLOR } from "./colors";

export interface TokenGalleryProps {
  className?: string;
}

const SURFACE_TOKENS: Array<{ name: string; var: string }> = [
  { name: "page", var: "--page" },
  { name: "surface", var: "--surface" },
  { name: "surface-2", var: "--surface-2" },
  { name: "hover", var: "--hover" },
  { name: "selected", var: "--selected" },
];

const INK_TOKENS: Array<{ name: string; var: string }> = [
  { name: "ink", var: "--ink" },
  { name: "ink-2", var: "--ink-2" },
  { name: "muted", var: "--muted" },
  { name: "muted-raised", var: "--muted-raised" },
];

const RULE_TOKENS: Array<{ name: string; var: string }> = [
  { name: "grid", var: "--grid" },
  { name: "border", var: "--border" },
  { name: "baseline", var: "--baseline" },
];

const ACCENT_TOKENS: Array<{ name: string; var: string }> = [
  { name: "accent", var: "--accent" },
  { name: "danger", var: "--danger" },
  { name: "live", var: "--live" },
  { name: "gold", var: "--gold" },
  { name: "sample", var: "--sample" },
];

const RADIUS_TOKENS: Array<{ name: string; var: string; used: string }> = [
  { name: "r-tiny", var: "--r-tiny", used: "colour chips, bar caps" },
  { name: "r-chip", var: "--r-chip", used: "badges, mini-buttons" },
  { name: "r-control", var: "--r-control", used: "inputs, selects, buttons" },
  { name: "r-inner", var: "--r-inner", used: "nested cards, breakdown rows" },
  { name: "r-card", var: "--r-card", used: "panels" },
  { name: "r-modal", var: "--r-modal", used: "modals" },
];

const TYPE_TOKENS: Array<{ name: string; var: string; used: string }> = [
  { name: "fs-micro", var: "--fs-micro", used: "rail headings, chart-adjacent meta" },
  { name: "fs-tiny", var: "--fs-tiny", used: "column headers, controls, chips" },
  { name: "fs-small", var: "--fs-small", used: "secondary meta" },
  { name: "fs-base", var: "--fs-base", used: "body and table cells" },
  { name: "fs-lg", var: "--fs-lg", used: "panel and section names" },
  { name: "fs-xl", var: "--fs-xl", used: "stat values" },
  { name: "fs-display", var: "--fs-display", used: "dashboard tile values" },
];

const WEIGHT_TOKENS: Array<{ name: string; var: string }> = [
  { name: "w-normal", var: "--w-normal" },
  { name: "w-medium", var: "--w-medium" },
  { name: "w-strong", var: "--w-strong" },
  { name: "w-bold", var: "--w-bold" },
  { name: "w-heavy", var: "--w-heavy" },
];

const SPACE_TOKENS: Array<{ name: string; var: string }> = [
  { name: "sp-1", var: "--sp-1" },
  { name: "sp-2", var: "--sp-2" },
  { name: "sp-3", var: "--sp-3" },
  { name: "sp-4", var: "--sp-4" },
  { name: "sp-5", var: "--sp-5" },
  { name: "sp-6", var: "--sp-6" },
  { name: "sp-7", var: "--sp-7" },
  { name: "sp-8", var: "--sp-8" },
];

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section style={{ marginBottom: 16 }}>
      <h3
        style={{
          margin: "0 0 8px",
          color: "var(--ink)",
          fontSize: "var(--fs-lg)",
          fontWeight: "var(--w-heavy)",
        }}
      >
        {title}
      </h3>
      {children}
    </section>
  );
}

function Swatch({ name, cssVar }: { name: string; cssVar: string }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 6, width: 128 }}>
      <div
        style={{
          height: 48,
          borderRadius: "var(--r-inner)",
          border: "1px solid var(--border)",
          background: `var(${cssVar})`,
        }}
      />
      <div style={{ fontSize: "var(--fs-tiny)", color: "var(--ink-2)", fontWeight: "var(--w-bold)" }}>
        {name}
      </div>
      <div style={{ fontSize: "var(--fs-micro)", color: "var(--muted)", fontFamily: "var(--font-mono)" }}>
        {cssVar}
      </div>
    </div>
  );
}

function HexSwatch({ hex, label }: { hex: string; label: string }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 6, width: 96 }}>
      <div
        style={{
          height: 40,
          borderRadius: "var(--r-tiny)",
          border: "1px solid var(--border)",
          background: hex,
        }}
      />
      <div style={{ fontSize: "var(--fs-micro)", color: "var(--muted)" }}>{label}</div>
      <div style={{ fontSize: "var(--fs-micro)", color: "var(--ink-2)", fontFamily: "var(--font-mono)" }}>
        {hex}
      </div>
    </div>
  );
}

/**
 * Every design token this kit ships, laid out so the system is browsable on
 * its own — the colour ramp, the 8-slot categorical chart palette, the
 * radius scale, the type scale and weight ladder, and the spacing scale.
 * Not a component an app would render in production; a reference page for
 * whoever is building with the kit. See
 * docs/architecture/adr-015-visual-language.md in the EQDeeps repo for the
 * reasoning behind each value.
 */
export function TokenGallery({ className }: TokenGalleryProps) {
  return (
    <div className={className} style={{ color: "var(--ink-2)" }}>
      <Section title="Surfaces">
        <div style={{ display: "flex", gap: 14, flexWrap: "wrap" }}>
          {SURFACE_TOKENS.map((t) => (
            <Swatch key={t.var} name={t.name} cssVar={t.var} />
          ))}
        </div>
      </Section>

      <Section title="Ink">
        <div style={{ display: "flex", gap: 14, flexWrap: "wrap" }}>
          {INK_TOKENS.map((t) => (
            <Swatch key={t.var} name={t.name} cssVar={t.var} />
          ))}
        </div>
      </Section>

      <Section title="Rules">
        <div style={{ display: "flex", gap: 14, flexWrap: "wrap" }}>
          {RULE_TOKENS.map((t) => (
            <Swatch key={t.var} name={t.name} cssVar={t.var} />
          ))}
        </div>
      </Section>

      <Section title="Accents">
        <div style={{ display: "flex", gap: 14, flexWrap: "wrap" }}>
          {ACCENT_TOKENS.map((t) => (
            <Swatch key={t.var} name={t.name} cssVar={t.var} />
          ))}
        </div>
      </Section>

      <Section title="Chart palette (8-slot categorical)">
        <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
          <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
            {SERIES_COLORS.slice(0, CHART_SERIES_LIMIT).map((hex, i) => (
              <HexSwatch key={hex} hex={hex} label={`series ${i + 1}`} />
            ))}
            <HexSwatch hex={OTHER_COLOR} label="other (past cap)" />
          </div>
          <p style={{ fontSize: "var(--fs-tiny)", color: "var(--muted)", margin: 0, maxWidth: 640 }}>
            A chart draws its series simultaneously, so these eight are validated against every
            pair, not just neighbours — a series colour is a 3:1 mark, never text. Charts fold
            anything past the {CHART_SERIES_LIMIT}th series into "Other".
          </p>
          <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
            {SERIES_COLORS.slice(CHART_SERIES_LIMIT).map((hex, i) => (
              <HexSwatch key={hex} hex={hex} label={`row tint ${i + 9}`} />
            ))}
          </div>
          <p style={{ fontSize: "var(--fs-tiny)", color: "var(--muted)", margin: 0, maxWidth: 640 }}>
            The second eight are the same hue families, stepped in lightness — table row tints
            only, never charts.
          </p>
        </div>
      </Section>

      <Section title="Radius scale">
        <div style={{ display: "flex", gap: 18, flexWrap: "wrap" }}>
          {RADIUS_TOKENS.map((t) => (
            <div key={t.var} style={{ display: "flex", flexDirection: "column", gap: 6, width: 128 }}>
              <div
                style={{
                  height: 48,
                  width: 48,
                  background: "var(--surface-2)",
                  border: "1px solid var(--border)",
                  borderRadius: `var(${t.var})`,
                }}
              />
              <div style={{ fontSize: "var(--fs-tiny)", color: "var(--ink-2)", fontWeight: "var(--w-bold)" }}>
                {t.name}
              </div>
              <div style={{ fontSize: "var(--fs-micro)", color: "var(--muted)" }}>{t.used}</div>
            </div>
          ))}
        </div>
      </Section>

      <Section title="Type scale">
        <div style={{ display: "flex", flexDirection: "column", gap: 5 }}>
          {TYPE_TOKENS.map((t) => (
            <div key={t.var} style={{ display: "flex", alignItems: "baseline", gap: 14 }}>
              <div style={{ width: 190, fontSize: "var(--fs-tiny)", color: "var(--muted)", flex: "none" }}>
                {t.name} · {t.used}
              </div>
              <div style={{ fontSize: `var(${t.var})`, color: "var(--ink)" }}>Aa Combat Log 123</div>
            </div>
          ))}
        </div>
      </Section>

      <Section title="Weight ladder">
        <div style={{ display: "flex", flexDirection: "column", gap: 5 }}>
          {WEIGHT_TOKENS.map((t) => (
            <div key={t.var} style={{ display: "flex", alignItems: "baseline", gap: 14 }}>
              <div style={{ width: 120, fontSize: "var(--fs-tiny)", color: "var(--muted)", flex: "none" }}>
                {t.name}
              </div>
              <div style={{ fontSize: "var(--fs-lg)", fontWeight: `var(${t.var})`, color: "var(--ink)" }}>
                Hierarchy is a weight, not a brightness
              </div>
            </div>
          ))}
        </div>
      </Section>

      <Section title="Spacing scale">
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          {SPACE_TOKENS.map((t) => (
            <div key={t.var} style={{ display: "flex", alignItems: "center", gap: 12 }}>
              <div style={{ width: 60, fontSize: "var(--fs-tiny)", color: "var(--muted)", flex: "none" }}>
                {t.name}
              </div>
              <div style={{ height: 14, width: `var(${t.var})`, background: "var(--accent)" }} />
              <div style={{ fontSize: "var(--fs-micro)", color: "var(--muted)", fontFamily: "var(--font-mono)" }}>
                {t.var}
              </div>
            </div>
          ))}
        </div>
      </Section>
    </div>
  );
}
