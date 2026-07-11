import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useSystemHealth } from "@/hooks/useQuery";

export function SystemHealthPage() {
  const { t } = useLocale();
  const { data, isLoading } = useSystemHealth();

  const health = (data as Record<string, unknown>) || {};

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  const statusVariant: Record<string, "success" | "warning" | "danger"> = {
    healthy: "success",
    degraded: "warning",
    down: "danger",
  };
  const status = String(health.status ?? "");

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("health.title")}</h2>

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Card>
          <p className="text-sm text-text-muted">{t("health.status")}</p>
          <div className="mt-1">
            <Badge variant={statusVariant[status] || "default"}>
              {status ? t(`health.${status}`) : t("common.noData")}
            </Badge>
          </div>
        </Card>
        <Card>
          <p className="text-sm text-text-muted">{t("health.region")}</p>
          <p className="mt-1 text-2xl font-bold">{String(health.region || t("common.notAvailable"))}</p>
        </Card>
        <Card>
          <p className="text-sm text-text-muted">{t("health.activeTables")}</p>
          <p className="mt-1 text-2xl font-bold">{Number(health.activeTables) || 0}</p>
        </Card>
        <Card>
          <p className="text-sm text-text-muted">{t("health.uptime")}</p>
          <p className="mt-1 text-2xl font-bold">{String(health.uptime || t("common.notAvailable"))}</p>
        </Card>
      </div>
    </div>
  );
}
