import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import { AuditLogPage } from "./AuditLogPage";

/* FRD-18: the audit log names an outcome label row like the transaction timeline does: the
 * entity badge and the action in words, at the time the admin decided (the server sends it). */

vi.mock("@/hooks/useQuery", () => ({
  useAuditLog: () => ({
    isLoading: false,
    data: {
      entries: [
        {
          entityType: "label",
          entityId: "outcome-labels",
          action: "outcome_label",
          actor: "admin-1",
          toState: "fraud_confirmed",
          timestamp: "2026-11-02T08:00:00.0000000Z",
        },
        {
          entityType: "fraud",
          entityId: "c2lnbmFsLTE",
          action: "fraud_reject",
          actor: "admin-1",
          toState: "reject",
          timestamp: "2026-11-02T07:59:59.0000000Z",
        },
      ],
    },
  }),
  useEscrowAudit: () => ({
    isLoading: false,
    data: {
      auditEntries: [
        {
          escrowId: "escrow-1",
          action: "hold_success",
          actor: "system",
          fromState: "initiated",
          toState: "escrowed",
          timestamp: "2026-11-02T07:00:00.0000000Z",
        },
      ],
    },
  }),
}));

vi.mock("@/hooks/useLocale", async () => {
  const { t } = await import("@/lib/i18n");
  return {
    useLocale: () => ({
      locale: "en" as const,
      setLocale: vi.fn(),
      t: (key: string) => t(key, "en"),
    }),
  };
});

describe("AuditLogPage", () => {
  it("shows an outcome label row with its entity and action in words", () => {
    render(<AuditLogPage />);

    // The table may render a row more than once (wide and narrow layouts), so count, never pick one.
    expect(screen.getAllByText("Outcome").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Outcome labelled (admin)").length).toBeGreaterThan(0);
    expect(screen.queryAllByText("label")).toHaveLength(0);
    // An entity without a translation keeps its raw name.
    expect(screen.getAllByText("fraud").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Fraud signal rejected (admin)").length).toBeGreaterThan(0);
  });

  /* FUP-80: the states read as the timeline names them, and a search matches the labels too. */
  it("labels the states and finds a row by the words it shows as well as by its raw values", async () => {
    const user = userEvent.setup();
    render(<AuditLogPage />);

    expect(screen.getAllByText("Initiated → Held in escrow").length).toBeGreaterThan(0);
    // A row with only a to state shows that side alone.
    expect(screen.getAllByText("→ Fraud confirmed").length).toBeGreaterThan(0);
    expect(screen.queryAllByText(/escrowed|fraud_confirmed/)).toHaveLength(0);

    const search = screen.getByRole("textbox");
    // The action label: the raw action is fraud_reject.
    await user.type(search, "rejected (admin)");
    expect(screen.getAllByText("Fraud signal rejected (admin)").length).toBeGreaterThan(0);
    expect(screen.queryAllByText("Outcome labelled (admin)")).toHaveLength(0);
    expect(screen.queryAllByText("Initiated → Held in escrow")).toHaveLength(0);

    // A state label: the raw state is escrowed.
    await user.clear(search);
    await user.type(search, "held in escrow");
    expect(screen.getAllByText("Initiated → Held in escrow").length).toBeGreaterThan(0);
    expect(screen.queryAllByText("Fraud signal rejected (admin)")).toHaveLength(0);

    // The raw values still match.
    await user.clear(search);
    await user.type(search, "fraud_reject");
    expect(screen.getAllByText("Fraud signal rejected (admin)").length).toBeGreaterThan(0);
    expect(screen.queryAllByText("Initiated → Held in escrow")).toHaveLength(0);
  });
});
