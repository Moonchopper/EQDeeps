/** Joins truthy class-name fragments with a space. No dependency for a job this small. */
export function cx(...parts: Array<string | false | null | undefined>): string {
  return parts.filter(Boolean).join(" ");
}
