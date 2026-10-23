import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { formatDateTime } from "@/lib/i18n";

import { FraudReviewPage } from "./FraudReviewPage";

/* FRD-05: the Fraud Review page shows what each action changed (fraud-detection-architecture.md
 * section 19). The hooks are mocked; the English strings are real so the tests also prove the keys exist. */

type MutateOptions = {
  onSuccess?: (data: unknown) => void;
  onError?: (err: unknown) => void;
};

const mocks = vi.hoisted(() => ({
  signals: { data: undefined as unknown, isLoading: false },
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
}));

vi.mock("@/hooks/useQuery", () => ({
  useFraudSignals: () => mocks.signals,
  useFraudAction: () => ({ mutate: mocks.mutate, isPending: false }),
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

function signal(overrides: Record<string, unknown> = {}) {
  return {
    signalId: "sig-1",
    userId: "farmer-1",
    signalType: "FRAUD_DUPLICATE_LISTING",
    severity: "high",
    status: "pending",
    createdAt: "2026-10-22T10:00:00Z",
    ...overrides,
  };
}

function table(): HTMLElement {
  // DataTable renders a desktop table and a mobile card list; scope to the table.
  return screen.getAllByRole("table")[0];
}

function lastMutate() {
  const [vars, opts] = mocks.mutate.mock.calls[mocks.mutate.mock.calls.length - 1];
  return { vars, opts: opts ?? {} };
}

function succeedWith(data: unknown) {
  const { opts } = lastMutate();
  act(() => opts.onSuccess?.(data));
}

describe("FraudReviewPage", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
    mocks.signals.data = { signals: [signal()] };
  });

  it("approve: says the signal no longer counts", async () => {
    const user = userEvent.setup();
    render(<FraudReviewPage />);

    await user.click(within(table()).getByRole("button", { name: "Approve" }));

    expect(lastMutate().vars).toEqual({ id: "sig-1", action: { action: "approve" } });
    succeedWith({ resolution: "approve", userId: "farmer-1" });
    expect(screen.getByRole("status").textContent).toBe(
      "Signal approved. It no longer counts toward this user's risk score.",
    );
  });

  it("reject: asks first, then shows the suspension and the open escrows, read-only", async () => {
    const user = userEvent.setup();
    render(<FraudReviewPage />);

    await user.click(within(table()).getByRole("button", { name: "Reject" }));
    expect(mocks.mutate).not.toHaveBeenCalled();
    expect(within(table()).getByText("Rejecting suspends the account and signs the user out.")).toBeTruthy();
    await user.click(within(table()).getByRole("button", { name: "Reject and suspend" }));

    expect(lastMutate().vars).toEqual({ id: "sig-1", action: { action: "reject" } });
    succeedWith({
      resolution: "reject",
      userId: "farmer-1",
      suspended: true,
      suspendReason: "fraud_confirmed",
      sessionsEnded: true,
      openEscrowsComplete: true,
      openEscrows: [
        { escrowId: "esc-1", status: "escrowed", party: "farmer", amount: 8000 },
        { escrowId: "esc-2", status: "held", party: "trucker", amount: 700 },
      ],
    });

    const status = screen.getByRole("status");
    expect(within(status).getByText("Signal rejected. User farmer-1 is suspended (fraud confirmed).")).toBeTruthy();
    expect(within(status).getByText("Open escrows (read-only)")).toBeTruthy();
    expect(
      within(status).getByText(
        "Nothing was held now. The next settlement sweep holds each of these escrows when it comes due.",
      ),
    ).toBeTruthy();
    const items = within(status).getAllByRole("listitem").map((li) => li.textContent);
    expect(items).toEqual(["esc-1: Farmer, Escrowed, 8,000.00 Birr", "esc-2: Trucker, Under review, 700.00 Birr"]);
    expect(status.textContent).not.toContain("may stay signed in");
    expect(status.textContent).not.toContain("may be incomplete");
    // No release or refund from here: the list only informs.
    expect(within(status).queryByRole("button")).toBeNull();
  });

  it("reject: cancel leaves the signal alone", async () => {
    const user = userEvent.setup();
    render(<FraudReviewPage />);

    await user.click(within(table()).getByRole("button", { name: "Reject" }));
    await user.click(within(table()).getByRole("button", { name: "Cancel" }));

    expect(mocks.mutate).not.toHaveBeenCalled();
    expect(within(table()).getByRole("button", { name: "Reject" })).toBeTruthy();
  });

  it("reject: warns when sessions were not ended, there is nothing open, or the list is cut short", async () => {
    const user = userEvent.setup();
    render(<FraudReviewPage />);
    await user.click(within(table()).getByRole("button", { name: "Reject" }));
    await user.click(within(table()).getByRole("button", { name: "Reject and suspend" }));

    succeedWith({
      resolution: "reject",
      userId: "farmer-1",
      suspended: true,
      sessionsEnded: false,
      openEscrows: [],
      openEscrowsComplete: false,
    });

    const status = screen.getByRole("status");
    expect(
      within(status).getByText(
        "The sign-in service was not updated, so the user may stay signed in until the session expires.",
      ),
    ).toBeTruthy();
    expect(within(status).getByText("This user has no open escrows.")).toBeTruthy();
    expect(within(status).getByText("This list may be incomplete. Check the Escrows page.")).toBeTruthy();
  });

  it("whitelist: shows until when soft signals do not count", async () => {
    const user = userEvent.setup();
    render(<FraudReviewPage />);

    await user.click(within(table()).getByRole("button", { name: "Whitelist" }));

    expect(lastMutate().vars).toEqual({ id: "sig-1", action: { action: "whitelist" } });
    const until = "2026-11-22T10:00:00.0000000Z";
    succeedWith({ resolution: "whitelist", userId: "farmer-1", whitelistUntil: until });
    expect(screen.getByRole("status").textContent).toBe(
      `Signal whitelisted. This user's soft signals (GPS and price) do not count until ${formatDateTime(until)}.`,
    );
  });

  it("reviewed rows show the decision and offer no actions", () => {
    mocks.signals.data = {
      signals: [
        signal({ signalId: "a", status: "reviewed", resolution: "approve" }),
        signal({ signalId: "r", status: "reviewed", resolution: "reject" }),
        signal({ signalId: "w", status: "reviewed", resolution: "whitelist" }),
      ],
    };
    render(<FraudReviewPage />);

    expect(within(table()).getByText("Approved (not counted)")).toBeTruthy();
    expect(within(table()).getByText("Rejected (fraud confirmed)")).toBeTruthy();
    expect(within(table()).getByText("Whitelisted")).toBeTruthy();
    expect(within(table()).queryByRole("button")).toBeNull();
  });

  it("shows the error for a refused action", async () => {
    const user = userEvent.setup();
    render(<FraudReviewPage />);
    await user.click(within(table()).getByRole("button", { name: "Approve" }));

    act(() => lastMutate().opts.onError?.({ code: "ADMIN_006" }));

    expect(screen.getByRole("alert").textContent).toBe("Fraud signal not found. Reload the page.");
    expect(screen.queryByRole("status")).toBeNull();
  });
});
