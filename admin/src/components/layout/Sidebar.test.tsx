import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { describe, expect, it, vi } from "vitest";

import { SidebarProvider } from "@/hooks/SidebarProvider";
import { Sidebar } from "./Sidebar";

/* The rendered sidebar is the source of truth for the admin navigation: e2e/admin
 * navigation.spec.ts checks every route listed here redirects to login when signed out. */

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

describe("Sidebar", () => {
  // Page M board: the dark nav, the open page marked for a screen reader as well as by its ground.
  it("marks the open page as current on the dark nav", () => {
    render(
      <MemoryRouter initialEntries={["/disputes"]}>
        <SidebarProvider>
          <Sidebar />
        </SidebarProvider>
      </MemoryRouter>,
    );

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
    render(
      <MemoryRouter>
        <SidebarProvider>
          <Sidebar />
        </SidebarProvider>
      </MemoryRouter>,
    );

    const nav = screen.getByRole("navigation");
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
