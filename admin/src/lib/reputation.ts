/* ESC-32 (reputation-api.md 1.4.0 section 9): what the admin reputation routes answer, and the
 * rules the panel and the appeals queue share. The server omits a field with no value, so every
 * field that can be absent is optional here. */

export interface ReputationRestriction {
  since?: string;
  until?: string;
  cause: string;
}

export interface ReputationOverride {
  tag: string;
  until?: string;
  reason?: string;
  by?: string;
}

export interface ReputationSummary {
  userId: string;
  role: string;
  tier?: string;
  activePoints?: number;
  thresholds?: { warning: number; low: number; restricted: number };
  ratingAverage?: number;
  ratingCount?: number;
  ratingDeduction?: number;
  effectiveRating?: number;
  reliabilityPct?: number;
  completedOrders90d?: number;
  faultCancels90d?: number;
  noShows90d?: number;
  restriction?: ReputationRestriction;
  override?: ReputationOverride;
  nextPointExpiryAt?: string;
  penaltiesEnabled?: boolean;
  asOf: string;
}

export interface RestrictionRow {
  eventId: string;
  cause: string;
  status: string;
  since?: string;
  until?: string;
  reason?: string;
  by?: string;
  liftedAt?: string;
  liftedBy?: string;
}

export interface EventAppeal {
  status: string;
  canAppeal?: boolean;
  text?: string;
  appealedAt?: string;
  /** When the party's text arrived; absent with no text. An uphold sends it back (seenAppealTextAt). */
  textAt?: string;
  hasPhoto?: boolean;
  appealPhotoUrl?: string;
  decidedAt?: string;
  decidedBy?: string;
  note?: string;
}

export interface ReputationEventRow {
  eventId: string;
  code: string;
  party?: string;
  points?: number;
  basePoints?: number;
  zeroReason?: string;
  earnedBack?: number;
  remainingPoints?: number;
  status: string;
  occurredAt?: string;
  recordedAt?: string;
  pointsExpireAt?: string;
  orderId?: string;
  tripId?: string;
  buyerId?: string;
  earnBackEventId?: string;
  appeal?: EventAppeal;
  cause?: string;
  since?: string;
  until?: string;
  tag?: string;
  reason?: string;
  by?: string;
  fromTier?: string;
  toTier?: string;
  restrictedUntil?: string;
  liftedAt?: string;
  liftedBy?: string;
}

export interface UserReputationPage {
  reputation: ReputationSummary;
  restrictions?: RestrictionRow[];
  events?: ReputationEventRow[];
  nextToken?: string | null;
}

export interface AppealItem {
  userId: string;
  role?: string;
  displayName?: string;
  eventId: string;
  code: string;
  points?: number;
  occurredAt?: string;
  orderId?: string;
  tripId?: string;
  appealStatus: string;
  appealedAt?: string;
  text?: string;
  /** When the party's text arrived; absent with no text. An uphold sends it back (seenAppealTextAt). */
  appealTextAt?: string;
  hasPhoto?: boolean;
  appealPhotoUrl?: string;
}

export interface AppealsPage {
  appeals?: AppealItem[];
  nextToken?: string | null;
}

export type AppealQueueStatus = "pending" | "review";
export type ReputationDecision = "excuse" | "uphold";
export type ReputationAction = "tag" | "untag" | "restrict" | "lift";

export interface DecisionResult {
  event: ReputationEventRow;
  reputation: ReputationSummary;
  alreadyDecided?: boolean;
}

export interface ActionResult {
  reputation: ReputationSummary;
  alreadyApplied?: boolean;
}

/**
 * The server refuses an uphold when the appeal is not the one the page showed (the party's text
 * arrived or changed since it loaded): the page reloads and the admin reads it before deciding.
 */
export const APPEAL_CHANGED = "REPUTATION_APPEAL_CHANGED";

/** The reason or note every admin reputation write needs (the server's limits). */
export const REASON_MIN = 3;
export const REASON_MAX = 500;

/** The furthest end the server takes when its policy is the default (it answers 400 past its own limit). */
export const TAG_MAX_DAYS = 90;
export const RESTRICT_MAX_DAYS = 365;

/** The codes that can carry points: the only rows an admin can excuse. */
const PENALTY_CODES = new Set([
  "DECLINED_REQUEST",
  "RESPONSE_EXPIRED",
  "CANCELLED_AFTER_ACCEPT",
  "LATE_CANCEL",
  "TRIP_CANCELLED_WITH_BOOKINGS",
  "NO_SHOW",
  "DELIVERY_OVERDUE",
]);

/** System rows (restrictions, tags, tier changes): history only, never decided. */
export function isSystemRow(event: ReputationEventRow): boolean {
  return event.appeal === undefined;
}

export function hasOpenAppeal(event: { appeal?: EventAppeal }): boolean {
  return event.appeal?.status === "pending" || event.appeal?.status === "review";
}

/**
 * Excuse: a penalty whose points still count (status active). The server refuses an event that was
 * upheld with no open appeal (409), so the button is not offered there.
 */
export function canExcuse(event: ReputationEventRow): boolean {
  if (!PENALTY_CODES.has(event.code) || event.status !== "active") return false;
  return hasOpenAppeal(event) || event.appeal?.status !== "upheld";
}

/** Uphold: only an event with an open appeal or review. */
export function canUphold(event: ReputationEventRow): boolean {
  return hasOpenAppeal(event);
}

export function needsUntil(action: ReputationAction): boolean {
  return action !== "lift";
}

export function maxUntilDays(action: ReputationAction | "uphold"): number {
  return action === "tag" || action === "untag" ? TAG_MAX_DAYS : RESTRICT_MAX_DAYS;
}

/**
 * A date input's value (yyyy-mm-dd) as the end of that day on the admin's clock, in the ISO UTC
 * form the server takes. The admin's own day, so the date the pages show afterwards (also on the
 * admin's clock) is the one that was picked. An unreadable value gives "".
 */
export function endOfDayIso(date: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date);
  if (!match) return "";
  const end = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]), 23, 59, 59);
  return Number.isNaN(end.getTime()) ? "" : end.toISOString();
}

/** yyyy-mm-dd (on the admin's clock) of the day `days` after `from`, for the bounds of the date input. */
export function dayOffset(from: Date, days: number): string {
  const d = new Date(from.getFullYear(), from.getMonth(), from.getDate() + days);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/**
 * Why a form cannot be sent yet: "reason" (too short or too long), "until" (missing, not after
 * today or past the limit), or null when it is ready. `today` is the admin's clock; the server
 * checks again with its own.
 */
export function formProblem(
  reason: string,
  until: string,
  untilRequired: boolean,
  maxDays: number,
  today: Date,
): "reason" | "until" | null {
  const length = reason.trim().length;
  if (length < REASON_MIN || length > REASON_MAX) return "reason";
  if (!until) return untilRequired ? "until" : null;
  const iso = endOfDayIso(until);
  if (!iso) return "until";
  const end = new Date(iso).getTime();
  if (end <= today.getTime()) return "until";
  // The last allowed day is the one that ends inside the limit.
  if (end > today.getTime() + maxDays * 24 * 60 * 60 * 1000) return "until";
  return null;
}

export type BadgeVariant = "default" | "success" | "warning" | "danger" | "info";

export function tierVariant(tier: string | undefined): BadgeVariant {
  switch (tier) {
    case "good":
      return "success";
    case "warning":
      return "warning";
    case "low":
    case "restricted":
      return "danger";
    default:
      return "default";
  }
}

export function appealVariant(status: string | undefined): BadgeVariant {
  switch (status) {
    case "pending":
    case "review":
      return "warning";
    case "excused":
      return "success";
    case "upheld":
      return "danger";
    default:
      return "default";
  }
}

export function eventStatusVariant(status: string): BadgeVariant {
  switch (status) {
    case "active":
      return "warning";
    case "excused":
      return "success";
    default:
      return "default";
  }
}

/** The path of the decision route: the event id carries a `#`, so it is encoded. */
export function decisionPath(userId: string, eventId: string): string {
  return `/admin/users/${encodeURIComponent(userId)}/reputation/events/${encodeURIComponent(eventId)}/decision`;
}
