import { useState } from "react";
import { useNavigate } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { SearchInput } from "@/components/ui/SearchInput";
import { useLocale } from "@/hooks/useLocale";
import { useTransactions } from "@/hooks/useQuery";
import { formatETB, formatDate } from "@/lib/i18n";
import { partyName } from "@/lib/user";
import {
  orderStatusVariant,
  stageVariant,
  orderStatusLabel,
  stageLabel,
} from "@/lib/transaction";

export function TransactionsPage() {
  const { t, locale } = useLocale();
  const [search, setSearch] = useState("");
  const navigate = useNavigate();
  const { data, isLoading } = useTransactions(
    search ? `search=${encodeURIComponent(search)}` : undefined,
  );

  const resp = data as Record<string, unknown> | undefined;
  const transactions = (resp?.transactions as Record<string, unknown>[]) || [];

  const columns = [
    {
      key: "orderId",
      header: t("transactions.orderId"),
      render: (row: Record<string, unknown>) => (
        <span className="font-mono text-xs">
          {String(row.orderId || "").slice(0, 12)}
        </span>
      ),
    },
    {
      key: "buyerName",
      header: t("transactions.buyer"),
      render: (row: Record<string, unknown>) =>
        partyName(row.buyerName, row.buyerId, locale),
    },
    {
      key: "farmerName",
      header: t("transactions.seller"),
      render: (row: Record<string, unknown>) =>
        partyName(row.farmerName, row.farmerId, locale),
    },
    {
      key: "truckerName",
      header: t("transactions.trucker"),
      render: (row: Record<string, unknown>) =>
        row.truckerName || row.truckerId ? (
          partyName(row.truckerName, row.truckerId, locale)
        ) : (
          <span className="text-text-muted">{t("transactions.noTrucker")}</span>
        ),
    },
    {
      key: "cropType",
      header: t("transactions.crop"),
      render: (row: Record<string, unknown>) => String(row.cropType || ""),
    },
    {
      key: "orderTotal",
      header: t("transactions.amount"),
      render: (row: Record<string, unknown>) =>
        formatETB(Number(row.orderTotal) || 0, locale),
    },
    {
      key: "stage",
      header: t("transactions.stage"),
      render: (row: Record<string, unknown>) => {
        const stage = String(row.stage || "");
        return (
          <Badge variant={stageVariant[stage] || "default"}>
            {stageLabel(stage, t)}
          </Badge>
        );
      },
    },
    {
      key: "status",
      header: t("transactions.status"),
      render: (row: Record<string, unknown>) => {
        const status = String(row.status || "");
        return (
          <Badge variant={orderStatusVariant[status] || "default"}>
            {orderStatusLabel(status, t)}
          </Badge>
        );
      },
    },
    {
      key: "createdAt",
      header: t("transactions.createdAt"),
      render: (row: Record<string, unknown>) =>
        formatDate(String(row.createdAt || "")),
    },
  ];

  return (
    <div>
      <h2 className="mb-1 text-xl font-bold">{t("transactions.title")}</h2>
      <p className="mb-6 text-sm text-text-muted">{t("transactions.subtitle")}</p>
      <Card>
        <div className="mb-4 max-w-full md:max-w-sm">
          <SearchInput
            value={search}
            onChange={setSearch}
            placeholder={t("transactions.searchPlaceholder")}
          />
        </div>
        <DataTable
          columns={columns}
          data={transactions}
          isLoading={isLoading}
          onRowClick={(row) => navigate(`/transactions/${row.orderId}`)}
        />
      </Card>
    </div>
  );
}
