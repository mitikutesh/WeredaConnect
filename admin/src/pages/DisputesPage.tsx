import { useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { DispositionChoiceCard, VerdictMessages } from "@/components/disputes/DisputeVerdict";
import { type Verdict, useDisputeVerdict } from "@/components/disputes/verdictFlow";
import { useLocale } from "@/hooks/useLocale";
import { useDisputes, useEscrowPages } from "@/hooks/useQuery";
import { formatETB, formatDate } from "@/lib/i18n";
import { partyName } from "@/lib/user";

type Row = Record<string, unknown>;

const verdictOf = (row: Row): Verdict | null => {
  const resolution = (row.dispute as Row | undefined)?.resolution;
  return resolution === "farmer_wins" || resolution === "buyer_wins" ? resolution : null;
};

export function DisputesPage() {
  const { t, locale } = useLocale();
  const [tab, setTab] = useState<"open" | "awaiting">("open");
  const open = useDisputes();
  const awaiting = useEscrowPages("dispute_resolved");
  const verdictFlow = useDisputeVerdict(t);
  const { submit, resolvingId, openChoice } = verdictFlow;

  // Both lists come in pages: a status-filtered scan can return a short page with more behind it.
  const disputes = (open.data?.pages ?? []).flatMap((page) => (page.disputes as Row[] | undefined) ?? []);
  const awaitingRows = (awaiting.data?.pages ?? []).flatMap((page) => (page.escrows as Row[] | undefined) ?? []);
  const current = tab === "open" ? open : awaiting;

  const partyColumns = [
    {
      key: "escrowId",
      header: t("escrows.id"),
      // LT-13: the dispute page with its evidence tabs (Trip trail today; FRD-14 adds the rest).
      render: (row: Row) => (
        <Link
          to={`/disputes/${encodeURIComponent(String(row.escrowId || ""))}`}
          className="font-medium text-primary hover:underline"
        >
          {String(row.escrowId || "")}
        </Link>
      ),
    },
    {
      key: "buyerName",
      header: t("escrows.buyer"),
      render: (row: Row) => partyName(row.buyerName, row.buyerId, locale),
    },
    {
      key: "farmerName",
      header: t("escrows.seller"),
      render: (row: Row) => partyName(row.farmerName, row.farmerId, locale),
    },
    {
      key: "amount",
      header: t("escrows.amount"),
      render: (row: Row) => formatETB(Number(row.amount) || 0, locale),
    },
  ];

  const openColumns = [
    ...partyColumns,
    {
      key: "reason",
      header: t("disputes.reason"),
      render: (row: Row) => String((row.dispute as Row | undefined)?.reason ?? ""),
    },
    {
      key: "createdAt",
      header: t("common.date"),
      render: (row: Row) => formatDate(String(row.createdAt || "")),
    },
    {
      key: "status",
      header: t("escrows.status"),
      render: (row: Row) => (
        <Badge variant={row.status === "resolved" ? "success" : "warning"}>
          {t(`disputes.${row.status}`)}
        </Badge>
      ),
    },
    {
      key: "actions",
      header: t("common.actions"),
      render: (row: Row) => {
        if (row.status === "resolved") return null;
        const id = String(row.escrowId || "");
        const isResolving = resolvingId === id;
        return (
          <div className="flex gap-2">
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); submit(id, "farmer_wins"); }}
              disabled={isResolving}
              className="rounded bg-success px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("disputes.farmerWins")}
            </button>
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); submit(id, "buyer_wins"); }}
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

  const awaitingColumns = [
    ...partyColumns,
    {
      key: "verdict",
      header: t("disputes.resolution"),
      render: (row: Row) => {
        const verdict = verdictOf(row);
        return verdict ? t(`disputes.verdict_${verdict}`) : "";
      },
    },
    {
      key: "disposition",
      header: t("disputes.recordedChoice"),
      render: (row: Row) => {
        const dispute = row.dispute as Row | undefined;
        const recorded = dispute?.disposition;
        if (typeof recorded !== "string") return t("disputes.noChoice");
        return (
          <span>
            {t(`disputes.disposition_${recorded}`)}
            {dispute?.dispositionReason ? (
              <span className="block text-xs text-text-muted">{String(dispute.dispositionReason)}</span>
            ) : null}
          </span>
        );
      },
    },
    {
      key: "resolvedAt",
      header: t("disputes.resolvedAt"),
      render: (row: Row) => formatDate(String((row.dispute as Row | undefined)?.resolvedAt || "")),
    },
    {
      key: "actions",
      header: t("common.actions"),
      render: (row: Row) => {
        const verdict = verdictOf(row);
        if (!verdict) return null;
        const id = String(row.escrowId || "");
        return (
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); openChoice(id, verdict); }}
            disabled={resolvingId === id}
            className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50"
          >
            {t("disputes.chooseDisposition")}
          </button>
        );
      },
    },
  ];

  const tabButton = (value: "open" | "awaiting", label: string) => (
    <button
      type="button"
      aria-pressed={tab === value}
      onClick={() => setTab(value)}
      className={`rounded px-3 py-1 text-sm ${
        tab === value ? "bg-primary text-white" : "border border-gray-300 hover:bg-gray-50"
      }`}
    >
      {label}
    </button>
  );

  return (
    <div>
      <h2 className="mb-6 text-xl font-bold">{t("disputes.title")}</h2>
      <div className="mb-4 flex gap-2">
        {tabButton("open", t("disputes.tabOpen"))}
        {tabButton("awaiting", t("disputes.tabAwaiting"))}
      </div>
      <VerdictMessages flow={verdictFlow} />
      <DispositionChoiceCard flow={verdictFlow} t={t} />
      <Card>
        {tab === "open" ? (
          <DataTable columns={openColumns} data={disputes} isLoading={open.isLoading} />
        ) : (
          <>
            <p className="mb-3 text-sm text-text-muted">{t("disputes.awaitingHint")}</p>
            <DataTable columns={awaitingColumns} data={awaitingRows} isLoading={awaiting.isLoading} />
          </>
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
