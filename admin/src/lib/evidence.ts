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
  /** the phone's time of the pickup scan */
  capturedAt?: string;
  actorId?: string;
  /** buyer, farmer or trucker */
  actorRole?: string;
  /** qr (scanned) or code (typed); absent when not recorded */
  method?: string;
  gps?: LatLng;
  /** the listing for a pickup, the trip's destination for a drop-off */
  planned?: LatLng;
  distanceKm?: number;
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
