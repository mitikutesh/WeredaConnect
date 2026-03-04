import { Wallet, TrendingUp, Scale, ShieldAlert } from "lucide-react";
import { KpiCard } from "@/components/ui/Card";
import { useLocale } from "@/hooks/useLocale";
import { useDashboardStats } from "@/hooks/useQuery";
import { formatETB } from "@/lib/i18n";

export function DashboardPage() {
  const { t, locale } = useLocale();
  const { data, isLoading } = useDashboardStats();

  const stats = (data as Record<string, number>) || {};

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("dashboard.title")}</h2>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard
          label={t("dashboard.activeEscrows")}
          value={stats.activeEscrows ?? 0}
          icon={<Wallet size={20} />}
        />
        <KpiCard
          label={t("dashboard.totalRevenue")}
          value={formatETB(stats.totalRevenue ?? 0, locale)}
          icon={<TrendingUp size={20} />}
        />
        <KpiCard
          label={t("dashboard.openDisputes")}
          value={stats.openDisputes ?? 0}
          icon={<Scale size={20} />}
        />
        <KpiCard
          label={t("dashboard.fraudFlags")}
          value={stats.fraudFlags ?? 0}
          icon={<ShieldAlert size={20} />}
        />
      </div>
    </div>
  );
}
