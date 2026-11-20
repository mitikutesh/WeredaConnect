import { TrendingUp, DollarSign, Percent, ArrowLeftRight } from "lucide-react";
import { KpiCard } from "@/components/ui/Card";
import { useLocale } from "@/hooks/useLocale";
import { useRevenueAnalytics } from "@/hooks/useQuery";
import { formatETB } from "@/lib/i18n";

export function RevenuePage() {
  const { t, locale } = useLocale();
  const { data, isLoading } = useRevenueAnalytics();

  const stats = (data as Record<string, unknown>) || {};

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("revenue.title")}</h2>

      {/*
        The server returns period aggregates only (no monthly time series), so this
        page shows the aggregate KPIs the /admin/analytics/revenue endpoint provides.
      */}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard
          label={t("revenue.totalCommission")}
          value={formatETB(Number(stats.totalCommissions) || 0, locale)}
          icon={<DollarSign size={20} />}
        />
        <KpiCard
          label={t("revenue.totalVolume")}
          value={formatETB(Number(stats.totalVolume) || 0, locale)}
          icon={<TrendingUp size={20} />}
        />
        <KpiCard
          label={t("revenue.transactionCount")}
          value={Number(stats.transactionCount) || 0}
          icon={<ArrowLeftRight size={20} />}
        />
        <KpiCard
          label={t("revenue.avgCommission")}
          value={formatETB(Number(stats.averageCommission) || 0, locale)}
          icon={<Percent size={20} />}
        />
      </div>
    </div>
  );
}
