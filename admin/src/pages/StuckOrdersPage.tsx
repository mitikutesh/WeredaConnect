import { useState } from "react";
import { Link } from "react-router";
import { Badge } from "@/components/ui/Badge";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { useLocale } from "@/hooks/useLocale";
import { useStuckOrders } from "@/hooks/useQuery";
import { formatDateTime, formatETB } from "@/lib/i18n";
import { type StuckOrder, type StuckSource, labelOrRaw, stuckAge } from "@/lib/legs";
import { orderStatusLabel, orderStatusVariant } from "@/lib/transaction";
import { partyName } from "@/lib/user";

type Row = Record<string, unknown>;

const sources: StuckSource[] = ["queue", "deadline"];

/**
 * ESC-24: the stuck orders. "Admin queue" is the stuck-order queue of ESC-06 (an order escrow held
 * 21 days whose order is not done); "Missed deadlines" the orders whose commitment deadline passed
 * and the sweep did not act on it (ESC-16), then the accepted orders still not picked up after the
 * overdue notice went out (FUP-170). Read only: each row links to its transaction, where the
 * admin sees the whole order, and the money moves only through the existing actions.
 */
export function StuckOrdersPage() {
  const { t, locale } = useLocale();
  const [source, setSource] = useState<StuckSource>("queue");
  const queue = useStuckOrders("queue");
  const deadline = useStuckOrders("deadline");
  const current = source === "queue" ? queue : deadline;
  const rows = (current.data?.pages ?? []).flatMap((page) => page.orders ?? []) as unknown as Row[];
  const order = (row: Row) => row as unknown as StuckOrder;

  const columns = [
    {
      key: "orderId",
      header: t("stuckOrders.order"),
      render: (row: Row) => (
        <Link
          to={`/transactions/${encodeURIComponent(order(row).orderId)}`}
          className="font-medium text-primary hover:underline"
        >
          {order(row).orderId}
        </Link>
      ),
    },
    {
      key: "reason",
      header: t("stuckOrders.reason"),
      render: (row: Row) => (
        <Badge variant="warning">{labelOrRaw(t, `stuckOrders.reason_${order(row).reason}`, order(row).reason)}</Badge>
      ),
    },
    {
      key: "stuckSince",
      header: t("stuckOrders.since"),
      render: (row: Row) => (
        <span>
          {stuckAge(order(row).ageHours, t)}
          <span className="block text-xs text-text-muted">
            {order(row).stuckSince ? formatDateTime(order(row).stuckSince) : ""}
          </span>
        </span>
      ),
    },
    {
      key: "status",
      header: t("escrows.status"),
      render: (row: Row) => (
        <Badge variant={orderStatusVariant[order(row).status] || "default"}>
          {orderStatusLabel(order(row).status, t)}
        </Badge>
      ),
    },
    {
      key: "buyer",
      header: t("transactions.buyer"),
      render: (row: Row) => partyName(row.buyerName, row.buyerId, locale),
    },
    {
      key: "farmer",
      header: t("transactions.seller"),
      render: (row: Row) => partyName(row.farmerName, row.farmerId, locale),
    },
    {
      key: "trucker",
      header: t("transactions.trucker"),
      render: (row: Row) => partyName(row.truckerName, row.truckerId, locale) || t("transactions.noTrucker"),
    },
    {
      key: "orderTotal",
      header: t("transactions.orderTotal"),
      render: (row: Row) => formatETB(Number(row.orderTotal) || 0, locale),
    },
  ];

  return (
    <div>
      <h2 className="mb-2 text-xl font-bold">{t("stuckOrders.title")}</h2>
      <p className="mb-6 text-sm text-text-muted">{t("stuckOrders.subtitle")}</p>
      <div className="mb-4 flex gap-2">
        {sources.map((value) => (
          <button
            key={value}
            type="button"
            aria-pressed={source === value}
            onClick={() => setSource(value)}
            className={`rounded px-3 py-1 text-sm ${
              source === value ? "bg-primary text-white" : "border border-gray-300 hover:bg-gray-50"
            }`}
          >
            {t(`stuckOrders.tab_${value}`)}
          </button>
        ))}
      </div>
      <Card>
        <p className="mb-3 text-sm text-text-muted">{t(`stuckOrders.hint_${source}`)}</p>
        {current.isError ? (
          <div role="alert" className="rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
            {t("stuckOrders.loadFailed")}
          </div>
        ) : !current.isLoading && rows.length === 0 ? (
          <p className="py-8 text-center text-sm text-text-muted">{t("stuckOrders.empty")}</p>
        ) : (
          <DataTable columns={columns} data={rows} isLoading={current.isLoading} />
        )}
        {current.hasNextPage && (
          <div className="flex justify-center border-t border-gray-200 p-4">
            <button
              type="button"
              onClick={() => current.fetchNextPage()}
              disabled={current.isFetchingNextPage}
              className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
            >
              {t("disputes.loadMore")}
            </button>
          </div>
        )}
      </Card>
    </div>
  );
}
