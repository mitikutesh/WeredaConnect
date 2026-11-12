import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { StuckOrdersPage } from "./StuckOrdersPage";

/* ESC-24: the stuck orders page. The hook is mocked per source; the English strings are real, so
 * the tests also prove the keys exist. */

function query() {
  return {
    data: undefined as unknown,
    isLoading: false,
    isError: false,
    hasNextPage: false,
    isFetchingNextPage: false,
    fetchNextPage: vi.fn(),
  };
}

const mocks = vi.hoisted(() => ({
  queue: undefined as unknown as ReturnType<typeof query>,
  deadline: undefined as unknown as ReturnType<typeof query>,
}));

vi.mock("@/hooks/useQuery", () => ({
  useStuckOrders: (source: "queue" | "deadline") => mocks[source],
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

function stuck(overrides: Record<string, unknown> = {}) {
  return {
    orderId: "ord-1",
    status: "accepted",
    stage: "in_progress",
    reason: "hold_cap",
    stuckSince: "2026-11-08T10:00:00Z",
    ageHours: 74,
    buyerId: "buyer-1",
    buyerName: "Hawi Flour Mill",
    farmerId: "farmer-1",
    farmerName: "Tolosa Gemechu",
    truckerId: "trucker-1",
    truckerName: "Dawit Tesfaye",
    cropType: "teff",
    orderTotal: 17250,
    currency: "ETB",
    ...overrides,
  };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <StuckOrdersPage />
    </MemoryRouter>,
  );
}

describe("StuckOrdersPage (ESC-24)", () => {
  beforeEach(() => {
    mocks.queue = query();
    mocks.deadline = query();
  });

  it("lists the admin queue with the reason, the age, the parties and a link to the transaction", () => {
    mocks.queue.data = { pages: [{ source: "queue", orders: [stuck()] }] };

    renderPage();

    expect(screen.getByRole("heading", { name: "Stuck Orders" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Admin queue" }).getAttribute("aria-pressed")).toBe("true");
    const table = screen.getByRole("table");
    const row = within(table).getAllByRole("row")[1];
    expect(within(row).getByRole("link", { name: "ord-1" }).getAttribute("href")).toBe("/transactions/ord-1");
    expect(within(row).getByText("Held 21 days, an admin decides")).toBeTruthy();
    expect(within(row).getByText("3 d")).toBeTruthy();
    expect(within(row).getByText("Hawi Flour Mill")).toBeTruthy();
    expect(within(row).getByText("Tolosa Gemechu")).toBeTruthy();
    expect(within(row).getByText("Dawit Tesfaye")).toBeTruthy();
    expect(within(row).getByText("17,250.00 Birr")).toBeTruthy();
    // Read only: the page offers the tabs and nothing that moves an order or its money.
    expect(screen.getAllByRole("button").map((b) => b.textContent)).toEqual(["Admin queue", "Missed deadlines"]);
  });

  it("the missed deadlines tab names each deadline, falls back to ids and to the raw reason", async () => {
    const user = userEvent.setup();
    mocks.deadline.data = {
      pages: [
        {
          source: "deadline",
          orders: [
            stuck({ orderId: "ord-2", status: "funded", reason: "farmer_response", ageHours: 2, truckerId: undefined, truckerName: undefined }),
            stuck({ orderId: "ord-3", status: "awaiting_transport", reason: "buyer_choice", ageHours: 5, buyerName: undefined }),
            stuck({ orderId: "ord-4", status: "funded", reason: "a_new_reason", ageHours: 1 }),
          ],
        },
      ],
    };

    renderPage();
    expect(screen.getByText("No stuck orders.")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Missed deadlines" }));

    const rows = within(screen.getByRole("table")).getAllByRole("row");
    expect(rows).toHaveLength(4);
    expect(within(rows[1]).getByText("Paid, the farmer did not answer in time")).toBeTruthy();
    expect(within(rows[1]).getByText("2 h")).toBeTruthy();
    expect(within(rows[1]).getByText("No trucker")).toBeTruthy();
    expect(within(rows[2]).getByText("Awaiting transport, the buyer did not choose in time")).toBeTruthy();
    expect(within(rows[2]).getByText("buyer-1")).toBeTruthy();
    expect(within(rows[3]).getByText("a_new_reason")).toBeTruthy();
    expect(within(rows[3]).getByRole("link", { name: "ord-4" }).getAttribute("href")).toBe("/transactions/ord-4");
  });

  it("loads the next page on demand, also when the page before it was empty", async () => {
    const user = userEvent.setup();
    mocks.queue.data = { pages: [{ source: "queue", orders: [], nextToken: "t1" }] };
    mocks.queue.hasNextPage = true;

    renderPage();
    await user.click(screen.getByRole("button", { name: "Load more" }));

    expect(mocks.queue.fetchNextPage).toHaveBeenCalledTimes(1);
  });

  it("says when the list could not be loaded", () => {
    mocks.queue.isError = true;

    renderPage();

    expect(screen.getByRole("alert").textContent).toBe("The stuck orders could not be loaded. Reload the page.");
    expect(screen.queryByRole("table")).toBeNull();
  });
});
