import { describe, expect, it } from "vitest";

import { countOpenDisputes, disputeReasonTitle, isOpenDispute } from "@/lib/disputes";
import { t, type Locale } from "@/lib/i18n";

const locales: Locale[] = ["en", "am", "om"];
const titled = [
  "damaged_goods",
  "wrong_quantity",
  "wrong_crop",
  "not_delivered",
  "quality_mismatch",
  "delivery_overdue",
  "silent_buyer",
  "dropoff_out_of_range",
];

describe("open disputes", () => {
  it("counts every row still waiting for a verdict, as the list's Open tab does", () => {
    expect(isOpenDispute({ status: "disputed" })).toBe(true);
    expect(isOpenDispute({ status: "pending" })).toBe(true);
    expect(isOpenDispute({ status: "resolved" })).toBe(false);
    expect(isOpenDispute(null)).toBe(false);
    expect(countOpenDisputes([{ status: "disputed" }, { status: "resolved" }, { status: "disputed" }, null])).toBe(2);
    expect(countOpenDisputes([])).toBe(0);
  });
});

describe("dispute reason titles", () => {
  it("gives the reason codes of the contract and of the system's disputes a title", () => {
    const en = (key: string) => t(key, "en");
    expect(disputeReasonTitle("wrong_quantity", en)).toBe("Wrong quantity");
    expect(disputeReasonTitle("damaged_goods", en)).toBe("Damaged goods");
    expect(disputeReasonTitle("silent_buyer", en)).toBe("Silent buyer");
    expect(disputeReasonTitle("delivery_overdue", en)).toBe("Delivery overdue");
  });

  it("has every title in every locale", () => {
    for (const locale of locales) {
      const missing = titled.filter((code) => disputeReasonTitle(code, (key) => t(key, locale)) === null);
      expect(missing).toEqual([]);
    }
  });

  it("gives no title to other, an unknown code, free text or a missing reason", () => {
    const en = (key: string) => t(key, "en");
    expect(disputeReasonTitle("other", en)).toBeNull();
    expect(disputeReasonTitle("short_delivery", en)).toBeNull();
    expect(disputeReasonTitle("The sacks were wet. Half is lost.", en)).toBeNull();
    expect(disputeReasonTitle("", en)).toBeNull();
    expect(disputeReasonTitle(undefined, en)).toBeNull();
  });

  it("has the nav count line with its placeholder in every locale", () => {
    for (const locale of locales) {
      expect(t("nav.disputesOpen", locale)).toContain("{count}");
    }
  });
});
