import { useId, useState } from "react";
import { useLocale } from "@/hooks/useLocale";
import { useReopenPickup } from "@/hooks/useQuery";
import { DEADLINE_HOURS, REOPEN_ACTIONS, isValidReopenReason, type ReopenExit, type ReopenKind } from "@/lib/pickup";
import { fill } from "@/lib/trail";

/* FUP-287 (delivery-verification-api.md 2.8.0 P.2): re-open a judged pickup after the admin reconciled what really
 * happened (a scan that reached the server too late is refused, so without this the only exits were the refund
 * paths). Three steps, as Correct reference: the action, a form (what happened, the new deadline, the reason), then
 * a confirm before anything is sent. Only the exits the server named for this order are offered. No money moves.
 * FUP-330 (2.11.0): `kind="noShow"` is the same form for a judged trucker no-show (`reopen-no-show`): its texts say
 * the trip booking is taken back and the trucker's no-show excused, and the answer says what happened to that. */

type Step = "closed" | "form" | "confirm";

/** The keys whose text differs for a trucker no-show; the rest of the form reads `pickup.reopen.*`. */
const NO_SHOW_TEXTS = new Set([
  "action",
  "title",
  "hint",
  "exitPendingHelp",
  "exitHandoffHelp",
  "confirmPending",
  "confirmHandoff",
  "donePending",
  "doneHandoff",
]);

export function ReopenPickup({
  orderId,
  version,
  exits,
  kind = "judgement",
}: {
  orderId: string;
  /** The order version the page read; the server refuses the re-open when the order moved since. */
  version: number;
  exits: ReopenExit[];
  kind?: ReopenKind;
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

  /** The form's text: the no-show's own where it differs, else the judgement re-open's. */
  const text = (key: string) => t(kind === "noShow" && NO_SHOW_TEXTS.has(key) ? `pickup.noShowReopen.${key}` : `pickup.reopen.${key}`);

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
    setReason("");
  }

  function submit() {
    setError(null);
    reopen.mutate(
      {
        orderId,
        // The hook posts to reopen-pickup unless told otherwise.
        ...(kind === "noShow" ? { action: REOPEN_ACTIONS.noShow } : {}),
        exit,
        reason: reason.trim(),
        expectedVersion: version,
        ...(exit === "pending" ? { deadlineHours: hours } : {}),
      },
      {
        onSuccess: (result) => {
          const done = text(result?.alreadyApplied ? "already" : exit === "pending" ? "donePending" : "doneHandoff");
          const eventKey = result?.noShowEvent ? `pickup.noShowEvent_${result.noShowEvent}` : null;
          const event = eventKey && t(eventKey) !== eventKey ? t(eventKey) : null;
          setNotice(event ? `${done} ${event}` : done);
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
            {text("action")}
          </button>
        )}
      </div>
    );
  }

  if (step === "confirm") {
    return (
      <div className="mt-3 max-w-md space-y-2 rounded border border-warning/40 bg-warning/5 p-2 text-left text-xs">
        <p className="text-sm font-medium">
          {exit === "pending" ? fill(text("confirmPending"), { hours }) : text("confirmHandoff")}
        </p>
        <p className="text-text-muted">
          {text("reason")}: {reason.trim()}
        </p>
        <p className="text-text-muted">{text("hint")}</p>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={submit}
            disabled={busy}
            className="rounded bg-primary px-2 py-1 text-white hover:opacity-90 disabled:opacity-50"
          >
            {text("confirm")}
          </button>
          <button
            type="button"
            onClick={() => setStep("form")}
            disabled={busy}
            className="rounded border border-gray-300 px-2 py-1 hover:bg-gray-50 disabled:opacity-50"
          >
            {text("back")}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="mt-3 max-w-md space-y-2 rounded border border-gray-200 p-2 text-left text-xs">
      <p className="font-medium">{text("title")}</p>
      <p className="text-text-muted">{text("hint")}</p>
      {error && (
        <p role="alert" className="text-danger">
          {error}
        </p>
      )}
      <fieldset className="space-y-1">
        <legend className="font-medium">{text("exit")}</legend>
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
                {text(option === "pending" ? "exitPendingHelp" : "exitHandoffHelp")}
              </span>
            </span>
          </label>
        ))}
      </fieldset>
      {exit === "pending" && (
        <div className="space-y-1">
          <label htmlFor={`${id}-hours`} className="block font-medium">
            {text("hours")}
          </label>
          <select
            id={`${id}-hours`}
            value={hours}
            onChange={(e) => setHours(Number(e.target.value))}
            className="rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none"
          >
            {DEADLINE_HOURS.map((h) => (
              <option key={h} value={h}>
                {fill(text("hoursOption"), { hours: h })}
              </option>
            ))}
          </select>
        </div>
      )}
      <div className="space-y-1">
        <label htmlFor={`${id}-reason`} className="block font-medium">
          {text("reason")}
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
          {text("reasonHint")}
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
          {text("review")}
        </button>
        <button
          type="button"
          onClick={() => {
            setError(null);
            close();
          }}
          className="rounded border border-gray-300 px-2 py-1 hover:bg-gray-50"
        >
          {text("cancel")}
        </button>
      </div>
    </div>
  );
}
