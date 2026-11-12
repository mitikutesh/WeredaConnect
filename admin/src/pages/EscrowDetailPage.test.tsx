import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { EscrowDetailPage } from "./EscrowDetailPage";

/* FUP-35: the escrow audit trail printed the raw action; it now uses the timeline labels. */

const mocks = vi.hoisted(() => ({
  escrow: { data: undefined as unknown, isLoading: false },
  audit: { data: undefined as unknown },
  ledger: {
    data: undefined as unknown,
    isLoading: false,
    isError: false,
    hasNextPage: false,
    isFetchingNextPage: false,
    fetchNextPage: vi.fn(),
  },
  ledgerCalls: [] as { escrowId: string; enabled: boolean }[],
}));

vi.mock("@/hooks/useQuery", () => ({
  useEscrowDetail: () => mocks.escrow,
  useEscrowAudit: () => mocks.audit,
  useEscrowLedger: (escrowId: string, enabled: boolean) => {
    mocks.ledgerCalls.push({ escrowId, enabled });
    return mocks.ledger;
  },
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

/* ESC-24: the transport leg, the ledger view and a recorded split on the escrow page. */
describe("EscrowDetailPage legs and ledger (ESC-24)", () => {
  const ledgerEscrow = {
    escrowId: "escrow-order-ord-2",
    status: "released",
    amount: 17250,
    farmerPayout: 15000,
    truckerPayout: 1500,
    commission: 750,
    truckerId: "trucker-1",
    truckerName: "Dawit Tesfaye",
    legStatus: { crop: "released", transport: "released", commission: "kept" },
    releasedTotal: 16500,
    refundedTotal: 0,
    truckerLeg: { state: "released", pending: false, inManualQueue: false },
  };

  function renderPage() {
    return render(
      <MemoryRouter>
        <EscrowDetailPage />
      </MemoryRouter>,
    );
  }

  beforeEach(() => {
    mocks.audit.data = { auditEntries: [] };
    mocks.ledger.data = undefined;
    mocks.ledger.isLoading = false;
    mocks.ledger.isError = false;
    mocks.ledger.hasNextPage = false;
    mocks.ledgerCalls.length = 0;
  });

  it("shows the trucker, the trucker payout and the ledger rows of a ledger escrow", () => {
    mocks.escrow.data = { escrow: ledgerEscrow };
    mocks.ledger.data = {
      pages: [
        {
          escrowId: "escrow-order-ord-2",
          settlementModel: "ledger",
          rows: [
            {
              ref: "release#farmer", kind: "release", legs: ["crop"], payeeRole: "farmer", payeeId: "farmer-1",
              payeeName: "Tolosa Gemechu", amount: 15000, status: "done", gatewayRef: "WCLAAA", telebirrRef: "TB-F-1",
              attempts: 1, reason: "auto_release", actor: "system", createdAt: "2026-11-05T10:00:00Z",
              updatedAt: "2026-11-05T10:05:00Z", sentAt: "2026-11-05T10:01:00Z", doneAt: "2026-11-05T10:05:00Z",
            },
            {
              ref: "release#trucker#trucker-1", kind: "release", legs: ["transport"], payeeRole: "trucker",
              payeeId: "trucker-1", amount: 1500, status: "manual", gatewayRef: "WCLBBB", manualReason: "PAYEE_NOT_VERIFIED",
              attempts: 0, reason: "auto_release", actor: "system", createdAt: "2026-11-05T10:00:00Z",
              updatedAt: "2026-11-05T10:00:00Z",
            },
          ],
        },
      ],
    };

    renderPage();

    expect(screen.getByText("Dawit Tesfaye")).toBeTruthy();
    expect(screen.getByText("Trucker payout").nextElementSibling?.textContent).toBe("1,500.00 Birr");
    expect(screen.getByText("Decided for the trucker (see the ledger)")).toBeTruthy();
    expect(mocks.ledgerCalls[0]).toEqual({ escrowId: "escrow-order-ord-2", enabled: true });

    const legs = screen.getByRole("list", { name: "Legs of the amount" });
    expect(within(legs).getByText("Kept")).toBeTruthy();
    const table = screen.getByRole("table", { name: "Ledger" });
    const rows = within(table).getAllByRole("row");
    expect(rows).toHaveLength(3);
    expect(within(rows[1]).getByText("release#farmer")).toBeTruthy();
    expect(within(rows[1]).getByText("15,000.00 Birr")).toBeTruthy();
    expect(within(rows[1]).getByText("Done")).toBeTruthy();
    expect(within(rows[1]).getByText("TB-F-1")).toBeTruthy();
    expect(within(rows[1]).getByText("Tolosa Gemechu")).toBeTruthy();
    expect(within(rows[2]).getByText("In Manual Payouts")).toBeTruthy();
    expect(within(rows[2]).getByText("Payee is not verified")).toBeTruthy();
    // Read only: the ledger offers no action.
    expect(within(table).queryByRole("button")).toBeNull();
  });

  it("a legacy escrow shows its fields as before, without a ledger card or a ledger read", () => {
    mocks.escrow.data = { escrow: { escrowId: "escrow-old", status: "escrowed", amount: 1000, farmerPayout: 950, commission: 50 } };

    renderPage();

    expect(screen.queryByRole("heading", { name: "Ledger" })).toBeNull();
    expect(mocks.ledgerCalls.every((call) => !call.enabled)).toBe(true);
    expect(screen.queryByText("Transport leg")).toBeNull();
    expect(screen.getByText("Farmer payout").nextElementSibling?.textContent).toBe("950.00 Birr");
  });

  it("a trucker leg owed and blocked says why, and points at Manual Payouts as the only place to record it", () => {
    mocks.escrow.data = {
      escrow: {
        escrowId: "escrow-old", status: "released", amount: 17250, farmerPayout: 15000, truckerPayout: 1500, truckerId: "trucker-1",
        truckerLeg: { state: "blocked", pending: true, inManualQueue: false, blockReason: "phone_unverified", pendingSince: "2026-11-05T10:00:00Z" },
      },
    };

    renderPage();

    expect(screen.getByText("Owed, trucker cannot be paid yet")).toBeTruthy();
    expect(screen.getByText("The trucker's phone is not verified")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Manual Payouts" }).getAttribute("href")).toBe("/payouts");
    // No action on this page pays or marks the leg.
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("shows a recorded split and a failed ledger read", () => {
    mocks.escrow.data = {
      escrow: { ...ledgerEscrow, verdictAmounts: { farmerAmount: 9000, truckerAmount: 1500, buyerRefund: 6000 } },
    };
    mocks.ledger.isError = true;

    renderPage();

    expect(
      screen.getByText("Farmer receives 9,000.00 Birr · Trucker receives 1,500.00 Birr · Buyer is refunded 6,000.00 Birr"),
    ).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toBe("The ledger could not be loaded. Reload the page.");
  });
});
