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

/* Both lists are infinite queries: the page reads data.pages and offers "Load more". */
function pagedList() {
  return {
    data: undefined as unknown,
    isLoading: false,
    hasNextPage: false,
    isFetchingNextPage: false,
    fetchNextPage: vi.fn(),
  };
}

const mocks = vi.hoisted(() => ({
  disputes: undefined as unknown as ReturnType<typeof pagedList>,
  awaiting: undefined as unknown as ReturnType<typeof pagedList>,
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useDisputes: () => mocks.disputes,
  useEscrowPages: () => mocks.awaiting,
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
    mocks.disputes = pagedList();
    mocks.awaiting = pagedList();
    mocks.disputes.data = { pages: [{ disputes: [openDispute()] }] };
    mocks.awaiting.data = { pages: [{ escrows: [recordedVerdict()] }] };
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

  it("pages the awaiting list: rows of every page, and Load more fetches the next", async () => {
    const user = userEvent.setup();
    const waiting = recordedVerdict();
    waiting.escrowId = "escrow-3";
    (waiting.dispute as Record<string, unknown>).disposition = "wait_for_delivery";
    (waiting.dispute as Record<string, unknown>).dispositionReason = "Trucker says two more days";
    // An empty first page (the scan found no match there) still has more behind it.
    mocks.awaiting.data = { pages: [{ escrows: [] }, { escrows: [recordedVerdict(), waiting] }] };
    mocks.awaiting.hasNextPage = true;
    render(<DisputesPage />);

    await user.click(screen.getByRole("button", { name: "Awaiting settlement" }));

    expect(within(table()).getByText("escrow-2")).toBeTruthy();
    expect(within(table()).getByText("None yet")).toBeTruthy();
    expect(within(table()).getByText("Wait for the delivery")).toBeTruthy();
    expect(within(table()).getByText("Trucker says two more days")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Load more" }));
    expect(mocks.awaiting.fetchNextPage).toHaveBeenCalledTimes(1);
  });

  it("offers Load more on the open list only when the server has more", async () => {
    const { rerender } = render(<DisputesPage />);
    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();

    mocks.disputes.hasNextPage = true;
    rerender(<DisputesPage />);

    expect(screen.getByRole("button", { name: "Load more" })).toBeTruthy();
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
