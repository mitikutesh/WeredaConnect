import { useId, useState } from "react";
import { useLocale } from "@/hooks/useLocale";
import { useCorrectPayoutReference } from "@/hooks/useQuery";
import { isValidCorrectionReason, isValidExternalRef, referenceUsedOn, sameExternalRef } from "@/lib/payoutRef";

/* FUP-218 (escrow-protocol.md 2.18.0): replace the Telebirr transaction ID of a payout leg an admin recorded by
 * mistake, so the leg whose payment that ID really is can be recorded. Three steps: the action, a form with the
 * correct ID and the reason, then a confirm that shows the old and the new ID and the reason before anything is
 * sent. Nothing is paid or refunded. Offered only on a leg an admin recorded (never one the gateway paid). */

type Step = "closed" | "form" | "confirm";

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

export function CorrectReference({
  escrowId,
  legRef,
  currentRef,
}: {
  escrowId: string;
  /** A 1.x leg name (farmer, trucker, buyer) or a ledger ref (release#farmer). */
  legRef: string;
  currentRef: string;
}) {
  const { t } = useLocale();
  const correct = useCorrectPayoutReference();
  const id = useId();
  const [step, setStep] = useState<Step>("closed");
  const [newRef, setNewRef] = useState("");
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const refProblem = !newRef.trim()
    ? null
    : !isValidExternalRef(newRef)
      ? t("errors.ESCROW_037")
      : sameExternalRef(newRef, currentRef)
        ? t("correctRef.sameRef")
        : null;
  const canReview = isValidExternalRef(newRef) && !sameExternalRef(newRef, currentRef) && isValidCorrectionReason(reason);
  const busy = correct.isPending;

  function errorText(err: unknown): string {
    const { code, message } = err as { code?: string; message?: string };
    const usedOn = code === "ESCROW_047" ? referenceUsedOn(message) : null;
    if (usedOn) return fill(t("payouts.duplicateRef"), usedOn);
    if (code === "ESCROW_036") return t("correctRef.changed");
    if (code === "ESCROW_035") return t("correctRef.notCorrectable");
    if (code) {
      const text = t(`errors.${code}`);
      if (text !== `errors.${code}`) return text;
    }
    return t("errors.actionFailed");
  }

  function close() {
    setStep("closed");
    setNewRef("");
    setReason("");
  }

  function submit() {
    setError(null);
    correct.mutate(
      { escrowId, ref: legRef, oldExternalRef: currentRef, externalRef: newRef.trim(), reason: reason.trim() },
      {
        onSuccess: (result) => {
          setNotice(t(result?.alreadyCorrected ? "correctRef.already" : "correctRef.done"));
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
      <div className="mt-1 space-y-1">
        {notice && (
          <p role="status" className="text-xs text-success">
            {notice}
          </p>
        )}
        <button
          type="button"
          onClick={() => {
            setNotice(null);
            setError(null);
            setStep("form");
          }}
          className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50"
        >
          {t("correctRef.action")}
        </button>
      </div>
    );
  }

  if (step === "confirm") {
    return (
      <div className="mt-2 max-w-sm space-y-2 rounded border border-warning/40 bg-warning/5 p-2 text-left text-xs">
        <p className="text-sm font-medium">
          {fill(t("correctRef.confirmQuestion"), { oldRef: currentRef, newRef: newRef.trim() })}
        </p>
        <p className="text-text-muted">
          {t("correctRef.reason")}: {reason.trim()}
        </p>
        <p className="text-text-muted">{t("correctRef.hint")}</p>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={submit}
            disabled={busy}
            className="rounded bg-primary px-2 py-1 text-white hover:opacity-90 disabled:opacity-50"
          >
            {t("correctRef.confirm")}
          </button>
          <button
            type="button"
            onClick={() => setStep("form")}
            disabled={busy}
            className="rounded border border-gray-300 px-2 py-1 hover:bg-gray-50 disabled:opacity-50"
          >
            {t("correctRef.back")}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="mt-2 max-w-sm space-y-2 rounded border border-gray-200 p-2 text-left text-xs">
      <p className="font-medium">{t("correctRef.title")}</p>
      <p className="text-text-muted">{t("correctRef.hint")}</p>
      <p>
        {t("correctRef.current")}: <span className="font-mono">{currentRef}</span>
      </p>
      {error && (
        <p role="alert" className="text-danger">
          {error}
        </p>
      )}
      <div className="space-y-1">
        <label htmlFor={`${id}-ref`} className="block font-medium">
          {t("correctRef.newRef")}
        </label>
        <input
          id={`${id}-ref`}
          type="text"
          value={newRef}
          onChange={(e) => setNewRef(e.target.value)}
          placeholder={t("payouts.externalRefPlaceholder")}
          aria-invalid={refProblem ? true : undefined}
          aria-describedby={refProblem ? `${id}-ref-problem` : undefined}
          className="w-full rounded border border-gray-300 px-2 py-1 font-mono text-sm focus:border-primary focus:outline-none"
        />
        {refProblem && (
          <p id={`${id}-ref-problem`} className="text-warning">
            {refProblem}
          </p>
        )}
      </div>
      <div className="space-y-1">
        <label htmlFor={`${id}-reason`} className="block font-medium">
          {t("correctRef.reason")}
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
        <p id={`${id}-reason-hint`} className="text-text-muted">
          {t("correctRef.reasonHint")}
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
          className="rounded bg-primary px-2 py-1 text-white hover:opacity-90 disabled:opacity-50"
        >
          {t("correctRef.review")}
        </button>
        <button
          type="button"
          onClick={() => {
            setError(null);
            close();
          }}
          className="rounded border border-gray-300 px-2 py-1 hover:bg-gray-50"
        >
          {t("correctRef.cancel")}
        </button>
      </div>
    </div>
  );
}
