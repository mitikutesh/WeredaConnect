import { useParams } from "react-router";
import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useEscrowDetail, useEscrowAudit } from "@/hooks/useQuery";
import { formatETB, formatDateTime } from "@/lib/i18n";

export function EscrowDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { t, locale } = useLocale();
  const { data: escrow, isLoading } = useEscrowDetail(id || "");
  const { data: auditData } = useEscrowAudit(id || "");

  const detail = (escrow as Record<string, unknown>) || {};
  const auditEntries = (auditData as Record<string, unknown>[]) || [];

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
              <dd>{String(detail.buyerName || "")}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("escrows.seller")}</dt>
              <dd>{String(detail.sellerName || "")}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("escrows.amount")}</dt>
              <dd className="font-medium">
                {formatETB(Number(detail.amount) || 0, locale)}
              </dd>
            </div>
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
          </dl>
        </Card>

        <Card>
          <h3 className="mb-4 font-semibold">{t("escrows.auditTrail")}</h3>
          <div className="space-y-3">
            {auditEntries.map((entry, i) => (
              <div key={i} className="flex items-start gap-3 border-l-2 border-primary/30 pl-4">
                <div>
                  <p className="text-sm font-medium">{String(entry.action || "")}</p>
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
      </div>
    </div>
  );
}
