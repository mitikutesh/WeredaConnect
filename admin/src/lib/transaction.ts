/**
 * Shared vocabulary for the transaction monitor: status/stage → badge colour,
 * plus label lookups that fall back to the raw machine value when a translation
 * key is missing (so a new server-side status never renders blank).
 *
 * Badge variants mirror the mobile StatusChip palette (see Badge.tsx).
 */

type Variant = "default" | "success" | "warning" | "danger" | "info" | "transit";

/** Order lifecycle status → badge colour (OrderDto state machine). */
export const orderStatusVariant: Record<string, Variant> = {
  created: "default",
  pending_payment: "warning",
  payment_submitted: "info",
  payment_failed: "danger",
  funded: "info",
  accepted: "info",
  pickup_confirmed: "info",
  in_transit: "transit",
  delivered: "success",
  disbursing: "info",
  settled: "success",
  cancelled: "default",
  rejected: "default",
  disputed: "danger",
  dispute_resolved: "success",
};

/**
 * Collapse an order status into its coarse lifecycle stage. Mirrors the server
 * DeriveStage (AdminFunction), kept in sync so the detail header matches the
 * list row. Used where the API returns only the raw status (transaction detail).
 * ESC-09: the order follows the money, so disbursing (paying out, possibly days
 * in Manual Payouts) and dispute_resolved (decided) have stages of their own,
 * and rejected ends like a cancel.
 */
export function deriveStage(status: string): string {
  switch (status) {
    case "created":
    case "pending_payment":
      return "awaiting_payment";
    case "payment_submitted":
    case "funded":
    case "accepted":
    case "pickup_confirmed":
    case "in_transit":
      return "in_progress";
    case "delivered":
      return "delivered";
    case "disbursing":
      return "paying_out";
    case "settled":
      return "completed";
    case "cancelled":
    case "rejected":
      return "cancelled";
    case "dispute_resolved":
      return "resolved";
    case "payment_failed":
    case "disputed":
      return "problem";
    default:
      return "unknown";
  }
}

/** Coarse lifecycle stage → badge colour (server DeriveStage). */
export const stageVariant: Record<string, Variant> = {
  awaiting_payment: "warning",
  in_progress: "info",
  delivered: "success",
  paying_out: "info",
  completed: "success",
  cancelled: "default",
  resolved: "success",
  problem: "danger",
  unknown: "default",
};

/** Escrow/payment status → badge colour. */
export const escrowStatusVariant: Record<string, Variant> = {
  initiated: "info",
  escrowed: "info",
  released: "success",
  completed: "success",
  disputed: "danger",
  dispute_resolved: "success",
  refunded: "warning",
  cancelled: "default",
  // FRD-03: frozen for fraud review until an admin releases or refunds it.
  held: "warning",
};

/** Delivery status → badge colour. */
export const deliveryStatusVariant: Record<string, Variant> = {
  pending: "default",
  pickedUp: "info",
  inTransit: "transit",
  delivered: "success",
  disputed: "danger",
  cancelled: "default",
};

/** Which timeline leg an event belongs to → badge colour. */
export const timelineEntityVariant: Record<string, Variant> = {
  order: "info",
  delivery: "transit",
  escrow: "success",
};

type Translate = (key: string) => string;

/** Look up a translation, falling back to the raw value if the key is missing. */
function labelOrRaw(t: Translate, key: string, raw: string): string {
  const label = t(key);
  return label === key ? raw : label;
}

export const orderStatusLabel = (status: string, t: Translate) =>
  labelOrRaw(t, `transactions.status_${status}`, status);

export const stageLabel = (stage: string, t: Translate) =>
  labelOrRaw(t, `transactions.stage_${stage}`, stage);

export const escrowStatusLabel = (status: string, t: Translate) =>
  labelOrRaw(t, `transactions.escrow_${status}`, status);

export const deliveryStatusLabel = (status: string, t: Translate) =>
  labelOrRaw(t, `transactions.delivery_${status}`, status);

export const timelineActionLabel = (action: string, t: Translate) =>
  labelOrRaw(t, `transactions.action_${action}`, action);

/** ESC-06: why an order waits in the admin stuck-order queue (hold_cap). */
export const adminQueueLabel = (reason: string, t: Translate) =>
  labelOrRaw(t, `transactions.adminQueue_${reason}`, reason);
