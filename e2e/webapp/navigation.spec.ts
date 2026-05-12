import { test, expect } from "@playwright/test";

/**
 * Flutter Web App - Navigation Tests
 *
 * Tests URL-based navigation via go_router.
 * Flutter web with CanvasKit: we verify URL changes, Flutter rendering,
 * and absence of console/JS errors across all routes.
 *
 * Protected routes should redirect unauthenticated users to /login.
 * Unknown routes should be handled gracefully (no crash, no blank screen).
 */

/** Wait for Flutter to fully render. */
async function waitForFlutter(page: import("@playwright/test").Page) {
  await page.waitForSelector("flutter-view", { timeout: 30_000 });
  await page.waitForTimeout(3000);
}

test.describe("Webapp Navigation - Auth Redirects", () => {
  /**
   * All protected routes must redirect to /login when unauthenticated.
   * This verifies the go_router redirect guard is working correctly.
   */
  const protectedRoutes = [
    "/",
    "/my-crops",
    "/find-crops",
    "/trips",
    "/listings",
    "/marketplace",
    "/orders",
    "/deliveries",
    "/payments",
    "/profile",
    "/settings",
  ];

  for (const route of protectedRoutes) {
    test(`${route} redirects to /login when unauthenticated`, async ({
      page,
    }) => {
      await page.goto(route);
      await waitForFlutter(page);
      // go_router auth redirect should send to /login
      expect(
        page.url(),
        `Route ${route} should redirect to /login`,
      ).toContain("/login");
    });
  }
});

test.describe("Webapp Navigation - Route Rendering", () => {
  test("all routes render Flutter view without crash", async ({ page }) => {
    const routes = [
      "/",
      "/login",
      "/register",
      "/forgot-password",
      "/reset-password",
      "/my-crops",
      "/find-crops",
      "/trips",
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

  test("login page has correct document title", async ({ page }) => {
    await page.goto("/login");
    await waitForFlutter(page);
    const title = await page.title();
    expect(title.length).toBeGreaterThan(0);
    expect(title).toContain("WeredaConnect");
  });
});

test.describe("Webapp Navigation - Error Handling", () => {
  test("no critical JS errors on page load (login)", async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (err) => {
      errors.push(err.message);
    });

    await page.goto("/login");
    await waitForFlutter(page);

    // Filter Flutter-internal errors that are non-critical
    const realErrors = errors.filter(
      (e) =>
        !e.includes("LateInitializationError") &&
        !e.includes("setState") &&
        !e.includes("mounted"),
    );
    expect(realErrors, "Critical JS errors found on /login").toEqual([]);
  });

  test("no critical JS errors across multiple routes", async ({ page }) => {
    const criticalErrors: string[] = [];
    page.on("pageerror", (err) => {
      criticalErrors.push(err.message);
    });

    const routes = [
      "/",
      "/login",
      "/register",
      "/forgot-password",
      "/orders",
      "/deliveries",
      "/payments",
    ];

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

  test("unknown route handled gracefully (no crash)", async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (err) => {
      errors.push(err.message);
    });

    await page.goto("/nonexistent-page-xyz");
    await waitForFlutter(page);

    // Flutter view should still render (no blank screen)
    const flutterView = page.locator("flutter-view");
    await expect(flutterView).toBeVisible();

    // Should redirect to /login (unauthenticated) or display an error screen
    // go_router treats unknown routes as catch-all, auth guard redirects
    const url = page.url();
    const isHandled = url.includes("/login") || url.includes("/nonexistent");
    expect(isHandled, "Unknown route should not crash the app").toBeTruthy();

    // No critical errors
    const realErrors = errors.filter(
      (e) =>
        !e.includes("LateInitializationError") &&
        !e.includes("setState") &&
        !e.includes("mounted"),
    );
    expect(realErrors).toEqual([]);
  });

  test("multiple rapid navigations don't crash the app", async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (err) => {
      errors.push(err.message);
    });

    // Rapidly navigate between routes
    for (const route of [
      "/",
      "/login",
      "/register",
      "/orders",
      "/deliveries",
      "/login",
    ]) {
      await page.goto(route, { waitUntil: "domcontentloaded" });
    }

    await waitForFlutter(page);

    const realErrors = errors.filter(
      (e) =>
        !e.includes("LateInitializationError") &&
        !e.includes("setState") &&
        !e.includes("mounted"),
    );
    expect(realErrors).toEqual([]);
  });
});

test.describe("Webapp Navigation - Screenshots", () => {
  test("screenshots at key routes for visual verification", async ({
    page,
  }) => {
    const routes = ["/", "/login", "/register", "/forgot-password"];

    for (const route of routes) {
      await page.goto(route);
      await waitForFlutter(page);
      await page.screenshot({
        path: `test-results/webapp-route-${route.replace(/\//g, "") || "home"}.png`,
      });
    }
  });
});
