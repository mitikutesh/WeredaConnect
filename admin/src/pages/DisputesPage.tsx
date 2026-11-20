import { useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { confirmContradictsReason } from "@/lib/evidence";
import { DispositionChoiceCard, VerdictMessages } from "@/components/disputes/DisputeVerdict";
import { type Verdict, useDisputeVerdict } from "@/components/disputes/verdictFlow";
import { useLocale } from "@/hooks/useLocale";
import { useDisputes, useEscrowPages } from "@/hooks/useQuery";
import { formatETB, formatDate } from "@/lib/i18n";
import { isOpenDispute } from "@/lib/disputes";
import { heldLegs, recordedSplit } from "@/lib/legs";
import { partyName } from "@/lib/user";

type Row = Record<string, unknown>;

const verdictOf = (row: Row): Verdict | null => {
  const resolution = (row.dispute as Row | undefined)?.resolution;
  return resolution === "farmer_wins" || resolution === "buyer_wins" || resolution === "partial_refund"
    ? resolution
    : null;
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
          className="font-medium text-ink underline decoration-field underline-offset-2 hover:decoration-ink"
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
      render: (row: Row) => {
        const dispute = row.dispute as Row | undefined;
        // BUG-6: the disputer is the one who confirmed receipt (stamped when the dispute was opened).
        const confirmedByDisputer = (dispute?.handoffEvidence as Row | undefined)?.confirmedByDisputer === true;
        return (
          <span className="inline-flex flex-wrap items-center gap-2">
            {String(dispute?.reason ?? "")}
            {confirmedByDisputer && (
              <Badge variant={confirmContradictsReason(dispute) ? "warning" : "default"}>
                {t("evidence.confirmedByDisputerShort")}
              </Badge>
            )}
          </span>
        );
      },
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
        if (!isOpenDispute(row)) return null;
        const id = String(row.escrowId || "");
        const isResolving = resolvingId === id;
        return (
          <div className="flex gap-2">
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); submit(id, "farmer_wins"); }}
              disabled={isResolving}
              className="rounded-[10px] bg-ink px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("disputes.farmerWins")}
            </button>
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); submit(id, "buyer_wins"); }}
              disabled={isResolving}
              className="rounded-[10px] border border-field bg-ground px-2 py-1 text-xs text-ink hover:bg-paper disabled:opacity-50"
            >
              {t("disputes.buyerWins")}
            </button>
            {/* FUP-173: the per-leg verdict lives on the dispute page; this opens it with the form shown. Only where
                the server takes a split (a ledger escrow, crop leg held), like the dispute page's own button. */}
            {heldLegs(row) && (
              <Link
                to={`/disputes/${encodeURIComponent(id)}?decide=split`}
                onClick={(e) => e.stopPropagation()}
                className="rounded-[10px] border border-field px-2 py-1 text-xs hover:bg-paper"
              >
                {t("legs.decidePerLeg")}
              </Link>
            )}
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
        const split = recordedSplit(row);
        if (!verdict) return "";
        return (
          <span>
            {t(`disputes.verdict_${verdict}`)}
            {split && (
              <span className="block text-xs text-slate">
                {`${t("legs.farmerGets")} ${formatETB(split.farmerAmount, locale)} · ${t("legs.truckerGets")} ${formatETB(
                  split.truckerAmount,
                  locale,
                )} · ${t("legs.buyerGets")} ${formatETB(split.buyerRefund, locale)}`}
              </span>
            )}
          </span>
        );
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
              <span className="block text-xs text-slate">{String(dispute.dispositionReason)}</span>
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
        if (!verdict || (verdict === "partial_refund" && !recordedSplit(row))) return null;
        const id = String(row.escrowId || "");
        return (
          <button
            type="button"
            // ESC-24: a recorded split is replayed with its own amounts (another split answers ADMIN_010).
            onClick={(e) => { e.stopPropagation(); openChoice(id, verdict, recordedSplit(row) ?? undefined); }}
            disabled={resolvingId === id}
            className="rounded-[10px] border border-field px-2 py-1 text-xs hover:bg-paper disabled:opacity-50"
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
      className={`rounded-[10px] px-3 py-1 text-sm ${
        tab === value ? "bg-ink text-white" : "border border-field hover:bg-paper"
      }`}
    >
      {label}
    </button>
  );

  return (
    <div>
      <h2 className="mb-6 text-[28px] font-extrabold leading-tight tracking-[-0.03em]">{t("disputes.title")}</h2>
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
            <p className="mb-3 text-sm text-slate">{t("disputes.awaitingHint")}</p>
            <DataTable columns={awaitingColumns} data={awaitingRows} isLoading={awaiting.isLoading} />
          </>
        )}
        {current.hasNextPage && (
          <div className="flex justify-center border-t border-line p-4">
            <button
              type="button"
              onClick={() => current.fetchNextPage()}
              disabled={current.isFetchingNextPage}
              className="rounded-[10px] border border-field px-3 py-1 text-sm hover:bg-paper disabled:opacity-50"
            >
              {t("disputes.loadMore")}
            </button>
          </div>
        )}
      </Card>
    </div>
  );
}
