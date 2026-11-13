/**
 * FUP-19 (spec E9, delivery-verification-api.md 2.9.0): when the transaction page offers an admin the cancel of a
 * delivery after pickup. The server checks every rule again (the order's status and every escrow behind the
 * delivery); the page only avoids offering what it would refuse.
 */

/** Picked up and not delivered: the only deliveries an admin cancels. */
const CANCELLABLE_DELIVERY_STATUSES = new Set(["pickedUp", "inTransit"]);

/** Escrow statuses that hold and govern no money any more. */
const SETTLED_ESCROW_STATUSES = new Set(["completed", "cancelled", "refunded"]);

export function canAdminCancelDelivery(status: string): boolean {
  return CANCELLABLE_DELIVERY_STATUSES.has(status);
}

/** The money behind the delivery is decided: no escrow on the page, or one that is settled. */
export function deliveryMoneyDecided(escrowStatus: string | null | undefined): boolean {
  return !escrowStatus || SETTLED_ESCROW_STATUSES.has(escrowStatus);
}

/** A reason for the cancel, 10 to 500 characters once trimmed (the server's rule). */
export function isValidDeliveryCancelReason(value: string): boolean {
  const length = value.trim().length;
  return length >= 10 && length <= 500;
}
