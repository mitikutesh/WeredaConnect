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

  const resp = data as Record<string, unknown> | undefined;
  const disputes = (resp?.disputes as Record<string, unknown>[]) || [];

  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  function handleResolve(id: string, winner: "farmer" | "buyer") {
    setResolvingId(id);
    setErrorMsg(null);
    // Server contract (ResolveDisputeRequest) requires `resolution` + `action`.
    // Map the winner to the outcome (farmer wins → release funds; buyer wins → refund).
    resolveDispute.mutate(
      {
        id,
        body: {
          resolution: winner === "farmer" ? "farmer_wins" : "buyer_wins",
          action: winner === "farmer" ? "release" : "refund",
        },
      },
      {
        onSettled: () => setResolvingId(null),
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          const key = code ? `errors.${code}` : "errors.actionFailed";
          const msg = t(key);
          setErrorMsg(msg !== key ? msg : t("errors.actionFailed"));
        },
      },
    );
  }

  const columns = [
    { key: "escrowId", header: t("escrows.id") },
    {
      key: "buyerName",
      header: t("escrows.buyer"),
      render: (row: Record<string, unknown>) => String(row.buyerName || row.buyerId || ""),
    },
    {
      key: "farmerName",
      header: t("escrows.seller"),
      render: (row: Record<string, unknown>) => String(row.farmerName || row.farmerId || ""),
    },
    {
      key: "amount",
      header: t("escrows.amount"),
      render: (row: Record<string, unknown>) => formatETB(Number(row.amount) || 0, locale),
    },
    {
      key: "reason",
      header: t("disputes.reason"),
      render: (row: Record<string, unknown>) =>
        String((row.dispute as Record<string, unknown> | undefined)?.reason ?? ""),
    },
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
      {errorMsg && (
        <div className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
          {errorMsg}
        </div>
      )}
      <Card>
        <DataTable columns={columns} data={disputes} isLoading={isLoading} />
      </Card>
    </div>
  );
}
