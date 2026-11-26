import { describe, expect, it } from "vitest";

import {
  COMMISSION_EDITABLE_STATUSES,
  MAX_COMMISSION_PERCENT,
  formatRatePercent,
  isCommissionEditable,
  isSameRate,
  isValidCommissionReason,
  parseCommissionPercent,
  previewCommission,
} from "./commission";

/* The admin commission change of one unpaid order: what the form accepts and the sums it previews, rounded as the
 * server rounds them (order-lifecycle.md 2.26.0). */

describe("which orders take another commission", () => {
  it("is only an order with no money yet", () => {
    expect(COMMISSION_EDITABLE_STATUSES).toEqual(["created", "pending_payment"]);
    expect(isCommissionEditable("created")).toBe(true);
    expect(isCommissionEditable("pending_payment")).toBe(true);
    for (const status of ["payment_submitted", "funded", "accepted", "cancelled", ""]) {
      expect(isCommissionEditable(status)).toBe(false);
    }
  });
});

describe("parseCommissionPercent", () => {
  it.each([
    ["2.5", 0.025],
    ["0", 0],
    ["20", 0.2],
    ["20.00", 0.2],
    ["18.99", 0.1899],
    ["0.01", 0.0001],
    ["5", 0.05],
    ["  7.25 ", 0.0725],
    ["05", 0.05],
  ])("reads %j as the rate %d", (text, rate) => {
    expect(parseCommissionPercent(text)).toBe(rate);
  });

  it.each(["", "   ", "-1", "-0.5", "20.01", "21", "100", "2.555", "0.001", "abc", "1e1", "2.", ".5", "2,5", "+3", "Infinity", "NaN", "5%"])(
    "refuses %j",
    (text) => {
      expect(parseCommissionPercent(text)).toBeNull();
    },
  );

  it("caps at the server's 20 percent", () => {
    expect(MAX_COMMISSION_PERCENT).toBe(20);
    expect(parseCommissionPercent(String(MAX_COMMISSION_PERCENT))).toBe(0.2);
  });
});

describe("previewCommission", () => {
  it("adds the commission of the crop cost to the crop and transport costs", () => {
    expect(previewCommission(1000, 200, 0.05)).toEqual({ commissionAmount: 50, orderTotal: 1250 });
    expect(previewCommission(1000, 200, 0.025)).toEqual({ commissionAmount: 25, orderTotal: 1225 });
  });

  it("takes no commission at 0 and a fifth at 20 percent", () => {
    expect(previewCommission(1000, 200, 0)).toEqual({ commissionAmount: 0, orderTotal: 1200 });
    expect(previewCommission(1000, 0, 0.2)).toEqual({ commissionAmount: 200, orderTotal: 1200 });
  });

  it("rounds a half santim away from zero, where float sums round it down", () => {
    // 1.45 x 0.10 = 0.145 and 2.90 x 0.05 = 0.145 exactly: the server's decimal sum makes both 0.15.
    expect(previewCommission(1.45, 0, 0.1).commissionAmount).toBe(0.15);
    expect(previewCommission(2.9, 0, 0.05).commissionAmount).toBe(0.15);
    expect(previewCommission(1.4, 0, 0.025).commissionAmount).toBe(0.04);
  });

  it("is not moved by float noise in the crop cost (18.99 x 170)", () => {
    const cropCost = 18.99 * 170; // 3228.2999999999997 in floats
    expect(previewCommission(cropCost, 150, 0.05)).toEqual({ commissionAmount: 161.42, orderTotal: 3539.72 });
    expect(previewCommission(cropCost, 0, 0.025)).toEqual({ commissionAmount: 80.71, orderTotal: 3309.01 });
  });

  it("keeps the total to the santim", () => {
    expect(previewCommission(0.1, 0.2, 0).orderTotal).toBe(0.3);
    expect(previewCommission(10.1, 0.2, 0.1899)).toEqual({ commissionAmount: 1.92, orderTotal: 12.22 });
  });
});

describe("isSameRate", () => {
  it("compares to the server's 4 decimals", () => {
    expect(isSameRate(0.05, 0.05)).toBe(true);
    expect(isSameRate(0.1 + 0.2, 0.3)).toBe(true);
    expect(isSameRate(0.05, 0.0501)).toBe(false);
  });
});

describe("isValidCommissionReason", () => {
  it("takes 10 to 500 characters once trimmed", () => {
    expect(isValidCommissionReason("")).toBe(false);
    expect(isValidCommissionReason("123456789")).toBe(false);
    expect(isValidCommissionReason("   123456789   ")).toBe(false);
    expect(isValidCommissionReason("1234567890")).toBe(true);
    expect(isValidCommissionReason(`  ${"a".repeat(500)}  `)).toBe(true);
    expect(isValidCommissionReason("a".repeat(501))).toBe(false);
  });
});

describe("formatRatePercent", () => {
  it.each([
    [0.025, "2.5%"],
    [0, "0%"],
    [0.05, "5%"],
    [0.1899, "18.99%"],
    [0.2, "20%"],
    [0.0001, "0.01%"],
    [0.105, "10.5%"],
    [0.07, "7%"],
  ])("shows %d as %s", (rate, text) => {
    expect(formatRatePercent(rate)).toBe(text);
  });

  it("shows nothing for a rate that is not a number", () => {
    expect(formatRatePercent(Number.NaN)).toBe("");
  });
});
