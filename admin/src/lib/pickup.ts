/**
 * FUP-287 (delivery-verification-api.md 2.8.0 P.2, order-lifecycle.md 2.16.0): the pickup judgement as the admin
 * transaction view reads it (`pickupEvidence` on GET /admin/transactions/{orderId}) and the rules the re-open form
 * checks before it sends. The server checks them again and decides which exits it takes (`reopenExits`).
 */

export type ReopenExit = "pending" | "handoff_confirmed";

export interface CheckInEvidence {
  receivedAt: string;
  capturedAt?: string;
  by?: string;
  latitude?: number;
  longitude?: number;
  accuracyM?: number;
  distanceKm?: number;
  distanceChecked: boolean;
  late: boolean;
  outOfRange: boolean;
}

export interface PickupEvidence {
  judgement?: string;
  judgedAt?: string;
  deadlineAt?: string;
  selfPickup: boolean;
  pointLatitude?: number;
  pointLongitude?: number;
  checkIns?: Record<string, CheckInEvidence>;
  reopenExits: ReopenExit[];
  /** FUP-330: the exits the trucker no-show re-open takes (POST /admin/orders/{orderId}/reopen-no-show). */
  noShowReopenExits?: ReopenExit[];
  lastReopen?: { at?: string; exit?: string; fromJudgement?: string; deadlineAt?: string };
}

/** Which re-open: FUP-287's of a judgement that left the order accepted, or FUP-330's of a trucker no-show. */
export type ReopenKind = "judgement" | "noShow";

/** The route action of each re-open. */
export const REOPEN_ACTIONS: Record<ReopenKind, "reopen-pickup" | "reopen-no-show"> = {
  judgement: "reopen-pickup",
  noShow: "reopen-no-show",
};

/** What a trucker no-show re-open did with the trucker's NO_SHOW event (`noShowEvent` of the answer). */
export const NO_SHOW_EVENTS = ["excused", "already_excused", "not_recorded", "not_counted", "upheld", "off", "failed"] as const;

/** The two check-in kinds of the judgement, in the order they are shown. */
export const CHECK_IN_KINDS = ["arrived_pickup", "crops_ready"] as const;

/** The judged outcomes a no-show moved on (refused by the server; the page explains instead). */
export const NO_SHOW_JUDGEMENTS = ["trucker_no_show", "farmer_no_show", "buyer_no_show"];

/** The new deadline of a `pending` re-open, in hours from now; the server takes 1 to 168. */
export const DEADLINE_HOURS = [24, 48, 72] as const;

/** A re-open reason, 10 to 500 characters once trimmed (the server's rule). */
export function isValidReopenReason(value: string): boolean {
  const length = value.trim().length;
  return length >= 10 && length <= 500;
}

/** "8.76010, 38.97850 ±35 m": a check-in's fix with its accuracy, or empty without one. */
export function formatFix(entry: Pick<CheckInEvidence, "latitude" | "longitude" | "accuracyM">): string {
  if (entry.latitude == null || entry.longitude == null) return "";
  const point = `${Number(entry.latitude).toFixed(5)}, ${Number(entry.longitude).toFixed(5)}`;
  return entry.accuracyM != null ? `${point} ±${Math.round(Number(entry.accuracyM))} m` : point;
}

/** "0.87 km": a check-in's distance to the farm point, or empty. */
export function formatKm(km: number | undefined): string {
  return km == null ? "" : `${Number(km).toFixed(2)} km`;
}
