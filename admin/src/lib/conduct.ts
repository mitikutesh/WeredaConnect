/* GCR-07 (reputation-api.md 1.8, good-conduct-rewards.md sections 4, 5 and 7): the conduct stamp of each party of an
 * order as the admin transaction view reads it (`conduct` on GET /admin/transactions/{orderId}), and the labels of its
 * steps, results and reasons. The server omits a field with no value, so every field that can be absent is optional.
 * A value the admin has no label for yet (a newer server) reads as the raw value, never blank. */

export type ConductResult = "earned" | "missed" | "left_out";

export interface ConductStep {
  id: string;
  result: string;
  /** Only on a `left_out` step. */
  reason?: string;
}

export interface ConductPartyStamp {
  /** `farmer`, `trucker` or `buyer`. */
  party: string;
  userId: string;
  /** The steps that applied (not left out). */
  offered: number;
  /** The steps taken, 0 to `offered`. */
  earned: number;
  /** Whether the order counts toward the party's verified handoffs figure. */
  counted: boolean;
  /** Only when `counted` is false. */
  notCountedReason?: string;
  steps: ConductStep[];
  /** When the order settled. */
  occurredAt: string;
  /** When the stamp was first written. */
  recordedAt: string;
  /** The one re-evaluation a late rating or photo caused. */
  reevaluatedAt?: string;
}

export interface TransactionConduct {
  /** Farmer, trucker, buyer in that order; only the parties that have a stamp. Empty before the settle. */
  parties: ConductPartyStamp[];
  /** True when at least one party's stamp could not be read; the parties read are still listed. */
  readFailed?: boolean;
}

/** Every step id the server stamps (ConductScoring.Steps). */
export const CONDUCT_STEPS = [
  "as_described",
  "stock_current",
  "pickup_scanned",
  "pickup_fix",
  "pickup_photo",
  "picked_up_on_day",
  "trip_shared",
  "confirmed_in_person",
  "dropoff_scanned",
  "dropoff_fix",
  "dropoff_photo",
  "dropoff_place_set",
  "rated_parties",
  "fair_disputes",
] as const;

export const CONDUCT_RESULTS = ["earned", "missed", "left_out"] as const;

/** Why a step is left out of `offered` (ConductScoring.LeftOutReasons). */
export const LEFT_OUT_REASONS = ["no_signal", "coarse_fix", "web", "old_client", "not_recorded", "self_pickup"] as const;

/** Why an order does not count for a party (ConductScoring.NotCountedReasons). */
export const NOT_COUNTED_REASONS = ["disputed", "below_minimum", "fraud_signal", "fraud_unchecked", "pair_cap"] as const;

type Translate = (key: string) => string;
type Variant = "default" | "success" | "warning";

function labelOrRaw(t: Translate, key: string, raw: string): string {
  const label = t(key);
  return label === key ? raw : label;
}

export const conductStepLabel = (id: string, t: Translate) => labelOrRaw(t, `conduct.step_${id}`, id);

export const conductResultLabel = (result: string, t: Translate) => labelOrRaw(t, `conduct.result_${result}`, result);

export const leftOutReasonLabel = (reason: string, t: Translate) => labelOrRaw(t, `conduct.reason_${reason}`, reason);

export const notCountedReasonLabel = (reason: string, t: Translate) =>
  labelOrRaw(t, `conduct.notCounted_${reason}`, reason);

/** The badge of a step result; the text label always goes with it, so the colour is never the only cue. */
export function conductResultVariant(result: string): Variant {
  switch (result) {
    case "earned":
      return "success";
    case "missed":
      return "warning";
    default:
      return "default";
  }
}
