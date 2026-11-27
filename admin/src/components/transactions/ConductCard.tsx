import type { ReactNode } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { formatDateTime } from "@/lib/i18n";
import { fill } from "@/lib/trail";
import { partyLabel } from "@/lib/transaction";
import {
  conductResultLabel,
  conductResultVariant,
  conductStepLabel,
  leftOutReasonLabel,
  notCountedReasonLabel,
  type ConductPartyStamp,
  type TransactionConduct,
} from "@/lib/conduct";

/* GCR-07 (reputation-api.md 1.8): why the order scored as it did for each party. Per party, the steps offered and
 * earned, whether the order counts toward the verified handoffs figure (and why not), and every step with its result
 * and, for a step left out, the reason. Admin only: a party sees its own breakdown in the app. */

function Row({ label, children }: { label: string; children: ReactNode }) {
  if (children == null || children === "") return null;
  return (
    <div className="flex justify-between gap-3">
      <dt className="text-slate shrink-0">{label}</dt>
      <dd className="text-right">{children}</dd>
    </div>
  );
}

function PartyStamp({ stamp }: { stamp: ConductPartyStamp }) {
  const { t } = useLocale();
  const party = partyLabel(stamp.party, t);
  return (
    <section aria-label={party} className="rounded-[10px] border border-line p-3">
      <div className="mb-2 flex flex-wrap items-baseline justify-between gap-2">
        <h4 className="text-sm font-medium">
          {party}{" "}
          <Link
            to={`/users/${encodeURIComponent(stamp.userId)}`}
            className="font-mono text-xs text-slate underline decoration-field underline-offset-2 hover:decoration-ink"
          >
            {stamp.userId}
          </Link>
        </h4>
        <span className="text-sm font-medium">
          {fill(t("conduct.score"), { earned: stamp.earned, offered: stamp.offered })}
        </span>
      </div>
      <dl className="space-y-2 text-xs">
        <Row label={t("conduct.verifiedFigure")}>
          <Badge variant={stamp.counted ? "success" : "warning"}>
            {t(stamp.counted ? "conduct.counted" : "conduct.notCounted")}
          </Badge>
          {!stamp.counted && stamp.notCountedReason && (
            <span className="ml-2">{notCountedReasonLabel(stamp.notCountedReason, t)}</span>
          )}
        </Row>
        <Row label={t("conduct.settledAt")}>{formatDateTime(stamp.occurredAt)}</Row>
        <Row label={t("conduct.recordedAt")}>{formatDateTime(stamp.recordedAt)}</Row>
        <Row label={t("conduct.reevaluatedAt")}>{stamp.reevaluatedAt ? formatDateTime(stamp.reevaluatedAt) : ""}</Row>
      </dl>
      {stamp.steps.length > 0 && (
        <table className="mt-3 w-full text-left text-xs">
          <caption className="sr-only">{fill(t("conduct.stepsOf"), { party })}</caption>
          <thead>
            <tr className="text-slate">
              <th scope="col" className="py-1 pr-3 font-medium">{t("conduct.colStep")}</th>
              <th scope="col" className="py-1 pr-3 font-medium">{t("conduct.colResult")}</th>
              <th scope="col" className="py-1 font-medium">{t("conduct.colReason")}</th>
            </tr>
          </thead>
          <tbody>
            {stamp.steps.map((step, index) => (
              <tr key={`${step.id}-${index}`} className="border-t border-track align-top">
                <td className="py-1 pr-3">{conductStepLabel(step.id, t)}</td>
                <td className="py-1 pr-3">
                  <Badge variant={conductResultVariant(step.result)}>{conductResultLabel(step.result, t)}</Badge>
                </td>
                <td className="py-1 text-slate">
                  {step.result === "left_out" && step.reason ? leftOutReasonLabel(step.reason, t) : ""}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

export function ConductCard({ conduct }: { conduct: TransactionConduct }) {
  const { t } = useLocale();
  const parties = conduct.parties ?? [];
  return (
    <Card className="lg:col-span-2">
      <h3 className="mb-4 text-[15px] font-extrabold">{t("conduct.title")}</h3>
      {conduct.readFailed && (
        <p role="alert" className="mb-4 rounded-[10px] border border-orange/60 bg-orange-tint px-4 py-2 text-sm text-orange-ink">
          {t("conduct.readFailed")}
        </p>
      )}
      {parties.length === 0 ? (
        // A failed read of every party is not "not stamped": the warning above says so.
        !conduct.readFailed && <p className="text-sm text-slate">{t("conduct.notStamped")}</p>
      ) : (
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
          {parties.map((stamp) => (
            <PartyStamp key={`${stamp.party}-${stamp.userId}`} stamp={stamp} />
          ))}
        </div>
      )}
    </Card>
  );
}
