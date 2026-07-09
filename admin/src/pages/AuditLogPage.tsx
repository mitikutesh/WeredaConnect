import { useState } from "react";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { SearchInput } from "@/components/ui/SearchInput";
import { useLocale } from "@/hooks/useLocale";
import { useAuditLog, useEscrowAudit } from "@/hooks/useQuery";
import { formatDateTime } from "@/lib/i18n";
import { timelineEntityVariant, timelineActionLabel } from "@/lib/transaction";

/** Normalised audit row merged from the unified feed + the escrow audit feed. */
interface AuditRow {
  entityType: string;
  entityId: string;
  action: string;
  actor: string;
  ipAddress: string;
  fromState: string;
  toState: string;
  timestamp: string;
}

function pick(o: Record<string, unknown>, k: string): string {
  return o[k] == null ? "" : String(o[k]);
}

export function AuditLogPage() {
  const { t } = useLocale();
  const [search, setSearch] = useState("");

  // Cross-entity feed: the unified order/delivery/admin trail + the escrow
  // trail (which lives in its own immutable table), merged and sorted.
  const { data: unifiedData, isLoading: unifiedLoading } = useAuditLog();
  const { data: escrowData, isLoading: escrowLoading } = useEscrowAudit("all");

  const unified = ((unifiedData as Record<string, unknown>)?.entries as Record<string, unknown>[]) || [];
  const escrow = ((escrowData as Record<string, unknown>)?.auditEntries as Record<string, unknown>[]) || [];

  const rows: AuditRow[] = [
    ...unified.map((e) => ({
      entityType: pick(e, "entityType"),
      entityId: pick(e, "entityId"),
      action: pick(e, "action"),
      actor: pick(e, "actor"),
      ipAddress: pick(e, "ipAddress"),
      fromState: pick(e, "fromState"),
      toState: pick(e, "toState"),
      timestamp: pick(e, "timestamp"),
    })),
    ...escrow.map((e) => ({
      entityType: "escrow",
      entityId: pick(e, "escrowId"),
      action: pick(e, "action"),
      actor: pick(e, "actor"),
      ipAddress: pick(e, "ipAddress"),
      fromState: pick(e, "fromState"),
      toState: pick(e, "toState"),
      timestamp: pick(e, "timestamp"),
    })),
  ].sort((a, b) => b.timestamp.localeCompare(a.timestamp));

  const filtered = search
    ? rows.filter((r) =>
        Object.values(r).some((v) =>
          v.toLowerCase().includes(search.toLowerCase()),
        ),
      )
    : rows;

  const columns = [
    {
      key: "entityType",
      header: t("audit.type"),
      render: (row: Record<string, unknown>) => (
        <Badge variant={timelineEntityVariant[String(row.entityType)] || "default"}>
          {String(row.entityType)}
        </Badge>
      ),
    },
    {
      key: "action",
      header: t("audit.action"),
      render: (row: Record<string, unknown>) =>
        timelineActionLabel(String(row.action), t),
    },
    { key: "actor", header: t("audit.actor") },
    {
      key: "entityId",
      header: t("audit.target"),
      render: (row: Record<string, unknown>) => (
        <span className="font-mono text-xs">{String(row.entityId).slice(0, 16)}</span>
      ),
    },
    {
      key: "ipAddress",
      header: t("audit.ip"),
      render: (row: Record<string, unknown>) => String(row.ipAddress || "—"),
    },
    {
      key: "details",
      header: t("audit.details"),
      render: (row: Record<string, unknown>) =>
        row.fromState || row.toState
          ? `${String(row.fromState || "—")} → ${String(row.toState || "—")}`
          : "",
    },
    {
      key: "timestamp",
      header: t("audit.timestamp"),
      render: (row: Record<string, unknown>) =>
        row.timestamp ? formatDateTime(String(row.timestamp)) : "",
    },
  ];

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("audit.title")}</h2>
      <Card>
        <div className="mb-4 max-w-full md:max-w-sm">
          <SearchInput value={search} onChange={setSearch} />
        </div>
        <DataTable
          columns={columns}
          data={filtered as unknown as Record<string, unknown>[]}
          isLoading={unifiedLoading || escrowLoading}
        />
      </Card>
    </div>
  );
}
