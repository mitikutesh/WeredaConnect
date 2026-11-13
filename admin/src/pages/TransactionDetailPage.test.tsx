import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { Locale } from "@/lib/i18n";

import { TransactionDetailPage } from "./TransactionDetailPage";

/* FUP-35: the transaction timeline names every audit action in words. The escrow-audit
 * refusal rows (ESC-07) used to show their raw snake_case action. */

const mocks = vi.hoisted(() => ({
  detail: { data: undefined as unknown, isLoading: false },
  locale: "en" as Locale,
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
  useTransactionDetail: () => mocks.detail,
  useEscrowLedger: (escrowId: string, enabled: boolean) => {
    mocks.ledgerCalls.push({ escrowId, enabled });
    return mocks.ledger;
  },
  useCancelDelivery: () => ({ mutate: vi.fn(), isPending: false }),
}));

vi.mock("@/hooks/useLocale", async () => {
  const { t } = await import("@/lib/i18n");
  return {
    useLocale: () => ({
      locale: mocks.locale,
      setLocale: vi.fn(),
      t: (key: string) => t(key, mocks.locale),
    }),
  };
});

function detailWithTimeline(timeline: Record<string, unknown>[]) {
  return {
    order: { orderId: "ord-1", status: "in_transit", buyerId: "buyer-1", farmerId: "farmer-1" },
    escrow: null,
    delivery: null,
    timeline,
  };
}

const refusalRow = {
  timestamp: "2026-10-20T08:00:00Z",
  entity: "escrow",
  action: "dispute_refused_not_held",
  fromState: "initiated",
  toState: "initiated",
  actor: "buyer-1",
  detail: "deliveryId=del-1",
};

function renderPage() {
  return render(
    <MemoryRouter>
      <TransactionDetailPage />
    </MemoryRouter>,
  );
}

describe("TransactionDetailPage timeline labels (FUP-35)", () => {
  beforeEach(() => {
    mocks.locale = "en";
  });

  it("labels a dispute refusal row instead of showing the raw action", () => {
    mocks.detail.data = detailWithTimeline([
      refusalRow,
      { ...refusalRow, action: "dispute_refused_other_delivery", detail: "deliveryId=del-2" },
    ]);

    renderPage();

    expect(screen.getByText("Dispute refused (money not held)")).toBeTruthy();
    expect(screen.getByText("Dispute refused (another delivery)")).toBeTruthy();
    expect(screen.queryByText("dispute_refused_not_held")).toBeNull();
    expect(screen.queryByText("dispute_refused_other_delivery")).toBeNull();
  });

  it("uses the admin's language", () => {
    mocks.locale = "am";
    mocks.detail.data = detailWithTimeline([refusalRow]);

    renderPage();

    expect(screen.getByText("ክርክሩ ውድቅ ሆኗል (ገንዘቡ አልተያዘም)")).toBeTruthy();
  });

  it("reads an action it has no label for as words", () => {
    mocks.detail.data = detailWithTimeline([{ ...refusalRow, action: "some_future_action" }]);

    renderPage();

    expect(screen.getByText("Some future action")).toBeTruthy();
    expect(screen.queryByText("some_future_action")).toBeNull();
  });
});

function detailWithEscrow(escrow: Record<string, unknown>) {
  return {
    order: {
      orderId: "ord-1",
      status: "in_transit",
      buyerId: "buyer-1",
      farmerId: "farmer-1",
      paymentMethod: "telebirr",
    },
    escrow,
    delivery: null,
    timeline: [],
  };
}

/* FUP-112: the escrow block shows the expected payout date (releaseNotBefore) when the
 * server returns one, and omits the row when it does not. */
describe("TransactionDetailPage escrow payout date (FUP-112)", () => {
  beforeEach(() => {
    mocks.locale = "en";
  });

  it("shows the expected payout date when releaseNotBefore is set", () => {
    mocks.detail.data = detailWithEscrow({
      status: "escrowed",
      amount: 1000,
      commission: 50,
      farmerPayout: 950,
      releaseNotBefore: "2026-10-26T09:00:00Z",
    });

    renderPage();

    expect(screen.getByText("Expected payout")).toBeTruthy();
  });

  it("hides the expected payout row when releaseNotBefore is absent", () => {
    mocks.detail.data = detailWithEscrow({
      status: "escrowed",
      amount: 1000,
      commission: 50,
      farmerPayout: 950,
    });

    renderPage();

    expect(screen.queryByText("Expected payout")).toBeNull();
  });
});

/* ACC-05: an order whose buyer was deleted. ACC-02 wrote the "Deleted user" sentinel onto the
 * order's buyerName and stripped the buyer's phone from the delivery, so the admin shows the
 * anonymized name and simply omits the missing phone, without a runtime error. */
function detailWithDeletedBuyer() {
  return {
    order: {
      orderId: "ord-1",
      status: "in_transit",
      buyerName: "Deleted user",
      buyerId: "buyer-1",
      farmerName: "Abebe Kebede",
      farmerId: "farmer-1",
    },
    escrow: null,
    delivery: { farmerPhone: "+251900100001" },
    timeline: [],
  };
}

describe("TransactionDetailPage deleted buyer (ACC-05)", () => {
  beforeEach(() => {
    mocks.locale = "en";
  });

  it("shows the buyer as 'Deleted user' and omits the missing phone", () => {
    mocks.detail.data = detailWithDeletedBuyer();

    expect(() => renderPage()).not.toThrow();

    expect(screen.getByText("Deleted user")).toBeTruthy();
    expect(screen.getByText("Abebe Kebede")).toBeTruthy();
    // The buyer phone row is hidden because the delivery no longer carries it.
    expect(screen.queryByText("Buyer phone")).toBeNull();
  });

  it("localizes the deleted-buyer label for the admin's language", () => {
    mocks.locale = "am";
    mocks.detail.data = detailWithDeletedBuyer();

    renderPage();

    expect(screen.getByText("የተሰረዘ ተጠቃሚ")).toBeTruthy();
  });
});

/* FRD-12: the delivery card shows the handoff photos (presigned GETs the server signs for the
 * admin view) as thumbnails that open full size; a leg without a photo shows no row. */
describe("TransactionDetailPage handoff photos (FRD-12)", () => {
  beforeEach(() => {
    mocks.locale = "en";
  });

  function detailWithPhotos(delivery: Record<string, unknown>) {
    return {
      order: { orderId: "ord-1", status: "disputed", buyerId: "buyer-1", farmerId: "farmer-1" },
      escrow: null,
      delivery: { deliveryId: "del-1", status: "disputed", ...delivery },
      timeline: [],
    };
  }

  it("shows each photo as a thumbnail that opens it full size", () => {
    mocks.detail.data = detailWithPhotos({
      pickupPhotoUrl: "https://uploads.s3.test/pickup?sig=1",
      dropoffPhotoUrl: "https://uploads.s3.test/dropoff?sig=2",
    });

    renderPage();

    const pickup = screen.getByRole("img", { name: "Pickup photo" });
    expect(pickup.getAttribute("src")).toBe("https://uploads.s3.test/pickup?sig=1");
    const open = screen.getByRole("link", { name: "Open the Drop-off photo full size" });
    expect(open.getAttribute("href")).toBe("https://uploads.s3.test/dropoff?sig=2");
    expect(open.getAttribute("rel")).toContain("noopener");
  });

  it("shows no photo row for a leg without one", () => {
    mocks.detail.data = detailWithPhotos({ pickupPhotoUrl: "https://uploads.s3.test/pickup?sig=1" });

    renderPage();

    expect(screen.getByRole("img", { name: "Pickup photo" })).toBeTruthy();
    expect(screen.queryByText("Drop-off photo")).toBeNull();
  });

  it("names the photos in the admin's language", () => {
    mocks.locale = "om";
    mocks.detail.data = detailWithPhotos({ dropoffPhotoUrl: "https://uploads.s3.test/dropoff?sig=2" });

    renderPage();

    expect(screen.getByRole("img", { name: "Suuraa geejjibaa" })).toBeTruthy();
  });
});

/* FUP-19: the delivery card offers the cancel of a delivery picked up and never delivered once its money is decided. */
describe("TransactionDetailPage delivery cancel (FUP-19)", () => {
  beforeEach(() => {
    mocks.locale = "en";
  });

  function detail(deliveryStatus: string, escrowStatus: string | null) {
    return {
      order: { orderId: "ord-1", status: "in_transit", buyerId: "buyer-1", farmerId: "farmer-1" },
      escrow: escrowStatus ? { escrowId: "escrow-order-ord-1", status: escrowStatus, amount: 1000 } : null,
      delivery: { deliveryId: "del-1", status: deliveryStatus, version: 3 },
      timeline: [],
    };
  }

  it("offers the cancel on a delivery on the road whose payment was refunded", () => {
    mocks.detail.data = detail("inTransit", "refunded");

    renderPage();

    expect(screen.getByRole("button", { name: "Cancel delivery" })).toBeTruthy();
  });

  it("says to decide the money first while the payment is held", () => {
    mocks.detail.data = detail("pickedUp", "escrowed");

    renderPage();

    expect(screen.queryByRole("button", { name: "Cancel delivery" })).toBeNull();
    expect(screen.getByText(/The payment is still held/)).toBeTruthy();
  });

  it("offers nothing once the delivery is cancelled", () => {
    mocks.detail.data = detail("cancelled", "refunded");

    renderPage();

    expect(screen.queryByRole("button", { name: "Cancel delivery" })).toBeNull();
  });
});

/* ESC-24: the transaction page shows the transport leg and the ledger of the order's escrow. */
describe("TransactionDetailPage legs and ledger (ESC-24)", () => {
  beforeEach(() => {
    mocks.locale = "en";
    mocks.ledger.data = undefined;
    mocks.ledgerCalls.length = 0;
  });

  function detail(escrow: Record<string, unknown> | null) {
    return {
      order: { orderId: "ord-1", status: "disbursing", buyerId: "buyer-1", farmerId: "farmer-1", truckerId: "trucker-1" },
      escrow,
      delivery: null,
      timeline: [],
    };
  }

  it("shows the trucker payout, the leg state and the ledger of a ledger escrow", () => {
    mocks.detail.data = detail({
      escrowId: "escrow-order-ord-1", status: "released", amount: 17250, farmerPayout: 15000, truckerPayout: 1500, commission: 750,
      legStatus: { crop: "released", transport: "held", commission: "kept" }, releasedTotal: 15000, refundedTotal: 0,
      truckerLeg: { state: "held", pending: false, inManualQueue: false },
    });
    mocks.ledger.data = {
      pages: [{ escrowId: "escrow-order-ord-1", settlementModel: "ledger", rows: [
        { ref: "release#farmer", kind: "release", legs: ["crop"], payeeRole: "farmer", payeeId: "farmer-1", amount: 15000,
          status: "sent", gatewayRef: "WCLAAA", attempts: 1, reason: "auto_release", actor: "system",
          createdAt: "2026-11-05T10:00:00Z", updatedAt: "2026-11-05T10:01:00Z" },
      ] }],
    };

    renderPage();

    expect(screen.getByText("Trucker payout").nextElementSibling?.textContent).toBe("1,500.00 Birr");
    expect(screen.getByText("Transport leg")).toBeTruthy();
    expect(screen.getByRole("table", { name: "Ledger" })).toBeTruthy();
    expect(screen.getByText("Sent to gateway")).toBeTruthy();
    expect(mocks.ledgerCalls[0]).toEqual({ escrowId: "escrow-order-ord-1", enabled: true });
  });

  it("keeps the legacy fields for an escrow without a ledger, and shows no ledger without an escrow", () => {
    mocks.detail.data = detail({ escrowId: "escrow-old", status: "escrowed", amount: 1000, farmerPayout: 950, commission: 50 });
    const first = renderPage();
    expect(screen.getByText("Farmer payout")).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "Ledger" })).toBeNull();
    expect(mocks.ledgerCalls.every((call) => !call.enabled)).toBe(true);
    first.unmount();

    mocks.ledgerCalls.length = 0;
    mocks.detail.data = detail(null);
    renderPage();
    expect(mocks.ledgerCalls).toEqual([]);
  });
});
