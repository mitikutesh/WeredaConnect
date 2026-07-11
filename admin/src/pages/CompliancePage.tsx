import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useComplianceSummary } from "@/hooks/useQuery";
import { formatETB } from "@/lib/i18n";

export function CompliancePage() {
  const { t, locale } = useLocale();
  const { data, isLoading } = useComplianceSummary();

  const compliance = (data as Record<string, unknown>) || {};

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("compliance.title")}</h2>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <Card>
          <h3 className="mb-4 font-semibold">{t("compliance.overview")}</h3>
          <dl className="space-y-3">
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("compliance.totalTransactions")}</dt>
              <dd className="font-medium">{Number(compliance.totalTransactions) || 0}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("compliance.totalVolume")}</dt>
              <dd className="font-medium">
                {formatETB(Number(compliance.totalVolume) || 0, locale)}
              </dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("compliance.disputesOpen")}</dt>
              <dd className="font-medium">{Number(compliance.disputesOpen) || 0}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("compliance.disputesResolved")}</dt>
              <dd className="font-medium">{Number(compliance.disputesResolved) || 0}</dd>
            </div>
          </dl>
        </Card>

        <Card>
          <h3 className="mb-4 font-semibold">{t("compliance.kycTracker")}</h3>
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <span className="text-sm">{t("compliance.verified")}</span>
              <Badge variant="success">{Number(compliance.faydaVerifiedUsers) || 0}</Badge>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-sm">{t("compliance.unverified")}</span>
              <Badge variant="warning">{Number(compliance.unverifiedUsers) || 0}</Badge>
            </div>
          </div>
        </Card>
      </div>
    </div>
  );
}
