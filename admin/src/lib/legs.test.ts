import { describe, expect, it } from "vitest";

import { t } from "@/lib/i18n";
import { heldLegs, parseAmount, previewSplit, recordedSplit, splitAmounts, stuckAge } from "./legs";

/* ESC-24: the split arithmetic mirrors the server's EscrowLedgerBook.PlanSplit (ESC-20, P.8), so
 * the amounts the admin confirms are amounts the server accepts. */

const escrow = {
  farmerPayout: 15000,
  truckerPayout: 1500,
  commission: 750,
  truckerId: "trucker-1",
  legStatus: { crop: "held", transport: "held", commission: "held" },
};

describe("heldLegs", () => {
  it("reads what a ledger escrow still holds, in cents", () => {
    expect(heldLegs(escrow)).toEqual({ crop: 1_500_000, transport: 150_000, commission: 75_000, truckerPayable: true });
  });

  it("is null where the server refuses a split: no ledger, or the crop leg decided", () => {
    expect(heldLegs({ farmerPayout: 15000 })).toBeNull();
    expect(heldLegs({ ...escrow, legStatus: { crop: "released", transport: "held" } })).toBeNull();
    expect(heldLegs(null)).toBeNull();
  });

  it("counts a leg that is not held as 0, and a trucker payable only with a trucker", () => {
    expect(heldLegs({ ...escrow, legStatus: { crop: "held", transport: "released", commission: "held" } })).toMatchObject({
      transport: 0,
      truckerPayable: false,
    });
    expect(heldLegs({ ...escrow, truckerId: undefined })).toMatchObject({ transport: 150_000, truckerPayable: false });
    expect(heldLegs({ ...escrow, legStatus: { crop: "held" } })).toMatchObject({ transport: 0, commission: 0 });
  });
});

describe("parseAmount", () => {
  it("takes 0 or more with at most 2 decimals", () => {
    expect(parseAmount("9000")).toBe(900_000);
    expect(parseAmount(" 9000.5 ")).toBe(900_050);
    expect(parseAmount("0.07")).toBe(7);
    expect(parseAmount("1234.56")).toBe(123_456);
  });

  it("refuses everything else", () => {
    for (const text of ["", "-1", "1.234", "1,000", "abc", "1e3", "."]) {
      expect(parseAmount(text)).toBeNull();
    }
  });
});

describe("previewSplit", () => {
  const held = heldLegs(escrow)!;

  it("the buyer gets the rest of the crop and transport legs; the commission is kept when the farmer is paid", () => {
    const preview = previewSplit(held, 900_000, 150_000);
    expect(preview.errors).toEqual([]);
    expect(preview.buyerRefund).toBe(600_000);
    expect(preview.commissionKept).toBe(75_000);
    expect(splitAmounts(preview)).toEqual({ farmerAmount: 9000, truckerAmount: 1500, buyerRefund: 6000 });
  });

  it("the commission goes back to the buyer when the farmer gets nothing", () => {
    const preview = previewSplit(held, 0, 150_000);
    expect(preview.buyerRefund).toBe(1_500_000 + 75_000);
    expect(preview.commissionKept).toBe(0);
    expect(preview.commissionRefunded).toBe(75_000);
  });

  it("the three amounts always add up to what the server expects", () => {
    for (const [farmer, trucker] of [[1_500_000, 150_000], [1, 0], [0, 0], [749_999, 1]]) {
      const preview = previewSplit(held, farmer, trucker);
      const expected = held.crop + held.transport + (farmer > 0 ? 0 : held.commission);
      expect(preview.farmer + preview.trucker + preview.buyerRefund).toBe(expected);
    }
  });

  it("names what is wrong", () => {
    expect(previewSplit(held, 1_500_001, 0).errors).toEqual(["legs.errorFarmerMax"]);
    expect(previewSplit(held, 0, 150_001).errors).toEqual(["legs.errorTruckerMax"]);
    expect(previewSplit(held, null, null).errors).toEqual(["legs.errorFarmerAmount", "legs.errorTruckerAmount"]);
    expect(previewSplit({ ...held, truckerPayable: false }, 0, 100).errors).toEqual(["legs.errorNoTrucker"]);
  });
});

describe("recordedSplit and stuckAge", () => {
  it("reads the split the server recorded", () => {
    expect(recordedSplit({ verdictAmounts: { farmerAmount: 9000, truckerAmount: 1500, buyerRefund: 6000 } })).toEqual({
      farmerAmount: 9000,
      truckerAmount: 1500,
      buyerRefund: 6000,
    });
    expect(recordedSplit({})).toBeNull();
  });

  it("shows hours under two days and days from there", () => {
    const en = (key: string) => t(key, "en");
    expect(stuckAge(3, en)).toBe("3 h");
    expect(stuckAge(47, en)).toBe("47 h");
    expect(stuckAge(50, en)).toBe("2 d");
  });
});
