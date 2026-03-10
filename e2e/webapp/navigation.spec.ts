import { test, expect } from "@playwright/test";
import { TEST_USERS, apiLogin } from "../helpers/auth";

/**
 * Flutter Web App - Navigation Tests
 *
 * Tests URL-based navigation via go_router.
 * Flutter web with CanvasKit: we verify URL changes, Flutter rendering,
 * and absence of console errors.
 */

/** Wait for Flutter to fully render. */
async function waitForFlutter(page: import("@playwright/test").Page) {
  await page.waitForSelector("flutter-view", { timeout: 30_000 });
  await page.waitForTimeout(3000);
}

test.describe("Webapp Navigation", () => {
  test("unauthenticated access redirects all routes to /login", async ({
    page,
  }) => {
    const protectedRoutes = [
      "/orders",
      "/deliveries",
      "/payments",
      "/profile",
      "/settings",
    ];

    for (const route of protectedRoutes) {
      await page.goto(route);
      await waitForFlutter(page);
      // go_router auth redirect should send to /login
      expect(page.url(), `Route ${route} should redirect to /login`).toContain(
        "/login",
      );
    }
  });

  test("all main routes render Flutter view", async ({ page }) => {
    const routes = [
      "/",
      "/login",
      "/register",
      "/orders",
      "/deliveries",
      "/payments",
    ];

    for (const route of routes) {
      await page.goto(route);
      await waitForFlutter(page);

      const flutterView = page.locator("flutter-view");
      await expect(
        flutterView,
        `Flutter view missing on route ${route}`,
      ).toBeVisible();
    }
  });

  test("no critical JS errors on routes", async ({ page }) => {
    const criticalErrors: string[] = [];
    page.on("pageerror", (err) => {
      criticalErrors.push(err.message);
    });

    const routes = ["/", "/login", "/orders", "/deliveries", "/payments"];

    for (const route of routes) {
      await page.goto(route);
      await waitForFlutter(page);
    }

    // Filter Flutter-internal errors that are non-critical
    const realErrors = criticalErrors.filter(
      (e) =>
        !e.includes("LateInitializationError") &&
        !e.includes("setState") &&
        !e.includes("mounted"),
    );
    expect(realErrors, "Critical JS errors found").toEqual([]);
  });

  test("screenshots at each route", async ({ page }) => {
    const routes = ["/", "/login", "/register"];

    for (const route of routes) {
      await page.goto(route);
      await waitForFlutter(page);
      await page.screenshot({
        path: `test-results/webapp-route-${route.replace(/\//g, "") || "home"}.png`,
      });
    }
  });
});
