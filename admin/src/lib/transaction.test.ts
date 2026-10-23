import { describe, expect, it } from "vitest";

import { t as translate } from "@/lib/i18n";

import {
  adminQueueLabel,
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
  });

  // ESC-09: the order follows the money; same table as AdminDeriveStageTests (server).
  it("gives paying out, a decided dispute and a rejection their own stages", () => {
    expect(deriveStage("disbursing")).toBe("paying_out");
    expect(deriveStage("dispute_resolved")).toBe("resolved");
    expect(deriveStage("rejected")).toBe("cancelled");
  });

  it("labels the new stages and statuses in every locale", () => {
    for (const locale of ["en", "am", "om"] as const) {
      const tr = (key: string) => translate(key, locale);
      expect(stageLabel("paying_out", tr)).not.toBe("paying_out");
      expect(stageLabel("resolved", tr)).not.toBe("resolved");
      expect(orderStatusLabel("rejected", tr)).not.toBe("rejected");
      expect(timelineActionLabel("order_money_mismatch", tr)).not.toBe("order_money_mismatch");
    }
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
    // FUP-35: an action is humanized instead (auditActions.test.ts).
  });
});

describe("admin queue label (ESC-06)", () => {
  it("names the hold-cap reason in every locale and keeps an unknown reason raw", () => {
    for (const locale of ["en", "am", "om"] as const) {
      const tr = (key: string) => translate(key, locale);
      expect(adminQueueLabel("hold_cap", tr)).not.toBe("hold_cap");
      expect(tr("transactions.adminQueue")).not.toBe("transactions.adminQueue");
      expect(tr("transactions.pickupDeadline")).not.toBe("transactions.pickupDeadline");
      expect(adminQueueLabel("t11_verdict", tr)).toBe("t11_verdict");
    }
  });
});
