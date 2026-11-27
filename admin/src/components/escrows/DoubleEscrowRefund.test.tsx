import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { DoubleEscrowRefund } from "./DoubleEscrowRefund";

/* FUP-41: the refund of a second payment for an order. The mutation hook is mocked; the English strings are real,
 * so the tests also prove every doubleEscrow.* key exists. */

type MutateOptions = {
  onSuccess?: (result: { status?: string; error?: { code?: string } }) => void;
  onError?: (err: unknown) => void;
};

const mocks = vi.hoisted(() => ({
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useRefundDoubleEscrow: () => ({ mutate: mocks.mutate, isPending: false }),
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

const REASON = "The buyer paid this delivery twice.";

const double = {
  escrowId: "escrow-del-41",
  orderId: "ord-41",
  status: "escrowed",
  amount: 1500,
  doubleEscrow: true,
  orderEscrowStatus: "released",
  settlementBlockReason: "release:trade:double_escrow",
};

async function openAndFill(user: ReturnType<typeof userEvent.setup>, reason = REASON) {
  await user.click(screen.getByRole("button", { name: "Refund this payment to the buyer" }));
  if (reason) await user.type(screen.getByLabelText("Why this is a second payment"), reason);
}

describe("DoubleEscrowRefund (FUP-41)", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
  });

  it("explains the double payment, asks for a reason, shows the amount before sending, then sends doubleEscrow", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ status: "refunded" }));
    const user = userEvent.setup();
    render(<DoubleEscrowRefund escrow={double} />);

    expect(screen.getByText("Second payment for this order")).toBeTruthy();
    expect(screen.getByText(/The order's own escrow \(Released\) already holds or paid/)).toBeTruthy();

    await openAndFill(user, `  ${REASON}  `);
    await user.click(screen.getByRole("button", { name: "Review" }));

    expect(screen.getByText(/Refund .*1,500.* to the buyer\?/)).toBeTruthy();
    expect(screen.getByText(`Why this is a second payment: ${REASON}`)).toBeTruthy();
    expect(mocks.mutate).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Yes, refund it" }));

    expect(mocks.mutate).toHaveBeenCalledTimes(1);
    const [vars] = mocks.mutate.mock.calls[0] as [{ escrowId: string; reason: string; idempotencyKey: string }];
    expect(vars.escrowId).toBe("escrow-del-41");
    expect(vars.reason).toBe(REASON);
    expect(vars.idempotencyKey).toMatch(/^admin:escrow-del-41:double_escrow_refund:\d+$/);
    expect(screen.getByRole("status").textContent).toBe("Refunded to the buyer.");
  });

  it("keeps one idempotency key for the opened form, so a second send is the same request", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onError?.({ code: "ESCROW_013" }));
    const user = userEvent.setup();
    render(<DoubleEscrowRefund escrow={double} />);

    await openAndFill(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, refund it" }));
    // The gateway refused: back on the form with the reason kept and the error said.
    expect(screen.getByRole("alert").textContent).toBe("Telebirr service error. Please try again.");
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, refund it" }));

    const keys = mocks.mutate.mock.calls.map((c) => (c[0] as { idempotencyKey: string }).idempotencyKey);
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it("says when the refund waits in Manual Payouts (202 ESCROW_034)", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onSuccess?.({ error: { code: "ESCROW_034" } }));
    const user = userEvent.setup();
    render(<DoubleEscrowRefund escrow={double} />);

    await openAndFill(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, refund it" }));

    expect(screen.getByRole("status").textContent).toMatch(/waits in Manual Payouts/);
  });

  it("names a refusal in the admin's language", async () => {
    mocks.mutate.mockImplementation((_vars, opts) => opts?.onError?.({ code: "ESCROW_049", message: "server text" }));
    const user = userEvent.setup();
    render(<DoubleEscrowRefund escrow={double} />);

    await openAndFill(user);
    await user.click(screen.getByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Yes, refund it" }));

    expect(screen.getByRole("alert").textContent).toMatch(/not a second payment/);
  });

  it("cannot review without a reason of 10 to 500 characters", async () => {
    const user = userEvent.setup();
    render(<DoubleEscrowRefund escrow={double} />);

    await openAndFill(user, "too short");
    expect((screen.getByRole("button", { name: "Review" }) as HTMLButtonElement).disabled).toBe(true);
    await user.type(screen.getByLabelText("Why this is a second payment"), " and more");
    expect((screen.getByRole("button", { name: "Review" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it.each([
    ["not a double payment", { ...double, doubleEscrow: false }],
    ["not computed", { ...double, doubleEscrow: undefined }],
    ["already refunded", { ...double, status: "refunded" }],
    ["held for review", { ...double, status: "held" }],
  ])("offers nothing when the escrow is %s", (_case, escrow) => {
    const { container } = render(<DoubleEscrowRefund escrow={escrow} />);
    expect(container.textContent).toBe("");
    expect(screen.queryByRole("button")).toBeNull();
  });
});
