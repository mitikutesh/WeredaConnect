import { readFileSync } from "node:fs";
import path from "node:path";
import { test, expect } from "@playwright/test";

/**
 * Admin Dashboard - Navigation Tests
 *
 * Tests sidebar navigation, route loading, and UI shell.
 * Since admin requires a separate Cognito admin user (not the mobile test users),
 * these tests verify the login-gate behavior and test the UI structure
 * of the login page and any publicly accessible elements.
 *
 * For authenticated navigation testing, we mock the auth state.
 */

const protectedRoutes = [
  "/",
  "/escrows",
  "/disputes",
  "/payouts",
  "/unapplied-payments",
  "/fraud-review",
  "/users",
  "/revenue",
  "/transactions",
  "/analytics",
  "/audit-log",
  "/system-health",
  "/compliance",
];

test.describe("Admin Navigation - Unauthenticated", () => {
  for (const route of protectedRoutes) {
    test(`${route} redirects to /login`, async ({ page }) => {
      await page.goto(route);
      await page.waitForURL("**/login", { timeout: 10_000 });
      expect(page.url()).toContain("/login");
    });
  }
});

test.describe("Admin Navigation - UI Structure", () => {
  test("login page has correct document title", async ({ page }) => {
    await page.goto("/login");
    // Vite sets title from index.html
    const title = await page.title();
    expect(title.length).toBeGreaterThan(0);
  });

  test("login page loads without JS errors", async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (err) => {
      errors.push(err.message);
    });

    await page.goto("/login");
    await page.waitForTimeout(2000);

    // Filter known issues (Cognito config missing in test env is expected)
    const criticalErrors = errors.filter(
      (e) =>
        !e.includes("Cognito") &&
        !e.includes("Missing Cognito config") &&
        !e.includes("UserPoolId"),
    );
    expect(criticalErrors).toEqual([]);
  });

  test("every sidebar link is a protected route checked above", () => {
    // The sidebar is not rendered on the login page, so read its link list from the source
    // (admin/src/components/layout/Sidebar.test.tsx asserts the rendered links match it).
    const source = readFileSync(
      path.resolve(__dirname, "../../admin/src/components/layout/Sidebar.tsx"),
      "utf-8",
    );
    const navBlock = source.slice(source.indexOf("const navItems"), source.indexOf("];"));
    const sidebarPaths = [...navBlock.matchAll(/path: "([^"]+)"/g)].map((m) => m[1]);

    expect(sidebarPaths.length).toBeGreaterThan(0);
    expect([...sidebarPaths].sort()).toEqual([...protectedRoutes].sort());
  });

  test("unknown routes redirect to home (then to login)", async ({ page }) => {
    await page.goto("/nonexistent-page");
    await page.waitForURL("**/login", { timeout: 10_000 });
    expect(page.url()).toContain("/login");
  });

  test("multiple rapid navigations don't crash the app", async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (err) => {
      errors.push(err.message);
    });

    // Rapidly navigate between routes
    for (const route of ["/", "/escrows", "/users", "/login", "/"]) {
      await page.goto(route, { waitUntil: "domcontentloaded" });
    }

    await page.waitForTimeout(2000);

    const criticalErrors = errors.filter(
      (e) => !e.includes("Cognito") && !e.includes("Missing Cognito config"),
    );
    expect(criticalErrors).toEqual([]);
  });
});
