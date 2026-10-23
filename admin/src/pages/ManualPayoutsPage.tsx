import { useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useManualPayouts, useCompleteManualPayout } from "@/hooks/useQuery";
import { formatETB, formatDateTime } from "@/lib/i18n";
import { partyName } from "@/lib/user";

/* Manual payout queue (ESC-02). The live Telebirr gateway has no payout API, so a
 * release or refund it cannot make waits here. One table row per payee leg: the
 * admin pays the shown amount to the shown phone in the merchant portal, pastes the
 * portal transaction id and confirms. The last leg of an escrow settles it. */

interface PayoutLeg {
  ref: string;
  payeeRole: string;
  payeeId: string;
  payeeName?: string;
  payeePhone?: string;
  payeeVerified: boolean;
  blockReason?: string;
  amount: number;
  status: "pending" | "paid";
  externalRef?: string;
  paidBy?: string;
  paidAt?: string;
  /* ESC-05: the reference the server sent Telebirr for this leg (release:<id>, release:<id>:trucker, refund:<id>). */
  gatewayRef?: string;
}

interface ManualPayout {
  escrowId: string;
  orderId?: string;
  op: "release" | "refund";
  reason: string;
  escrowStatus: string;
  fromStatus: string;
  deliveryConfirmedAt?: string;
  /* ESC-05: when the settlement lock was taken, which is when the gateway may have been called. */
  settlementLockedAt?: string;
  /* ESC-05: the dispute verdict, if any. A lock from before the verdict can go the other way. */
  disputeResolution?: string;
  queuedAt: string;
  currency: string;
  legs: PayoutLeg[];
  pendingAmount: number;
}

type Row = Record<string, unknown> & {
  key: string;
  payout: ManualPayout;
  leg: PayoutLeg;
};

const OVERDUE_HOURS = 24;

/* The gateway said it paid but gave no reference (GATEWAY_REF_MISSING), or never said
 * whether it paid (GATEWAY_OUTCOME_UNKNOWN, ESC-05): the money may have moved, so the admin
 * looks the payment up in the portal and records its id instead of paying again. */
const REF_MISSING = "GATEWAY_REF_MISSING";
const OUTCOME_UNKNOWN = "GATEWAY_OUTCOME_UNKNOWN";
const CHECK_PORTAL_FIRST = new Set([REF_MISSING, OUTCOME_UNKNOWN]);

/* ESC-05: a payout that goes against the escrow's dispute verdict (a lock taken before it). */
function contradictsVerdict(payout: ManualPayout): boolean {
  return (
    (payout.op === "release" && payout.disputeResolution === "buyer_wins") ||
    (payout.op === "refund" && payout.disputeResolution === "farmer_wins")
  );
}

function hoursSince(iso: string): number {
  const ms = Date.now() - new Date(iso).getTime();
  return Number.isFinite(ms) ? Math.max(0, Math.floor(ms / 3_600_000)) : 0;
}

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

export function ManualPayoutsPage() {
  const { t, locale } = useLocale();
  const { data, isLoading, isError, hasNextPage, fetchNextPage, isFetchingNextPage } = useManualPayouts();
  const complete = useCompleteManualPayout();
  const [refs, setRefs] = useState<Record<string, string>>({});
  const [confirming, setConfirming] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const pages = (data?.pages ?? []) as { payouts?: ManualPayout[] }[];
  const payouts = pages.flatMap((page) => page.payouts ?? []);
  const rows: Row[] = payouts.flatMap((payout) =>
    payout.legs.map((leg) => ({ key: `${payout.escrowId}:${leg.ref}`, payout, leg })),
  );
  const pendingTotal = payouts.reduce((sum, p) => sum + (Number(p.pendingAmount) || 0), 0);

  // A key the locale file does not have falls back to the raw server value.
  function label(key: string, fallback: string): string {
    const text = t(key);
    return text === key ? fallback : text;
  }

  function submit(row: Row) {
    const externalRef = (refs[row.key] ?? "").trim();
    if (!externalRef) return;
    setSubmitting(row.key);
    setErrorMsg(null);
    setNotice(null);
    complete.mutate(
      { escrowId: row.payout.escrowId, ref: row.leg.ref, externalRef },
      {
        onSuccess: () => {
          setNotice(t("payouts.recorded"));
          setRefs((prev) => ({ ...prev, [row.key]: "" }));
          setConfirming(null);
        },
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          setErrorMsg(code ? label(`errors.${code}`, t("errors.actionFailed")) : t("errors.actionFailed"));
          setConfirming(null);
        },
        onSettled: () => setSubmitting(null),
      },
    );
  }

  const columns = [
    {
      key: "escrow",
      header: t("payouts.escrow"),
      render: (row: Row) => (
        <div className="space-y-1">
          <Link
            to={`/escrows/${row.payout.escrowId}`}
            className="font-medium text-primary hover:underline"
          >
            {row.payout.escrowId}
          </Link>
          <div>
            <Badge variant={row.payout.op === "refund" ? "warning" : "info"}>
              {t(`payouts.op_${row.payout.op}`)}
            </Badge>
          </div>
          <p className="text-xs text-text-muted">
            {label(`payouts.reason_${row.payout.reason}`, row.payout.reason)}
          </p>
        </div>
      ),
    },
    {
      key: "order",
      header: t("payouts.order"),
      render: (row: Row) =>
        row.payout.orderId ? (
          <Link
            to={`/transactions/${row.payout.orderId}`}
            className="text-primary hover:underline"
          >
            {row.payout.orderId}
          </Link>
        ) : (
          <span className="text-text-muted">{t("common.notAvailable")}</span>
        ),
    },
    {
      key: "payee",
      header: t("payouts.payee"),
      render: (row: Row) => (
        <div className="space-y-1">
          <Badge>{label(`payouts.role_${row.leg.payeeRole}`, row.leg.payeeRole)}</Badge>
          <p>{partyName(row.leg.payeeName, row.leg.payeeId, locale)}</p>
        </div>
      ),
    },
    {
      key: "phone",
      header: t("payouts.phone"),
      render: (row: Row) => (
        <span className="font-medium">{row.leg.payeePhone || t("common.notAvailable")}</span>
      ),
    },
    {
      key: "amount",
      header: t("payouts.amount"),
      render: (row: Row) => (
        <div className="space-y-1">
          <span className="font-semibold">{formatETB(Number(row.leg.amount) || 0, locale)}</span>
          {row.payout.reason === REF_MISSING && row.leg.status === "pending" && (
            <p className="max-w-56 text-xs text-warning">{t("payouts.refMissingHint")}</p>
          )}
          {row.payout.reason === OUTCOME_UNKNOWN && row.leg.status === "pending" && (
            <>
              <p className="max-w-56 text-xs text-warning">
                {fill(t("payouts.outcomeUnknownHint"), { gatewayRef: row.leg.gatewayRef ?? row.payout.escrowId })}
              </p>
              {row.payout.settlementLockedAt && (
                <p className="text-xs text-text-muted">
                  {fill(t("payouts.lockAge"), { hours: String(hoursSince(row.payout.settlementLockedAt)) })}
                </p>
              )}
            </>
          )}
        </div>
      ),
    },
    {
      key: "queuedAt",
      header: t("payouts.queuedAt"),
      render: (row: Row) => {
        const hours = hoursSince(row.payout.queuedAt);
        return (
          <div className="space-y-1">
            <p>{formatDateTime(row.payout.queuedAt)}</p>
            {hours >= OVERDUE_HOURS ? (
              <Badge variant="danger">{t("payouts.overdue")}</Badge>
            ) : (
              <p className="text-xs text-text-muted">
                {fill(t("payouts.age"), { hours: String(hours) })}
              </p>
            )}
          </div>
        );
      },
    },
    {
      key: "escrowStatus",
      header: t("payouts.escrowStatus"),
      render: (row: Row) => (
        <div className="space-y-1">
          <Badge variant={row.payout.escrowStatus === "disputed" ? "danger" : "info"}>
            {label(`escrows.${row.payout.escrowStatus}`, row.payout.escrowStatus)}
          </Badge>
          {row.payout.escrowStatus !== row.payout.fromStatus && (
            <div>
              <Badge variant="warning">{t("payouts.statusChanged")}</Badge>
            </div>
          )}
          {row.payout.op === "refund" &&
            row.payout.deliveryConfirmedAt &&
            row.payout.deliveryConfirmedAt > row.payout.queuedAt && (
            <p className="max-w-56 text-xs text-warning">{t("payouts.deliveredWarning")}</p>
          )}
          {contradictsVerdict(row.payout) && (
            <p role="alert" className="max-w-56 text-xs text-danger">
              {fill(t("payouts.verdictConflict"), {
                verdict: t(row.payout.disputeResolution === "buyer_wins" ? "disputes.buyerWins" : "disputes.farmerWins"),
              })}
            </p>
          )}
        </div>
      ),
    },
    {
      key: "actions",
      header: t("common.actions"),
      render: (row: Row) => {
        if (row.leg.status === "paid") {
          return (
            <div className="space-y-1">
              <Badge variant="success">{t("payouts.paid")}</Badge>
              <p className="text-xs">{row.leg.externalRef}</p>
              {row.leg.paidBy && (
                <p className="text-xs text-text-muted">
                  {t("payouts.paidBy")}: {row.leg.paidBy}
                </p>
              )}
            </div>
          );
        }

        if (!row.leg.payeeVerified) {
          return (
            <div className="space-y-1">
              <Badge variant="danger">{t("payouts.payeeNotVerified")}</Badge>
              {row.leg.blockReason && (
                <p className="text-xs text-text-muted">{row.leg.blockReason}</p>
              )}
            </div>
          );
        }

        const value = refs[row.key] ?? "";
        const busy = submitting === row.key;
        const checkPortalFirst = CHECK_PORTAL_FIRST.has(row.payout.reason);
        if (confirming === row.key) {
          return (
            <div className="space-y-2">
              <p className="text-sm">
                {fill(t(checkPortalFirst ? "payouts.confirmFound" : "payouts.confirmPaid"), {
                  amount: formatETB(Number(row.leg.amount) || 0, locale),
                  phone: row.leg.payeePhone || row.leg.payeeId,
                })}
              </p>
              <div className="flex gap-2">
                <button
                  type="button"
                  onClick={() => submit(row)}
                  disabled={busy}
                  className="rounded bg-success px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
                >
                  {t("payouts.confirm")}
                </button>
                <button
                  type="button"
                  onClick={() => setConfirming(null)}
                  disabled={busy}
                  className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50"
                >
                  {t("payouts.cancel")}
                </button>
              </div>
            </div>
          );
        }

        return (
          <div className="flex flex-col gap-2 md:min-w-56">
            <input
              type="text"
              value={value}
              onChange={(e) => setRefs((prev) => ({ ...prev, [row.key]: e.target.value }))}
              aria-label={t("payouts.externalRef")}
              placeholder={t("payouts.externalRefPlaceholder")}
              className="w-full rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none"
            />
            <button
              type="button"
              onClick={() => setConfirming(row.key)}
              disabled={!value.trim() || busy}
              className="rounded bg-primary px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
            >
              {t(checkPortalFirst ? "payouts.recordRef" : "payouts.markPaid")}
            </button>
          </div>
        );
      },
    },
  ];

  return (
    <div>
      <div className="mb-6 flex flex-col gap-2 md:flex-row md:items-end md:justify-between">
        <div>
          <h2 className="text-xl font-bold">{t("payouts.title")}</h2>
          <p className="mt-1 max-w-2xl text-sm text-text-muted">{t("payouts.subtitle")}</p>
        </div>
        {payouts.length > 0 && (
          <p className="text-sm text-text-muted">
            {t("payouts.pendingTotal")}:{" "}
            <span className="font-semibold text-text">{formatETB(pendingTotal, locale)}</span>
          </p>
        )}
      </div>
      {errorMsg && (
        <div
          role="alert"
          className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger"
        >
          {errorMsg}
        </div>
      )}
      {isError && (
        <div
          role="alert"
          className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger"
        >
          {t("payouts.loadFailed")}
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
      {!isError && (
        <Card>
          {!isLoading && rows.length === 0 ? (
            <div className="flex items-center justify-center py-12 text-text-muted">
              {t("payouts.empty")}
            </div>
          ) : (
            <DataTable columns={columns} data={rows} isLoading={isLoading} />
          )}
          {hasNextPage && (
            <div className="flex justify-center border-t border-gray-200 p-4">
              <button
                type="button"
                onClick={() => fetchNextPage()}
                disabled={isFetchingNextPage}
                className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
              >
                {t("payouts.loadMore")}
              </button>
            </div>
          )}
        </Card>
      )}
    </div>
  );
}
