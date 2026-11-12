/**
 * ESC-24: the admin's view of an escrow's money legs. The ledger rows of ESC-20, the transport leg,
 * and the arithmetic of a per-leg (split) verdict. The split rules mirror the server's
 * EscrowLedgerBook.PlanSplit (escrow-protocol.md 2.2.0, P.8) so the admin sees the amounts the
 * server will accept before confirming; the server checks them again.
 */

type Row = Record<string, unknown>;
type Variant = "default" | "success" | "warning" | "danger" | "info" | "transit";
type Translate = (key: string) => string;

export interface LedgerRow {
  ref: string;
  kind: string;
  legs: string[];
  payeeRole?: string;
  payeeId?: string;
  payeeName?: string;
  amount: number;
  status: string;
  gatewayRef: string;
  telebirrRef?: string;
  attempts: number;
  lastCode?: string;
  manualReason?: string;
  reason: string;
  actor: string;
  paidBy?: string;
  createdAt: string;
  updatedAt: string;
  sentAt?: string;
  doneAt?: string;
}

export interface LedgerPage {
  escrowId: string;
  settlementModel: string;
  rows?: LedgerRow[];
  nextToken?: string | null;
}

export interface StuckOrder {
  orderId: string;
  status: string;
  stage: string;
  reason: string;
  stuckSince: string;
  ageHours: number;
  buyerId: string;
  buyerName?: string;
  farmerId: string;
  farmerName?: string;
  truckerId?: string;
  truckerName?: string;
  cropType: string;
  orderTotal: number;
  currency: string;
  escrowId?: string;
}

export interface StuckOrderPage {
  source: string;
  orders?: StuckOrder[];
  nextToken?: string | null;
  asOf?: string;
}

export type StuckSource = "queue" | "deadline";

/** Ledger row status to badge colour. */
export const ledgerStatusVariant: Record<string, Variant> = {
  pending: "info",
  sent: "transit",
  done: "success",
  failed: "danger",
  manual: "warning",
};

/** Leg state to badge colour (held, released, refunded, kept, and the transport leg's own states). */
export const legStateVariant: Record<string, Variant> = {
  held: "info",
  released: "success",
  refunded: "warning",
  kept: "default",
  paid: "success",
  pending: "warning",
  blocked: "danger",
  manual_queue: "warning",
  none: "default",
};

/** A translation, or the raw value when the server sends one the admin has no label for yet. */
export function labelOrRaw(t: Translate, key: string, raw: string): string {
  const label = t(key);
  return label === key ? raw : label;
}

/** The legs of a ledger escrow as the server sends them; null on an escrow of the legacy model. */
export function legStatusOf(escrow: Row | null | undefined): Record<string, string> | null {
  const legs = escrow?.legStatus;
  return legs && typeof legs === "object" ? (legs as Record<string, string>) : null;
}

/** An amount in cents, so the split adds up exactly. */
export function toCents(value: unknown): number {
  return Math.round((Number(value) || 0) * 100);
}

/** "1500" or "1500.5" or "1500.50" to cents; null for anything else (negative, 3 decimals, text). */
export function parseAmount(text: string): number | null {
  const trimmed = text.trim();
  if (!/^\d+(\.\d{1,2})?$/.test(trimmed)) return null;
  return Math.round(parseFloat(trimmed) * 100);
}

/** What a ledger escrow still holds, in cents. A leg that is not held counts 0. */
export interface HeldLegs {
  crop: number;
  transport: number;
  commission: number;
  /** A trucker can be paid: the transport leg is held and the escrow names a trucker. */
  truckerPayable: boolean;
}

/** The legs a split may divide, or null when the server would refuse any split (no ledger, crop leg not held). */
export function heldLegs(escrow: Row | null | undefined): HeldLegs | null {
  const legs = legStatusOf(escrow);
  if (!escrow || !legs || legs.crop !== "held") return null;
  const transport = legs.transport === "held" ? toCents(escrow.truckerPayout) : 0;
  return {
    crop: toCents(escrow.farmerPayout),
    transport,
    commission: legs.commission === "held" ? toCents(escrow.commission) : 0,
    truckerPayable: transport > 0 && !!escrow.truckerId,
  };
}

export interface SplitPreview {
  /** In cents. */
  farmer: number;
  trucker: number;
  buyerRefund: number;
  /** The commission stays with the platform when the farmer gets anything; otherwise it goes back to the buyer. */
  commissionKept: number;
  commissionRefunded: number;
  /** The i18n keys of what is wrong, empty when the server would accept the split. */
  errors: string[];
}

/**
 * The split of a per-leg verdict. The farmer gets at most the crop leg held, the trucker at most the
 * transport leg held; the buyer gets the rest of both legs, plus the commission held when the farmer
 * gets nothing. Mirrors EscrowLedgerBook.PlanSplit.
 */
export function previewSplit(held: HeldLegs, farmer: number | null, trucker: number | null): SplitPreview {
  const errors: string[] = [];
  if (farmer == null) errors.push("legs.errorFarmerAmount");
  else if (farmer > held.crop) errors.push("legs.errorFarmerMax");
  if (trucker == null) errors.push("legs.errorTruckerAmount");
  else if (trucker > held.transport) errors.push("legs.errorTruckerMax");
  else if (trucker > 0 && !held.truckerPayable) errors.push("legs.errorNoTrucker");
  const f = farmer ?? 0;
  const tr = trucker ?? 0;
  const keep = f > 0;
  return {
    farmer: f,
    trucker: tr,
    buyerRefund: held.crop + held.transport + (keep ? 0 : held.commission) - f - tr,
    commissionKept: keep ? held.commission : 0,
    commissionRefunded: keep ? 0 : held.commission,
    errors,
  };
}

/** The request amounts of a split, in birr with at most 2 decimals. */
export function splitAmounts(preview: SplitPreview) {
  return {
    farmerAmount: preview.farmer / 100,
    truckerAmount: preview.trucker / 100,
    buyerRefund: preview.buyerRefund / 100,
  };
}

/** A split the server recorded with a verdict (escrow.verdictAmounts), or null. */
export function recordedSplit(escrow: Row | null | undefined) {
  const split = escrow?.verdictAmounts as Row | undefined;
  if (!split || typeof split !== "object") return null;
  return {
    farmerAmount: Number(split.farmerAmount) || 0,
    truckerAmount: Number(split.truckerAmount) || 0,
    buyerRefund: Number(split.buyerRefund) || 0,
  };
}

/** "3 h" under two days, "4 d" from there: how long an order has been stuck. */
export function stuckAge(hours: number, t: Translate): string {
  return hours < 48
    ? t("stuckOrders.ageHours").replace("{hours}", String(hours))
    : t("stuckOrders.ageDays").replace("{days}", String(Math.floor(hours / 24)));
}
