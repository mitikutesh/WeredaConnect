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
  correct: vi.fn(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useEscrowDetail: () => mocks.escrow,
  useEscrowAudit: () => mocks.audit,
  useEscrowLedger: (escrowId: string, enabled: boolean) => {
    mocks.ledgerCalls.push({ escrowId, enabled });
    return mocks.ledger;
  },
  useCorrectPayoutReference: () => ({ mutate: mocks.correct, isPending: false }),
  useRefundDoubleEscrow: () => ({ mutate: mocks.correct, isPending: false }),
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

/* FUP-218: where a reference recorded by mistake is corrected: a legacy escrow's manual payout legs, and a
 * ledger row an admin recorded. Never a leg the gateway paid or one not recorded yet. */
describe("EscrowDetailPage reference correction (FUP-218)", () => {
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
    mocks.ledger.isError = false;
    mocks.ledgerCalls.length = 0;
  });

  it("lists a legacy escrow's manual payout legs and offers the correction on the admin-recorded one only", () => {
    mocks.escrow.data = {
      escrow: {
        escrowId: "escrow-old",
        status: "released",
        amount: 17250,
        manualLegs: [
          { ref: "farmer", status: "paid", externalRef: "GW-FARMER-1", paidBy: "gateway", paidAt: "2026-11-05T10:00:00Z" },
          { ref: "trucker", status: "paid", externalRef: "TB-WRONG-1", paidBy: "admin-2", paidAt: "2026-11-05T11:00:00Z" },
        ],
      },
    };

    renderPage();

    const table = screen.getByRole("table", { name: "Manual payouts" });
    const rows = within(table).getAllByRole("row");
    expect(rows).toHaveLength(3);
    expect(within(rows[1]).getByText("GW-FARMER-1")).toBeTruthy();
    expect(within(rows[1]).getByText("Paid by the gateway")).toBeTruthy();
    expect(within(rows[1]).queryByRole("button")).toBeNull();
    expect(within(rows[2]).getByText("TB-WRONG-1")).toBeTruthy();
    expect(within(rows[2]).getByText("admin-2")).toBeTruthy();
    expect(within(rows[2]).getByRole("button", { name: "Correct reference" })).toBeTruthy();
  });

  it("offers nothing on a pending leg, and shows no card for an escrow that never entered the queue", () => {
    mocks.escrow.data = {
      escrow: { escrowId: "escrow-old", status: "escrowed", amount: 1000, manualLegs: [{ ref: "buyer", status: "pending" }] },
    };
    const { unmount } = renderPage();
    expect(within(screen.getByRole("table", { name: "Manual payouts" })).queryByRole("button")).toBeNull();
    unmount();

    mocks.escrow.data = { escrow: { escrowId: "escrow-old", status: "escrowed", amount: 1000 } };
    renderPage();
    expect(screen.queryByRole("table", { name: "Manual payouts" })).toBeNull();
  });

  it("offers the correction on a ledger row an admin recorded, not on one the gateway paid", () => {
    mocks.escrow.data = {
      escrow: {
        escrowId: "escrow-order-ord-2",
        status: "completed",
        amount: 17250,
        legStatus: { crop: "released", transport: "released", commission: "kept" },
      },
    };
    const row = {
      kind: "release", legs: ["crop"], amount: 15000, status: "done", attempts: 1, reason: "auto_release",
      actor: "system", createdAt: "2026-11-05T10:00:00Z", updatedAt: "2026-11-05T10:05:00Z",
    };
    mocks.ledger.data = {
      pages: [
        {
          escrowId: "escrow-order-ord-2",
          settlementModel: "ledger",
          rows: [
            { ...row, ref: "release#farmer", payeeRole: "farmer", gatewayRef: "WCLA", telebirrRef: "TB-WRONG-2", paidBy: "admin-2" },
            { ...row, ref: "release#trucker#t1", payeeRole: "trucker", gatewayRef: "WCLB", telebirrRef: "GW-T-1", paidBy: "gateway" },
          ],
        },
      ],
    };

    renderPage();

    const rows = within(screen.getByRole("table", { name: "Ledger" })).getAllByRole("row");
    expect(within(rows[1]).getByRole("button", { name: "Correct reference" })).toBeTruthy();
    expect(within(rows[2]).queryByRole("button")).toBeNull();
  });
});

/* FUP-41: the sweep's block reason, and the refund of a second payment for the order. */
describe("EscrowDetailPage sweep block and double escrow (FUP-41)", () => {
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
  });

  it("shows why the sweep leaves the escrow alone, and since when", () => {
    mocks.escrow.data = {
      escrow: {
        escrowId: "escrow-order-ord-7",
        status: "escrowed",
        amount: 20000,
        settlementBlockReason: "release:payout:phone_unverified",
        settlementBlockReasonAt: "2026-11-12T08:00:00Z",
      },
    };

    renderPage();

    expect(screen.getByText("Sweep block")).toBeTruthy();
    expect(screen.getByText("release:payout:phone_unverified")).toBeTruthy();
    expect(screen.getByText(/^since /)).toBeTruthy();
    expect(screen.queryByText("Second payment for this order")).toBeNull();
  });

  it("offers the double escrow refund only on an escrowed escrow the server marked as a second payment", () => {
    mocks.escrow.data = {
      escrow: {
        escrowId: "escrow-del-41",
        orderId: "ord-41",
        status: "escrowed",
        amount: 1500,
        doubleEscrow: true,
        orderEscrowStatus: "completed",
        settlementBlockReason: "release:trade:order_settled",
      },
    };
    const { unmount } = renderPage();
    expect(screen.getByText("Second payment for this order")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Refund this payment to the buyer" })).toBeTruthy();
    unmount();

    mocks.escrow.data = { escrow: { escrowId: "escrow-del-42", orderId: "ord-42", status: "escrowed", amount: 1500, doubleEscrow: false } };
    renderPage();
    expect(screen.queryByText("Second payment for this order")).toBeNull();
    expect(screen.queryByText("Sweep block")).toBeNull();
  });
});
