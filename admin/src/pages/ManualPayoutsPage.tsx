import { useEffect, useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useManualPayouts, useCompleteManualPayout, useManualPayoutClaim } from "@/hooks/useQuery";
import { formatETB, formatDateTime } from "@/lib/i18n";
import { partyName } from "@/lib/user";

/* Manual payout queue (ESC-02). The live Telebirr gateway has no payout API, so a
 * release or refund it cannot make waits here. One table row per payee leg: the
 * admin pays the shown amount to the shown phone in the merchant portal, pastes the
 * portal transaction id and confirms. The last leg of an escrow settles it.
 * FUP-05: an admin claims a leg before paying it, so every other admin sees that it
 * is being paid and cannot record it; one portal transaction id records one leg. The
 * page asks for the claim first: the reference field and the record button appear only
 * on a leg the viewer holds a live claim on. The queue reloads every 30 seconds and when
 * the window gets focus, so other admins' claims show up. */

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
  /* FUP-05: the admin who claimed this leg (30 minutes), whether that is the viewer, and whether the
   * claim still lived when the queue was read. A lapsed claim is shown for 24 hours. */
  claimedBy?: string;
  claimedAt?: string;
  claimExpiresAt?: string;
  claimedByMe?: boolean;
  claimLive?: boolean;
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

/* FUP-05: the server names where a refused reference is used in a fixed form,
 * "... on escrow <escrowId>, leg <ref>.", so the page can say it in the admin's language. */
const USED_ON = /on escrow (\S+), leg (\S+?)\.$/;

function referenceUsedOn(message: string | undefined): { escrowId: string; ref: string } | null {
  const match = USED_ON.exec(message ?? "");
  return match ? { escrowId: match[1], ref: match[2] } : null;
}

/* FUP-05: an ESCROW_046 message starts "Another admin (<id>) has claimed this leg until <time>." */
const CLAIM_HOLDER = /^Another admin \((.+?)\) has claimed this leg/;

function claimHolder(message: string | undefined): string | null {
  return CLAIM_HOLDER.exec(message ?? "")?.[1] ?? null;
}

type ClaimState = "mine" | "mineLapsed" | "other" | "otherLapsed" | "none";

/* FUP-05: a claim lives while the server said so and its end is still ahead on this clock; the
 * page treats its own claim as lost the moment it lapses, without waiting for the next reload. */
function claimState(leg: PayoutLeg, now: number): ClaimState {
  if (!leg.claimedBy) return "none";
  const end = leg.claimExpiresAt ? Date.parse(leg.claimExpiresAt) : NaN;
  const live = leg.claimLive !== false && Number.isFinite(end) && end > now;
  if (leg.claimedByMe) return live ? "mine" : "mineLapsed";
  return live ? "other" : "otherLapsed";
}

/* The current time, moved on every 15 seconds so a claim that lapses changes the row. */
function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);
  return now;
}

export function ManualPayoutsPage() {
  const { t, locale } = useLocale();
  const { data, isLoading, isError, hasNextPage, fetchNextPage, isFetchingNextPage } = useManualPayouts();
  const complete = useCompleteManualPayout();
  const claim = useManualPayoutClaim();
  const now = useNow(15_000);
  const [refs, setRefs] = useState<Record<string, string>>({});
  const [claiming, setClaiming] = useState<string | null>(null);
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

  function errorText(err: unknown): string {
    const { code, message } = err as { code?: string; message?: string };
    // FUP-05: a reference already recorded elsewhere names the escrow and leg it is on.
    const usedOn = code === "ESCROW_047" ? referenceUsedOn(message) : null;
    if (usedOn) return fill(t("payouts.duplicateRef"), usedOn);
    return code ? label(`errors.${code}`, t("errors.actionFailed")) : t("errors.actionFailed");
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
          // FUP-05: refused because another admin holds the claim. The admin has usually paid
          // already: do not pay again, and talk to the holder so the leg is recorded once.
          const { code, message } = err as { code?: string; message?: string };
          const holder = code === "ESCROW_046" ? claimHolder(message) : null;
          setErrorMsg(holder ? fill(t("payouts.claimedOnRecord"), { admin: holder }) : errorText(err));
          setConfirming(null);
        },
        onSettled: () => setSubmitting(null),
      },
    );
  }

  /* FUP-05: claim the leg before paying it in the portal, renew the claim, or give it back. */
  function changeClaim(row: Row, action: "claim" | "unclaim", renew = false) {
    setClaiming(row.key);
    setErrorMsg(null);
    setNotice(null);
    claim.mutate(
      { escrowId: row.payout.escrowId, ref: row.leg.ref, action },
      {
        onSuccess: () =>
          setNotice(t(action === "unclaim" ? "payouts.unclaimDone" : renew ? "payouts.renewDone" : "payouts.claimDone")),
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          // The leg was recorded, released or busy in between: the queue reloads with what it is now.
          setErrorMsg(code === "ESCROW_035" ? t("payouts.claimChanged") : errorText(err));
        },
        onSettled: () => setClaiming(null),
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

        const state = claimState(row.leg, now);
        const claimTime = row.leg.claimExpiresAt ? formatDateTime(row.leg.claimExpiresAt) : "";

        // FUP-05: another admin is paying this leg; it cannot be paid or recorded here until the
        // claim ends (30 minutes after it was taken or renewed) or its holder gives it back.
        if (state === "other") {
          return (
            <div className="space-y-1 md:max-w-56">
              <Badge variant="warning">{t("payouts.claimedByOther")}</Badge>
              <p className="text-xs text-text-muted">
                {fill(t("payouts.claimedByOtherHint"), { admin: row.leg.claimedBy ?? "", time: claimTime })}
              </p>
            </div>
          );
        }

        const value = refs[row.key] ?? "";
        const busy = submitting === row.key || claiming === row.key;
        const checkPortalFirst = CHECK_PORTAL_FIRST.has(row.payout.reason);
        const claimButton = (
          <button
            type="button"
            onClick={() => changeClaim(row, "claim")}
            disabled={busy}
            className="rounded border border-primary px-2 py-1 text-xs text-primary hover:bg-primary/10 disabled:opacity-50"
          >
            {t("payouts.claim")}
          </button>
        );

        // Claim is the first step: without a live claim of the viewer's own there is nothing to
        // pay or record here. A lapsed claim (the viewer's or another admin's) says that the
        // payment may already have been made.
        if (state !== "mine") {
          const lapsedText =
            state === "mineLapsed"
              ? fill(t("payouts.claimLapsedMine"), { time: claimTime })
              : state === "otherLapsed"
                ? fill(t("payouts.claimLapsedOther"), { admin: row.leg.claimedBy ?? "", time: claimTime })
                : null;
          return (
            <div className="space-y-1 md:max-w-56">
              {lapsedText ? (
                <p role="note" className="text-xs text-warning">
                  {lapsedText}
                </p>
              ) : (
                <p className="text-xs text-text-muted">{t("payouts.claimHint")}</p>
              )}
              {claimButton}
            </div>
          );
        }

        const claimLine = (
          <div className="space-y-1">
            <p className="text-xs text-success">{fill(t("payouts.claimedByYou"), { time: claimTime })}</p>
            <div className="flex gap-2">
              <button
                type="button"
                onClick={() => changeClaim(row, "claim", true)}
                disabled={busy}
                className="rounded border border-primary px-2 py-1 text-xs text-primary hover:bg-primary/10 disabled:opacity-50"
              >
                {t("payouts.renew")}
              </button>
              <button
                type="button"
                onClick={() => changeClaim(row, "unclaim")}
                disabled={busy}
                className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50"
              >
                {t("payouts.unclaim")}
              </button>
            </div>
          </div>
        );
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
            {claimLine}
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
