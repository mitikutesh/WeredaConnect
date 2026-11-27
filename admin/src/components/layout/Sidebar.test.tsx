import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Link, MemoryRouter } from "react-router";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { SidebarProvider } from "@/hooks/SidebarProvider";
import { OPEN_DISPUTE_COUNT_MAX_PAGES } from "@/hooks/useQuery";
import { Sidebar } from "./Sidebar";

/* The rendered sidebar is the source of truth for the admin navigation: e2e/admin
 * navigation.spec.ts checks every route listed here redirects to login when signed out. */

const api = vi.hoisted(() => ({ get: vi.fn() }));

vi.mock("@/lib/api", () => ({ api }));

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

const disputed = (n: number) => Array.from({ length: n }, (_, i) => ({ escrowId: `escrow-${i}`, status: "disputed" }));

/** The sidebar at a path, with links to move between pages as the shell's routes do. */
function renderSidebar(path = "/") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <SidebarProvider>
          <Sidebar />
        </SidebarProvider>
        <Link to="/disputes/escrow-1">go to a dispute</Link>
        <Link to="/escrows/escrow-1">go to an escrow</Link>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  api.get.mockReset();
  api.get.mockResolvedValue({ disputes: [] });
});

describe("Sidebar", () => {
  // Page M board: the dark nav, the open page marked for a screen reader as well as by its ground.
  it("marks the open page as current on the dark nav", () => {
    renderSidebar("/disputes");

    const nav = screen.getByRole("navigation", { name: "Admin" });
    const current = within(nav).getByRole("link", { name: "Disputes" });
    expect(current.getAttribute("aria-current")).toBe("page");
    expect(current.className).toContain("bg-nav-selected");
    const other = within(nav).getByRole("link", { name: "Escrows" });
    expect(other.getAttribute("aria-current")).toBeNull();
    expect(other.className).toContain("text-nav-text");
    expect(nav.closest("aside")?.className).toContain("bg-nav");
  });

  it("renders one link per admin page, Manual Payouts and Unapplied Payments included", () => {
    renderSidebar();

    const nav = screen.getByRole("navigation", { name: "Admin" });
    const hrefs = within(nav)
      .getAllByRole("link")
      .map((link) => link.getAttribute("href"));
    expect(hrefs).toEqual([
      "/",
      "/escrows",
      "/disputes",
      "/payouts",
      "/stuck-orders",
      "/unapplied-payments",
      "/fraud-review",
      "/listing-reports",
      "/reputation-appeals",
      "/users",
      "/transactions",
      "/revenue",
      "/analytics",
      "/audit-log",
      "/system-health",
      "/compliance",
    ]);
    expect(within(nav).getByRole("link", { name: "Manual Payouts" }).getAttribute("href")).toBe("/payouts");
    expect(within(nav).getByRole("link", { name: "Stuck Orders" }).getAttribute("href")).toBe("/stuck-orders");
    expect(within(nav).getByRole("link", { name: "Reputation Appeals" }).getAttribute("href")).toBe(
      "/reputation-appeals",
    );
    expect(within(nav).getByRole("link", { name: "Unapplied Payments" }).getAttribute("href")).toBe(
      "/unapplied-payments",
    );
  });
});

/* Page M board: "Disputes · 3" in the nav, from the Disputes list's own call. */
describe("Sidebar open dispute count", () => {
  const disputesLink = () =>
    within(screen.getByRole("navigation", { name: "Admin" })).getByRole("link", { name: /^Disputes/ });

  it("shows the open disputes beside Disputes and reads the count out", async () => {
    api.get.mockResolvedValue({
      disputes: [...disputed(3), { escrowId: "escrow-old", status: "resolved" }],
    });
    renderSidebar();

    const link = await screen.findByRole("link", { name: "Disputes, 3 open" });
    expect(link.textContent).toBe("Disputes · 3");
    expect(link.getAttribute("href")).toBe("/disputes");
    expect(api.get).toHaveBeenCalledTimes(1);
    expect(api.get).toHaveBeenCalledWith("/admin/disputes");
  });

  it("shows nothing while the count loads", () => {
    api.get.mockReturnValue(new Promise(() => {}));
    renderSidebar();

    expect(disputesLink().textContent).toBe("Disputes");
    expect(disputesLink().getAttribute("aria-label")).toBeNull();
  });

  it("shows nothing when the read fails", async () => {
    api.get.mockRejectedValue({ code: "ADMIN_500" });
    renderSidebar();

    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(1));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(disputesLink().textContent).toBe("Disputes");
    expect(disputesLink().getAttribute("aria-label")).toBeNull();
  });

  it("shows nothing when no dispute is open", async () => {
    api.get.mockResolvedValue({ disputes: [{ escrowId: "escrow-old", status: "resolved" }] });
    renderSidebar();

    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(1));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(disputesLink().textContent).toBe("Disputes");
  });

  it("adds up the pages of a list that comes back in short pages", async () => {
    api.get
      .mockResolvedValueOnce({ disputes: disputed(2), nextToken: "tok/1+" })
      .mockResolvedValueOnce({ disputes: [], nextToken: "tok2" })
      .mockResolvedValueOnce({ disputes: disputed(1) });
    renderSidebar();

    expect((await screen.findByRole("link", { name: "Disputes, 3 open" })).textContent).toBe("Disputes · 3");
    expect(api.get.mock.calls.map((call) => call[0])).toEqual([
      "/admin/disputes",
      "/admin/disputes?nextToken=tok%2F1%2B",
      "/admin/disputes?nextToken=tok2",
    ]);
  });

  it("shows no count rather than a wrong one when the list is longer than the pages it reads", async () => {
    api.get.mockResolvedValue({ disputes: disputed(1), nextToken: "more" });
    renderSidebar();

    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(OPEN_DISPUTE_COUNT_MAX_PAGES));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(api.get).toHaveBeenCalledTimes(OPEN_DISPUTE_COUNT_MAX_PAGES);
    expect(disputesLink().textContent).toBe("Disputes");
  });

  it("reads again on entering and on leaving a dispute page, not on other moves", async () => {
    const user = userEvent.setup();
    api.get.mockResolvedValueOnce({ disputes: disputed(3) });
    renderSidebar("/");
    await screen.findByRole("link", { name: "Disputes, 3 open" });

    // Between two pages that are not dispute pages: no read.
    await user.click(screen.getByRole("link", { name: "go to an escrow" }));
    expect(api.get).toHaveBeenCalledTimes(1);

    // Onto a dispute page: read again.
    api.get.mockResolvedValueOnce({ disputes: disputed(2) });
    await user.click(screen.getByRole("link", { name: "go to a dispute" }));
    expect((await screen.findByRole("link", { name: "Disputes, 2 open" })).textContent).toBe("Disputes · 2");
    expect(api.get).toHaveBeenCalledTimes(2);

    // From the dispute page to the Disputes list: still a dispute page, no read.
    await user.click(screen.getByRole("link", { name: "Disputes, 2 open" }));
    expect(api.get).toHaveBeenCalledTimes(2);

    // Away from the dispute pages: read again.
    api.get.mockResolvedValueOnce({ disputes: disputed(1) });
    await user.click(screen.getByRole("link", { name: "go to an escrow" }));
    expect((await screen.findByRole("link", { name: "Disputes, 1 open" })).textContent).toBe("Disputes · 1");
    expect(api.get).toHaveBeenCalledTimes(3);
  });

  it("drops the count when a later read fails", async () => {
    const user = userEvent.setup();
    api.get.mockResolvedValueOnce({ disputes: disputed(3) });
    renderSidebar("/");
    await screen.findByRole("link", { name: "Disputes, 3 open" });

    api.get.mockRejectedValueOnce({ code: "ADMIN_500" });
    await user.click(screen.getByRole("link", { name: "go to a dispute" }));
    await waitFor(() => expect(disputesLink().textContent).toBe("Disputes"));
    expect(disputesLink().getAttribute("aria-label")).toBeNull();
  });
});
