import { useParams } from "react-router";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { DoubleEscrowRefund } from "@/components/escrows/DoubleEscrowRefund";
import { LedgerCard, ManualLegsCard, RecordedSplitRow, TruckerLegRows } from "@/components/escrows/MoneyLegs";
import { useLocale } from "@/hooks/useLocale";
import { useEscrowDetail, useEscrowAudit } from "@/hooks/useQuery";
import { formatETB, formatDateTime } from "@/lib/i18n";
import { partyName } from "@/lib/user";
import { timelineActionLabel } from "@/lib/transaction";

export function EscrowDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t, locale } = useLocale();
  const { data: escrow, isLoading } = useEscrowDetail(id || "");
  const { data: auditData } = useEscrowAudit(id || "");

  const detailResp = escrow as Record<string, unknown> | undefined;
  const detail = (detailResp?.escrow as Record<string, unknown>) || {};
  const auditResp = auditData as Record<string, unknown> | undefined;
  const auditEntries = (auditResp?.auditEntries as Record<string, unknown>[]) || [];

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("escrows.detail")}</h2>
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <Card>
          <dl className="space-y-3">
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("escrows.id")}</dt>
              <dd className="font-medium">{String(detail.escrowId || "")}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("escrows.buyer")}</dt>
              <dd>{partyName(detail.buyerName, detail.buyerId, locale)}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("escrows.seller")}</dt>
              <dd>{partyName(detail.farmerName, detail.farmerId, locale)}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("escrows.amount")}</dt>
              <dd className="font-medium">
                {formatETB(Number(detail.amount) || 0, locale)}
              </dd>
            </div>
            {/* ESC-24: the legs of the amount. The farmer and commission parts show once the escrow carries them. */}
            {detail.farmerPayout != null && (
              <div className="flex justify-between gap-3">
                <dt className="text-text-muted">{t("transactions.farmerPayout")}</dt>
                <dd>{formatETB(Number(detail.farmerPayout) || 0, locale)}</dd>
              </div>
            )}
            {detail.commission != null && (
              <div className="flex justify-between gap-3">
                <dt className="text-text-muted">{t("transactions.commission")}</dt>
                <dd>{formatETB(Number(detail.commission) || 0, locale)}</dd>
              </div>
            )}
            <TruckerLegRows escrow={detail} />
            <RecordedSplitRow escrow={detail} />
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("escrows.status")}</dt>
              <dd>
                <Badge>{String(detail.status || "")}</Badge>
              </dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("escrows.createdAt")}</dt>
              <dd>{detail.createdAt ? formatDateTime(String(detail.createdAt)) : ""}</dd>
            </div>
            {/* FUP-41: why the hourly sweeps leave this escrow alone (ESC-10), the code the alarm runbooks name. */}
            {detail.settlementBlockReason ? (
              <div className="flex justify-between gap-3">
                <dt className="text-text-muted">{t("escrows.sweepBlock")}</dt>
                <dd className="text-right">
                  <span className="font-mono text-sm">{String(detail.settlementBlockReason)}</span>
                  {detail.settlementBlockReasonAt ? (
                    <span className="block text-xs text-text-muted">
                      {t("escrows.sweepBlockSince").replace(
                        "{time}",
                        formatDateTime(String(detail.settlementBlockReasonAt)),
                      )}
                    </span>
                  ) : null}
                </dd>
              </div>
            ) : null}
          </dl>
        </Card>

        {/* FUP-41: a second payment for the order, refunded to the buyer by an admin. */}
        <DoubleEscrowRefund escrow={detail} />

        <Card>
          <h3 className="mb-4 font-semibold">{t("escrows.auditTrail")}</h3>
          <div className="space-y-3">
            {auditEntries.map((entry, i) => (
              <div key={i} className="flex items-start gap-3 border-l-2 border-primary/30 pl-4">
                <div>
                  <p className="text-sm font-medium">
                    {timelineActionLabel(String(entry.action || ""), t)}
                  </p>
                  <p className="text-xs text-text-muted">
                    {entry.timestamp ? formatDateTime(String(entry.timestamp)) : ""}
                  </p>
                  {entry.details ? (
                    <p className="mt-1 text-xs text-text-muted">{String(entry.details)}</p>
                  ) : null}
                </div>
              </div>
            ))}
            {auditEntries.length === 0 && (
              <p className="text-sm text-text-muted">{t("common.noData")}</p>
            )}
          </div>
        </Card>

        {/* ESC-24: the ledger of an escrow on the ledger (ESC-20); nothing for the legacy model. */}
        <LedgerCard escrow={detail} />
        {/* FUP-218: the manual payout legs of a legacy escrow, where a reference recorded by mistake is corrected. */}
        <ManualLegsCard escrow={detail} />
      </div>
    </div>
  );
}
