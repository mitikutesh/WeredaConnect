import type { ReactNode } from "react";
import { Link } from "react-router";
import { Badge } from "@/components/ui/Badge";
import { Card } from "@/components/ui/Card";
import { useLocale } from "@/hooks/useLocale";
import { useEscrowLedger } from "@/hooks/useQuery";
import { formatDateTime, formatETB } from "@/lib/i18n";
import {
  type LedgerRow,
  labelOrRaw,
  ledgerStatusVariant,
  legStateVariant,
  legStatusOf,
  recordedSplit,
} from "@/lib/legs";
import { partyName } from "@/lib/user";

type Row = Record<string, unknown>;

function LegRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex justify-between gap-3">
      <dt className="shrink-0 text-text-muted">{label}</dt>
      <dd className="text-right">{children}</dd>
    </div>
  );
}

/**
 * ESC-24: the transport leg of an escrow, as rows of the page's detail list: the trucker, the
 * payout, and where the leg stands. Read only. A trucker leg is paid by the settlement sweeps or
 * recorded in Manual Payouts, so the only action offered here is the link to that queue.
 * Renders nothing for an escrow without a transport leg.
 */
export function TruckerLegRows({ escrow, showPayout = true }: { escrow: Row; showPayout?: boolean }) {
  const { t, locale } = useLocale();
  const leg = escrow.truckerLeg as Row | undefined;
  const hasPayout = escrow.truckerPayout != null;
  if (!leg && !hasPayout && !escrow.truckerId) return null;
  const state = leg ? String(leg.state || "none") : "";

  return (
    <>
      {(escrow.truckerId || escrow.truckerName) && (
        <LegRow label={t("legs.trucker")}>{partyName(escrow.truckerName, escrow.truckerId, locale)}</LegRow>
      )}
      {showPayout && hasPayout && (
        <LegRow label={t("transactions.truckerPayout")}>{formatETB(Number(escrow.truckerPayout) || 0, locale)}</LegRow>
      )}
      {leg && (
        <LegRow label={t("legs.truckerLeg")}>
          <Badge variant={legStateVariant[state] || "default"}>{labelOrRaw(t, `legs.truckerState_${state}`, state)}</Badge>
        </LegRow>
      )}
      {leg?.blockReason ? (
        <LegRow label={t("legs.blockReason")}>
          {labelOrRaw(t, `legs.block_${String(leg.blockReason)}`, String(leg.blockReason))}
        </LegRow>
      ) : null}
      {leg?.pendingSince ? (
        <LegRow label={t("legs.pendingSince")}>{formatDateTime(String(leg.pendingSince))}</LegRow>
      ) : null}
      {leg?.paidAt ? <LegRow label={t("legs.truckerPaidAt")}>{formatDateTime(String(leg.paidAt))}</LegRow> : null}
      {leg?.telebirrRef ? <LegRow label={t("legs.truckerRef")}>{String(leg.telebirrRef)}</LegRow> : null}
      {leg && (state === "manual_queue" || state === "blocked" || state === "pending") && (
        <p className="text-xs text-text-muted">
          {t(state === "manual_queue" ? "legs.truckerQueueHint" : "legs.truckerRetryHint")}{" "}
          <Link to="/payouts" className="font-medium text-primary hover:underline">
            {t("nav.manualPayouts")}
          </Link>
        </p>
      )}
    </>
  );
}

/** A recorded per-leg verdict (ESC-20), as one row of the page's detail list. */
export function RecordedSplitRow({ escrow }: { escrow: Row }) {
  const { t, locale } = useLocale();
  const split = recordedSplit(escrow);
  if (!split) return null;
  return (
    <LegRow label={t("legs.recordedSplit")}>
      {`${t("legs.farmerGets")} ${formatETB(split.farmerAmount, locale)} · ${t("legs.truckerGets")} ${formatETB(
        split.truckerAmount,
        locale,
      )} · ${t("legs.buyerGets")} ${formatETB(split.buyerRefund, locale)}`}
    </LegRow>
  );
}

const legAmountField: Record<string, string> = {
  crop: "farmerPayout",
  transport: "truckerPayout",
  commission: "commission",
};

/**
 * ESC-24: the ledger of an escrow (ESC-20): its legs with what each still carries, the totals
 * decided so far, and one row per money movement. Read only. An escrow of the legacy model has no
 * ledger: the card is not shown and the page keeps its fields as before.
 */
export function LedgerCard({ escrow }: { escrow: Row }) {
  const { t, locale } = useLocale();
  const legs = legStatusOf(escrow);
  const escrowId = String(escrow.escrowId || "");
  const ledger = useEscrowLedger(escrowId, legs != null);
  if (!legs) return null;

  const rows = (ledger.data?.pages ?? []).flatMap((page) => page.rows ?? []);

  return (
    <Card className="lg:col-span-2">
      <h3 className="mb-1 font-semibold">{t("legs.ledgerTitle")}</h3>
      <p className="mb-4 text-xs text-text-muted">{t("legs.ledgerHint")}</p>

      <ul aria-label={t("legs.legsLabel")} className="mb-4 flex flex-wrap gap-3 text-sm">
        {["crop", "transport", "commission"]
          .filter((leg) => legs[leg])
          .map((leg) => (
            <li key={leg} className="rounded-lg border border-gray-200 px-3 py-2">
              <span className="block text-xs text-text-muted">{t(`legs.leg_${leg}`)}</span>
              <span className="font-medium">{formatETB(Number(escrow[legAmountField[leg]]) || 0, locale)}</span>{" "}
              <Badge variant={legStateVariant[legs[leg]] || "default"}>
                {labelOrRaw(t, `legs.legState_${legs[leg]}`, legs[leg])}
              </Badge>
            </li>
          ))}
        <li className="rounded-lg border border-gray-200 px-3 py-2">
          <span className="block text-xs text-text-muted">{t("legs.releasedTotal")}</span>
          <span className="font-medium">{formatETB(Number(escrow.releasedTotal) || 0, locale)}</span>
        </li>
        <li className="rounded-lg border border-gray-200 px-3 py-2">
          <span className="block text-xs text-text-muted">{t("legs.refundedTotal")}</span>
          <span className="font-medium">{formatETB(Number(escrow.refundedTotal) || 0, locale)}</span>
        </li>
      </ul>

      {ledger.isLoading ? (
        <p role="status" className="text-sm text-text-muted">
          {t("common.loading")}
        </p>
      ) : ledger.isError ? (
        <div role="alert" className="rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
          {t("legs.ledgerLoadFailed")}
        </div>
      ) : rows.length === 0 ? (
        <p className="text-sm text-text-muted">{t("legs.ledgerEmpty")}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <caption className="sr-only">{t("legs.ledgerTitle")}</caption>
            <thead>
              <tr className="border-b border-gray-200 text-xs text-text-muted">
                <th scope="col" className="px-2 py-2 font-medium">{t("legs.colLeg")}</th>
                <th scope="col" className="px-2 py-2 font-medium">{t("legs.colKind")}</th>
                <th scope="col" className="px-2 py-2 font-medium">{t("legs.colPayee")}</th>
                <th scope="col" className="px-2 py-2 text-right font-medium">{t("legs.colAmount")}</th>
                <th scope="col" className="px-2 py-2 font-medium">{t("legs.colStatus")}</th>
                <th scope="col" className="px-2 py-2 font-medium">{t("legs.colReference")}</th>
                <th scope="col" className="px-2 py-2 font-medium">{t("legs.colTimes")}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <LedgerTableRow key={row.ref} row={row} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      {ledger.hasNextPage && (
        <div className="mt-3 flex justify-center">
          <button
            type="button"
            onClick={() => ledger.fetchNextPage()}
            disabled={ledger.isFetchingNextPage}
            className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
          >
            {t("disputes.loadMore")}
          </button>
        </div>
      )}
    </Card>
  );
}

function LedgerTableRow({ row }: { row: LedgerRow }) {
  const { t, locale } = useLocale();
  return (
    <tr className="border-b border-gray-100 align-top">
      <td className="px-2 py-2">
        {(row.legs ?? []).map((leg) => labelOrRaw(t, `legs.leg_${leg}`, leg)).join(", ")}
      </td>
      <td className="px-2 py-2">
        {labelOrRaw(t, `legs.kind_${row.kind}`, row.kind)}
        <span className="block font-mono text-xs text-text-muted">{row.ref}</span>
      </td>
      <td className="px-2 py-2">
        {row.payeeRole ? labelOrRaw(t, `payouts.role_${row.payeeRole}`, row.payeeRole) : ""}
        <span className="block text-xs text-text-muted">{partyName(row.payeeName, row.payeeId, locale)}</span>
      </td>
      <td className="px-2 py-2 text-right font-medium">{formatETB(Number(row.amount) || 0, locale)}</td>
      <td className="px-2 py-2">
        <Badge variant={ledgerStatusVariant[row.status] || "default"}>
          {labelOrRaw(t, `legs.rowStatus_${row.status}`, row.status)}
        </Badge>
        {row.manualReason && (
          <span className="mt-1 block text-xs text-text-muted">
            {labelOrRaw(t, `legs.manual_${row.manualReason}`, row.manualReason)}
          </span>
        )}
        {row.lastCode && row.status !== "done" && (
          <span className="mt-1 block font-mono text-xs text-text-muted">{row.lastCode}</span>
        )}
      </td>
      <td className="px-2 py-2 font-mono text-xs">
        {row.telebirrRef || ""}
        <span className="block text-text-muted">{row.gatewayRef}</span>
      </td>
      <td className="px-2 py-2 text-xs">
        <span className="block">
          {t("legs.timeCreated")} {row.createdAt ? formatDateTime(row.createdAt) : ""}
        </span>
        {row.sentAt && (
          <span className="block">
            {t("legs.timeSent")} {formatDateTime(row.sentAt)}
          </span>
        )}
        {row.doneAt && (
          <span className="block">
            {t("legs.timeDone")} {formatDateTime(row.doneAt)}
          </span>
        )}
      </td>
    </tr>
  );
}
