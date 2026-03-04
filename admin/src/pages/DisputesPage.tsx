import { useState } from "react";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useDisputes, useResolveDispute } from "@/hooks/useQuery";
import { formatETB, formatDate } from "@/lib/i18n";

export function DisputesPage() {
  const { t, locale } = useLocale();
  const { data, isLoading } = useDisputes();
  const resolveDispute = useResolveDispute();
  const [resolvingId, setResolvingId] = useState<string | null>(null);

  const disputes = (data as Record<string, unknown>[]) || [];

  function handleResolve(id: string, winner: "farmer" | "buyer") {
    setResolvingId(id);
    resolveDispute.mutate(
      { id, resolution: { winner } },
      { onSettled: () => setResolvingId(null) },
    );
  }

  const columns = [
    { key: "escrowId", header: t("escrows.id") },
    { key: "buyerName", header: t("escrows.buyer") },
    { key: "sellerName", header: t("escrows.seller") },
    {
      key: "amount",
      header: t("escrows.amount"),
      render: (row: Record<string, unknown>) => formatETB(Number(row.amount) || 0, locale),
    },
    { key: "reason", header: t("disputes.reason") },
    {
      key: "createdAt",
      header: t("common.date"),
      render: (row: Record<string, unknown>) => formatDate(String(row.createdAt || "")),
    },
    {
      key: "status",
      header: t("escrows.status"),
      render: (row: Record<string, unknown>) => (
        <Badge variant={row.status === "resolved" ? "success" : "warning"}>
          {t(`disputes.${row.status}`)}
        </Badge>
      ),
    },
    {
      key: "actions",
      header: t("common.actions"),
      render: (row: Record<string, unknown>) => {
        if (row.status === "resolved") return null;
        const id = String(row.escrowId || "");
        const isResolving = resolvingId === id;
        return (
          <div className="flex gap-2">
            <button
              onClick={(e) => { e.stopPropagation(); handleResolve(id, "farmer"); }}
              disabled={isResolving}
              className="rounded bg-success px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("disputes.farmerWins")}
            </button>
            <button
              onClick={(e) => { e.stopPropagation(); handleResolve(id, "buyer"); }}
              disabled={isResolving}
              className="rounded bg-accent px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("disputes.buyerWins")}
            </button>
          </div>
        );
      },
    },
  ];

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("disputes.title")}</h2>
      <Card>
        <DataTable columns={columns} data={disputes} isLoading={isLoading} />
      </Card>
    </div>
  );
}
