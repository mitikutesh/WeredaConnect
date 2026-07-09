import { describe, expect, it } from "vitest";

import {
  deriveStage,
  orderStatusLabel,
  stageLabel,
  timelineActionLabel,
} from "./transaction";

describe("deriveStage", () => {
  // Must stay in sync with the server DeriveStage (AdminFunction.cs).
  it("groups pre-payment statuses as awaiting_payment", () => {
    expect(deriveStage("created")).toBe("awaiting_payment");
    expect(deriveStage("pending_payment")).toBe("awaiting_payment");
  });

  it("groups mid-lifecycle statuses as in_progress", () => {
    for (const s of [
      "payment_submitted",
      "funded",
      "accepted",
      "pickup_confirmed",
      "in_transit",
      "disbursing",
    ]) {
      expect(deriveStage(s)).toBe("in_progress");
    }
  });

  it("maps terminal + exception statuses", () => {
    expect(deriveStage("delivered")).toBe("delivered");
    expect(deriveStage("settled")).toBe("completed");
    expect(deriveStage("cancelled")).toBe("cancelled");
    expect(deriveStage("payment_failed")).toBe("problem");
    expect(deriveStage("disputed")).toBe("problem");
    expect(deriveStage("dispute_resolved")).toBe("problem");
  });

  it("falls back to unknown for unmapped statuses", () => {
    expect(deriveStage("something_new")).toBe("unknown");
  });
});

describe("label helpers", () => {
  // A translate fn that only knows one key — everything else "misses".
  const t = (key: string) =>
    key === "transactions.status_funded" ? "Paid" : key;

  it("returns the translation when the key exists", () => {
    expect(orderStatusLabel("funded", t)).toBe("Paid");
  });

  it("falls back to the raw value when the key is missing", () => {
    expect(orderStatusLabel("in_transit", t)).toBe("in_transit");
    expect(stageLabel("in_progress", t)).toBe("in_progress");
    expect(timelineActionLabel("some_new_action", t)).toBe("some_new_action");
  });
});
