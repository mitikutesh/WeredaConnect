import type { ReactNode } from "react";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { formatDateTime } from "@/lib/i18n";
import { CHECK_IN_KINDS, NO_SHOW_JUDGEMENTS, formatFix, formatKm, type PickupEvidence } from "@/lib/pickup";
import { formatGps } from "@/lib/transaction";
import { ReopenPickup } from "./ReopenPickup";

/* FUP-287 (delivery-verification-api.md 2.8.0 P.2): the pickup judgement of an order as evidence, with what the
 * parties' view leaves out: the farm point stamped at acceptance and, per check-in, the server's receipt time, the
 * phone's capture time, the fix and its accuracy, the distance to the farm point, whether it was measured, whether
 * it came late, and who sent it. The re-open action sits under it when the server offers an exit, and next to it
 * (FUP-330) the re-open of a judged trucker no-show. */

type Translate = (key: string) => string;

function labelOrRaw(t: Translate, key: string, raw: string): string {
  const label = t(key);
  return label === key ? raw : label;
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  if (children == null || children === "") return null;
  return (
    <div className="flex justify-between gap-3">
      <dt className="text-text-muted shrink-0">{label}</dt>
      <dd className="text-right">{children}</dd>
    </div>
  );
}

export function PickupEvidenceCard({
  orderId,
  version,
  evidence,
}: {
  orderId: string;
  version: number;
  evidence: PickupEvidence;
}) {
  const { t } = useLocale();
  const judgement = evidence.judgement ?? "";
  const point = formatGps({ latitude: evidence.pointLatitude, longitude: evidence.pointLongitude });
  const last = evidence.lastReopen;
  const noShowExits = evidence.noShowReopenExits ?? [];

  return (
    <Card>
      <h3 className="mb-4 font-semibold">{t("pickup.title")}</h3>
      <dl className="space-y-3 text-sm">
        <Row label={t("pickup.judgement")}>
          {judgement ? (
            <Badge variant={NO_SHOW_JUDGEMENTS.includes(judgement) ? "danger" : judgement === "scanned" ? "success" : "warning"}>
              {labelOrRaw(t, `pickup.judgement_${judgement}`, judgement)}
            </Badge>
          ) : (
            ""
          )}
        </Row>
        <Row label={t("pickup.judgedAt")}>{evidence.judgedAt ? formatDateTime(evidence.judgedAt) : ""}</Row>
        <Row label={t("pickup.deadline")}>{evidence.deadlineAt ? formatDateTime(evidence.deadlineAt) : ""}</Row>
        <Row label={t("pickup.farmPoint")}>{point}</Row>
        {last?.exit ? (
          <Row label={t("pickup.lastReopen")}>
            {labelOrRaw(t, `pickup.exit_${last.exit}`, last.exit)}
            {last.at ? ` (${formatDateTime(last.at)})` : ""}
          </Row>
        ) : null}
      </dl>

      <div className="mt-4 grid grid-cols-1 gap-4 sm:grid-cols-2">
        {CHECK_IN_KINDS.map((kind) => {
          const entry = evidence.checkIns?.[kind];
          const title = t(kind === "arrived_pickup" && evidence.selfPickup ? "pickup.kind_arrived_pickup_self" : `pickup.kind_${kind}`);
          return (
            <section key={kind} aria-label={title} className="rounded border border-gray-200 p-3">
              <h4 className="mb-2 text-sm font-medium">{title}</h4>
              {entry ? (
                <dl className="space-y-2 text-xs">
                  <Row label={t("pickup.receivedAt")}>{formatDateTime(entry.receivedAt)}</Row>
                  <Row label={t("pickup.capturedAt")}>{entry.capturedAt ? formatDateTime(entry.capturedAt) : ""}</Row>
                  <Row label={t("pickup.fix")}>{formatFix(entry) || t("pickup.noFix")}</Row>
                  <Row label={t("pickup.distance")}>{formatKm(entry.distanceKm)}</Row>
                  <Row label={t("pickup.measured")}>
                    <Badge variant={entry.distanceChecked ? "success" : "warning"}>
                      {entry.distanceChecked ? t("pickup.measured") : t("pickup.notMeasured")}
                    </Badge>
                  </Row>
                  <Row label={t("pickup.by")}>{entry.by ?? ""}</Row>
                  {entry.late && <p className="text-warning">{t("pickup.late")}</p>}
                  {entry.outOfRange && <p className="text-warning">{t("pickup.outOfRange")}</p>}
                </dl>
              ) : (
                <p className="text-xs text-text-muted">{t("pickup.noCheckIn")}</p>
              )}
            </section>
          );
        })}
      </div>

      {NO_SHOW_JUDGEMENTS.includes(judgement) && (
        <p className="mt-4 text-xs text-text-muted">
          {t(judgement === "trucker_no_show" && noShowExits.length > 0 ? "pickup.truckerNoShowHint" : "pickup.noShowHint")}
        </p>
      )}
      <ReopenPickup orderId={orderId} version={version} exits={evidence.reopenExits ?? []} />
      {/* FUP-330: a judged trucker no-show, while the buyer has not chosen and the money is held. */}
      <ReopenPickup orderId={orderId} version={version} exits={noShowExits} kind="noShow" />
    </Card>
  );
}
