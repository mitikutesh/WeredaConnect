import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { formatDateTime } from "@/lib/i18n";

import { FraudReviewPage } from "./FraudReviewPage";

/* FRD-05: the Fraud Review page shows what each action changed (fraud-detection-architecture.md
 * section 19). FRD-07: the signals come grouped by user with the user's risk score, and the held
 * queue releases or refunds an escrow held for fraud review (section 18.3). The hooks are mocked;
 * the English strings are real so the tests also prove the keys exist. */

type MutateOptions = {
  onSuccess?: (data: unknown) => void;
  onError?: (err: unknown) => void;
  onSettled?: () => void;
};

const mocks = vi.hoisted(() => ({
  pages: [] as unknown[],
  statuses: [] as string[],
  held: [] as unknown[],
  mutate: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
  settle: vi.fn<(vars: unknown, opts?: MutateOptions) => void>(),
  fetchNextPage: vi.fn(),
  hasNextPage: false,
}));

function infinite(pages: unknown[]) {
  return {
    data: { pages },
    isLoading: false,
    isError: false,
    hasNextPage: mocks.hasNextPage,
    fetchNextPage: mocks.fetchNextPage,
    isFetchingNextPage: false,
  };
}

vi.mock("@/hooks/useQuery", () => ({
  useFraudSignals: (status: string) => {
    mocks.statuses.push(status);
    return infinite(mocks.pages);
  },
  useFraudAction: () => ({ mutate: mocks.mutate, isPending: false }),
  useHeldEscrows: () => infinite([{ escrows: mocks.held }]),
  useSettleHeldEscrow: () => ({ mutate: mocks.settle, isPending: false }),
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

function scored(overrides: Record<string, unknown> = {}) {
  return {
    userId: "farmer-1",
    riskScore: 20,
    enforcementScore: 20,
    riskTier: "low",
    truncated: false,
    scoreAvailable: true,
    ...overrides,
  };
}

function setSignals(signals: unknown[], users: unknown[] = [scored()]) {
  mocks.pages = [{ signals, users }];
}

function heldEscrow(overrides: Record<string, unknown> = {}) {
  return {
    escrowId: "escrow-order-ord-9",
    orderId: "ord-9",
    deliveryId: "del-9",
    buyerId: "buyer-1",
    buyerName: "Meron",
    farmerId: "farmer-1",
    farmerName: "Abebe",
    amount: 5000,
    status: "held",
    holdReason: "fraud_confirmed",
    heldAt: "2026-10-20T10:00:00Z",
    heldFor: "release",
    heldFromStatus: "escrowed",
    ...overrides,
  };
}

function renderPage() {
  render(
    <MemoryRouter>
      <FraudReviewPage />
    </MemoryRouter>,
  );
}

function table(): HTMLElement {
  // DataTable renders a desktop table and a mobile card list; scope to the first table.
  return screen.getAllByRole("table")[0];
}

function lastCall(fn: typeof mocks.mutate) {
  const [vars, opts] = fn.mock.calls[fn.mock.calls.length - 1];
  return { vars, opts: opts ?? {} };
}

function succeedWith(data: unknown) {
  const { opts } = lastCall(mocks.mutate);
  act(() => opts.onSuccess?.(data));
}

describe("FraudReviewPage signals", () => {
  beforeEach(() => {
    mocks.mutate.mockReset();
    mocks.settle.mockReset();
    mocks.fetchNextPage.mockReset();
    mocks.statuses = [];
    mocks.hasNextPage = false;
    setSignals([signal()]);
  });

  it("approve: says the signal no longer counts", async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(within(table()).getByRole("button", { name: "Approve" }));

    expect(lastCall(mocks.mutate).vars).toEqual({ id: "sig-1", action: { action: "approve" } });
    succeedWith({ resolution: "approve", userId: "farmer-1" });
    expect(screen.getByRole("status").textContent).toBe(
      "Signal approved. It no longer counts toward this user's risk score.",
    );
  });

  it("reject: asks first, then shows the suspension and the open escrows, read-only", async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(within(table()).getByRole("button", { name: "Reject" }));
    expect(mocks.mutate).not.toHaveBeenCalled();
    expect(within(table()).getByText("Rejecting suspends the account and signs the user out.")).toBeTruthy();
    await user.click(within(table()).getByRole("button", { name: "Reject and suspend" }));

    expect(lastCall(mocks.mutate).vars).toEqual({ id: "sig-1", action: { action: "reject" } });
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
    // No release or refund from here: the list only informs; the held queue settles.
    expect(within(status).queryByRole("button")).toBeNull();
  });

  it("reject: cancel leaves the signal alone", async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(within(table()).getByRole("button", { name: "Reject" }));
    await user.click(within(table()).getByRole("button", { name: "Cancel" }));

    expect(mocks.mutate).not.toHaveBeenCalled();
    expect(within(table()).getByRole("button", { name: "Reject" })).toBeTruthy();
  });

  it("reject: warns when sessions were not ended, there is nothing open, or the list is cut short", async () => {
    const user = userEvent.setup();
    renderPage();
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

  it("whitelist: offered on soft signals only, and says the signal no longer counts", async () => {
    setSignals([
      signal({ signalId: "hard", signalType: "FRAUD_DUPLICATE_LISTING" }),
      signal({ signalId: "gps", signalType: "GPS_ANOMALY" }),
      signal({ signalId: "price", signalType: "PRICE_ANOMALY", severity: "medium" }),
    ]);
    const user = userEvent.setup();
    renderPage();

    // A whitelist would clear a hard signal for good, so the hard row offers approve and reject only.
    const rows = within(table()).getAllByRole("row").slice(1);
    expect(within(rows[0]).queryByRole("button", { name: "Whitelist" })).toBeNull();
    expect(within(rows[0]).getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(within(rows[1]).getByRole("button", { name: "Whitelist" })).toBeTruthy();
    await user.click(within(rows[2]).getByRole("button", { name: "Whitelist" }));

    expect(lastCall(mocks.mutate).vars).toEqual({ id: "price", action: { action: "whitelist" } });
    const until = "2026-11-22T10:00:00.0000000Z";
    succeedWith({ resolution: "whitelist", userId: "farmer-1", whitelistUntil: until });
    expect(screen.getByRole("status").textContent).toBe(
      "Signal whitelisted. This signal no longer counts, and this user's soft signals " +
        `do not count until ${formatDateTime(until)}.`,
    );
  });

  it("whitelist: offered on every soft type of the catalogue, never on a hard one (spec section 15)", () => {
    const soft = [
      "GPS_ANOMALY", "PRICE_ANOMALY", "QR_HANDOFF_FAILURE", "TELEBIRR_PAYER_MISMATCH", "TELEBIRR_PAYER_REFUSED",
      "STALE_VERSION_BURST", "SUSPENDED_TOKEN_USE", "GPS_JUMP", "GPS_MOCKED", "AMOUNT_ANOMALY",
      "NEW_ACCOUNT_LARGE_AMOUNT", "CANCEL_AFTER_FUND_RATE", "DISPUTE_RATE", "IMPOSSIBLE_DELIVERY_TIME",
      "SHARED_DEVICE_TRADE", "REPEATED_PAIR",
    ];
    const hard = [
      "FRAUD_DUPLICATE_LISTING", "FRAUD_VELOCITY_EXCEEDED", "IDOR_CREATE_CONFLICT", "ROLE_FORBIDDEN",
      "IMMUTABLE_FIELD_CHANGE", "KYC_THRESHOLD_STRUCTURING", "SELF_TRADE", "CONTACT_THEN_TRANSPORT_CANCEL",
    ];
    setSignals([...soft, ...hard].map((type) => signal({ signalId: type, signalType: type })));
    renderPage();

    const rows = within(table()).getAllByRole("row").slice(1);
    const offered = (type: string) => {
      const row = rows.find((r) => within(r).queryByText(type) !== null);
      expect(row).toBeTruthy();
      return within(row!).queryByRole("button", { name: "Whitelist" }) !== null;
    };
    for (const type of soft) expect([type, offered(type)]).toEqual([type, true]);
    for (const type of hard) expect([type, offered(type)]).toEqual([type, false]);
  });

  it("reviewed rows show the decision and offer no actions", async () => {
    setSignals([
      signal({ signalId: "a", status: "reviewed", resolution: "approve" }),
      signal({ signalId: "r", status: "reviewed", resolution: "reject" }),
      signal({ signalId: "w", status: "reviewed", resolution: "whitelist" }),
    ]);
    const user = userEvent.setup();
    renderPage();
    await user.click(screen.getByRole("button", { name: "Reviewed" }));

    expect(mocks.statuses).toContain("reviewed");
    expect(within(table()).getByText("Approved (not counted)")).toBeTruthy();
    expect(within(table()).getByText("Rejected (fraud confirmed)")).toBeTruthy();
    expect(within(table()).getByText("Whitelisted")).toBeTruthy();
    expect(within(table()).queryByRole("button")).toBeNull();
  });

  it("shows this page's wording for a refused action, then the shared text", async () => {
    const user = userEvent.setup();
    renderPage();
    const fail = async (err: { code: string; status: number }) => {
      await user.click(within(table()).getByRole("button", { name: "Approve" }));
      act(() => lastCall(mocks.mutate).opts.onError?.(err));
      return screen.getByRole("alert").textContent;
    };

    expect(await fail({ code: "ADMIN_006", status: 404 })).toBe(
      "This signal was not found, or it is not a signal. Reload the page.",
    );
    expect(await fail({ code: "ADMIN_007", status: 404 })).toBe(
      "The user of this signal was not found, so no account was suspended. The signal is still pending.",
    );
    // 503 ADMIN_006 is the missing table, not a missing signal.
    expect(await fail({ code: "ADMIN_006", status: 503 })).toBe("Action failed. Please try again.");
    expect(await fail({ code: "ADMIN_009", status: 400 })).toBe("Unknown action.");
    expect(screen.queryByRole("status")).toBeNull();
  });

  /* FRD-07 */

  it("reads the pending signals first and groups them by user, each with the user's risk score", () => {
    setSignals(
      [
        signal({ signalId: "a1", userId: "farmer-1", signalType: "GPS_ANOMALY" }),
        signal({ signalId: "b1", userId: "buyer-2", signalType: "PRICE_ANOMALY" }),
        signal({ signalId: "a2", userId: "farmer-1", signalType: "FRAUD_DUPLICATE_LISTING" }),
      ],
      [
        scored({ userId: "farmer-1", displayName: "Abebe", riskScore: 60, riskTier: "high" }),
        scored({ userId: "buyer-2", riskScore: 10, riskTier: "low" }),
      ],
    );
    renderPage();

    expect(mocks.statuses[0]).toBe("pending");
    const groups = screen.getAllByRole("region");
    expect(groups.map((g) => g.getAttribute("aria-label"))).toEqual(["Abebe", "buyer-2"]);
    expect(within(groups[0]).getByRole("link", { name: "Abebe" }).getAttribute("href")).toBe("/users/farmer-1");
    expect(within(groups[0]).getByText("Risk score 60 (High)")).toBeTruthy();
    expect(within(groups[1]).getByText("Risk score 10 (Low)")).toBeTruthy();
    const firstTable = within(groups[0]).getAllByRole("table")[0];
    expect(within(firstTable).getAllByRole("row").slice(1)).toHaveLength(2);
    expect(within(firstTable).getByText("GPS_ANOMALY")).toBeTruthy();
    expect(within(firstTable).getByText("FRAUD_DUPLICATE_LISTING")).toBeTruthy();
  });

  it("shows each row's first time and says a row stands for an hour of refusals", () => {
    renderPage();

    expect(
      screen.getByText("One row stands for every refusal of its type in one hour. The time shown is the first one."),
    ).toBeTruthy();
    expect(within(table()).getByRole("columnheader", { name: "First seen" })).toBeTruthy();
    expect(within(table()).getByText(formatDateTime("2026-10-22T10:00:00Z"))).toBeTruthy();
  });

  it("says when a score could not be read, or was cut short and counts as critical", () => {
    setSignals(
      [signal({ signalId: "a", userId: "u-1" }), signal({ signalId: "b", userId: "u-2" })],
      [
        scored({ userId: "u-1", scoreAvailable: false }),
        scored({ userId: "u-2", riskScore: 120, riskTier: "critical", truncated: true }),
      ],
    );
    renderPage();

    const [first, second] = screen.getAllByRole("region");
    expect(within(first).getByText("Risk score unavailable")).toBeTruthy();
    expect(within(first).queryByText(/Risk score \d/)).toBeNull();
    expect(within(second).getByText("Risk score 120 (Critical)")).toBeTruthy();
    expect(
      within(second).getByText("Too many signals to read in full, so this user counts as critical."),
    ).toBeTruthy();
  });

  /* FRD-19 */

  it("shows the AI advice on the user's group, labelled as advice, and keeps its points apart from the rules'", () => {
    setSignals(
      [
        signal({
          signalId: "ai",
          userId: "buyer-1",
          signalType: "AI_ADVISORY",
          severity: "medium",
          entityType: "user",
          description: "Probes order-1, then splits order-2 under the identity check.",
          aiConcern: "high",
          aiModel: "eu.anthropic.claude-opus-5-5",
        }),
        signal({ signalId: "s", userId: "buyer-1", signalType: "IDOR_CREATE_CONFLICT" }),
      ],
      [scored({ userId: "buyer-1", riskScore: 70, enforcementScore: 60, riskTier: "high" })],
    );
    renderPage();

    const group = screen.getByRole("region");
    expect(within(group).getByText("Risk score 70 (High)")).toBeTruthy();
    expect(within(group).getByText("Rules 60 + AI advice 10. Critical is decided by the rules alone.")).toBeTruthy();

    const advice = within(group).getByRole("complementary", { name: "AI advice" });
    expect(within(advice).getByText("Probes order-1, then splits order-2 under the identity check.")).toBeTruthy();
    expect(within(advice).getByText("Concern: High")).toBeTruthy();
    expect(advice.textContent).toContain("It is advice, not a decision");
    expect(advice.textContent).toContain("Model: eu.anthropic.claude-opus-5-5");

    // The advice row itself is in the table, labelled, with approve and reject (it is not a soft signal).
    const rows = within(within(group).getAllByRole("table")[0]).getAllByRole("row").slice(1);
    expect(within(rows[0]).getByText("AI advice")).toBeTruthy();
    expect(within(rows[0]).queryByText("AI_ADVISORY")).toBeNull();
    expect(within(rows[0]).getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(within(rows[0]).queryByRole("button", { name: "Whitelist" })).toBeNull();
    expect(within(rows[1]).getByText("IDOR_CREATE_CONFLICT")).toBeTruthy();
  });

  /* FUP-145 */

  it("lists what each label of the AI advice stands for, linked where the admin app has a page", () => {
    setSignals(
      [
        signal({
          signalId: "ai",
          userId: "buyer-1",
          signalType: "AI_ADVISORY",
          entityType: "user",
          description: "Probes order-1, then disputes escrow-1 after delivery-1.",
          aiConcern: "high",
          aiLabels: [
            { label: "delivery-1", entityType: "delivery", entityId: "del-9" },
            { label: "escrow-1", entityType: "escrow", entityId: "escrow-del-9" },
            { label: "escrow-2", entityType: "escrow", entityId: "escrow-order-ord-7", orderId: "ord-7" },
            { label: "order-1", entityType: "order", entityId: "ord-7", orderId: "ord-7" },
            { label: "broken" },
          ],
        }),
      ],
      [scored({ userId: "buyer-1", riskScore: 70, enforcementScore: 60, riskTier: "high" })],
    );
    renderPage();

    const advice = screen.getByRole("complementary", { name: "AI advice" });
    const items = within(within(advice).getByRole("list", { name: "What the labels stand for" })).getAllByRole(
      "listitem",
    );
    expect(items.map((i) => i.textContent)).toEqual([
      "delivery-1: del-9",
      "escrow-1: escrow-del-9",
      "escrow-2: escrow-order-ord-7",
      "order-1: ord-7",
    ]);
    // A delivery has no admin page; an escrow goes to the escrow, an order (or its escrow) to the timeline.
    expect(within(items[0]).queryByRole("link")).toBeNull();
    expect(within(items[1]).getByRole("link").getAttribute("href")).toBe("/escrows/escrow-del-9");
    expect(within(items[2]).getByRole("link").getAttribute("href")).toBe("/transactions/ord-7");
    expect(within(items[3]).getByRole("link").getAttribute("href")).toBe("/transactions/ord-7");
  });

  it("shows the AI advice of a row without a label map as before (rows written before the map was kept)", () => {
    setSignals(
      [
        signal({
          signalId: "ai",
          userId: "buyer-1",
          signalType: "AI_ADVISORY",
          entityType: "user",
          description: "Probes order-1.",
          aiConcern: "low",
        }),
        signal({
          signalId: "ai-empty",
          userId: "buyer-2",
          signalType: "AI_ADVISORY",
          entityType: "user",
          description: "Nothing unusual.",
          aiConcern: "none",
          aiLabels: [],
        }),
      ],
      [scored({ userId: "buyer-1" }), scored({ userId: "buyer-2" })],
    );
    renderPage();

    const panels = screen.getAllByRole("complementary", { name: "AI advice" });
    expect(panels).toHaveLength(2);
    expect(within(panels[0]).getByText("Probes order-1.")).toBeTruthy();
    expect(within(panels[1]).getByText("Nothing unusual.")).toBeTruthy();
    for (const panel of panels) {
      expect(within(panel).queryByRole("list")).toBeNull();
      expect(within(panel).queryByText("What the labels stand for")).toBeNull();
    }
  });

  it("shows no advice panel and no breakdown for a user without AI advice", () => {
    setSignals([signal()], [scored({ riskScore: 60, enforcementScore: 60, riskTier: "high" })]);
    renderPage();

    const group = screen.getByRole("region");
    expect(within(group).queryByRole("complementary")).toBeNull();
    expect(within(group).queryByText(/AI advice/)).toBeNull();
  });

  it("links a signal on an order to its transaction timeline", () => {
    setSignals([
      signal({ signalId: "o", entityType: "order", entityId: "ord-1", orderId: "ord-1" }),
      signal({ signalId: "d", entityType: "delivery", entityId: "del-1" }),
    ]);
    renderPage();

    const rows = within(table()).getAllByRole("row").slice(1);
    expect(within(rows[0]).getByRole("link", { name: "Transaction timeline" }).getAttribute("href")).toBe(
      "/transactions/ord-1",
    );
    expect(within(rows[1]).queryByRole("link", { name: "Transaction timeline" })).toBeNull();
    expect(within(rows[1]).getByText("delivery del-1")).toBeTruthy();
  });

  it("loads the next page on demand", async () => {
    mocks.hasNextPage = true;
    const user = userEvent.setup();
    renderPage();
    await user.click(screen.getByRole("button", { name: "Load more" }));
    expect(mocks.fetchNextPage).toHaveBeenCalled();
  });

  it("says when there is nothing to review", () => {
    setSignals([], []);
    renderPage();
    expect(screen.getByText("No signals to review.")).toBeTruthy();
  });
});

describe("FraudReviewPage held queue", () => {
  beforeEach(() => {
    mocks.settle.mockReset();
    mocks.held = [heldEscrow()];
    mocks.hasNextPage = false;
    setSignals([]);
  });

  async function openHeld() {
    const user = userEvent.setup();
    renderPage();
    await user.click(screen.getByRole("button", { name: "Held escrows" }));
    return user;
  }

  function settleWith(data: unknown) {
    const { opts } = lastCall(mocks.settle);
    act(() => {
      opts.onSuccess?.(data);
      opts.onSettled?.();
    });
  }

  it("lists a held escrow with why and from what it was held, linked to its escrow and timeline", async () => {
    await openHeld();

    const row = within(table()).getAllByRole("row")[1];
    expect(within(row).getByRole("link", { name: "escrow-order-ord-9" }).getAttribute("href")).toBe(
      "/escrows/escrow-order-ord-9",
    );
    expect(within(row).getByRole("link", { name: "Transaction timeline" }).getAttribute("href")).toBe(
      "/transactions/ord-9",
    );
    expect(within(row).getByText("Fraud confirmed in review")).toBeTruthy();
    expect(within(row).getByText("Was about to: Release")).toBeTruthy();
    expect(within(row).getByText("Held from: Escrowed")).toBeTruthy();
    expect(within(row).getByText("5,000.00 Birr")).toBeTruthy();
    expect(within(row).getByText("Buyer: Meron")).toBeTruthy();
    expect(within(row).getByText("Farmer: Abebe")).toBeTruthy();
  });

  it("release: needs a reason of 10 characters, asks once more, then releases", async () => {
    const user = await openHeld();
    const release = () => within(table()).getByRole("button", { name: "Release" });

    expect((release() as HTMLButtonElement).disabled).toBe(true);
    await user.type(within(table()).getByRole("textbox", { name: "Reason (10 to 500 characters)" }), "too short");
    expect((release() as HTMLButtonElement).disabled).toBe(true);
    await user.type(within(table()).getByRole("textbox", { name: "Reason (10 to 500 characters)" }), " still");
    expect((release() as HTMLButtonElement).disabled).toBe(false);

    await user.click(release());
    expect(mocks.settle).not.toHaveBeenCalled();
    expect(
      within(table()).getByText(
        "Release 5,000.00 Birr now? This pays the farmer and, when there is one, the trucker.",
      ),
    ).toBeTruthy();
    await user.click(within(table()).getByRole("button", { name: "Yes, release" }));

    expect(lastCall(mocks.settle).vars).toEqual({
      escrow: expect.objectContaining({ escrowId: "escrow-order-ord-9" }),
      op: "release",
      reason: "too short still",
    });
    settleWith({ escrowId: "escrow-order-ord-9", status: "completed" });
    expect(screen.getByRole("status").textContent).toBe("Escrow released.");
  });

  it("refund: refunds, and says when the payout waits in the manual payout queue", async () => {
    const user = await openHeld();
    await user.type(
      within(table()).getByRole("textbox", { name: "Reason (10 to 500 characters)" }),
      "Buyer paid for goods never sent.",
    );
    await user.click(within(table()).getByRole("button", { name: "Refund" }));
    expect(within(table()).getByText("Refund 5,000.00 Birr to the buyer now?")).toBeTruthy();
    await user.click(within(table()).getByRole("button", { name: "Yes, refund" }));

    expect(lastCall(mocks.settle).vars).toEqual({
      escrow: expect.objectContaining({ escrowId: "escrow-order-ord-9" }),
      op: "refund",
      reason: "Buyer paid for goods never sent.",
    });
    // 202 ESCROW_034: the live gateway could not pay; an admin pays it from the manual payout queue.
    settleWith({ error: { code: "ESCROW_034" } });
    expect(screen.getByRole("status").textContent).toBe("This payout is waiting in the manual payout queue.");
  });

  it("a refused settlement says why", async () => {
    const user = await openHeld();
    await user.type(
      within(table()).getByRole("textbox", { name: "Reason (10 to 500 characters)" }),
      "Buyer paid for goods never sent.",
    );
    await user.click(within(table()).getByRole("button", { name: "Refund" }));
    await user.click(within(table()).getByRole("button", { name: "Yes, refund" }));

    const { opts } = lastCall(mocks.settle);
    act(() => {
      opts.onError?.({ code: "ESCROW_INVALID_TRANSITION", status: 422 });
      opts.onSettled?.();
    });
    expect(screen.getByRole("alert").textContent).toBe(
      "This escrow cannot be settled that way now: its order or its verdict does not allow it. Open the transaction timeline.",
    );
  });

  it("an escrow held from a dispute verdict offers the verdict's direction only", async () => {
    mocks.held = [
      heldEscrow({ escrowId: "e-farmer", heldFromStatus: "dispute_resolved", dispute: { resolution: "farmer_wins" } }),
      heldEscrow({ escrowId: "e-buyer", heldFromStatus: "dispute_resolved", dispute: { resolution: "buyer_wins" } }),
    ];
    await openHeld();

    const [farmerRow, buyerRow] = within(table()).getAllByRole("row").slice(1);
    expect(within(farmerRow).getByRole("button", { name: "Release" })).toBeTruthy();
    expect(within(farmerRow).queryByRole("button", { name: "Refund" })).toBeNull();
    expect(within(farmerRow).getByText("The dispute verdict (farmer wins) allows a release only.")).toBeTruthy();
    expect(within(buyerRow).getByRole("button", { name: "Refund" })).toBeTruthy();
    expect(within(buyerRow).queryByRole("button", { name: "Release" })).toBeNull();
  });

  it("says when nothing is held", async () => {
    mocks.held = [];
    await openHeld();
    expect(screen.getByText("No escrows are held.")).toBeTruthy();
  });
});
