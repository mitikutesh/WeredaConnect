import { useState } from "react";
import { Link } from "react-router";
import { Card } from "@/components/ui/Card";
import { DataTable } from "@/components/ui/DataTable";
import { Badge } from "@/components/ui/Badge";
import { useLocale } from "@/hooks/useLocale";
import { useUnappliedPayments, useRecordManualRefund } from "@/hooks/useQuery";
import { formatETB, formatDateTime } from "@/lib/i18n";
import { orderStatusLabel, orderStatusVariant } from "@/lib/transaction";

/* Unapplied payments (FUP-02). Telebirr captured these payments but the webhook could not
 * apply them to their order (a cancelled or missing order, a second payment, a payment it
 * could not process). The admin refunds each one in the merchant portal and records the
 * refund here; recording moves no order and no escrow. */

interface UnappliedPayment {
  orderId: string;
  escrowId: string;
  paymentOrderId?: string;
  merchOrderId?: string;
  actions: string[];
  reasons: string[];
  firstSeenAt: string;
  lastSeenAt: string;
  capturedAmount?: number;
  currency: string;
  orderStatus?: string;
  buyerId?: string;
  /* Not decided for good (only "could not be processed" rows, or Telebirr never named the
   * payment) and the order still waits for a payment: Telebirr may still deliver it. */
  mayStillApply: boolean;
}

type Row = Record<string, unknown> & {
  key: string;
  payment: UnappliedPayment;
};

interface RefundForm {
  telebirrRef: string;
  paymentOrderId: string;
  amount: string;
  reason: string;
}

const MIN_REASON = 10;
const MAX_REASON = 500;

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

function rowKey(p: UnappliedPayment): string {
  return `${p.orderId}:${p.paymentOrderId ?? `merch:${p.merchOrderId ?? ""}`}`;
}

export function UnappliedPaymentsPage() {
  const { t, locale } = useLocale();
  const { data, isLoading, isError, hasNextPage, fetchNextPage, isFetchingNextPage } = useUnappliedPayments();
  const record = useRecordManualRefund();
  const [forms, setForms] = useState<Record<string, RefundForm>>({});
  const [confirming, setConfirming] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState<string | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const pages = (data?.pages ?? []) as { payments?: UnappliedPayment[] }[];
  // The server never lists a payment twice; dedupe anyway so a retried page cannot double a row.
  const seen = new Set<string>();
  const rows: Row[] = pages
    .flatMap((page) => page.payments ?? [])
    .filter((payment) => {
      const key = rowKey(payment);
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    })
    .map((payment) => ({ key: rowKey(payment), payment }));

  // A key the locale file does not have falls back to the raw server value.
  function label(key: string, fallback: string): string {
    const text = t(key);
    return text === key ? fallback : text;
  }

  /* The webhook's reason codes. status_<s> names the order status it found; replay_exhausted
   * carries its cause after a colon; anything else unknown is an exception type name. */
  function reasonLabel(reason: string): string {
    if (reason.startsWith("status_")) {
      return fill(t("unapplied.reason_status"), { status: orderStatusLabel(reason.slice(7), t) });
    }
    if (reason.startsWith("replay_exhausted")) {
      return t("unapplied.reason_replay_exhausted");
    }
    return label(`unapplied.reason_${reason}`, fill(t("unapplied.reason_error"), { code: reason }));
  }

  function formFor(row: Row): RefundForm {
    return (
      forms[row.key] ?? {
        telebirrRef: "",
        paymentOrderId: row.payment.paymentOrderId ?? "",
        amount: row.payment.capturedAmount != null ? String(row.payment.capturedAmount) : "",
        reason: "",
      }
    );
  }

  function update(row: Row, patch: Partial<RefundForm>) {
    setForms((prev) => ({ ...prev, [row.key]: { ...formFor(row), ...patch } }));
  }

  function isComplete(form: RefundForm): boolean {
    const amount = Number(form.amount);
    const reason = form.reason.trim().length;
    return (
      form.telebirrRef.trim().length > 0 &&
      form.paymentOrderId.trim().length > 0 &&
      Number.isFinite(amount) &&
      amount > 0 &&
      reason >= MIN_REASON &&
      reason <= MAX_REASON
    );
  }

  function submit(row: Row) {
    const form = formFor(row);
    if (!isComplete(form)) return;
    setSubmitting(row.key);
    setErrorMsg(null);
    setNotice(null);
    record.mutate(
      {
        orderId: row.payment.orderId,
        body: {
          paymentOrderId: form.paymentOrderId.trim(),
          telebirrRef: form.telebirrRef.trim(),
          amount: Number(form.amount),
          reason: form.reason.trim(),
          // Only a payment the webhook could not name is matched by its checkout.
          ...(row.payment.paymentOrderId ? {} : { merchOrderId: row.payment.merchOrderId }),
        },
      },
      {
        onSuccess: (result) => {
          const r = (result ?? {}) as { alreadyRecorded?: boolean; amountChecked?: boolean };
          setNotice(
            t(
              r.alreadyRecorded
                ? "unapplied.alreadyRecorded"
                : r.amountChecked === false
                  ? "unapplied.recordedUnchecked"
                  : "unapplied.recorded",
            ),
          );
          setForms((prev) => {
            const next = { ...prev };
            delete next[row.key];
            return next;
          });
          setConfirming(null);
        },
        onError: (err: unknown) => {
          const code = (err as { code?: string }).code;
          // A page-specific text first (ESCROW_036 is about this order here, not an escrow).
          setErrorMsg(
            code
              ? label(`unapplied.error_${code}`, label(`errors.${code}`, t("errors.actionFailed")))
              : t("errors.actionFailed"),
          );
          setConfirming(null);
        },
        onSettled: () => setSubmitting(null),
      },
    );
  }

  const columns = [
    {
      key: "order",
      header: t("unapplied.order"),
      render: (row: Row) => (
        <div className="space-y-1">
          <Link to={`/transactions/${row.payment.orderId}`} className="font-medium text-primary hover:underline">
            {row.payment.orderId}
          </Link>
          {row.payment.orderStatus ? (
            <div>
              <Badge variant={orderStatusVariant[row.payment.orderStatus] ?? "default"}>
                {orderStatusLabel(row.payment.orderStatus, t)}
              </Badge>
            </div>
          ) : (
            <p className="text-xs text-text-muted">{t("unapplied.orderMissing")}</p>
          )}
        </div>
      ),
    },
    {
      key: "payment",
      header: t("unapplied.payment"),
      render: (row: Row) =>
        row.payment.paymentOrderId ? (
          <span className="font-medium">{row.payment.paymentOrderId}</span>
        ) : (
          <p className="max-w-56 text-xs text-warning">
            {fill(t("unapplied.paymentUnknown"), { merchOrderId: row.payment.merchOrderId ?? "" })}
          </p>
        ),
    },
    {
      key: "why",
      header: t("unapplied.why"),
      render: (row: Row) => (
        <div className="space-y-1">
          {row.payment.actions.map((action) => (
            <div key={action}>
              <Badge variant={action === "order_payment_unprocessed" ? "warning" : "danger"}>
                {label(`unapplied.action_${action}`, action)}
              </Badge>
            </div>
          ))}
          {row.payment.reasons.length > 0 && (
            <p className="max-w-56 text-xs text-text-muted">{row.payment.reasons.map(reasonLabel).join(", ")}</p>
          )}
        </div>
      ),
    },
    {
      key: "captured",
      header: t("unapplied.captured"),
      render: (row: Row) =>
        row.payment.capturedAmount != null ? (
          <span className="font-semibold">{formatETB(Number(row.payment.capturedAmount) || 0, locale)}</span>
        ) : (
          <p className="max-w-48 text-xs text-warning">{t("unapplied.capturedUnknown")}</p>
        ),
    },
    {
      key: "seen",
      header: t("unapplied.seen"),
      render: (row: Row) => <span>{formatDateTime(row.payment.firstSeenAt)}</span>,
    },
    {
      key: "actions",
      header: t("common.actions"),
      render: (row: Row) => {
        if (row.payment.mayStillApply) {
          return (
            <p role="note" className="max-w-56 text-xs text-warning">
              {t("unapplied.mayStillApply")}
            </p>
          );
        }

        const form = formFor(row);
        const busy = submitting === row.key;
        if (confirming === row.key) {
          return (
            <div className="space-y-2">
              <p className="text-sm">
                {fill(t("unapplied.confirmRefund"), {
                  amount: formatETB(Number(form.amount) || 0, locale),
                  payment: form.paymentOrderId.trim(),
                  ref: form.telebirrRef.trim(),
                })}
              </p>
              <div className="flex gap-2">
                <button
                  type="button"
                  onClick={() => submit(row)}
                  disabled={busy}
                  className="rounded bg-success px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
                >
                  {t("unapplied.confirm")}
                </button>
                <button
                  type="button"
                  onClick={() => setConfirming(null)}
                  disabled={busy}
                  className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50"
                >
                  {t("unapplied.cancel")}
                </button>
              </div>
            </div>
          );
        }

        const inputClass =
          "w-full rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none";
        return (
          <div className="flex flex-col gap-2 md:min-w-64">
            {!row.payment.paymentOrderId && (
              <input
                type="text"
                value={form.paymentOrderId}
                onChange={(e) => update(row, { paymentOrderId: e.target.value })}
                aria-label={t("unapplied.paymentOrderId")}
                placeholder={t("unapplied.paymentOrderId")}
                className={inputClass}
              />
            )}
            <input
              type="text"
              value={form.telebirrRef}
              onChange={(e) => update(row, { telebirrRef: e.target.value })}
              aria-label={t("unapplied.telebirrRef")}
              placeholder={t("unapplied.telebirrRefPlaceholder")}
              className={inputClass}
            />
            <input
              type="number"
              inputMode="decimal"
              min="0"
              step="0.01"
              value={form.amount}
              onChange={(e) => update(row, { amount: e.target.value })}
              aria-label={t("unapplied.amount")}
              placeholder={t("unapplied.amount")}
              className={inputClass}
            />
            <textarea
              value={form.reason}
              onChange={(e) => update(row, { reason: e.target.value })}
              aria-label={t("unapplied.reason")}
              placeholder={t("unapplied.reason")}
              maxLength={MAX_REASON}
              rows={2}
              className={inputClass}
            />
            <button
              type="button"
              onClick={() => setConfirming(row.key)}
              disabled={!isComplete(form) || busy}
              className="rounded bg-primary px-2 py-1 text-xs text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("unapplied.recordRefund")}
            </button>
          </div>
        );
      },
    },
  ];

  return (
    <div>
      <div className="mb-6">
        <h2 className="text-xl font-bold">{t("unapplied.title")}</h2>
        <p className="mt-1 max-w-2xl text-sm text-text-muted">{t("unapplied.subtitle")}</p>
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
          {t("unapplied.loadFailed")}
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
              {t(hasNextPage ? "unapplied.emptySoFar" : "unapplied.empty")}
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
                {t("unapplied.loadMore")}
              </button>
            </div>
          )}
        </Card>
      )}
    </div>
  );
}
