import { useEffect, useId, useRef, useState } from "react";
import { useLocale } from "@/hooks/useLocale";
import { REASON_MAX, dayOffset, endOfDayIso, formProblem } from "@/lib/reputation";

/* ESC-32: the one dialog of every admin reputation write. Each write needs a reason (the server
 * audits it, and for a decision the account holder reads it), and tag, untag and restrict need an
 * end date. Confirm says what is missing and sends nothing until both are valid; the server checks
 * again with its own clock and limits. */

export type UntilMode = "required" | "optional" | "none";

interface Props {
  title: string;
  help: string;
  /** The label of the reason field: a reason for the audit log, or a note the account holder reads. */
  reasonLabel: string;
  confirmLabel: string;
  danger?: boolean;
  until: UntilMode;
  untilLabel?: string;
  /** The furthest end, in days from today. */
  maxDays: number;
  busy: boolean;
  /** What the server answered to the last attempt, already translated. */
  error: string | null;
  onConfirm: (reason: string, untilIso?: string) => void;
  onCancel: () => void;
}

function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_, k: string) => values[k] ?? "");
}

export function ReputationActionDialog({
  title,
  help,
  reasonLabel,
  confirmLabel,
  danger = false,
  until,
  untilLabel,
  maxDays,
  busy,
  error,
  onConfirm,
  onCancel,
}: Props) {
  const { t } = useLocale();
  const titleId = useId();
  const helpId = useId();
  const reasonRef = useRef<HTMLTextAreaElement>(null);
  const [reason, setReason] = useState("");
  const [untilDate, setUntilDate] = useState("");
  const [touched, setTouched] = useState(false);
  // The admin's clock when the dialog opened: the bounds of the date field.
  const [today] = useState(() => new Date());

  useEffect(() => {
    reasonRef.current?.focus();
  }, []);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === "Escape" && !busy) onCancel();
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [busy, onCancel]);

  const problem = formProblem(reason, until === "none" ? "" : untilDate, until === "required", maxDays, today);

  function submit(event: React.FormEvent) {
    event.preventDefault();
    setTouched(true);
    if (problem || busy) return;
    onConfirm(reason.trim(), until !== "none" && untilDate ? endOfDayIso(untilDate) : undefined);
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={helpId}
        className="w-full max-w-lg rounded-xl bg-white p-6 shadow-xl"
      >
        <h3 id={titleId} className="text-lg font-semibold">
          {title}
        </h3>
        <p id={helpId} className="mt-1 text-sm text-text-muted">
          {help}
        </p>
        <form onSubmit={submit} className="mt-4 space-y-4" noValidate>
          <label className="block text-sm">
            <span className="mb-1 block font-medium">{reasonLabel}</span>
            <textarea
              ref={reasonRef}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              maxLength={REASON_MAX}
              rows={3}
              aria-invalid={touched && problem === "reason"}
              className="w-full rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none"
            />
            <span className="mt-1 block text-xs text-text-muted">
              {reason.trim().length}/{REASON_MAX}
            </span>
          </label>
          {until !== "none" && (
            <label className="block text-sm">
              <span className="mb-1 block font-medium">{untilLabel ?? t("reputation.until")}</span>
              <input
                type="date"
                value={untilDate}
                onChange={(e) => setUntilDate(e.target.value)}
                min={dayOffset(today, 1)}
                max={dayOffset(today, maxDays - 1)}
                aria-invalid={touched && problem === "until"}
                className="rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none"
              />
              <span className="mt-1 block text-xs text-text-muted">
                {fill(t("reputation.untilHint"), { days: String(maxDays) })}
              </span>
            </label>
          )}
          {touched && problem && (
            <p role="alert" className="text-sm text-danger">
              {problem === "reason"
                ? t("reputation.reasonInvalid")
                : fill(t("reputation.untilInvalid"), { days: String(maxDays) })}
            </p>
          )}
          {error && (
            <p role="alert" className="rounded border border-danger/30 bg-danger/10 px-3 py-2 text-sm text-danger">
              {error}
            </p>
          )}
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={onCancel}
              disabled={busy}
              className="rounded border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50"
            >
              {t("common.cancel")}
            </button>
            <button
              type="submit"
              disabled={busy}
              className={`rounded px-3 py-1.5 text-sm text-white hover:opacity-90 disabled:opacity-50 ${
                danger ? "bg-danger" : "bg-primary"
              }`}
            >
              {confirmLabel}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
