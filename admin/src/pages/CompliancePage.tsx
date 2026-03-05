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
          <h3 className="mb-4 font-semibold">{t("compliance.nbeLimits")}</h3>
          <dl className="space-y-3">
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("compliance.dailyLimit")}</dt>
              <dd className="font-medium">
                {formatETB(Number(compliance.dailyLimit) || 0, locale)}
              </dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-text-muted">{t("compliance.monthlyLimit")}</dt>
              <dd className="font-medium">
                {formatETB(Number(compliance.monthlyLimit) || 0, locale)}
              </dd>
            </div>
          </dl>
        </Card>

        <Card>
          <h3 className="mb-4 font-semibold">{t("compliance.kycTracker")}</h3>
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <span className="text-sm">{t("compliance.verified")}</span>
              <Badge variant="success">{Number(compliance.verifiedUsers) || 0}</Badge>
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
