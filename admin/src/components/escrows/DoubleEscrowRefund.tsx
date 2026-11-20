import { useId, useState } from "react";
import { Card } from "@/components/ui/Card";
import { useLocale } from "@/hooks/useLocale";
import { useRefundDoubleEscrow } from "@/hooks/useQuery";
import { formatETB } from "@/lib/i18n";
import { isValidCorrectionReason } from "@/lib/payoutRef";
import { escrowStatusLabel } from "@/lib/transaction";

/* FUP-41 (escrow-protocol.md 2.19.0): an /escrow/initiate escrow next to its order's own escrow, which holds or paid
 * the trade's money, is a second payment the hourly sweep never settles. An admin refunds it to the buyer through
 * the existing refund (POST /escrow/refund with doubleEscrow), after a reason and a confirm step that shows the
 * amount. The order, its delivery and the order's own escrow do not change. Shown only on an escrowed escrow the
 * server marked as a double payment (doubleEscrow on the admin escrow detail); the server checks it again. */

type Step = "closed" | "form" | "confirm";

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

export function DoubleEscrowRefund({ escrow }: { escrow: Record<string, unknown> }) {
  const { t, locale } = useLocale();
  const refund = useRefundDoubleEscrow();
  const id = useId();
  const [step, setStep] = useState<Step>("closed");
  const [reason, setReason] = useState("");
  const [idempotencyKey, setIdempotencyKey] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const escrowId = String(escrow.escrowId || "");
  if (escrow.doubleEscrow !== true || escrow.status !== "escrowed" || !escrowId) {
    return notice ? (
      <Card>
        <p role="status" className="text-sm text-success">
          {notice}
        </p>
      </Card>
    ) : null;
  }

  const amount = formatETB(Number(escrow.amount) || 0, locale);
  const orderEscrowStatus = escrowStatusLabel(String(escrow.orderEscrowStatus || ""), t);
  const busy = refund.isPending;

  function errorText(err: unknown): string {
    const { code } = err as { code?: string };
    if (code) {
      const text = t(`errors.${code}`);
      if (text !== `errors.${code}`) return text;
    }
    return t("errors.actionFailed");
  }

  function open() {
    setNotice(null);
    setError(null);
    // One key per opened form: a double click or a lost answer is answered with the refund already made.
    setIdempotencyKey(`admin:${escrowId}:double_escrow_refund:${Date.now()}`);
    setStep("form");
  }

  function submit() {
    setError(null);
    refund.mutate(
      { escrowId, reason: reason.trim(), idempotencyKey },
      {
        onSuccess: (result) => {
          // 202 ESCROW_034: the gateway cannot pay out here, so the refund waits in Manual Payouts.
          setNotice(t(result?.error?.code === "ESCROW_034" ? "doubleEscrow.queued" : "doubleEscrow.done"));
          setStep("closed");
          setReason("");
        },
        onError: (err: unknown) => {
          setError(errorText(err));
          setStep("form");
        },
      },
    );
  }

  return (
    <Card>
      <h3 className="mb-2 font-semibold">{t("doubleEscrow.title")}</h3>
      <p className="text-sm text-text-muted">{fill(t("doubleEscrow.explain"), { orderEscrowStatus })}</p>
      {notice && (
        <p role="status" className="mt-2 text-sm text-success">
          {notice}
        </p>
      )}

      {step === "closed" && (
        <button
          type="button"
          onClick={open}
          className="mt-3 rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50"
        >
          {t("doubleEscrow.action")}
        </button>
      )}

      {step === "form" && (
        <div className="mt-3 space-y-2 text-sm">
          <p className="text-text-muted">{t("doubleEscrow.hint")}</p>
          {error && (
            <p role="alert" className="text-danger">
              {error}
            </p>
          )}
          <label htmlFor={`${id}-reason`} className="block font-medium">
            {t("doubleEscrow.reason")}
          </label>
          <textarea
            id={`${id}-reason`}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            rows={3}
            maxLength={500}
            aria-describedby={`${id}-reason-hint`}
            className="w-full rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none"
          />
          <p id={`${id}-reason-hint`} className="text-xs text-text-muted">
            {t("doubleEscrow.reasonHint")}
          </p>
          <div className="flex gap-2">
            <button
              type="button"
              onClick={() => setStep("confirm")}
              disabled={!isValidCorrectionReason(reason)}
              className="rounded bg-primary px-3 py-1 text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("doubleEscrow.review")}
            </button>
            <button
              type="button"
              onClick={() => {
                setError(null);
                setReason("");
                setStep("closed");
              }}
              className="rounded border border-gray-300 px-3 py-1 hover:bg-gray-50"
            >
              {t("doubleEscrow.cancel")}
            </button>
          </div>
        </div>
      )}

      {step === "confirm" && (
        <div className="mt-3 space-y-2 rounded border border-warning/40 bg-warning/5 p-2 text-sm">
          <p className="font-medium">{fill(t("doubleEscrow.confirmQuestion"), { amount })}</p>
          <p className="text-text-muted">
            {t("doubleEscrow.reason")}: {reason.trim()}
          </p>
          <p className="text-text-muted">{t("doubleEscrow.hint")}</p>
          <div className="flex gap-2">
            <button
              type="button"
              onClick={submit}
              disabled={busy}
              className="rounded bg-primary px-3 py-1 text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("doubleEscrow.confirm")}
            </button>
            <button
              type="button"
              onClick={() => setStep("form")}
              disabled={busy}
              className="rounded border border-gray-300 px-3 py-1 hover:bg-gray-50 disabled:opacity-50"
            >
              {t("doubleEscrow.back")}
            </button>
          </div>
        </div>
      )}
    </Card>
  );
}
