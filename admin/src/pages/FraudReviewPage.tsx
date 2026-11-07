import { useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import {
  useFraudSignals,
  useFraudAction,
  useHeldEscrows,
  useSettleHeldEscrow,
  type HeldExitResult,
} from "@/hooks/useQuery";
import { formatDateTime, formatETB, type Locale } from "@/lib/i18n";
import { partyName } from "@/lib/user";

const severityVariant: Record<string, "danger" | "warning" | "default"> = {
  high: "danger",
  medium: "warning",
  low: "default",
};

/* FRD-07: the risk tiers of fraud-detection-architecture.md section 16. */
const tierVariant: Record<string, "danger" | "warning" | "default"> = {
  critical: "danger",
  high: "danger",
  medium: "warning",
  low: "default",
};

/* FRD-05: the soft class of the signal catalogue (fraud-detection-architecture.md section 15), as the
 * server's FraudDetectionService.IsSoftSignal has it. A whitelist clears the signal it is given for good
 * and skips only soft signals until its end, so it is offered on soft signals only. */
const SOFT_SIGNAL_TYPES = new Set([
  "GPS_ANOMALY",
  "PRICE_ANOMALY",
  // FRD-08
  "QR_HANDOFF_FAILURE",
  "TELEBIRR_PAYER_MISMATCH",
  "TELEBIRR_PAYER_REFUSED",
  "STALE_VERSION_BURST",
  "SUSPENDED_TOKEN_USE",
  // LT-07
  "GPS_JUMP",
  "GPS_MOCKED",
  // FRD-09 (not KYC_THRESHOLD_STRUCTURING, which is hard)
  "AMOUNT_ANOMALY",
  "NEW_ACCOUNT_LARGE_AMOUNT",
  "CANCEL_AFTER_FUND_RATE",
  "DISPUTE_RATE",
  "IMPOSSIBLE_DELIVERY_TIME",
  // FRD-15 (not SELF_TRADE, which is hard)
  "SHARED_DEVICE_TRADE",
  "REPEATED_PAIR",
]);

/* FRD-05: what a reviewed signal was decided as. */
const resolutionVariant: Record<string, "success" | "danger" | "info"> = {
  approve: "success",
  reject: "danger",
  whitelist: "info",
};

/* FRD-03: the reason the server asks for to release or refund a held escrow, trimmed. */
const REASON_MIN = 10;
const REASON_MAX = 500;

/** FRD-05: the action response (fraud-detection-architecture.md section 19). */
interface OpenEscrow {
  escrowId: string;
  status: string;
  party: string;
  amount: number;
}

interface FraudActionResult {
  resolution?: string;
  userId?: string;
  suspended?: boolean;
  sessionsEnded?: boolean;
  openEscrows?: OpenEscrow[];
  openEscrowsComplete?: boolean;
  whitelistUntil?: string;
}

/** FRD-07: a user's risk score as the list returns it. */
interface SignalUser {
  userId: string;
  displayName?: string;
  riskScore: number;
  enforcementScore: number;
  riskTier: string;
  truncated?: boolean;
  scoreAvailable: boolean;
}

type SignalRow = Record<string, unknown>;

interface UserGroup {
  userId: string;
  user?: SignalUser;
  signals: SignalRow[];
}

/** FRD-07: an escrow of the held queue (the admin escrow DTO). */
interface HeldEscrow extends Record<string, unknown> {
  escrowId: string;
  deliveryId?: string;
  orderId?: string;
  buyerId?: string;
  buyerName?: string;
  farmerId?: string;
  farmerName?: string;
  amount?: number;
  holdReason?: string;
  heldAt?: string;
  heldFor?: string;
  heldFromStatus?: string;
  dispute?: { resolution?: string };
}

type Tab = "pending" | "reviewed" | "held";
type HeldOp = "release" | "refund";
type Translate = (key: string) => string;

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

/** The text of a key, or null when the locale file does not have it. */
function labelOf(t: Translate, key: string): string | null {
  const text = t(key);
  return text === key ? null : text;
}

/** FRD-07: the signals of every loaded page, grouped by user in first appearance order (newest first). */
function groupByUser(pages: { signals?: unknown[]; users?: unknown[] }[]): UserGroup[] {
  const users = new Map<string, SignalUser>();
  for (const page of pages) {
    for (const u of (page.users ?? []) as SignalUser[]) {
      if (!users.has(u.userId)) users.set(u.userId, u);
    }
  }
  const groups = new Map<string, UserGroup>();
  for (const page of pages) {
    for (const s of (page.signals ?? []) as SignalRow[]) {
      const userId = String(s.userId ?? "");
      let group = groups.get(userId);
      if (!group) {
        group = { userId, user: users.get(userId), signals: [] };
        groups.set(userId, group);
      }
      group.signals.push(s);
    }
  }
  return [...groups.values()];
}

/* FRD-03, section 18.3: an escrow held from a dispute verdict keeps the verdict's direction. */
function allowedOps(escrow: HeldEscrow): HeldOp[] {
  if (escrow.heldFromStatus === "dispute_resolved") {
    if (escrow.dispute?.resolution === "farmer_wins") return ["release"];
    if (escrow.dispute?.resolution === "buyer_wins") return ["refund"];
  }
  return ["release", "refund"];
}

function Alert({ children }: { children: string }) {
  return (
    <div role="alert" className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
      {children}
    </div>
  );
}

export function FraudReviewPage() {
  const { t, locale } = useLocale();
  const [tab, setTab] = useState<Tab>("pending");

  const tabButton = (value: Tab, label: string) => (
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
      <h2 className="text-xl font-bold">{t("fraud.title")}</h2>
      <p className="mb-4 mt-1 max-w-3xl text-sm text-text-muted">{t("fraud.subtitle")}</p>
      <div className="mb-4 flex flex-wrap gap-2">
        {tabButton("pending", t("fraud.tabPending"))}
        {tabButton("reviewed", t("fraud.tabReviewed"))}
        {tabButton("held", t("fraud.tabHeld"))}
      </div>
      {tab === "held" ? (
        <HeldEscrowQueue t={t} locale={locale} />
      ) : (
        <SignalsByUser key={tab} status={tab} t={t} locale={locale} />
      )}
    </div>
  );
}

function SignalsByUser({ status, t, locale }: { status: "pending" | "reviewed"; t: Translate; locale: Locale }) {
  const { data, isLoading, isError, hasNextPage, fetchNextPage, isFetchingNextPage } = useFraudSignals(status);
  const fraudAction = useFraudAction();

  const groups = groupByUser(data?.pages ?? []);

  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [result, setResult] = useState<FraudActionResult | null>(null);
  // Reject suspends the account, so it asks once more.
  const [confirmingReject, setConfirmingReject] = useState<string | null>(null);

  function handleAction(id: string, action: string) {
    setErrorMsg(null);
    setResult(null);
    setConfirmingReject(null);
    fraudAction.mutate(
      { id, action: { action } },
      {
        onSuccess: (res: unknown) => setResult((res ?? {}) as FraudActionResult),
        onError: (err: unknown) => {
          const { code, status: httpStatus } = err as { code?: string; status?: number };
          // This page's wording first: errors.* is shared with other pages, and ADMIN_006 also
          // answers 503 when the signals table is not configured.
          const scoped =
            code && !(code === "ADMIN_006" && httpStatus !== 404) ? labelOf(t, `fraud.error_${code}`) : null;
          setErrorMsg(scoped ?? (code ? labelOf(t, `errors.${code}`) : null) ?? t("errors.actionFailed"));
        },
      },
    );
  }

  const columns = [
    { key: "signalType", header: t("fraud.signalType") },
    {
      key: "severity",
      header: t("fraud.severity"),
      render: (row: SignalRow) => {
        const sev = String(row.severity || "");
        return <Badge variant={severityVariant[sev] || "default"}>{t(`fraud.${sev}`) || sev}</Badge>;
      },
    },
    {
      key: "createdAt",
      header: t("fraud.firstSeen"),
      render: (row: SignalRow) => (row.createdAt ? formatDateTime(String(row.createdAt)) : ""),
    },
    {
      key: "entity",
      header: t("fraud.entity"),
      render: (row: SignalRow) => (
        <div className="space-y-1">
          {row.entityId ? (
            <p className="text-xs text-text-muted">
              {String(row.entityType ?? "")} {String(row.entityId)}
            </p>
          ) : null}
          {row.orderId ? (
            <Link to={`/transactions/${String(row.orderId)}`} className="text-xs text-primary hover:underline">
              {t("fraud.timeline")}
            </Link>
          ) : null}
        </div>
      ),
    },
    {
      key: "status",
      header: t("escrows.status"),
      render: (row: SignalRow) => {
        const resolution = String(row.resolution || "");
        if (row.status === "reviewed" && resolutionVariant[resolution]) {
          return <Badge variant={resolutionVariant[resolution]}>{t(`fraud.resolution_${resolution}`)}</Badge>;
        }
        return (
          <Badge variant={row.status === "reviewed" ? "success" : "warning"}>
            {t(`fraud.${String(row.status)}`)}
          </Badge>
        );
      },
    },
    {
      key: "actions",
      header: t("common.actions"),
      render: (row: SignalRow) => {
        if (row.status === "reviewed") return null;
        const id = String(row.signalId || "");
        if (confirmingReject === id) {
          return (
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-xs text-danger">{t("fraud.confirmReject")}</span>
              <button
                onClick={(e) => {
                  e.stopPropagation();
                  handleAction(id, "reject");
                }}
                className="rounded bg-danger px-2 py-1 text-xs text-white hover:opacity-90"
              >
                {t("fraud.confirmRejectButton")}
              </button>
              <button
                onClick={(e) => {
                  e.stopPropagation();
                  setConfirmingReject(null);
                }}
                className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50"
              >
                {t("common.cancel")}
              </button>
            </div>
          );
        }
        return (
          <div className="flex gap-2">
            <button
              onClick={(e) => {
                e.stopPropagation();
                handleAction(id, "approve");
              }}
              className="rounded bg-success px-2 py-1 text-xs text-white hover:opacity-90"
            >
              {t("fraud.approve")}
            </button>
            <button
              onClick={(e) => {
                e.stopPropagation();
                setConfirmingReject(id);
              }}
              className="rounded bg-danger px-2 py-1 text-xs text-white hover:opacity-90"
            >
              {t("fraud.reject")}
            </button>
            {SOFT_SIGNAL_TYPES.has(String(row.signalType)) && (
              <button
                onClick={(e) => {
                  e.stopPropagation();
                  handleAction(id, "whitelist");
                }}
                className="rounded bg-gray-500 px-2 py-1 text-xs text-white hover:opacity-90"
              >
                {t("fraud.whitelist")}
              </button>
            )}
          </div>
        );
      },
    },
  ];

  function renderResult(r: FraudActionResult) {
    if (r.resolution === "approve") {
      return <p>{t("fraud.result_approve")}</p>;
    }
    if (r.resolution === "whitelist") {
      return (
        <p>
          {fill(t("fraud.result_whitelist"), {
            date: r.whitelistUntil ? formatDateTime(r.whitelistUntil) : "",
          })}
        </p>
      );
    }
    const escrows = r.openEscrows ?? [];
    return (
      <div className="space-y-2">
        <p>{fill(t("fraud.result_reject"), { userId: r.userId ?? "" })}</p>
        {r.sessionsEnded === false && <p className="text-warning">{t("fraud.result_sessionsNotEnded")}</p>}
        <p className="font-semibold">{t("fraud.openEscrows")}</p>
        <p className="text-text-muted">{t("fraud.openEscrowsHint")}</p>
        {escrows.length === 0 ? (
          <p>{t("fraud.noOpenEscrows")}</p>
        ) : (
          <ul className="list-disc pl-5">
            {escrows.map((e) => (
              <li key={e.escrowId}>
                {e.escrowId}: {t(`users.${e.party}`)}, {t(`escrows.${e.status}`)},{" "}
                {formatETB(Number(e.amount) || 0, locale)}
              </li>
            ))}
          </ul>
        )}
        {r.openEscrowsComplete === false && <p className="text-warning">{t("fraud.openEscrowsIncomplete")}</p>}
      </div>
    );
  }

  function riskBadge(user: SignalUser | undefined) {
    if (!user) return null;
    if (!user.scoreAvailable) {
      return <Badge variant="warning">{t("fraud.scoreUnavailable")}</Badge>;
    }
    const tier = labelOf(t, `fraud.tier_${user.riskTier}`) ?? user.riskTier;
    return (
      <Badge variant={tierVariant[user.riskTier] ?? "default"}>
        {fill(t("fraud.riskScore"), { score: String(user.riskScore), tier })}
      </Badge>
    );
  }

  let body;
  if (isLoading) {
    body = (
      <Card>
        <div className="flex items-center justify-center py-12 text-text-muted">{t("common.loading")}</div>
      </Card>
    );
  } else if (groups.length === 0) {
    body = isError ? null : (
      <Card>
        <div className="flex items-center justify-center py-12 text-text-muted">
          {t(status === "pending" ? "fraud.emptyPending" : "fraud.emptyReviewed")}
        </div>
      </Card>
    );
  } else {
    body = groups.map((group) => (
      <section key={group.userId} aria-label={partyName(group.user?.displayName, group.userId, locale)} className="mb-4">
        <Card>
          <div className="mb-3 flex flex-wrap items-start justify-between gap-2">
            <div>
              <Link to={`/users/${group.userId}`} className="font-semibold text-primary hover:underline">
                {partyName(group.user?.displayName, group.userId, locale)}
              </Link>
              {group.user?.displayName && <p className="text-xs text-text-muted">{group.userId}</p>}
            </div>
            {riskBadge(group.user)}
          </div>
          {group.user?.truncated && <p className="mb-2 text-xs text-danger">{t("fraud.scoreTruncated")}</p>}
          <DataTable columns={columns} data={group.signals} />
        </Card>
      </section>
    ));
  }

  return (
    <div>
      <p className="mb-4 text-sm text-text-muted">{t("fraud.hourNote")}</p>
      {errorMsg && <Alert>{errorMsg}</Alert>}
      {result && (
        <div role="status" className="mb-4 rounded border border-info/30 bg-info/10 px-4 py-3 text-sm">
          {renderResult(result)}
        </div>
      )}
      {isError && <Alert>{t("fraud.loadFailed")}</Alert>}
      {body}
      {hasNextPage && (
        <div className="flex justify-center p-4">
          <button
            type="button"
            onClick={() => fetchNextPage()}
            disabled={isFetchingNextPage}
            className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
          >
            {t("fraud.loadMore")}
          </button>
        </div>
      )}
    </div>
  );
}

function HeldEscrowQueue({ t, locale }: { t: Translate; locale: Locale }) {
  const { data, isLoading, isError, hasNextPage, fetchNextPage, isFetchingNextPage } = useHeldEscrows();
  const settle = useSettleHeldEscrow();
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [confirming, setConfirming] = useState<{ escrowId: string; op: HeldOp } | null>(null);
  const [submitting, setSubmitting] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const escrows = (data?.pages ?? []).flatMap((page) => (page.escrows ?? []) as HeldEscrow[]);

  function submit(escrow: HeldEscrow, op: HeldOp) {
    const reason = (reasons[escrow.escrowId] ?? "").trim();
    setSubmitting(escrow.escrowId);
    setErrorMsg(null);
    setNotice(null);
    settle.mutate(
      { escrow, op, reason },
      {
        onSuccess: (res: HeldExitResult) => {
          // 202 ESCROW_034: the settlement waits for an admin in the manual payout queue.
          setNotice(res?.error?.code === "ESCROW_034" ? t("errors.ESCROW_034") : t(`fraud.settled_${op}`));
          setReasons((prev) => ({ ...prev, [escrow.escrowId]: "" }));
        },
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          const scoped = code ? (labelOf(t, `fraud.error_${code}`) ?? labelOf(t, `errors.${code}`)) : null;
          setErrorMsg(scoped ?? t("errors.actionFailed"));
        },
        onSettled: () => {
          setSubmitting(null);
          setConfirming(null);
        },
      },
    );
  }

  const columns = [
    {
      key: "escrow",
      header: t("fraud.escrow"),
      render: (row: HeldEscrow) => (
        <div className="space-y-1">
          <Link to={`/escrows/${row.escrowId}`} className="font-medium text-primary hover:underline">
            {row.escrowId}
          </Link>
          {row.orderId && (
            <div>
              <Link to={`/transactions/${row.orderId}`} className="text-xs text-primary hover:underline">
                {t("fraud.timeline")}
              </Link>
            </div>
          )}
        </div>
      ),
    },
    {
      key: "parties",
      header: t("fraud.parties"),
      render: (row: HeldEscrow) => (
        <div className="space-y-1 text-xs">
          <p>
            {t("users.buyer")}: {partyName(row.buyerName, row.buyerId, locale)}
          </p>
          <p>
            {t("users.farmer")}: {partyName(row.farmerName, row.farmerId, locale)}
          </p>
        </div>
      ),
    },
    {
      key: "amount",
      header: t("escrows.amount"),
      render: (row: HeldEscrow) => <span className="font-semibold">{formatETB(Number(row.amount) || 0, locale)}</span>,
    },
    {
      key: "hold",
      header: t("fraud.holdReason"),
      render: (row: HeldEscrow) => (
        <div className="space-y-1">
          <Badge variant="warning">
            {labelOf(t, `fraud.holdReason_${row.holdReason}`) ?? String(row.holdReason ?? "")}
          </Badge>
          {row.heldFor && (
            <p className="text-xs">
              {t("fraud.heldFor")}: {labelOf(t, `fraud.heldFor_${row.heldFor}`) ?? row.heldFor}
            </p>
          )}
          {row.heldFromStatus && (
            <p className="text-xs text-text-muted">
              {t("fraud.heldFrom")}: {labelOf(t, `escrows.${row.heldFromStatus}`) ?? row.heldFromStatus}
            </p>
          )}
          {row.heldAt && <p className="text-xs text-text-muted">{formatDateTime(row.heldAt)}</p>}
        </div>
      ),
    },
    {
      key: "actions",
      header: t("common.actions"),
      render: (row: HeldEscrow) => {
        const reason = reasons[row.escrowId] ?? "";
        const length = reason.trim().length;
        const reasonOk = length >= REASON_MIN && length <= REASON_MAX;
        const busy = submitting === row.escrowId;
        const ops = allowedOps(row);
        const amount = formatETB(Number(row.amount) || 0, locale);

        if (confirming?.escrowId === row.escrowId) {
          const op = confirming.op;
          return (
            <div className="space-y-2 md:min-w-56">
              <p className="text-sm">{fill(t(`fraud.confirm_${op}`), { amount })}</p>
              <div className="flex gap-2">
                <button
                  type="button"
                  onClick={() => submit(row, op)}
                  disabled={busy}
                  className={`rounded px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50 ${
                    op === "release" ? "bg-success" : "bg-warning"
                  }`}
                >
                  {t(`fraud.confirmButton_${op}`)}
                </button>
                <button
                  type="button"
                  onClick={() => setConfirming(null)}
                  disabled={busy}
                  className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50"
                >
                  {t("common.cancel")}
                </button>
              </div>
            </div>
          );
        }

        return (
          <div className="flex flex-col gap-2 md:min-w-56">
            {/* A wrapping label: the table renders each row twice (desktop and mobile), so no ids. */}
            <label className="flex flex-col gap-1 text-xs text-text-muted">
              {t("fraud.reason")}
              <textarea
                value={reason}
                onChange={(e) => setReasons((prev) => ({ ...prev, [row.escrowId]: e.target.value }))}
                placeholder={t("fraud.reasonPlaceholder")}
                maxLength={REASON_MAX}
                rows={2}
                className="w-full rounded border border-gray-300 px-2 py-1 text-sm text-text focus:border-primary focus:outline-none"
              />
            </label>
            {ops.length === 1 && <p className="text-xs text-warning">{t(`fraud.verdictOnly_${ops[0]}`)}</p>}
            <div className="flex gap-2">
              {ops.includes("release") && (
                <button
                  type="button"
                  onClick={() => setConfirming({ escrowId: row.escrowId, op: "release" })}
                  disabled={!reasonOk || busy}
                  className="rounded bg-success px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
                >
                  {t("fraud.release")}
                </button>
              )}
              {ops.includes("refund") && (
                <button
                  type="button"
                  onClick={() => setConfirming({ escrowId: row.escrowId, op: "refund" })}
                  disabled={!reasonOk || busy}
                  className="rounded bg-warning px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
                >
                  {t("fraud.refund")}
                </button>
              )}
            </div>
          </div>
        );
      },
    },
  ];

  return (
    <div>
      <p className="mb-4 max-w-3xl text-sm text-text-muted">{t("fraud.heldHint")}</p>
      {errorMsg && <Alert>{errorMsg}</Alert>}
      {isError && <Alert>{t("fraud.loadFailed")}</Alert>}
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
          {!isLoading && escrows.length === 0 ? (
            <div className="flex items-center justify-center py-12 text-text-muted">{t("fraud.emptyHeld")}</div>
          ) : (
            <DataTable columns={columns} data={escrows} isLoading={isLoading} />
          )}
          {hasNextPage && (
            <div className="flex justify-center border-t border-gray-200 p-4">
              <button
                type="button"
                onClick={() => fetchNextPage()}
                disabled={isFetchingNextPage}
                className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
              >
                {t("fraud.loadMore")}
              </button>
            </div>
          )}
        </Card>
      )}
    </div>
  );
}
