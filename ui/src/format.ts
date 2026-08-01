// Display formatting per the domain conventions: K/M/B with one decimal,
// rates to one decimal place.

export function fmtNum(value: number): string {
  const abs = Math.abs(value);
  if (abs >= 1e9) return (value / 1e9).toFixed(1) + "B";
  if (abs >= 1e6) return (value / 1e6).toFixed(1) + "M";
  if (abs >= 1e3) return (value / 1e3).toFixed(1) + "K";
  return Math.round(value).toString();
}

export function fmtRate(value: number): string {
  return value.toFixed(1) + "%";
}

export function fmtClock(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

export function fmtDuration(beginIso: string, endIso: string): string {
  const seconds = Math.max(1, (new Date(endIso).getTime() - new Date(beginIso).getTime()) / 1000 + 1);
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return m > 0 ? `${m}m ${s.toString().padStart(2, "0")}s` : `${s}s`;
}

/** Categorical series slots (validated dark-surface palette; see ADR-006). */
export const SERIES_COLORS = [
  "#3987e5",
  "#d95926",
  "#199e70",
  "#c98500",
  "#d55181",
  "#008300",
  "#9085e9",
  "#e66767",
];

export const OTHER_COLOR = "#898781";
