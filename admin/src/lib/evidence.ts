/**
 * FRD-14: the dispute evidence package (GET /admin/disputes/{escrowId}/evidence, escrow-protocol.md
 * 1.40.0). The escrow, order and delivery stay loose rows, as on the transaction view; the parts the
 * dispute page reads are typed here.
 */

type Row = Record<string, unknown>;

export interface LatLng {
  latitude: number;
  longitude: number;
}

export interface TimelineEvent {
  timestamp: string;
  entity: string;
  action: string;
  fromState?: string;
  toState?: string;
  actor?: string;
  telebirrRef?: string;
  ipAddress?: string;
  location?: LatLng;
  detail?: string;
}

/** One handoff: the trucker's pickup, the buyer's drop-off, or the buyer's self-pickup at the farm. */
export interface Handoff {
  leg: "pickup" | "dropoff" | "self_pickup" | string;
  /** the server's time of the handoff */
  at?: string;
  /** the phone's time of the handoff (the pickup scan, or since 2.30.0 the drop-off confirm) */
  capturedAt?: string;
  actorId?: string;
  /** buyer, farmer or trucker */
  actorRole?: string;
  /** qr (scanned) or code (typed); absent when not recorded */
  method?: string;
  gps?: LatLng;
  /**
   * FUP-440 (escrow-protocol 2.30.0): with gps, fix or approximate (coarse location only); without,
   * the cause: services_off, denied, denied_forever or timeout. Absent for an older app or delivery.
   */
  gpsStatus?: string;
  /** the fix's own time on the phone */
  gpsCapturedAt?: string;
  /** the fix's accuracy in metres */
  gpsAccuracyM?: number;
  /** GCR-02: android or web */
  platform?: string;
  /** the listing for a pickup, the trip's destination for a drop-off */
  planned?: LatLng;
  distanceKm?: number;
}

/** FUP-440: the causes of a handoff without a GPS fix that the server stores. */
export const NO_FIX_CAUSES = ["services_off", "denied", "denied_forever", "timeout"] as const;

/** The stored cause of a handoff without a fix; undefined when none is known (an older app or delivery). */
export function noFixCause(handoff: Handoff | undefined): string | undefined {
  const status = handoff?.gpsStatus;
  return status && (NO_FIX_CAUSES as readonly string[]).includes(status) ? status : undefined;
}

/** Received this long after the phone made it, a handoff was made offline and sent later (as a trip's check-ins). */
export const SENT_LATE_MINUTES = 15;

/**
 * Whether the phone made the handoff more than {@link SENT_LATE_MINUTES} before the server received it: done
 * offline and sent later. Reads the handoff's own phone time only, not the fix's: a phone may hand back an older
 * cached fix while online. False when a time is missing or unreadable.
 */
export function sentLate(handoff: Handoff): boolean {
  const received = Date.parse(handoff.at ?? "");
  const made = Date.parse(handoff.capturedAt ?? "");
  return !Number.isNaN(received) && !Number.isNaN(made) && received - made > SENT_LATE_MINUTES * 60_000;
}

export interface EvidenceMessage {
  messageId: string;
  conversationId: string;
  /** order or delivery */
  contextType: string;
  contextId: string;
  senderId: string;
  recipientId: string;
  senderName?: string;
  recipientName?: string;
  body: string;
  createdAt: string;
}

export interface DisputeEvidence {
  escrow: Row;
  order?: Row;
  delivery?: Row;
  timeline: TimelineEvent[];
  handoffs: Handoff[];
  /** only with include=messages */
  messages?: EvidenceMessage[];
  messagesTruncated?: boolean;
}

/** The parties of the dispute by user id, for naming the sender of a message or the maker of a handoff. */
export function partiesOf(evidence: DisputeEvidence | undefined): Map<string, { role: string; name: unknown }> {
  const parties = new Map<string, { role: string; name: unknown }>();
  if (!evidence) return parties;
  const sources = [evidence.delivery, evidence.order, evidence.escrow];
  for (const role of ["buyer", "farmer", "trucker"]) {
    for (const source of sources) {
      const id = source?.[`${role}Id`];
      if (typeof id === "string" && id && !parties.has(id)) {
        const name = sources.map((s) => s?.[`${role}Name`]).find((n) => typeof n === "string" && n.trim());
        parties.set(id, { role, name });
      }
    }
  }
  return parties;
}

/** A delivery photo leg: the two handoffs (FRD-12) and, ESC-36, the trucker's arrival at the drop-off. */
export type PhotoLeg = "pickup" | "dropoff" | "arrival";

/** The photos (presigned GETs) the delivery carries: the two handoffs, then the trucker's arrival photo (ESC-36). */
export function photosOf(evidence: DisputeEvidence | undefined): { leg: PhotoLeg; url: string }[] {
  const delivery = evidence?.delivery;
  if (!delivery) return [];
  return (["pickup", "dropoff", "arrival"] as const)
    .map((leg) => ({ leg, url: delivery[`${leg}PhotoUrl`] }))
    .filter((p): p is { leg: PhotoLeg; url: string } => typeof p.url === "string" && p.url !== "");
}

/**
 * The dispute reasons for which a confirmation by the disputer contradicts the complaint itself (the goods
 * never came, or came short). For damage, quality or the wrong crop the buyer confirms first and complains
 * after: that is the normal path, so it is shown as a plain fact, never as a warning.
 */
const CONTRADICTED_BY_OWN_CONFIRM = new Set(["not_delivered", "wrong_quantity"]);

/** Whether the disputer's own confirmation of receipt is a warning for this dispute (see above). */
export function confirmContradictsReason(dispute: Record<string, unknown> | undefined): boolean {
  return CONTRADICTED_BY_OWN_CONFIRM.has(String(dispute?.reason ?? ""));
}
