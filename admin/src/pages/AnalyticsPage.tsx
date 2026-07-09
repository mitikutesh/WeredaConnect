import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from "recharts";
import { ArrowLeftRight, TrendingUp, Clock, CheckCircle } from "lucide-react";
import { Card, KpiCard } from "@/components/ui/Card";
import { useLocale } from "@/hooks/useLocale";
import { useTransactionAnalytics } from "@/hooks/useQuery";

export function AnalyticsPage() {
  const { t } = useLocale();
  const { data, isLoading } = useTransactionAnalytics();

  const stats = (data as Record<string, unknown>) || {};
  const chartData = (stats.daily as Record<string, unknown>[]) || [];

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("analytics.title")}</h2>

      <div className="mb-6 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard
          label={t("analytics.totalVolume")}
          value={Number(stats.totalVolume) || 0}
          icon={<ArrowLeftRight size={20} />}
        />
        <KpiCard
          label={t("analytics.dailyAvg")}
          value={Number(stats.dailyAvg) || 0}
          icon={<TrendingUp size={20} />}
        />
        <KpiCard
          label={t("analytics.peakHour")}
          value={String(stats.peakHour || t("common.notAvailable"))}
          icon={<Clock size={20} />}
        />
        <KpiCard
          label={t("analytics.successRate")}
          value={`${Number(stats.successRate) || 0}%`}
          icon={<CheckCircle size={20} />}
        />
      </div>

      <Card>
        <h3 className="mb-4 font-semibold">{t("analytics.totalVolume")}</h3>
        <ResponsiveContainer width="100%" height={300}>
          <BarChart data={chartData}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="date" />
            <YAxis />
            <Tooltip />
            <Bar dataKey="count" fill="#1A6B5C" radius={[4, 4, 0, 0]} />
          </BarChart>
        </ResponsiveContainer>
      </Card>
    </div>
  );
}
