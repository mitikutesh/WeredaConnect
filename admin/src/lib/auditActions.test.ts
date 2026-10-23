import { describe, expect, it } from "vitest";

import am from "@/i18n/am.json";
import en from "@/i18n/en.json";
import om from "@/i18n/om.json";
import { t, type Locale } from "@/lib/i18n";

import { timelineActionLabel } from "./transaction";

/* FUP-35: every audit action the server writes has a label in all three locales, so the
 * transaction timeline, the escrow audit trail and the audit log never show snake_case.
 * A server change that writes a new action adds it here and labels it in en, am and om. */

/** Escrow-audit rows (EscrowService, EscrowCronFunction, TelebirrWebhookFunction, AdminFunction). */
const ESCROW_AUDIT_ACTIONS = [
  // Hold, the Telebirr webhook and the reconciliation sweep
  "initiate",
  "hold_pending",
  "hold_success",
  "hold_failed",
  "webhook_hold_success",
  "webhook_hold_failed",
  "webhook_amount_mismatch",
  "webhook_payer_unbound",
  "webhook_payer_mismatch",
  "reconcile_confirm",
  "reconcile_cancel",
  // Order-funded payment (checkout, webhook, payment that reached no order)
  "order_checkout_opened",
  "order_checkout_failed",
  "order_checkout_unknown",
  "order_funded",
  "order_payment_failed",
  "order_payment_status_ignored",
  "order_payment_unmatched",
  "order_payment_duplicate",
  "order_payment_unprocessed",
  "delivery_linked",
  "trucker_backfilled",
  // Settlement
  "release",
  "release_failed",
  "release_blocked_kyc",
  "trucker_release",
  "trucker_release_failed",
  "trucker_release_blocked_kyc",
  "complete",
  "refund",
  "refund_failed",
  "refund_scheduled",
  "refund_order_mismatch_overridden",
  "admin_settlement_order_refused",
  "auto_release_blocked",
  "auto_refund_blocked",
  "hold_cap_reached",
  "order_money_mismatch",
  "order_money_ahead",
  // Manual payout queue (ESC-02, ESC-04, ESC-05)
  "stale_lock_parked",
  "legacy_trucker_parked",
  "manual_payout_queued",
  "manual_payout_paid",
  "manual_payout_refused",
  // Disputes (ESC-03, ESC-07)
  "dispute",
  "dispute_propagation_failed",
  "dispute_refused_not_held",
  "dispute_refused_other_delivery",
  "dispute_refused_manual_payout",
  "dispute_refused_settlement_in_flight",
  "admin_dispute_resolved",
  "dispute_verdict_mismatch",
  "dispute_verdict_propagation_failed",
  "dispute_settlement_blocked",
  "dispute_order_mismatch_overridden",
];

/** Transaction-audit rows (TransactionAuditWriter callers) and the synthesised timeline rows. */
const TRANSACTION_AUDIT_ACTIONS = [
  "order_created",
  "payment_verified",
  "farmer_responded",
  "order_updated",
  "order_cancelled",
  "order_money_mismatch",
  "order_money_ahead",
  "delivery_created",
  "picked_up",
  "delivered",
  "delivery_updated",
  "delivery_cancelled",
  "dispute",
  "dispute_resolved",
  "dispute_mismatch_override",
  "refund_mismatch_override",
  "admin_dispute_resolved",
  "admin_manual_payout",
  "admin_queue_entered",
  "admin_queue_cleared",
  // AdminFunction writes fraud_<action> and user_<action> for the actions the admin UI sends
  "fraud_approve",
  "fraud_reject",
  "fraud_whitelist",
  "user_suspend",
  "user_unsuspend",
  "user_verify",
  "user_change_role",
];

/** Written by an open PR that has no admin change of its own; labelled here ahead of the merge. */
const PENDING_ACTIONS = [
  "dispute_refused_refund_scheduled", // FUP-32 (PR #168)
];

const locales: Locale[] = ["en", "am", "om"];

const actionKeys = (messages: { transactions: Record<string, string> }) =>
  Object.keys(messages.transactions)
    .filter((k) => k.startsWith("action_"))
    .sort();

describe("every audit action the server writes has a label (FUP-35)", () => {
  const actions = [
    ...new Set([...ESCROW_AUDIT_ACTIONS, ...TRANSACTION_AUDIT_ACTIONS, ...PENDING_ACTIONS]),
  ];

  for (const locale of locales) {
    it(`${locale} labels every action`, () => {
      const missing = actions.filter((a) => t(`transactions.action_${a}`, locale) === `transactions.action_${a}`);
      expect(missing).toEqual([]);
    });
  }

  it("labels the refusal rows the ticket names, in words", () => {
    const tr = (key: string) => t(key, "en");
    expect(timelineActionLabel("dispute_refused_not_held", tr)).toBe("Dispute refused (money not held)");
    expect(timelineActionLabel("dispute_refused_other_delivery", tr)).toBe("Dispute refused (another delivery)");
  });
});

describe("action labels are the same set in en, am and om", () => {
  it("has no action key in one locale that another lacks", () => {
    expect(actionKeys(am)).toEqual(actionKeys(en));
    expect(actionKeys(om)).toEqual(actionKeys(en));
  });

  it("has no empty label", () => {
    for (const messages of [en, am, om]) {
      const empty = actionKeys(messages).filter(
        (k) => (messages.transactions as Record<string, string>)[k].trim() === "",
      );
      expect(empty).toEqual([]);
    }
  });
});

describe("an action without a label reads as words", () => {
  const tr = (key: string) => t(key, "en");

  it("humanizes the snake_case action", () => {
    expect(timelineActionLabel("some_future_action", tr)).toBe("Some future action");
    expect(timelineActionLabel("refund", tr)).toBe("Refunded");
  });

  it("keeps an empty action empty", () => {
    expect(timelineActionLabel("", tr)).toBe("");
  });
});
