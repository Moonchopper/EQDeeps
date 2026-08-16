import type { ComponentType } from "react";

/** The icon contract every nav-rail entry glyph satisfies — Tabler's shape, so any Tabler icon drops in unchanged. */
export type RailIcon = ComponentType<{ size?: number | string; stroke?: number | string; className?: string }>;
