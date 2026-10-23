import { useState } from "react";
import { useNavigate } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { SearchInput } from "@/components/ui/SearchInput";
import { useLocale } from "@/hooks/useLocale";
import { useEscrows } from "@/hooks/useQuery";
import { formatETB, formatDate } from "@/lib/i18n";
import { partyName } from "@/lib/user";

const statusVariant: Record<string, "success" | "warning" | "danger" | "info" | "default"> = {
  initiated: "info",
  funded: "info",
  escrowed: "info",
  // FRD-03: frozen for fraud review until an admin releases or refunds it.
  held: "warning",
  released: "success",
  disputed: "danger",
  dispute_resolved: "success",
  refunded: "warning",
  expired: "default",
};

export function EscrowsPage() {
  const { t, locale } = useLocale();
  const [search, setSearch] = useState("");
  const navigate = useNavigate();
  const { data, isLoading } = useEscrows(search ? `search=${encodeURIComponent(search)}` : undefined);

  const resp = data as Record<string, unknown> | undefined;
  const escrows = (resp?.escrows as Record<string, unknown>[]) || [];

  const columns = [
    { key: "escrowId", header: t("escrows.id") },
    {
      key: "buyerName",
      header: t("escrows.buyer"),
      render: (row: Record<string, unknown>) => partyName(row.buyerName, row.buyerId, locale),
    },
    {
      key: "farmerName",
      header: t("escrows.seller"),
      render: (row: Record<string, unknown>) => partyName(row.farmerName, row.farmerId, locale),
    },
    {
      key: "amount",
      header: t("escrows.amount"),
      render: (row: Record<string, unknown>) => formatETB(Number(row.amount) || 0, locale),
    },
    {
      key: "status",
      header: t("escrows.status"),
      render: (row: Record<string, unknown>) => {
        const status = String(row.status || "");
        return (
          <Badge variant={statusVariant[status] || "default"}>
            {t(`escrows.${status}`) || status}
          </Badge>
        );
      },
    },
    {
      key: "createdAt",
      header: t("escrows.createdAt"),
      render: (row: Record<string, unknown>) => formatDate(String(row.createdAt || "")),
    },
  ];

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("escrows.title")}</h2>
      <Card>
        <div className="mb-4 max-w-full md:max-w-sm">
          <SearchInput value={search} onChange={setSearch} />
        </div>
        <DataTable
          columns={columns}
          data={escrows}
          isLoading={isLoading}
          onRowClick={(row) => navigate(`/escrows/${row.escrowId}`)}
        />
      </Card>
    </div>
  );
}
