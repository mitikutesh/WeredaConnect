import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { UnappliedPaymentsPage } from "./UnappliedPaymentsPage";

/* FUP-02 unapplied payments page. The hooks are mocked; the English strings are real, so
 * the tests also prove every unapplied.* key the page reads exists. */

type MutateOptions = {
  onSuccess?: (result: unknown) => void;
  onError?: (err: unknown) => void;
  onSettled?: () => void;
};

const mocks = vi.hoisted(() => ({
  payments: {
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
  useUnappliedPayments: () => mocks.payments,
  useRecordManualRefund: () => ({ mutate: mocks.mutate, isPending: false }),
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

const MERCH = "WCO0123456789ABCDEF01234567001";

function cancelledOrderPayment(overrides: Record<string, unknown> = {}) {
  return {
    orderId: "ord-1",
    escrowId: "escrow-order-ord-1",
    paymentOrderId: "TB-PAY-1",
    merchOrderId: MERCH,
    actions: ["order_payment_unmatched"],
    reasons: ["status_cancelled"],
    firstSeenAt: "2026-10-20T08:00:00.0000000Z",
    lastSeenAt: "2026-10-20T08:00:00.0000000Z",
    capturedAmount: 1500,
    currency: "ETB",
    orderStatus: "cancelled",
    buyerId: "buyer-1",
    mayStillApply: false,
    ...overrides,
  };
}

function namelessPayment() {
  return cancelledOrderPayment({
    orderId: "ord-2",
    escrowId: "escrow-order-ord-2",
    paymentOrderId: undefined,
    reasons: ["no_payment_order_id"],
  });
}

function stillPendingPayment() {
  return cancelledOrderPayment({
    orderId: "ord-3",
    escrowId: "escrow-order-ord-3",
    paymentOrderId: "TB-PAY-3",
    actions: ["order_payment_unprocessed"],
    reasons: ["TimeoutException"],
    capturedAmount: undefined,
    orderStatus: "payment_submitted",
    mayStillApply: true,
  });
}

/* useUnappliedPayments is an infinite query: the page reads data.pages. */
function pages(...list: Record<string, unknown>[]) {
  return { pages: list, pageParams: list.map(() => null) };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <UnappliedPaymentsPage />
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

async function fillRefund(row: HTMLElement, ref = "TB-REFUND-9", reason = "Order was cancelled before payment") {
  const user = userEvent.setup();
  await user.type(within(row).getByLabelText("Refund transaction ID"), ref);
  await user.type(within(row).getByLabelText("Reason, 10 to 500 characters"), reason);
  return user;
}

describe("UnappliedPaymentsPage", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
    mocks.payments.isLoading = false;
    mocks.payments.isError = false;
    mocks.payments.hasNextPage = false;
    mocks.payments.fetchNextPage = vi.fn();
    mocks.payments.data = pages({
      payments: [cancelledOrderPayment(), namelessPayment(), stillPendingPayment()],
      total: 3,
    });
  });

  it("lists each payment with its order, payment id, reason and captured amount", () => {
    renderPage();

    expect(screen.getByText("Unapplied Payments")).toBeTruthy();
    const row = rowFor("TB-PAY-1");
    expect(within(row).getByRole("link", { name: "ord-1" }).getAttribute("href")).toBe("/transactions/ord-1");
    expect(within(row).getByText("Cancelled")).toBeTruthy();
    expect(within(row).getByText("Did not match the order")).toBeTruthy();
    expect(within(row).getByText("The order was Cancelled")).toBeTruthy();
    expect(within(row).getByText("1,500.00 Birr")).toBeTruthy();
    expect((within(row).getByLabelText("Amount refunded (ETB)") as HTMLInputElement).value).toBe("1500");
  });

  it("keeps Record refund disabled until the reference and a 10 character reason are typed", async () => {
    renderPage();
    const row = rowFor("TB-PAY-1");
    const button = () => within(row).getByRole("button", { name: "Record refund" }) as HTMLButtonElement;
    expect(button().disabled).toBe(true);

    const user = userEvent.setup();
    await user.type(within(row).getByLabelText("Refund transaction ID"), "TB-REFUND-9");
    await user.type(within(row).getByLabelText("Reason, 10 to 500 characters"), "too short");
    expect(button().disabled).toBe(true);

    await user.type(within(row).getByLabelText("Reason, 10 to 500 characters"), " still");
    expect(button().disabled).toBe(false);
  });

  it("asks for confirmation, then posts the order, payment, reference, amount and reason", async () => {
    renderPage();
    const row = rowFor("TB-PAY-1");
    const user = await fillRefund(row, " TB-REFUND-9 ");
    await user.click(within(row).getByRole("button", { name: "Record refund" }));

    expect(
      within(row).getByText("You refunded 1,500.00 Birr for payment TB-PAY-1 in the portal, under TB-REFUND-9?"),
    ).toBeTruthy();
    expect(mocks.mutate).not.toHaveBeenCalled();

    await user.click(within(row).getByRole("button", { name: "Yes, record it" }));

    expect(mocks.mutate).toHaveBeenCalledTimes(1);
    expect(mocks.mutate.mock.calls[0][0]).toEqual({
      orderId: "ord-1",
      body: {
        paymentOrderId: "TB-PAY-1",
        telebirrRef: "TB-REFUND-9",
        amount: 1500,
        reason: "Order was cancelled before payment",
      },
    });
  });

  it("cancelling the confirmation posts nothing", async () => {
    renderPage();
    const row = rowFor("TB-PAY-1");
    const user = await fillRefund(row);
    await user.click(within(row).getByRole("button", { name: "Record refund" }));
    await user.click(within(row).getByRole("button", { name: "Cancel" }));

    expect(mocks.mutate).not.toHaveBeenCalled();
    expect(within(row).getByRole("button", { name: "Record refund" })).toBeTruthy();
  });

  it("asks for the portal's payment order id for a payment the webhook could not name, and sends its checkout", async () => {
    renderPage();
    const row = rowFor(`The payment order ID is not known. Search the portal for checkout ${MERCH}.`);
    const user = await fillRefund(row);
    expect((within(row).getByRole("button", { name: "Record refund" }) as HTMLButtonElement).disabled).toBe(true);

    await user.type(within(row).getByLabelText("Payment order ID (from the portal)"), "TB-PAY-PORTAL");
    await user.click(within(row).getByRole("button", { name: "Record refund" }));
    await user.click(within(row).getByRole("button", { name: "Yes, record it" }));

    expect(mocks.mutate.mock.calls[0][0]).toEqual({
      orderId: "ord-2",
      body: {
        paymentOrderId: "TB-PAY-PORTAL",
        telebirrRef: "TB-REFUND-9",
        amount: 1500,
        reason: "Order was cancelled before payment",
        merchOrderId: MERCH,
      },
    });
  });

  it("offers no form for a payment Telebirr may still deliver to its order", () => {
    renderPage();
    const row = rowFor("TB-PAY-3");

    expect(
      within(row).getByText("Telebirr may still deliver this payment to its order. Do not refund it yet."),
    ).toBeTruthy();
    expect(within(row).getByText("Not on record. Read the amount in the portal.")).toBeTruthy();
    expect(within(row).getByText("Could not be processed")).toBeTruthy();
    expect(within(row).queryByRole("button", { name: "Record refund" })).toBeNull();
    expect(within(row).queryByLabelText("Refund transaction ID")).toBeNull();
  });

  it("labels every reason code the webhook writes, and an unknown one as a processing failure", () => {
    mocks.payments.data = pages({
      payments: [
        cancelledOrderPayment({ paymentOrderId: "TB-A", reasons: ["no_payment_order_id", "order_missing"] }),
        cancelledOrderPayment({ paymentOrderId: "TB-B", reasons: ["already_paid", "currency", "amount", "payer"] }),
        cancelledOrderPayment({ paymentOrderId: "TB-C", reasons: ["order_changed", "stale_checkout", "stale_notify_time"] }),
        cancelledOrderPayment({ paymentOrderId: "TB-D", reasons: ["replay_exhausted:TimeoutException", "TimeoutException"] }),
      ],
    });
    renderPage();

    expect(within(rowFor("TB-A")).getByText("Telebirr sent no payment order ID, The order does not exist")).toBeTruthy();
    expect(
      within(rowFor("TB-B")).getByText(
        "The order was already paid, Paid in another currency, Paid a different amount, Paid from another phone",
      ),
    ).toBeTruthy();
    expect(
      within(rowFor("TB-C")).getByText(
        "The order changed after checkout, Paid on an earlier checkout, The notice arrived too late",
      ),
    ).toBeTruthy();
    expect(
      within(rowFor("TB-D")).getByText(
        "Processing gave up after repeated tries, Processing failed (TimeoutException)",
      ),
    ).toBeTruthy();
  });

  it("says a reference conflict is about this order", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => {
      opts?.onError?.({ code: "ESCROW_036" });
      opts?.onSettled?.();
    });
    renderPage();
    const row = rowFor("TB-PAY-1");
    const user = await fillRefund(row);
    await user.click(within(row).getByRole("button", { name: "Record refund" }));
    await user.click(within(row).getByRole("button", { name: "Yes, record it" }));

    expect(screen.getByRole("alert").textContent).toBe(
      "This reference conflicts with one already recorded for this order. Reload and check.",
    );
  });

  it("shows the localized message for a server error code", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => {
      opts?.onError?.({ code: "ESCROW_043" });
      opts?.onSettled?.();
    });
    renderPage();
    const row = rowFor("TB-PAY-1");
    const user = await fillRefund(row);
    await user.click(within(row).getByRole("button", { name: "Record refund" }));
    await user.click(within(row).getByRole("button", { name: "Yes, record it" }));

    expect(screen.getByRole("alert").textContent).toBe(
      "The amount differs from what Telebirr captured for this payment.",
    );
  });

  it.each([
    [{ alreadyRecorded: false, amountChecked: true }, "Refund recorded."],
    [
      { alreadyRecorded: false, amountChecked: false },
      "Refund recorded. The captured amount was not on record, so the amount was not checked.",
    ],
    [{ alreadyRecorded: true, amountChecked: true }, "This refund was already recorded."],
  ])("tells the admin what was recorded (%o)", async (result, message) => {
    mocks.mutate.mockImplementation((_vars, opts) => {
      opts?.onSuccess?.(result);
      opts?.onSettled?.();
    });
    renderPage();
    const row = rowFor("TB-PAY-1");
    const user = await fillRefund(row);
    await user.click(within(row).getByRole("button", { name: "Record refund" }));
    await user.click(within(row).getByRole("button", { name: "Yes, record it" }));

    expect(screen.getByRole("status").textContent).toBe(message);
  });

  it("shows a load failure instead of an empty list", () => {
    mocks.payments.isError = true;
    renderPage();

    expect(screen.getByRole("alert").textContent).toBe(
      "The unapplied payments could not be loaded. Reload before refunding anything.",
    );
    expect(screen.queryByText("No captured payment is waiting for a refund.")).toBeNull();
  });

  it("shows the empty state when nothing is waiting", () => {
    mocks.payments.data = pages({ payments: [], total: 0 });
    renderPage();

    expect(screen.getByText("No captured payment is waiting for a refund.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  });

  it("says the search is not finished when an empty page has more after it, and loads the next page", async () => {
    mocks.payments.data = pages({ payments: [], total: 0, nextToken: "t1" });
    mocks.payments.hasNextPage = true;
    renderPage();

    expect(screen.getByText("None found in this part of the history. Load more to keep searching.")).toBeTruthy();
    await userEvent.setup().click(screen.getByRole("button", { name: "Load more" }));
    expect(mocks.payments.fetchNextPage).toHaveBeenCalledTimes(1);
  });

  it("shows every loaded page once", () => {
    mocks.payments.data = pages(
      { payments: [cancelledOrderPayment()], nextToken: "t1" },
      { payments: [cancelledOrderPayment(), stillPendingPayment()] },
    );
    renderPage();

    const table = screen.getAllByRole("table")[0];
    expect(within(table).getAllByText("TB-PAY-1")).toHaveLength(1);
    expect(within(table).getByText("TB-PAY-3")).toBeTruthy();
  });
});
