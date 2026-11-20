import { useState } from "react";
import { Card } from "@/components/ui/Card";
import { formatETB, type Locale } from "@/lib/i18n";
import { heldLegs, parseAmount, previewSplit, splitAmounts } from "@/lib/legs";
import { type DisputeVerdictFlow, REASON_MAX, REASON_MIN } from "./verdictFlow";

type Row = Record<string, unknown>;

const cents = (value: number, locale: Locale) => formatETB(value / 100, locale);

/**
 * ESC-24: the per-leg verdict of ESC-20 (partial_refund, action split). The admin says what the
 * farmer gets of the crop leg and what the trucker gets of the transport leg; the buyer gets the
 * rest. The amounts are shown before the confirm, and the server checks them again (ADMIN_021).
 * Only for an escrow on the ledger whose crop leg is still held; otherwise nothing is rendered and
 * the two single verdicts stay the only choice.
 */
export function SplitVerdictCard({
  escrow,
  flow,
  t,
  locale,
  onClose,
}: {
  escrow: Row;
  flow: DisputeVerdictFlow;
  t: (key: string) => string;
  locale: Locale;
  onClose: () => void;
}) {
  const held = heldLegs(escrow);
  const [farmer, setFarmer] = useState(held ? (held.crop / 100).toFixed(2) : "0");
  const [trucker, setTrucker] = useState(held && held.truckerPayable ? (held.transport / 100).toFixed(2) : "0");
  const [reason, setReason] = useState("");
  const [reviewing, setReviewing] = useState(false);
  if (!held) return null;

  const escrowId = String(escrow.escrowId || "");
  const preview = previewSplit(held, parseAmount(farmer), parseAmount(trucker));
  const trimmedReason = reason.trim();
  const reasonValid = trimmedReason.length >= REASON_MIN && trimmedReason.length <= REASON_MAX;
  const valid = preview.errors.length === 0 && reasonValid;
  const busy = flow.resolvingId === escrowId;
  const inputClass =
    "mt-1 w-full rounded border border-gray-300 px-2 py-1 text-sm focus:border-primary focus:outline-none disabled:bg-gray-100";

  const result = (
    <div role="group" aria-label={t("legs.splitResult")} className="mt-4 rounded-lg bg-gray-50 p-3 text-sm">
      <dl className="space-y-1">
      <div className="flex justify-between gap-3">
        <dt>{t("legs.farmerGets")}</dt>
        <dd className="font-medium">{cents(preview.farmer, locale)}</dd>
      </div>
      <div className="flex justify-between gap-3">
        <dt>{t("legs.truckerGets")}</dt>
        <dd className="font-medium">{cents(preview.trucker, locale)}</dd>
      </div>
      <div className="flex justify-between gap-3">
        <dt>{t("legs.buyerGets")}</dt>
        <dd className="font-medium">{cents(Math.max(preview.buyerRefund, 0), locale)}</dd>
      </div>
      {held.commission > 0 && (
        <div className="flex justify-between gap-3 text-text-muted">
          <dt>{t(preview.commissionKept > 0 ? "legs.commissionKept" : "legs.commissionReturned")}</dt>
          <dd>{cents(held.commission, locale)}</dd>
        </div>
      )}
      </dl>
    </div>
  );

  return (
    <Card>
      <h3 className="font-semibold">{t("legs.splitTitle")}</h3>
      <p className="mt-1 text-sm text-text-muted">{t("legs.splitIntro")}</p>

      {reviewing ? (
        <>
          <p className="mt-3 text-sm font-medium">{t("legs.splitConfirmQuestion")}</p>
          {result}
          <p className="mt-3 text-sm">
            <span className="text-text-muted">{t("disputes.dispositionReason")}:</span> {trimmedReason}
          </p>
          <div className="mt-4 flex gap-2">
            <button
              type="button"
              onClick={() =>
                flow.submit(escrowId, "partial_refund", undefined, { ...splitAmounts(preview), notes: trimmedReason })
              }
              disabled={!valid || busy}
              className="rounded bg-primary px-3 py-1 text-sm text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("legs.splitConfirm")}
            </button>
            <button
              type="button"
              onClick={() => setReviewing(false)}
              disabled={busy}
              className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
            >
              {t("common.back")}
            </button>
          </div>
        </>
      ) : (
        <>
          <label htmlFor="split-farmer" className="mt-4 block text-sm font-medium">
            {t("legs.splitFarmerLabel")}
          </label>
          <input
            id="split-farmer"
            inputMode="decimal"
            value={farmer}
            onChange={(e) => setFarmer(e.target.value)}
            aria-describedby="split-farmer-hint"
            className={inputClass}
          />
          <p id="split-farmer-hint" className="text-xs text-text-muted">
            {t("legs.splitHeld").replace("{amount}", cents(held.crop, locale))}
          </p>

          <label htmlFor="split-trucker" className="mt-3 block text-sm font-medium">
            {t("legs.splitTruckerLabel")}
          </label>
          <input
            id="split-trucker"
            inputMode="decimal"
            value={trucker}
            onChange={(e) => setTrucker(e.target.value)}
            disabled={!held.truckerPayable}
            aria-describedby="split-trucker-hint"
            className={inputClass}
          />
          <p id="split-trucker-hint" className="text-xs text-text-muted">
            {held.truckerPayable
              ? t("legs.splitHeld").replace("{amount}", cents(held.transport, locale))
              : t(held.transport > 0 ? "legs.errorNoTrucker" : "legs.splitNoTransport")}
          </p>

          {result}
          {preview.errors.length > 0 && (
            <ul role="alert" className="mt-2 list-disc pl-5 text-xs text-danger">
              {preview.errors.map((key) => (
                <li key={key}>{t(key)}</li>
              ))}
            </ul>
          )}

          <label htmlFor="split-reason" className="mt-4 block text-sm font-medium">
            {t("disputes.dispositionReason")}
          </label>
          <textarea
            id="split-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            maxLength={REASON_MAX}
            rows={3}
            aria-describedby="split-reason-hint"
            className={inputClass}
          />
          <p id="split-reason-hint" className="text-xs text-text-muted">
            {t("disputes.dispositionReasonHint")}
          </p>
          <div className="mt-4 flex gap-2">
            <button
              type="button"
              onClick={() => setReviewing(true)}
              disabled={!valid}
              className="rounded bg-primary px-3 py-1 text-sm text-white hover:opacity-90 disabled:opacity-50"
            >
              {t("legs.splitReview")}
            </button>
            <button
              type="button"
              onClick={onClose}
              className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50"
            >
              {t("common.cancel")}
            </button>
          </div>
        </>
      )}
    </Card>
  );
}
