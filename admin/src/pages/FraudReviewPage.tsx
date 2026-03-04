import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useFraudSignals, useFraudAction } from "@/hooks/useQuery";
import { formatDateTime } from "@/lib/i18n";

const severityVariant: Record<string, "danger" | "warning" | "default"> = {
  high: "danger",
  medium: "warning",
  low: "default",
};

export function FraudReviewPage() {
  const { t } = useLocale();
  const { data, isLoading } = useFraudSignals();
  const fraudAction = useFraudAction();

  const signals = (data as Record<string, unknown>[]) || [];

  function handleAction(id: string, action: string) {
    fraudAction.mutate({ id, action: { action } });
  }

  const columns = [
    { key: "signalType", header: t("fraud.signalType") },
    {
      key: "severity",
      header: t("fraud.severity"),
      render: (row: Record<string, unknown>) => {
        const sev = String(row.severity || "");
        return (
          <Badge variant={severityVariant[sev] || "default"}>
            {t(`fraud.${sev}`) || sev}
          </Badge>
        );
      },
    },
    { key: "userId", header: t("users.name") },
    {
      key: "createdAt",
      header: t("common.date"),
      render: (row: Record<string, unknown>) =>
        row.createdAt ? formatDateTime(String(row.createdAt)) : "",
    },
    {
      key: "status",
      header: t("escrows.status"),
      render: (row: Record<string, unknown>) => (
        <Badge variant={row.status === "reviewed" ? "success" : "warning"}>
          {t(`fraud.${row.status}`)}
        </Badge>
      ),
    },
    {
      key: "actions",
      header: t("common.actions"),
      render: (row: Record<string, unknown>) => {
        if (row.status === "reviewed") return null;
        const id = String(row.signalId || "");
        return (
          <div className="flex gap-2">
            <button
              onClick={(e) => { e.stopPropagation(); handleAction(id, "approve"); }}
              className="rounded bg-success px-2 py-1 text-xs text-white hover:opacity-90"
            >
              {t("fraud.approve")}
            </button>
            <button
              onClick={(e) => { e.stopPropagation(); handleAction(id, "reject"); }}
              className="rounded bg-danger px-2 py-1 text-xs text-white hover:opacity-90"
            >
              {t("fraud.reject")}
            </button>
            <button
              onClick={(e) => { e.stopPropagation(); handleAction(id, "whitelist"); }}
              className="rounded bg-gray-500 px-2 py-1 text-xs text-white hover:opacity-90"
            >
              {t("fraud.whitelist")}
            </button>
          </div>
        );
      },
    },
  ];

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("fraud.title")}</h2>
      <Card>
        <DataTable columns={columns} data={signals} isLoading={isLoading} />
      </Card>
    </div>
  );
}
