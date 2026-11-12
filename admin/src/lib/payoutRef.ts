/**
 * Telebirr merchant-portal transaction IDs as the manual payout routes take them (escrow-protocol.md 1.8.0,
 * 2.12.0, 2.18.0). The server checks them again; the page only avoids sending what it would refuse.
 */

/** 4 to 64 characters, a letter or digit first, then letters, digits or . _ : / - (the server's rule, ESCROW_037). */
export function isValidExternalRef(value: string): boolean {
  return /^[A-Za-z0-9][A-Za-z0-9._:/-]{3,63}$/.test(value.trim());
}

/** Two IDs are the same reference when they match trimmed and without case (the server's payout-ref# key). */
export function sameExternalRef(a: string, b: string): boolean {
  return a.trim().toUpperCase() === b.trim().toUpperCase();
}

/* FUP-05: the server names where a refused reference is used in a fixed form,
 * "... on escrow <escrowId>, leg <ref>.", so the page can say it in the admin's language. */
const USED_ON = /on escrow (\S+), leg (\S+?)\.$/;

export function referenceUsedOn(message: string | undefined): { escrowId: string; ref: string } | null {
  const match = USED_ON.exec(message ?? "");
  return match ? { escrowId: match[1], ref: match[2] } : null;
}

/** FUP-218: a reason for a reference correction, 10 to 500 characters once trimmed. */
export function isValidCorrectionReason(value: string): boolean {
  const length = value.trim().length;
  return length >= 10 && length <= 500;
}
