import { useEffect, useId, useRef, useState } from "react";
import { useLocale } from "@/hooks/useLocale";
import { useChangeCommission } from "@/hooks/useQuery";
import {
  formatRatePercent,
  isSameRate,
  isValidCommissionReason,
  parseCommissionPercent,
  previewCommission,
} from "@/lib/commission";
import { formatETB } from "@/lib/i18n";
import { fill } from "@/lib/trail";

/* The negotiated commission of one order (order-lifecycle.md 2.26.0): before the buyer pays, an admin agrees another
 * commission rate for this order. Three steps, as the pickup re-open: the action, a form (the new rate in percent with
 * a live preview of the new commission and total, and the reason), then a confirm before anything is sent. The crop
 * and transport costs stay; the server sums the new commission and total, and tells the buyer. The page shows this
 * only while the order is unpaid (created or pending_payment); the server checks that again. */

type Step = "closed" | "form" | "confirm";

export function ChangeCommission({
  orderId,
  version,
  cropCost,
  transportCost,
  commissionRate,
  commissionAmount,
  orderTotal,
}: {
  orderId: string;
  /** The order version the page read; the server refuses the change when the order moved since. */
  version: number;
  cropCost: number;
  transportCost: number;
  /** The order's rate as read (a fraction), or null when the order carries none. */
  commissionRate: number | null;
  commissionAmount: number;
  orderTotal: number;
}) {
  const { t, locale } = useLocale();
  const change = useChangeCommission();
  const id = useId();
  const [step, setStep] = useState<Step>("closed");
  const [rateText, setRateText] = useState("");
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const busy = change.isPending;

  // Each step replaces the one before, so focus goes to the new step instead of falling back to the page.
  const actionRef = useRef<HTMLButtonElement>(null);
  const rateRef = useRef<HTMLInputElement>(null);
  const confirmRef = useRef<HTMLParagraphElement>(null);
  const opened = useRef(false);
  useEffect(() => {
    if (step === "form") rateRef.current?.focus();
    else if (step === "confirm") confirmRef.current?.focus();
    else if (opened.current) actionRef.current?.focus();
    if (step !== "closed") opened.current = true;
  }, [step]);

  const rate = parseCommissionPercent(rateText);
  const sameRate = rate != null && commissionRate != null && isSameRate(rate, commissionRate);
  const preview = rate != null ? previewCommission(cropCost, transportCost, rate) : null;
  const rateInvalid = rateText.trim() !== "" && rate == null;
  const canReview = rate != null && !sameRate && isValidCommissionReason(reason);
  const currentRate = commissionRate != null ? formatRatePercent(commissionRate) : t("common.notAvailable");

  function errorText(err: unknown): string {
    const { code } = err as { code?: string };
    if (code) {
      const message = t(`errors.${code}`);
      if (message !== `errors.${code}`) return message;
    }
    return t("errors.actionFailed");
  }

  function close() {
    setStep("closed");
    setRateText("");
    setReason("");
  }

  function submit() {
    if (rate == null || preview == null) return;
    setError(null);
    change.mutate(
      { orderId, commissionRate: rate, reason: reason.trim(), expectedVersion: version },
      {
        onSuccess: (result) => {
          setNotice(
            result?.alreadyApplied
              ? t("commission.already")
              : fill(t("commission.done"), {
                  rate: formatRatePercent(result?.commissionRate ?? rate),
                  total: formatETB(result?.orderTotal ?? preview.orderTotal, locale),
                }),
          );
          close();
        },
        onError: (err: unknown) => {
          setError(errorText(err));
          setStep("form");
        },
      },
    );
  }

  if (step === "closed") {
    return (
      <div className="mt-3 space-y-1">
        {notice && (
          <p role="status" className="text-xs text-success">
            {notice}
          </p>
        )}
        <button
          ref={actionRef}
          type="button"
          onClick={() => {
            setNotice(null);
            setError(null);
            setStep("form");
          }}
          className="rounded-[10px] border border-field px-2 py-1 text-xs hover:bg-paper"
        >
          {t("commission.action")}
        </button>
      </div>
    );
  }

  if (step === "confirm" && rate != null && preview != null) {
    return (
      <div className="mt-3 max-w-md space-y-2 rounded-[10px] border border-orange/60 bg-orange-wash p-2 text-left text-xs">
        <p ref={confirmRef} tabIndex={-1} className="text-sm font-medium focus:outline-none">
          {fill(t("commission.confirmText"), {
            fromRate: currentRate,
            toRate: formatRatePercent(rate),
            fromTotal: formatETB(orderTotal, locale),
            toTotal: formatETB(preview.orderTotal, locale),
          })}
        </p>
        <p className="text-slate">
          {t("commission.reason")}: {reason.trim()}
        </p>
        <p className="text-slate">{t("commission.confirmBuyer")}</p>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={submit}
            disabled={busy}
            className="rounded-[10px] bg-ink px-2 py-1 text-white hover:opacity-90 disabled:opacity-50"
          >
            {t("commission.confirm")}
          </button>
          <button
            type="button"
            onClick={() => setStep("form")}
            disabled={busy}
            className="rounded-[10px] border border-field px-2 py-1 hover:bg-paper disabled:opacity-50"
          >
            {t("commission.back")}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="mt-3 max-w-md space-y-2 rounded-[10px] border border-line p-2 text-left text-xs">
      <p className="font-medium">{t("commission.title")}</p>
      <p className="text-slate">{t("commission.hint")}</p>
      <p className="text-slate">
        {fill(t("commission.current"), {
          rate: currentRate,
          amount: formatETB(commissionAmount, locale),
          total: formatETB(orderTotal, locale),
        })}
      </p>
      {error && (
        <p role="alert" className="text-danger">
          {error}
        </p>
      )}
      <div className="space-y-1">
        <label htmlFor={`${id}-rate`} className="block font-medium">
          {t("commission.rate")}
        </label>
        <input
          ref={rateRef}
          id={`${id}-rate`}
          type="text"
          inputMode="decimal"
          autoComplete="off"
          value={rateText}
          onChange={(e) => setRateText(e.target.value)}
          aria-invalid={rateInvalid || sameRate}
          aria-describedby={`${id}-rate-hint ${id}-rate-check`}
          className="w-24 rounded-[10px] border border-field px-2 py-1 text-sm focus:border-ink focus:outline-none"
        />
        <p id={`${id}-rate-hint`} className="text-slate">
          {t("commission.rateHint")}
        </p>
        <p id={`${id}-rate-check`} aria-live="polite" className={rateInvalid || sameRate ? "text-danger" : "font-medium"}>
          {rateInvalid
            ? t("commission.rateInvalid")
            : sameRate
              ? t("commission.sameRate")
              : preview
                ? fill(t("commission.preview"), {
                    amount: formatETB(preview.commissionAmount, locale),
                    total: formatETB(preview.orderTotal, locale),
                  })
                : ""}
        </p>
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-reason`} className="block font-medium">
          {t("commission.reason")}
        </label>
        <textarea
          id={`${id}-reason`}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={3}
          maxLength={500}
          aria-describedby={`${id}-reason-hint`}
          className="w-full rounded-[10px] border border-field px-2 py-1 text-sm focus:border-ink focus:outline-none"
        />
        <p id={`${id}-reason-hint`} className="text-slate">
          {t("commission.reasonHint")}
        </p>
      </div>
      <div className="flex gap-2">
        <button
          type="button"
          onClick={() => {
            setError(null);
            setStep("confirm");
          }}
          disabled={!canReview}
          className="rounded-[10px] bg-ink px-2 py-1 text-white hover:opacity-90 disabled:opacity-50"
        >
          {t("commission.review")}
        </button>
        <button
          type="button"
          onClick={() => {
            setError(null);
            close();
          }}
          className="rounded-[10px] border border-field px-2 py-1 hover:bg-paper"
        >
          {t("commission.cancel")}
        </button>
      </div>
    </div>
  );
}
