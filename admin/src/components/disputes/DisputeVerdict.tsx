import { useState } from "react";
import { Card } from "@/components/ui/Card";
import {
  type Disposition,
  type DisputeVerdictFlow,
  REASON_MAX,
  REASON_MIN,
  type VerdictChoice,
  dispositionsFor,
} from "./verdictFlow";

/** The error and the saved notice of a verdict. */
export function VerdictMessages({ flow }: { flow: DisputeVerdictFlow }) {
  return (
    <>
      {flow.errorMsg && (
        <div
          role="alert"
          className="mb-4 rounded border border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger"
        >
          {flow.errorMsg}
        </div>
      )}
      {flow.notice && (
        <div
          role="status"
          className="mb-4 rounded border border-success/30 bg-success/10 px-4 py-2 text-sm text-success"
        >
          {flow.notice}
        </div>
      )}
    </>
  );
}

/** What happens when the order cannot follow the verdict: a disposition and a reason. */
export function DispositionChoiceCard({ flow, t }: { flow: DisputeVerdictFlow; t: (key: string) => string }) {
  const choice = flow.choice;
  if (!choice) return null;
  // Keyed so a new choice starts empty.
  return <DispositionForm key={`${choice.id}:${choice.verdict}`} choice={choice} flow={flow} t={t} />;
}

function DispositionForm({
  choice,
  flow,
  t,
}: {
  choice: VerdictChoice;
  flow: DisputeVerdictFlow;
  t: (key: string) => string;
}) {
  const [disposition, setDisposition] = useState<Disposition | null>(null);
  const [reason, setReason] = useState("");
  const trimmedReason = reason.trim();
  const reasonValid = trimmedReason.length >= REASON_MIN && trimmedReason.length <= REASON_MAX;

  return (
    <Card className="mb-4">
      <h3 className="font-semibold">{t("disputes.dispositionTitle")}</h3>
      <p className="mt-1 text-sm text-text-muted">
        {t("escrows.id")}: {choice.id} · {t(`disputes.verdict_${choice.verdict}`)}
      </p>
      <p className="mt-2 text-sm">
        {t(choice.verdict === "buyer_wins" ? "disputes.caseGoodsMoving" : "disputes.caseBeforeDelivery")}
      </p>
      <fieldset className="mt-4">
        <legend className="text-sm font-medium">{t("disputes.dispositionLegend")}</legend>
        <div className="mt-2 flex flex-col gap-2">
          {dispositionsFor[choice.verdict].map((d) => (
            <label key={d} className="flex items-start gap-2 text-sm">
              <input
                type="radio"
                name="disposition"
                value={d}
                checked={disposition === d}
                onChange={() => setDisposition(d)}
                className="mt-1"
              />
              <span>
                <span className="font-medium">{t(`disputes.disposition_${d}`)}</span>
                <span className="block text-text-muted">{t(`disputes.dispositionHint_${d}`)}</span>
              </span>
            </label>
          ))}
        </div>
      </fieldset>
      <label htmlFor="disposition-reason" className="mt-4 block text-sm font-medium">
        {t("disputes.dispositionReason")}
      </label>
      <textarea
        id="disposition-reason"
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        maxLength={REASON_MAX}
        rows={3}
        aria-describedby="disposition-reason-hint"
        className="mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none"
      />
      <p id="disposition-reason-hint" className="text-xs text-text-muted">
        {t("disputes.dispositionReasonHint")}
      </p>
      <div className="mt-4 flex gap-2">
        <button
          type="button"
          onClick={() =>
            disposition && flow.submit(choice.id, choice.verdict, { disposition, reason: trimmedReason })
          }
          disabled={!disposition || !reasonValid || flow.resolvingId === choice.id}
          className="rounded bg-primary px-3 py-1 text-sm text-white hover:opacity-90 disabled:opacity-50"
        >
          {t("disputes.confirmDisposition")}
        </button>
        <button
          type="button"
          onClick={flow.closeChoice}
          className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50"
        >
          {t("common.cancel")}
        </button>
      </div>
    </Card>
  );
}
