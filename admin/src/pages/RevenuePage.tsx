import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from "recharts";
import { TrendingUp, DollarSign, Percent, ArrowLeftRight } from "lucide-react";
import { Card, KpiCard } from "@/components/ui/Card";
import { useLocale } from "@/hooks/useLocale";
import { useRevenueAnalytics } from "@/hooks/useQuery";
import { formatETB } from "@/lib/i18n";

export function RevenuePage() {
  const { t, locale } = useLocale();
  const { data, isLoading } = useRevenueAnalytics();

  const stats = (data as Record<string, unknown>) || {};
  const chartData = (stats.monthly as Record<string, unknown>[]) || [];

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("revenue.title")}</h2>

      <div className="mb-6 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard
          label={t("revenue.totalCommission")}
          value={formatETB(Number(stats.totalCommission) || 0, locale)}
          icon={<DollarSign size={20} />}
        />
        <KpiCard
          label={t("revenue.monthlyRevenue")}
          value={formatETB(Number(stats.monthlyRevenue) || 0, locale)}
          icon={<TrendingUp size={20} />}
        />
        <KpiCard
          label={t("revenue.avgTransaction")}
          value={formatETB(Number(stats.avgTransaction) || 0, locale)}
          icon={<ArrowLeftRight size={20} />}
        />
        <KpiCard
          label={t("revenue.commissionRate")}
          value={`${Number(stats.commissionRate) || 2.5}%`}
          icon={<Percent size={20} />}
        />
      </div>

      <Card>
        <h3 className="mb-4 font-semibold">{t("revenue.monthlyRevenue")}</h3>
        <ResponsiveContainer width="100%" height={300}>
          <AreaChart data={chartData}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="month" />
            <YAxis />
            <Tooltip />
            <Area
              type="monotone"
              dataKey="revenue"
              stroke="#1A6B5C"
              fill="#1A6B5C"
              fillOpacity={0.1}
            />
          </AreaChart>
        </ResponsiveContainer>
      </Card>
    </div>
  );
}
