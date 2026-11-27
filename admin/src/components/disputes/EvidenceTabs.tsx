import { useState, type ReactNode } from "react";
import { MapPin } from "lucide-react";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { HandoffPhoto, TransactionTimeline } from "@/components/transactions/TransactionTimeline";
import { useLocale } from "@/hooks/useLocale";
import { useDisputeMessages } from "@/hooks/useQuery";
import type { DisputeEvidence, EvidenceMessage, Handoff } from "@/lib/evidence";
import { confirmContradictsReason, noFixCause, partiesOf, sentLate } from "@/lib/evidence";
import { formatDateTime } from "@/lib/i18n";
import { formatGps } from "@/lib/transaction";
import { fill } from "@/lib/trail";
import { partyName } from "@/lib/user";

type Translate = (key: string) => string;

/** A translation with a fallback when the key is missing (a value the app does not know yet). */
function label(t: Translate, key: string, fallback: string): string {
  const text = t(key);
  return text === key ? fallback : text;
}

/** The error of an evidence read in the words of its code, with a retry. */
export function EvidenceError({ error, fallbackKey, onRetry }: { error: unknown; fallbackKey: string; onRetry: () => void }) {
  const { t } = useLocale();
  const code = (error as { code?: string } | null)?.code;
  return (
    <Card>
      <div role="alert" className="text-sm text-danger">
        {code ? label(t, `errors.${code}`, t(fallbackKey)) : t(fallbackKey)}
      </div>
      <button
        type="button"
        onClick={onRetry}
        className="mt-3 rounded-[10px] border border-field px-3 py-1 text-sm hover:bg-paper"
      >
        {t("common.retry")}
      </button>
    </Card>
  );
}

/** "Trucker · Dawit", the role and the name of a party, or the id when it is not one of them. */
function useWho(evidence: DisputeEvidence | undefined) {
  const { t, locale } = useLocale();
  const parties = partiesOf(evidence);
  return (id: string | undefined, name?: unknown): string => {
    if (!id) return "";
    const party = parties.get(id);
    if (!party) return partyName(name, id, locale);
    return `${t(`transactions.party_${party.role}`)} · ${partyName(name ?? party.name, id, locale)}`;
  };
}

/** FUP-440: why a handoff had no GPS fix when the phone said so, else the old "No GPS fix" line. */
function noFixText(t: Translate, handoff: Handoff | undefined): string {
  const cause = noFixCause(handoff);
  return cause ? t(`evidence.gpsCause_${cause}`) : t("evidence.noGps");
}

/**
 * One handoff: when, how and by whom, where, and how far from the planned point. FUP-440 and GCR-02: why there
 * was no fix, the fix's own time and accuracy, an approximate fix, a handoff made offline and sent later, and
 * the app it came from, each only when the server stored it.
 */
function HandoffItem({ handoff, who }: { handoff: Handoff; who: (id: string | undefined) => string }) {
  const { t } = useLocale();
  const by = who(handoff.actorId);
  const how = by
    ? fill(t(`evidence.method_${handoff.method === "qr" || handoff.method === "code" ? handoff.method : "unknown"}`), {
        who: by,
      })
    : t("evidence.whoUnknown");
  const place = formatGps(handoff.gps);
  const distanceKey = handoff.leg === "dropoff" ? "evidence.fromDestination" : "evidence.fromListing";
  const fixFacts = [
    handoff.gpsCapturedAt ? fill(t("evidence.gpsFixAt"), { time: formatDateTime(handoff.gpsCapturedAt) }) : "",
    handoff.gpsAccuracyM != null ? fill(t("evidence.gpsAccuracy"), { meters: Math.round(handoff.gpsAccuracyM) }) : "",
  ].filter(Boolean);
  const late = sentLate(handoff);
  const platform = handoff.platform === "android" || handoff.platform === "web" ? handoff.platform : undefined;

  return (
    <li className="rounded-[14px] border border-line p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="font-semibold">{label(t, `evidence.leg_${handoff.leg}`, handoff.leg)}</span>
        {handoff.at && <span className="text-sm text-slate">{formatDateTime(handoff.at)}</span>}
      </div>
      <p className="mt-1 text-sm">{how}</p>
      {handoff.capturedAt && (
        <p className="text-xs text-slate">
          {fill(t("evidence.capturedAt"), { time: formatDateTime(handoff.capturedAt) })}
        </p>
      )}
      {place ? (
        <p className="mt-1 flex flex-wrap items-center gap-1 text-sm">
          <MapPin size={14} aria-hidden="true" className="text-slate" />
          <span className="font-mono text-xs">{place}</span>
          {handoff.distanceKm != null && (
            <Badge variant={handoff.distanceKm > 5 ? "warning" : "default"}>
              {fill(t(distanceKey), { km: handoff.distanceKm })}
            </Badge>
          )}
          {handoff.gpsStatus === "approximate" && <Badge variant="warning">{t("evidence.gpsApproximate")}</Badge>}
        </p>
      ) : (
        <p className="mt-1 text-sm text-slate">{noFixText(t, handoff)}</p>
      )}
      {place && fixFacts.length > 0 && <p className="text-xs text-slate">{fixFacts.join(" · ")}</p>}
      {(late || platform) && (
        <p className="mt-1 flex flex-wrap items-center gap-1">
          {late && <Badge variant="warning">{t("evidence.sentLate")}</Badge>}
          {platform && <Badge>{t(`evidence.platform_${platform}`)}</Badge>}
        </p>
      )}
    </li>
  );
}

type Row = Record<string, unknown>;


/**
 * One leg of the stamped evidence: when, how and by whom, and whether a GPS fix was sent. Without one, the
 * cause from the evidence read's handoff when the phone sent it (FUP-440).
 */
function StampedLeg({
  legKey,
  at,
  method,
  by,
  fix,
  distanceKm,
  distanceKey,
  handoff,
}: {
  legKey: string;
  at: unknown;
  method: unknown;
  by: string;
  fix: unknown;
  distanceKm?: number;
  distanceKey: string;
  handoff?: Handoff;
}) {
  const { t } = useLocale();
  const how = by
    ? fill(t(`evidence.method_${method === "qr" || method === "code" ? method : "unknown"}`), { who: by })
    : t("evidence.whoUnknown");
  return (
    <li className="rounded-[14px] border border-line p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="font-semibold">{t(legKey)}</span>
        {typeof at === "string" && at && <span className="text-sm text-slate">{formatDateTime(at)}</span>}
      </div>
      <p className="mt-1 text-sm">{how}</p>
      <p className="mt-1 flex flex-wrap items-center gap-1 text-sm">
        {fix === true ? (
          <>
            <MapPin size={14} aria-hidden="true" className="text-slate" />
            <span>{t("evidence.gpsSent")}</span>
            {distanceKm != null && (
              <Badge variant={distanceKm > 5 ? "warning" : "default"}>{fill(t(distanceKey), { km: distanceKm })}</Badge>
            )}
          </>
        ) : (
          <Badge variant="warning">{noFixText(t, handoff)}</Badge>
        )}
      </p>
    </li>
  );
}

/**
 * Persona simulation BUG-6 (escrow-protocol.md 2.28.0): the handoff evidence the server stamped on the
 * dispute when it was created (how each leg was confirmed, by whom, whether a GPS fix was sent), and
 * whether the user who opened the dispute is the one who confirmed receipt. The distance to the planned
 * point comes from the evidence read (FRD-14) once it has loaded. Nothing for a dispute without a stamp.
 */
export function StampedHandoffEvidence({ escrow, evidence }: { escrow: Row; evidence: DisputeEvidence | undefined }) {
  const { t } = useLocale();
  const who = useWho(evidence ?? { escrow, timeline: [], handoffs: [] });
  const stamped = (escrow.dispute as Row | undefined)?.handoffEvidence as Row | undefined;
  if (!stamped) return null;

  const selfPickup = stamped.selfPickup === true;
  const handoffOf = (pickup: boolean) =>
    evidence?.handoffs.find((h) => (pickup ? h.leg === "pickup" : h.leg !== "pickup"));
  const text = (value: unknown) => (typeof value === "string" ? value : undefined);

  return (
    <Card className="mb-6">
      <h3 className="mb-3 text-[15px] font-extrabold">{t("evidence.stampedTitle")}</h3>
      {stamped.confirmedByDisputer === true &&
        (confirmContradictsReason(escrow.dispute as Row | undefined) ? (
          <p role="note" className="mb-3 rounded-[10px] border border-orange/60 bg-orange-tint px-3 py-2 text-sm font-semibold">
            {t("evidence.confirmedByDisputer")}
          </p>
        ) : (
          <p className="mb-3 text-sm text-slate">{t("evidence.confirmedByDisputerFact")}</p>
        ))}
      <ul className="space-y-3">
        {!selfPickup && text(stamped.pickedUpAt) && (
          <StampedLeg
            legKey="evidence.leg_pickup"
            at={stamped.pickedUpAt}
            method={stamped.pickupMethod}
            by={who(text(stamped.pickupBy))}
            fix={stamped.pickupFix}
            distanceKm={handoffOf(true)?.distanceKm}
            distanceKey="evidence.fromListing"
            handoff={handoffOf(true)}
          />
        )}
        {text(stamped.confirmedAt) && (
          <StampedLeg
            legKey={selfPickup ? "evidence.leg_self_pickup" : "evidence.leg_dropoff"}
            at={stamped.confirmedAt}
            method={stamped.confirmMethod}
            by={who(text(stamped.confirmedBy))}
            fix={stamped.dropoffFix}
            distanceKm={handoffOf(false)?.distanceKm}
            distanceKey={selfPickup ? "evidence.fromListing" : "evidence.fromDestination"}
            handoff={handoffOf(false)}
          />
        )}
      </ul>
      {!text(stamped.confirmedAt) && <p className="mt-3 text-sm text-slate">{t("evidence.notConfirmed")}</p>}
    </Card>
  );
}

/**
 * FRD-14: the Timeline tab of the dispute page. The handoffs first (QR scan or typed code, who, the
 * GPS and the distance to the planned point), then the stitched order, delivery and escrow timeline
 * of the transaction view.
 */
export function TimelineTab({ evidence }: { evidence: DisputeEvidence }) {
  const { t } = useLocale();
  const who = useWho(evidence);
  return (
    <>
      <Card>
        <h3 className="mb-3 text-[15px] font-extrabold">{t("evidence.handoffs")}</h3>
        {evidence.handoffs.length === 0 ? (
          <p className="text-sm text-slate">{t("evidence.noHandoffs")}</p>
        ) : (
          <ul className="space-y-3">
            {evidence.handoffs.map((handoff) => (
              <HandoffItem key={handoff.leg} handoff={handoff} who={(id) => who(id)} />
            ))}
          </ul>
        )}
      </Card>
      <Card>
        <h3 className="mb-4 text-[15px] font-extrabold">{t("transactions.timeline")}</h3>
        <TransactionTimeline timeline={evidence.timeline as unknown as Record<string, unknown>[]} />
      </Card>
    </>
  );
}

/** FRD-14: the Photos tab, the pickup and drop-off photos (FRD-12) with the time and place of each handoff. */
export function PhotosTab({ evidence }: { evidence: DisputeEvidence }) {
  const { t } = useLocale();
  const delivery = evidence.delivery ?? {};
  const handoffAt = (leg: "pickup" | "dropoff" | "arrival") =>
    leg === "arrival" ? undefined : evidence.handoffs.find((h) => (leg === "pickup" ? h.leg === "pickup" : h.leg !== "pickup"));
  // ESC-36: the trucker's arrival photo at the drop-off, shown only when the delivery has one.
  const legs = (["pickup", "dropoff", "arrival"] as const).filter(
    (leg) => leg !== "arrival" || (typeof delivery.arrivalPhotoUrl === "string" && delivery.arrivalPhotoUrl !== ""),
  );
  // FUP-373: an arrival photo attached on the system's overdue dispute, after the money froze, carries when.
  // FUP-140: so does the buyer's drop-off photo attached on a system dispute without the buyer's confirm.
  const lateAtOf = (leg: "pickup" | "dropoff" | "arrival") => {
    const value = leg === "pickup" ? undefined : delivery[`${leg}PhotoLateAt`];
    return typeof value === "string" && value !== "" ? value : "";
  };

  return (
    <Card>
      <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
        {legs.map((leg) => {
          const url = typeof delivery[`${leg}PhotoUrl`] === "string" ? String(delivery[`${leg}PhotoUrl`]) : "";
          const name = t(`transactions.${leg}Photo`);
          const handoff = handoffAt(leg);
          const lateAt = lateAtOf(leg);
          return (
            <figure key={leg}>
              {url ? (
                <HandoffPhoto
                  url={url}
                  label={name}
                  openLabel={fill(t("transactions.openPhoto"), { label: name })}
                  size="h-56 w-full max-w-sm"
                />
              ) : (
                <p className="flex h-24 items-center text-sm text-slate">{t(`evidence.noPhoto_${leg}`)}</p>
              )}
              <figcaption className="mt-2 text-sm">
                <span className="font-semibold">{name}</span>
                {handoff?.at ? <span className="text-slate"> · {formatDateTime(handoff.at)}</span> : null}
                {lateAt ? <span className="text-slate"> · {formatDateTime(lateAt)}</span> : null}
                {handoff?.gps ? (
                  <span className="block font-mono text-xs text-slate">{formatGps(handoff.gps)}</span>
                ) : null}
                {lateAt ? (
                  <span className="mt-1 block">
                    <Badge variant="warning">{t(`evidence.${leg}PhotoLate`)}</Badge>
                  </span>
                ) : null}
              </figcaption>
            </figure>
          );
        })}
      </div>
      <p className="mt-4 text-xs text-slate">{t("evidence.photosNote")}</p>
    </Card>
  );
}

/**
 * FRD-14: the Messages tab. The parties' messages are private, so nothing is read until the admin
 * asks; the server writes every read to the audit log before it answers. Read-only, oldest first,
 * the order's and the delivery's threads together.
 */
export function MessagesTab({ escrowId, evidence }: { escrowId: string; evidence: DisputeEvidence | undefined }) {
  const { t } = useLocale();
  const [open, setOpen] = useState(false);
  const { data, isLoading, isError, error, refetch } = useDisputeMessages(escrowId, open);
  const who = useWho(evidence ?? data);

  if (!open) {
    return (
      <Card>
        <p className="text-sm">{t("evidence.messagesPrivate")}</p>
        <button
          type="button"
          onClick={() => setOpen(true)}
          className="mt-4 rounded-[14px] bg-ink px-4 py-2 text-sm font-semibold text-white hover:opacity-90"
        >
          {t("evidence.showMessages")}
        </button>
      </Card>
    );
  }

  if (isLoading) {
    return (
      <Card>
        <p role="status" className="text-sm text-slate">
          {t("evidence.messagesLoading")}
        </p>
      </Card>
    );
  }

  if (isError || !data) {
    return <EvidenceError error={error} fallbackKey="evidence.messagesFailed" onRetry={() => refetch()} />;
  }

  const messages = data.messages ?? [];
  return (
    <Card>
      <p className="mb-4 text-xs text-slate">{t("evidence.messagesAudited")}</p>
      {data.messagesTruncated && (
        <p className="mb-4 rounded-[10px] border border-orange/60 bg-orange-tint px-3 py-2 text-sm">
          {t("evidence.messagesTruncated")}
        </p>
      )}
      {messages.length === 0 ? (
        <p className="text-sm text-slate">{t("evidence.noMessages")}</p>
      ) : (
        <ol aria-label={t("evidence.tabMessages")} className="space-y-3">
          {messages.map((m) => (
            <MessageItem key={m.messageId} message={m} who={who} />
          ))}
        </ol>
      )}
    </Card>
  );
}

function MessageItem({
  message,
  who,
}: {
  message: EvidenceMessage;
  who: (id: string | undefined, name?: unknown) => string;
}) {
  const { t } = useLocale();
  const header: ReactNode = fill(t("evidence.messageFromTo"), {
    from: who(message.senderId, message.senderName),
    to: who(message.recipientId, message.recipientName),
  });
  return (
    <li className="rounded-[14px] border border-line p-3">
      <div className="flex flex-wrap items-center gap-2 text-xs text-slate">
        <Badge>{label(t, `evidence.context_${message.contextType}`, message.contextType)}</Badge>
        <span className="font-medium text-ink">{header}</span>
        <span>· {formatDateTime(message.createdAt)}</span>
      </div>
      <p className="mt-2 whitespace-pre-wrap break-words text-sm">{message.body}</p>
    </li>
  );
}
