import { useState } from "react";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { SearchInput } from "@/components/ui/SearchInput";
import { useLocale } from "@/hooks/useLocale";
import { useEscrowAudit } from "@/hooks/useQuery";
import { formatDateTime } from "@/lib/i18n";

export function AuditLogPage() {
  const { t } = useLocale();
  const [search, setSearch] = useState("");
  // Audit log shows all audit entries (uses escrow audit endpoint with no specific ID filter)
  const { data, isLoading } = useEscrowAudit("all");

  const resp = data as Record<string, unknown> | undefined;
  const entries = (resp?.auditEntries as Record<string, unknown>[]) || [];
  const filtered = search
    ? entries.filter((e) =>
        Object.values(e).some((v) =>
          String(v).toLowerCase().includes(search.toLowerCase()),
        ),
      )
    : entries;

  const columns = [
    { key: "action", header: t("audit.action") },
    { key: "actor", header: t("audit.actor") },
    { key: "target", header: t("audit.target") },
    {
      key: "timestamp",
      header: t("audit.timestamp"),
      render: (row: Record<string, unknown>) =>
        row.timestamp ? formatDateTime(String(row.timestamp)) : "",
    },
    { key: "details", header: t("audit.details") },
  ];

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("audit.title")}</h2>
      <Card>
        <div className="mb-4 max-w-full md:max-w-sm">
          <SearchInput value={search} onChange={setSearch} />
        </div>
        <DataTable columns={columns} data={filtered} isLoading={isLoading} />
      </Card>
    </div>
  );
}
