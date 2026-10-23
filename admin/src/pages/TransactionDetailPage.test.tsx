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
}));

vi.mock("@/hooks/useQuery", () => ({
  useTransactionDetail: () => mocks.detail,
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
