import { describe, expect, it } from "vitest";

import en from "@/i18n/en.json";
import { t, type Locale } from "@/lib/i18n";

/* Every key the manual payouts page (ESC-02) reads must exist in all three locales,
 * otherwise the page shows the raw key to an Amharic or Afan Oromo admin. */

const locales: Locale[] = ["en", "am", "om"];

const payoutKeys = Object.keys(en.payouts).map((k) => `payouts.${k}`);
const otherKeys = [
  "nav.manualPayouts",
  "disputes.farmerWins",
  "disputes.buyerWins",
  "errors.ESCROW_015",
  "errors.ESCROW_029",
  "errors.ESCROW_034",
  "errors.ESCROW_035",
  "errors.ESCROW_036",
  "errors.ESCROW_037",
  "errors.ADMIN_013",
];

describe("manual payout strings exist in every locale", () => {
  for (const locale of locales) {
    it(`${locale} has every payouts key, the nav label and the new error codes`, () => {
      const missing = [...payoutKeys, ...otherKeys].filter((key) => t(key, locale) === key);
      expect(missing).toEqual([]);
    });
  }

  it("keeps the placeholders the page fills in", () => {
    for (const locale of locales) {
      expect(t("payouts.age", locale)).toContain("{hours}");
      expect(t("payouts.confirmPaid", locale)).toContain("{amount}");
      expect(t("payouts.confirmPaid", locale)).toContain("{phone}");
      expect(t("payouts.confirmFound", locale)).toContain("{amount}");
      expect(t("payouts.confirmFound", locale)).toContain("{phone}");
      expect(t("payouts.outcomeUnknownHint", locale)).toContain("{gatewayRef}");
      expect(t("payouts.lockAge", locale)).toContain("{hours}");
      expect(t("payouts.verdictConflict", locale)).toContain("{verdict}");
    }
  });
});

/* FUP-02: the unapplied payments page, its error codes and the two timeline rows a recorded
 * refund writes (escrow-audit and transaction-audit). */
describe("unapplied payment strings exist in every locale", () => {
  const unappliedKeys = Object.keys(en.unapplied).map((k) => `unapplied.${k}`);
  const keys = [
    ...unappliedKeys,
    "nav.unappliedPayments",
    "errors.ESCROW_015",
    "errors.ESCROW_036",
    "errors.ESCROW_040",
    "errors.ESCROW_042",
    "errors.ESCROW_043",
    "errors.ESCROW_044",
    "transactions.action_order_payment_refunded_manual",
    "transactions.action_admin_payment_refunded_manual",
  ];

  for (const locale of locales) {
    it(`${locale} has every unapplied key, the nav label, the error codes and the timeline labels`, () => {
      expect(keys.filter((key) => t(key, locale) === key)).toEqual([]);
    });
  }

  it("keeps the placeholders the page fills in", () => {
    for (const locale of locales) {
      expect(t("unapplied.paymentUnknown", locale)).toContain("{merchOrderId}");
      expect(t("unapplied.confirmRefund", locale)).toContain("{amount}");
      expect(t("unapplied.confirmRefund", locale)).toContain("{payment}");
      expect(t("unapplied.confirmRefund", locale)).toContain("{ref}");
      expect(t("unapplied.reason_status", locale)).toContain("{status}");
      expect(t("unapplied.reason_error", locale)).toContain("{code}");
    }
  });
});

/* FRD-03: an escrow held for fraud review, and its audit actions, are labelled in every locale. */
describe("fraud hold labels exist in every locale", () => {
  const keys = [
    "escrows.held",
    "transactions.escrow_held",
    "transactions.action_fraud_hold",
    "transactions.action_fraud_hold_release",
    "transactions.action_fraud_hold_refund",
  ];
  for (const locale of locales) {
    it(`${locale} labels the held status and its actions`, () => {
      expect(keys.filter((key) => t(key, locale) === key)).toEqual([]);
    });
  }
});

/* FRD-05: the Fraud Review action results, the decision labels and the codes the actions answer with. */
describe("fraud review strings exist in every locale", () => {
  const keys = [
    ...Object.keys(en.fraud).map((k) => `fraud.${k}`),
    "errors.ADMIN_009",
    "errors.ADMIN_017",
    "users.buyer",
    "users.farmer",
    "users.trucker",
    "escrows.escrowed",
    "escrows.disputed",
    "escrows.dispute_resolved",
    "escrows.held",
  ];
  for (const locale of locales) {
    it(`${locale} has every fraud key and the codes`, () => {
      expect(keys.filter((key) => t(key, locale) === key)).toEqual([]);
    });
  }

  it("keeps the placeholders the page fills in", () => {
    for (const locale of locales) {
      expect(t("fraud.result_reject", locale)).toContain("{userId}");
      expect(t("fraud.result_whitelist", locale)).toContain("{date}");
    }
  });

  it("keeps the fraud wording of ADMIN_006 and ADMIN_007 off the shared error keys", () => {
    // The user page answers its own ADMIN_007 (user not found) through errors.*.
    for (const locale of locales) {
      expect(t("errors.ADMIN_006", locale)).toBe("errors.ADMIN_006");
      expect(t("errors.ADMIN_007", locale)).toBe("errors.ADMIN_007");
    }
  });
});

/* ESC-08: a party's cancel (and the system cancel of a dispute verdict) is audited as
 * delivery_cancelled; without a label the transaction timeline shows the raw key. */
describe("delivery cancel audit label exists in every locale", () => {
  for (const locale of locales) {
    it(`${locale} labels action_delivery_cancelled`, () => {
      const key = "transactions.action_delivery_cancelled";
      expect(t(key, locale)).not.toBe(key);
    });
  }
});

/* FUP-16: the dispute disposition panel, its error codes and the new timeline actions. */
describe("dispute disposition strings exist in every locale", () => {
  const disputeKeys = [
    "tabOpen",
    "tabAwaiting",
    "awaitingHint",
    "chooseDisposition",
    "saved",
    "verdict_farmer_wins",
    "verdict_buyer_wins",
    "dispositionTitle",
    "caseGoodsMoving",
    "caseBeforeDelivery",
    "dispositionLegend",
    "disposition_return_to_farmer",
    "disposition_delivered_anyway",
    "disposition_settle_now",
    "disposition_wait_for_delivery",
    "dispositionHint_return_to_farmer",
    "dispositionHint_delivered_anyway",
    "dispositionHint_settle_now",
    "dispositionHint_wait_for_delivery",
    "dispositionReason",
    "dispositionReasonHint",
    "confirmDisposition",
    "recordedChoice",
    "noChoice",
    "loadMore",
  ].map((k) => `disputes.${k}`);
  const otherKeys = [
    "errors.ADMIN_015",
    "errors.ADMIN_016",
    "transactions.action_dispute_resolved_goods_returned",
    "transactions.action_dispute_resolved_delivered_anyway",
    "transactions.action_dispute_resolved_settled_early",
    "transactions.action_dispute_resolved_waiting_for_delivery",
    "common.cancel",
  ];
  for (const locale of locales) {
    it(`${locale} has every disposition key`, () => {
      const missing = [...disputeKeys, ...otherKeys].filter((key) => t(key, locale) === key);
      expect(missing).toEqual([]);
    });
  }
});

/* LT-13: the dispute page, its Trip trail tab and the trail's not-found code. */
describe("trip trail strings exist in every locale", () => {
  const keys = [
    ...Object.keys(en.trail).map((k) => `trail.${k}`),
    "disputes.detailTitle",
    "disputes.evidence",
    "disputes.raisedAt",
    "disputes.delivery",
    "errors.ADMIN_018",
    "common.retry",
  ];
  for (const locale of locales) {
    it(`${locale} has every trail key`, () => {
      expect(keys.filter((key) => t(key, locale) === key)).toEqual([]);
    });
  }

  it("keeps the placeholders the tab fills in", () => {
    const placeholders: Record<string, string[]> = {
      window: ["{from}", "{until}"],
      windowOpen: ["{from}"],
      mapLabel: ["{fixes}"],
      legValue: ["{km}", "{minutes}", "{kmh}"],
      legSameTime: ["{km}"],
      flagCoarse: ["{meters}"],
      flagLate: ["{time}"],
      event_paused: ["{time}"],
      pauseRange: ["{from}", "{until}"],
      gapRange: ["{from}", "{until}", "{minutes}"],
      saysSharedWholeWay: ["{fixes}"],
      saysGaps: ["{gaps}"],
      saysDeclined: ["{fixes}"],
      saysNoJumps: ["{kmh}"],
      saysJumps: ["{jumps}"],
      saysMocked: ["{count}"],
      saysCoarse: ["{count}"],
      saysLate: ["{count}"],
      saysPauses: ["{count}"],
      saysDistance: ["{km}", "{minutes}"],
    };
    for (const locale of locales) {
      for (const [key, names] of Object.entries(placeholders)) {
        for (const name of names) {
          expect([locale, key, t(`trail.${key}`, locale).includes(name)]).toEqual([locale, key, true]);
        }
      }
    }
  });
});
