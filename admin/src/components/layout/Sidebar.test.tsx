import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { describe, expect, it, vi } from "vitest";

import { SidebarProvider } from "@/hooks/useSidebar";
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
      "/unapplied-payments",
      "/fraud-review",
      "/users",
      "/transactions",
      "/revenue",
      "/analytics",
      "/audit-log",
      "/system-health",
      "/compliance",
    ]);
    expect(within(nav).getByRole("link", { name: "Manual Payouts" }).getAttribute("href")).toBe("/payouts");
    expect(within(nav).getByRole("link", { name: "Unapplied Payments" }).getAttribute("href")).toBe(
      "/unapplied-payments",
    );
  });
});
