import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { describe, expect, it, vi } from "vitest";

import { EscrowDetailPage } from "./EscrowDetailPage";

/* FUP-35: the escrow audit trail printed the raw action; it now uses the timeline labels. */

const mocks = vi.hoisted(() => ({
  escrow: { data: undefined as unknown, isLoading: false },
  audit: { data: undefined as unknown },
}));

vi.mock("@/hooks/useQuery", () => ({
  useEscrowDetail: () => mocks.escrow,
  useEscrowAudit: () => mocks.audit,
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

describe("EscrowDetailPage audit trail labels (FUP-35)", () => {
  it("names each audit action in words", () => {
    mocks.escrow.data = { escrow: { escrowId: "escrow-order-ord-1", status: "escrowed", amount: 1000 } };
    mocks.audit.data = {
      auditEntries: [
        { action: "order_funded", timestamp: "2026-10-20T07:00:00Z" },
        { action: "dispute_refused_other_delivery", timestamp: "2026-10-20T08:00:00Z" },
        { action: "manual_payout_queued", timestamp: "2026-10-20T09:00:00Z" },
      ],
    };

    render(
      <MemoryRouter>
        <EscrowDetailPage />
      </MemoryRouter>,
    );

    expect(screen.getByText("Order paid into escrow")).toBeTruthy();
    expect(screen.getByText("Dispute refused (another delivery)")).toBeTruthy();
    expect(screen.getByText("Queued for manual payout")).toBeTruthy();
    expect(screen.queryByText("dispute_refused_other_delivery")).toBeNull();
  });
});
