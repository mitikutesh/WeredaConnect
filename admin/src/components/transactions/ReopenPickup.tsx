import { useId, useState } from "react";
import { useLocale } from "@/hooks/useLocale";
import { useReopenPickup } from "@/hooks/useQuery";
import { DEADLINE_HOURS, isValidReopenReason, type ReopenExit } from "@/lib/pickup";
import { fill } from "@/lib/trail";

/* FUP-287 (delivery-verification-api.md 2.8.0 P.2): re-open a judged pickup after the admin reconciled what really
 * happened (a scan that reached the server too late is refused, so without this the only exits were the refund
 * paths). Three steps, as Correct reference: the action, a form (what happened, the new deadline, the reason), then
 * a confirm before anything is sent. Only the exits the server named for this order are offered. No money moves. */

type Step = "closed" | "form" | "confirm";

export function ReopenPickup({
  orderId,
  version,
  exits,
}: {
  orderId: string;
  /** The order version the page read; the server refuses the re-open when the order moved since. */
  version: number;
  exits: ReopenExit[];
}) {
  const { t } = useLocale();
  const reopen = useReopenPickup();
  const id = useId();
  const [step, setStep] = useState<Step>("closed");
  const [exit, setExit] = useState<ReopenExit>(exits[0] ?? "pending");
  const [hours, setHours] = useState<number>(DEADLINE_HOURS[0]);
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const busy = reopen.isPending;

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
    reopen.mutate(
      {
        orderId,
        exit,
        reason: reason.trim(),
        expectedVersion: version,
        ...(exit === "pending" ? { deadlineHours: hours } : {}),
      },
      {
        onSuccess: (result) => {
          setNotice(
            t(result?.alreadyApplied ? "pickup.reopen.already" : exit === "pending" ? "pickup.reopen.donePending" : "pickup.reopen.doneHandoff"),
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
        {exits.length > 0 && (
          <button
            type="button"
            onClick={() => {
              setNotice(null);
              setError(null);
              setExit(exits[0]);
              setStep("form");
            }}
            className="rounded border border-gray-300 px-2 py-1 text-xs hover:bg-gray-50"
          >
            {t("pickup.reopen.action")}
          </button>
        )}
      </div>
    );
  }

  if (step === "confirm") {
    return (
      <div className="mt-3 max-w-md space-y-2 rounded border border-warning/40 bg-warning/5 p-2 text-left text-xs">
        <p className="text-sm font-medium">
          {exit === "pending"
            ? fill(t("pickup.reopen.confirmPending"), { hours })
            : t("pickup.reopen.confirmHandoff")}
        </p>
        <p className="text-text-muted">
          {t("pickup.reopen.reason")}: {reason.trim()}
        </p>
        <p className="text-text-muted">{t("pickup.reopen.hint")}</p>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={submit}
            disabled={busy}
            className="rounded bg-primary px-2 py-1 text-white hover:opacity-90 disabled:opacity-50"
          >
            {t("pickup.reopen.confirm")}
          </button>
          <button
            type="button"
            onClick={() => setStep("form")}
            disabled={busy}
            className="rounded border border-gray-300 px-2 py-1 hover:bg-gray-50 disabled:opacity-50"
          >
            {t("pickup.reopen.back")}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="mt-3 max-w-md space-y-2 rounded border border-gray-200 p-2 text-left text-xs">
      <p className="font-medium">{t("pickup.reopen.title")}</p>
      <p className="text-text-muted">{t("pickup.reopen.hint")}</p>
      {error && (
        <p role="alert" className="text-danger">
          {error}
        </p>
      )}
      <fieldset className="space-y-1">
        <legend className="font-medium">{t("pickup.reopen.exit")}</legend>
        {exits.map((option) => (
          <label key={option} className="flex items-start gap-2">
            <input
              type="radio"
              name={`${id}-exit`}
              value={option}
              checked={exit === option}
              onChange={() => setExit(option)}
              className="mt-0.5"
            />
            <span>
              <span className="block">{t(`pickup.exit_${option}`)}</span>
              <span className="block text-text-muted">
                {t(option === "pending" ? "pickup.reopen.exitPendingHelp" : "pickup.reopen.exitHandoffHelp")}
              </span>
            </span>
          </label>
        ))}
      </fieldset>
      {exit === "pending" && (
        <div className="space-y-1">
          <label htmlFor={`${id}-hours`} className="block font-medium">
            {t("pickup.reopen.hours")}
          </label>
          <select
            id={`${id}-hours`}
            value={hours}
            onChange={(e) => setHours(Number(e.target.value))}
            className="rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none"
          >
            {DEADLINE_HOURS.map((h) => (
              <option key={h} value={h}>
                {fill(t("pickup.reopen.hoursOption"), { hours: h })}
              </option>
            ))}
          </select>
        </div>
      )}
      <div className="space-y-1">
        <label htmlFor={`${id}-reason`} className="block font-medium">
          {t("pickup.reopen.reason")}
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
          {t("pickup.reopen.reasonHint")}
        </p>
      </div>
      <div className="flex gap-2">
        <button
          type="button"
          onClick={() => {
            setError(null);
            setStep("confirm");
          }}
          disabled={!isValidReopenReason(reason)}
          className="rounded bg-primary px-2 py-1 text-white hover:opacity-90 disabled:opacity-50"
        >
          {t("pickup.reopen.review")}
        </button>
        <button
          type="button"
          onClick={() => {
            setError(null);
            close();
          }}
          className="rounded border border-gray-300 px-2 py-1 hover:bg-gray-50"
        >
          {t("pickup.reopen.cancel")}
        </button>
      </div>
    </div>
  );
}
