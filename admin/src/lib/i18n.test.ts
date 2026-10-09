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
