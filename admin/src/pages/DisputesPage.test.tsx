import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { DisputesPage } from "./DisputesPage";

/* FUP-16: a verdict the order cannot follow as it is needs a disposition with a reason. The
 * hooks are mocked; the English strings are real so the tests also prove the keys exist. */

type MutateOptions = {
  onSuccess?: () => void;
  onError?: (err: unknown) => void;
  onSettled?: () => void;
};

const mocks = vi.hoisted(() => ({
  disputes: { data: undefined as unknown, isLoading: false },
  awaiting: { data: undefined as unknown, isLoading: false },
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useDisputes: () => mocks.disputes,
  useEscrows: () => mocks.awaiting,
  useResolveDispute: () => ({ mutate: mocks.mutate, isPending: false }),
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

const REASON = "Bags came back wet from the truck";

function openDispute() {
  return {
    escrowId: "escrow-1",
    buyerName: "Sara",
    farmerName: "Abebe",
    amount: 20000,
    status: "disputed",
    createdAt: "2026-10-20T10:00:00Z",
    dispute: { reason: "damaged_goods" },
  };
}

function recordedVerdict() {
  return {
    escrowId: "escrow-2",
    buyerName: "Almaz",
    farmerName: "Chala",
    amount: 9000,
    status: "dispute_resolved",
    dispute: { reason: "not_delivered", resolution: "farmer_wins", resolvedAt: "2026-10-19T10:00:00Z" },
  };
}

function table(): HTMLElement {
  // DataTable renders a desktop table and a mobile card list; scope to the table.
  return screen.getAllByRole("table")[0];
}

/** react-query calls onError, then onSettled. */
function failWith(code: string) {
  const { opts } = lastMutate();
  act(() => {
    opts.onError?.({ code });
    opts.onSettled?.();
  });
}

function lastMutate() {
  const [vars, opts] = mocks.mutate.mock.calls[mocks.mutate.mock.calls.length - 1];
  return { vars: vars as { id: string; body: Record<string, unknown> }, opts: opts ?? {} };
}

describe("DisputesPage", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
    mocks.disputes.data = { disputes: [openDispute()] };
    mocks.awaiting.data = { escrows: [recordedVerdict()] };
  });

  it("sends a plain verdict, and on ADMIN_015 offers the goods choices for buyer_wins", async () => {
    const user = userEvent.setup();
    render(<DisputesPage />);

    await user.click(within(table()).getByRole("button", { name: "Buyer Wins" }));

    const first = lastMutate();
    expect(first.vars).toEqual({ id: "escrow-1", body: { resolution: "buyer_wins", action: "refund" } });
    failWith("ADMIN_015");

    expect(screen.getByText("The order cannot follow this verdict as it is")).toBeTruthy();
    expect(screen.getByText(/The goods are already on the way/)).toBeTruthy();
    expect(screen.getByLabelText(/Return the goods to the farmer/)).toBeTruthy();
    expect(screen.getByLabelText(/The buyer keeps the goods/)).toBeTruthy();
    expect(screen.queryByLabelText(/Pay the farmer now/)).toBeNull();
    // ADMIN_015 is a question, not an error.
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("records the chosen disposition with a trimmed reason, and only with a valid reason", async () => {
    const user = userEvent.setup();
    render(<DisputesPage />);
    await user.click(within(table()).getByRole("button", { name: "Buyer Wins" }));
    failWith("ADMIN_015");
    const record = screen.getByRole("button", { name: "Record the verdict" }) as HTMLButtonElement;

    await user.click(screen.getByLabelText(/Return the goods to the farmer/));
    await user.type(screen.getByLabelText("Reason (kept in the audit trail)"), "too short");
    expect(record.disabled).toBe(true);

    await user.clear(screen.getByLabelText("Reason (kept in the audit trail)"));
    await user.type(screen.getByLabelText("Reason (kept in the audit trail)"), `  ${REASON}  `);
    expect(record.disabled).toBe(false);
    await user.click(record);

    const second = lastMutate();
    expect(second.vars).toEqual({
      id: "escrow-1",
      body: { resolution: "buyer_wins", action: "refund", disposition: "return_to_farmer", reason: REASON },
    });
    act(() => {
      second.opts.onSuccess?.();
      second.opts.onSettled?.();
    });
    expect(screen.queryByText("The order cannot follow this verdict as it is")).toBeNull();
    expect(screen.getByRole("status").textContent).toBe("Verdict saved.");
  });

  it("lists recorded verdicts awaiting settlement and offers the farmer_wins choices", async () => {
    const user = userEvent.setup();
    render(<DisputesPage />);

    await user.click(screen.getByRole("button", { name: "Awaiting settlement" }));
    expect(within(table()).getByText("Farmer wins")).toBeTruthy();
    await user.click(within(table()).getByRole("button", { name: "Choose what happens" }));

    expect(screen.getByText(/The order is not delivered yet/)).toBeTruthy();
    expect(screen.getByLabelText(/Pay the farmer now/)).toBeTruthy();
    expect(screen.getByLabelText(/Wait for the delivery/)).toBeTruthy();
    expect(screen.queryByLabelText(/Return the goods to the farmer/)).toBeNull();

    await user.click(screen.getByLabelText(/Pay the farmer now/));
    await user.type(screen.getByLabelText("Reason (kept in the audit trail)"), REASON);
    await user.click(screen.getByRole("button", { name: "Record the verdict" }));

    expect(lastMutate().vars).toEqual({
      id: "escrow-2",
      body: { resolution: "farmer_wins", action: "release", disposition: "settle_now", reason: REASON },
    });
  });

  it("shows ADMIN_016 when the choice does not fit the order", async () => {
    const user = userEvent.setup();
    render(<DisputesPage />);
    await user.click(screen.getByRole("button", { name: "Awaiting settlement" }));
    await user.click(within(table()).getByRole("button", { name: "Choose what happens" }));
    await user.click(screen.getByLabelText(/Pay the farmer now/));
    await user.type(screen.getByLabelText("Reason (kept in the audit trail)"), REASON);
    await user.click(screen.getByRole("button", { name: "Record the verdict" }));

    failWith("ADMIN_016");

    expect(screen.getByRole("alert").textContent).toBe(
      "That choice does not fit the order's current state. Reload the page and check the order.",
    );
  });

  it("cancel closes the choice without sending anything", async () => {
    const user = userEvent.setup();
    render(<DisputesPage />);
    await user.click(screen.getByRole("button", { name: "Awaiting settlement" }));
    await user.click(within(table()).getByRole("button", { name: "Choose what happens" }));

    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByText("The order cannot follow this verdict as it is")).toBeNull();
    expect(mocks.mutate).not.toHaveBeenCalled();
  });
});
