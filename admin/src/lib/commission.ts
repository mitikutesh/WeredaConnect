/**
 * The negotiated commission of one order (order-lifecycle.md 2.26.0, "Admin commission change"): before the buyer
 * pays, an admin may agree another commission rate for that order (POST /admin/orders/{orderId}/commission). These
 * are the rules the form checks before it sends, and the preview it shows. The server checks them again and does the
 * sums itself; the preview rounds the same way so the admin sees the total the buyer will be asked to pay.
 */

/** The order statuses before any money moved: the only ones that take another commission. */
export const COMMISSION_EDITABLE_STATUSES: readonly string[] = ["created", "pending_payment"];

/** The highest rate an admin may agree, in percent (the server's 0.20). */
export const MAX_COMMISSION_PERCENT = 20;

/** A percent the admin types: digits, then at most 2 decimals ("2.5", "0", "20", "18.99"). */
const PERCENT_PATTERN = /^(\d+)(?:\.(\d{1,2}))?$/;

/** Whether the order status still takes another commission. */
export function isCommissionEditable(status: string): boolean {
  return COMMISSION_EDITABLE_STATUSES.includes(status);
}

/**
 * The rate fraction of a typed percent ("2.5" is 0.025), at most 4 decimals, or null when the text is not a percent
 * from 0 to 20 with at most 2 decimals. Worked in hundredths of a percent so "18.99" is exactly 0.1899.
 */
export function parseCommissionPercent(text: string): number | null {
  const match = PERCENT_PATTERN.exec(text.trim());
  if (!match) return null;
  const hundredths = Number(match[1]) * 100 + Number((match[2] ?? "").padEnd(2, "0"));
  if (!Number.isSafeInteger(hundredths) || hundredths > MAX_COMMISSION_PERCENT * 100) return null;
  return hundredths / 10000;
}

/** A rate in 1/10000 steps (0.025 is 250), the server's 4 decimals. */
function rateUnits(rate: number): number {
  return Math.round(rate * 10000);
}

/** ETB to whole santim, halves away from zero; toFixed first so 1.005 is not read as 1.00499. */
function toSantim(amount: number): number {
  const scaled = Number((Math.abs(amount) * 100).toFixed(6));
  return Math.sign(amount) * Math.round(scaled);
}

/** `numerator / divisor` to a whole number, halves away from zero (both whole, divisor positive). */
function divideRounded(numerator: number, divisor: number): number {
  const magnitude = Math.abs(numerator);
  const quotient = Math.floor(magnitude / divisor);
  const rounded = (magnitude % divisor) * 2 >= divisor ? quotient + 1 : quotient;
  return numerator < 0 ? -rounded : rounded;
}

/**
 * The order's commission and total at `rate`, as the server sums them: commission = round2(cropCost x rate) and
 * total = cropCost + transportCost + commission, each to the santim with halves away from zero. Done in whole santim
 * so float noise (18.99 x 170 and the like) never moves a santim.
 */
export function previewCommission(
  cropCost: number,
  transportCost: number,
  rate: number,
): { commissionAmount: number; orderTotal: number } {
  const crop = toSantim(cropCost);
  const transport = toSantim(transportCost);
  const commission = divideRounded(crop * rateUnits(rate), 10000);
  return { commissionAmount: commission / 100, orderTotal: (crop + transport + commission) / 100 };
}

/** Whether two rates are the same to the server's 4 decimals. */
export function isSameRate(a: number, b: number): boolean {
  return rateUnits(a) === rateUnits(b);
}

/** A change reason, 10 to 500 characters once trimmed (the server's rule). */
export function isValidCommissionReason(value: string): boolean {
  const length = value.trim().length;
  return length >= 10 && length <= 500;
}

/** "2.5%", "0%", "5%", "18.99%": a rate fraction as a percent with at most 2 decimals and no trailing zeros. */
export function formatRatePercent(rate: number): string {
  if (!Number.isFinite(rate)) return "";
  const units = rateUnits(rate);
  const magnitude = Math.abs(units);
  const whole = Math.floor(magnitude / 100);
  const decimals = String(magnitude % 100).padStart(2, "0").replace(/0+$/, "");
  return `${units < 0 ? "-" : ""}${whole}${decimals ? `.${decimals}` : ""}%`;
}
