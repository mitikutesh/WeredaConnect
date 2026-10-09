import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ManualPayoutsPage } from "./ManualPayoutsPage";

/* ESC-02 manual payout queue page. The hooks are mocked; the English strings are
 * real so the tests also prove every payouts.* key exists. */

type MutateOptions = {
  onSuccess?: () => void;
  onError?: (err: unknown) => void;
  onSettled?: () => void;
};

const mocks = vi.hoisted(() => ({
  payouts: {
    data: undefined as unknown,
    isLoading: false,
    isError: false,
    hasNextPage: false,
    isFetchingNextPage: false,
    fetchNextPage: vi.fn(),
  },
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useManualPayouts: () => mocks.payouts,
  useCompleteManualPayout: () => ({ mutate: mocks.mutate, isPending: false }),
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

const hoursAgo = (h: number) => new Date(Date.now() - h * 3_600_000).toISOString();

function releaseWithTwoLegs(overrides: Record<string, unknown> = {}) {
  return {
    escrowId: "escrow-order-ord-1",
    orderId: "ord-1",
    op: "release",
    reason: "PAYOUT_NOT_SUPPORTED",
    escrowStatus: "escrowed",
    fromStatus: "escrowed",
    queuedAt: hoursAgo(2),
    currency: "ETB",
    pendingAmount: 19500,
    legs: [
      {
        ref: "farmer",
        payeeRole: "farmer",
        payeeId: "farmer-1",
        payeeName: "Abebe Kebede",
        payeePhone: "+251911000001",
        payeeVerified: true,
        amount: 16500,
        status: "pending",
      },
      {
        ref: "trucker",
        payeeRole: "trucker",
        payeeId: "trucker-1",
        payeeName: "Chala Tolosa",
        payeePhone: "+251911000002",
        payeeVerified: false,
        blockReason: "Payout blocked: the trucker's phone number is not verified.",
        amount: 3000,
        status: "pending",
      },
    ],
    ...overrides,
  };
}

function refundOverdue() {
  return {
    escrowId: "escrow-order-ord-2",
    orderId: "ord-2",
    op: "refund",
    reason: "GATEWAY_REF_MISSING",
    escrowStatus: "disputed",
    fromStatus: "escrowed",
    queuedAt: hoursAgo(30),
    currency: "ETB",
    pendingAmount: 20000,
    legs: [
      {
        ref: "buyer",
        payeeRole: "buyer",
        payeeId: "buyer-1",
        payeeName: "Sara Bekele",
        payeePhone: "+251911000003",
        payeeVerified: true,
        amount: 20000,
        status: "pending",
      },
    ],
  };
}

/* useManualPayouts is an infinite query: the page reads data.pages. */
function pages(...list: Record<string, unknown>[]) {
  return { pages: list, pageParams: list.map(() => null) };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <ManualPayoutsPage />
    </MemoryRouter>,
  );
}

function rowFor(text: string): HTMLElement {
  // Desktop table rows (the mobile card view renders the same content; scope to the table).
  const table = screen.getAllByRole("table")[0];
  const row = within(table).getByText(text).closest("tr");
  if (!row) throw new Error(`no table row containing ${text}`);
  return row;
}

describe("ManualPayoutsPage", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
    mocks.payouts.isLoading = false;
    mocks.payouts.isError = false;
    mocks.payouts.hasNextPage = false;
    mocks.payouts.fetchNextPage = vi.fn();
    mocks.payouts.data = pages({ payouts: [releaseWithTwoLegs(), refundOverdue()], total: 2 });
  });

  it("renders one row per leg with payee, amount, operation and reason", () => {
    renderPage();

    expect(screen.getByText("Manual Payouts")).toBeTruthy();
    const farmerRow = rowFor("Abebe Kebede");
    expect(within(farmerRow).getByText("16,500.00 Birr")).toBeTruthy();
    expect(within(farmerRow).getByText("+251911000001")).toBeTruthy();
    expect(within(farmerRow).getByText("Release")).toBeTruthy();
    expect(within(farmerRow).getByText("Gateway has no payout API")).toBeTruthy();

    const buyerRow = rowFor("Sara Bekele");
    expect(within(buyerRow).getByText("20,000.00 Birr")).toBeTruthy();
    expect(within(buyerRow).getByText("Refund")).toBeTruthy();
    expect(within(buyerRow).getByText("Gateway returned no reference")).toBeTruthy();
    // Total still waiting across the queue.
    expect(screen.getByText("39,500.00 Birr")).toBeTruthy();
  });

  it("flags a payout older than 24 hours and a status that changed since queueing", () => {
    renderPage();

    const buyerRow = rowFor("Sara Bekele");
    expect(within(buyerRow).getByText("Over 24 h")).toBeTruthy();
    expect(within(buyerRow).getByText("Status changed since queueing")).toBeTruthy();

    const farmerRow = rowFor("Abebe Kebede");
    expect(within(farmerRow).queryByText("Over 24 h")).toBeNull();
    expect(within(farmerRow).queryByText("Status changed since queueing")).toBeNull();
  });

  it("keeps Mark as paid disabled until a reference is typed, and blocks an unverified payee", () => {
    renderPage();

    const farmerRow = rowFor("Abebe Kebede");
    expect(
      (within(farmerRow).getByRole("button", { name: "Mark as paid" }) as HTMLButtonElement).disabled,
    ).toBe(true);

    const truckerRow = rowFor("Chala Tolosa");
    expect(within(truckerRow).getByText("Payee cannot be paid yet")).toBeTruthy();
    expect(
      within(truckerRow).getByText("Payout blocked: the trucker's phone number is not verified."),
    ).toBeTruthy();
    expect(within(truckerRow).queryByRole("button", { name: "Mark as paid" })).toBeNull();
    expect(within(truckerRow).queryByLabelText("Telebirr transaction ID")).toBeNull();
  });

  it("asks for confirmation with amount and phone, then posts the escrow, leg and reference", async () => {
    const user = userEvent.setup();
    renderPage();

    const farmerRow = rowFor("Abebe Kebede");
    await user.type(within(farmerRow).getByLabelText("Telebirr transaction ID"), " TB2026100600001 ");
    await user.click(within(farmerRow).getByRole("button", { name: "Mark as paid" }));

    expect(within(farmerRow).getByText("You paid 16,500.00 Birr to +251911000001?")).toBeTruthy();
    expect(mocks.mutate).not.toHaveBeenCalled();

    await user.click(within(farmerRow).getByRole("button", { name: "Yes, recorded" }));

    expect(mocks.mutate).toHaveBeenCalledTimes(1);
    expect(mocks.mutate.mock.calls[0][0]).toEqual({
      escrowId: "escrow-order-ord-1",
      ref: "farmer",
      externalRef: "TB2026100600001",
    });
  });

  it("cancelling the confirmation posts nothing", async () => {
    const user = userEvent.setup();
    renderPage();

    const farmerRow = rowFor("Abebe Kebede");
    await user.type(within(farmerRow).getByLabelText("Telebirr transaction ID"), "TB2026100600001");
    await user.click(within(farmerRow).getByRole("button", { name: "Mark as paid" }));
    await user.click(within(farmerRow).getByRole("button", { name: "Cancel" }));

    expect(mocks.mutate).not.toHaveBeenCalled();
    expect(within(farmerRow).getByRole("button", { name: "Mark as paid" })).toBeTruthy();
  });

  it("shows the localized message for a server error code", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => {
      opts?.onError?.({ code: "ESCROW_036" });
      opts?.onSettled?.();
    });
    const user = userEvent.setup();
    renderPage();

    const farmerRow = rowFor("Abebe Kebede");
    await user.type(within(farmerRow).getByLabelText("Telebirr transaction ID"), "TB2026100600001");
    await user.click(within(farmerRow).getByRole("button", { name: "Mark as paid" }));
    await user.click(within(farmerRow).getByRole("button", { name: "Yes, recorded" }));

    expect(screen.getByRole("alert").textContent).toBe(
      "This reference conflicts with one already recorded for this escrow. Reload and check.",
    );
  });

  it("shows a paid leg with its reference and who recorded it", () => {
    mocks.payouts.data = pages({
      payouts: [
        releaseWithTwoLegs({
          legs: [
            {
              ref: "farmer",
              payeeRole: "farmer",
              payeeId: "farmer-1",
              payeeName: "Abebe Kebede",
              payeePhone: "+251911000001",
              payeeVerified: true,
              amount: 16500,
              status: "paid",
              externalRef: "GW-FARMER-1",
              paidBy: "gateway",
            },
            {
              ref: "trucker",
              payeeRole: "trucker",
              payeeId: "trucker-1",
              payeeName: "Chala Tolosa",
              payeePhone: "+251911000002",
              payeeVerified: true,
              amount: 3000,
              status: "pending",
            },
          ],
        }),
      ],
      total: 1,
    });
    renderPage();

    const farmerRow = rowFor("Abebe Kebede");
    expect(within(farmerRow).getByText("Paid")).toBeTruthy();
    expect(within(farmerRow).getByText("GW-FARMER-1")).toBeTruthy();
    expect(within(farmerRow).getByText("Recorded by: gateway")).toBeTruthy();
    expect(within(farmerRow).queryByRole("button", { name: "Mark as paid" })).toBeNull();
    const truckerRow = rowFor("Chala Tolosa");
    expect(within(truckerRow).getByRole("button", { name: "Mark as paid" })).toBeTruthy();
  });

  it("tells the admin to look up a gateway-reported payment instead of paying it again", async () => {
    const user = userEvent.setup();
    renderPage();

    const hint =
      "Gateway reported paid without a reference. Find it in the portal and record its ID; pay only if it is missing.";
    const buyerRow = rowFor("Sara Bekele");
    expect(within(buyerRow).getByText(hint)).toBeTruthy();
    expect(within(buyerRow).queryByRole("button", { name: "Mark as paid" })).toBeNull();

    // A PAYOUT_NOT_SUPPORTED row keeps the plain pay-and-record wording.
    const farmerRow = rowFor("Abebe Kebede");
    expect(within(farmerRow).queryByText(hint)).toBeNull();
    expect(within(farmerRow).getByRole("button", { name: "Mark as paid" })).toBeTruthy();

    await user.type(within(buyerRow).getByLabelText("Telebirr transaction ID"), "TB2026100600009");
    await user.click(within(buyerRow).getByRole("button", { name: "Record reference" }));

    expect(
      within(buyerRow).getByText("The portal shows 20,000.00 Birr paid to +251911000003 under this ID?"),
    ).toBeTruthy();
  });

  it("tells the admin to search the portal (and again later) for a payout whose outcome is unknown", async () => {
    // ESC-05: a timeout, a lost answer or a stale lock parks the leg as GATEWAY_OUTCOME_UNKNOWN.
    const unknown = {
      ...refundOverdue(),
      escrowId: "escrow-order-ord-4",
      orderId: "ord-4",
      reason: "GATEWAY_OUTCOME_UNKNOWN",
      escrowStatus: "escrowed",
      settlementLockedAt: hoursAgo(3),
      queuedAt: hoursAgo(2),
      legs: [{ ...refundOverdue().legs[0], payeeName: "Tigist Alemu", gatewayRef: "refund:escrow-order-ord-4" }],
    };
    mocks.payouts.data = pages({ payouts: [unknown, releaseWithTwoLegs()], total: 2 });
    const user = userEvent.setup();
    renderPage();

    const row = rowFor("Tigist Alemu");
    expect(within(row).getByText("Gateway outcome unknown")).toBeTruthy();
    expect(
      within(row).getByText(
        "Telebirr may already have paid this leg. Search the portal for refund:escrow-order-ord-4. If it is not there, wait and search again before paying: a payment can show up late. Record its ID if you find it; pay only if it is still missing.",
      ),
    ).toBeTruthy();
    expect(within(row).getByText("Gateway called 3 h ago")).toBeTruthy();
    expect(within(row).queryByRole("button", { name: "Mark as paid" })).toBeNull();

    await user.type(within(row).getByLabelText("Telebirr transaction ID"), "TB2026100900004");
    await user.click(within(row).getByRole("button", { name: "Record reference" }));
    expect(
      within(row).getByText("The portal shows 20,000.00 Birr paid to +251911000003 under this ID?"),
    ).toBeTruthy();

    // A plain PAYOUT_NOT_SUPPORTED row shows neither the hint nor the gateway age.
    const farmerRow = rowFor("Abebe Kebede");
    expect(within(farmerRow).queryByText(/Gateway called/)).toBeNull();
    expect(within(farmerRow).queryByText(/may already have paid/)).toBeNull();
  });

  it("leaves out the gateway age for a lock taken before ESC-05", () => {
    const legacy = {
      ...refundOverdue(),
      reason: "GATEWAY_OUTCOME_UNKNOWN",
      legs: [{ ...refundOverdue().legs[0], gatewayRef: "refund:escrow-order-ord-2" }],
    };
    mocks.payouts.data = pages({ payouts: [legacy], total: 1 });
    renderPage();

    const row = rowFor("Sara Bekele");
    expect(within(row).getByText(/Search the portal for refund:escrow-order-ord-2/)).toBeTruthy();
    expect(within(row).queryByText(/Gateway called/)).toBeNull();
  });

  it("shows a load failure instead of an empty queue", () => {
    mocks.payouts.isError = true;
    mocks.payouts.data = undefined;
    renderPage();

    expect(screen.getByRole("alert").textContent).toBe(
      "The payout queue could not be loaded. Reload before paying anything.",
    );
    expect(screen.queryByText("No payouts are waiting.")).toBeNull();
  });

  it("loads the next page on demand and shows every loaded page", async () => {
    mocks.payouts.data = pages(
      { payouts: [releaseWithTwoLegs()], nextToken: "next-page", total: 1 },
      { payouts: [refundOverdue()], total: 1 },
    );
    mocks.payouts.hasNextPage = true;
    const user = userEvent.setup();
    renderPage();

    expect(rowFor("Abebe Kebede")).toBeTruthy();
    expect(rowFor("Sara Bekele")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Load more" }));
    expect(mocks.payouts.fetchNextPage).toHaveBeenCalledTimes(1);
  });

  it("has no Load more button on the last page", () => {
    renderPage();

    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  });

  it("warns on a refund whose delivery was confirmed after it was queued", () => {
    const refund = { ...refundOverdue(), deliveryConfirmedAt: hoursAgo(1) };
    const earlier = {
      ...refundOverdue(),
      escrowId: "escrow-order-ord-3",
      orderId: "ord-3",
      deliveryConfirmedAt: hoursAgo(40),
      legs: [{ ...refundOverdue().legs[0], payeeName: "Hana Girma" }],
    };
    mocks.payouts.data = pages({ payouts: [refund, earlier], total: 2 });
    renderPage();

    const warning =
      "Delivery was confirmed after this refund was queued. Check with the buyer and the farmer before refunding.";
    expect(within(rowFor("Sara Bekele")).getByText(warning)).toBeTruthy();
    // Confirmed before the refund was queued (for example a buyer_wins verdict): no warning.
    expect(within(rowFor("Hana Girma")).queryByText(warning)).toBeNull();
  });

  it("shows the empty state when nothing is waiting", () => {
    mocks.payouts.data = pages({ payouts: [], total: 0 });
    renderPage();

    expect(screen.getByText("No payouts are waiting.")).toBeTruthy();
  });
});
