import { useState, type ReactNode } from "react";
import { MapPin } from "lucide-react";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { HandoffPhoto, TransactionTimeline } from "@/components/transactions/TransactionTimeline";
import { useLocale } from "@/hooks/useLocale";
import { useDisputeMessages } from "@/hooks/useQuery";
import type { DisputeEvidence, EvidenceMessage, Handoff } from "@/lib/evidence";
import { partiesOf } from "@/lib/evidence";
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
        className="mt-3 rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50"
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

/** One handoff: when, how and by whom, where, and how far from the planned point. */
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

  return (
    <li className="rounded-lg border border-gray-200 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="font-semibold">{label(t, `evidence.leg_${handoff.leg}`, handoff.leg)}</span>
        {handoff.at && <span className="text-sm text-text-muted">{formatDateTime(handoff.at)}</span>}
      </div>
      <p className="mt-1 text-sm">{how}</p>
      {handoff.capturedAt && (
        <p className="text-xs text-text-muted">
          {fill(t("evidence.capturedAt"), { time: formatDateTime(handoff.capturedAt) })}
        </p>
      )}
      {place ? (
        <p className="mt-1 flex flex-wrap items-center gap-1 text-sm">
          <MapPin size={14} aria-hidden="true" className="text-text-muted" />
          <span className="font-mono text-xs">{place}</span>
          {handoff.distanceKm != null && (
            <Badge variant={handoff.distanceKm > 5 ? "warning" : "default"}>
              {fill(t(distanceKey), { km: handoff.distanceKm })}
            </Badge>
          )}
        </p>
      ) : (
        <p className="mt-1 text-sm text-text-muted">{t("evidence.noGps")}</p>
      )}
    </li>
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
        <h3 className="mb-3 font-semibold">{t("evidence.handoffs")}</h3>
        {evidence.handoffs.length === 0 ? (
          <p className="text-sm text-text-muted">{t("evidence.noHandoffs")}</p>
        ) : (
          <ul className="space-y-3">
            {evidence.handoffs.map((handoff) => (
              <HandoffItem key={handoff.leg} handoff={handoff} who={(id) => who(id)} />
            ))}
          </ul>
        )}
      </Card>
      <Card>
        <h3 className="mb-4 font-semibold">{t("transactions.timeline")}</h3>
        <TransactionTimeline timeline={evidence.timeline as unknown as Record<string, unknown>[]} />
      </Card>
    </>
  );
}

/** FRD-14: the Photos tab, the pickup and drop-off photos (FRD-12) with the time and place of each handoff. */
export function PhotosTab({ evidence }: { evidence: DisputeEvidence }) {
  const { t } = useLocale();
  const delivery = evidence.delivery ?? {};
  const handoffAt = (leg: "pickup" | "dropoff") =>
    evidence.handoffs.find((h) => (leg === "pickup" ? h.leg === "pickup" : h.leg !== "pickup"));

  return (
    <Card>
      <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
        {(["pickup", "dropoff"] as const).map((leg) => {
          const url = typeof delivery[`${leg}PhotoUrl`] === "string" ? String(delivery[`${leg}PhotoUrl`]) : "";
          const name = t(`transactions.${leg}Photo`);
          const handoff = handoffAt(leg);
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
                <p className="flex h-24 items-center text-sm text-text-muted">{t(`evidence.noPhoto_${leg}`)}</p>
              )}
              <figcaption className="mt-2 text-sm">
                <span className="font-semibold">{name}</span>
                {handoff?.at ? <span className="text-text-muted"> · {formatDateTime(handoff.at)}</span> : null}
                {handoff?.gps ? (
                  <span className="block font-mono text-xs text-text-muted">{formatGps(handoff.gps)}</span>
                ) : null}
              </figcaption>
            </figure>
          );
        })}
      </div>
      <p className="mt-4 text-xs text-text-muted">{t("evidence.photosNote")}</p>
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
          className="mt-4 rounded-lg bg-primary px-4 py-2 text-sm font-semibold text-white hover:opacity-90"
        >
          {t("evidence.showMessages")}
        </button>
      </Card>
    );
  }

  if (isLoading) {
    return (
      <Card>
        <p role="status" className="text-sm text-text-muted">
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
      <p className="mb-4 text-xs text-text-muted">{t("evidence.messagesAudited")}</p>
      {data.messagesTruncated && (
        <p className="mb-4 rounded border border-warning/30 bg-warning/10 px-3 py-2 text-sm">
          {t("evidence.messagesTruncated")}
        </p>
      )}
      {messages.length === 0 ? (
        <p className="text-sm text-text-muted">{t("evidence.noMessages")}</p>
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
    <li className="rounded-lg border border-gray-200 p-3">
      <div className="flex flex-wrap items-center gap-2 text-xs text-text-muted">
        <Badge>{label(t, `evidence.context_${message.contextType}`, message.contextType)}</Badge>
        <span className="font-medium text-text">{header}</span>
        <span>· {formatDateTime(message.createdAt)}</span>
      </div>
      <p className="mt-2 whitespace-pre-wrap break-words text-sm">{message.body}</p>
    </li>
  );
}
