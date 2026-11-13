import { describe, expect, it } from "vitest";

import { t as translate } from "@/lib/i18n";

import {
  adminQueueLabel,
  commitmentResponseLabel,
  deriveStage,
  escrowStatusLabel,
  escrowStatusVariant,
  orderStatusLabel,
  orderStatusVariant,
  partyLabel,
  stageLabel,
  timelineActionLabel,
  timelineEntityLabel,
  transportFailureLabel,
} from "./transaction";

describe("outcome label rows (FRD-18)", () => {
  it("name the entity and the action in all three locales, and leave an unknown entity raw", () => {
    const en = (key: string) => translate(key, "en");
    expect(timelineEntityLabel("label", en)).toBe("Outcome");
    expect(timelineEntityLabel("label", (key) => translate(key, "am"))).toBe("ውጤት");
    expect(timelineEntityLabel("label", (key) => translate(key, "om"))).toBe("Bu'aa");
    expect(timelineEntityLabel("escrow", en)).toBe("Escrow");
    expect(timelineEntityLabel("fraud", en)).toBe("fraud");
    expect(timelineActionLabel("outcome_label", en)).toBe("Outcome labelled (admin)");
  });
});

describe("held escrow (FRD-03)", () => {
  it("has a badge colour and a label, and its audit actions are labelled", () => {
    const t = (key: string) => translate(key, "en");
    expect(escrowStatusVariant.held).toBe("warning");
    expect(escrowStatusLabel("held", t)).toBe("Under review");
    expect(timelineActionLabel("fraud_hold", t)).toBe("Held for review");
    expect(timelineActionLabel("fraud_hold_release", t)).toBe("Released after review (admin)");
    expect(timelineActionLabel("fraud_hold_refund", t)).toBe("Refunded after review (admin)");
  });
});

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
      "awaiting_transport",
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

  // ESC-15: the commitment status and fields read as words in every locale.
  it("labels awaiting_transport and the commitment values in every locale", () => {
    for (const locale of ["en", "am", "om"] as const) {
      const tr = (key: string) => translate(key, locale);
      expect(orderStatusLabel("awaiting_transport", tr)).not.toBe("awaiting_transport");
      expect(tr("transactions.action_trucker_responded")).not.toBe("transactions.action_trucker_responded");
      for (const r of ["pending", "accepted", "declined", "expired", "cancelled", "not_asked", "failed_capacity", "none", "no_show"]) {
        expect(commitmentResponseLabel(r, tr)).not.toBe(r);
      }
      for (const f of ["trucker_declined", "trucker_expired", "trucker_cancelled", "trip_cancelled", "trip_full", "trucker_no_show"]) {
        expect(transportFailureLabel(f, tr)).not.toBe(f);
      }
      for (const p of ["buyer", "farmer", "trucker", "system"]) {
        expect(partyLabel(p, tr)).not.toBe(p);
      }
    }
    expect(orderStatusVariant.awaiting_transport).toBe("warning");
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
      // ESC-34: the pickup judgement's queue reasons.
      for (const r of ["both_present_no_scan", "unchecked_check_in", "late_pickup_evidence"]) {
        expect(adminQueueLabel(r, tr)).not.toBe(r);
      }
      // ESC-36: an overdue delivery or a silent buyer whose money could not be moved.
      for (const r of ["delivery_overdue", "silent_buyer"]) {
        expect(adminQueueLabel(r, tr)).not.toBe(r);
      }
      expect(tr("transactions.adminQueue")).not.toBe("transactions.adminQueue");
      expect(tr("transactions.pickupDeadline")).not.toBe("transactions.pickupDeadline");
      expect(adminQueueLabel("t11_verdict", tr)).toBe("t11_verdict");
    }
  });
});
