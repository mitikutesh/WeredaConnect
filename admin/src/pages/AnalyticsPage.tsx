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

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  // The server returns aggregate status counts (no daily time series), so the chart
  // shows the transaction status breakdown for the period.
  const chartData = [
    { status: t("analytics.completed"), count: Number(stats.completedTransactions) || 0 },
    { status: t("analytics.pending"), count: Number(stats.pendingTransactions) || 0 },
    { status: t("analytics.disputed"), count: Number(stats.disputedTransactions) || 0 },
    { status: t("analytics.refunded"), count: Number(stats.refundedTransactions) || 0 },
  ];

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("analytics.title")}</h2>

      <div className="mb-6 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <KpiCard
          label={t("analytics.totalTransactions")}
          value={Number(stats.totalTransactions) || 0}
          icon={<ArrowLeftRight size={20} />}
        />
        <KpiCard
          label={t("analytics.completed")}
          value={Number(stats.completedTransactions) || 0}
          icon={<CheckCircle size={20} />}
        />
        <KpiCard
          label={t("analytics.pending")}
          value={Number(stats.pendingTransactions) || 0}
          icon={<Clock size={20} />}
        />
        <KpiCard
          label={t("analytics.totalVolume")}
          value={Number(stats.totalVolume) || 0}
          icon={<TrendingUp size={20} />}
        />
      </div>

      <Card>
        <h3 className="mb-4 font-semibold">{t("analytics.statusBreakdown")}</h3>
        <ResponsiveContainer width="100%" height={300}>
          <BarChart data={chartData}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="status" />
            <YAxis />
            <Tooltip />
            <Bar dataKey="count" fill="#1A6B5C" radius={[4, 4, 0, 0]} />
          </BarChart>
        </ResponsiveContainer>
      </Card>
    </div>
  );
}
