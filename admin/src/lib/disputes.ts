/**
 * What the Disputes list, the nav and the dispute page share: which row counts as open, and the
 * title of a known dispute reason.
 */

type Row = Record<string, unknown>;
type Translate = (key: string) => string;

/** Open as the Disputes list's Open tab has it: a row still waiting for a verdict (not `resolved`). */
export function isOpenDispute(row: Row | null | undefined): boolean {
  return !!row && row.status !== "resolved";
}

/** How many rows of the Disputes list are open. */
export function countOpenDisputes(rows: unknown[]): number {
  return rows.filter((row) => isOpenDispute(row as Row | null | undefined)).length;
}

/**
 * The title of a known reason code (`disputes.reasonTitle_<code>`), or null for free text, a code
 * with no label yet, and `other`, which names nothing.
 */
export function disputeReasonTitle(reason: unknown, t: Translate): string | null {
  if (typeof reason !== "string" || !/^[a-z][a-z_]*$/.test(reason)) return null;
  const key = `disputes.reasonTitle_${reason}`;
  const label = t(key);
  return label === key ? null : label;
}
