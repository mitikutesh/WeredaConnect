import { useId, useState } from "react";
import { useLocale } from "@/hooks/useLocale";
import { useCancelDelivery } from "@/hooks/useQuery";
import { canAdminCancelDelivery, deliveryMoneyDecided, isValidDeliveryCancelReason } from "@/lib/deliveryCancel";
import { escrowStatusLabel } from "@/lib/transaction";
import { fill } from "@/lib/trail";

/* FUP-19 (spec E9, delivery-verification-api.md 2.10.0): cancel a delivery that was picked up and will not reach the
 * buyer, once the money behind it is decided. Three steps: the action, a form with the reason, then a confirm before
 * anything is sent. Only the delivery is cancelled: no money moves and the order keeps its status. While the payment
 * is still held the page says to decide it first and offers no action (the server refuses it too). */

type Step = "closed" | "form" | "confirm";

export function CancelDelivery({
  deliveryId,
  status,
  version,
  escrowStatus,
}: {
  deliveryId: string;
  /** The delivery status the page read. */
  status: string;
  /** The delivery version the page read: the cancel is refused if the delivery moved since. */
  version: number;
  /** The order's escrow status on the page, or null when the order has none. */
  escrowStatus: string | null;
}) {
  const { t } = useLocale();
  const cancel = useCancelDelivery();
  const id = useId();
  const [step, setStep] = useState<Step>("closed");
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  if (!canAdminCancelDelivery(status)) {
    return notice ? (
      <p role="status" className="text-xs text-success">
        {notice}
      </p>
    ) : null;
  }

  if (!deliveryMoneyDecided(escrowStatus)) {
    return (
      <p className="text-xs text-text-muted">
        {fill(t("cancelDelivery.moneyFirst"), { status: escrowStatusLabel(escrowStatus ?? "", t) })}
      </p>
    );
  }

  const busy = cancel.isPending;

  function errorText(err: unknown): string {
    const { code } = err as { code?: string };
    if (code) {
      const text = t(`errors.${code}`);
      if (text !== `errors.${code}`) return text;
    }
    return t("errors.actionFailed");
  }

  function close() {
    setStep("closed");
    setReason("");
  }

  function submit() {
    setError(null);
    cancel.mutate(
      { deliveryId, reason: reason.trim(), expectedVersion: version },
      {
        onSuccess: (result) => {
          setNotice(t(result?.alreadyCancelled ? "cancelDelivery.already" : "cancelDelivery.done"));
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
      <div className="space-y-1">
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
          className="rounded border border-danger/40 px-2 py-1 text-xs text-danger hover:bg-danger/5"
        >
          {t("cancelDelivery.action")}
        </button>
      </div>
    );
  }

  if (step === "confirm") {
    return (
      <div className="max-w-sm space-y-2 rounded border border-warning/40 bg-warning/5 p-2 text-left text-xs">
        <p className="text-sm font-medium">{t("cancelDelivery.confirmQuestion")}</p>
        <p className="text-text-muted">
          {t("cancelDelivery.reason")}: {reason.trim()}
        </p>
        <p className="text-text-muted">{t("cancelDelivery.hint")}</p>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={submit}
            disabled={busy}
            className="rounded bg-danger px-2 py-1 text-white hover:opacity-90 disabled:opacity-50"
          >
            {t("cancelDelivery.confirm")}
          </button>
          <button
            type="button"
            onClick={() => setStep("form")}
            disabled={busy}
            className="rounded border border-gray-300 px-2 py-1 hover:bg-gray-50 disabled:opacity-50"
          >
            {t("cancelDelivery.back")}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="max-w-sm space-y-2 rounded border border-gray-200 p-2 text-left text-xs">
      <p className="font-medium">{t("cancelDelivery.title")}</p>
      <p className="text-text-muted">{t("cancelDelivery.hint")}</p>
      {error && (
        <p role="alert" className="text-danger">
          {error}
        </p>
      )}
      <div className="space-y-1">
        <label htmlFor={`${id}-reason`} className="block font-medium">
          {t("cancelDelivery.reason")}
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
          {t("cancelDelivery.reasonHint")}
        </p>
      </div>
      <div className="flex gap-2">
        <button
          type="button"
          onClick={() => {
            setError(null);
            setStep("confirm");
          }}
          disabled={!isValidDeliveryCancelReason(reason)}
          className="rounded bg-primary px-2 py-1 text-white hover:opacity-90 disabled:opacity-50"
        >
          {t("cancelDelivery.review")}
        </button>
        <button
          type="button"
          onClick={() => {
            setError(null);
            close();
          }}
          className="rounded border border-gray-300 px-2 py-1 hover:bg-gray-50"
        >
          {t("cancelDelivery.cancel")}
        </button>
      </div>
    </div>
  );
}
