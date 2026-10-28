import { useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useDisputes, useEscrowPages, useResolveDispute } from "@/hooks/useQuery";
import { formatETB, formatDate } from "@/lib/i18n";
import { partyName } from "@/lib/user";

type Verdict = "farmer_wins" | "buyer_wins";
type Disposition = "return_to_farmer" | "delivered_anyway" | "settle_now" | "wait_for_delivery";

/* FUP-16: when the order or delivery cannot follow a verdict as it is, the admin chooses what
 * happens. Each disposition belongs to one verdict (escrow-protocol.md 1.21.0). */
const dispositionsFor: Record<Verdict, Disposition[]> = {
  buyer_wins: ["return_to_farmer", "delivered_anyway"],
  farmer_wins: ["settle_now", "wait_for_delivery"],
};

const REASON_MIN = 10;
const REASON_MAX = 500;

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
  const resolveDispute = useResolveDispute();
  const [resolvingId, setResolvingId] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [choice, setChoice] = useState<{ id: string; verdict: Verdict } | null>(null);
  const [disposition, setDisposition] = useState<Disposition | null>(null);
  const [reason, setReason] = useState("");

  // Both lists come in pages: a status-filtered scan can return a short page with more behind it.
  const disputes = (open.data?.pages ?? []).flatMap((page) => (page.disputes as Row[] | undefined) ?? []);
  const awaitingRows = (awaiting.data?.pages ?? []).flatMap((page) => (page.escrows as Row[] | undefined) ?? []);
  const current = tab === "open" ? open : awaiting;

  function openChoice(id: string, verdict: Verdict) {
    setChoice({ id, verdict });
    setDisposition(null);
    setReason("");
  }

  function submit(id: string, verdict: Verdict, picked?: { disposition: Disposition; reason: string }) {
    setResolvingId(id);
    setErrorMsg(null);
    setNotice(null);
    // Server contract (ResolveDisputeRequest) requires `resolution` + `action`.
    // Map the winner to the outcome (farmer wins → release funds; buyer wins → refund).
    resolveDispute.mutate(
      {
        id,
        body: {
          resolution: verdict,
          action: verdict === "farmer_wins" ? "release" : "refund",
          ...(picked ?? {}),
        },
      },
      {
        onSettled: () => setResolvingId(null),
        onSuccess: () => {
          setChoice(null);
          setNotice(t("disputes.saved"));
        },
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          if (code === "ADMIN_015") {
            // The goods are moving or not delivered yet: the admin chooses what happens.
            openChoice(id, verdict);
            return;
          }
          const key = code ? `errors.${code}` : "errors.actionFailed";
          const msg = t(key);
          setErrorMsg(msg !== key ? msg : t("errors.actionFailed"));
        },
      },
    );
  }

  const trimmedReason = reason.trim();
  const reasonValid = trimmedReason.length >= REASON_MIN && trimmedReason.length <= REASON_MAX;

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
      {errorMsg && (
        <div
          role="alert"
          className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger"
        >
          {errorMsg}
        </div>
      )}
      {notice && (
        <div
          role="status"
          className="mb-4 rounded border border-success/30 bg-success/10 px-4 py-2 text-sm text-success"
        >
          {notice}
        </div>
      )}
      {choice && (
        <Card className="mb-4">
          <h3 className="font-semibold">{t("disputes.dispositionTitle")}</h3>
          <p className="mt-1 text-sm text-text-muted">
            {t("escrows.id")}: {choice.id} · {t(`disputes.verdict_${choice.verdict}`)}
          </p>
          <p className="mt-2 text-sm">
            {t(choice.verdict === "buyer_wins" ? "disputes.caseGoodsMoving" : "disputes.caseBeforeDelivery")}
          </p>
          <fieldset className="mt-4">
            <legend className="text-sm font-medium">{t("disputes.dispositionLegend")}</legend>
            <div className="mt-2 flex flex-col gap-2">
              {dispositionsFor[choice.verdict].map((d) => (
                <label key={d} className="flex items-start gap-2 text-sm">
                  <input
                    type="radio"
                    name="disposition"
                    value={d}
                    checked={disposition === d}
                    onChange={() => setDisposition(d)}
                    className="mt-1"
                  />
                  <span>
                    <span className="font-medium">{t(`disputes.disposition_${d}`)}</span>
                    <span className="block text-text-muted">{t(`disputes.dispositionHint_${d}`)}</span>
                  </span>
                </label>
              ))}
            </div>
          </fieldset>
          <label htmlFor="disposition-reason" className="mt-4 block text-sm font-medium">
            {t("disputes.dispositionReason")}
          </label>
          <textarea
            id="disposition-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            maxLength={REASON_MAX}
            rows={3}
            aria-describedby="disposition-reason-hint"
            className="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none"
          />
          <p id="disposition-reason-hint" className="text-xs text-text-muted">
            {t("disputes.dispositionReasonHint")}
          </p>
          <div className="mt-4 flex gap-2">
            <button
              type="button"
              onClick={() =>
                disposition && submit(choice.id, choice.verdict, { disposition, reason: trimmedReason })
              }
              disabled={!disposition || !reasonValid || resolvingId === choice.id}
              className="rounded bg-primary px-3 py-1 text-sm text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("disputes.confirmDisposition")}
            </button>
            <button
              type="button"
              onClick={() => setChoice(null)}
              className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50"
            >
              {t("common.cancel")}
            </button>
          </div>
        </Card>
      )}
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
