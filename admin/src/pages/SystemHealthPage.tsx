import { Card } from "@/components/ui/Card";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useSystemHealth } from "@/hooks/useQuery";

export function SystemHealthPage() {
  const { t } = useLocale();
  const { data, isLoading } = useSystemHealth();

  const health = (data as Record<string, unknown>) || {};
  const services = (health.services as Record<string, unknown>[]) || [];

  if (isLoading) {
    return <div className="text-text-muted">{t("common.loading")}</div>;
  }

  const statusVariant: Record<string, "success" | "warning" | "danger"> = {
    healthy: "success",
    degraded: "warning",
    down: "danger",
  };

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("health.title")}</h2>

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <Card>
          <p className="text-sm text-text-muted">{t("health.lambdaErrors")}</p>
          <p className="mt-1 text-2xl font-bold">{Number(health.lambdaErrors) || 0}</p>
        </Card>
        <Card>
          <p className="text-sm text-text-muted">{t("health.apiLatency")}</p>
          <p className="mt-1 text-2xl font-bold">{Number(health.apiLatencyMs) || 0}ms</p>
        </Card>
        <Card>
          <p className="text-sm text-text-muted">{t("health.cronStatus")}</p>
          <div className="mt-1">
            <Badge variant={statusVariant[String(health.cronStatus)] || "default"}>
              {t(`health.${health.cronStatus}`) || String(health.cronStatus || "")}
            </Badge>
          </div>
        </Card>
      </div>

      {services.length > 0 && (
        <Card className="mt-6">
          <h3 className="mb-4 font-semibold">Services</h3>
          <div className="space-y-2">
            {services.map((svc, i) => (
              <div key={i} className="flex items-center justify-between rounded-lg bg-gray-50 p-3">
                <span className="text-sm font-medium">{String(svc.name || "")}</span>
                <Badge variant={statusVariant[String(svc.status)] || "default"}>
                  {t(`health.${svc.status}`) || String(svc.status || "")}
                </Badge>
              </div>
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}
